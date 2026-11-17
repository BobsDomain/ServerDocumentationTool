using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IABB.App
{
    internal sealed class MainForm : Form
    {
        private const int EngineRunspaces = 40;

        private static readonly Color ColRunning = Color.FromArgb(220, 235, 250);
        private static readonly Color ColComplete = Color.FromArgb(212, 237, 218);
        private static readonly Color ColPartial = Color.FromArgb(255, 243, 205);
        private static readonly Color ColFailed = Color.FromArgb(248, 215, 218);
        private static readonly Color ColCancelled = Color.FromArgb(231, 230, 230);

        private readonly AppConfig config;
        private readonly RunLogger logger;
        private EngineHost engine;
        private bool engineReady;
        private bool running;
        private CancellationTokenSource cts;
        private PSCredential credential;
        private string lastOutputFolder;
        private ReleaseInfo availableUpdate;

        // controls
        private readonly TextBox txtClient = new TextBox();
        private readonly TextBox txtOutput = new TextBox();
        private readonly Button btnBrowse = new Button();
        private readonly CheckBox chkCred = new CheckBox();
        private readonly Button btnCred = new Button();
        private readonly CheckBox chkDcom = new CheckBox();
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
        private readonly LinkLabel lnkUpdate = new LinkLabel();
        private readonly Label lblCount = new Label();
        private readonly DataGridView grid = new DataGridView();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly TextBox txtLog = new TextBox();

        public MainForm()
        {
            config = AppConfig.Load(AppInfo.EngineDirectory);
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
            txtClient.Location = new Point(120, 55); txtClient.Size = new Size(300, 24); txtClient.Text = "Client";
            chkCred.Text = "Use alternate credentials"; chkCred.Location = new Point(840, 55); chkCred.Size = new Size(190, 24);
            chkCred.CheckedChanged += (s, e) => OnCredentialToggled();
            btnCred.Text = "Change..."; btnCred.Location = new Point(1035, 53); btnCred.Size = new Size(90, 26); btnCred.Enabled = false;
            btnCred.Click += (s, e) => PromptCredential();

            var lblOutput = new Label { Text = "Output Path", Location = new Point(25, 88), Size = new Size(90, 22) };
            txtOutput.Location = new Point(120, 85); txtOutput.Size = new Size(610, 24); txtOutput.Text = config.GetString("DefaultOutputRoot", @"C:\ServerDocumentation");
            btnBrowse.Text = "Browse..."; btnBrowse.Location = new Point(736, 83); btnBrowse.Size = new Size(90, 26);
            btnBrowse.Click += (s, e) => BrowseOutput();
            chkDcom.Text = "Enable DCOM fallback"; chkDcom.Location = new Point(840, 85); chkDcom.Size = new Size(170, 24); chkDcom.Checked = config.GetBool("EnableDcomFallback", true);
            var lblParallel = new Label { Text = "Servers at once", Location = new Point(1015, 88), Size = new Size(100, 22) };
            numParallel.Location = new Point(1118, 85); numParallel.Size = new Size(55, 24); numParallel.Minimum = 1; numParallel.Maximum = 16;
            numParallel.Value = config.GetInt("ParallelServers", 4, 1, 16);

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
            SetButton(btnUpdate, "Check for Updates", 775, 224, 150, 32); btnUpdate.Click += async (s, e) => await CheckForUpdatesAsync(false);
            lblCount.Location = new Point(940, 230); lblCount.Size = new Size(300, 22); lblCount.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

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

            Controls.AddRange(new Control[] { title, lblVersion, lnkUpdate, lblClient, txtClient, chkCred, btnCred, lblOutput, txtOutput, btnBrowse, chkDcom, lblParallel, numParallel, grp,
                btnTest, btnRemove, btnStart, btnCancel, btnOpen, btnUpdate, lblCount, grid, progress, txtLog });
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
            logger.Log(AppInfo.ProductName + " " + AppInfo.VersionText + " - loading the engine from " + AppInfo.EngineDirectory);
            try
            {
                engine = await Task.Run(() => new EngineHost(AppInfo.EngineDirectory, EngineRunspaces));
                IList<string> missing = await Task.Run(() => engine.FindMissingCommands());
                if (missing.Count > 0) throw new InvalidOperationException("these engine functions did not load: " + string.Join(", ", missing));
                engineReady = true;
                logger.Log("Engine loaded (Windows PowerShell " + PSVersionText() + "). Add target servers by name, import a list, add the local server, or discover the domain.");
            }
            catch (Exception ex)
            {
                logger.Log("ERROR: the engine could not be loaded: " + ex.Message);
                MessageBox.Show(this, "The PowerShell engine could not be loaded:" + Environment.NewLine + Environment.NewLine + ex.Message +
                    Environment.NewLine + Environment.NewLine + "Check that the Engine folder is next to the application and that Windows PowerShell 5.1 is installed.",
                    AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            UpdateButtons();
            if (config.GetBool("CheckForUpdatesOnStart", true)) await CheckForUpdatesAsync(true);
        }

        private string PSVersionText()
        {
            try
            {
                using (var ps = engine.Create())
                {
                    ps.AddScript("$PSVersionTable.PSVersion.ToString()");
                    var r = ps.Invoke();
                    return r.Count > 0 ? r[0].ToString() : "?";
                }
            }
            catch { return "?"; }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (running)
            {
                var answer = MessageBox.Show(this, "An assessment is still running. Cancel it and close?", AppInfo.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) { e.Cancel = true; return; }
                CancelRun();
            }
            if (engine != null) { var en = engine; engine = null; Task.Run(() => en.Dispose()); }
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
            bool idle = engineReady && !running;
            foreach (var b in new Control[] { btnAddServers, btnImport, btnAddLocal, btnDiscover, btnClear, btnTest, btnRemove, btnStart })
                b.Enabled = idle;
            txtClient.Enabled = !running; txtOutput.Enabled = !running; btnBrowse.Enabled = !running;
            chkCred.Enabled = !running; btnCred.Enabled = !running && chkCred.Checked; chkDcom.Enabled = !running; numParallel.Enabled = !running;
            btnCancel.Enabled = running;
            btnUpdate.Enabled = !running;
            btnOpen.Enabled = !string.IsNullOrEmpty(lastOutputFolder) && Directory.Exists(lastOutputFolder);
        }

        private void UpdateCount()
        {
            lblCount.Text = string.Format("{0} server(s) queued   |   step limit {1}s, server limit {2} min",
                grid.Rows.Count, config.GetInt("SectionTimeoutSec", 300, 30, 3600), config.GetInt("ServerTimeLimitMinutes", 30, 1, 600));
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

        private static List<KeyValuePair<string, string>> ToServerList(IEnumerable<PSObject> objects)
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (PSObject o in EngineHost.NonNull(objects))
                list.Add(new KeyValuePair<string, string>(EngineHost.Str(o, "ServerName"), EngineHost.Str(o, "Source")));
            return list;
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
            using (var dlg = new CredentialDialog(credential != null ? credential.UserName : null))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    credential = dlg.GetCredential();
                    logger.Log("Alternate credentials set for " + credential.UserName + ".");
                    return true;
                }
            }
            return false;
        }

        private PSCredential CurrentCredential()
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

        private void OpenOutputFolder()
        {
            if (!string.IsNullOrEmpty(lastOutputFolder) && Directory.Exists(lastOutputFolder))
                Process.Start("explorer.exe", "\"" + lastOutputFolder + "\"");
        }

        // ------------------------------------------------------------------ server list

        private async Task AddFromTextAsync()
        {
            string text = txtServers.Text;
            try
            {
                var found = await Task.Run(() => engine.Run("ConvertFrom-IABBServerText", new Dictionary<string, object> { { "Text", text } }));
                var list = ToServerList(found);
                if (list.Count == 0) { logger.Log("No valid server names entered."); return; }
                int n = AddServerRows(list);
                logger.Log(string.Format("Added {0} server(s) from manual entry.", n));
                txtServers.Clear();
            }
            catch (Exception ex) { logger.Log("Add error: " + ex.Message); }
        }

        private async Task ImportAsync()
        {
            using (var dlg = new OpenFileDialog { Filter = "Server lists (*.txt;*.csv)|*.txt;*.csv|All files (*.*)|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string path = dlg.FileName;
                try
                {
                    var found = await Task.Run(() => engine.Run("Import-IABBServerList", new Dictionary<string, object> { { "Path", path } }));
                    int n = AddServerRows(ToServerList(found));
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
            string filter = config.GetString("ServerOperatingSystemFilter", "Windows Server");
            logger.Log("Discovering domain servers...");
            btnDiscover.Enabled = false;
            try
            {
                var found = await Task.Run(() => engine.Run("Get-IABBDomainServers", new Dictionary<string, object> { { "OperatingSystemFilter", filter } }));
                int n = AddServerRows(ToServerList(found));
                logger.Log(string.Format("Added {0} server(s) from Active Directory.", n));
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
            PSCredential cred = CurrentCredential();
            bool dcom = chkDcom.Checked;
            int timeout = Math.Max(30, config.GetInt("TimeoutSeconds", 30, 5, 600));
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
                    var p = new Dictionary<string, object> { { "ComputerName", name }, { "EnableDcomFallback", dcom }, { "OperationTimeoutSec", timeout } };
                    if (cred != null) p["Credential"] = cred;
                    PSObject r = (await Task.Run(() => engine.Run("Test-IABBCollectionReadiness", p))).FirstOrDefault(x => x != null);
                    string cls = EngineHost.Str(r, "Classification");
                    string transport = EngineHost.Str(r, "Transport");
                    bool reachable = EngineHost.Bool(r, "Reachable");
                    string detail = EngineHost.Str(r, "Detail");
                    string wsErr = EngineHost.Str(r, "WsmanError");
                    Ui(() =>
                    {
                        row.Cells["Reachability"].Value = cls; row.Cells["Transport"].Value = transport;
                        row.Cells["Result"].Value = reachable ? "Ready (" + transport + ")" : cls;
                        SetRowColour(row, reachable ? (transport == "DCOM" ? ColPartial : ColComplete) : ColFailed);
                    });
                    if (!reachable && detail.Length > 0) logger.Log("  " + name + ": " + detail);
                    if (transport == "DCOM" && wsErr.Length > 0) logger.Log("  " + name + ": WinRM failed, DCOM only - " + wsErr);
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
                PSObject folder = (await Task.Run(() => engine.Run("New-IABBRunFolder", new Dictionary<string, object> { { "OutputRoot", outputRoot }, { "ClientName", client } }))).FirstOrDefault(x => x != null);
                if (folder == null) throw new InvalidOperationException("the output folder could not be created.");
                var s = new RunSettings
                {
                    ClientName = EngineHost.Str(folder, "ClientName"),
                    RunRoot = EngineHost.Str(folder, "Root"),
                    RawDataPath = EngineHost.Str(folder, "RawData"),
                    Credential = CurrentCredential(),
                    EnableDcomFallback = chkDcom.Checked,
                    ParallelServers = (int)numParallel.Value,
                    ServerTimeLimitMinutes = config.GetInt("ServerTimeLimitMinutes", 30, 1, 600),
                    TimeoutSeconds = config.GetInt("TimeoutSeconds", 30, 5, 600),
                    CimOperationTimeoutSec = config.GetInt("CimOperationTimeoutSec", 180, 10, 3600),
                    SectionTimeoutSec = config.GetInt("SectionTimeoutSec", 300, 30, 3600),
                    GroupMemberDisplayCap = config.GetInt("GroupMemberDisplayCap", 15, 1, 100000),
                    ApplicationDisplayCap = config.GetInt("ApplicationDisplayCap", 200, 1, 100000),
                    IncludeWorkstations = config.GetBool("IncludeWorkstations", false)
                };
                lastOutputFolder = s.RunRoot;
                logger.SetFile(Path.Combine(s.RunRoot, "IABB_Run.log"));
                logger.Log("Created output folder: " + s.RunRoot);
                logger.Log(string.Format("{0} {1} - run started by {2}\\{3} on {4}", AppInfo.ProductName, AppInfo.VersionText, Environment.UserDomainName, Environment.UserName, Environment.MachineName));
                logger.Log(string.Format("Client: {0} | Credentials: {1} | DCOM fallback: {2} | servers at once: {3} | CIM query limit {4}s | step limit {5}s | server limit {6} min",
                    s.ClientName, s.Credential != null ? s.Credential.UserName : "current user", s.EnableDcomFallback, s.ParallelServers, s.CimOperationTimeoutSec, s.SectionTimeoutSec, s.ServerTimeLimitMinutes));
                logger.Log(string.Format("Servers ({0}): {1}", names.Count, string.Join(", ", names)));
                foreach (var r in rows) { r.Cells["Status"].Value = "Queued"; r.Cells["Result"].Value = ""; SetRowColour(r, SystemColors.Window); }

                var runner = new AssessmentRunner(engine);
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

                var forSummary = outcomes.Where(o => o.Result != null && !o.SkippedWorkstation).Select(o => o.Result).ToArray();
                var cancelled = outcomes.Where(o => o.Cancelled).Select(o => o.Name).ToArray();
                string htmlName = s.ClientName + "_Server_Summary.html";
                try
                {
                    await Task.Run(() => engine.Run("New-IABBAppSummary", new Dictionary<string, object>
                    {
                        { "Results", forSummary }, { "CancelledServers", cancelled }, { "OutputPath", Path.Combine(s.RunRoot, htmlName) }, { "ClientName", s.ClientName }
                    }));
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

        private async Task CheckForUpdatesAsync(bool silent)
        {
            string repo = config.GetString("UpdateRepository", UpdateService.DefaultRepository);
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
                else
                {
                    if (!silent)
                    {
                        string msg = latest == null ? "No releases have been published yet." : "You have the latest version (" + AppInfo.VersionText + ").";
                        logger.Log("Update check: " + msg);
                        MessageBox.Show(this, msg, AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Log("Update check failed: " + ex.Message);
                if (!silent)
                {
                    MessageBox.Show(this, "Could not check for updates:" + Environment.NewLine + ex.Message + Environment.NewLine + Environment.NewLine +
                        "You can download releases manually from" + Environment.NewLine + UpdateService.ReleasesPage(repo), AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally { btnUpdate.Enabled = !running; }
        }

        private async Task InstallUpdateAsync()
        {
            if (availableUpdate == null) return;
            if (running) { MessageBox.Show(this, "Wait for the current run to finish before updating.", AppInfo.ProductName); return; }
            var answer = MessageBox.Show(this, "Install " + availableUpdate.Tag + " now?" + Environment.NewLine + Environment.NewLine +
                "The application will close, update itself and restart. Your settings (Engine\\Config\\appsettings.json) are kept.",
                AppInfo.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
            ReleaseInfo rel = availableUpdate;
            Enabled = false;
            try
            {
                logger.Log("Downloading " + rel.ZipName + "...");
                string zip = await Task.Run(() => UpdateService.DownloadAndVerify(rel));
                logger.Log("Download verified (SHA256). Starting the updater...");
                UpdateService.LaunchUpdater(zip, AppInfo.AppDirectory, Application.ExecutablePath);
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
