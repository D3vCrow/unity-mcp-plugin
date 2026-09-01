using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Persistent settings for the MCP Bridge Server, stored via EditorPrefs.
    ///
    /// EditorPrefs is global to the machine, so keys are namespaced into two tiers:
    ///   - Instance-scoped (<see cref="InstancePrefix"/>) — unique per Unity instance.
    ///     The main Editor, each ParrelSync clone and each MPPM virtual player have
    ///     distinct project paths, so each keeps its own value. Used for Port, etc.
    ///   - Project-scoped (<see cref="ProjectPrefix"/>) — shared across a project and
    ///     its clones / virtual players, which symlink ProjectSettings/ and therefore
    ///     share PlayerSettings.productGUID. Used for policy-like settings.
    /// </summary>
    public static class MCPSettingsManager
    {
        // Legacy pre-2-tier global prefix — kept only for one-shot migration.
        private const string LegacyPrefix = "UnityMCP_";

        private static string _instancePrefix;
        private static string _projectPrefix;

        /// <summary>Key prefix for instance-scoped settings (unique per Unity instance).</summary>
        private static string InstancePrefix
        {
            get
            {
                if (_instancePrefix == null)
                {
                    string dataPath = Application.dataPath;
                    string projectPath = dataPath.EndsWith("/Assets")
                        ? dataPath.Substring(0, dataPath.Length - "/Assets".Length)
                        : dataPath;
                    _instancePrefix = $"UnityMCP_inst_{projectPath.GetHashCode():X8}_";
                }
                return _instancePrefix;
            }
        }

        /// <summary>Key prefix for project-scoped settings (shared by clones / virtual players).</summary>
        private static string ProjectPrefix
        {
            get
            {
                if (_projectPrefix == null)
                {
                    string guid = PlayerSettings.productGUID.ToString("N");
                    if (string.IsNullOrEmpty(guid) || guid == "00000000000000000000000000000000")
                        guid = $"path{Application.dataPath.GetHashCode():X8}"; // fallback
                    _projectPrefix = $"UnityMCP_proj_{guid}_";
                }
                return _projectPrefix;
            }
        }

        static MCPSettingsManager()
        {
            MigrateLegacyKeys();
        }

        /// <summary>
        /// One-shot migration: copy any pre-2-tier global "UnityMCP_*" value into its
        /// new tiered key so existing users keep their settings. Best-effort.
        /// </summary>
        private static void MigrateLegacyKeys()
        {
            try
            {
                MigrateInt("Port", InstancePrefix);
                MigrateBool("UseManualPort", InstancePrefix);
                MigrateBool("AutoStart", InstancePrefix);
                MigrateBool("StartOnVirtualPlayers", ProjectPrefix);
                MigrateBool("ContextEnabled", ProjectPrefix);
                MigrateString("ContextPath", ProjectPrefix);
                MigrateBool("ActionHistoryPersistence", ProjectPrefix);
                MigrateInt("ActionHistoryMaxEntries", ProjectPrefix);
                MigrateString("EnabledCategories", ProjectPrefix);
            }
            catch { /* migration is best-effort — never block startup on it */ }
        }

        private static void MigrateBool(string name, string prefix)
        {
            string newKey = prefix + name, oldKey = LegacyPrefix + name;
            if (!EditorPrefs.HasKey(newKey) && EditorPrefs.HasKey(oldKey))
                EditorPrefs.SetBool(newKey, EditorPrefs.GetBool(oldKey));
        }

        private static void MigrateInt(string name, string prefix)
        {
            string newKey = prefix + name, oldKey = LegacyPrefix + name;
            if (!EditorPrefs.HasKey(newKey) && EditorPrefs.HasKey(oldKey))
                EditorPrefs.SetInt(newKey, EditorPrefs.GetInt(oldKey));
        }

        private static void MigrateString(string name, string prefix)
        {
            string newKey = prefix + name, oldKey = LegacyPrefix + name;
            if (!EditorPrefs.HasKey(newKey) && EditorPrefs.HasKey(oldKey))
                EditorPrefs.SetString(newKey, EditorPrefs.GetString(oldKey));
        }

        // ─── Categories ───
        private static readonly string[] AllCategories = new[]
        {
            "amplify", "animation", "asmdef", "asset", "audio", "build", "component", "console",
            "constraint", "debugger", "editor", "gameobject", "graphics", "input", "lighting",
            "memoryprofiler", "mppm", "navigation", "packagemanager", "particle", "physics", "prefab",
            "prefabasset", "prefs", "profiler", "project", "projectsettings", "renderer",
            "scenario", "scene", "screenshot", "script", "scriptableobject", "search",
            "selection", "shadergraph", "spriteatlas", "taglayer", "terrain", "testing",
            "texture", "ui", "uma", "undo"
        };

        private static Dictionary<string, bool> _enabledCategories;

        // ─── Port (instance-scoped) ───

        public static int Port
        {
            get => EditorPrefs.GetInt(InstancePrefix + "Port", 7700);
            set => EditorPrefs.SetInt(InstancePrefix + "Port", value);
        }

        /// <summary>
        /// When true, uses the manually configured Port value instead of auto-selecting.
        /// Default is false (auto-select from port range 7700-7709). Instance-scoped:
        /// each clone / virtual player picks its own port.
        /// </summary>
        public static bool UseManualPort
        {
            get => EditorPrefs.GetBool(InstancePrefix + "UseManualPort", false);
            set => EditorPrefs.SetBool(InstancePrefix + "UseManualPort", value);
        }

        // ─── Auto-Start (instance-scoped) ───

        public static bool AutoStart
        {
            get => EditorPrefs.GetBool(InstancePrefix + "AutoStart", true);
            set => EditorPrefs.SetBool(InstancePrefix + "AutoStart", value);
        }

        // ─── Multiplayer Play Mode (project-scoped) ───

        /// <summary>
        /// When false, the MCP bridge does not auto-start on MPPM Virtual Players —
        /// only on the main Editor. Manual start still works. Default true
        /// (preserves the historical behaviour where every Editor starts a bridge).
        /// Project-scoped so the virtual players inherit the project's policy.
        /// </summary>
        public static bool StartOnVirtualPlayers
        {
            get => EditorPrefs.GetBool(ProjectPrefix + "StartOnVirtualPlayers", true);
            set => EditorPrefs.SetBool(ProjectPrefix + "StartOnVirtualPlayers", value);
        }

        // ─── Security: Origin check + shared-secret token ───
        //
        // Origin allowlist is always enforced (cheap defense against browser drive-by).
        // Shared-secret token on mutating verbs is opt-in via EnforceToken so upstream
        // users don't regress when they pick up this release; operators who want the full
        // hardened mode flip EnforceToken=true and configure their companion to send
        // X-MCP-Token. The companion reads the secret from
        // %APPDATA%/unity-mcp/secret (Windows) / $XDG_CONFIG_HOME/unity-mcp/secret (Unix).
        //
        // Security keys are MACHINE-GLOBAL — deliberately NOT instance/project-scoped like
        // the two-tier settings above. The secret sidecar (SecretSidecarPath) is a single
        // machine-wide file with no project/instance component, so the stored secret and
        // enforcement flag must be machine-wide too; scoping them per-instance/project would
        // desync EditorPrefs from the one sidecar every companion reads. Value matches the
        // legacy prefix, so keys stored before the two-tier refactor survive untouched.
        private const string SecurityPrefix = "UnityMCP_";

        // Thread-safe snapshot of security settings. The bridge's HTTP handler runs on a
        // ThreadPool thread and cannot touch EditorPrefs (main-thread-only), so we mirror
        // the values into volatile fields. RefreshSecuritySnapshot() repopulates them and
        // MUST run on the main thread (called from Start() and each security setter).
        private static volatile bool _enforceTokenSnapshot;
        private static volatile string _sharedSecretSnapshot = "";

        public static bool EnforceToken
        {
            get => EditorPrefs.GetBool(SecurityPrefix + "EnforceToken", false);
            set
            {
                EditorPrefs.SetBool(SecurityPrefix + "EnforceToken", value);
                RefreshSecuritySnapshot();
            }
        }

        /// <summary>
        /// Worker-thread-safe view of EnforceToken. Backed by the snapshot refreshed on
        /// the main thread — the HTTP handler calls this instead of the EditorPrefs getter.
        /// </summary>
        public static bool EnforceTokenThreadsafe => _enforceTokenSnapshot;

        /// <summary>
        /// Shared secret for X-MCP-Token. Lazily generated on first access; persisted to
        /// EditorPrefs AND to a sidecar file on disk so companion processes can read it.
        /// </summary>
        public static string SharedSecret
        {
            get
            {
                string v = EditorPrefs.GetString(SecurityPrefix + "SharedSecret", "");
                if (string.IsNullOrEmpty(v))
                {
                    v = GenerateSecret();
                    EditorPrefs.SetString(SecurityPrefix + "SharedSecret", v);
                    TryWriteSecretSidecar(v);
                }
                return v;
            }
            set
            {
                EditorPrefs.SetString(SecurityPrefix + "SharedSecret", value);
                TryWriteSecretSidecar(value);
                RefreshSecuritySnapshot();
            }
        }

        /// <summary>Worker-thread-safe view of SharedSecret. Empty until RefreshSecuritySnapshot() runs.</summary>
        public static string SharedSecretThreadsafe => _sharedSecretSnapshot;

        /// <summary>
        /// Re-read EnforceToken and SharedSecret from EditorPrefs into the volatile
        /// snapshot. Reading SharedSecret triggers lazy generation if no secret exists.
        /// MUST be called from the Unity main thread (EditorPrefs is not thread-safe).
        /// </summary>
        public static void RefreshSecuritySnapshot()
        {
            _enforceTokenSnapshot = EditorPrefs.GetBool(SecurityPrefix + "EnforceToken", false);
            _sharedSecretSnapshot = SharedSecret; // triggers lazy generation if absent
        }

        public static string SecretSidecarPath
        {
            get
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(baseDir, "unity-mcp", "secret");
            }
        }

        private static string GenerateSecret()
        {
            byte[] buf = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(buf);
            }
            return Convert.ToBase64String(buf);
        }

        private static void TryWriteSecretSidecar(string secret)
        {
            try
            {
                string path = SecretSidecarPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, secret);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning(
                    $"[AB-UMCP] Failed to write shared-secret sidecar at {SecretSidecarPath}: {ex.Message}. " +
                    "Companion processes will not be able to auto-discover the token.");
            }
        }

        // ─── Project Context (project-scoped) ───

        public static bool ContextEnabled
        {
            get => EditorPrefs.GetBool(ProjectPrefix + "ContextEnabled", true);
            set => EditorPrefs.SetBool(ProjectPrefix + "ContextEnabled", value);
        }

        public static string ContextPath
        {
            get => EditorPrefs.GetString(ProjectPrefix + "ContextPath", "Assets/MCP/Context");
            set => EditorPrefs.SetString(ProjectPrefix + "ContextPath", value);
        }

        // ─── Action History (project-scoped) ───

        public static bool ActionHistoryPersistence
        {
            get => EditorPrefs.GetBool(ProjectPrefix + "ActionHistoryPersistence", false);
            set => EditorPrefs.SetBool(ProjectPrefix + "ActionHistoryPersistence", value);
        }

        public static int ActionHistoryMaxEntries
        {
            get => EditorPrefs.GetInt(ProjectPrefix + "ActionHistoryMaxEntries", 500);
            set => EditorPrefs.SetInt(ProjectPrefix + "ActionHistoryMaxEntries", value);
        }

        // ─── Category Management (project-scoped) ───

        public static string[] GetAllCategoryNames() => AllCategories;

        public static Dictionary<string, bool> GetEnabledCategories()
        {
            if (_enabledCategories != null) return _enabledCategories;

            _enabledCategories = new Dictionary<string, bool>();
            foreach (var cat in AllCategories)
                _enabledCategories[cat] = true;

            string saved = EditorPrefs.GetString(ProjectPrefix + "EnabledCategories", "");
            if (!string.IsNullOrEmpty(saved))
            {
                var parts = saved.Split(',');
                foreach (var part in parts)
                {
                    var kv = part.Split(':');
                    if (kv.Length == 2 && _enabledCategories.ContainsKey(kv[0]))
                    {
                        bool.TryParse(kv[1], out bool enabled);
                        _enabledCategories[kv[0]] = enabled;
                    }
                }
            }

            return _enabledCategories;
        }

        public static bool IsCategoryEnabled(string category)
        {
            var cats = GetEnabledCategories();
            string lower = category.ToLower();
            return !cats.ContainsKey(lower) || cats[lower];
        }

        public static void SetCategoryEnabled(string category, bool enabled)
        {
            var cats = GetEnabledCategories();
            string lower = category.ToLower();
            if (cats.ContainsKey(lower))
            {
                cats[lower] = enabled;
                SaveEnabledCategories();
            }
        }

        private static void SaveEnabledCategories()
        {
            var parts = new List<string>();
            foreach (var kv in _enabledCategories)
                parts.Add($"{kv.Key}:{kv.Value}");
            EditorPrefs.SetString(ProjectPrefix + "EnabledCategories", string.Join(",", parts));
        }

        /// <summary>
        /// Reset all settings to defaults.
        /// </summary>
        public static void ResetToDefaults()
        {
            Port = 7700;
            AutoStart = true;
            StartOnVirtualPlayers = true;
            EnforceToken = false;
            ContextEnabled = true;
            ContextPath = "Assets/MCP/Context";
            ActionHistoryPersistence = false;
            ActionHistoryMaxEntries = 500;
            _enabledCategories = null;
            EditorPrefs.DeleteKey(ProjectPrefix + "EnabledCategories");
        }
    }
}
