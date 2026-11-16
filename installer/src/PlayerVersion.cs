// The port version of a player build or an installed copy: PortVersion and BuildNumber from the version.json compiled into
// resources.assets, with a build-label.txt label applied (patch 103). Shared by the Release Manager and the launcher.
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ReleaseGate
{
    public static class PlayerVersion
    {
        /// <summary>Reads the port version and BuildNumber from version.json, which the build compiles into resources.assets. The port
        /// version is PortVersion (patch 107, D1: ProductVersion went back to retail's 1.9.1); a player built before 107 has only
        /// ProductVersion, which then held the port version.</summary>
        public static string DetectVersion(string player)
        {
            string path = Path.Combine(player, "BattleTech_Data", "resources.assets");
            if (!File.Exists(path)) return null;
            byte[] needle = Encoding.ASCII.GetBytes("\"BuildNumber\"");
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan))
            {
                var buf = new byte[4 << 20];
                int keep = 0;
                long baseAt = 0;
                while (true)
                {
                    int n = f.Read(buf, keep, buf.Length - keep);
                    if (n <= 0) return null;
                    int len = keep + n;
                    int at = Find(buf, len, needle);
                    if (at >= 0)
                    {
                        f.Position = Math.Max(0, baseAt + at - 1024);
                        var win = new byte[2048];
                        int got = f.Read(win, 0, win.Length);
                        string text = Encoding.ASCII.GetString(win, 0, got);
                        Match pv = Regex.Match(text, "\"PortVersion\"\\s*:\\s*\"([^\"]+)\""), bn = Regex.Match(text, "\"BuildNumber\"\\s*:\\s*\"([^\"]+)\"");
                        if (!pv.Success) pv = Regex.Match(text, "\"ProductVersion\"\\s*:\\s*\"([^\"]+)\"");
                        if (!bn.Success) return null;
                        return (pv.Success ? pv.Groups[1].Value + "-" : "") + bn.Groups[1].Value;
                    }
                    keep = Math.Min(needle.Length - 1, len);
                    Buffer.BlockCopy(buf, len - keep, buf, 0, keep);
                    baseAt += len - keep;
                }
            }
        }

        static int Find(byte[] b, int len, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= len; i++)
            {
                if (b[i] != needle[0]) continue;
                int k = 1;
                while (k < needle.Length && b[i + k] == needle[k]) k++;
                if (k == needle.Length) return i;
            }
            return -1;
        }

        /// <summary>
        /// Patch 103: a player may take its build label from BattleTech_Data\build-label.txt (tools/set-player-label.ps1 writes
        /// it) instead of the BuildNumber compiled into resources.assets. Same rule as the player's VersionInfo: the file's text,
        /// trimmed, must be &lt;number&gt;&lt;letter&gt; (e.g. 11A). A player without patch 103 ignores the file, and so does this.
        /// Returns the version with its label replaced, or unchanged.
        /// </summary>
        public static string WithFileLabel(string player, string version)
        {
            string label = FileLabel(player);
            if (label == null) return version;
            if (version == null) return label;
            int dash = version.LastIndexOf('-');
            return dash < 0 ? label : version.Substring(0, dash + 1) + label;
        }

        static string FileLabel(string player)
        {
            string file = Path.Combine(player, "BattleTech_Data", "build-label.txt");
            string dll = Path.Combine(player, "BattleTech_Data", "Managed", "Assembly-CSharp.dll");
            try
            {
                if (!File.Exists(file) || !File.Exists(dll)) return null;
                string label = File.ReadAllText(file).Trim();
                if (!Regex.IsMatch(label, "^[0-9]{1,4}[A-Z]$")) return null;
                byte[] b = File.ReadAllBytes(dll), w = Encoding.Unicode.GetBytes("build-label.txt");
                return Find(b, b.Length, w) >= 0 ? label : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
