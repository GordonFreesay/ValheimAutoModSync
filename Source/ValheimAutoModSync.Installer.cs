using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Reflection;

[assembly: AssemblyTitle("Valheim AutoModSync Installer")]
[assembly: AssemblyDescription("Standalone Windows installer for Valheim AutoModSync client, dedicated-server, and host roles.")]
[assembly: AssemblyCompany("GordonFreesay")]
[assembly: AssemblyProduct("Valheim AutoModSync")]
[assembly: AssemblyVersion("2.6.0.0")]
[assembly: AssemblyFileVersion("2.6.0.0")]

internal static class AutoModSyncInstaller
{
    private const string ProductVersion = "2.6.0";
    private const string BepInExVersion = "5.4.2350";
    private const string BepInExSha256 = "37a91c000b4e88f2ed7a4bd7d812239852d2e36cbf0ff0a9f5faacfba46b105f";
    private const string LegacyBootstrapSha256 = "e5b15848829648dc97c7f40df2800c33372500e3b8047944ef1c84a2a107c3b8";

    [STAThread]
    // Intent: Starts the visible standalone installer and refuses to write into protected game folders unless Windows has already elevated this process.
    // Workflow: the EXE is built with a requireAdministrator manifest, so normal launches receive the standard Windows UAC prompt before this code runs.
    private static int Main()
    {
        try
        {
            if (!IsAdministrator())
            {
                MessageBox.Show("Administrator access is required to install into a protected Steam game folder.", "Valheim AutoModSync Installer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (InstallerForm form = new InstallerForm())
            {
                Application.Run(form);
                return form.ExitCode;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Valheim AutoModSync Installer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    // Intent: Checks the current Windows token for the Administrator role; it does not change account or security settings.
    private static bool IsAdministrator()
    {
        try
        {
            WindowsPrincipal principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // Intent: Returns the extracted release folder containing this installer and its Client, Server, and Bundled payload directories.
    private static string PackageRoot()
    {
        return Path.GetDirectoryName(typeof(AutoModSyncInstaller).Assembly.Location);
    }

    private sealed class InstallerForm : Form
    {
        private readonly RadioButton _clientRole;
        private readonly RadioButton _serverRole;
        private readonly RadioButton _hostRole;
        private readonly TextBox _path;
        private readonly Button _browse;
        private readonly Button _detect;
        private readonly Button _install;
        private readonly Button _uninstall;
        private readonly Label _status;
        private readonly CheckBox _removeServerIdentity;
        private readonly TextBox _log;
        private Image _brandLogo;
        private bool _busy;

        private static readonly Color WindowBack = Color.FromArgb(18, 20, 22);
        private static readonly Color PanelBack = Color.FromArgb(27, 30, 33);
        private static readonly Color FieldBack = Color.FromArgb(35, 39, 43);
        private static readonly Color TextMain = Color.FromArgb(235, 237, 239);
        private static readonly Color TextMuted = Color.FromArgb(155, 162, 170);
        private static readonly Color Accent = Color.FromArgb(255, 112, 20);

        public int ExitCode { get; private set; }

        // Intent: Constructs the branded installer UI, defaults to the Client role, performs read-only path detection, and exposes uninstall only for a complete selected-role install.
        internal InstallerForm()
        {
            Text = "Valheim AutoModSync " + ProductVersion + " Installer";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 600);
            MinimumSize = new Size(760, 600);
            Font = new Font("Segoe UI", 9F);
            MaximizeBox = false;
            BackColor = WindowBack;
            ForeColor = TextMain;
            TryApplyWindowIcon();

            PictureBox logo = new PictureBox();
            logo.Location = new Point(22, 18);
            logo.Size = new Size(58, 58);
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            _brandLogo = LoadBrandLogo();
            if (_brandLogo != null) logo.Image = _brandLogo;
            Controls.Add(logo);

            Label title = new Label();
            title.Text = "AUTOMODSYNC";
            title.Font = new Font(Font.FontFamily, 19F, FontStyle.Bold);
            title.ForeColor = TextMain;
            title.AutoSize = true;
            title.Location = new Point(92, 18);
            Controls.Add(title);

            Label subtitle = new Label();
            subtitle.Text = "VALHEIM  •  VERIFIED MOD SYNCHRONIZATION";
            subtitle.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
            subtitle.ForeColor = Accent;
            subtitle.AutoSize = true;
            subtitle.Location = new Point(95, 53);
            Controls.Add(subtitle);

            Label intro = new Label();
            intro.Text = "Install, repair, update, or safely remove AutoModSync. Close Valheim and any dedicated server first.\r\n" +
                         "This installer uses only files included in this release package; it performs no network downloads.";
            intro.ForeColor = TextMuted;
            intro.Size = new Size(690, 44);
            intro.Location = new Point(24, 87);
            Controls.Add(intro);

            GroupBox roles = new GroupBox();
            roles.Text = "Installation type";
            roles.ForeColor = TextMain;
            roles.BackColor = PanelBack;
            roles.Location = new Point(22, 135);
            roles.Size = new Size(716, 92);
            Controls.Add(roles);

            _clientRole = MakeRole("Client", 20);
            _clientRole.Checked = true;
            roles.Controls.Add(_clientRole);

            _serverRole = MakeRole("Dedicated Server", 170);
            roles.Controls.Add(_serverRole);

            _hostRole = MakeRole("Host & Play", 385);
            roles.Controls.Add(_hostRole);

            Label roleHelp = new Label();
            roleHelp.Text = "Client joins synchronized servers. Dedicated Server serves its plugin set. Host & Play installs both roles.";
            roleHelp.UseMnemonic = false;
            roleHelp.ForeColor = TextMuted;
            roleHelp.Location = new Point(20, 56);
            roleHelp.Size = new Size(660, 22);
            roles.Controls.Add(roleHelp);

            Label pathLabel = new Label();
            pathLabel.Text = "Valheim folder";
            pathLabel.ForeColor = TextMain;
            pathLabel.AutoSize = true;
            pathLabel.Location = new Point(22, 242);
            Controls.Add(pathLabel);

            _path = new TextBox();
            _path.Location = new Point(22, 264);
            _path.Size = new Size(552, 23);
            _path.BackColor = FieldBack;
            _path.ForeColor = TextMain;
            _path.BorderStyle = BorderStyle.FixedSingle;
            _path.TextChanged += new EventHandler(PathChanged);
            Controls.Add(_path);

            _browse = new Button();
            _browse.Text = "Browse...";
            _browse.Location = new Point(582, 262);
            _browse.Size = new Size(78, 27);
            StyleSecondaryButton(_browse);
            _browse.Click += new EventHandler(BrowseClicked);
            Controls.Add(_browse);

            _detect = new Button();
            _detect.Text = "Detect";
            _detect.Location = new Point(666, 262);
            _detect.Size = new Size(72, 27);
            StyleSecondaryButton(_detect);
            _detect.Click += new EventHandler(DetectClicked);
            Controls.Add(_detect);

            _status = new Label();
            _status.Text = "Checking installation state...";
            _status.ForeColor = TextMuted;
            _status.Location = new Point(22, 298);
            _status.Size = new Size(716, 24);
            Controls.Add(_status);

            _removeServerIdentity = new CheckBox();
            _removeServerIdentity.Text = "Also remove server config + signing identity";
            _removeServerIdentity.ForeColor = Color.FromArgb(255, 165, 105);
            _removeServerIdentity.BackColor = WindowBack;
            _removeServerIdentity.AutoSize = true;
            _removeServerIdentity.Location = new Point(22, 326);
            _removeServerIdentity.Visible = false;
            Controls.Add(_removeServerIdentity);

            _log = new TextBox();
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BackColor = FieldBack;
            _log.ForeColor = TextMain;
            _log.BorderStyle = BorderStyle.FixedSingle;
            _log.Location = new Point(22, 357);
            _log.Size = new Size(716, 170);
            _log.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(_log);

            _uninstall = new Button();
            _uninstall.Text = "Uninstall";
            _uninstall.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
            _uninstall.Location = new Point(474, 542);
            _uninstall.Size = new Size(120, 36);
            _uninstall.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _uninstall.BackColor = Color.FromArgb(96, 45, 31);
            _uninstall.ForeColor = TextMain;
            _uninstall.FlatStyle = FlatStyle.Flat;
            _uninstall.FlatAppearance.BorderColor = Color.FromArgb(190, 75, 40);
            _uninstall.Visible = false;
            _uninstall.Click += new EventHandler(UninstallClicked);
            Controls.Add(_uninstall);

            _install = new Button();
            _install.Text = "Install";
            _install.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
            _install.Location = new Point(602, 542);
            _install.Size = new Size(136, 36);
            _install.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _install.BackColor = Accent;
            _install.ForeColor = Color.Black;
            _install.FlatStyle = FlatStyle.Flat;
            _install.FlatAppearance.BorderColor = Color.FromArgb(255, 145, 70);
            _install.Click += new EventHandler(InstallClicked);
            Controls.Add(_install);

            Label note = new Label();
            note.Text = "BepInEx is treated as a shared dependency and is preserved during uninstall.";
            note.ForeColor = TextMuted;
            note.Location = new Point(22, 553);
            note.AutoSize = true;
            note.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(note);

            ExitCode = 0;
            DetectSuggestedPath();
            RefreshInstallState();
        }

        private sealed class InstallationState
        {
            internal bool BepInEx;
            internal bool ClientPlugin;
            internal bool ApplyHelper;
            internal bool ServerPlugin;
            internal bool ServerConfig;
            internal bool ServerPrivateKey;
            internal bool ServerPublicKey;
            internal bool ServerReleaseClient;

            internal bool ClientComplete
            {
                get { return BepInEx && ClientPlugin && ApplyHelper; }
            }

            internal bool ServerComplete
            {
                get { return BepInEx && ServerPlugin && ServerConfig && ServerPrivateKey && ServerPublicKey && ServerReleaseClient; }
            }

            internal bool AnyAutoModSync
            {
                get { return ClientPlugin || ApplyHelper || ServerPlugin || ServerConfig || ServerPrivateKey || ServerPublicKey || ServerReleaseClient; }
            }
        }

        // Intent: Applies the executable's embedded AMS icon to the WinForms title bar/taskbar surface instead of leaving the generic framework icon.
        private void TryApplyWindowIcon()
        {
            try
            {
                Icon executableIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (executableIcon != null) Icon = executableIcon;
            }
            catch
            {
                // Branding failure must never block installation, repair, or uninstall.
            }
        }

        // Intent: Applies the understated AMS charcoal/orange style to secondary installer actions without changing their behavior.
        private void StyleSecondaryButton(Button button)
        {
            button.BackColor = PanelBack;
            button.ForeColor = TextMain;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(83, 90, 96);
        }

        // Intent: Loads the same tracked AMS logo embedded by the release builder; a missing resource leaves the installer fully usable with text branding.
        private Image LoadBrandLogo()
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ValheimAutoModSync.Branding.Logo.png"))
                {
                    if (stream == null) return null;
                    using (Image loaded = Image.FromStream(stream))
                        return new Bitmap(loaded);
                }
            }
            catch { return null; }
        }

        // Intent: Re-evaluates install/uninstall availability whenever the operator edits or auto-detects the target path.
        private void PathChanged(object sender, EventArgs e)
        {
            if (!_busy) RefreshInstallState();
        }

        // Intent: Reads only fixed AutoModSync/BepInEx paths to decide whether the selected role is complete enough to expose the uninstall action.
        private InstallationState InspectInstallation(string root)
        {
            InstallationState state = new InstallationState();
            if (String.IsNullOrEmpty(root) || !Directory.Exists(root)) return state;

            string bep = Path.Combine(root, "BepInEx");
            string config = Path.Combine(bep, "config");
            string ams = Path.Combine(bep, "AutoModSync");
            state.BepInEx = File.Exists(Path.Combine(bep, "core", "BepInEx.dll"));
            state.ClientPlugin = File.Exists(Path.Combine(bep, "plugins", "ValheimAutoModSync.Client.dll"));
            state.ApplyHelper = File.Exists(Path.Combine(ams, "ValheimAutoModSync.Apply.exe"));
            state.ServerPlugin = File.Exists(Path.Combine(bep, "plugins", "ValheimAutoModSync.Server.dll"));
            state.ServerConfig = File.Exists(Path.Combine(config, "com.gordonfreesay.valheimautomodsync.server.cfg"));
            state.ServerPrivateKey = File.Exists(Path.Combine(config, "ValheimAutoModSync.private.xml"));
            state.ServerPublicKey = File.Exists(Path.Combine(config, "ValheimAutoModSync.public.xml"));
            state.ServerReleaseClient = File.Exists(Path.Combine(ams, "release", "ValheimAutoModSync.Client.dll"));
            return state;
        }

        // Intent: Returns whether the currently selected Client, Dedicated Server, or Host & Play role has every required AutoModSync component present.
        private bool SelectedRoleComplete(InstallationState state)
        {
            if (state == null) return false;
            if (_clientRole.Checked) return state.ClientComplete;
            if (_serverRole.Checked) return state.ServerComplete;
            return state.ClientComplete && state.ServerComplete;
        }

        // Intent: Updates status text plus Install/Repair/Uninstall affordances from read-only on-disk state; incomplete or ambiguous installs never expose uninstall.
        private void RefreshInstallState()
        {
            if (_status == null || _install == null || _uninstall == null) return;

            string root = "";
            try { root = Path.GetFullPath((_path.Text ?? "").Trim().Trim('"')); }
            catch { }

            InstallationState state = InspectInstallation(root);
            bool complete = SelectedRoleComplete(state);
            bool serverSelected = _serverRole.Checked || _hostRole.Checked;

            _install.Text = complete ? "Repair / Update" : "Install";
            _uninstall.Visible = complete;
            _uninstall.Enabled = complete && !_busy;
            _removeServerIdentity.Visible = complete && serverSelected;
            if (!_removeServerIdentity.Visible) _removeServerIdentity.Checked = false;

            if (complete)
            {
                _status.Text = "Installed • selected role is complete. Repair/update or uninstall safely.";
                _status.ForeColor = Accent;
            }
            else if (state.AnyAutoModSync)
            {
                _status.Text = "Partial AutoModSync install detected. Install/Repair will restore the selected role.";
                _status.ForeColor = Color.FromArgb(255, 185, 110);
            }
            else if (root.Length > 0 && Directory.Exists(root))
            {
                _status.Text = "AutoModSync is not installed for the selected role.";
                _status.ForeColor = TextMuted;
            }
            else
            {
                _status.Text = "Choose or detect a Valheim folder.";
                _status.ForeColor = TextMuted;
            }
        }

        // Intent: Creates one role-selection radio button and wires it to path re-detection when selected.
        private RadioButton MakeRole(string text, int left)
        {
            RadioButton role = new RadioButton();
            role.Text = text;
            role.UseMnemonic = false;
            role.Location = new Point(left, 25);
            role.AutoSize = true;
            role.ForeColor = TextMain;
            role.BackColor = PanelBack;
            role.CheckedChanged += new EventHandler(RoleChanged);
            return role;
        }

        // Intent: Updates the suggested path when the chosen install role changes between client/host and dedicated server.
        private void RoleChanged(object sender, EventArgs e)
        {
            if (_path != null && !_busy)
            {
                DetectSuggestedPath();
                RefreshInstallState();
            }
        }

        // Intent: Lets the user explicitly choose a game/server folder rather than relying on auto-detection.
        private void BrowseClicked(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = _serverRole.Checked ? "Select the folder containing valheim_server.exe" : "Select the folder containing valheim.exe";
                if (Directory.Exists(_path.Text)) dialog.SelectedPath = _path.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) _path.Text = dialog.SelectedPath;
            }
        }

        // Intent: Repeats read-only Steam/common-path detection on explicit user request.
        private void DetectClicked(object sender, EventArgs e)
        {
            DetectSuggestedPath();
        }

        // Intent: Validates the chosen root, installs the selected role(s), and reports each meaningful action in the visible log.
        private void InstallClicked(object sender, EventArgs e)
        {
            if (_busy) return;
            _busy = true;
            SetUiEnabled(false);
            ExitCode = 1;
            try
            {
                _log.Clear();
                string root = Path.GetFullPath((_path.Text ?? "").Trim().Trim('"'));
                ValidateTargetRoot(root);

                if (_clientRole.Checked) InstallClientRole(root);
                else if (_serverRole.Checked) InstallServerRole(root, "Dedicated Server");
                else
                {
                    InstallClientRole(root);
                    InstallServerRole(root, "Host & Play");
                }

                AppendLog("");
                AppendLog("INSTALL COMPLETE");
                ExitCode = 0;
                MessageBox.Show(this, "Valheim AutoModSync " + ProductVersion + " installed successfully.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendLog("");
                AppendLog("ERROR: " + ex.Message);
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _busy = false;
                SetUiEnabled(true);
                RefreshInstallState();
            }
        }

        // Intent: Confirms and removes the selected complete AutoModSync role while preserving shared BepInEx and, by default, server identity/configuration.
        private void UninstallClicked(object sender, EventArgs e)
        {
            if (_busy) return;

            string root;
            try { root = Path.GetFullPath((_path.Text ?? "").Trim().Trim('"')); }
            catch
            {
                MessageBox.Show(this, "Choose a valid Valheim folder first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            InstallationState state = InspectInstallation(root);
            if (!SelectedRoleComplete(state))
            {
                RefreshInstallState();
                MessageBox.Show(this, "The selected role is not a complete AutoModSync installation, so uninstall is not available.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            bool removeServerIdentity = _removeServerIdentity.Visible && _removeServerIdentity.Checked;
            StringBuilder prompt = new StringBuilder();
            prompt.AppendLine("Remove AutoModSync from the selected role?");
            prompt.AppendLine();
            prompt.AppendLine("BepInEx and unrelated mods are always preserved.");
            if (_clientRole.Checked || _hostRole.Checked)
                prompt.AppendLine("Files that AutoModSync previously installed from trusted servers are removed only when their bytes still match AMS ownership records; locally modified files are preserved.");
            if (_serverRole.Checked || _hostRole.Checked)
            {
                if (removeServerIdentity)
                    prompt.AppendLine("Server config and signing identity WILL be removed. A later reinstall will create a new server identity.");
                else
                    prompt.AppendLine("Server config and signing identity will be preserved for a future reinstall.");
            }

            if (MessageBox.Show(this, prompt.ToString(), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            _busy = true;
            SetUiEnabled(false);
            ExitCode = 1;
            try
            {
                _log.Clear();
                ValidateTargetRoot(root);

                if (_clientRole.Checked || _hostRole.Checked)
                {
                    EnsureNoPendingApplyForUninstall(root);
                    UninstallClientRole(root);
                }

                if (_serverRole.Checked || _hostRole.Checked)
                    UninstallServerRole(root, removeServerIdentity);

                AppendLog("");
                AppendLog("UNINSTALL COMPLETE");
                ExitCode = 0;
                MessageBox.Show(this, "AutoModSync was removed from the selected role. Shared BepInEx was preserved.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppendLog("");
                AppendLog("ERROR: " + ex.Message);
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _busy = false;
                SetUiEnabled(true);
                RefreshInstallState();
            }
        }

        // Intent: Refuses uninstall while a client apply transaction is pending so the installer cannot destroy rollback/recovery evidence mid-transaction.
        private void EnsureNoPendingApplyForUninstall(string root)
        {
            string amsRoot = Path.Combine(root, "BepInEx", "AutoModSync");
            if (File.Exists(Path.Combine(amsRoot, "pending.txt")) || Directory.Exists(Path.Combine(amsRoot, "apply-transaction")))
                throw new InvalidOperationException("AutoModSync has a pending/recovery apply transaction. Start Valheim once and let AutoModSync finish recovery before uninstalling.");
        }

        // Intent: Removes the AutoModSync client role, exact still-owned synchronized files, client state, and helper while preserving BepInEx and unrelated/local modifications.
        private void UninstallClientRole(string root)
        {
            AppendLog("Removing AutoModSync client role...");
            RetireOwnedClientFiles(root);

            string bep = Path.Combine(root, "BepInEx");
            string ams = Path.Combine(bep, "AutoModSync");
            DeleteFileIfExists(Path.Combine(bep, "plugins", "ValheimAutoModSync.Client.dll"), "client plugin");
            DeleteFileIfExists(Path.Combine(ams, "ValheimAutoModSync.Apply.exe"), "apply helper");
            DeleteFileIfExists(Path.Combine(ams, "ValheimAutoModSync.Apply.ico"), "apply helper icon");
            DeleteFileIfExists(Path.Combine(bep, "config", "com.gordonfreesay.valheimautomodsync.client.cfg"), "client config");

            string[] clientFiles = new string[]
            {
                "trusted-servers.txt",
                "last-successful-server.txt",
                "ownership-next.txt",
                "reconnect.txt",
                "launch-context.txt",
                "apply.log"
            };
            int i;
            for (i = 0; i < clientFiles.Length; i++)
                DeleteFileIfExists(Path.Combine(ams, clientFiles[i]), "client state " + clientFiles[i]);

            DeleteDirectoryIfExists(Path.Combine(ams, "resume"), "client resume state");
            DeleteDirectoryIfExists(Path.Combine(ams, "staging"), "client staging state");
            DeleteDirectoryIfExists(Path.Combine(ams, "ownership"), "client ownership metadata");

            if (Directory.Exists(ams))
            {
                string[] markers = Directory.GetFiles(ams, "*.once", SearchOption.TopDirectoryOnly);
                for (i = 0; i < markers.Length; i++) DeleteFileIfExists(markers[i], "development marker");
            }

            TryDeleteEmptyDirectory(ams);
            AppendLog("BepInEx and unrelated mods were preserved.");
        }

        // Intent: Removes the AutoModSync server plugin/release cache while preserving operator-owned ClientPayload and server identity/config unless explicitly requested.
        private void UninstallServerRole(string root, bool removeIdentity)
        {
            AppendLog("Removing AutoModSync server role...");
            string bep = Path.Combine(root, "BepInEx");
            string ams = Path.Combine(bep, "AutoModSync");
            string config = Path.Combine(bep, "config");

            DeleteFileIfExists(Path.Combine(bep, "plugins", "ValheimAutoModSync.Server.dll"), "server plugin");
            DeleteFileIfExists(Path.Combine(ams, "release", "ValheimAutoModSync.Client.dll"), "server release client payload");
            DeleteDirectoryIfExists(Path.Combine(ams, "cache"), "server bundle cache");
            TryDeleteEmptyDirectory(Path.Combine(ams, "release"));

            if (Directory.Exists(Path.Combine(ams, "ClientPayload")))
                AppendLog(@"Preserved operator-managed BepInEx\AutoModSync\ClientPayload content.");

            if (removeIdentity)
            {
                DeleteFileIfExists(Path.Combine(config, "com.gordonfreesay.valheimautomodsync.server.cfg"), "server config");
                DeleteFileIfExists(Path.Combine(config, "ValheimAutoModSync.private.xml"), "server private signing identity");
                DeleteFileIfExists(Path.Combine(config, "ValheimAutoModSync.public.xml"), "server public signing identity");
            }
            else
            {
                AppendLog("Preserved server config and signing identity.");
            }

            TryDeleteEmptyDirectory(ams);
            AppendLog("BepInEx and unrelated mods were preserved.");
        }

        // Intent: Removes client files previously installed by AutoModSync only when a strict ownership ledger parses and the live size/SHA-256 still match its last-owned bytes.
        private void RetireOwnedClientFiles(string root)
        {
            string bep = Path.Combine(root, "BepInEx");
            string ams = Path.Combine(bep, "AutoModSync");
            string ownership = Path.Combine(ams, "ownership");
            if (!Directory.Exists(ownership)) return;

            string[] ledgers = Directory.GetFiles(ownership, "*.txt", SearchOption.TopDirectoryOnly);
            HashSet<string> considered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int removed = 0;
            int preserved = 0;
            int i;
            for (i = 0; i < ledgers.Length; i++)
            {
                List<ValheimAutoModSync.AutoModSyncOwnershipEntry> entries;
                string fingerprint;
                try
                {
                    entries = ValheimAutoModSync.AutoModSyncOwnershipState.ReadFile(ledgers[i], "", out fingerprint);
                }
                catch (Exception ex)
                {
                    AppendLog("WARNING: preserved synchronized files for unreadable ownership ledger " + Path.GetFileName(ledgers[i]) + ": " + ex.Message);
                    continue;
                }

                int j;
                for (j = 0; j < entries.Count; j++)
                {
                    ValheimAutoModSync.AutoModSyncOwnershipEntry entry = entries[j];
                    string destinationRoot = entry.Kind == 'P'
                        ? Path.Combine(bep, "plugins")
                        : entry.Kind == 'R' ? Path.Combine(bep, "patchers") : Path.Combine(bep, "config");
                    string destination;
                    try { destination = ValheimAutoModSync.AutoModSyncPathSafety.SafeUnderRoot(destinationRoot, entry.RelativePath, true); }
                    catch
                    {
                        preserved++;
                        continue;
                    }

                    if (!considered.Add(destination)) continue;
                    if (!File.Exists(destination)) continue;

                    FileInfo info = new FileInfo(destination);
                    bool exact = info.Length == entry.Size;
                    if (exact)
                    {
                        string actual;
                        try { actual = Sha256File(destination); }
                        catch { actual = ""; }
                        exact = String.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase);
                    }

                    if (exact)
                    {
                        File.Delete(destination);
                        removed++;
                        AppendLog("Removed AMS-owned synchronized file: " + entry.Kind + ":" + entry.RelativePath);
                        TryDeleteEmptyParents(Path.GetDirectoryName(destination), destinationRoot);
                    }
                    else
                    {
                        preserved++;
                        AppendLog("Preserved locally changed synchronized file: " + entry.Kind + ":" + entry.RelativePath);
                    }
                }
            }

            AppendLog("Ownership cleanup: removed " + removed.ToString(CultureInfo.InvariantCulture) +
                      " exact AMS-owned file(s), preserved " + preserved.ToString(CultureInfo.InvariantCulture) + " changed/ambiguous file(s).");
        }

        // Intent: Deletes one known AutoModSync file if present and logs the action; it never treats absence as an error.
        private void DeleteFileIfExists(string path, string label)
        {
            if (!File.Exists(path)) return;
            File.Delete(path);
            AppendLog("Removed " + label + ".");
        }

        // Intent: Deletes one known AutoModSync-generated directory recursively if present; callers use this only for AMS-owned cache/staging/state roots.
        private void DeleteDirectoryIfExists(string path, string label)
        {
            if (!Directory.Exists(path)) return;
            Directory.Delete(path, true);
            AppendLog("Removed " + label + ".");
        }

        // Intent: Removes empty directories created for a synchronized file without walking above the fixed plugins/patchers/config root.
        private void TryDeleteEmptyParents(string start, string stopRoot)
        {
            string current;
            string stop;
            try
            {
                current = Path.GetFullPath(start ?? "");
                stop = Path.GetFullPath(stopRoot ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { return; }

            while (!String.IsNullOrEmpty(current) && !String.Equals(current, stop, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(current) || Directory.GetFileSystemEntries(current).Length != 0) break;
                    Directory.Delete(current);
                    current = Path.GetDirectoryName(current);
                }
                catch { break; }
            }
        }

        // Intent: Removes one directory only when it is empty so shared/operator-managed AutoModSync content cannot be deleted accidentally.
        private void TryDeleteEmptyDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0)
                    Directory.Delete(path);
            }
            catch { }
        }

        // Intent: Prevents role/path changes and duplicate install/uninstall clicks while filesystem operations are running.
        private void SetUiEnabled(bool enabled)
        {
            _clientRole.Enabled = enabled;
            _serverRole.Enabled = enabled;
            _hostRole.Enabled = enabled;
            _path.Enabled = enabled;
            _browse.Enabled = enabled;
            _detect.Enabled = enabled;
            _install.Enabled = enabled;
            _uninstall.Enabled = enabled && _uninstall.Visible;
            _removeServerIdentity.Enabled = enabled;
        }

        // Intent: Appends one status line and pumps paint events so the log remains visibly current during copies/extraction.
        private void AppendLog(string message)
        {
            _log.AppendText((message ?? "") + Environment.NewLine);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
            Application.DoEvents();
        }

        // Intent: Finds a likely client/server folder from Steam and common drive layouts without writing to any candidate.
        private void DetectSuggestedPath()
        {
            bool server = _serverRole.Checked;
            string found = FindInstallRoot(server);
            if (found.Length > 0) _path.Text = found;
        }

        // Intent: Confirms the selected directory contains valheim.exe or valheim_server.exe for the selected role before changing files.
        private void ValidateTargetRoot(string root)
        {
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The selected Valheim folder does not exist.");
            string exe = _serverRole.Checked ? "valheim_server.exe" : "valheim.exe";
            if (!File.Exists(Path.Combine(root, exe))) throw new FileNotFoundException(exe + " was not found in the selected folder.");
        }

        // Intent: Installs the client plugin/apply helper and only adds the release-bundled visible BepInEx runtime when BepInEx is absent.
        // Safety: preserves existing BepInEx and refuses to overwrite an unknown winhttp.dll proxy.
        private void InstallClientRole(string root)
        {
            AppendLog("Installing AutoModSync client role...");
            string clientDir = Path.Combine(PackageRoot(), "Client");
            string clientDll = Path.Combine(clientDir, "ValheimAutoModSync.Client.dll");
            string applyExe = Path.Combine(clientDir, "BepInEx", "AutoModSync", "ValheimAutoModSync.Apply.exe");
            string applyIcon = Path.Combine(clientDir, "BepInEx", "AutoModSync", "ValheimAutoModSync.Apply.ico");
            string sourceCore = Path.Combine(clientDir, "BepInEx", "core");

            RequireFile(clientDll);
            RequireFile(applyExe);
            RequireFile(applyIcon);
            RequireFile(Path.Combine(clientDir, "winhttp.dll"));
            RequireFile(Path.Combine(clientDir, "doorstop_config.ini"));
            RequireFile(Path.Combine(sourceCore, "BepInEx.dll"));

            RemoveKnownLegacyBootstrap(root);

            if (!File.Exists(Path.Combine(root, "BepInEx", "core", "BepInEx.dll")))
            {
                string targetWinHttp = Path.Combine(root, "winhttp.dll");
                if (File.Exists(targetWinHttp)) throw new InvalidOperationException("winhttp.dll already exists but BepInEx was not detected. Unknown proxy DLL preserved.");

                CopyFile(Path.Combine(clientDir, "winhttp.dll"), targetWinHttp);
                CopyFile(Path.Combine(clientDir, "doorstop_config.ini"), Path.Combine(root, "doorstop_config.ini"));
                CopyDirectory(sourceCore, Path.Combine(root, "BepInEx", "core"));
                AppendLog("Installed bundled BepInEx client runtime.");
            }
            else AppendLog("Existing BepInEx installation preserved.");

            CopyFile(clientDll, Path.Combine(root, "BepInEx", "plugins", "ValheimAutoModSync.Client.dll"));
            CopyFile(applyExe, Path.Combine(root, "BepInEx", "AutoModSync", "ValheimAutoModSync.Apply.exe"));
            CopyFile(applyIcon, Path.Combine(root, "BepInEx", "AutoModSync", "ValheimAutoModSync.Apply.ico"));
            AppendLog("Installed AutoModSync client plugin, apply helper, and helper icon.");
        }

        // Intent: Installs the server plugin/config/release payload and preserves an existing BepInEx install, server config, and server signing identity.
        private void InstallServerRole(string root, string label)
        {
            AppendLog("Installing AutoModSync server role for " + label + "...");
            string serverDll = Path.Combine(PackageRoot(), "Server", "ValheimAutoModSync.Server.dll");
            string configTemplate = Path.Combine(PackageRoot(), "Server", "server-config-example.cfg");
            string clientDll = Path.Combine(PackageRoot(), "Client", "ValheimAutoModSync.Client.dll");
            RequireFile(serverDll);
            RequireFile(configTemplate);
            RequireFile(clientDll);

            if (!File.Exists(Path.Combine(root, "BepInEx", "core", "BepInEx.dll"))) InstallBepInExFromBundle(root);
            else AppendLog("Existing BepInEx installation preserved.");

            string configDir = Path.Combine(root, "BepInEx", "config");
            string pluginDir = Path.Combine(root, "BepInEx", "plugins");
            Directory.CreateDirectory(configDir);
            Directory.CreateDirectory(pluginDir);

            EnsureIdentity(Path.Combine(configDir, "ValheimAutoModSync.private.xml"), Path.Combine(configDir, "ValheimAutoModSync.public.xml"));
            CopyFile(serverDll, Path.Combine(pluginDir, "ValheimAutoModSync.Server.dll"));

            string config = Path.Combine(configDir, "com.gordonfreesay.valheimautomodsync.server.cfg");
            if (!File.Exists(config)) CopyFile(configTemplate, config);
            else AppendLog("Existing server config preserved.");

            string releaseDir = Path.Combine(root, "BepInEx", "AutoModSync", "release");
            CopyFile(clientDll, Path.Combine(releaseDir, "ValheimAutoModSync.Client.dll"));
            string obsolete = Path.Combine(releaseDir, "version.dll");
            if (File.Exists(obsolete)) File.Delete(obsolete);
            AppendLog("Installed AutoModSync server role.");
        }

        // Intent: Installs server-side BepInEx only from the BepInEx archive bundled at release-build time after its fixed upstream SHA-256 is verified.
        // Network behavior: this installer does not download BepInEx at runtime.
        private void InstallBepInExFromBundle(string root)
        {
            if (File.Exists(Path.Combine(root, "winhttp.dll"))) throw new InvalidOperationException("winhttp.dll already exists but BepInEx was not detected. Unknown proxy DLL preserved.");

            string zip = Path.Combine(PackageRoot(), "Bundled", "BepInExPack_Valheim-" + BepInExVersion + ".zip");
            RequireFile(zip);
            if (!String.Equals(Sha256File(zip), BepInExSha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Bundled BepInEx SHA-256 verification failed.");

            ExtractBundledBepInEx(zip, root);
            if (!File.Exists(Path.Combine(root, "BepInEx", "core", "BepInEx.dll")))
                throw new InvalidDataException("BepInEx extraction did not produce BepInEx.dll.");
            AppendLog("Installed verified bundled BepInEx " + BepInExVersion + ".");
        }

        // Intent: Extracts only the known BepInExPack_Valheim subtree and verifies each output remains underneath the selected game root.
        private void ExtractBundledBepInEx(string zipPath, string root)
        {
            const string prefix = "BepInExPack_Valheim/";
            using (FileStream input = File.OpenRead(zipPath))
            using (ZipArchive archive = new ZipArchive(input, ZipArchiveMode.Read, false))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string name = (entry.FullName ?? "").Replace('\\', '/');
                    if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.EndsWith("/", StringComparison.Ordinal)) continue;
                    string relative = NormalizeRelative(name.Substring(prefix.Length));
                    if (relative.Length == 0) continue;
                    string destination = SafeUnder(root, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    using (Stream source = entry.Open())
                    using (FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                        source.CopyTo(output);
                }
            }
        }

        // Intent: Removes only the exact SHA-256-known AutoModSync 2.4.4 packed version.dll; every unknown version.dll is preserved and reported.
        private void RemoveKnownLegacyBootstrap(string root)
        {
            string legacy = Path.Combine(root, "version.dll");
            if (!File.Exists(legacy)) return;
            if (String.Equals(Sha256File(legacy), LegacyBootstrapSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(legacy);
                AppendLog("Removed known legacy AutoModSync 2.4.4 version.dll.");
            }
            else AppendLog("WARNING: unknown version.dll preserved.");
        }

        // Intent: Creates the server RSA identity only when absent; otherwise reuses the existing private identity and rewrites only its corresponding public-key file.
        private void EnsureIdentity(string privatePath, string publicPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(privatePath));
            string publicXml;
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(2048))
            {
                rsa.PersistKeyInCsp = false;
                if (File.Exists(privatePath)) rsa.FromXmlString(File.ReadAllText(privatePath).Trim());
                else File.WriteAllText(privatePath, rsa.ToXmlString(true) + Environment.NewLine, new UTF8Encoding(false));
                publicXml = rsa.ToXmlString(false);
            }
            File.WriteAllText(publicPath, publicXml + Environment.NewLine, new UTF8Encoding(false));
            using (SHA256 sha = SHA256.Create())
            {
                string fingerprint = ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(publicXml)));
                AppendLog("Server identity verification code: " + ValheimAutoModSync.AutoModSyncIdentityDisplay.VerificationCode(fingerprint));
            }
        }

        // Intent: Copies one required package file to one explicit destination and creates only that destination's parent directory.
        private void CopyFile(string source, string destination)
        {
            RequireFile(source);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(source, destination, true);
        }

        // Intent: Recursively copies one known package directory while preserving relative paths beneath the selected destination.
        private void CopyDirectory(string source, string destination)
        {
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Required package directory is missing: " + source);
            string[] files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
            int i;
            for (i = 0; i < files.Length; i++)
            {
                string relative = files[i].Substring(source.TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                CopyFile(files[i], SafeUnder(destination, relative.Replace('\\', '/')));
            }
        }

        // Intent: Fails fast when the extracted release package is incomplete instead of fetching or substituting missing content.
        private void RequireFile(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Required release file is missing.", path);
        }

        // Intent: Computes SHA-256 from local file bytes for pinned BepInEx and legacy-bootstrap verification.
        private string Sha256File(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create()) return ToHex(sha.ComputeHash(stream));
        }

        // Intent: Converts bytes into deterministic lowercase hexadecimal for SHA-256 and server-fingerprint display.
        private string ToHex(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            int i;
            for (i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // Intent: Normalizes archive/package relative paths and rejects parent traversal, drive separators, tabs, and newlines.
        private string NormalizeRelative(string value)
        {
            if (value == null) return "";
            value = value.Replace('\\', '/').TrimStart('/');
            if (value.Length == 0 || value == ".." || value.IndexOf("../", StringComparison.Ordinal) >= 0 || value.IndexOf(':') >= 0 ||
                value.IndexOf('\t') >= 0 || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0) return "";
            return value;
        }

        // Intent: Resolves one normalized relative path under a fixed root and rejects any full path that escapes that root.
        private string SafeUnder(string root, string relative)
        {
            relative = NormalizeRelative(relative);
            if (relative.Length == 0) throw new InvalidDataException("Unsafe installer path.");
            string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Installer path escaped target root.");
            return full;
        }

        // Intent: Finds the first existing Valheim client/server folder from Steam registry roots, Steam libraries, and common drive layouts without modifying candidates.
        private string FindInstallRoot(bool server)
        {
            string exe = server ? "valheim_server.exe" : "valheim.exe";
            string game = server ? "Valheim dedicated server" : "Valheim";
            List<string> candidates = new List<string>();
            List<string> steamRoots = new List<string>();
            AddRegistrySteamRoot(steamRoots, Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            AddRegistrySteamRoot(steamRoots, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            AddRegistrySteamRoot(steamRoots, Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");

            int i;
            for (i = 0; i < steamRoots.Count; i++)
            {
                AddCandidate(candidates, Path.Combine(steamRoots[i], "steamapps", "common", game));
                AddSteamLibraries(candidates, steamRoots[i], game);
            }

            string[] drives = Environment.GetLogicalDrives();
            for (i = 0; i < drives.Length; i++)
            {
                AddCandidate(candidates, Path.Combine(drives[i], "SteamLibrary", "steamapps", "common", game));
                AddCandidate(candidates, Path.Combine(drives[i], "Steam", "steamapps", "common", game));
                if (server) AddCandidate(candidates, Path.Combine(drives[i], "SteamCMD", "steamapps", "common", game));
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (i = 0; i < candidates.Count; i++)
            {
                string candidate;
                try { candidate = Path.GetFullPath(candidates[i]); } catch { continue; }
                if (seen.Add(candidate) && File.Exists(Path.Combine(candidate, exe))) return candidate;
            }
            return "";
        }

        // Intent: Reads a Steam installation root from the Windows registry for discovery only; it never writes registry data.
        private void AddRegistrySteamRoot(List<string> roots, RegistryKey hive, string subkey, string valueName)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(subkey))
                {
                    if (key == null) return;
                    object value = key.GetValue(valueName);
                    if (value != null && !String.IsNullOrEmpty(value.ToString())) roots.Add(value.ToString().Replace('/', Path.DirectorySeparatorChar));
                }
            }
            catch { }
        }

        // Intent: Reads Steam libraryfolders.vdf and appends the requested Valheim common-folder candidate from each declared Steam library.
        private void AddSteamLibraries(List<string> candidates, string steamRoot, string gameName)
        {
            try
            {
                string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) return;
                string[] lines = File.ReadAllLines(vdf);
                int i;
                for (i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (!line.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
                    int first = line.IndexOf('"', 6);
                    if (first < 0) continue;
                    int second = line.IndexOf('"', first + 1);
                    if (second < 0) continue;
                    string library = line.Substring(first + 1, second - first - 1).Replace("\\\\", "\\");
                    AddCandidate(candidates, Path.Combine(library, "steamapps", "common", gameName));
                }
            }
            catch { }
        }

        // Intent: Adds one non-empty candidate path to the read-only discovery list; existence/deduplication is checked later.
        private void AddCandidate(List<string> candidates, string path)
        {
            if (!String.IsNullOrEmpty(path)) candidates.Add(path);
        }
    }
}
