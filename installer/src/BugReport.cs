// "Collect bug report": one zip of every log the game, its crash handler, the mod loader and the mods may have written,
// plus a short system.txt, for the player to attach to a GitHub issue. Text files have the Windows profile folder
// (the user's home folder) replaced with %USERPROFILE%, since the zip is meant to be posted publicly. Logs the game holds open
// are read with sharing, and streamed, so a huge log never sits in memory. Every file goes in whole first; only when the
// finished zip is over GitHub's attachment limit is it rebuilt with logs cut to their tails (where the failure usually
// is), tighter each pass, until it fits.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ReleaseGate
{
    static class BugReport
    {
        /// <summary>GitHub's issue attachment limit is 25 MB; the zip aims a little under it.</summary>
        public const long GitHubLimit = 25L << 20;
        const long Target = 24L << 20;
        const long MB = 1L << 20;

        // Per-file and all-mod-logs caps (bytes before compression) for each pass; the first pass has none.
        static readonly long[][] Passes =
        {
            new[] { long.MaxValue, long.MaxValue },
            new[] { 64 * MB, 512 * MB },
            new[] { 16 * MB, 160 * MB },
            new[] { 4 * MB, 48 * MB },
            new[] { 1 * MB, 12 * MB },
        };

        static readonly string[] TextExt = { ".log", ".txt", ".ini", ".json", ".tsv", ".xml", ".cfg" };

        sealed class Source { public string Path, Entry; public bool Mod; }

        /// <summary>Writes the zip to <paramref name="folder"/> and returns its path. Call off the UI thread.</summary>
        public static string Collect(string install, LauncherSettings settings, string folder)
        {
            string zipPath = Path.Combine(folder, "BTUCU-bug-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
            var notes = new List<string>();
            List<Source> sources = Gather(install, notes);
            for (int pass = 0; ; pass++)
            {
                long fileCap = Passes[pass][0], modCap = Passes[pass][1];
                var lines = new List<string>(notes);
                using (var zip = new ZipArchive(new FileStream(zipPath, FileMode.Create, FileAccess.Write), ZipArchiveMode.Create))
                {
                    long modBytes = 0;
                    bool modsLeftOut = false;
                    foreach (Source src in sources)
                    {
                        if (src.Mod && modBytes >= modCap) { modsLeftOut = true; continue; }
                        long added = AddFile(zip, src.Path, src.Entry, src.Mod ? Math.Min(fileCap, modCap - modBytes) : fileCap, lines);
                        if (src.Mod) modBytes += added;
                    }
                    if (pass > 0)
                        lines.Insert(0, "Trimmed to fit GitHub's 25 MB limit: each file keeps its last " + (fileCap / MB) + " MB, mod logs " + (modCap / MB) +
                            " MB in all" + (modsLeftOut ? " (later mod logs left out)" : "") + ".");
                    WriteSystem(zip, install, settings, lines);
                }
                if (new FileInfo(zipPath).Length <= Target || pass == Passes.Length - 1) return zipPath;
            }
        }

        /// <summary>Every file the report may hold: game, crash and install files first, mod logs last.</summary>
        static List<Source> Gather(string install, List<string> notes)
        {
            var list = new List<Source>();
            string low = Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) ?? "", "LocalLow");
            string ucuData = Path.Combine(low, "BTUCU", "BattleTechUCU");
            AddDir(list, ucuData, "Player*.log", "game", false);
            AddDir(list, Path.Combine(ucuData, "campaign-sessions"), "*.json", "game/campaign-sessions", false);
            AddDir(list, Path.Combine(low, "Harebrained Schemes", "BATTLETECH"), "Player*.log", "game/hbs-folder", false);

            // Unity's crash handler: %TEMP%\<company>\<product>\Crashes, newest three.
            string crashes = Path.Combine(Path.GetTempPath(), "BTUCU", "BattleTechUCU", "Crashes");
            if (Directory.Exists(crashes))
                foreach (var d in new DirectoryInfo(crashes).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).Take(3))
                    AddDir(list, d.FullName, "*", "crashes/" + d.Name, false);

            list.Add(new Source { Path = LauncherSettings.FilePath, Entry = "launcher/launcher.ini" });
            if (Launcher.IsInstall(install))
            {
                if (Installer.Unfinished(install)) notes.Add("The last install or update of this folder didn't finish (" + Installer.PendingName + " is there).");
                foreach (string f in new[] { "BattleTechPortBootstrap.log", "doorstop_config.ini", Installer.LauncherLogName, Launcher.LinkMarker, "build-label.txt" })
                    list.Add(new Source { Path = Path.Combine(install, f), Entry = "install/" + f });
                string mods = Path.Combine(install, "Mods");
                if (Directory.Exists(mods))
                {
                    notes.Add("Mods folder entries: " + string.Join(", ", SafeDirs(mods).Select(Path.GetFileName)));
                    AddDir(list, Path.Combine(mods, ".modtek"), "*", "mods/.modtek", true);
                    foreach (string dir in SafeDirs(mods).Where(d => Path.GetFileName(d) != ".modtek"))
                        foreach (string f in LogsUnder(dir, 2))
                            list.Add(new Source { Path = f, Entry = "mods/" + Path.GetFileName(dir) + "/" + f.Substring(dir.Length + 1).Replace('\\', '/'), Mod = true });
                }
            }
            return list;
        }

        static void AddDir(List<Source> list, string dir, string pattern, string entryDir, bool mod)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                foreach (string f in Directory.GetFiles(dir, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    list.Add(new Source { Path = f, Entry = entryDir + "/" + Path.GetFileName(f), Mod = mod });
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        static void WriteSystem(ZipArchive zip, string install, LauncherSettings settings, List<string> lines)
        {
            var sys = new StringBuilder();
            sys.AppendLine("BattleTech: Unofficial Community Update (UCU) bug report, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            sys.AppendLine("Install: " + (install ?? "(none)"));
            if (Launcher.IsInstall(install))
            {
                sys.AppendLine("Installed version: " + (Launcher.InstalledVersion(install) ?? "unknown"));
                sys.AppendLine("Mod support: " + (Installer.ModsWanted(install) ? "on" : "off"));
                PackLink link = Launcher.ReadLink(install);
                sys.AppendLine("Mod pack: " + (link == null ? "none linked" : (link.Junction ? "linked to " : "copied from ") + link.Source));
            }
            sys.AppendLine("Graphics API setting: " + settings.GraphicsApi + "; AI thinking: " + settings.AiThinking + " ms; extra arguments: " + (settings.ExtraArgs ?? ""));
            sys.AppendLine("Windows: " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " (64-bit)" : " (32-bit)"));
            sys.AppendLine("Processors: " + Environment.ProcessorCount + "; culture: " + System.Globalization.CultureInfo.CurrentCulture.Name);
            sys.AppendLine();
            sys.AppendLine("Collected:");
            foreach (string l in lines) sys.AppendLine("  " + l);
            byte[] data = Encoding.UTF8.GetBytes(Redact(sys.ToString()));
            using (Stream s = zip.CreateEntry("system.txt", CompressionLevel.Optimal).Open()) s.Write(data, 0, data.Length);
        }

        static IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray(); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return new string[0]; }
        }

        /// <summary>*.log files in <paramref name="dir"/> and up to <paramref name="depth"/> folders below it (mods keep theirs beside their DLL).</summary>
        static IEnumerable<string> LogsUnder(string dir, int depth)
        {
            string[] here;
            try { here = Directory.GetFiles(dir, "*.log"); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { yield break; }
            foreach (string f in here.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) yield return f;
            if (depth > 0)
                foreach (string d in SafeDirs(dir))
                    foreach (string f in LogsUnder(d, depth - 1)) yield return f;
        }

        /// <summary>Adds one file, streamed. Past <paramref name="cap"/> bytes a text file keeps its tail from a line start, and a
        /// binary (a crash dump, useless once cut) is named instead. Returns the bytes read, 0 when missing or unreadable.</summary>
        static long AddFile(ZipArchive zip, string path, string entry, long cap, List<string> lines)
        {
            if (!File.Exists(path)) return 0;
            bool isText = TextExt.Contains(Path.GetExtension(path).ToLowerInvariant());
            try
            {
                using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long len = s.Length;
                    bool cut = len > cap;
                    if (cut && !isText) { lines.Add(entry + ": left out, " + (len / MB) + " MB"); return 0; }
                    using (Stream z = zip.CreateEntry(entry, CompressionLevel.Optimal).Open())
                    {
                        if (!isText) { s.CopyTo(z); lines.Add(entry); return len; }
                        if (cut) s.Seek(len - cap, SeekOrigin.Begin);
                        using (var r = new StreamReader(s, Encoding.UTF8, true, 1 << 16))
                        using (var w = new StreamWriter(z, new UTF8Encoding(false), 1 << 16))
                        {
                            if (cut)
                            {
                                r.ReadLine();   // the partial first line: no path is split at the cut and missed by Redact
                                w.Write("[first part cut: this is the last " + Math.Max(1, cap / MB) + " MB]\n");
                            }
                            for (string line; (line = r.ReadLine()) != null; ) { w.Write(Redact(line)); w.Write('\n'); }
                        }
                    }
                    lines.Add(entry + (cut ? " (last " + Math.Max(1, cap / MB) + " MB of " + (len / MB) + " MB)" : ""));
                    return cut ? cap : len;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                lines.Add(entry + ": not readable (" + e.Message + ")");
                return 0;
            }
        }

        /// <summary>The Windows profile folder, in either slash form, becomes %USERPROFILE%; the zip is posted publicly.</summary>
        static string Redact(string text)
        {
            if (Profile == null) return text;
            foreach (string form in ProfileForms)
                text = ReplaceIgnoreCase(text, form, "%USERPROFILE%");
            return text;
        }

        static readonly string Profile = ProfileFolder();
        static readonly string[] ProfileForms = Profile == null ? new string[0] : new[] { Profile, Profile.Replace('\\', '/'), Profile.Replace("\\", "\\\\") };

        static string ProfileFolder()
        {
            string p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(p) || p.Length < 4 ? null : p;
        }

        static string ReplaceIgnoreCase(string text, string find, string with)
        {
            int i = text.IndexOf(find, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return text;
            var sb = new StringBuilder(text.Length);
            int from = 0;
            for (; i >= 0; i = text.IndexOf(find, from, StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(text, from, i - from).Append(with);
                from = i + find.Length;
            }
            return sb.Append(text, from, text.Length - from).ToString();
        }
    }
}
