using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    public static class MCPScriptCommands
    {
        public static object Create(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            string content = args.ContainsKey("content") ? args["content"].ToString() : "";

            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };
            if (string.IsNullOrEmpty(content))
                return new { error = "content is required" };

            bool overwrite = args.ContainsKey("overwrite") && Convert.ToBoolean(args["overwrite"]);
            if (!MCPPathGuard.TryResolveWrite(path, overwrite, out string fullPath, out string pathError))
                return new { error = pathError };
            string dir = Path.GetDirectoryName(fullPath);

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(fullPath, content);
            AssetDatabase.ImportAsset(path);

            return new { success = true, path, size = content.Length };
        }

        public static object Read(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            if (!MCPPathGuard.TryResolveRead(path, out string fullPath, out string pathError))
                return new { error = pathError };

            if (!File.Exists(fullPath))
                return new { error = $"File not found: {path}" };

            string content = File.ReadAllText(fullPath);
            return new Dictionary<string, object>
            {
                { "path", path },
                { "content", content },
                { "lines", content.Split('\n').Length },
                { "size", content.Length },
            };
        }

        public static object Update(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            string content = args.ContainsKey("content") ? args["content"].ToString() : "";

            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            if (!MCPPathGuard.TryResolveWrite(path, overwrite: true, out string fullPath, out string pathError))
                return new { error = pathError };

            if (!File.Exists(fullPath))
                return new { error = $"File not found: {path}" };

            File.WriteAllText(fullPath, content);
            AssetDatabase.ImportAsset(path);

            return new { success = true, path, size = content.Length };
        }
    }
}
