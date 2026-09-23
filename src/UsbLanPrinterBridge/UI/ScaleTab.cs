using System;
using System.Drawing;
using System.Windows.Forms;
using UsbLanPrinterBridge.Core;

namespace UsbLanPrinterBridge.UI
{
    /// <summary>
    /// The Scale tab: configures the weighing scale (serial/USB COM port or a network scale over TCP), starts/stops
    /// the reader + the GET /scale endpoint, and shows the live weight, the raw lines (to identify an unknown scale)
    /// and the scale log. The scale is published on the LAN, so it works from any device, wireless by nature.
    /// </summary>
    public sealed class ScaleTab : UserControl
    {
        private readonly ScaleService _service;
        private ScaleConfig _cfg;
        private Action _onChanged;
        private bool _binding;

        private readonly CheckBox _enabled = new CheckBox { Text = "Publish a scale on the network (GET /scale)", AutoSize = true };
        private readonly RadioButton _serial = new RadioButton { Text = "Serial / USB (COM port)", AutoSize = true, Checked = true };
        private readonly RadioButton _tcp = new RadioButton { Text = "Network scale (TCP)", AutoSize = true };

        private readonly ComboBox _port = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 120 };
        private readonly Button _refresh = new Button { Text = "Refresh", AutoSize = true };
        private readonly ComboBox _baud = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        private readonly ComboBox _parity = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
        private readonly ComboBox _dataBits = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 50 };
        private readonly ComboBox _stopBits = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 50 };

        private readonly TextBox _host = new TextBox { Width = 150 };
        private readonly NumericUpDown _tcpPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 4001, Width = 80 };

        private readonly TextBox _bind = new TextBox { Width = 150 };
        private readonly NumericUpDown _httpPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 8020, Width = 80 };
        private readonly TextBox _poll = new TextBox { Width = 90 };
        private readonly NumericUpDown _pollMs = new NumericUpDown { Minimum = 0, Maximum = 60000, Increment = 100, Value = 0, Width = 80 };
        private readonly ComboBox _displayUnit = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };

        private readonly Button _start = new Button { Text = "Start", AutoSize = true };
        private readonly Button _stop = new Button { Text = "Stop", AutoSize = true, Enabled = false };
        private readonly Label _endpoint = new Label { AutoSize = true, ForeColor = Color.FromArgb(11, 91, 211) };

        private readonly Label _weight = new Label { AutoSize = true, Font = new Font("Segoe UI", 26f, FontStyle.Bold) };
        private readonly Label _statusLine = new Label { AutoSize = true, ForeColor = Color.DimGray };
        private readonly TextBox _raw = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 90, Font = new Font("Consolas", 9f) };
        private readonly ListBox _log = new ListBox { Height = 120, Font = new Font("Consolas", 9f), IntegralHeight = false };

        private readonly Timer _tick = new Timer { Interval = 300 };

        public ScaleTab(ScaleService service)
        {
            _service = service;
            Dock = DockStyle.Fill;
            Padding = new Padding(12);
            BackColor = Color.White;
            BuildUi();

            foreach (int b in new[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 }) _baud.Items.Add(b);
            _baud.SelectedItem = 9600;
            _parity.Items.AddRange(new object[] { "none", "even", "odd" }); _parity.SelectedIndex = 0;
            _dataBits.Items.AddRange(new object[] { "7", "8" }); _dataBits.SelectedItem = "8";
            _stopBits.Items.AddRange(new object[] { "1", "1.5", "2" }); _stopBits.SelectedItem = "1";
            _displayUnit.Items.AddRange(new object[] { "as scale", "kg", "g", "lb", "oz" }); _displayUnit.SelectedIndex = 0;
            _displayUnit.SelectedIndexChanged += (s, e) => WriteOnly();
            RefreshPorts();

            _refresh.Click += (s, e) => RefreshPorts();
            _serial.CheckedChanged += (s, e) => { UpdateTransportVisibility(); WriteOnly(); };
            _tcp.CheckedChanged += (s, e) => { UpdateTransportVisibility(); WriteOnly(); };
            _start.Click += (s, e) => StartScale();
            _stop.Click += (s, e) => { _service.Stop(); _cfg.Enabled = false; _enabled.Checked = false; Changed(); RefreshRunning(); };
            _enabled.CheckedChanged += (s, e) => { if (_binding) return; if (_enabled.Checked) StartScale(); else { _cfg.Enabled = false; _service.Stop(); Changed(); RefreshRunning(); } };
            foreach (Control c in new Control[] { _port, _baud, _parity, _dataBits, _stopBits, _host, _bind, _poll })
                if (c is TextBox) c.TextChanged += (s, e) => WriteOnly(); else ((ComboBox)c).SelectedIndexChanged += (s, e) => WriteOnly();
            _tcpPort.ValueChanged += (s, e) => WriteOnly();
            _httpPort.ValueChanged += (s, e) => WriteOnly();
            _pollMs.ValueChanged += (s, e) => WriteOnly();

            ScaleLog.EventAdded += OnScaleEvent;
            _tick.Tick += (s, e) => RefreshLive();
            _tick.Start();
        }

        public void Bind(ScaleConfig cfg, Action onChanged)
        {
            _binding = true;
            _cfg = cfg ?? new ScaleConfig();
            _onChanged = onChanged;

            _enabled.Checked = _cfg.Enabled;
            _serial.Checked = !_cfg.IsTcp;
            _tcp.Checked = _cfg.IsTcp;
            if (!string.IsNullOrEmpty(_cfg.PortName)) _port.Text = _cfg.PortName;
            _baud.SelectedItem = _cfg.Baud; if (_baud.SelectedItem == null) { _baud.Items.Add(_cfg.Baud); _baud.SelectedItem = _cfg.Baud; }
            _parity.SelectedItem = string.IsNullOrEmpty(_cfg.Parity) ? "none" : _cfg.Parity.ToLowerInvariant();
            _dataBits.SelectedItem = _cfg.DataBits.ToString(); if (_dataBits.SelectedItem == null) _dataBits.SelectedItem = "8";
            _stopBits.SelectedItem = string.IsNullOrEmpty(_cfg.StopBits) ? "1" : _cfg.StopBits;
            _host.Text = _cfg.Host ?? "";
            if (_cfg.TcpPort >= 1 && _cfg.TcpPort <= 65535) _tcpPort.Value = _cfg.TcpPort;
            _bind.Text = _cfg.BindAddress ?? "";
            if (_cfg.HttpPort >= 1 && _cfg.HttpPort <= 65535) _httpPort.Value = _cfg.HttpPort;
            _poll.Text = _cfg.PollCommand ?? "";
            _pollMs.Value = Math.Max(0, Math.Min(60000, _cfg.PollIntervalMs));
            _displayUnit.SelectedItem = string.IsNullOrEmpty(_cfg.DisplayUnit) ? "as scale" : _cfg.DisplayUnit.ToLowerInvariant();
            if (_displayUnit.SelectedItem == null) _displayUnit.SelectedIndex = 0;

            UpdateTransportVisibility();
            RefreshRunning();
            _binding = false;
        }

        private void StartScale()
        {
            WriteToConfig();
            _cfg.Enabled = true;
            StartOutcome outcome = _service.Start(_cfg);
            if (!outcome.Success)
            {
                MessageBox.Show(this, outcome.Message, "Scale", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _cfg.Enabled = false;
            }
            Changed();
            RefreshRunning();
        }

        private void WriteToConfig()
        {
            if (_cfg == null) return;
            _cfg.Transport = _tcp.Checked ? "tcp" : "serial";
            _cfg.PortName = _port.Text.Trim();
            _cfg.Baud = _baud.SelectedItem is int ? (int)_baud.SelectedItem : 9600;
            _cfg.Parity = _parity.SelectedItem as string ?? "none";
            _cfg.DataBits = int.TryParse(_dataBits.SelectedItem as string, out int db) ? db : 8;
            _cfg.StopBits = _stopBits.SelectedItem as string ?? "1";
            _cfg.Host = _host.Text.Trim();
            _cfg.TcpPort = (int)_tcpPort.Value;
            _cfg.BindAddress = _bind.Text.Trim();
            _cfg.HttpPort = (int)_httpPort.Value;
            _cfg.PollCommand = _poll.Text;
            _cfg.PollIntervalMs = (int)_pollMs.Value;
            _cfg.DisplayUnit = _displayUnit.SelectedIndex <= 0 ? "" : (_displayUnit.SelectedItem as string ?? "");
        }

        private void WriteOnly()
        {
            if (_binding || _cfg == null) return;
            WriteToConfig();
        }

        private void Changed()
        {
            if (_binding || _cfg == null) return;
            WriteToConfig();
            if (_onChanged != null) { try { _onChanged(); } catch { } }
        }

        private void RefreshPorts()
        {
            string current = _port.Text;
            _port.Items.Clear();
            foreach (string p in SerialScaleSource.AvailablePorts()) _port.Items.Add(p);
            if (!string.IsNullOrEmpty(current)) _port.Text = current;
            else if (_port.Items.Count > 0) _port.SelectedIndex = 0;
        }

        private void UpdateTransportVisibility()
        {
            bool tcp = _tcp.Checked;
            _serialGroup.Visible = !tcp;
            _tcpGroup.Visible = tcp;
        }

        private void RefreshRunning()
        {
            bool running = _service.Running;
            _start.Enabled = !running;
            _stop.Enabled = running;
            _endpoint.Text = running ? "Live at " + _service.Endpoint : "";
        }

        private void RefreshLive()
        {
            if (_service.Reader == null)
            {
                _weight.Text = "—";
                _weight.ForeColor = Color.Gray;
                _statusLine.Text = _service.Running ? "starting…" : "stopped";
                return;
            }
            ScaleReading r = _service.Reader.Current;
            if (_cfg != null && !string.IsNullOrEmpty(_cfg.DisplayUnit)) r = r.InUnit(_cfg.DisplayUnit);
            if (r.Ok)
            {
                _weight.Text = r.Weight.ToString(System.Globalization.CultureInfo.InvariantCulture) + (r.Unit.Length > 0 ? " " + r.Unit : "");
                _weight.ForeColor = r.Stable ? Color.FromArgb(22, 122, 52) : Color.FromArgb(180, 120, 0);
            }
            else { _weight.Text = "—"; _weight.ForeColor = Color.Gray; }
            _statusLine.Text = (_service.Reader.Connected ? "connected · " : "") + _service.Reader.Status
                               + (r.Ok ? (r.Stable ? "  ·  stable" : "  ·  moving") : "");
            _raw.Text = string.Join("\r\n", _service.Reader.RecentRaw());
        }

        private void OnScaleEvent(ScaleEvent e)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) { BeginInvoke((Action)(() => OnScaleEvent(e))); return; }
                _log.Items.Insert(0, e.TimeText + "  " + e.What + (string.IsNullOrEmpty(e.Detail) ? "" : "  " + e.Detail));
                while (_log.Items.Count > 200) _log.Items.RemoveAt(_log.Items.Count - 1);
            }
            catch { }
        }

        private GroupBox _serialGroup;
        private GroupBox _tcpGroup;

        private void BuildUi()
        {
            var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

            root.Controls.Add(Pad(_enabled, 0, 0, 0, 8));
            var transport = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            transport.Controls.Add(_serial); transport.Controls.Add(Spacer(16)); transport.Controls.Add(_tcp);
            root.Controls.Add(transport);

            _serialGroup = new GroupBox { Text = "Serial scale (COM / RS232-to-USB)", AutoSize = true, Padding = new Padding(8) };
            var sg = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            sg.Controls.Add(Field("Port", _port)); sg.Controls.Add(_refresh); sg.Controls.Add(Field("Baud", _baud));
            sg.Controls.Add(Field("Data", _dataBits)); sg.Controls.Add(Field("Parity", _parity)); sg.Controls.Add(Field("Stop", _stopBits));
            _serialGroup.Controls.Add(sg);
            root.Controls.Add(_serialGroup);

            _tcpGroup = new GroupBox { Text = "Network scale (streams weight over TCP)", AutoSize = true, Padding = new Padding(8), Visible = false };
            var tg = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            tg.Controls.Add(Field("Host / IP", _host)); tg.Controls.Add(Field("Port", _tcpPort));
            _tcpGroup.Controls.Add(tg);
            root.Controls.Add(_tcpGroup);

            var pubGroup = new GroupBox { Text = "Published as GET /scale on the network", AutoSize = true, Padding = new Padding(8) };
            var pg = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            pg.Controls.Add(Field("Address (blank = all, or its own IP)", _bind));
            pg.Controls.Add(Field("Port", _httpPort));
            pg.Controls.Add(Field("Show as", _displayUnit));
            pg.Controls.Add(Field("Poll cmd (optional)", _poll));
            pg.Controls.Add(Field("every ms", _pollMs));
            pubGroup.Controls.Add(pg);
            root.Controls.Add(pubGroup);

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            buttons.Controls.Add(_start); buttons.Controls.Add(_stop); buttons.Controls.Add(Spacer(12)); buttons.Controls.Add(_endpoint);
            root.Controls.Add(Pad(buttons, 0, 8, 0, 8));

            var liveGroup = new GroupBox { Text = "Live weight", AutoSize = true, Padding = new Padding(8), MinimumSize = new Size(420, 0) };
            var lv = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            lv.Controls.Add(_weight); lv.Controls.Add(_statusLine);
            lv.Controls.Add(new Label { Text = "Raw lines from the scale (use these to identify an unknown model):", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 8, 0, 2) });
            _raw.Width = 420; lv.Controls.Add(_raw);
            liveGroup.Controls.Add(lv);
            root.Controls.Add(liveGroup);

            var logGroup = new GroupBox { Text = "Scale log", AutoSize = true, Padding = new Padding(8) };
            _log.Width = 560; logGroup.Controls.Add(_log);
            root.Controls.Add(logGroup);

            Controls.Add(root);
        }

        private static Control Field(string label, Control c)
        {
            var p = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 12, 0) };
            p.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Color.DimGray });
            p.Controls.Add(c);
            return p;
        }

        private static Control Spacer(int w) { return new Panel { Width = w, Height = 1 }; }
        private static Control Pad(Control c, int l, int t, int r, int b) { c.Margin = new Padding(l, t, r, b); return c; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { ScaleLog.EventAdded -= OnScaleEvent; } catch { }
                try { _tick.Stop(); _tick.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
