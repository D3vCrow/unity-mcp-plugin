using System;
using System.IO;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Confines caller-supplied file paths to the Unity project folder (the parent of Assets).
    /// Relative paths resolve against the project folder, not the process working directory.
    /// </summary>
    internal static class MCPPathGuard
    {
        public static string ProjectRoot =>
            TrimSeparators(Path.GetFullPath(Directory.GetParent(Application.dataPath).FullName));

        public static bool TryResolveRead(string path, out string fullPath, out string error)
            => TryResolve(ProjectRoot, path, true, out fullPath, out error);

        /// <summary>Refuses an existing file unless <paramref name="overwrite"/> is true.</summary>
        public static bool TryResolveWrite(string path, bool overwrite, out string fullPath, out string error)
            => TryResolve(ProjectRoot, path, overwrite, out fullPath, out error);

        internal static bool TryResolve(string root, string path, bool allowExisting, out string fullPath, out string error)
        {
            fullPath = null;
            error = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "path is empty";
                return false;
            }

            string candidate;
            try
            {
                candidate = TrimSeparators(Path.GetFullPath(Path.Combine(root, path)));
            }
            catch (Exception ex)
            {
                error = $"Invalid path '{path}': {ex.Message}";
                return false;
            }

            // Whole-segment prefix, so a sibling such as '<root>_evil' does not pass.
            var cmp = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, cmp))
            {
                error = $"Path '{path}' resolves to '{candidate}', not inside the Unity project folder. Only paths inside '{root}' are allowed.";
                return false;
            }

            if (!allowExisting && File.Exists(candidate))
            {
                error = $"File '{path}' already exists. Pass overwrite: true to replace it.";
                return false;
            }

            fullPath = candidate;
            return true;
        }

        static string TrimSeparators(string path)
        {
            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return trimmed.Length == 0 ? path : trimmed;
        }
    }
}
