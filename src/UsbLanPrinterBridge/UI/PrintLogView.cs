using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using UsbLanPrinterBridge.Core;

namespace UsbLanPrinterBridge.UI
{
    /// <summary>
    /// The print log as a control: every job the bridge handled, with the time, who sent it, how it arrived, how
    /// big it was, whether it printed, and the ticket as it went to the printer. It lives in the "Print log" tab
    /// of the main window and moves into its own window when that tab is pulled out.
    ///
    /// Failed jobs are listed too and shown in red, which is the point when a client is being rejected.
    /// </summary>
    public sealed class PrintLogView : UserControl
    {
        private readonly DataGridView _grid;
        private readonly Timer _timer;
        private readonly CheckBox _chkRaw;
        private readonly CheckBox _chkFollow;
        private readonly TextBox _detail;
        private readonly SplitContainer _split;
        private bool _splitPlaced;
        private int _shown = -1;

        /// <summary>Raised when the number of jobs listed changes.</summary>
        public event EventHandler CountChanged;

        public int JobCount { get { return Math.Max(0, _shown); } }

        public PrintLogView()
        {
            Dock = DockStyle.Fill;
            Font = SystemFonts.MessageBoxFont;

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle
            };
            _grid.Columns.Add(Col("Time", 70));
            _grid.Columns.Add(Col("From", 110));
            _grid.Columns.Add(Col("Printer", 110));
            _grid.Columns.Add(Col("Via", 70));
            _grid.Columns.Add(Col("Size", 55));
            _grid.Columns.Add(Col("Result", 90));
            _grid.Columns.Add(Col("What was printed", 260));
            _grid.SelectionChanged += (s, e) => ShowDetail();

            _detail = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font(FontFamily.GenericMonospace, 9f),
                BackColor = Color.FromArgb(250, 250, 250)
            };

            // The list on the left, the ticket on the right: the tab is wide and short, and so is a second monitor.
            _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
            _split.Panel1.Controls.Add(_grid);
            _split.Panel2.Controls.Add(_detail);
            _split.SizeChanged += (s, e) => PlaceSplitter();

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4, 2, 4, 2) };
            bar.Controls.Add(Btn("Clear list", (s, e) => { PrintHistory.Clear(); Reload(true); }));
            bar.Controls.Add(Btn("Open log folder", (s, e) => OpenFolder(ConfigStore.LogDirectory)));
            bar.Controls.Add(Btn("Open raw job dumps", (s, e) => OpenFolder(PrintHistory.JobDumpDirectory)));

            _chkRaw = new CheckBox { Text = "Save raw bytes of every job", AutoSize = true, Margin = new Padding(12, 7, 6, 0), Checked = PrintHistory.SaveRawJobs };
            _chkRaw.CheckedChanged += (s, e) => PrintHistory.SaveRawJobs = _chkRaw.Checked;
            bar.Controls.Add(_chkRaw);

            _chkFollow = new CheckBox { Text = "Follow new jobs", AutoSize = true, Margin = new Padding(12, 7, 6, 0), Checked = true };
            bar.Controls.Add(_chkFollow);
            bar.Controls.Add(new Label { AutoSize = true, Margin = new Padding(12, 8, 0, 0), ForeColor = SystemColors.GrayText, Text = "Every job is also appended to a daily CSV in the log folder, so this survives restarts." });

            Controls.Add(_split);
            Controls.Add(bar);

            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) => Reload(false);
            _timer.Start();
            Reload(true);
        }

        private void PlaceSplitter()
        {
            if (_splitPlaced || _split.Width < 500) return;
            try
            {
                _split.Panel1MinSize = 260;
                _split.Panel2MinSize = 200;
                _split.SplitterDistance = (int)(_split.Width * 0.6);
                _splitPlaced = true;
            }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
        }

        private static DataGridViewTextBoxColumn Col(string header, int width)
        {
            return new DataGridViewTextBoxColumn { HeaderText = header, FillWeight = width, MinimumWidth = 50 };
        }

        private static Button Btn(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(3) };
            b.Click += onClick;
            return b;
        }

        /// <summary>Re-reads the history; cheap when nothing changed.</summary>
        public void Reload(bool force)
        {
            if (_chkRaw.Checked != PrintHistory.SaveRawJobs) _chkRaw.Checked = PrintHistory.SaveRawJobs;
            List<PrintRecord> records = PrintHistory.Snapshot();
            if (!force && records.Count == _shown) return;
            bool changed = records.Count != _shown;
            _shown = records.Count;

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (PrintRecord r in records)
            {
                int index = _grid.Rows.Add(r.Time.ToString("HH:mm:ss"), r.Source, r.Printer, r.Path, BridgeListener.FormatBytes(r.Bytes), r.Status, r.Preview);
                _grid.Rows[index].Tag = r;
                if (r.Failed) _grid.Rows[index].DefaultCellStyle.ForeColor = Color.Firebrick;
            }
            _grid.ResumeLayout();

            if (_chkFollow.Checked && _grid.Rows.Count > 0)
            {
                _grid.ClearSelection();
                int last = _grid.Rows.Count - 1;
                _grid.Rows[last].Selected = true;
                try { _grid.FirstDisplayedScrollingRowIndex = last; } catch (InvalidOperationException) { }
            }
            if (_grid.Rows.Count == 0) ShowDetail();
            if (changed)
            {
                EventHandler h = CountChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        private void ShowDetail()
        {
            if (_grid.SelectedRows.Count == 0) { _detail.Text = _grid.Rows.Count == 0 ? "Nothing printed yet. Each job the bridge handles is listed here; select one to see the ticket as it went to the printer." : ""; return; }
            var r = _grid.SelectedRows[0].Tag as PrintRecord;
            if (r == null) { _detail.Text = ""; return; }
            _detail.Text =
                "Time    : " + r.TimeText + Environment.NewLine +
                "From    : " + r.Source + Environment.NewLine +
                "Printer : " + r.Printer + Environment.NewLine +
                "Arrived : " + r.Path + Environment.NewLine +
                "Size    : " + r.Bytes + " bytes" + Environment.NewLine +
                "Result  : " + r.Status + Environment.NewLine +
                Environment.NewLine +
                "The ticket as sent to the printer:" + Environment.NewLine +
                "------------------------------------------------" + Environment.NewLine +
                (string.IsNullOrEmpty(r.Ticket)
                    ? (string.IsNullOrEmpty(r.Preview) ? "(nothing printable: this job was a status exchange, a drawer pulse or an image the bridge could not describe)" : r.Preview)
                    : r.Ticket.Replace("\n", Environment.NewLine));
        }

        private static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch
            {
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
