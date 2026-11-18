// Release manager: one window that packs a player build and publishes the package to itch.io (butler) or GitHub Releases.
// It packs with the content key, so never ship it. Run it from outside the repository, like ReleasePack (see the README).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ReleaseGate
{
    static class ManagerProgram
    {
        [STAThread]
        static int Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ManagerForm());
            return 0;
        }
    }

    /// <summary>Paths and names only; never a key or token.</summary>
    sealed class Settings
    {
        public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BattleTechUCUReleaseManager");
        readonly string _path = Path.Combine(Folder, "settings.ini");
        readonly Dictionary<string, string> _v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Before the UCU rename (2026-11-13) settings lived in %APPDATA%\BattleTechU6ReleaseManager. The first start after it
        // moves that folder's contents here, and points the GitHub repository at its new name.
        static readonly string OldFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BattleTechU6ReleaseManager");

        static void MoveOld()
        {
            if (Directory.Exists(Folder) || !Directory.Exists(OldFolder)) return;
            Directory.Move(OldFolder, Folder);
            string ini = Path.Combine(Folder, "settings.ini");
            if (File.Exists(ini))
                File.WriteAllText(ini, File.ReadAllText(ini).Replace("repo=InternetDadCo/BattleTech-Next", "repo=BATTLETECH-Unofficial-Community-Update/BattleTech-UCU"));
        }

        public Settings()
        {
            try { MoveOld(); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            try
            {
                if (File.Exists(_path))
                    foreach (string line in File.ReadAllLines(_path))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) _v[line.Substring(0, eq)] = line.Substring(eq + 1);
                    }
            }
            catch (IOException) { }
            // The repositories moved to an organisation (the maintainer, 2026-11-15). GitHub redirects reads under the old owner but
            // the release token is refused there, so a saved old-owner repository follows the move; it is saved on close.
            foreach (string key in new[] { "repo", "dataRepo" })
            {
                string v;
                if (_v.TryGetValue(key, out v) && v.StartsWith("InternetDadCo/BattleTech-UCU", StringComparison.OrdinalIgnoreCase))
                    _v[key] = "BATTLETECH-Unofficial-Community-Update/" + v.Substring("InternetDadCo/".Length);
            }
        }

        public string Get(string key, string fallback)
        {
            string v;
            return _v.TryGetValue(key, out v) && v.Length > 0 ? v : fallback;
        }

        public void Set(string key, string value) { _v[key] = (value ?? "").Replace("\r", " ").Replace("\n", " "); }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllLines(_path, _v.OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Value));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }
    }

    sealed class PlayerInfo
    {
        public string Problem;
        public string Version;
        public bool NeedsAppId;
        public int Files;
        public long Bytes;
        public List<string> Unexpected = new List<string>();
    }

    static class Checks
    {
        // Loose files and folders a release player has beside BattleTech.exe. Anything else that ships gets a warning.
        static readonly string[] KnownTop = { "BattleTech.exe", "UnityPlayer.dll", "UnityCrashHandler64.exe", "steam_appid.txt", "dstorage.dll",
            "dstoragecore.dll", "D3D12", "MonoBleedingEdge", "BattleTech_Data", "ModSupport" };

        public static PlayerInfo Player(string dir)
        {
            var p = new PlayerInfo();
            if (dir.Length == 0) { p.Problem = "Choose the player build folder."; return p; }
            if (!Directory.Exists(dir)) { p.Problem = "That folder doesn't exist."; return p; }
            if (!File.Exists(Path.Combine(dir, "BattleTech.exe"))) { p.Problem = "No BattleTech.exe here. Build the player with that exe name (README, Package a release)."; return p; }
            if (!Directory.Exists(Path.Combine(dir, "BattleTech_Data"))) { p.Problem = "No BattleTech_Data folder beside BattleTech.exe."; return p; }
            p.NeedsAppId = !Gate.HasAppId(dir);
            string root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(root.Length).Replace('\\', '/');
                if (!Pack.Ships(rel)) continue;
                p.Files++;
                p.Bytes += new FileInfo(f).Length;
                string top = rel.Split('/')[0];
                if (!KnownTop.Contains(top, StringComparer.OrdinalIgnoreCase) && !p.Unexpected.Contains(top, StringComparer.OrdinalIgnoreCase)) p.Unexpected.Add(top);
            }
            p.Version = WithFileLabel(dir, DetectVersion(dir));
            return p;
        }

        public static string DetectVersion(string player) { return PlayerVersion.DetectVersion(player); }

        public static string WithFileLabel(string player, string version) { return PlayerVersion.WithFileLabel(player, version); }

        public static string FindButler()
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    string p = Path.Combine(dir.Trim().Trim('"'), "butler.exe");
                    if (dir.Trim().Length > 0 && File.Exists(p)) return p;
                }
                catch (ArgumentException) { }
            }
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string broth = Path.Combine(appData, "itch", "broth", "butler", "versions");
            if (Directory.Exists(broth))
            {
                string best = Directory.GetDirectories(broth).Where(d => File.Exists(Path.Combine(d, "butler.exe")))
                    .OrderByDescending(d => { Version v; return Version.TryParse(Path.GetFileName(d), out v) ? v : new Version(0, 0); }).FirstOrDefault();
                if (best != null) return Path.Combine(best, "butler.exe");
            }
            string apps = Path.Combine(appData, "itch", "apps", "butler", "butler.exe");
            return File.Exists(apps) ? apps : null;
        }

        public static string Quote(string s) { return "\"" + s.TrimEnd('\\') + "\""; }

        public static string SafeLabel(string version)
        {
            string s = Regex.Replace(version ?? "", "[^A-Za-z0-9._-]", "-").Trim('-');
            return s.Length == 0 ? "release" : s;
        }

        public static long DirBytes(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0; }
            catch (IOException) { return 0; }
            catch (UnauthorizedAccessException) { return 0; }
        }

        public static string Gib(long b) { return string.Format("{0:F2} GiB", b / 1073741824.0); }
    }

    sealed class ManagerForm : Form
    {
        const string Title = "BattleTech UCU Release Manager";
        static readonly Color Good = Color.FromArgb(0, 110, 50), Bad = Color.FromArgb(170, 30, 30), Warn = Color.FromArgb(150, 90, 0);
        const string DefaultRepo = "BATTLETECH-Unofficial-Community-Update/BattleTech-UCU";
        // The data repository's releases hold package.vault, the data parts and a copy of the installer; the installer reads
        // them from there (Remote.DefaultSource), so the main repository's release page shows only the installer.
        const string DefaultDataRepo = "BATTLETECH-Unofficial-Community-Update/BattleTech-UCU-data";
        // Checks an install against the public manifest with nothing but PowerShell; build.ps1 -Deploy puts it beside this tool.
        internal const string VerifyScript = "verify.ps1";

        readonly Settings _settings = new Settings();
        readonly string _logFile = Path.Combine(Settings.Folder, "log-" + DateTime.Now.ToString("yyyy-MM-dd") + ".txt");

        // Package tab
        readonly TextBox _player = Box("PlayerPath"), _version = Box("Version"), _retail = Box("RetailPath"), _gog = Box("GogPath"), _key = Box("KeyFile"),
            _extra = Box("ExtraFingerprints"), _package = Box("PackagePath"), _testDest = Box("TestInstallPath");
        readonly Label _playerNote = Note(), _retailNote = Note(), _keyNote = Note(), _packageNote = Note(), _testNote = Note();
        readonly Button _addAppId = Btn("Add steam_appid.txt"), _pack = Btn("Pack"), _fingerprint = Btn("Add install..."),
            _testInstall = Btn("Test install"), _openSetup = Btn("Open installer window"), _startGame = Btn("Start test install");
        // Unlock server (v3 packages, docs/keyserver-design.md)
        readonly TextBox _master = Box("MasterSecretFile"), _unlockUrl = Box("UnlockUrl");
        readonly Label _unlockNote = Note();
        readonly Button _makeMaster = Btn("Create secret");

        // itch.io tab
        readonly TextBox _butler = Box("ButlerPath"), _butlerKey = Box("ButlerKeyFile"), _itchTarget = Box("ItchTarget"), _itchChannel = Box("ItchChannel");
        readonly Label _butlerNote = Note(), _itchNote = Note();
        readonly CheckBox _itchDry = new CheckBox { Text = "Dry run (show what would upload, push nothing)", AutoSize = true, Checked = true };
        readonly Button _itchLogin = Btn("Log in to itch.io..."), _itchStatus = Btn("Check status"), _itchPush = Btn("Push to itch.io");

        // GitHub tab
        readonly TextBox _repo = Box("Repository"), _dataRepo = Box("DataRepository"), _token = Box("TokenFile"), _tag = Box("Tag"), _relName = Box("ReleaseName"),
            _assets = Box("AssetsPath"), _partGib = Box("PartSize"), _changelog = Box("ChangelogFile");
        readonly TextBox _notes = new TextBox { Name = "ReleaseNotes", Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Height = 150, AcceptsReturn = true };
        readonly CheckBox _draft = new CheckBox { Text = "Draft (publish it on GitHub when you're ready)", AutoSize = true, Checked = true },
            _pre = new CheckBox { Text = "Pre-release", AutoSize = true };
        readonly Label _ghNote = Note(), _assetsNote = Note();
        readonly Button _prepare = Btn("Prepare assets"), _ghCheck = Btn("Check access"), _ghTest = Btn("Test install from assets"), _upload = Btn("Upload to GitHub"),
            _publish = Btn("Publish both releases"), _counts = Btn("Download counts");

        // Shared
        readonly ProgressBar _bar = new ProgressBar { Dock = DockStyle.Fill, Maximum = 1000 };
        readonly Label _status = new Label { AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Text = "Ready." };
        readonly Button _cancel = Btn("Cancel");
        readonly TextBox _log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9f) };
        readonly System.Windows.Forms.Timer _settle = new System.Windows.Forms.Timer { Interval = 400 }, _tick = new System.Windows.Forms.Timer { Interval = 250 };

        PlayerInfo _playerInfo;
        string _blobCountFor;
        int _blobCount;
        bool _blobCountStale;
        bool _retailOk, _busy, _versionEdited, _settingVersion;
        int _checkId;
        CancellationTokenSource _cts;
        Func<double> _fraction;
        Func<string> _progressText;

        static TextBox Box(string name) { return new TextBox { Name = name, Dock = DockStyle.Fill }; }
        static Label Note() { return new Label { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 8), MaximumSize = new Size(760, 0) }; }
        static Button Btn(string text) { return new Button { Text = text, AutoSize = true, MinimumSize = new Size(100, 0) }; }

        public ManagerForm()
        {
            SuspendLayout();
            Text = Title;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1000, 900);
            MinimumSize = new Size(820, 640);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(PackageTab());
            // The itch.io tab (ItchTab) is kept but not shown; its push code stays for a later return.
            tabs.TabPages.Add(GitHubTab());

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8, 4, 8, 8) };
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var barRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
            barRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            barRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _bar.Height = 22;
            barRow.Controls.Add(_bar);
            barRow.Controls.Add(_cancel);
            bottom.Controls.Add(barRow);
            bottom.Controls.Add(_status);
            bottom.Controls.Add(_log);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2 };
            split.Panel1.Controls.Add(tabs);
            split.Panel2.Controls.Add(bottom);
            Controls.Add(split);
            Load += (s, e) => { split.SplitterDistance = Math.Max(200, split.Height - 240); };

            _cancel.Enabled = false;
            _cancel.Click += (s, e) => { if (_cts != null) { _cts.Cancel(); _status.Text = "Stopping..."; } };
            _tick.Tick += (s, e) => ShowProgress();
            _settle.Tick += (s, e) => { _settle.Stop(); RunChecks(); };
            FormClosing += OnClosing;

            LoadSettings();
            ResumeLayout(true);

            string here = AppDomain.CurrentDomain.BaseDirectory;
            if (!File.Exists(Path.Combine(here, "BattleTechUCUSetup.exe")))
                Log("BattleTechUCUSetup.exe isn't beside this tool, so Pack can't copy it into a package. Run build.ps1 -Deploy <folder>.");
            if (here.IndexOf(@"\tools\release-gate\", StringComparison.OrdinalIgnoreCase) >= 0)
                Log("Running from inside the repository. On this workstation writes from tools/ fail; run build.ps1 -Deploy <folder> and start it from there.");
            QueueChecks();
        }

        // ---------- layout ----------

        static TableLayoutPanel Grid()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoScroll = true, Padding = new Padding(10) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return t;
        }

        static void Row(TableLayoutPanel t, string caption, Control box, Control button)
        {
            t.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 3) });
            t.Controls.Add(box);
            t.Controls.Add(button ?? new Label { AutoSize = true });
        }

        static void Wide(TableLayoutPanel t, Control c)
        {
            t.Controls.Add(new Label { AutoSize = true });
            t.Controls.Add(c);
            t.SetColumnSpan(c, 2);
        }

        static void Heading(TableLayoutPanel t, string text)
        {
            var l = new Label { Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(3, 12, 3, 6) };
            t.Controls.Add(l);
            t.SetColumnSpan(l, 3);
        }

        static void Para(TableLayoutPanel t, string text)
        {
            var l = new Label { Text = text, AutoSize = true, MaximumSize = new Size(900, 0), Margin = new Padding(3, 0, 3, 8) };
            t.Controls.Add(l);
            t.SetColumnSpan(l, 3);
        }

        static FlowLayoutPanel Buttons(params Control[] b)
        {
            var f = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 4, 0, 8) };
            f.Controls.AddRange(b);
            return f;
        }

        // New player builds land under here (full-8 on). Only where Browse starts, never a required path.
        const string BuildsFolder = @"F:\Unity-Tools\builds";

        Button Browse(TextBox box, bool folder, string prompt, string start = null)
        {
            var b = new Button { Text = "Browse...", AutoSize = true };
            b.Click += (s, e) =>
            {
                if (folder)
                    using (var d = new FolderBrowserDialog { Description = prompt, ShowNewFolderButton = true })
                    {
                        if (Directory.Exists(box.Text)) d.SelectedPath = box.Text;
                        else if (start != null && Directory.Exists(start)) d.SelectedPath = start;
                        if (d.ShowDialog(this) == DialogResult.OK) box.Text = d.SelectedPath;
                    }
                else
                    using (var d = new OpenFileDialog { Title = prompt, CheckFileExists = false })
                    {
                        try { if (box.Text.Length > 0) d.InitialDirectory = Path.GetDirectoryName(box.Text); } catch (ArgumentException) { }
                        if (d.ShowDialog(this) == DialogResult.OK) box.Text = d.FileName;
                    }
            };
            return b;
        }

        TabPage PackageTab()
        {
            var page = new TabPage("1. Package");
            var t = Grid();
            Para(t, "Turn a player build into a package: an installer, package.vault and encrypted data/. Files identical to retail aren't shipped; " +
                "the installer copies them from the player's own BATTLETECH. Choose an existing package folder to publish it without packing again.");
            Heading(t, "Player build");
            Row(t, "Player folder", _player, Browse(_player, true, "Choose the player build folder (holds BattleTech.exe)", BuildsFolder));
            Wide(t, _playerNote);
            Wide(t, Buttons(_addAppId));
            Row(t, "Version", _version, null);
            Heading(t, "Packing");
            Row(t, "BATTLETECH install", _retail, Browse(_retail, true, "Choose the retail BATTLETECH folder"));
            Wide(t, _retailNote);
            Row(t, "GOG install (optional)", _gog, Browse(_gog, true, "Choose a GOG BATTLETECH folder: the package then also installs from GOG"));
            Row(t, "Content key file", _key, Browse(_key, false, "Choose the content key file (created on first pack)"));
            Wide(t, _keyNote);
            Row(t, "Extra fingerprints", _extra, _fingerprint);
            Wide(t, new Label { AutoSize = true, Text = "Optional: other retail builds to accept (GOG, a new Steam version), as hex, separated by spaces.", Margin = new Padding(3, 0, 3, 8) });
            Heading(t, "Unlock server (optional)");
            Para(t, "With both set, the package needs an access code: the installer asks the unlock server for this release's share, and you can " +
                "revoke a code or switch installing off (keyserver/README.md). Leave both empty for a package anyone with BATTLETECH can install.");
            Row(t, "Master secret file", _master, Buttons(Browse(_master, false, "Choose the unlock server's master secret file"), _makeMaster));
            Row(t, "Unlock server URL", _unlockUrl, null);
            Wide(t, _unlockNote);
            Row(t, "Package folder", _package, Browse(_package, true, "Choose a new folder for the package, or an existing package"));
            Wide(t, _packageNote);
            Wide(t, Buttons(_pack));
            Heading(t, "Test the package");
            Para(t, "Install it, then start the game with Steam running and reach the main menu. Hashes can't show a missing file, so boot every package. " +
                "The port and retail share a Steam app ID, so close retail first.");
            Row(t, "Test install folder", _testDest, Browse(_testDest, true, "Choose a folder for the test install"));
            Wide(t, _testNote);
            Wide(t, Buttons(_testInstall, _openSetup, _startGame));
            page.Controls.Add(t);

            foreach (TextBox b in new[] { _player, _retail, _key, _extra, _package, _testDest, _master, _unlockUrl }) b.TextChanged += (s, e) => QueueChecks();
            _makeMaster.Click += (s, e) => MakeMaster();
            _version.TextChanged += (s, e) => { if (!_settingVersion) _versionEdited = true; UpdateDefaults(); };
            _addAppId.Click += (s, e) => AddAppId();
            _fingerprint.Click += (s, e) => AddFingerprint();
            _pack.Click += (s, e) => DoPack();
            _testInstall.Click += (s, e) => TestInstall(_package.Text.Trim(), _testDest.Text.Trim());
            _openSetup.Click += (s, e) => Launch(Path.Combine(_package.Text.Trim(), "BattleTechUCUSetup.exe"), "");
            _startGame.Click += (s, e) => { if (LeaseAllowsBoot()) Launch(Path.Combine(_testDest.Text.Trim(), "BattleTech.exe"), ""); };
            return page;
        }

        TabPage ItchTab()
        {
            var page = new TabPage("2. itch.io");
            var t = Grid();
            Para(t, "Pushes the package folder with butler to a project page restricted to download-key holders. butler sends only blobs that changed since " +
                "the last push. Players install the itch app, redeem their key and press Launch, which runs the installer.");
            Row(t, "butler.exe", _butler, Browse(_butler, false, "Choose butler.exe"));
            Wide(t, _butlerNote);
            Row(t, "API key file", _butlerKey, Browse(_butlerKey, false, "Choose a file holding a butler API key"));
            Wide(t, new Label { AutoSize = true, Text = "Optional. Blank uses the login from \"Log in to itch.io\". The key is passed to butler, never shown or logged.", Margin = new Padding(3, 0, 3, 8) });
            Row(t, "Project (user/game)", _itchTarget, null);
            Row(t, "Channel", _itchChannel, null);
            Wide(t, _itchNote);
            Wide(t, _itchDry);
            Wide(t, Buttons(_itchLogin, _itchStatus, _itchPush));
            page.Controls.Add(t);

            foreach (TextBox b in new[] { _butler, _butlerKey, _itchTarget, _itchChannel }) b.TextChanged += (s, e) => QueueChecks();
            _itchDry.CheckedChanged += (s, e) => UpdateButtons();
            _itchLogin.Click += (s, e) => Launch(_butler.Text.Trim(), "login");
            _itchStatus.Click += (s, e) => ItchStatus();
            _itchPush.Click += (s, e) => ItchPush();
            return page;
        }

        TabPage GitHubTab()
        {
            var page = new TabPage("2. GitHub Releases");
            var t = Grid();
            Para(t, "Releases are public. The repository's release holds only BattleTechUCUSetup.exe; the data repository's release with the same tag " +
                "holds package.vault, the data parts and a copy of the installer. The installer checks BATTLETECH, has Steam confirm the player owns it, " +
                "then downloads from the newest published data release only the files their install folder lacks. GitHub takes at most 1,000 files " +
                "under 2 GiB each per release, so Prepare assets bundles the package's blobs into a few uncompressed zip parts. A package without the " +
                "unlock server is refused.");
            Heading(t, "Assets");
            Row(t, "Assets folder", _assets, Browse(_assets, true, "Choose a new folder for the GitHub assets"));
            Row(t, "Part size (GiB)", _partGib, null);
            Wide(t, _assetsNote);
            Wide(t, Buttons(_prepare, _ghTest));
            Heading(t, "Release");
            Row(t, "Repository", _repo, null);
            Row(t, "Data repository", _dataRepo, null);
            Row(t, "Upload token file", _token, Browse(_token, false, "Choose the file holding the GitHub token that uploads releases"));
            Row(t, "Tag", _tag, null);
            Row(t, "Release title", _relName, null);
            Row(t, "Changelog file", _changelog, Browse(_changelog, false, "Choose CHANGELOG.md: the selected release's section is appended to the notes"));
            Row(t, "Notes", _notes, null);
            Wide(t, Buttons(_draft, _pre));
            Wide(t, _ghNote);
            Wide(t, Buttons(_ghCheck, _upload, _publish, _counts));
            page.Controls.Add(t);

            foreach (TextBox b in new[] { _repo, _dataRepo, _token, _tag, _assets, _partGib }) b.TextChanged += (s, e) => QueueChecks();
            _changelog.TextChanged += (s, e) => { if (!_settingVersion) UpdateDefaults(); };
            _publish.Click += (s, e) => GitHubPublish();
            _counts.Click += (s, e) =>
            {
                GitHub main = Client(), data = DataClient();
                string mainName = _repo.Text.Trim(), dataName = _dataRepo.Text.Trim();
                RunJob("Reading download counts", token => GitHubReleases.DownloadCounts(main, data, mainName, dataName, Log), null, null, null);
            };
            _prepare.Click += (s, e) => PrepareAssets();
            _ghTest.Click += (s, e) => TestInstall(_assets.Text.Trim(), _testDest.Text.Trim().TrimEnd('\\') + "-github");
            _ghCheck.Click += (s, e) => GitHubCheck();
            _upload.Click += (s, e) => GitHubUpload();
            return page;
        }

        // ---------- settings ----------

        void LoadSettings()
        {
            _player.Text = _settings.Get("player", "");
            _retail.Text = _settings.Get("retail", Gate.FindSteam() ?? "");
            _gog.Text = _settings.Get("gog", Gate.FindGog() ?? "");
            _key.Text = _settings.Get("key", "");
            _extra.Text = _settings.Get("extra", "");
            _package.Text = _settings.Get("package", "");
            _testDest.Text = _settings.Get("testDest", "");
            _butler.Text = _settings.Get("butler", Checks.FindButler() ?? "");
            _butlerKey.Text = _settings.Get("butlerKey", "");
            _itchTarget.Text = _settings.Get("itchTarget", "");
            _itchChannel.Text = _settings.Get("itchChannel", "windows");
            _repo.Text = _settings.Get("repo", DefaultRepo);
            _dataRepo.Text = _settings.Get("dataRepo", DefaultDataRepo);
            // Write access is the org token (secrets\btucu-mgmt-token.txt); a saved path to any other token file is replaced by it.
            string org = FindOrgToken(), savedToken = _settings.Get("token", "");
            _token.Text = org != null && !Path.GetFileName(savedToken).Equals(OrgTokenName, StringComparison.OrdinalIgnoreCase) ? org : savedToken;
            _changelog.Text = _settings.Get("changelog", FindChangelog() ?? "");
            _assets.Text = _settings.Get("assets", "");
            _partGib.Text = _settings.Get("partGib", "1.95");
            _master.Text = _settings.Get("master", "");
            _unlockUrl.Text = _settings.Get("unlockUrl", "");
            _settingVersion = true;
            _version.Text = _settings.Get("version", "");
            _settingVersion = false;
            _versionEdited = _version.Text.Length > 0;
            UpdateDefaults();
        }

        void SaveSettings()
        {
            _settings.Set("player", _player.Text); _settings.Set("retail", _retail.Text); _settings.Set("gog", _gog.Text); _settings.Set("key", _key.Text);
            _settings.Set("master", _master.Text); _settings.Set("unlockUrl", _unlockUrl.Text);
            _settings.Set("extra", _extra.Text); _settings.Set("package", Typed(_package)); _settings.Set("testDest", Typed(_testDest));
            _settings.Set("butler", _butler.Text); _settings.Set("butlerKey", _butlerKey.Text); _settings.Set("itchTarget", _itchTarget.Text);
            _settings.Set("itchChannel", _itchChannel.Text); _settings.Set("repo", _repo.Text); _settings.Set("dataRepo", _dataRepo.Text); _settings.Set("token", _token.Text); _settings.Set("changelog", _changelog.Text);
            _settings.Set("assets", Typed(_assets)); _settings.Set("partGib", _partGib.Text); _settings.Set("version", _versionEdited ? _version.Text : "");
            _settings.Save();
        }

        /// <summary>Fills fields that follow the version and the player folder, unless the user typed them.</summary>
        void UpdateDefaults()
        {
            string v = _version.Text.Trim();
            string label = Checks.SafeLabel(v);
            string parent = null;
            try { if (_player.Text.Trim().Length > 0) parent = Path.GetDirectoryName(Path.GetFullPath(_player.Text.Trim()).TrimEnd('\\')); }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            if (parent != null && v.Length > 0)
            {
                Default(_package, Path.Combine(parent, "release-" + label + "-package"));
                Default(_testDest, Path.Combine(parent, "release-gate-test", "install-" + label));
            }
            if (_package.Text.Trim().Length > 0) Default(_assets, _package.Text.Trim().TrimEnd('\\') + "-github");
            if (v.Length > 0)
            {
                Default(_tag, "v" + label);
                Default(_relName, "BattleTech: Unofficial Community Update (UCU) " + v);
            }
            if (_notes.Text.Length == 0 || _notes.Tag as string == _notes.Text)
            {
                _notes.Text = DefaultNotes(v, _changelog.Text.Trim());
                _notes.Tag = _notes.Text;
            }
        }

        // A field follows its computed default until the user types something else into it.
        static void Default(TextBox box, string value)
        {
            if (box.Text.Length == 0 || box.Text == box.Tag as string)
            {
                box.Tag = value;
                box.Text = value;
            }
        }

        // Saved only when the user typed it, so computed paths follow the next version.
        static string Typed(TextBox box) { return box.Text == box.Tag as string ? "" : box.Text; }

        // Notes open with what a player needs, then carry the selected release's CHANGELOG.md section.
        internal static string DefaultNotes(string version, string changelogPath = null)
        {
            string text = "You need two things: the BattleTech UCU launcher (download BattleTechUCUSetup.exe below) and BATTLETECH itself, installed through Steam or GOG. " +
                "The launcher finds your copy, installs UCU to a new folder beside it and starts the game; your own BATTLETECH isn't changed. " +
                "To update, run the launcher again: it keeps a copy of itself in your install folder as BattleTech UCU Launcher.exe. (The \"Source code\" archives aren't needed. Windows SmartScreen may warn because the launcher isn't signed yet.)";
            string section = ChangelogSection(changelogPath, version);
            return section.Length == 0 ? text : text + "\r\n\r\n" + section;
        }

        /// <summary>The CHANGELOG.md section whose "## " heading starts with the version (or its label); empty when there is none.</summary>
        internal static string ChangelogSection(string path, string version)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(version)) return "";
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException) { return ""; }
            string v = version.Trim(), label = Checks.SafeLabel(v);
            int start = -1;
            for (int i = 0; i < lines.Length && start < 0; i++)
                if (lines[i].StartsWith("## ") && (HeadingIs(lines[i], v) || HeadingIs(lines[i], label))) start = i;
            if (start < 0) return "";
            int end = lines.Length;
            for (int i = start + 1; i < lines.Length; i++) if (lines[i].StartsWith("## ")) { end = i; break; }
            return string.Join("\r\n", lines.Skip(start).Take(end - start)).Trim();
        }

        static bool HeadingIs(string heading, string v)
        {
            string h = heading.Substring(3).Trim();
            return h == v || h.StartsWith(v + " ");
        }

        const string OrgTokenName = "btucu-mgmt-token.txt";

        /// <summary>secrets\btucu-mgmt-token.txt in the nearest folder above this tool or the working directory.</summary>
        static string FindOrgToken()
        {
            foreach (string start in new[] { AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory })
            {
                try
                {
                    for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                        if (File.Exists(Path.Combine(d.FullName, "secrets", OrgTokenName))) return Path.Combine(d.FullName, "secrets", OrgTokenName);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
            return null;
        }

        /// <summary>CHANGELOG.md in the nearest folder above this tool or the working directory.</summary>
        static string FindChangelog()
        {
            foreach (string start in new[] { AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory })
            {
                try
                {
                    for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                        if (File.Exists(Path.Combine(d.FullName, "CHANGELOG.md"))) return Path.Combine(d.FullName, "CHANGELOG.md");
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
            return null;
        }

        // ---------- checks ----------

        void QueueChecks()
        {
            _settle.Stop();
            _settle.Start();
            UpdateButtons();
        }

        void RunChecks()
        {
            int id = ++_checkId;
            string player = _player.Text.Trim().Trim('"'), retail = Installer.TidyRetail(_retail.Text);
            _playerNote.Text = player.Length > 0 ? "Checking..." : "";
            _playerNote.ForeColor = SystemColors.ControlText;
            Task.Run(() =>
            {
                PlayerInfo p = null;
                string retailProblem = null, retailPrint = null;
                try { p = Checks.Player(player); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException) { p = new PlayerInfo { Problem = "Can't read it: " + e.Message }; }
                try
                {
                    if (retail.Length == 0) retailProblem = "Choose the retail BATTLETECH folder. Its fingerprint becomes the accepted one.";
                    else retailPrint = Gate.Hex(Gate.Fingerprint(retail));
                }
                catch (FileNotFoundException e) { retailProblem = "Not a complete BATTLETECH install: " + e.FileName + " is missing."; }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException) { retailProblem = "Can't read it: " + e.Message; }
                return new object[] { p, retailProblem, retailPrint };
            }).ContinueWith(t =>
            {
                if (id != _checkId || IsDisposed) return;
                var r = t.Result;
                ShowPlayer((PlayerInfo)r[0]);
                _retailOk = r[1] == null;
                if (_retailOk) Show(_retailNote, "BATTLETECH found, fingerprint " + ((string)r[2]).Substring(0, 16) + "...", Good);
                else Show(_retailNote, (string)r[1], Bad);
                UpdateButtons();
            }, TaskScheduler.FromCurrentSynchronizationContext());
            UpdateButtons();
        }

        void ShowPlayer(PlayerInfo p)
        {
            _playerInfo = p;
            if (p.Problem != null)
            {
                Show(_playerNote, _player.Text.Trim().Length == 0 ? "" : p.Problem, Bad);
                return;
            }
            var s = new StringBuilder(string.Format("{0} files, {1} will be packed.", p.Files, Checks.Gib(p.Bytes)));
            Color c = Good;
            if (p.NeedsAppId) { s.Append(" This build doesn't set the Steam app ID in its code (it predates patch 101), and steam_appid.txt is missing or doesn't hold 637090: without one of them the game may relaunch retail through Steam."); c = Bad; }
            if (p.Unexpected.Count > 0)
            {
                s.Append(" Also ships: " + string.Join(", ", p.Unexpected) + ". Remove development files first, or add them to SkipFiles in Pack.cs.");
                if (c == Good) c = Warn;
            }
            if (p.Version != null) s.Append(" Build version " + p.Version + ".");
            Show(_playerNote, s.ToString(), c);
            if (p.Version != null && (!_versionEdited || _version.Text.Trim().Length == 0))
            {
                _settingVersion = true;
                _version.Text = p.Version;
                _settingVersion = false;
                _versionEdited = false;
                UpdateDefaults();
            }
        }

        static void Show(Label l, string text, Color c)
        {
            l.Text = text;
            l.ForeColor = c;
        }

        static bool HasPackage(string dir)
        {
            try { return dir.Length > 0 && File.Exists(Path.Combine(dir, Gate.VaultName)) && File.Exists(Path.Combine(dir, "BattleTechUCUSetup.exe")); }
            catch (ArgumentException) { return false; }
        }

        static bool IsNewFolder(string dir)
        {
            try { return dir.Length > 0 && Path.IsPathRooted(dir) && !Directory.Exists(dir) && !File.Exists(dir); }
            catch (ArgumentException) { return false; }
        }

        List<byte[]> ExtraFingerprints(out string problem)
        {
            problem = null;
            var list = new List<byte[]>();
            foreach (string h in _extra.Text.Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Regex.IsMatch(h, "^[0-9a-fA-F]{64}$")) { problem = "Each extra fingerprint is 64 hex digits: " + h; return null; }
                list.Add(Pack.FromHex(h));
            }
            return list;
        }

        long PartBytes()
        {
            double g;
            if (!double.TryParse(_partGib.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out g) || g < 0.01 || g >= 2.0) return 0;
            return (long)(g * 1073741824.0);
        }

        void UpdateButtons()
        {
            string package = _package.Text.Trim(), key = _key.Text.Trim(), assets = _assets.Text.Trim(), dest = _testDest.Text.Trim();
            bool playerOk = _playerInfo != null && _playerInfo.Problem == null;
            bool hasPackage = HasPackage(package), newPackage = IsNewFolder(package);
            string extraProblem;
            ExtraFingerprints(out extraProblem);

            if (hasPackage)
            {
                if (_blobCountFor != package || !_busy && _blobCountStale)
                {
                    _blobCount = Directory.Exists(Path.Combine(package, "data")) ? Directory.EnumerateFiles(Path.Combine(package, "data"), "*.pak").Count() : 0;
                    _blobCountFor = package;
                    _blobCountStale = false;
                }
                int blobs = _blobCount;
                Show(_packageNote, string.Format("Existing package, {0} blobs. Publish it from the GitHub tab, or choose a new folder to pack again.", blobs), Good);
            }
            else if (newPackage) Show(_packageNote, "A new package will be written here.", Good);
            else if (package.Length == 0) Show(_packageNote, "Choose a new folder for the package.", Bad);
            else Show(_packageNote, "That folder exists but isn't a package. Choose a new folder.", Bad);

            if (key.Length == 0) Show(_keyNote, "Choose the content key file. Every release must use the same one, or players download everything again.", Bad);
            else if (File.Exists(key)) Show(_keyNote, "Existing key; this release reuses it, so unchanged files keep their blobs.", Good);
            else Show(_keyNote, "No file here yet: Pack creates a new key. Keep it outside the repository and every package, and reuse it for every release.", Warn);
            if (extraProblem != null) Show(_keyNote, extraProblem, Bad);
            string unlockProblem = UnlockProblem();
            if (unlockProblem != null) Show(_unlockNote, unlockProblem, Bad);
            else if (_master.Text.Trim().Length == 0) Show(_unlockNote, "No unlock server: anyone with BATTLETECH and the package can install it.", SystemColors.ControlText);
            else Show(_unlockNote, "Packs a package that needs an access code from " + _unlockUrl.Text.Trim() + ". Back up the master secret with the content key.", Good);
            _makeMaster.Enabled = !_busy && _master.Text.Trim().Length > 0 && !File.Exists(_master.Text.Trim());

            bool testable = hasPackage && _retailOk;
            Show(_testNote, File.Exists(Path.Combine(dest, "BattleTech.exe")) ? "Installed here already; a test install updates it in place." : "", Good);

            _addAppId.Enabled = !_busy && playerOk && _playerInfo.NeedsAppId;
            _fingerprint.Enabled = !_busy;
            _pack.Enabled = !_busy && playerOk && !_playerInfo.NeedsAppId && _retailOk && key.Length > 0 && newPackage && extraProblem == null && unlockProblem == null && _version.Text.Trim().Length > 0;
            _testInstall.Enabled = !_busy && testable && dest.Length > 0;
            _openSetup.Enabled = !_busy && hasPackage;
            _startGame.Enabled = !_busy && File.Exists(Path.Combine(dest, "BattleTech.exe"));

            // itch.io
            string butler = _butler.Text.Trim();
            bool butlerOk = butler.Length > 0 && File.Exists(butler);
            Show(_butlerNote, butlerOk ? "" : "butler.exe not found. Install the itch app (it fetches butler) or download butler from itch.io.", butlerOk ? Good : Bad);
            bool targetOk = Regex.IsMatch(_itchTarget.Text.Trim(), "^[A-Za-z0-9_-]+/[A-Za-z0-9_-]+$") && Regex.IsMatch(_itchChannel.Text.Trim(), "^[A-Za-z0-9_.-]+$");
            if (!hasPackage) Show(_itchNote, "Pack a release, or choose an existing package folder on the Package tab.", Bad);
            else if (!targetOk) Show(_itchNote, "Enter the project as user/game, as in its itch.io address, and a channel such as windows.", Bad);
            else Show(_itchNote, string.Format("Will push {0} to {1}:{2} as version {3}. butler's limit is 30 GB per build.", package, _itchTarget.Text.Trim(), _itchChannel.Text.Trim(), _version.Text.Trim()), Good);
            _itchLogin.Enabled = !_busy && butlerOk;
            _itchStatus.Enabled = !_busy && butlerOk && targetOk;
            _itchPush.Enabled = !_busy && butlerOk && targetOk && hasPackage && _version.Text.Trim().Length > 0;
            _itchPush.Text = _itchDry.Checked ? "Dry run" : "Push to itch.io";

            // GitHub
            bool assetsReady = HasPackage(assets) && AssetParts(assets).Count > 0;
            if (!hasPackage) Show(_assetsNote, "Pack a release, or choose an existing package folder on the Package tab.", Bad);
            else if (PartBytes() == 0) Show(_assetsNote, "Part size is in GiB, below 2 (GitHub's limit per file).", Bad);
            else if (assetsReady) Show(_assetsNote, string.Format("Prepared: {0} files, {1}. Prepare again to rebuild them from the package.", AssetFiles(assets).Count, Checks.Gib(AssetFiles(assets).Sum(f => new FileInfo(f).Length))), Good);
            else Show(_assetsNote, "Prepare writes the installer, package.vault and the data parts here.", SystemColors.ControlText);
            bool repoOk = Regex.IsMatch(_repo.Text.Trim(), "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") && Regex.IsMatch(_dataRepo.Text.Trim(), "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") &&
                !string.Equals(_repo.Text.Trim(), _dataRepo.Text.Trim(), StringComparison.OrdinalIgnoreCase);
            bool tokenOk = _token.Text.Trim().Length > 0 && File.Exists(_token.Text.Trim());
            if (!repoOk) Show(_ghNote, "Enter both repositories as owner/name, and make them different.", Bad);
            else if (!tokenOk) Show(_ghNote, "Choose the file holding your GitHub token with Contents read and write on both repositories; GitHub needs it to create releases " +
                "and upload files. Players need no token. It is read for each request and never logged.", Bad);
            else if (_tag.Text.Trim().Length == 0) Show(_ghNote, "Enter a tag, such as v2.0.0-6A. GitHub creates it on the default branch when the release is published.", Bad);
            else if (assetsReady && LockOf(assets) == null) Show(_ghNote, "This package needs no access code, and the release is public. Set the unlock server on the Package tab, pack again and prepare assets.", Bad);
            else Show(_ghNote, "Upload creates both releases with this tag, or adds to existing ones, and skips files already uploaded. package.vault goes last, " +
                "so installers pass over the data release until it's complete. Publish both releases makes them public together, data first.", Good);
            _prepare.Enabled = !_busy && hasPackage && PartBytes() > 0 && assets.Length > 0 && Path.IsPathRooted(assets);
            _ghTest.Enabled = !_busy && assetsReady && _retailOk && dest.Length > 0;
            _ghCheck.Enabled = !_busy && repoOk && tokenOk;
            _upload.Enabled = !_busy && repoOk && tokenOk && assetsReady && _tag.Text.Trim().Length > 0 && LockOf(assets) != null;
            _publish.Enabled = !_busy && repoOk && tokenOk && _tag.Text.Trim().Length > 0;
            _counts.Enabled = !_busy && repoOk && tokenOk;
        }

        static List<string> AssetParts(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.GetFiles(dir, Volumes.PartPrefix + "-*-data-*.zip").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>(); }
            catch (IOException) { return new List<string>(); }
        }

        /// <summary>The release-notes sentence for the GOG manifest, or "" when the package takes no GOG installs.</summary>
        internal static string GogManifestLine(string assets)
        {
            string f = Path.Combine(assets, PublicManifest.GogFileName);
            return File.Exists(f) ? " GOG installs: " + PublicManifest.GogFileName + ", SHA-256 " + PublicManifest.Sha256Of(File.ReadAllBytes(f)) + "." : "";
        }

        internal static List<string> AssetFiles(string dir)
        {
            // Upload order. The installer only takes a release that has package.vault, so it goes last: a release whose
            // upload stopped part way is passed over rather than installed with parts missing.
            var l = AssetParts(dir);
            l.Add(Path.Combine(dir, Remote.InstallerName));
            l.Add(Path.Combine(dir, PublicManifest.FileName));
            l.Add(Path.Combine(dir, PublicManifest.GogFileName));
            l.Add(Path.Combine(dir, VerifyScript));
            l.Add(Path.Combine(dir, Gate.VaultName));
            return l.Where(File.Exists).ToList();
        }

        // ---------- jobs ----------

        void Log(string line)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), line); return; }
            string stamped = DateTime.Now.ToString("HH:mm:ss") + "  " + line;
            _log.AppendText(stamped + Environment.NewLine);
            if (_logBroken) return;
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                File.AppendAllText(_logFile, stamped + Environment.NewLine);
            }
            catch (Exception e)
            {
                // Never let the log file stop a job's end, which turns the buttons back on.
                _logBroken = true;
                _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  Can't write the log file " + _logFile + " (" + e.GetType().Name + ": " + e.Message + "); logging to this window only." + Environment.NewLine);
            }
        }

        bool _logBroken;

        void RunJob(string what, Func<CancellationToken, string> work, Func<double> fraction, Func<string> text, Action<bool> after)
        {
            SaveSettings();
            _busy = true;
            _cts = new CancellationTokenSource();
            _fraction = fraction;
            _progressText = text;
            _cancel.Enabled = true;
            _bar.Style = fraction == null ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            _bar.Value = 0;
            _status.Text = what + "...";
            Log(what + "...");
            _tick.Start();
            UpdateButtons();
            CancellationToken token = _cts.Token;
            var clock = Stopwatch.StartNew();
            Task.Run(() => work(token), token).ContinueWith(t =>
            {
                _tick.Stop();
                _busy = false;
                _cancel.Enabled = false;
                _bar.Style = ProgressBarStyle.Continuous;
                try
                {
                    bool cancelled = t.IsCanceled || (t.IsFaulted && t.Exception.Flatten().InnerExceptions.All(x => x is OperationCanceledException));
                    bool ok = !t.IsFaulted && !t.IsCanceled;
                    string msg = ok ? t.Result : cancelled ? "Cancelled." : "Failed: " + t.Exception.GetBaseException().Message;
                    if (ok) _bar.Value = _bar.Maximum;
                    _status.Text = what + ": " + msg;
                    Log(string.Format("{0}: {1} ({2:F0} s)", what, msg, clock.Elapsed.TotalSeconds));
                    if (after != null) after(ok);
                }
                catch (Exception e) { _log.AppendText("Error while finishing " + what + ": " + e + Environment.NewLine); }
                finally
                {
                    // Whatever happened above, the job is over: the buttons must come back.
                    _blobCountStale = true;
                    UpdateButtons();
                    RunChecks();
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ShowProgress()
        {
            double f = _fraction != null ? _fraction() : double.NaN;
            if (!double.IsNaN(f)) _bar.Value = (int)Math.Max(0, Math.Min(_bar.Maximum, f * _bar.Maximum));
            if (_progressText != null) _status.Text = _progressText();
        }

        void AddAppId()
        {
            string p = Path.Combine(_player.Text.Trim(), "steam_appid.txt");
            File.WriteAllText(p, Gate.AppId.ToString());
            Log("Wrote " + p);
            RunChecks();
        }

        void AddFingerprint()
        {
            using (var d = new FolderBrowserDialog { Description = "Choose another retail BATTLETECH install to accept" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string dir = d.SelectedPath, hex = null;
                RunJob("Fingerprinting " + dir, token => hex = Gate.Hex(Gate.Fingerprint(Installer.TidyRetail(dir))), null, null, ok =>
                {
                    if (ok) _extra.Text = (_extra.Text.Trim() + " " + hex).Trim();
                });
            }
        }

        void DoPack()
        {
            string player = Path.GetFullPath(_player.Text.Trim()), retail = Installer.TidyRetail(_retail.Text), output = Path.GetFullPath(_package.Text.Trim()), keyFile = _key.Text.Trim();
            string gog = Installer.TidyRetail(_gog.Text);
            string problem;
            List<byte[]> extra = ExtraFingerprints(out problem);
            if (_playerInfo.Unexpected.Count > 0 &&
                MessageBox.Show(this, "These also go into the package: " + string.Join(", ", _playerInfo.Unexpected) + ".\n\nPack anyway?", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            var progress = new PackProgress();
            string masterFile = _master.Text.Trim(), unlockUrl = _unlockUrl.Text.Trim();
            RunJob("Packing " + _version.Text.Trim(), token =>
            {
                try
                {
                    byte[] key = Pack.LoadKey(keyFile, output, Log);
                    byte[] master = masterFile.Length == 0 ? null : Pack.LoadMaster(masterFile, output);
                    return Pack.Run(player, retail, output, key, extra, progress, token, master, master == null ? null : unlockUrl, null, gog.Length == 0 ? null : gog).Summary;
                }
                catch (Exception failed)
                {
                    // The folder was new when packing started, so only this run's partial output is in it. Pack.Run refuses a folder
                    // that exists (one made after the Pack button was enabled), and that folder isn't this run's to remove.
                    if (!(failed is OutputExistsException) && Directory.Exists(output))
                    {
                        try { Directory.Delete(output, true); Log("Removed the partial package " + output); }
                        catch (IOException e) { Log("Couldn't remove the partial package " + output + ": " + e.Message); }
                    }
                    throw;
                }
            },
            () => progress.Total == 0 ? 0 : (double)Interlocked.Read(ref progress.Done) / progress.Total,
            () => progress.Total == 0 ? "Indexing files..." : string.Format("Packing... {0:F1} of {1:F1} GiB", Interlocked.Read(ref progress.Done) / 1073741824.0, progress.Total / 1073741824.0),
            ok => { if (ok) UpdateDefaults(); });
        }

        void TestInstall(string source, string dest)
        {
            string setup = Path.Combine(source, "BattleTechUCUSetup.exe"), retail = Installer.TidyRetail(_retail.Text);
            dest = Path.GetFullPath(dest);
            // A package that needs the unlock server is unlocked here with its share, so a test install needs no server.
            VaultLock lk = LockOf(source);
            string shareFile = null;
            if (lk != null)
            {
                byte[] share;
                try { share = Gate.Share(Pack.LoadMaster(_master.Text.Trim(), null), lk.ReleaseId); }
                catch (Exception e) when (e is IOException || e is ArgumentException || e is FormatException)
                {
                    MessageBox.Show(this, "This package needs the unlock server. Choose its master secret file on the Package tab to test it: " + e.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                shareFile = Path.Combine(Path.GetTempPath(), "btucu-share-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(shareFile, Gate.Hex(share));
            }
            var clock = Stopwatch.StartNew();
            long written = 0;
            RunJob("Test install from " + source, token =>
            {
                int code;
                try { code = RunProcess(setup, "--retail " + Checks.Quote(retail) + " --dest " + Checks.Quote(dest) + (shareFile != null ? " --share-file " + Checks.Quote(shareFile) : ""), null, token, Log); }
                finally { if (shareFile != null) File.Delete(shareFile); }
                string[] meaning = { "installed", "failed", "no retail found", "retail refused", "a file failed its check" };
                string m = code >= 0 && code < meaning.Length ? meaning[code] : "exit code " + code;
                if (code != 0) throw new IOException("installer: " + m + " (exit " + code + ")");
                return "installed to " + dest + ". Start it with Steam running and reach the main menu.";
            }, null, () =>
            {
                if (clock.ElapsedMilliseconds > 2000) { written = Checks.DirBytes(dest); clock.Restart(); }
                return "Installing... " + Checks.Gib(written) + " written";
            }, null);
        }

        // ---------- unlock server ----------

        /// <summary>What stops packing with the unlock settings, or null. Both empty is fine (no server).</summary>
        string UnlockProblem()
        {
            string master = _master.Text.Trim(), url = _unlockUrl.Text.Trim();
            if (master.Length == 0 && url.Length == 0) return null;
            if (master.Length == 0) return "Choose the master secret file too, or clear the URL.";
            if (url.Length == 0) return "Enter the unlock server's address too (https://...workers.dev), or clear the master secret file.";
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttps) return "The unlock server's address must start with https://.";
            if (!File.Exists(master)) return "No master secret file here yet. Create secret makes one; the server needs the same (keyserver/README.md).";
            try { Pack.LoadMaster(master, null); }
            catch (Exception e) when (e is IOException || e is FormatException || e is ArgumentException) { return "The master secret file can't be used: " + e.Message; }
            return null;
        }

        void MakeMaster()
        {
            string file = Path.GetFullPath(_master.Text.Trim());
            if (File.Exists(file)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, Gate.Hex(Gate.Random(32)));
            Log("Created the unlock server's master secret at " + file + ". Back it up with the content key. Give the server the same secret: " +
                "in the keyserver folder run npx wrangler secret put MASTER_SECRET and paste the file's contents (keyserver/README.md).");
            RunChecks();
        }

        internal static VaultLock LockOf(string dir)
        {
            string v = Path.Combine(dir ?? "", Gate.VaultName);
            try
            {
                if (!File.Exists(v)) return null;
                using (var f = new FileStream(v, FileMode.Open, FileAccess.Read, FileShare.Read)) return Gate.PeekLock(f);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException) { return null; }
        }

        /// <summary>
        /// Before a real push of a package that needs the unlock server: the live server must hand out this release's share
        /// for the test code (keys\test-code.txt beside the master secret), or players couldn't install it. Its Steam route
        /// is checked too: required for GitHub, where players have no code; only reported for itch.io. Runs in a job.
        /// </summary>
        internal static void CheckServer(string dir, string masterFile, Action<string> log, bool requireSteam)
        {
            VaultLock lk = LockOf(dir);
            if (lk == null) return;
            if (masterFile.Length == 0) throw new IOException("This package needs the unlock server. Choose its master secret file on the Package tab, so the server can be checked first. Nothing was pushed.");
            byte[] want = Gate.Share(Pack.LoadMaster(masterFile, null), lk.ReleaseId);
            string codeFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(masterFile)), "test-code.txt");
            if (!File.Exists(codeFile))
                throw new IOException("This package needs the unlock server, and there's no test code to check the server with. Make one with " +
                    "keyserver\\admin.ps1 new-code release-manager-test and save the code in " + codeFile + ". Nothing was pushed.");
            Unlock.Code = Unlock.ReadFile(codeFile);
            Unlock.ShareOverride = null;
            byte[] got;
            try { got = Unlock.Get(lk); }
            catch (UnlockException e) { throw new IOException("The unlock server at " + lk.Url + " didn't unlock release " + lk.Id + ": " + e.Message + " Players couldn't install it, so nothing was pushed."); }
            if (!got.SequenceEqual(want))
                throw new IOException("The unlock server at " + lk.Url + " holds a different master secret from " + masterFile + ". Players couldn't install this release, so nothing was pushed.");
            log("The unlock server at " + (Unlock.UrlOverride ?? lk.Url) + " unlocks release " + lk.Id + ".");
            string steam = Unlock.SteamRouteProblem(Unlock.UrlOverride ?? lk.Url);
            if (steam == null) log("Its Steam route reaches Valve: players who own BATTLETECH on Steam need no code.");
            else if (requireSteam)
                throw new IOException("The unlock server can't confirm Steam owners: " + steam + ". Players downloading from GitHub have no code, so nothing was pushed.");
            else log("Warning: the unlock server can't confirm Steam owners: " + steam + ". Only players with an access code can install.");
            if (!File.Exists(Path.Combine(dir, PublicManifest.GogFileName))) return;
            // A package with a GOG section: GOG owners unlock on their install's files. With a GOG install on this PC, a real
            // proof checks that the server holds its fingerprint; without one, a dummy proof checks only that the route is on.
            string gogDir = Gate.FindGog();
            byte[] gogPrint = null;
            if (gogDir != null)
                try { gogPrint = Gate.Fingerprint(gogDir); }
                catch (IOException) { gogDir = null; }
            string gog = Unlock.GogRouteProblem(Unlock.UrlOverride ?? lk.Url, lk, gogPrint, want);
            if (gog == null) log(gogDir != null ? "Its GOG route accepts the GOG install at " + gogDir + ": players who own BATTLETECH on GOG need no code."
                : "Its GOG route is on. No GOG install on this PC, so its GOG_FINGERPRINTS weren't checked against one.");
            else if (requireSteam)
                throw new IOException("The unlock server can't unlock GOG installs: " + gog + ". Players downloading from GitHub have no code, so nothing was pushed.");
            else log("Warning: the unlock server can't unlock GOG installs: " + gog + ". GOG owners need an access code.");
        }

        // Every player runs as Steam app 637090, so a test boot collides with an agent's player leg. The project's lease file
        // (AGENTS.md, Tracks and sessions) says who holds the machine; booting is allowed when it reads free or sam.
        const string LeaseFile = @"F:\Unity-Tools\BattleTech-Unity6\.hermes\reports\editor-lease.txt";

        bool LeaseAllowsBoot()
        {
            string line = "";
            try { if (File.Exists(LeaseFile)) line = File.ReadAllText(LeaseFile).Trim(); }
            catch (IOException) { }
            string track = line.Split('|')[0].Trim();
            if (line.Length == 0 || track.Equals("free", StringComparison.OrdinalIgnoreCase) || track.Equals("sam", StringComparison.OrdinalIgnoreCase)) return true;
            Log("Not starting the game: the lease reads \"" + line + "\".");
            MessageBox.Show(this, "The lease reads:\n\n" + line + "\n\nAnother player run may be up, and a second BattleTech.exe collides with it on Steam. " +
                "Start the test install once the lease reads free (or take it as sam with tools/editor-lease.ps1).", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        void Launch(string exe, string args)
        {
            if (!File.Exists(exe)) { Log("Not found: " + exe); return; }
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
            Log("Started " + exe + (args.Length > 0 ? " " + args : ""));
        }

        /// <summary>Runs a console tool, sends each output line to onLine, and kills it on cancel.</summary>
        static int RunProcess(string exe, string args, Dictionary<string, string> env, CancellationToken stop, Action<string> onLine)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            using (var p = new Process { StartInfo = psi })
            using (var outDone = new ManualResetEvent(false))
            using (var errDone = new ManualResetEvent(false))
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) onLine(e.Data); else outDone.Set(); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) onLine(e.Data); else errDone.Set(); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                while (!p.WaitForExit(200))
                    if (stop.IsCancellationRequested)
                    {
                        try { p.Kill(); } catch (InvalidOperationException) { }
                        p.WaitForExit(10000);
                        throw new OperationCanceledException();
                    }
                // The tool has exited. A child it left running can hold the output pipes open, and a plain WaitForExit()
                // would then wait for it forever, so wait a bounded time for the last lines.
                WaitHandle.WaitAll(new WaitHandle[] { outDone, errDone }, 10000);
                return p.ExitCode;
            }
        }

        // ---------- itch.io ----------

        static string ButlerCreds { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "itch", "butler_creds"); } }

        /// <summary>Without a login butler opens a browser and waits, which a hidden process can't show. Refuse instead.</summary>
        bool ButlerLoggedIn()
        {
            if (_butlerKey.Text.Trim().Length > 0 || File.Exists(ButlerCreds)) return true;
            MessageBox.Show(this, "butler has no itch.io login yet. Press \"Log in to itch.io\", finish the login in the browser, then try again.", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        /// <summary>Logs butler's JSON lines; a login request stops the run, since nobody can answer it.</summary>
        static Action<string> ButlerLines(CancellationTokenSource stop, Action<string> log, Action<double, string> progress)
        {
            var json = new JavaScriptSerializer();
            // A dry run lists every file (31,626 for 8A), one log line each. Logged one by one they queue behind each other
            // on the window's thread for minutes, and the job's end, which turns the buttons back on, waits behind them.
            var listing = new Regex(@"^[-dl][-rwx]{9}\s");
            int listed = 0;
            return line =>
            {
                Dictionary<string, object> d = null;
                try { d = line.StartsWith("{") ? json.DeserializeObject(line) as Dictionary<string, object> : null; }
                catch (ArgumentException) { }
                if (d == null) { log(line); return; }
                object type, v;
                d.TryGetValue("type", out type);
                string t = type as string;
                if (t == "log" && d.TryGetValue("message", out v) && listing.IsMatch(v as string ?? "")) { listed++; return; }
                if (listed > 0) { log(string.Format("[log] ({0} files listed; the list isn't shown)", listed)); listed = 0; }
                if (t == "progress")
                {
                    double f = d.TryGetValue("progress", out v) ? Convert.ToDouble(v) : 0;
                    string eta = d.TryGetValue("eta", out v) ? string.Format(", {0:F0} s left", Convert.ToDouble(v)) : "";
                    if (progress != null) progress(f, eta);
                }
                else if (t == "login")
                {
                    log("butler asked for an itch.io login. Use \"Log in to itch.io\" first.");
                    stop.Cancel();
                }
                else if (d.TryGetValue("message", out v)) log(string.Format("[{0}] {1}", t, v));
                else if (t != "result") log(line);
                else if (progress == null && d.TryGetValue("value", out v)) log(json.Serialize(v));
            };
        }

        Dictionary<string, string> ButlerEnv()
        {
            string f = _butlerKey.Text.Trim();
            if (f.Length == 0) return null;
            return new Dictionary<string, string> { { "BUTLER_API_KEY", File.ReadAllText(f).Trim() } };
        }

        void ItchStatus()
        {
            string butler = _butler.Text.Trim(), target = _itchTarget.Text.Trim();
            if (!ButlerLoggedIn()) return;
            var env = ButlerEnv();
            RunJob("butler status " + target, token =>
            {
                int code;
                using (var stop = CancellationTokenSource.CreateLinkedTokenSource(token))
                    code = RunProcess(butler, "--json status " + target, env, stop.Token, ButlerLines(stop, Log, null));
                if (code != 0) throw new IOException("butler exited " + code + ". Log in first if it asks for credentials.");
                return "done";
            }, null, null, null);
        }

        void ItchPush()
        {
            string butler = _butler.Text.Trim(), package = Path.GetFullPath(_package.Text.Trim()), version = _version.Text.Trim();
            string target = _itchTarget.Text.Trim() + ":" + _itchChannel.Text.Trim();
            bool dry = _itchDry.Checked;
            if (!ButlerLoggedIn()) return;
            if (!dry && MessageBox.Show(this, string.Format("Push {0}\nto {1} as version {2}?\n\nPlayers with download keys get it as an update.", package, target, version),
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            var env = ButlerEnv();
            double frac = 0;
            string eta = "";
            string masterFile = _master.Text.Trim();
            RunJob((dry ? "Dry run of butler push to " : "butler push to ") + target, token =>
            {
                if (!dry) CheckServer(package, masterFile, Log, false);
                string args = "--json push " + Checks.Quote(package) + " " + target + " --userversion " + Checks.Quote(version) + (dry ? " --dry-run" : "");
                int code;
                using (var stop = CancellationTokenSource.CreateLinkedTokenSource(token))
                    code = RunProcess(butler, args, env, stop.Token, ButlerLines(stop, Log, (f, e) => { frac = f; eta = e; }));
                if (code != 0) throw new IOException("butler exited " + code + ". If it asks for credentials, use \"Log in to itch.io\" or an API key file.");
                return dry ? "dry run finished; nothing was pushed" : "pushed. itch.io processes the build before players see it; Check status shows when it's live.";
            }, () => frac, () => string.Format("butler: {0:P0}{1}", frac, eta), null);
        }

        // ---------- GitHub ----------

        void PrepareAssets()
        {
            string package = Path.GetFullPath(_package.Text.Trim()), outDir = Path.GetFullPath(_assets.Text.Trim()), label = Checks.SafeLabel(_version.Text.Trim());
            long partBytes = PartBytes();
            if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
            {
                var ours = AssetFiles(outDir);
                var all = Directory.GetFileSystemEntries(outDir);
                if (all.Length != ours.Count)
                {
                    MessageBox.Show(this, "The assets folder holds other files. Choose an empty or new folder.", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (MessageBox.Show(this, string.Format("Replace the {0} files prepared earlier in {1}?", ours.Count, outDir), Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                foreach (string f in ours) File.Delete(f);
            }
            long total = Directory.EnumerateFiles(Path.Combine(package, "data"), "*.pak").Sum(f => new FileInfo(f).Length), done = 0;
            // This tool's own installer reads data parts; one in a package packed before parts existed doesn't. The vault format is the same.
            string setup = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BattleTechUCUSetup.exe");
            if (!File.Exists(setup))
            {
                MessageBox.Show(this, "BattleTechUCUSetup.exe must sit beside this tool. Run build.ps1 -Deploy <folder>.", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            RunJob("Preparing GitHub assets", token =>
            {
                Directory.CreateDirectory(outDir);
                File.Copy(setup, Path.Combine(outDir, "BattleTechUCUSetup.exe"));
                File.Copy(Path.Combine(package, Gate.VaultName), Path.Combine(outDir, Gate.VaultName));
                // The public manifest, and the script that checks an install against it, go up in the clear.
                foreach (string m in new[] { PublicManifest.FileName, PublicManifest.GogFileName })
                    if (File.Exists(Path.Combine(package, m))) File.Copy(Path.Combine(package, m), Path.Combine(outDir, m));
                string verify = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, VerifyScript);
                if (File.Exists(verify)) File.Copy(verify, Path.Combine(outDir, VerifyScript));
                List<string> parts = Volumes.WriteParts(package, outDir, label, partBytes, n => Interlocked.Add(ref done, n), token);
                // Check that the installer will find every blob, at its size.
                var index = Volumes.Index(outDir);
                foreach (string blob in Directory.EnumerateFiles(Path.Combine(package, "data"), "*.pak"))
                {
                    PartBlob b;
                    if (!index.TryGetValue("data/" + Path.GetFileName(blob), out b) || b.Length != new FileInfo(blob).Length)
                        throw new IOException("Part check failed for " + Path.GetFileName(blob));
                }
                var files = AssetFiles(outDir);
                long biggest = files.Max(f => new FileInfo(f).Length);
                if (biggest >= Volumes.GitHubFileLimit) throw new IOException("A file is 2 GiB or more; GitHub won't take it.");
                if (files.Count > Volumes.GitHubAssetLimit) throw new IOException("More than 1,000 files; use a larger part size.");
                foreach (string f in files) Log(string.Format("  {0}  {1}", Path.GetFileName(f), Checks.Gib(new FileInfo(f).Length)));
                return string.Format("{0} files, {1}, largest {2}; all {3} blobs found in {4} parts", files.Count, Checks.Gib(files.Sum(f => new FileInfo(f).Length)),
                    Checks.Gib(biggest), index.Count, parts.Count);
            },
            () => total == 0 ? 0 : (double)Interlocked.Read(ref done) / total,
            () => string.Format("Writing parts... {0:F1} of {1:F1} GiB", Interlocked.Read(ref done) / 1073741824.0, total / 1073741824.0), null);
        }

        GitHub Client() { return new GitHub(_repo.Text, _token.Text.Trim()); }
        GitHub DataClient() { return new GitHub(_dataRepo.Text, _token.Text.Trim()); }

        void GitHubCheck()
        {
            GitHub main = Client(), data = DataClient();
            string tag = _tag.Text.Trim();
            RunJob("Checking GitHub access", token =>
            {
                var lines = new List<string>();
                foreach (GitHub gh in new[] { main, data })
                {
                    var repo = gh.Repo();
                    object perms;
                    bool push = repo.TryGetValue("permissions", out perms) && perms is Dictionary<string, object> && true.Equals(((Dictionary<string, object>)perms)["push"]);
                    string s = string.Format("{0} is reachable; {1}.", repo["full_name"], push ? "the token can write releases" : "the token CAN'T write; it needs Contents read and write");
                    if (gh.IsEmpty()) s += " It has no commit yet; Upload adds a README first, as GitHub needs one for a release.";
                    else if (tag.Length > 0)
                    {
                        var rel = gh.FindRelease(tag);
                        s += rel == null ? " No release has tag " + tag + " yet." :
                            string.Format(" Release {0} exists ({1}), {2} files.", tag, true.Equals(rel["draft"]) ? "draft" : "published", GitHub.Assets(rel).Count());
                    }
                    Log(s);
                    lines.Add(s);
                }
                return string.Join(" ", lines);
            }, null, null, null);
        }

        void GitHubPublish()
        {
            GitHub main = Client(), data = DataClient();
            string tag = _tag.Text.Trim(), mainName = _repo.Text.Trim(), dataName = _dataRepo.Text.Trim();
            if (MessageBox.Show(this, string.Format("Publish release {0} in {1} and {2}?\n\nBoth become public, and installers start installing it.", tag, dataName, mainName),
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            RunJob("Publishing " + tag, token => GitHubReleases.PublishBoth(main, data, mainName, dataName, tag, Log), null, null, null);
        }

        void GitHubUpload()
        {
            GitHub main = Client(), data = DataClient();
            string mainName = _repo.Text.Trim(), dataName = _dataRepo.Text.Trim();
            string tag = _tag.Text.Trim(), name = _relName.Text.Trim(), body = _notes.Text, assets = Path.GetFullPath(_assets.Text.Trim());
            bool draft = _draft.Checked, pre = _pre.Checked;
            if (LockOf(assets) == null)
            {
                MessageBox.Show(this, "This package needs no access code, and GitHub releases are public. Set the unlock server on the Package tab, " +
                    "pack again and prepare assets.", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // Data release: the parts, the installer (for an older installer that must hand on) and package.vault, last.
            // Main release: the installer only, so its page shows players the one file they need.
            List<string> files = AssetFiles(assets);
            // Main release: the installer, plus the public manifest and verify.ps1 so anyone can see what the release installs.
            var exeOnly = files.Where(f => new[] { Remote.InstallerName, PublicManifest.FileName, PublicManifest.GogFileName, VerifyScript }
                .Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)).ToList();
            string manifestFile = Path.Combine(assets, PublicManifest.FileName);
            string manifestLine = null;
            if (File.Exists(manifestFile))
                manifestLine = "Public manifest: " + PublicManifest.FileName + ", SHA-256 " + PublicManifest.Sha256Of(File.ReadAllBytes(manifestFile)) +
                    ". It lists every file this release installs, with SHA-512 and origin; check an install with " + VerifyScript + " or the installer's Verify install.";
            else if (MessageBox.Show(this, "This package has no public manifest (" + PublicManifest.FileName + "): it was packed before manifests existed. " +
                    "Pack it again to publish one. Upload without it?", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            if (manifestLine != null) manifestLine += GogManifestLine(assets);
            if (manifestLine != null && !body.Contains(manifestLine)) body = (body.TrimEnd() + "\n\n" + manifestLine).TrimStart();
            long total = files.Sum(f => new FileInfo(f).Length) + exeOnly.Sum(f => new FileInfo(f).Length);
            if (MessageBox.Show(this, string.Format("Upload {0} files, {1}, to {2} and the installer to {3}, under tag {4}{5}?\n\nFiles already uploaded at the same size are skipped.",
                    files.Count, Checks.Gib(total), dataName, mainName, tag, draft ? " as drafts" : ""), Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            var sent = new long[1];
            string current = "";
            string masterFile = _master.Text.Trim();
            string dataBody = "Package data for " + name + ". Players: download BattleTechUCUSetup.exe from https://github.com/" + mainName + "/releases instead; " +
                "it downloads these files itself.";
            RunJob("Uploading to GitHub " + tag, token =>
            {
                CheckServer(assets, masterFile, Log, true);
                // Players run only the installer, so it must be the one the public repository's CI built and attested
                // (SCOPE.md extension 8, ci/installer.yml). The build is reproducible, so this tool's own build of the
                // published commit passes; anything else is refused.
                string setupFile = Path.Combine(assets, Remote.InstallerName);
                string setupSha;
                using (var h = Gate.NewSha()) using (var f = File.OpenRead(setupFile)) setupSha = Gate.Hex(h.ComputeHash(f));
                if (!main.HasAttestation(setupSha))
                    throw new IOException("The installer in the assets folder (SHA-256 " + setupSha + ") has no build attestation in " + mainName +
                        ". Publish the installer source (publish-source.ps1), let its Installer workflow run, then build this tool from the same commit " +
                        "and prepare assets again. Nothing was uploaded.");
                Log("Installer SHA-256 " + setupSha + " is attested by " + mainName + "'s CI.");
                int uploaded = 0, skipped = 0;
                var d = GitHubReleases.UploadRelease(data, dataName, tag, name, dataBody, draft, pre, files, sent, fn => current = fn, token, ref uploaded, ref skipped, mainName, Log);
                var m = GitHubReleases.UploadRelease(main, mainName, tag, name, body, draft, pre, exeOnly, sent, fn => current = fn, token, ref uploaded, ref skipped, mainName, Log);
                // A release made earlier keeps its notes; it still gets this package's manifest line.
                if (manifestLine != null && !((m["body"] as string) ?? "").Contains(manifestLine))
                {
                    m = main.AppendToBody(m, manifestLine);
                    Log("Added the public manifest's SHA-256 to the notes of " + tag + " in " + mainName);
                }
                bool drafts = true.Equals(d["draft"]) || true.Equals(m["draft"]);
                return string.Format("{0} uploaded, {1} already there. {2}", uploaded, skipped,
                    drafts ? "Drafts: press Publish both releases to make them public." : "Both releases are live.") + " " + m["html_url"];
            },
            () => total == 0 ? 0 : (double)Interlocked.Read(ref sent[0]) / total,
            () => string.Format("Uploading {0}... {1:F2} of {2:F2} GiB", current, Interlocked.Read(ref sent[0]) / 1073741824.0, total / 1073741824.0), null);
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (_busy)
            {
                if (MessageBox.Show(this, "A job is running. Stop it and close?", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _cts.Cancel();
            }
            SaveSettings();
        }
    }
}
