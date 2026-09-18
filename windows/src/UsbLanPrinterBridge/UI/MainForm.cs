using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using UsbLanPrinterBridge.Core;

namespace UsbLanPrinterBridge.UI
{
    public sealed class MainForm : Form
    {
        private const string AutoAdapter = "(Auto)";
        private const int MaxLogLines = 600;
        private const string RunningEditHint = "Stop this bridge before changing it, then start it again.";

        private readonly StartupOptions _options;
        private readonly BridgeManager _manager = new BridgeManager();
        private BridgeConfig _config = new BridgeConfig();
        private List<PrinterInfo> _printers = new List<PrinterInfo>();
        private List<AdapterInfo> _adapters = new List<AdapterInfo>();
        private readonly HashSet<string> _wanted = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _logLines = new List<string>();

        private bool _busy;
        private bool _reallyExit;
        private bool _trayHintShown;
        private bool _loadingGrid;
        private DateTime _lastRetry = DateTime.MinValue;
        private DateTime _lastAddressCheck = DateTime.MinValue;
        private bool _repairingAddresses;
        private readonly ProbeListener _probe = new ProbeListener();
        private ToolStripMenuItem _miTrace;
        private ToolStripMenuItem _miSaveRaw;
        private PrintLogForm _printLog;

        // controls
        private MenuStrip _menu;
        private ToolStripMenuItem _miStartWithWindows;
        private Label _lblHost;
        private DataGridView _grid;
        private Button _btnAdd, _btnRemove, _btnStartAll, _btnStopAll, _btnStartSel, _btnStopSel, _btnTest;
        private NumericUpDown _numIdle;
        private CheckBox _chkFirewall, _chkAutoStart, _chkTray;
        private TextBox _log;
        private StatusStrip _status;
        private ToolStripStatusLabel _lblStatus;
        private NotifyIcon _tray;
        private Timer _timer;

        private DataGridViewCheckBoxColumn _colOn;
        private DataGridViewComboBoxColumn _colPrinter;
        private DataGridViewTextBoxColumn _colIp;
        private DataGridViewTextBoxColumn _colPort;
        private DataGridViewComboBoxColumn _colAdapter;
        private DataGridViewCheckBoxColumn _colEsc;
        private DataGridViewCheckBoxColumn _colEpos;
        private DataGridViewTextBoxColumn _colState;
        private DataGridViewTextBoxColumn _colJobs;
        private DataGridViewTextBoxColumn _colBytes;

        public MainForm(StartupOptions options)
        {
            _options = options ?? new StartupOptions();
            BuildUi();
            Logger.EntryAdded += OnLogEntry;
            _manager.ListenerChanged += OnListenerChanged;
        }

        // =====================================================================================  UI construction

        private void BuildUi()
        {
            Text = Program.AppName;
            Icon = Program.LoadIcon(32);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(820, 560);
            ClientSize = new Size(960, 640);

            BuildMenu();

            _lblHost = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Height = 34,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 4, 0),
                ForeColor = SystemColors.GrayText
            };

            BuildGrid();

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 2, 0, 2) };
            _btnAdd = MakeButton("+ Add mapping", (s, e) => AddMapping());
            _btnRemove = MakeButton("− Remove", (s, e) => RemoveSelected());
            _btnStartAll = MakeButton("▶ Start all", (s, e) => StartAll());
            _btnStartAll.Font = new Font(Font, FontStyle.Bold);
            _btnStopAll = MakeButton("■ Stop all", (s, e) => StopAll());
            _btnStartSel = MakeButton("Start selected", (s, e) => StartSelected());
            _btnStopSel = MakeButton("Stop selected", (s, e) => StopSelected());
            _btnTest = MakeButton("Test print…", (s, e) => ShowTestMenu());
            buttons.Controls.AddRange(new Control[] { _btnAdd, _btnRemove, Spacer(), _btnStartAll, _btnStopAll, _btnStartSel, _btnStopSel, Spacer(), _btnTest });

            var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 2, 0, 2) };
            settings.Controls.Add(new Label { Text = "Job idle timeout (ms):", AutoSize = true, Margin = new Padding(3, 8, 0, 0) });
            _numIdle = new NumericUpDown { Minimum = 0, Maximum = 600000, Increment = 250, Value = 1500, Width = 80, Margin = new Padding(3, 4, 12, 0) };
            _numIdle.ValueChanged += (s, e) => { _config.JobIdleTimeoutMs = (int)_numIdle.Value; ApplyIdleTimeout(); };
            settings.Controls.Add(_numIdle);
            _chkFirewall = MakeCheck("Add Windows Firewall rule automatically", (s, e) => _config.AutoFirewallRule = _chkFirewall.Checked);
            _chkAutoStart = MakeCheck("Start bridges when the app opens", (s, e) => _config.AutoStartBridges = _chkAutoStart.Checked);
            _chkTray = MakeCheck("Close button hides to tray", (s, e) => _config.CloseToTray = _chkTray.Checked);
            settings.Controls.AddRange(new Control[] { _chkFirewall, _chkAutoStart, _chkTray });
            new ToolTip().SetToolTip(_numIdle, "A print job is sent to the printer when the client disconnects, or after this much silence on an open connection.\n0 = only when the client disconnects.");

            _log = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = false,
                BackColor = Color.FromArgb(250, 250, 250),
                Font = new Font(FontFamily.GenericMonospace, 8.5f),
                HideSelection = false
            };

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8, 4, 8, 4) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            layout.Controls.Add(_lblHost, 0, 0);
            layout.Controls.Add(_grid, 0, 1);
            layout.Controls.Add(buttons, 0, 2);
            layout.Controls.Add(settings, 0, 3);
            layout.Controls.Add(_log, 0, 4);

            _status = new StatusStrip { SizingGrip = true };
            _lblStatus = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _status.Items.Add(_lblStatus);

            Controls.Add(layout);
            Controls.Add(_status);
            Controls.Add(_menu);
            MainMenuStrip = _menu;

            BuildTray();

            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) => OnTimerTick();

            Load += (s, e) => OnFormLoad();
            FormClosing += OnFormClosingHandler;
            Resize += (s, e) => { if (WindowState == FormWindowState.Minimized && _config.CloseToTray) HideToTray(); };
        }

        private void BuildMenu()
        {
            _menu = new MenuStrip();

            var file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add(new ToolStripMenuItem("&Save configuration", null, (s, e) => SaveConfig(true)) { ShortcutKeys = Keys.Control | Keys.S });
            file.DropDownItems.Add(new ToolStripMenuItem("Open &data folder (config + logs)", null, (s, e) => OpenFolder(ConfigStore.DataDirectory)));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(new ToolStripMenuItem("Hide to &tray", null, (s, e) => HideToTray()));
            file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (s, e) => ExitApplication()));

            var bridges = new ToolStripMenuItem("&Bridges");
            bridges.DropDownItems.Add(new ToolStripMenuItem("Start &all", null, (s, e) => StartAll()) { ShortcutKeys = Keys.F5 });
            bridges.DropDownItems.Add(new ToolStripMenuItem("St&op all", null, (s, e) => StopAll()) { ShortcutKeys = Keys.F6 });
            bridges.DropDownItems.Add(new ToolStripSeparator());
            bridges.DropDownItems.Add(new ToolStripMenuItem("&Refresh printers and adapters", null, (s, e) => RefreshDevices()) { ShortcutKeys = Keys.F4 });

            var tools = new ToolStripMenuItem("&Tools");
            tools.DropDownItems.Add(new ToolStripMenuItem("Test print — &direct to printer", null, (s, e) => TestPrint(false)));
            tools.DropDownItems.Add(new ToolStripMenuItem("Test print — &through the bridge (TCP)", null, (s, e) => TestPrint(true)));
            tools.DropDownItems.Add(new ToolStripSeparator());
            tools.DropDownItems.Add(new ToolStripMenuItem("Add Windows &Firewall rule now", null, (s, e) => FirewallRule(true)));
            tools.DropDownItems.Add(new ToolStripMenuItem("Remove Windows Firewall rule", null, (s, e) => FirewallRule(false)));
            tools.DropDownItems.Add(new ToolStripSeparator());
            tools.DropDownItems.Add(new ToolStripMenuItem("Show ePOS-Print &endpoints for selected…", null, (s, e) => ShowEposEndpoints()));
            tools.DropDownItems.Add(new ToolStripMenuItem("Export ePOS HTTPS &certificate (for clients)…", null, (s, e) => ExportCertificate()));
            tools.DropDownItems.Add(new ToolStripSeparator());
            tools.DropDownItems.Add(new ToolStripMenuItem("Network repair: re-enable &DHCP on an adapter…", null, (s, e) => RepairDhcp()));
            tools.DropDownItems.Add(new ToolStripSeparator());
            _miStartWithWindows = new ToolStripMenuItem("Start with &Windows (as administrator)", null, (s, e) => ToggleStartWithWindows()) { CheckOnClick = false };
            tools.DropDownItems.Add(_miStartWithWindows);

            var log = new ToolStripMenuItem("&Log");
            log.DropDownItems.Add(new ToolStripMenuItem("&Print log: what printed and when…", null, (s, e) => ShowPrintLog()) { ShortcutKeys = Keys.Control | Keys.L });
            log.DropDownItems.Add(new ToolStripSeparator());
            _miTrace = new ToolStripMenuItem("&Trace connection attempts (diagnose \"printer not found\")", null, (s, e) => ToggleTracing());
            log.DropDownItems.Add(_miTrace);
            _miSaveRaw = new ToolStripMenuItem("Save &raw bytes of every job", null, (s, e) => ToggleSaveRaw());
            log.DropDownItems.Add(_miSaveRaw);
            log.DropDownItems.Add(new ToolStripSeparator());
            log.DropDownItems.Add(new ToolStripMenuItem("Open log &folder", null, (s, e) => OpenFolder(ConfigStore.LogDirectory)));

            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add(new ToolStripMenuItem("&How to connect devices to the bridge", null, (s, e) => ShowHowTo()));
            help.DropDownItems.Add(new ToolStripMenuItem("&About", null, (s, e) => ShowAbout()));

            _menu.Items.AddRange(new ToolStripItem[] { file, bridges, log, tools, help });
        }

        private void BuildGrid()
        {
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                RowHeadersVisible = false,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
                StandardTab = true
            };
            _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 249, 252);
            _grid.RowTemplate.Height = 26;

            _colOn = new DataGridViewCheckBoxColumn { HeaderText = "On", FillWeight = 22, MinimumWidth = 34, ToolTipText = "Include this mapping in Start all" };
            _colPrinter = new DataGridViewComboBoxColumn { HeaderText = "Printer (this PC)", FillWeight = 150, MinimumWidth = 160, DisplayMember = "DisplayText", ValueMember = "Name", FlatStyle = FlatStyle.Flat };
            _colIp = new DataGridViewTextBoxColumn { HeaderText = "LAN address (IP)", FillWeight = 80, MinimumWidth = 105 };
            _colPort = new DataGridViewTextBoxColumn { HeaderText = "Port", FillWeight = 34, MinimumWidth = 46 };
            _colAdapter = new DataGridViewComboBoxColumn { HeaderText = "Adapter", FillWeight = 70, MinimumWidth = 90, FlatStyle = FlatStyle.Flat };
            _colEsc = new DataGridViewCheckBoxColumn { HeaderText = "Status replies", FillWeight = 40, MinimumWidth = 64, ToolTipText = "Answer DLE EOT status requests with 'printer OK' so POS apps don't wait for a reply the USB printer cannot send (raw/9100 only)" };
            _colEpos = new DataGridViewCheckBoxColumn { HeaderText = "ePOS web", FillWeight = 40, MinimumWidth = 64, ToolTipText = "Also serve the Epson ePOS-Print endpoints (HTTP 80 + 8008, HTTPS 443 + 8043) so browser/Android POS apps can print to this printer" };
            _colState = new DataGridViewTextBoxColumn { HeaderText = "Status", FillWeight = 110, MinimumWidth = 120, ReadOnly = true };
            _colJobs = new DataGridViewTextBoxColumn { HeaderText = "Jobs", FillWeight = 32, MinimumWidth = 42, ReadOnly = true };
            _colBytes = new DataGridViewTextBoxColumn { HeaderText = "Data", FillWeight = 42, MinimumWidth = 56, ReadOnly = true };
            foreach (DataGridViewColumn c in new DataGridViewColumn[] { _colState, _colJobs, _colBytes })
            {
                c.DefaultCellStyle.BackColor = Color.FromArgb(240, 240, 240);
                c.DefaultCellStyle.ForeColor = SystemColors.GrayText;
            }
            _colPort.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            _colJobs.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            _colBytes.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;

            _grid.Columns.AddRange(new DataGridViewColumn[] { _colOn, _colPrinter, _colIp, _colPort, _colAdapter, _colEsc, _colEpos, _colState, _colJobs, _colBytes });

            _grid.DataError += (s, e) => { e.ThrowException = false; };
            _grid.CurrentCellDirtyStateChanged += (s, e) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _grid.CellValueChanged += (s, e) => { if (!_loadingGrid && e.RowIndex >= 0) SyncRowToMapping(_grid.Rows[e.RowIndex], e.ColumnIndex); };
            _grid.CellValidating += OnCellValidating;
            _grid.SelectionChanged += (s, e) => UpdateButtons();
            _grid.CellBeginEdit += (s, e) =>
            {
                MappingConfig m = MappingOf(_grid.Rows[e.RowIndex]);
                if (m != null && _manager.IsRunning(m) && e.ColumnIndex != _colOn.Index)
                {
                    e.Cancel = true;
                    // Cancelling silently just looks broken: the cell refuses to open and nothing says why.
                    _grid.Rows[e.RowIndex].ErrorText = RunningEditHint;
                }
            };
        }

        private void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Show window", null, (s, e) => ShowFromTray()) { Font = new Font(Font, FontStyle.Bold) });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Start all bridges", null, (s, e) => StartAll()));
            menu.Items.Add(new ToolStripMenuItem("Stop all bridges", null, (s, e) => StopAll()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => ExitApplication()));

            _tray = new NotifyIcon
            {
                Icon = Program.LoadIcon(16),
                Text = Program.AppName,
                ContextMenuStrip = menu,
                Visible = true
            };
            _tray.DoubleClick += (s, e) => ShowFromTray();
        }

        private Button MakeButton(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 2, 8, 2), MinimumSize = new Size(0, 28), Margin = new Padding(3) };
            b.Click += onClick;
            return b;
        }

        private CheckBox MakeCheck(string text, EventHandler onChanged)
        {
            var c = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(3, 6, 12, 0) };
            c.CheckedChanged += onChanged;
            return c;
        }

        private static Control Spacer() { return new Label { Width = 14, AutoSize = false, Text = "" }; }

        // =====================================================================================  lifecycle

        private void OnFormLoad()
        {
            try
            {
                _config = ConfigStore.Load();
            }
            catch (Exception ex)
            {
                Logger.Error("Could not read " + ConfigStore.ConfigPath + " (using defaults)", ex);
                _config = new BridgeConfig();
            }

            _numIdle.Value = Math.Max(_numIdle.Minimum, Math.Min(_numIdle.Maximum, _config.SanitizedIdleTimeout));
            _chkFirewall.Checked = _config.AutoFirewallRule;
            _chkAutoStart.Checked = _config.AutoStartBridges;
            _chkTray.Checked = _config.CloseToTray;
            ApplyIdleTimeout();
            _manager.AutoFirewallRule = _config.AutoFirewallRule;

            Logger.Info("Configuration: " + ConfigStore.ConfigPath);
            if (!_manager.IsElevated)
                Logger.Warn("Not running as administrator. Bridges can use this PC's existing addresses (or 0.0.0.0) but cannot add virtual addresses or firewall rules.");

            RefreshDevices();
            UpdateStartWithWindowsCheck();
            _timer.Start();

            if (_options.Minimized)
            {
                BeginInvoke(new Action(HideToTray));
            }

            if (_options.AutoStart || _config.AutoStartBridges)
            {
                BeginInvoke(new Action(() => StartAll()));
            }
        }

        private void OnFormClosingHandler(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !_reallyExit && _config.CloseToTray)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            ShutdownEverything();
        }

        private void ShutdownEverything()
        {
            _timer.Stop();
            Logger.EntryAdded -= OnLogEntry;
            _manager.ListenerChanged -= OnListenerChanged;
            SyncConfigFromGrid();
            SaveConfig(false);
            try { _probe.Stop(); } catch { }
            try { _manager.StopAll(); } catch (Exception ex) { Logger.Error("Error while stopping", ex); }
            _tray.Visible = false;
            _tray.Dispose();
            Logger.Info("Exited.");
        }

        private void ExitApplication()
        {
            if (_manager.RunningCount > 0)
            {
                DialogResult r = MessageBox.Show(this,
                    "Bridges are still running. Exit and stop them?\n\nDevices on the network will no longer be able to print through this PC.",
                    Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
            }
            _reallyExit = true;
            Close();
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                try { _tray.ShowBalloonTip(3000, Program.AppName, "Still running here. Double-click to open, right-click to exit.", ToolTipIcon.Info); } catch { }
            }
        }

        private void ShowFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        // =====================================================================================  devices

        private void RefreshDevices()
        {
            SyncConfigFromGrid();
            _printers = PrinterEnumerator.GetPrinters();
            _adapters = NetworkHelper.GetAdapters();

            // Keep printers that are configured but currently not installed selectable, so the row is not blanked out.
            foreach (MappingConfig m in _config.Mappings)
            {
                if (!string.IsNullOrEmpty(m.PrinterName) && !_printers.Any(p => p.Name == m.PrinterName))
                    _printers.Add(new PrinterInfo { Name = m.PrinterName, PortName = "not installed" });
            }

            _colPrinter.DataSource = null;
            _colPrinter.DataSource = _printers.ToList();
            _colPrinter.DisplayMember = "DisplayText";
            _colPrinter.ValueMember = "Name";

            _colAdapter.Items.Clear();
            _colAdapter.Items.Add(AutoAdapter);
            foreach (AdapterInfo a in _adapters) _colAdapter.Items.Add(a.Name);
            foreach (MappingConfig m in _config.Mappings)
                if (!string.IsNullOrEmpty(m.Adapter) && !_colAdapter.Items.Contains(m.Adapter)) _colAdapter.Items.Add(m.Adapter);

            var host = new StringBuilder();
            host.Append(_manager.IsElevated ? "Running as administrator.   " : "NOT running as administrator (virtual IPs unavailable).   ");
            if (_adapters.Count == 0) host.Append("No connected network adapter found.");
            else
            {
                host.Append("This PC: ");
                host.Append(string.Join("   ", _adapters.Select(a => a.Name + " " + string.Join(", ", a.Addresses.Select(x => x.ToString()).ToArray())).ToArray()));
            }
            _lblHost.Text = host.ToString();

            LoadGrid();
            Logger.Info("Found " + _printers.Count(p => p.PortName != "not installed") + " printer(s), " + _printers.Count(p => p.IsUsb) + " on USB; " + _adapters.Count + " network adapter(s).");
        }

        // =====================================================================================  grid <-> config

        private static MappingConfig MappingOf(DataGridViewRow row)
        {
            return row == null ? null : row.Tag as MappingConfig;
        }

        private void LoadGrid()
        {
            _loadingGrid = true;
            try
            {
                _grid.Rows.Clear();
                foreach (MappingConfig m in _config.Mappings)
                {
                    int idx = _grid.Rows.Add();
                    DataGridViewRow row = _grid.Rows[idx];
                    row.Tag = m;
                    FillRow(row, m);
                }
            }
            finally
            {
                _loadingGrid = false;
            }
            UpdateButtons();
        }

        private void FillRow(DataGridViewRow row, MappingConfig m)
        {
            row.Cells[_colOn.Index].Value = m.Enabled;
            row.Cells[_colPrinter.Index].Value = string.IsNullOrEmpty(m.PrinterName) ? null : m.PrinterName;
            row.Cells[_colIp.Index].Value = m.BindAddress ?? "";
            row.Cells[_colPort.Index].Value = m.Port.ToString();
            row.Cells[_colAdapter.Index].Value = string.IsNullOrEmpty(m.Adapter) ? AutoAdapter : m.Adapter;
            row.Cells[_colEsc.Index].Value = m.EscPosStatusReplies;
            row.Cells[_colEpos.Index].Value = m.EposEnabled;
            UpdateRowStatus(row);
        }

        private void SyncRowToMapping(DataGridViewRow row, int columnIndex)
        {
            MappingConfig m = MappingOf(row);
            if (m == null) return;
            object v = row.Cells[columnIndex].Value;

            if (columnIndex == _colOn.Index) m.Enabled = v is bool && (bool)v;
            else if (columnIndex == _colPrinter.Index) m.PrinterName = v as string;
            else if (columnIndex == _colIp.Index) m.BindAddress = (v as string ?? "").Trim();
            else if (columnIndex == _colPort.Index)
            {
                int port;
                if (int.TryParse(Convert.ToString(v), out port) && port >= 1 && port <= 65535) m.Port = port;
            }
            else if (columnIndex == _colAdapter.Index)
            {
                string a = v as string;
                m.Adapter = string.IsNullOrEmpty(a) || a == AutoAdapter ? "" : a;
            }
            else if (columnIndex == _colEsc.Index) m.EscPosStatusReplies = v is bool && (bool)v;
            else if (columnIndex == _colEpos.Index) m.EposEnabled = v is bool && (bool)v;

            if (columnIndex == _colOn.Index)
            {
                if (m.Enabled) _wanted.Add(m.Id); else _wanted.Remove(m.Id);
            }
        }

        private void SyncConfigFromGrid()
        {
            var ordered = new List<MappingConfig>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                MappingConfig m = MappingOf(row);
                if (m != null) ordered.Add(m);
            }
            if (ordered.Count > 0 || _grid.Rows.Count == 0 && _config.Mappings.Count == 0)
                _config.Mappings = ordered;
            _config.JobIdleTimeoutMs = (int)_numIdle.Value;
            _config.AutoFirewallRule = _chkFirewall.Checked;
            _config.AutoStartBridges = _chkAutoStart.Checked;
            _config.CloseToTray = _chkTray.Checked;
        }

        private void OnCellValidating(object sender, DataGridViewCellValidatingEventArgs e)
        {
            if (_loadingGrid || e.RowIndex < 0) return;
            DataGridViewRow row = _grid.Rows[e.RowIndex];
            string text = Convert.ToString(e.FormattedValue).Trim();

            if (e.ColumnIndex == _colIp.Index)
            {
                IPAddress ip;
                if (!BridgeManager.TryParseBindAddress(text, out ip))
                {
                    row.ErrorText = "Enter an IPv4 address such as 192.168.1.200, or 0.0.0.0 for all addresses.";
                    e.Cancel = true;
                    return;
                }
            }
            else if (e.ColumnIndex == _colPort.Index)
            {
                int port;
                if (!int.TryParse(text, out port) || port < 1 || port > 65535)
                {
                    row.ErrorText = "Port must be 1–65535 (9100 is the standard RAW printing port).";
                    e.Cancel = true;
                    return;
                }
            }
            row.ErrorText = "";
        }

        private void AddMapping()
        {
            SyncConfigFromGrid();
            var used = _config.Mappings.Select(x => x.BindAddress ?? "").ToList();
            var m = new MappingConfig
            {
                BindAddress = NetworkHelper.SuggestBindAddress(used),
                Port = 9100,
                PrinterName = PickDefaultPrinter()
            };
            _config.Mappings.Add(m);
            _loadingGrid = true;
            int idx;
            try
            {
                idx = _grid.Rows.Add();
                _grid.Rows[idx].Tag = m;
                FillRow(_grid.Rows[idx], m);
            }
            finally { _loadingGrid = false; }
            _grid.ClearSelection();
            _grid.Rows[idx].Selected = true;
            _grid.CurrentCell = _grid.Rows[idx].Cells[_colPrinter.Index];
            UpdateButtons();
        }

        private string PickDefaultPrinter()
        {
            var taken = new HashSet<string>(_config.Mappings.Select(x => x.PrinterName ?? ""));
            PrinterInfo p = _printers.FirstOrDefault(x => x.IsUsb && !taken.Contains(x.Name))
                ?? _printers.FirstOrDefault(x => x.PortName != "not installed" && !taken.Contains(x.Name))
                ?? _printers.FirstOrDefault();
            return p == null ? null : p.Name;
        }

        private void RemoveSelected()
        {
            var rows = SelectedRows();
            if (rows.Count == 0) return;
            if (rows.Any(r => _manager.IsRunning(MappingOf(r))))
            {
                MessageBox.Show(this, "Stop the selected bridge(s) before removing them.", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            foreach (DataGridViewRow r in rows)
            {
                MappingConfig m = MappingOf(r);
                if (m != null) { _manager.Forget(m); _wanted.Remove(m.Id); _config.Mappings.Remove(m); }
                _grid.Rows.Remove(r);
            }
            UpdateButtons();
        }

        private List<DataGridViewRow> SelectedRows()
        {
            var list = new List<DataGridViewRow>();
            foreach (DataGridViewRow r in _grid.SelectedRows) list.Add(r);
            if (list.Count == 0 && _grid.CurrentRow != null) list.Add(_grid.CurrentRow);
            return list.OrderBy(r => r.Index).ToList();
        }

        private DataGridViewRow RowOf(MappingConfig m)
        {
            foreach (DataGridViewRow r in _grid.Rows) if (ReferenceEquals(r.Tag, m)) return r;
            return null;
        }

        // =====================================================================================  start / stop

        private void StartAll()
        {
            SyncConfigFromGrid();
            var targets = _config.Mappings.Where(m => m.Enabled).ToList();
            if (targets.Count == 0)
            {
                Logger.Warn("Nothing to start: add a mapping and tick its \"On\" box.");
                if (Visible) MessageBox.Show(this, "Add a mapping first (\"+ Add mapping\") and make sure its \"On\" box is ticked.", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            foreach (MappingConfig m in targets) _wanted.Add(m.Id);
            RunStart(targets);
        }

        private void StartSelected()
        {
            SyncConfigFromGrid();
            var targets = SelectedRows().Select(MappingOf).Where(m => m != null).ToList();
            foreach (MappingConfig m in targets) _wanted.Add(m.Id);
            RunStart(targets);
        }

        private async void RunStart(List<MappingConfig> targets)
        {
            if (_busy || targets.Count == 0) return;
            SaveConfig(false);
            SetBusy(true);
            try
            {
                foreach (MappingConfig m in targets)
                {
                    if (_manager.IsRunning(m)) continue;
                    MappingConfig captured = m;
                    string problem = BridgeManager.Validate(captured);
                    if (problem != null)
                    {
                        Logger.Error("Cannot start \"" + (captured.PrinterName ?? "?") + "\": " + problem);
                        DataGridViewRow r = RowOf(captured);
                        if (r != null) { r.ErrorText = problem; r.Cells[_colState.Index].Value = "Error: " + problem; r.Cells[_colState.Index].Style.ForeColor = Color.Firebrick; }
                        continue;
                    }
                    StartOutcome outcome = await Task.Run(() => _manager.StartMapping(captured));
                    DataGridViewRow row = RowOf(captured);
                    if (row != null)
                    {
                        row.ErrorText = outcome.Success ? "" : outcome.Message;
                        UpdateRowStatus(row);
                    }
                }
            }
            finally
            {
                SetBusy(false);
                UpdateStatusBar();
            }
        }

        private async void StopAll()
        {
            if (_busy) return;
            _wanted.Clear();
            SetBusy(true);
            try
            {
                await Task.Run(() => _manager.StopAll());
                foreach (DataGridViewRow r in _grid.Rows) UpdateRowStatus(r);
            }
            finally
            {
                SetBusy(false);
                UpdateStatusBar();
            }
        }

        private async void StopSelected()
        {
            if (_busy) return;
            var targets = SelectedRows().Select(MappingOf).Where(m => m != null).ToList();
            foreach (MappingConfig m in targets) _wanted.Remove(m.Id);
            SetBusy(true);
            try
            {
                foreach (MappingConfig m in targets)
                {
                    MappingConfig captured = m;
                    await Task.Run(() => _manager.StopMapping(captured));
                    DataGridViewRow r = RowOf(captured);
                    if (r != null) UpdateRowStatus(r);
                }
            }
            finally
            {
                SetBusy(false);
                UpdateStatusBar();
            }
        }

        private void ApplyIdleTimeout()
        {
            _manager.JobIdleTimeoutMs = _config.SanitizedIdleTimeout;
            foreach (BridgeListener l in _manager.Listeners) l.JobIdleTimeoutMs = _config.SanitizedIdleTimeout;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            UseWaitCursor = busy;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool anySelected = SelectedRows().Count > 0;
            _btnAdd.Enabled = !_busy;
            _btnRemove.Enabled = !_busy && anySelected;
            _btnStartAll.Enabled = !_busy;
            _btnStopAll.Enabled = !_busy && _manager.RunningCount > 0;
            _btnStartSel.Enabled = !_busy && anySelected;
            _btnStopSel.Enabled = !_busy && anySelected;
            _btnTest.Enabled = !_busy && anySelected;
        }

        // =====================================================================================  status display

        private void UpdateRowStatus(DataGridViewRow row)
        {
            MappingConfig m = MappingOf(row);
            if (m == null) return;
            BridgeListener l = _manager.GetListener(m);
            DataGridViewCell state = row.Cells[_colState.Index];

            string text;
            Color color;
            if (l == null) { text = "Stopped"; color = SystemColors.GrayText; }
            else
            {
                switch (l.State)
                {
                    case BridgeState.Listening:
                        string epos = _manager.HasRunningEposServers(m) ? " + ePOS" : "";
                        text = "● Listening " + l.LocalEndPoint + epos + (l.ActiveConnections > 0 ? "  (" + l.ActiveConnections + " client" + (l.ActiveConnections == 1 ? "" : "s") + ")" : "");
                        color = Color.FromArgb(0, 128, 0);
                        break;
                    case BridgeState.Starting: text = "Starting…"; color = Color.DarkOrange; break;
                    case BridgeState.Error: text = "Error: " + l.StatusText; color = Color.Firebrick; break;
                    default: text = "Stopped"; color = SystemColors.GrayText; break;
                }
            }
            if (!Equals(state.Value, text)) state.Value = text;
            state.Style.ForeColor = color;
            state.ToolTipText = l != null && l.State == BridgeState.Error ? l.StatusText : "";

            string jobs = l == null ? "" : l.JobsCompleted.ToString();
            string bytes = l == null ? "" : BridgeListener.FormatBytes(l.BytesReceived);
            if (!Equals(row.Cells[_colJobs.Index].Value, jobs)) row.Cells[_colJobs.Index].Value = jobs;
            if (!Equals(row.Cells[_colBytes.Index].Value, bytes)) row.Cells[_colBytes.Index].Value = bytes;

            bool running = l != null && l.IsRunning;
            row.DefaultCellStyle.BackColor = running ? Color.FromArgb(236, 248, 236) : Color.Empty;

            // Drop the "stop it first" hint once it is stopped, without clearing a real start error.
            if (!running && row.ErrorText == RunningEditHint) row.ErrorText = "";
        }

        private void UpdateStatusBar()
        {
            int running = _manager.RunningCount;
            long jobs = 0, bytes = 0, clients = 0;
            foreach (BridgeListener l in _manager.Listeners) { jobs += l.JobsCompleted; bytes += l.BytesReceived; clients += l.ActiveConnections; }
            string text = running == 0
                ? "No bridges running."
                : running + " bridge" + (running == 1 ? "" : "s") + " listening · " + clients + " connected · " + jobs + " job" + (jobs == 1 ? "" : "s") + " · " + BridgeListener.FormatBytes(bytes);
            _lblStatus.Text = text;
            _tray.Text = Truncate(Program.AppName + " — " + text, 63);
        }

        private static string Truncate(string s, int max) { return s.Length <= max ? s : s.Substring(0, max - 1) + "…"; }

        private void OnTimerTick()
        {
            if (IsDisposed) return;
            foreach (DataGridViewRow r in _grid.Rows) UpdateRowStatus(r);
            UpdateStatusBar();
            _btnStopAll.Enabled = !_busy && _manager.RunningCount > 0;
            SuperviseBridges();
            CheckVirtualAddresses();
        }

        /// <summary>Every 15 s, re-add virtual addresses that Windows dropped (Wi-Fi reconnect, adapter reset...).</summary>
        private void CheckVirtualAddresses()
        {
            if (_repairingAddresses || !_manager.HasVirtualIps) return;
            if ((DateTime.Now - _lastAddressCheck).TotalSeconds < 15) return;
            _lastAddressCheck = DateTime.Now;
            _repairingAddresses = true;
            Task.Run(() =>
            {
                try { _manager.RepairVirtualIps(); }
                catch (Exception ex) { Logger.Warn("Address check failed: " + ex.Message); }
                finally { _repairingAddresses = false; }
            });
        }

        /// <summary>Restarts bridges the user wants running but which died (adapter went away, printer re-plugged...).</summary>
        private void SuperviseBridges()
        {
            if (_busy || _wanted.Count == 0) return;
            if ((DateTime.Now - _lastRetry).TotalSeconds < 30) return;
            var retry = _config.Mappings.Where(m => _wanted.Contains(m.Id) && !_manager.IsRunning(m) && BridgeManager.Validate(m) == null).ToList();
            if (retry.Count == 0) return;
            _lastRetry = DateTime.Now;
            Logger.Info("Retrying " + retry.Count + " bridge(s) that are not running...");
            RunStart(retry);
        }

        private void OnListenerChanged(BridgeListener listener)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    DataGridViewRow r = RowOf(listener.Mapping);
                    if (r != null) UpdateRowStatus(r);
                    UpdateStatusBar();
                }));
            }
            catch { }
        }

        private void OnLogEntry(LogEntry entry)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(new Action<LogEntry>(AppendLog), entry);
                else AppendLog(entry);
            }
            catch { }
        }

        private void AppendLog(LogEntry entry)
        {
            _logLines.Add(entry.Time.ToString("HH:mm:ss") + "  " + (entry.Level == LogLevel.Error ? "ERROR  " : entry.Level == LogLevel.Warn ? "WARN   " : "       ") + entry.Message);
            if (_logLines.Count > MaxLogLines)
            {
                _logLines.RemoveRange(0, _logLines.Count - MaxLogLines);
                _log.Text = string.Join(Environment.NewLine, _logLines.ToArray());
                _log.SelectionStart = _log.TextLength;
                _log.ScrollToCaret();
            }
            else
            {
                _log.AppendText((_log.TextLength > 0 ? Environment.NewLine : "") + _logLines[_logLines.Count - 1]);
            }
        }

        // =====================================================================================  tools

        private void SaveConfig(bool announce)
        {
            try
            {
                SyncConfigFromGrid();
                ConfigStore.Save(_config);
                if (announce) Logger.Info("Configuration saved to " + ConfigStore.ConfigPath);
            }
            catch (Exception ex)
            {
                Logger.Error("Could not save configuration", ex);
                if (announce) MessageBox.Show(this, "Could not save the configuration:\n" + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowTestMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Direct to the printer (checks the USB printer + driver)", null, (s, e) => TestPrint(false)));
            menu.Items.Add(new ToolStripMenuItem("Through the bridge over TCP (checks the whole path)", null, (s, e) => TestPrint(true)));
            menu.Show(_btnTest, new Point(0, _btnTest.Height));
        }

        private async void TestPrint(bool throughBridge)
        {
            DataGridViewRow row = SelectedRows().FirstOrDefault();
            MappingConfig m = MappingOf(row);
            if (m == null)
            {
                MessageBox.Show(this, "Select a mapping row first.", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (string.IsNullOrEmpty(m.PrinterName))
            {
                MessageBox.Show(this, "The selected row has no printer.", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (throughBridge && !_manager.IsRunning(m))
            {
                MessageBox.Show(this, "Start this bridge first, then test through it.", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string via = throughBridge ? "TCP " + m.EndpointText : "direct (spooler)";
            byte[] data = TestReceipt.Build(m.PrinterName, m.EndpointText, via);
            SetBusy(true);
            try
            {
                string result = await Task.Run(() =>
                {
                    if (!throughBridge)
                    {
                        new SpoolerPrintTarget(m.PrinterName).PrintBytes("Bridge test page", data);
                        return "Test page sent directly to \"" + m.PrinterName + "\".";
                    }
                    IPAddress ip;
                    BridgeManager.TryParseBindAddress(m.BindAddress, out ip);
                    if (ip.Equals(IPAddress.Any)) ip = IPAddress.Loopback;
                    using (var client = new TcpClient())
                    {
                        IAsyncResult ar = client.BeginConnect(ip, m.Port, null, null);
                        if (!ar.AsyncWaitHandle.WaitOne(4000)) throw new TimeoutException("No answer from " + ip + ":" + m.Port + " within 4 s.");
                        client.EndConnect(ar);
                        NetworkStream ns = client.GetStream();
                        ns.Write(data, 0, data.Length);
                        ns.Flush();
                        // Half-close: tells the bridge the job is complete, then wait for it to close its side
                        // (or 1 s) so the socket is torn down gracefully instead of with a reset.
                        client.Client.Shutdown(SocketShutdown.Send);
                        try
                        {
                            client.Client.ReceiveTimeout = 1000;
                            var sink = new byte[16];
                            while (client.Client.Receive(sink) > 0) { }
                        }
                        catch (SocketException) { }
                    }
                    return "Test page sent over TCP to " + ip + ":" + m.Port + " → \"" + m.PrinterName + "\".";
                });
                Logger.Info(result);
            }
            catch (Exception ex)
            {
                string hint = "";
                if (throughBridge && (ex is SocketException || ex is TimeoutException))
                {
                    bool addressPresent = false;
                    IPAddress ip;
                    if (BridgeManager.TryParseBindAddress(m.BindAddress, out ip)) addressPresent = ip.Equals(IPAddress.Any) || NetworkHelper.IsLocalAddress(ip);
                    hint = addressPresent
                        ? "\n\nThe address exists on this PC, so check the bridge status in the grid and the Windows Firewall."
                        : "\n\nThe address " + m.BindAddress + " is not present on this PC right now. Stop and start the bridge so it is added again; if the adapter lost its internet connection, use Tools → Network repair.";
                }
                Logger.Error("Test print failed", ex);
                MessageBox.Show(this, "Test print failed:\n\n" + ex.Message + hint, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void FirewallRule(bool add)
        {
            if (!_manager.IsElevated)
            {
                MessageBox.Show(this, "Administrator rights are required to change Windows Firewall rules. Restart the app as administrator.", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SetBusy(true);
            try
            {
                CommandResult r = await Task.Run(() => add ? NetworkHelper.AddFirewallRule(_manager.ExecutablePath) : NetworkHelper.RemoveFirewallRule());
                if (r.Success) Logger.Info(add ? "Firewall rule added for " + _manager.ExecutablePath : "Firewall rule removed.");
                else Logger.Warn("netsh: " + r.OutputOneLine);
            }
            finally { SetBusy(false); }
        }

        private void UpdateStartWithWindowsCheck()
        {
            Task.Run(() => StartupHelper.IsEnabled()).ContinueWith(t =>
            {
                if (IsDisposed || t.IsFaulted) return;
                try { BeginInvoke(new Action(() => _miStartWithWindows.Checked = t.Result)); } catch { }
            });
        }

        private async void ToggleStartWithWindows()
        {
            if (!_manager.IsElevated)
            {
                MessageBox.Show(this, "Administrator rights are required to create the logon task. Restart the app as administrator.", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            bool enable = !_miStartWithWindows.Checked;
            SetBusy(true);
            try
            {
                CommandResult r = await Task.Run(() => enable ? StartupHelper.Enable(_manager.ExecutablePath) : StartupHelper.Disable());
                if (r.Success)
                {
                    _miStartWithWindows.Checked = enable;
                    Logger.Info(enable
                        ? "Start with Windows enabled (scheduled task \"" + StartupHelper.TaskName + "\", runs at logon with administrator rights and starts all enabled bridges)."
                        : "Start with Windows disabled.");
                    if (enable && !_config.AutoStartBridges)
                    {
                        _chkAutoStart.Checked = true; // the logon task passes --autostart anyway; keep the UI consistent
                    }
                }
                else
                {
                    Logger.Warn("schtasks: " + r.OutputOneLine);
                    MessageBox.Show(this, "Could not change the logon task:\n\n" + r.OutputOneLine, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally { SetBusy(false); }
        }

        private void ShowPrintLog()
        {
            if (_printLog == null || _printLog.IsDisposed)
            {
                _printLog = new PrintLogForm();
                _printLog.FormClosed += (s, e) => _printLog = null;
            }
            _printLog.Show(this);
            _printLog.BringToFront();
        }

        private void ToggleSaveRaw()
        {
            PrintHistory.SaveRawJobs = !PrintHistory.SaveRawJobs;
            _miSaveRaw.Checked = PrintHistory.SaveRawJobs;
            Logger.Info(PrintHistory.SaveRawJobs
                ? "Saving the raw bytes of every job to " + PrintHistory.JobDumpDirectory
                : "Stopped saving raw job bytes.");
        }

        /// <summary>
        /// Listens on the other ports printing software commonly tries and logs anything that arrives.
        /// This is how to find out what a device is really doing when it says it cannot find the printer.
        /// </summary>
        private void ToggleTracing()
        {
            if (_probe.IsRunning)
            {
                _probe.Stop();
                _miTrace.Checked = false;
                return;
            }

            IPAddress address = IPAddress.Any;
            MappingConfig m = MappingOf(SelectedRows().FirstOrDefault());
            if (m != null)
            {
                IPAddress parsed;
                if (BridgeManager.TryParseBindAddress(m.BindAddress, out parsed)) address = parsed;
            }

            var busy = new List<int>();
            foreach (MappingConfig mapping in _config.Mappings)
            {
                busy.Add(mapping.Port);
                busy.Add(mapping.EposHttpPort);
                busy.Add(mapping.EposHttpsPort);
            }

            _probe.Log -= OnProbeLog;
            _probe.Log += OnProbeLog;
            _probe.Start(address, busy);
            _miTrace.Checked = _probe.IsRunning;

            MessageBox.Show(this,
                "Tracing is on for " + address + ".\n\n" +
                "Now try to add the printer on the other device again. Everything it sends to this PC will appear\n" +
                "in the log below, with a hex dump, including attempts on ports the bridge does not normally answer.\n\n" +
                "That tells us which protocol the app expects. Turn tracing off again when you are done.",
                Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnProbeLog(string message)
        {
            Logger.Info(message);
        }

        private void ShowEposEndpoints()
        {
            DataGridViewRow row = SelectedRows().FirstOrDefault();
            MappingConfig m = MappingOf(row);
            if (m == null) { MessageBox.Show(this, "Select a mapping row first.", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (!m.EposEnabled) { MessageBox.Show(this, "The \"ePOS web\" box is not ticked for this mapping.", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            var sb = new StringBuilder();
            sb.AppendLine("Point your Epson ePOS SDK client (web or Android) at one of these:");
            sb.AppendLine();
            List<string> live = _manager.EposEndpoints(m);
            if (live.Count > 0)
            {
                foreach (string url in live) sb.AppendLine("   " + url);
            }
            else
            {
                string ip = string.IsNullOrEmpty(m.BindAddress) ? "<this PC's IP>" : m.BindAddress;
                sb.AppendLine("   http://" + ip + "/cgi-bin/epos/service.cgi    (start the bridge to activate)");
                sb.AppendLine("   https://" + ip + "/cgi-bin/epos/service.cgi");
            }
            sb.AppendLine();
            sb.AppendLine("In the ePOS SDK, connect with device id \"local_printer\".");
            sb.AppendLine("Use the HTTPS endpoint when your POS page is served over HTTPS (browsers block http from an https page).");
            sb.AppendLine("For HTTPS, install the bridge certificate on the client first (Tools -> Export ePOS HTTPS certificate),");
            sb.AppendLine("or open the https URL once in the client's browser and accept the security warning.");
            MessageBox.Show(this, sb.ToString(), "ePOS-Print endpoints", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ExportCertificate()
        {
            try
            {
                using (var dlg = new SaveFileDialog())
                {
                    dlg.Title = "Export the bridge HTTPS certificate (public)";
                    dlg.Filter = "Certificate (*.cer)|*.cer";
                    dlg.FileName = "UsbLanPrinterBridge.cer";
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    string path = SelfSignedCertificate.ExportPublicCertificate(dlg.FileName, NetworkHelper.GetHostIPv4Addresses());
                    Logger.Info("Exported HTTPS certificate to " + path);
                    MessageBox.Show(this,
                        "Saved:\n" + path + "\n\nCopy this file to each client device and install it as a trusted root/CA certificate:\n\n" +
                        "• Windows: double-click → Install Certificate → Local Machine → Trusted Root Certification Authorities.\n" +
                        "• Android: Settings → Security → Encryption & credentials → Install a certificate → CA certificate.\n" +
                        "• iOS: open the file, install the profile, then enable full trust in Settings → General → About → Certificate Trust Settings.\n\n" +
                        "After that, the HTTPS ePOS endpoint is trusted and browsers stop warning.",
                        Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Certificate export failed", ex);
                MessageBox.Show(this, "Could not export the certificate:\n" + ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>Recovery tool: puts an adapter back on DHCP (address + DNS).</summary>
        private async void RepairDhcp()
        {
            if (!_manager.IsElevated)
            {
                MessageBox.Show(this, "Administrator rights are required to change adapter settings. Restart the app as administrator.", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            List<AdapterInfo> adapters = NetworkHelper.GetAdapters();
            if (adapters.Count == 0)
            {
                MessageBox.Show(this, "No connected network adapter found.", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string chosen;
            using (var dlg = new AdapterPickerDialog(adapters))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                chosen = dlg.SelectedAdapter;
            }
            if (string.IsNullOrEmpty(chosen)) return;

            SetBusy(true);
            try
            {
                CommandResult r = await Task.Run(() => NetworkHelper.SetDhcp(chosen));
                Logger.Info("DHCP repair on \"" + chosen + "\": " + r.OutputOneLine);
                MessageBox.Show(this, "Adapter \"" + chosen + "\":\n\n" + r.Output + "\n\nIt can take a few seconds until the adapter has a new address.",
                    Program.AppName, MessageBoxButtons.OK, r.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                RefreshDevices();
            }
            finally { SetBusy(false); }
        }

        private sealed class AdapterPickerDialog : Form
        {
            private readonly ListBox _list;

            public AdapterPickerDialog(List<AdapterInfo> adapters)
            {
                Text = "Re-enable DHCP on an adapter";
                Font = SystemFonts.MessageBoxFont;
                AutoScaleMode = AutoScaleMode.Font;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterParent;
                MinimizeBox = false;
                MaximizeBox = false;
                ShowInTaskbar = false;
                ClientSize = new Size(460, 300);

                var label = new Label
                {
                    Text = "Pick the adapter that lost its automatic (DHCP) configuration. Its IP address and DNS servers will be set back to \"Obtain automatically\".",
                    Dock = DockStyle.Top,
                    Height = 44,
                    Padding = new Padding(10, 8, 10, 0)
                };
                _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
                foreach (AdapterInfo a in adapters)
                    _list.Items.Add(a.Name + "   (" + string.Join(", ", a.Addresses.Select(x => x.ToString()).ToArray()) + (a.HasGateway ? ", has gateway" : ", NO gateway") + ")");
                if (_list.Items.Count > 0) _list.SelectedIndex = 0;
                _list.DoubleClick += (s, e) => { if (_list.SelectedIndex >= 0) DialogResult = DialogResult.OK; };

                var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 4, 10, 4) };
                listHost.Controls.Add(_list);

                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(6, 4, 6, 4) };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
                var ok = new Button { Text = "Re-enable DHCP", DialogResult = DialogResult.OK, Width = 120 };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(ok);

                Controls.Add(listHost);
                Controls.Add(buttons);
                Controls.Add(label);
                AcceptButton = ok;
                CancelButton = cancel;
                Adapters = adapters;
            }

            private List<AdapterInfo> Adapters { get; set; }

            public string SelectedAdapter
            {
                get { return _list.SelectedIndex < 0 ? null : Adapters[_list.SelectedIndex].Name; }
            }
        }

        private static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch { }
        }

        private void ShowHowTo()
        {
            const string text =
"1. Pick the USB printer, give it a LAN address that is free on your network (for example 192.168.1.200) and keep port 9100.\n" +
"2. Click \"Start all\". The app adds that address to this PC's network adapter, opens the firewall and listens.\n" +
"3. On other devices add a network printer that points at that address:\n\n" +
"   • Windows: Settings → Printers → Add printer → \"The printer that I want isn't listed\" →\n" +
"     \"Add a printer using a TCP/IP address\" → Device type: TCP/IP Device, Hostname: the LAN address,\n" +
"     untick \"Query the printer\". Choose Custom → Settings → Protocol RAW, Port 9100.\n" +
"     Install the same driver the printer uses on this PC (e.g. the EPSON driver or \"Generic / Text Only\").\n\n" +
"   • POS / cash-register apps (Android, iOS, Windows): choose \"Network\" / \"Ethernet\" / \"LAN\" printer,\n" +
"     enter the LAN address and port 9100.\n\n" +
"   • Linux / macOS (CUPS): socket://<LAN address>:9100 with the matching driver.\n\n" +
"The bridge passes bytes through unchanged (RAW / JetDirect style). It does not convert documents, so the sending device\n" +
"must use a driver or app that produces the printer's own language (ESC/POS, PCL, ...).\n\n" +
"   • Web / Android POS apps built on the Epson ePOS SDK: tick \"ePOS web\" on the mapping. The bridge then answers\n" +
"     the ePOS-Print endpoint on the standard ports, so apps that only ask for an IP address just work:\n" +
"        http://<LAN address>/cgi-bin/epos/service.cgi        (port 80, also served on 8008)\n" +
"        https://<LAN address>/cgi-bin/epos/service.cgi       (port 443, also served on 8043)\n" +
"     Use device id \"local_printer\". If your POS page is served over HTTPS you must use the https endpoint,\n" +
"     and install the bridge certificate on the client (Tools → Export ePOS HTTPS certificate) so it is trusted.\n" +
"     The bridge converts the ePOS-Print XML to ESC/POS, so any generic ESC/POS printer works.\n\n" +
"Tip: to skip virtual addresses entirely, use this PC's own address or 0.0.0.0 in the \"LAN address\" column.";
            MessageBox.Show(this, text, "How to connect devices to the bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowAbout()
        {
            MessageBox.Show(this,
                Program.AppName + " " + Program.Version + "\n\n" +
                "Exposes printers attached to this PC as RAW/9100 network printers on the LAN.\n" +
                "Runs on Windows 8 and later (.NET Framework 4.5+, included in Windows).\n\n" +
                "Config and logs: " + ConfigStore.DataDirectory,
                "About", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Logger.EntryAdded -= OnLogEntry;
                _manager.ListenerChanged -= OnListenerChanged;
                if (_timer != null) _timer.Dispose();
                if (_tray != null) _tray.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
