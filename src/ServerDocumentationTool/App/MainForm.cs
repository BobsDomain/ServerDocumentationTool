using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ServerDocumentationTool.Collect;
using ServerDocumentationTool.Common;

namespace ServerDocumentationTool
{
    internal sealed class MainForm : Form
    {
        private static readonly Color ColRunning = Color.FromArgb(220, 235, 250);
        private static readonly Color ColComplete = Color.FromArgb(212, 237, 218);
        private static readonly Color ColPartial = Color.FromArgb(255, 243, 205);
        private static readonly Color ColFailed = Color.FromArgb(248, 215, 218);
        private static readonly Color ColCancelled = Color.FromArgb(231, 230, 230);

        private readonly Settings config;
        private readonly RunLogger logger;
        private bool running;
        private CancellationTokenSource cts;
        private Credential credential;
        private string lastOutputFolder;
        private ReleaseInfo availableUpdate;
        private readonly List<string> discoveryLog = new List<string>(); // v2.0.1: copied into the run log

        // controls
        private readonly TextBox txtClient = new TextBox();
        private readonly TextBox txtOutput = new TextBox();
        private readonly Button btnBrowse = new Button();
        private readonly CheckBox chkCred = new CheckBox();
        private readonly Button btnCred = new Button();
        private readonly Button btnSettings = new Button();
        private readonly NumericUpDown numParallel = new NumericUpDown();
        private readonly TextBox txtServers = new TextBox();
        private readonly Button btnAddServers = new Button();
        private readonly Button btnImport = new Button();
        private readonly Button btnAddLocal = new Button();
        private readonly Button btnDiscover = new Button();
        private readonly Button btnClear = new Button();
        private readonly Button btnTest = new Button();
        private readonly Button btnRemove = new Button();
        private readonly Button btnStart = new Button();
        private readonly Button btnCancel = new Button();
        private readonly Button btnOpen = new Button();
        private readonly Button btnUpdate = new Button();
        private readonly Button btnUpdateFile = new Button();
        private readonly LinkLabel lnkUpdate = new LinkLabel();
        private readonly Label lblCount = new Label();
        private readonly DataGridView grid = new DataGridView();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly TextBox txtLog = new TextBox();

        public MainForm()
        {
            config = Settings.Load();
            BuildLayout();
            logger = new RunLogger(txtLog);
            Shown += OnShownAsync;
            FormClosing += OnFormClosing;
        }

        // ------------------------------------------------------------------ layout

        private void BuildLayout()
        {
            Text = AppInfo.ProductName + " " + AppInfo.VersionText;
            Font = new Font("Segoe UI", 9F);
            Size = new Size(1280, 880);
            MinimumSize = new Size(1100, 760);
            StartPosition = FormStartPosition.CenterScreen;

            var title = new Label { Text = AppInfo.ProductName, Font = new Font("Segoe UI", 18F, FontStyle.Bold), Location = new Point(20, 10), Size = new Size(620, 36) };
            var lblVersion = new Label { Text = AppInfo.VersionText, ForeColor = SystemColors.GrayText, Location = new Point(640, 24), Size = new Size(120, 20) };
            lnkUpdate.Location = new Point(780, 20);
            lnkUpdate.Size = new Size(460, 22);
            lnkUpdate.TextAlign = ContentAlignment.MiddleRight;
            lnkUpdate.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lnkUpdate.Visible = false;
            lnkUpdate.LinkClicked += async (s, e) => await InstallUpdateAsync();

            var lblClient = new Label { Text = "Client Name", Location = new Point(25, 58), Size = new Size(90, 22) };
            txtClient.Location = new Point(120, 55); txtClient.Size = new Size(300, 24); txtClient.Text = config.Str("DefaultClientName");
            chkCred.Text = "Use alternate credentials"; chkCred.Location = new Point(840, 55); chkCred.Size = new Size(190, 24);
            chkCred.CheckedChanged += (s, e) => OnCredentialToggled();
            btnCred.Text = "Change..."; btnCred.Location = new Point(1035, 53); btnCred.Size = new Size(90, 26); btnCred.Enabled = false;
            btnCred.Click += (s, e) => PromptCredential();

            var lblOutput = new Label { Text = "Output Path", Location = new Point(25, 88), Size = new Size(90, 22) };
            txtOutput.Location = new Point(120, 85); txtOutput.Size = new Size(610, 24); txtOutput.Text = config.Str("DefaultOutputRoot");
            btnBrowse.Text = "Browse..."; btnBrowse.Location = new Point(736, 83); btnBrowse.Size = new Size(90, 26);
            btnBrowse.Click += (s, e) => BrowseOutput();
            btnSettings.Text = "Open Settings"; btnSettings.Location = new Point(840, 83); btnSettings.Size = new Size(120, 26);
            btnSettings.Click += (s, e) => OpenSettings();
            var lblParallel = new Label { Text = "Servers at once", Location = new Point(1015, 88), Size = new Size(100, 22) };
            numParallel.Location = new Point(1118, 85); numParallel.Size = new Size(55, 24); numParallel.Minimum = 1; numParallel.Maximum = 16;
            numParallel.Value = config.Int("ParallelServers", 1, 16);

            var grp = new GroupBox { Text = "Target Servers", Location = new Point(25, 118), Size = new Size(1215, 96), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            var lblAdd = new Label { Text = "Enter server name(s) - separate with comma, space or new line:", Location = new Point(12, 22), Size = new Size(430, 20) };
            txtServers.Location = new Point(12, 44); txtServers.Size = new Size(560, 44); txtServers.Multiline = true; txtServers.ScrollBars = ScrollBars.Vertical;
            SetButton(btnAddServers, "Add Servers", 584, 44, 110); btnAddServers.Click += async (s, e) => await AddFromTextAsync();
            SetButton(btnImport, "Import from File...", 704, 44, 130); btnImport.Click += async (s, e) => await ImportAsync();
            SetButton(btnAddLocal, "Add Local Server", 844, 44, 120); btnAddLocal.Click += (s, e) => AddLocal();
            SetButton(btnDiscover, "Discover Domain", 974, 44, 120); btnDiscover.Click += async (s, e) => await DiscoverAsync();
            SetButton(btnClear, "Clear List", 1104, 44, 95); btnClear.Click += (s, e) => { grid.Rows.Clear(); UpdateCount(); logger.Log("Cleared server list."); };
            grp.Controls.AddRange(new Control[] { lblAdd, txtServers, btnAddServers, btnImport, btnAddLocal, btnDiscover, btnClear });

            SetButton(btnTest, "Test Reachability", 25, 224, 150, 32); btnTest.Click += async (s, e) => await TestReachabilityAsync();
            SetButton(btnRemove, "Remove Selected", 185, 224, 140, 32); btnRemove.Click += (s, e) => RemoveSelected();
            SetButton(btnStart, "Start Assessment", 335, 224, 160, 32); btnStart.Click += async (s, e) => await StartAssessmentAsync();
            SetButton(btnCancel, "Cancel", 505, 224, 100, 32); btnCancel.Enabled = false; btnCancel.Click += (s, e) => CancelRun();
            SetButton(btnOpen, "Open Output Folder", 615, 224, 150, 32); btnOpen.Enabled = false; btnOpen.Click += (s, e) => OpenOutputFolder();
            SetButton(btnUpdate, "Check for Updates", 775, 224, 140, 32); btnUpdate.Click += async (s, e) => await CheckForUpdatesAsync(false);
            SetButton(btnUpdateFile, "Update from File...", 925, 224, 150, 32); btnUpdateFile.Click += (s, e) => UpdateFromFile();
            lblCount.Location = new Point(1085, 230); lblCount.Size = new Size(155, 22); lblCount.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            grid.Location = new Point(25, 268); grid.Size = new Size(1215, 330);
            grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.RowHeadersVisible = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            var colInclude = new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = "Include", FillWeight = 40 };
            grid.Columns.Add(colInclude);
            foreach (var c in new[] { new[] { "Server", "170" }, new[] { "Source", "70" }, new[] { "Reachability", "150" }, new[] { "Roles", "170" }, new[] { "Status", "150" }, new[] { "Transport", "60" }, new[] { "Result", "170" } })
            {
                var col = new DataGridViewTextBoxColumn { Name = c[0], HeaderText = c[0], ReadOnly = true, FillWeight = float.Parse(c[1], System.Globalization.CultureInfo.InvariantCulture) };
                grid.Columns.Add(col);
            }

            progress.Location = new Point(25, 606); progress.Size = new Size(1215, 20);
            progress.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            txtLog.Location = new Point(25, 634); txtLog.Size = new Size(1215, 190);
            txtLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            txtLog.Multiline = true; txtLog.ScrollBars = ScrollBars.Vertical; txtLog.ReadOnly = true; txtLog.BackColor = SystemColors.Window;
            txtLog.Font = new Font("Consolas", 9F);

            Controls.AddRange(new Control[] { title, lblVersion, lnkUpdate, lblClient, txtClient, chkCred, btnCred, lblOutput, txtOutput, btnBrowse, btnSettings, lblParallel, numParallel, grp,
                btnTest, btnRemove, btnStart, btnCancel, btnOpen, btnUpdate, btnUpdateFile, lblCount, grid, progress, txtLog });
            UpdateCount();
            UpdateButtons();
        }

        private static void SetButton(Button b, string text, int x, int y, int width, int height = 28)
        {
            b.Text = text; b.Location = new Point(x, y); b.Size = new Size(width, height);
        }

        // ------------------------------------------------------------------ start-up

        private async void OnShownAsync(object sender, EventArgs e)
        {
            logger.Log(AppInfo.ProductName + " " + AppInfo.VersionText + " - native Windows application (WMI/DCOM, Remote Registry, LDAP; no PowerShell or WinRM needed).");
            logger.Log("Settings: " + config.Path + (config.LoadError != null ? " (could not be read, defaults used: " + config.LoadError + ")" : string.Empty));
            if (Environment.GetCommandLineArgs().Skip(1).Any(a => string.Equals(a, "--updated", StringComparison.OrdinalIgnoreCase)))
                logger.Log("Updated to " + AppInfo.VersionText + ".");
            logger.Log("Add target servers by name, import a list, add the local server, or discover the domain.");
            UpdateButtons();
            if (config.Bool("CheckForUpdatesOnStart")) await CheckForUpdatesAsync(true);
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (running)
            {
                var answer = MessageBox.Show(this, "An assessment is still running. Cancel it and close?", AppInfo.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) { e.Cancel = true; return; }
                CancelRun();
            }
        }

        // ------------------------------------------------------------------ helpers

        private void Ui(Action a)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(a); else a();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void UpdateButtons()
        {
            bool idle = !running;
            foreach (var b in new Control[] { btnAddServers, btnImport, btnAddLocal, btnDiscover, btnClear, btnTest, btnRemove, btnStart })
                b.Enabled = idle;
            txtClient.Enabled = !running; txtOutput.Enabled = !running; btnBrowse.Enabled = !running;
            chkCred.Enabled = !running; btnCred.Enabled = !running && chkCred.Checked; numParallel.Enabled = !running;
            btnCancel.Enabled = running;
            btnUpdate.Enabled = !running;
            btnUpdateFile.Enabled = !running;
            btnOpen.Enabled = !string.IsNullOrEmpty(lastOutputFolder) && Directory.Exists(lastOutputFolder);
        }

        private void UpdateCount()
        {
            lblCount.Text = string.Format("{0} server(s) queued", grid.Rows.Count);
        }

        private int AddServerRows(IEnumerable<string> names, string source)
        {
            return AddServerRows(names.Select(n => new KeyValuePair<string, string>(n, source)));
        }

        private int AddServerRows(IEnumerable<KeyValuePair<string, string>> servers)
        {
            int added = 0;
            foreach (var s in servers)
            {
                string n = (s.Key ?? string.Empty).Trim();
                if (n.Length == 0) continue;
                bool exists = grid.Rows.Cast<DataGridViewRow>().Any(r => string.Equals(Convert.ToString(r.Cells["Server"].Value), n, StringComparison.OrdinalIgnoreCase));
                if (exists) continue;
                grid.Rows.Add(true, n, s.Value, "Not tested", "", "Queued", "", "");
                added++;
            }
            UpdateCount();
            return added;
        }

        private List<DataGridViewRow> IncludedRows()
        {
            return grid.Rows.Cast<DataGridViewRow>()
                .Where(r => Convert.ToBoolean(r.Cells["Include"].Value ?? false) && !string.IsNullOrWhiteSpace(Convert.ToString(r.Cells["Server"].Value)))
                .ToList();
        }

        private static void SetRowColour(DataGridViewRow row, Color colour)
        {
            row.DefaultCellStyle.BackColor = colour;
        }

        private static Color ColourFor(string status)
        {
            switch (status)
            {
                case "Complete": return ColComplete;
                case "Partial": return ColPartial;
                case "Failed": return ColFailed;
                case "Cancelled": return ColCancelled;
                default: return SystemColors.Window;
            }
        }

        // ------------------------------------------------------------------ credentials / output

        private void OnCredentialToggled()
        {
            if (chkCred.Checked && credential == null)
            {
                if (!PromptCredential()) chkCred.Checked = false;
            }
            btnCred.Enabled = chkCred.Checked && !running;
        }

        private bool PromptCredential()
        {
            using (var dlg = new CredentialDialog(credential != null ? credential.FullUser : null))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    credential = dlg.GetCredential();
                    logger.Log("Alternate credentials set for " + credential.FullUser + ".");
                    return true;
                }
            }
            return false;
        }

        private Credential CurrentCredential()
        {
            return chkCred.Checked ? credential : null;
        }

        private void BrowseOutput()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Folder for the as-built output", ShowNewFolderButton = true })
            {
                if (Directory.Exists(txtOutput.Text)) dlg.SelectedPath = txtOutput.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK) txtOutput.Text = dlg.SelectedPath;
            }
        }

        private void OpenSettings()
        {
            try
            {
                if (!File.Exists(config.Path)) Settings.Load(config.Path);
                Process.Start("notepad.exe", "\"" + config.Path + "\"");
                logger.Log("Opened the settings file " + config.Path + ". Changes apply the next time the application starts.");
            }
            catch (Exception ex) { logger.Log("Could not open the settings file: " + ex.Message); }
        }

        private void OpenOutputFolder()
        {
            if (!string.IsNullOrEmpty(lastOutputFolder) && Directory.Exists(lastOutputFolder))
                Process.Start("explorer.exe", "\"" + lastOutputFolder + "\"");
        }

        // ------------------------------------------------------------------ server list

        private Task AddFromTextAsync()
        {
            var list = RunFiles.ParseText(txtServers.Text);
            if (list.Count == 0) { logger.Log("No valid server names entered."); return Task.CompletedTask; }
            int n = AddServerRows(list, "Manual Entry");
            logger.Log(string.Format("Added {0} server(s) from manual entry.", n));
            txtServers.Clear();
            return Task.CompletedTask;
        }

        private async Task ImportAsync()
        {
            using (var dlg = new OpenFileDialog { Filter = "Server lists (*.txt;*.csv)|*.txt;*.csv|All files (*.*)|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string path = dlg.FileName;
                try
                {
                    var found = await Task.Run(() => RunFiles.ImportList(path));
                    string source = string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase) ? "CSV Import" : "TXT Import";
                    int n = AddServerRows(found, source);
                    logger.Log(string.Format("Imported {0} server(s) from {1}.", n, Path.GetFileName(path)));
                }
                catch (Exception ex) { logger.Log("Import error: " + ex.Message); }
            }
        }

        private void AddLocal()
        {
            AddServerRows(new[] { new KeyValuePair<string, string>(Environment.MachineName, "Local") });
            logger.Log("Added local server: " + Environment.MachineName);
        }

        private async Task DiscoverAsync()
        {
            string filter = config.Str("ServerOperatingSystemFilter");
            int staleDays = config.Int("DiscoveryStaleDays", 0, 3650);
            logger.Log("Discovering domain servers" + (staleDays > 0 ? " (skipping disabled, cluster-name and accounts with no logon for " + staleDays + " days)..." : "..."));
            btnDiscover.Enabled = false;
            try
            {
                var found = await Task.Run(() => Discovery.DomainServers(filter, staleDays));
                var skipped = found.Where(o => o.Skipped).ToList();
                discoveryLog.Clear();
                discoveryLog.Add(string.Format("Discover Domain at {0:yyyy-MM-dd HH:mm} (filter '{1}', DiscoveryStaleDays {2}): {3} computer object(s) found, {4} skipped.", DateTime.Now, filter, staleDays, found.Count, skipped.Count));
                foreach (var sk in skipped)
                {
                    string line = "  Skipped " + sk.ServerName + ": " + sk.SkipReason;
                    logger.Log(line);
                    discoveryLog.Add(line);
                }
                int n = AddServerRows(found.Where(o => !o.Skipped).Select(o => o.ServerName), "Active Directory");
                logger.Log(string.Format("Added {0} server(s) from Active Directory; skipped {1} (listed above). Set DiscoveryStaleDays to 0 in the settings file to include stale accounts.", n, skipped.Count));
            }
            catch (Exception ex) { logger.Log("Domain discovery failed: " + ex.Message); }
            finally { UpdateButtons(); }
        }

        private void RemoveSelected()
        {
            var rows = grid.SelectedRows.Cast<DataGridViewRow>().ToList();
            if (rows.Count == 0) { logger.Log("No rows selected to remove."); return; }
            foreach (var r in rows) grid.Rows.Remove(r);
            UpdateCount();
            logger.Log(string.Format("Removed {0} server(s).", rows.Count));
        }

        // ------------------------------------------------------------------ reachability

        private async Task TestReachabilityAsync()
        {
            var rows = IncludedRows();
            if (rows.Count == 0) { logger.Log("No included servers to test."); return; }
            Credential cred = CurrentCredential();
            var rs = RunSettings.From(config);
            rs.Credential = cred;
            int parallel = (int)numParallel.Value;
            running = true; UpdateButtons();
            progress.Maximum = rows.Count; progress.Value = 0;
            logger.Log(string.Format("Testing {0} server(s), {1} at a time...", rows.Count, parallel));
            var gate = new SemaphoreSlim(parallel);
            var tasks = rows.Select(async row =>
            {
                string name = Convert.ToString(row.Cells["Server"].Value).Trim();
                await gate.WaitAsync();
                try
                {
                    Ui(() => { row.Cells["Reachability"].Value = "Testing..."; SetRowColour(row, ColRunning); });
                    Target target = rs.TargetFor(name);
                    Dictionary<string, object> r = await Task.Run(() => ServerCollector.Probe(target));
                    string cls = N.S(r, "Classification");
                    string transport = N.S(r, "Transport");
                    bool reachable = N.IsTrue(N.Get(r, "Reachable"));
                    string detail = N.S(r, "Detail");
                    Ui(() =>
                    {
                        row.Cells["Reachability"].Value = cls; row.Cells["Transport"].Value = transport;
                        row.Cells["Result"].Value = reachable ? "Ready (" + transport + ")" : cls;
                        SetRowColour(row, reachable ? ColComplete : ColFailed);
                    });
                    if (!reachable && detail.Length > 0) logger.Log("  " + name + ": " + detail);
                }
                catch (Exception ex)
                {
                    logger.Log("  " + name + ": readiness test error - " + ex.Message);
                    Ui(() => { row.Cells["Reachability"].Value = "Test error"; SetRowColour(row, ColFailed); });
                }
                finally
                {
                    gate.Release();
                    Ui(() => { if (progress.Value < progress.Maximum) progress.Value++; });
                }
            }).ToList();
            await Task.WhenAll(tasks);
            logger.Log("Readiness probe complete.");
            progress.Value = 0;
            running = false; UpdateButtons();
        }

        // ------------------------------------------------------------------ assessment

        private async Task StartAssessmentAsync()
        {
            var rows = IncludedRows();
            if (rows.Count == 0) { logger.Log("No included servers selected."); return; }
            if (chkCred.Checked && credential == null && !PromptCredential()) return;

            var rowMap = new Dictionary<string, DataGridViewRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows) rowMap[Convert.ToString(r.Cells["Server"].Value).Trim()] = r;
            var names = rowMap.Keys.ToList();

            running = true; UpdateButtons();
            cts = new CancellationTokenSource();
            progress.Maximum = names.Count; progress.Value = 0;
            string outputRoot = txtOutput.Text.Trim();
            string client = txtClient.Text.Trim();
            int done = 0;
            try
            {
                var s = RunSettings.From(config);
                s.Credential = CurrentCredential();
                s.ParallelServers = (int)numParallel.Value;
                await Task.Run(() => RunFiles.CreateRunFolder(s, outputRoot, client));
                lastOutputFolder = s.RunRoot;
                logger.SetFile(Path.Combine(s.RunRoot, RunLogger.FileName));
                logger.Log("Created output folder: " + s.RunRoot);
                logger.Log(string.Format("{0} {1} - run started by {2}\\{3} on {4}", AppInfo.ProductName, AppInfo.VersionText, Environment.UserDomainName, Environment.UserName, Environment.MachineName));
                logger.Log(string.Format("Client: {0} | Credentials: {1} | servers at once: {2} | connect limit {3}s | WMI query limit {4}s | step limit {5}s | server limit {6} min",
                    s.ClientName, s.Credential != null ? s.Credential.FullUser : "current user", s.ParallelServers, s.ConnectTimeoutSec, s.WmiQueryTimeoutSec, s.SectionTimeoutSec, s.ServerTimeLimitMinutes));
                foreach (string line in discoveryLog) logger.Log(line);
                logger.Log(string.Format("Servers ({0}): {1}", names.Count, string.Join(", ", names)));
                foreach (var r in rows) { r.Cells["Status"].Value = "Queued"; r.Cells["Result"].Value = ""; SetRowColour(r, SystemColors.Window); }

                var runner = new Runner();
                runner.ServerProgress += (name, msg) =>
                {
                    logger.Log("    ... " + name + ": " + msg);
                    DataGridViewRow row;
                    if (rowMap.TryGetValue(name, out row)) Ui(() => { row.Cells["Status"].Value = "Running: " + msg; SetRowColour(row, ColRunning); });
                };
                runner.ServerFinished += o =>
                {
                    LogOutcome(o);
                    DataGridViewRow row;
                    if (rowMap.TryGetValue(o.Name, out row))
                    {
                        Ui(() =>
                        {
                            row.Cells["Status"].Value = o.Status; row.Cells["Transport"].Value = o.Transport; row.Cells["Roles"].Value = o.Roles;
                            row.Cells["Result"].Value = ResultText(o);
                            SetRowColour(row, ColourFor(o.Status));
                        });
                    }
                    int n = Interlocked.Increment(ref done);
                    Ui(() => { progress.Value = Math.Min(progress.Maximum, n); });
                };

                List<ServerOutcome> outcomes = await runner.RunAsync(names, s, cts.Token);

                string htmlName = s.ClientName + "_Server_Summary.html";
                try
                {
                    await Task.Run(() => RunFiles.WriteSummary(outcomes, Path.Combine(s.RunRoot, htmlName), s.ClientName));
                    logger.Log("Created HTML summary: " + htmlName);
                }
                catch (Exception ex) { logger.Log("  ERROR: HTML summary could not be generated: " + ex.Message); }

                logger.Log(string.Format("Run summary: {0} Complete, {1} Partial, {2} Failed, {3} Cancelled. Reports: {4} DOCX created, {5} DOCX failed.",
                    outcomes.Count(o => o.Status == "Complete"), outcomes.Count(o => o.Status == "Partial"), outcomes.Count(o => o.Status == "Failed"),
                    outcomes.Count(o => o.Cancelled), outcomes.Count(o => o.DocName != null), outcomes.Count(o => o.ReportError != null)));
                logger.Log((cts.IsCancellationRequested ? "Cancelled. Output for the finished servers and the run log are in: " : "Completed. Output files and run log are in: ") + s.RunRoot);
            }
            catch (Exception ex)
            {
                logger.Log("ERROR: " + ex.Message);
            }
            finally
            {
                logger.SetFile(null);
                running = false;
                progress.Value = 0;
                if (cts != null) { cts.Dispose(); cts = null; }
                UpdateButtons();
            }
        }

        private static string ResultText(ServerOutcome o)
        {
            if (o.Cancelled) return "Cancelled";
            if (o.TimeLimitExceeded) return "Stopped - time limit";
            if (o.ReportError != null) return "DOCX failed - see log";
            if (o.Status == "Complete") return "Success";
            if (o.Status == "Partial") return "Partial - see warnings";
            return o.Classification;
        }

        private void LogOutcome(ServerOutcome o)
        {
            if (o.Cancelled) { logger.Log("  " + o.Name + " -> Cancelled (" + o.Classification + ")"); return; }
            logger.Log("  " + o.Name + " -> " + o.Status + " [" + o.Transport + "] " + o.Classification);
            foreach (string step in o.NonOkSteps) logger.Log("  " + o.Name + " STEP: " + step);
            foreach (string w in o.Warnings) logger.Log("  " + o.Name + " WARN: " + w);
            if (o.JsonError != null) logger.Log("  " + o.Name + " WARN: could not save RawData JSON: " + o.JsonError);
            if (o.DocName != null) logger.Log("  " + o.Name + " Created DOCX: " + o.DocName);
            if (o.ReportError != null) logger.Log("  " + o.Name + " ERROR: DOCX could not be generated: " + o.ReportError);
            if (o.SkippedWorkstation) logger.Log("  " + o.Name + " skipped: workstation (servers only)");
        }

        private void CancelRun()
        {
            if (cts != null && !cts.IsCancellationRequested)
            {
                logger.Log("Cancelling: running servers are being stopped and queued servers skipped...");
                cts.Cancel();
                btnCancel.Enabled = false;
            }
        }

        // ------------------------------------------------------------------ updates

        /// <summary>Install a release copied to this machine (servers without internet): ServerDocumentationTool.exe with its .sha256 file next to it.</summary>
        private void UpdateFromFile()
        {
            if (running) { MessageBox.Show(this, "Wait for the current run to finish before updating.", AppInfo.ProductName); return; }
            using (var dlg = new OpenFileDialog
            {
                Title = "Choose the new " + AppInfo.ExeName + " (its .sha256 file must be in the same folder)",
                Filter = "Server Documentation Tool (" + AppInfo.ExeName + ")|" + AppInfo.ExeName + "|Programs (*.exe)|*.exe"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    Version v;
                    string staged = UpdateService.PrepareLocalFile(dlg.FileName, out v);
                    if (!ConfirmInstall(v, "The SHA256 checksum matched.")) return;
                    logger.Log("Installing v" + v.ToString(3) + " from " + dlg.FileName + " (SHA256 verified)...");
                    UpdateService.InstallAndRestart(staged);
                    Close();
                }
                catch (Exception ex)
                {
                    logger.Log("Update from file failed: " + ex.Message);
                    MessageBox.Show(this, "The update could not be installed:" + Environment.NewLine + ex.Message, AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private bool ConfirmInstall(Version v, string verified)
        {
            string current = AppInfo.VersionText;
            string question;
            if (v > AppInfo.Version) question = "Install v" + v.ToString(3) + " now? (installed: " + current + ")";
            else if (v == AppInfo.Version) question = "v" + v.ToString(3) + " is already installed. Reinstall it?";
            else question = "v" + v.ToString(3) + " is OLDER than the installed " + current + ". Install it anyway?";
            question += Environment.NewLine + Environment.NewLine + verified + " The application will close, replace itself and restart. Your settings (" + config.Path + ") are kept.";
            return MessageBox.Show(this, question, AppInfo.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        private async Task CheckForUpdatesAsync(bool silent)
        {
            string repo = UpdateService.ResolveRepository(config.Str("UpdateRepository"));
            btnUpdate.Enabled = false;
            try
            {
                ReleaseInfo latest = await Task.Run(() => UpdateService.GetLatestRelease(repo));
                if (latest != null && UpdateService.IsNewer(latest))
                {
                    availableUpdate = latest;
                    lnkUpdate.Text = "Update available: " + latest.Tag + " - click to install";
                    lnkUpdate.Visible = true;
                    logger.Log("Update available: " + latest.Tag + " (this is " + AppInfo.VersionText + ").");
                    if (!silent) await InstallUpdateAsync();
                }
                else if (!silent)
                {
                    string msg = latest == null ? "No releases have been published yet." : "You have the latest version (" + AppInfo.VersionText + ").";
                    logger.Log("Update check: " + msg);
                    MessageBox.Show(this, msg, AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                logger.Log("Update check failed: " + ex.Message + (silent ? " (servers without internet access: use Update from File...)" : string.Empty));
                if (!silent)
                {
                    MessageBox.Show(this, "Could not check for updates:" + Environment.NewLine + ex.Message + Environment.NewLine + Environment.NewLine +
                        "On a server without internet access, download " + AppInfo.ExeName + " and " + AppInfo.ExeName + ".sha256 from" + Environment.NewLine +
                        UpdateService.ReleasesPage(repo) + Environment.NewLine + "on another PC, copy both files to this server and use Update from File...",
                        AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally { btnUpdate.Enabled = !running; }
        }

        private async Task InstallUpdateAsync()
        {
            if (availableUpdate == null) return;
            if (running) { MessageBox.Show(this, "Wait for the current run to finish before updating.", AppInfo.ProductName); return; }
            ReleaseInfo rel = availableUpdate;
            Enabled = false;
            try
            {
                logger.Log("Downloading " + rel.Tag + "...");
                Version v = null;
                string staged = await Task.Run(() => UpdateService.DownloadAndVerify(rel, out v));
                Enabled = true;
                if (!ConfirmInstall(v, "The download was verified (SHA256).")) return;
                logger.Log("Installing " + rel.Tag + "...");
                UpdateService.InstallAndRestart(staged);
                Close();
            }
            catch (Exception ex)
            {
                Enabled = true;
                logger.Log("Update failed: " + ex.Message);
                MessageBox.Show(this, "The update could not be installed:" + Environment.NewLine + ex.Message + Environment.NewLine + Environment.NewLine +
                    "You can download it manually from" + Environment.NewLine + rel.PageUrl, AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
