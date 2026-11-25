using System;
using System.Collections.Generic;
using System.Text;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Safe mode for the execute-code route: a namespace allowlist checked on the
    /// COMPILED assembly, before it is loaded or run.
    ///
    /// The check reads the metadata tables of the emitted DLL (TypeRef, MemberRef,
    /// ModuleRef). Every type the code touches — by any spelling, alias, global::,
    /// var, generic argument or method signature — has a TypeRef row there, so
    /// fully-qualified names cannot slip past it the way a text scan of "using"
    /// lines could. A ModuleRef row means a native import (P/Invoke) and is refused.
    ///
    /// Idea from Blender MCP's opt-in safe mode (DevCrow vet 2026-11-25), but
    /// enforced here, in the editor that runs the code, and ON by default.
    ///
    /// This is a speed bump, NOT a sandbox. It blocks the one-call escapes (start a
    /// process, read or write any file, open a socket, reflection, native code,
    /// environment variables, the safe-mode switch itself). Editor APIs still reach
    /// a lot, and the script/create route can still write a .cs file that runs with
    /// full trust after a recompile. The X-MCP-Token check is the real gate.
    /// </summary>
    internal static class MCPCodeSafety
    {
        // Namespaces allowed as written (sub-namespaces NOT included).
        private static readonly HashSet<string> AllowedNamespaces = new HashSet<string>(StringComparer.Ordinal)
        {
            "",                                  // global namespace: the project's own scripts
            "System",
            "System.Collections",
            "System.Collections.Generic",
            "System.Collections.ObjectModel",
            "System.Collections.Specialized",
            "System.Collections.Concurrent",
            "System.Linq",                       // NOT System.Linq.Expressions (calls methods by name)
            "System.Text",
            "System.Text.RegularExpressions",
            "System.Globalization",
            "System.Threading",                  // lock statement needs Monitor
            "System.Threading.Tasks",
            "System.Runtime.CompilerServices",   // the compiler emits these attributes
            "Unity.Collections",
            "Unity.Jobs",
            "TMPro",
        };

        // Namespaces allowed together with every sub-namespace.
        private static readonly string[] AllowedRoots =
        {
            "UnityEngine",
            "UnityEditor",
            "Unity.Mathematics",
        };

        // Sub-namespaces of an allowed root that stay blocked.
        private static readonly string[] DeniedRoots =
        {
            "UnityEngine.Networking",            // UnityWebRequest: send data anywhere
            "UnityEngine.Windows",               // UnityEngine.Windows.File / Directory
            "UnityEngine.WSA",                   // Launcher.LaunchUri
            "UnityEditor.Networking",
            "UnityEditor.PackageManager",        // install a package = run its code
            "UnityEditor.MPE",                   // starts helper processes
        };

        // Single types allowed outside the allowlist (the compiler emits them).
        private static readonly HashSet<string> AlwaysAllowedTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.Diagnostics.DebuggableAttribute",
            "System.Diagnostics.DebuggableAttribute/DebuggingModes",
            "System.Diagnostics.DebuggerHiddenAttribute",
            "System.Diagnostics.DebuggerStepThroughAttribute",
            "System.Diagnostics.DebuggerNonUserCodeAttribute",
            "System.Diagnostics.DebuggerBrowsableAttribute",
            "System.Diagnostics.DebuggerBrowsableState",
            "System.Diagnostics.DebuggerDisplayAttribute",
            "System.Diagnostics.Stopwatch",
            "System.Runtime.InteropServices.InAttribute",
            // typeof(X).Name and obj.GetType().Name compile to MemberInfo::get_Name.
            // A MemberInfo alone cannot invoke or write anything; MethodInfo,
            // FieldInfo, PropertyInfo and ConstructorInfo stay blocked.
            "System.Reflection.MemberInfo",
        };

        // Whole types blocked even inside an allowed namespace.
        private static readonly HashSet<string> DeniedTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.Activator",                  // create any type by Type object
            "System.AppDomain",                  // ExecuteAssembly, CreateInstance...
            "System.Runtime.CompilerServices.Unsafe",
            "UnityEditor.EditorPrefs",           // would flip safe mode off, read the token secret
            "UnityEditor.FileUtil",              // copy / delete / replace any path
            "UnityEngine.WWW",
        };

        // Single members blocked on an otherwise allowed type ("Type::Member").
        private static readonly HashSet<string> DeniedMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.Type::GetType",              // static GetType(string): any type by name
            "System.Type::GetTypeFromProgID",    // COM
            "System.Type::GetTypeFromCLSID",
            "System.Type::ReflectionOnlyGetType",
            "System.Delegate::CreateDelegate",   // bind any method by name
            "System.Delegate::DynamicInvoke",
            "System.Environment::GetEnvironmentVariable",
            "System.Environment::GetEnvironmentVariables",
            "System.Environment::SetEnvironmentVariable",
            "System.Environment::ExpandEnvironmentVariables",
            "System.Environment::Exit",
            "System.Environment::FailFast",
            "System.Runtime.CompilerServices.RuntimeHelpers::RunClassConstructor",
            "System.Runtime.CompilerServices.RuntimeHelpers::RunModuleConstructor",
            "System.Runtime.CompilerServices.RuntimeHelpers::GetUninitializedObject",
            "UnityEngine.Application::OpenURL",
            "UnityEditor.EditorUtility::OpenWithDefaultApp",
            "UnityEditor.AssetDatabase::ImportPackage",
            "UnityEditor.EditorApplication::OpenProject",
        };

        /// <summary>
        /// Returns the sorted list of blocked references in a compiled assembly, or an
        /// empty list when every reference passes. Any parse problem fails CLOSED: it
        /// returns one entry naming the problem.
        /// </summary>
        /// <param name="extraRoots">Namespaces the owner allowed in the dashboard
        /// (each with its sub-namespaces). They beat DeniedRoots, never DeniedTypes
        /// or DeniedMembers.</param>
        internal static List<string> FindBlocked(byte[] pe, IEnumerable<string> extraRoots)
        {
            var extras = new List<string>();
            if (extraRoots != null)
                foreach (var e in extraRoots)
                {
                    string t = (e ?? "").Trim();
                    if (t.Length > 0) extras.Add(t);
                }

            var blocked = new SortedSet<string>(StringComparer.Ordinal);
            MetadataView md;
            try
            {
                md = MetadataView.Read(pe);
            }
            catch (Exception ex)
            {
                return new List<string> { "could not read the compiled assembly (" + ex.Message + ")" };
            }

            if (md.ModuleRefCount > 0)
                blocked.Add("native code import (DllImport / extern)");

            for (int i = 0; i < md.TypeRefs.Count; i++)
            {
                var tr = md.TypeRefs[i];
                if (!IsTypeAllowed(tr.Namespace, tr.FullName, extras))
                    blocked.Add(tr.FullName);
            }

            foreach (var mr in md.MemberRefs)
                if (DeniedMembers.Contains(mr))
                    blocked.Add(mr);

            return new List<string>(blocked);
        }

        internal static bool IsTypeAllowed(string ns, string fullName, List<string> extras)
        {
            // Outer type decides for nested types: A/B is denied when A is.
            int slash = fullName.IndexOf('/');
            string outer = slash < 0 ? fullName : fullName.Substring(0, slash);
            if (DeniedTypes.Contains(outer)) return false;
            if (AlwaysAllowedTypes.Contains(fullName) || AlwaysAllowedTypes.Contains(outer)) return true;

            foreach (var e in extras)
                if (InRoot(ns, e)) return true;

            foreach (var d in DeniedRoots)
                if (InRoot(ns, d)) return false;

            if (AllowedNamespaces.Contains(ns)) return true;
            foreach (var r in AllowedRoots)
                if (InRoot(ns, r)) return true;
            return false;
        }

        private static bool InRoot(string ns, string root)
        {
            return ns == root || (ns.Length > root.Length && ns.StartsWith(root, StringComparison.Ordinal) && ns[root.Length] == '.');
        }

        /// <summary>Plain-text summary of the policy for error messages.</summary>
        internal static string PolicySummary()
        {
            return "Safe mode allows System (core types only), System.Collections.*, System.Linq, System.Text, " +
                   "System.Text.RegularExpressions, System.Globalization, System.Threading, UnityEngine.*, UnityEditor.*, " +
                   "Unity.Mathematics, Unity.Collections, Unity.Jobs, TMPro and the project's global-namespace scripts. " +
                   "It blocks files, processes, network, reflection, native code, environment variables, EditorPrefs and FileUtil.";
        }

        // ─── Minimal ECMA-335 metadata reader ───
        // Reads only what the check needs: TypeRef (0x01), MemberRef (0x0A) and the
        // ModuleRef (0x1A) row count. Row sizes of tables 0x00-0x09 are computed so
        // the MemberRef table can be located.
        private sealed class MetadataView
        {
            internal struct TypeRefRow { internal string Namespace; internal string FullName; }

            internal readonly List<TypeRefRow> TypeRefs = new List<TypeRefRow>();
            internal readonly List<string> MemberRefs = new List<string>();   // "Ns.Type::Name" for TypeRef parents
            internal int ModuleRefCount;

            private byte[] _b;
            private int _strings;
            private int _stringsSize;

            internal static MetadataView Read(byte[] pe)
            {
                var v = new MetadataView { _b = pe };
                v.Parse();
                return v;
            }

            private ushort U16(int o) { Check(o, 2); return (ushort)(_b[o] | (_b[o + 1] << 8)); }
            private uint U32(int o) { Check(o, 4); return (uint)(_b[o] | (_b[o + 1] << 8) | (_b[o + 2] << 16) | (_b[o + 3] << 24)); }
            private void Check(int o, int n) { if (o < 0 || n < 0 || o > _b.Length - n) throw new FormatException("offset out of range"); }

            private void Parse()
            {
                int peOff = (int)U32(0x3C);
                if (U32(peOff) != 0x00004550) throw new FormatException("no PE signature");
                int coff = peOff + 4;
                int nSections = U16(coff + 2);
                int optSize = U16(coff + 16);
                int opt = coff + 20;
                ushort magic = U16(opt);
                int dataDirs = magic == 0x20b ? opt + 112 : opt + 96;
                int cliRva = (int)U32(dataDirs + 14 * 8);
                int sections = opt + optSize;

                Func<int, int> rvaToOff = rva =>
                {
                    for (int s = 0; s < nSections; s++)
                    {
                        int sh = sections + s * 40;
                        int vsize = (int)U32(sh + 8), va = (int)U32(sh + 12), rawSize = (int)U32(sh + 16), raw = (int)U32(sh + 20);
                        int size = Math.Max(vsize, rawSize);
                        if (rva >= va && rva < va + size) return rva - va + raw;
                    }
                    throw new FormatException("RVA outside every section");
                };

                int cli = rvaToOff(cliRva);
                int mdRoot = rvaToOff((int)U32(cli + 8));
                if (U32(mdRoot) != 0x424A5342) throw new FormatException("no metadata signature");
                int verLen = (int)U32(mdRoot + 12);
                int p = mdRoot + 16 + verLen;
                int nStreams = U16(p + 2);
                p += 4;
                int tables = -1;
                for (int i = 0; i < nStreams; i++)
                {
                    int off = (int)U32(p), size = (int)U32(p + 4);
                    p += 8;
                    var name = new StringBuilder();
                    while (true) { Check(p, 1); byte c = _b[p++]; if (c == 0) break; name.Append((char)c); }
                    p = (p + 3) & ~3;
                    string n = name.ToString();
                    if (n == "#~") tables = mdRoot + off;
                    else if (n == "#-") throw new FormatException("uncompressed metadata (#-) is not supported");
                    else if (n == "#Strings") { _strings = mdRoot + off; _stringsSize = size; }
                }
                if (tables < 0 || _stringsSize == 0) throw new FormatException("missing #~ or #Strings stream");

                byte heapSizes = _b[tables + 6];
                Check(tables + 8, 16);
                ulong valid = (ulong)U32(tables + 8) | ((ulong)U32(tables + 12) << 32);
                var rows = new int[64];
                int q = tables + 24;
                for (int t = 0; t < 64; t++)
                    if ((valid & (1UL << t)) != 0) { rows[t] = (int)U32(q); q += 4; }

                int str = (heapSizes & 1) != 0 ? 4 : 2;
                int guid = (heapSizes & 2) != 0 ? 4 : 2;
                int blob = (heapSizes & 4) != 0 ? 4 : 2;
                Func<int, int> idx = t => rows[t] < 65536 ? 2 : 4;
                Func<int, int[], int> coded = (bits, ts) =>
                {
                    int max = 0;
                    foreach (var t in ts) max = Math.Max(max, rows[t]);
                    return max < (1 << (16 - bits)) ? 2 : 4;
                };
                int resolutionScope = coded(2, new[] { 0x00, 0x1A, 0x23, 0x01 });
                int typeDefOrRef = coded(2, new[] { 0x02, 0x01, 0x1B });
                int memberRefParent = coded(3, new[] { 0x02, 0x01, 0x1A, 0x06, 0x1B });

                var rowSize = new int[0x0B];
                rowSize[0x00] = 2 + str + guid * 3;                          // Module
                rowSize[0x01] = resolutionScope + str + str;                 // TypeRef
                rowSize[0x02] = 4 + str + str + typeDefOrRef + idx(0x04) + idx(0x06); // TypeDef
                rowSize[0x03] = idx(0x04);                                   // FieldPtr
                rowSize[0x04] = 2 + str + blob;                              // Field
                rowSize[0x05] = idx(0x06);                                   // MethodPtr
                rowSize[0x06] = 4 + 2 + 2 + str + blob + idx(0x08);          // MethodDef
                rowSize[0x07] = idx(0x08);                                   // ParamPtr
                rowSize[0x08] = 2 + 2 + str;                                 // Param
                rowSize[0x09] = idx(0x02) + typeDefOrRef;                    // InterfaceImpl
                rowSize[0x0A] = memberRefParent + str + blob;                // MemberRef

                var start = new int[0x0B];
                int cursor = q;
                for (int t = 0; t < 0x0B; t++)
                {
                    start[t] = cursor;
                    cursor += rows[t] * rowSize[t];
                }
                Check(start[0x0A], rows[0x0A] * rowSize[0x0A]);

                // TypeRef rows (1-based in tokens).
                int nTypeRefs = rows[0x01];
                var scope = new int[nTypeRefs + 1];
                var nameOf = new string[nTypeRefs + 1];
                var nsOf = new string[nTypeRefs + 1];
                for (int i = 1; i <= nTypeRefs; i++)
                {
                    int r = start[0x01] + (i - 1) * rowSize[0x01];
                    scope[i] = ReadIdx(r, resolutionScope);
                    nameOf[i] = ReadString(ReadIdx(r + resolutionScope, str));
                    nsOf[i] = ReadString(ReadIdx(r + resolutionScope + str, str));
                }
                var fullOf = new string[nTypeRefs + 1];
                for (int i = 1; i <= nTypeRefs; i++)
                {
                    // Walk ResolutionScope = TypeRef (tag 3) chains for nested types.
                    string full = nameOf[i];
                    string ns = nsOf[i];
                    int cur = i, depth = 0;
                    while ((scope[cur] & 3) == 3)
                    {
                        int parent = scope[cur] >> 2;
                        if (parent < 1 || parent > nTypeRefs || ++depth > 32) throw new FormatException("bad nested TypeRef");
                        full = nameOf[parent] + "/" + full;
                        ns = nsOf[parent];
                        cur = parent;
                    }
                    fullOf[i] = ns.Length > 0 ? ns + "." + full : full;
                    TypeRefs.Add(new TypeRefRow { Namespace = ns, FullName = fullOf[i] });
                }

                for (int i = 0; i < rows[0x0A]; i++)
                {
                    int r = start[0x0A] + i * rowSize[0x0A];
                    int parent = ReadIdx(r, memberRefParent);
                    if ((parent & 7) != 1) continue;                    // tag 1 = TypeRef
                    int tr = parent >> 3;
                    if (tr < 1 || tr > nTypeRefs) throw new FormatException("bad MemberRef parent");
                    MemberRefs.Add(fullOf[tr] + "::" + ReadString(ReadIdx(r + memberRefParent, str)));
                }

                ModuleRefCount = rows[0x1A];
            }

            private int ReadIdx(int o, int size) { return size == 2 ? U16(o) : (int)U32(o); }

            private string ReadString(int index)
            {
                if (index < 0 || index >= _stringsSize) throw new FormatException("string index out of range");
                int s = _strings + index, e = s;
                while (true) { Check(e, 1); if (_b[e] == 0) break; e++; }
                return Encoding.UTF8.GetString(_b, s, e - s);
            }
        }
    }
}
