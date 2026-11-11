using System;
using System.Collections.Generic;
using UnityEditor;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Builds "property not found" errors that list the nearest valid property paths,
    /// so an agent can fix a wrong name in one retry instead of guessing.
    /// Pattern from IvanMurzak's Unity-AI-Timeline / Terrain / Navigation add-ons
    /// (DevCrow vet 2026-11-11): errors that name the valid choices.
    /// </summary>
    public static class MCPPropertyHints
    {
        const int MaxHints = 5;

        /// <summary>Up to 5 property paths on the object closest to the requested name.</summary>
        public static string[] Nearest(SerializedObject serialized, string wanted)
        {
            var result = new List<string>();
            if (serialized == null) return result.ToArray();

            string want = Normalize(wanted);
            var scored = new List<KeyValuePair<int, string>>();
            var seen = new HashSet<string>();

            var it = serialized.GetIterator();
            bool enterChildren = true;
            while (it.Next(enterChildren))
            {
                // Arrays and long strings expand into thousands of element paths; list the array, skip its elements.
                enterChildren = !(it.isArray || it.propertyType == SerializedPropertyType.String);
                string path = it.propertyPath;
                if (path.Contains(".Array.data[") || !seen.Add(path)) continue;
                int score = Score(want, Normalize(path), Normalize(it.name), Normalize(it.displayName));
                if (score >= 0) scored.Add(new KeyValuePair<int, string>(score, path));
            }

            scored.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Value.Length.CompareTo(b.Value.Length));
            for (int i = 0; i < scored.Count && result.Count < MaxHints; i++) result.Add(scored[i].Value);
            return result.ToArray();
        }

        /// <summary>The error text plus a "Did you mean" tail when there are hints.</summary>
        public static string NotFoundMessage(string baseMessage, string[] hints)
        {
            if (hints == null || hints.Length == 0)
                return baseMessage + ". Call component/get-properties to list the valid names.";
            return baseMessage + ". Did you mean: " + string.Join(", ", hints) + "?";
        }

        // Lower is better; -1 = not similar enough to suggest.
        static int Score(string want, string path, string name, string display)
        {
            if (want.Length == 0) return -1;
            if (name == want || display == want || path == want) return 0;
            if (path.EndsWith(want, StringComparison.Ordinal)) return 1;
            if (name.Contains(want) || (name.Length >= 3 && want.Contains(name))) return 2;
            if (path.Contains(want)) return 3;
            int d = Math.Min(Distance(want, name), Distance(want, display));
            int limit = Math.Max(2, want.Length / 3);
            return d <= limit ? 4 + d : -1;
        }

        // Lowercase, drop the m_ prefix, spaces and underscores: "m_StartColor", "Start Color", "startColor" all match.
        static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var parts = s.Split('.');
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].StartsWith("m_", StringComparison.Ordinal)) parts[i] = parts[i].Substring(2);
            return string.Join(".", parts).Replace(" ", "").Replace("_", "").ToLowerInvariant();
        }

        static int Distance(string a, string b)
        {
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }
    }
}
