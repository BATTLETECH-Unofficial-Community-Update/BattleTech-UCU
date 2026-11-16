// The launcher side of BattleTechUCUSetup.exe: what the window's Home, Mods, Settings and Tools tabs read and do for an
// installed copy. Installing and updating stay in Installer.cs; linking a mod pack stays in the install's own
// ModSupport\Link-BTA.ps1 (tools/bta-link), which the launcher runs with the folders and mode the player chose.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace ReleaseGate
{
    /// <summary>The launcher's own settings, per Windows user: %APPDATA%\BattleTechUCU\launcher.ini, key=value lines.</summary>
    public sealed class LauncherSettings
    {
        public const string Default = "default", D3D11 = "d3d11", D3D12 = "d3d12";
        public string Install = "";             // the install the launcher last managed
        public bool CheckOnStart = true;        // look for a newer release when the window opens
        public bool CloseOnPlay = true;         // close the launcher once the game has started
        public string GraphicsApi = Default;    // default (the player's own choice, D3D11), d3d11 or d3d12
        public string ExtraArgs = "";           // passed to BattleTech.exe after the launcher's own
        public string PackKind = Launcher.PackBta;
        public string PackFolder = "";
        public bool PackCopy;                   // copy the pack instead of linking it with junctions
        public bool PackNoSaves;                // leave the pack's saves (BTASaves, ModSaves) out

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BattleTechUCU"); }
        }

        public static string FilePath { get { return Path.Combine(Folder, "launcher.ini"); } }

        public static LauncherSettings Load()
        {
            var s = new LauncherSettings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "Install": s.Install = v; break;
                        case "CheckOnStart": s.CheckOnStart = v != "0"; break;
                        case "CloseOnPlay": s.CloseOnPlay = v != "0"; break;
                        case "GraphicsApi": s.GraphicsApi = v == D3D11 || v == D3D12 ? v : Default; break;
                        case "ExtraArgs": s.ExtraArgs = v; break;
                        case "PackKind": s.PackKind = Launcher.PackKinds.Contains(v) ? v : Launcher.PackBta; break;
                        case "PackFolder": s.PackFolder = v; break;
                        case "PackCopy": s.PackCopy = v == "1"; break;
                        case "PackNoSaves": s.PackNoSaves = v == "1"; break;
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            return s;
        }

        /// <summary>Null when saved, otherwise why not.</summary>
        public string Save()
        {
            var b = new StringBuilder();
            b.Append("Install=").Append(Install).Append("\r\n");
            b.Append("CheckOnStart=").Append(CheckOnStart ? "1" : "0").Append("\r\n");
            b.Append("CloseOnPlay=").Append(CloseOnPlay ? "1" : "0").Append("\r\n");
            b.Append("GraphicsApi=").Append(GraphicsApi).Append("\r\n");
            b.Append("ExtraArgs=").Append((ExtraArgs ?? "").Replace("\r", " ").Replace("\n", " ")).Append("\r\n");
            b.Append("PackKind=").Append(PackKind).Append("\r\n");
            b.Append("PackFolder=").Append(PackFolder).Append("\r\n");
            b.Append("PackCopy=").Append(PackCopy ? "1" : "0").Append("\r\n");
            b.Append("PackNoSaves=").Append(PackNoSaves ? "1" : "0").Append("\r\n");
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, b.ToString(), new UTF8Encoding(false));
                return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return e.Message; }
        }
    }

    /// <summary>How the install's mod pack is linked: .bta-link.json, which Link-BTA.ps1 writes. Null fields when unknown.</summary>
    public sealed class PackLink
    {
        public string Source;
        public bool Junction;
        public bool Saves;
        public string LinkedAt;
    }

    public static class Launcher
    {
        /// <summary>The name the launcher keeps in an install folder: a copy of this exe, outside the install record.</summary>
        public const string FileName = "BattleTech UCU Launcher.exe";
        public const string LinkMarker = ".bta-link.json";
        public const string LinkScript = "Link-BTA.ps1";
        public const string PackBta = "bta", PackRogueTech = "roguetech", PackOther = "other";
        public static readonly string[] PackKinds = { PackBta, PackRogueTech, PackOther };
        public const string IssuesPage = "https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/issues/new/choose";
        public const string ReleasesPage = "https://github.com/BATTLETECH-Unofficial-Community-Update/BattleTech-UCU/releases";

        /// <summary>True for a folder this installer filled: BattleTech.exe and the install record.</summary>
        public static bool IsInstall(string dir)
        {
            try { return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "BattleTech.exe")) && File.Exists(Path.Combine(dir, Installer.RecordName)); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is IOException) { return false; }
        }

        /// <summary>The install this exe sits in (the launcher copy, or an installer saved into the folder), or null.</summary>
        public static string InstallBeside()
        {
            string dir = Installer.PackageFolder.TrimEnd('\\');
            return IsInstall(dir) ? dir : null;
        }

        /// <summary>The installed port version (for example 2.1.4-2B), or null when it can't be read. Reads resources.assets, so call it off the UI thread.</summary>
        public static string InstalledVersion(string dest)
        {
            try { return PlayerVersion.WithFileLabel(dest, PlayerVersion.DetectVersion(dest)); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        /// <summary>Release tags are v&lt;version&gt;; the game reports &lt;version&gt;.</summary>
        public static string Bare(string version)
        {
            string v = (version ?? "").Trim();
            return v.StartsWith("v", StringComparison.OrdinalIgnoreCase) && v.Length > 1 && char.IsDigit(v[1]) ? v.Substring(1) : v;
        }

        public static bool SameVersion(string a, string b)
        {
            return a != null && b != null && string.Equals(Bare(a), Bare(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Release pages are shown on the main repository, which carries the notes; the data repository's copy has only files.</summary>
        public static string NotesPage(ReleaseSummary r)
        {
            if (r == null) return ReleasesPage;
            return ReleasesPage + "/tag/" + Uri.EscapeDataString(r.Version);
        }

        // ---- the game ----

        /// <summary>BattleTech.exe processes started from this install.</summary>
        public static bool GameRunning(string dest)
        {
            string exe;
            try { exe = Path.GetFullPath(Path.Combine(dest, "BattleTech.exe")); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return false; }
            foreach (Process p in Process.GetProcessesByName("BattleTech"))
            {
                try
                {
                    if (string.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException || e is NotSupportedException) { }
                finally { p.Dispose(); }
            }
            return false;
        }

        /// <summary>The command line the launcher starts BattleTech.exe with.</summary>
        public static string GameArguments(LauncherSettings s)
        {
            var l = new List<string>();
            if (s.GraphicsApi == LauncherSettings.D3D11) l.Add("-force-d3d11");
            else if (s.GraphicsApi == LauncherSettings.D3D12) l.Add("-force-d3d12");
            if (!string.IsNullOrWhiteSpace(s.ExtraArgs)) l.Add(s.ExtraArgs.Trim());
            return string.Join(" ", l);
        }

        public static Process StartGame(string dest, LauncherSettings s)
        {
            return Process.Start(new ProcessStartInfo(Path.Combine(dest, "BattleTech.exe"), GameArguments(s)) { WorkingDirectory = dest, UseShellExecute = false });
        }

        // ---- mod packs ----

        /// <summary>A ModTek v4 pack: what Link-BTA.ps1 accepts (Is-Bta).</summary>
        public static bool IsModTekPack(string folder)
        {
            try { return !string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, "Mods", "ModTek", "lib", "ModTek.Preloader.dll")); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is IOException) { return false; }
        }

        public static string PackName(string kind)
        {
            return kind == PackRogueTech ? "RogueTech" : kind == PackOther ? "ModTek pack" : "BTA";
        }

        /// <summary>A guess at which pack a folder holds, from its name and its Mods folders; for display only.</summary>
        public static string GuessPack(string folder)
        {
            try
            {
                var names = new List<string> { Path.GetFileName((folder ?? "").TrimEnd('\\')) };
                string mods = Path.Combine(folder, "Mods");
                if (Directory.Exists(mods)) names.AddRange(Directory.GetDirectories(mods).Select(Path.GetFileName));
                if (names.Any(n => n.IndexOf("RogueTech", StringComparison.OrdinalIgnoreCase) >= 0)) return PackRogueTech;
                if (names.Any(n => n.IndexOf("BT Advanced", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("BTA", StringComparison.Ordinal) >= 0 || n.IndexOf("BattleTech Advanced", StringComparison.OrdinalIgnoreCase) >= 0)) return PackBta;
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is IOException || e is UnauthorizedAccessException) { }
            return null;
        }

        public static PackLink ReadLink(string dest)
        {
            try
            {
                string path = Path.Combine(dest, LinkMarker);
                if (!File.Exists(path)) return null;
                var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                var l = new PackLink();
                if (d == null) return l;
                object v;
                if (d.TryGetValue("source", out v)) l.Source = v as string;
                if (d.TryGetValue("linkedAt", out v)) l.LinkedAt = v as string;
                // Markers from before junction mode have no "junction" field: those were copy runs (Link-BTA.ps1).
                l.Junction = d.TryGetValue("junction", out v) && true.Equals(v);
                l.Saves = !d.TryGetValue("saves", out v) || !false.Equals(v);
                return l;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is InvalidOperationException) { return new PackLink(); }
        }

        public static string LinkScriptPath(string dest) { return Path.Combine(dest, "ModSupport", LinkScript); }

        /// <summary>Single-quoted for PowerShell: a quote inside is doubled.</summary>
        static string Ps(string s) { return "'" + s.Replace("'", "''") + "'"; }

        /// <summary>
        /// Runs the install's Link-BTA.ps1 in its own console window, which stays open on its result until Enter. An empty
        /// pack lets the script search for BTA. The script makes every check itself (game running, NTFS, free space, a Mods
        /// folder it didn't make).
        /// </summary>
        public static Process RunLink(string dest, string pack, bool copy, bool noSaves, bool unlink)
        {
            var cmd = new StringBuilder("& ").Append(Ps(LinkScriptPath(dest))).Append(" -Ucu ").Append(Ps(dest.TrimEnd('\\')));
            if (unlink) cmd.Append(" -Unlink");
            else
            {
                pack = pack ?? "";
                // An empty pack folder lets the script search the Steam libraries for BTA.
                if (pack.Length > 0) cmd.Append(" -Bta ").Append(Ps(pack.TrimEnd('\\')));
                if (copy) cmd.Append(" -Copy");
                if (noSaves) cmd.Append(" -NoSaves");
            }
            cmd.Append("; $code = $LASTEXITCODE; Write-Host ''; Read-Host 'Press Enter to close this window' | Out-Null; exit $code");
            string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            return Process.Start(new ProcessStartInfo(ps, "-NoProfile -ExecutionPolicy Bypass -Command \"" + cmd.ToString().Replace("\"", "\\\"") + "\"")
            {
                WorkingDirectory = dest,
                UseShellExecute = false,
            });
        }

        // ---- the launcher copy and shortcuts ----

        public static string CopyPath(string dest) { return Path.Combine(dest, FileName); }

        /// <summary>
        /// Keeps a copy of this exe in the install as the launcher, so the player has one place to start from after the
        /// download is gone. Not in the install record: an update leaves it alone, and the launcher refreshes it.
        /// Null when it is in place, otherwise why not.
        /// </summary>
        public static string PlaceCopy(string dest)
        {
            string self = Assembly.GetExecutingAssembly().Location, target = CopyPath(dest);
            try
            {
                if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return null;
                if (File.Exists(target) && Same(self, target)) return null;
                // A running launcher copy can't be overwritten, but it can be renamed out of the way.
                string old = target + ".old";
                if (File.Exists(old)) try { File.Delete(old); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                if (File.Exists(target)) File.Move(target, old);
                File.Copy(self, target);
                return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
            {
                return "The launcher couldn't be copied into the install: " + e.Message;
            }
        }

        static bool Same(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            return fa.Length == fb.Length && File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
        }

        /// <summary>Writes a .lnk through the Windows Script Host's shell object. Null when written, otherwise why not.</summary>
        public static string Shortcut(string lnk, string target, string arguments, string workDir, string description)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return "Windows Script Host isn't available, so no shortcut could be made.";
                object shell = Activator.CreateInstance(t);
                try
                {
                    object link = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                    Type lt = link.GetType();
                    lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
                    lt.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, new object[] { arguments ?? "" });
                    lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { workDir });
                    lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { description });
                    lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { target + ",0" });
                    lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
                }
                finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
                return null;
            }
            catch (Exception e) when (e is TargetInvocationException || e is System.Runtime.InteropServices.COMException || e is UnauthorizedAccessException || e is IOException)
            {
                return "The shortcut couldn't be made: " + (e.InnerException ?? e).Message;
            }
        }

        // ---- folders ----

        /// <summary>Where the player writes Player.log: LocalLow\&lt;company&gt;\&lt;product&gt; (ProjectSettings: BTUCU, BattleTechUCU).</summary>
        public static string LogFolder()
        {
            string low = Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) ?? "", "LocalLow");
            foreach (string sub in new[] { Path.Combine("BTUCU", "BattleTechUCU"), Path.Combine("Harebrained Schemes", "BATTLETECH") })
            {
                string p = Path.Combine(low, sub);
                if (File.Exists(Path.Combine(p, "Player.log"))) return p;
            }
            return Path.Combine(low, "BTUCU", "BattleTechUCU");
        }

        public static void Open(string pathOrUrl)
        {
            Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
        }
    }
}
