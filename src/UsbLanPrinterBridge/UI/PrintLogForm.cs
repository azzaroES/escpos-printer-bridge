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
    /// The print log: every job the bridge handled, with the time, who sent it, how it arrived, how big it was,
    /// and a readable extract of the text that went to the printer.
    ///
    /// Failed jobs are listed too and shown in red, which is the point when a client is being rejected.
    /// </summary>
    public sealed class PrintLogForm : Form
    {
        private readonly DataGridView _grid;
        private readonly Timer _timer;
        private readonly CheckBox _chkRaw;
        private readonly CheckBox _chkFollow;
        private readonly TextBox _detail;
        private int _shown;

        public PrintLogForm()
        {
            Text = "Print log";
            Icon = Program.LoadIcon(32);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(760, 420);
            ClientSize = new Size(1000, 560);

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
            _grid.Columns.Add(Col("Time", 90));
            _grid.Columns.Add(Col("From", 90));
            _grid.Columns.Add(Col("Printer", 110));
            _grid.Columns.Add(Col("Via", 70));
            _grid.Columns.Add(Col("Size", 55));
            _grid.Columns.Add(Col("Result", 90));
            _grid.Columns.Add(Col("What was printed", 300));
            _grid.SelectionChanged += (s, e) => ShowDetail();

            _detail = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font(FontFamily.GenericMonospace, 9f),
                BackColor = Color.FromArgb(250, 250, 250)
            };

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 340
            };
            split.Panel1.Controls.Add(_grid);
            split.Panel2.Controls.Add(_detail);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
            bar.Controls.Add(Btn("Refresh", (s, e) => Reload()));
            bar.Controls.Add(Btn("Clear list", (s, e) => { PrintHistory.Clear(); Reload(); }));
            bar.Controls.Add(Btn("Open log folder", (s, e) => OpenFolder(ConfigStore.LogDirectory)));
            bar.Controls.Add(Btn("Open raw job dumps", (s, e) => OpenFolder(PrintHistory.JobDumpDirectory)));

            _chkRaw = new CheckBox
            {
                Text = "Save raw bytes of every job",
                AutoSize = true,
                Margin = new Padding(12, 8, 6, 0),
                Checked = PrintHistory.SaveRawJobs
            };
            _chkRaw.CheckedChanged += (s, e) => PrintHistory.SaveRawJobs = _chkRaw.Checked;
            bar.Controls.Add(_chkRaw);

            _chkFollow = new CheckBox { Text = "Follow new jobs", AutoSize = true, Margin = new Padding(12, 8, 6, 0), Checked = true };
            bar.Controls.Add(_chkFollow);

            var hint = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 34,
                Padding = new Padding(8, 8, 8, 0),
                ForeColor = SystemColors.GrayText,
                Text = "Every job is also appended to a daily CSV in the log folder, so this survives restarts."
            };

            Controls.Add(split);
            Controls.Add(bar);
            Controls.Add(hint);

            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) => Reload();
            _timer.Start();

            Load += (s, e) => Reload();
            FormClosed += (s, e) => _timer.Dispose();
        }

        private static DataGridViewTextBoxColumn Col(string header, int width)
        {
            return new DataGridViewTextBoxColumn { HeaderText = header, FillWeight = width, MinimumWidth = 50 };
        }

        private Button Btn(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(3) };
            b.Click += onClick;
            return b;
        }

        private void Reload()
        {
            List<PrintRecord> records = PrintHistory.Snapshot();
            if (records.Count == _shown) return;
            _shown = records.Count;

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (PrintRecord r in records)
            {
                int index = _grid.Rows.Add(
                    r.Time.ToString("HH:mm:ss"),
                    r.Source,
                    r.Printer,
                    r.Path,
                    BridgeListener.FormatBytes(r.Bytes),
                    r.Status,
                    r.Preview);
                _grid.Rows[index].Tag = r;
                if (r.Failed) _grid.Rows[index].DefaultCellStyle.ForeColor = Color.Firebrick;
            }
            _grid.ResumeLayout();

            if (_chkFollow.Checked && _grid.Rows.Count > 0)
            {
                _grid.ClearSelection();
                int last = _grid.Rows.Count - 1;
                _grid.Rows[last].Selected = true;
                _grid.FirstDisplayedScrollingRowIndex = last;
            }
            Text = "Print log  —  " + records.Count + " job(s)";
        }

        private void ShowDetail()
        {
            if (_grid.SelectedRows.Count == 0) { _detail.Text = ""; return; }
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
                "Text sent to the printer:" + Environment.NewLine +
                (string.IsNullOrEmpty(r.Preview) ? "(no readable text; this job was probably an image or a status exchange)" : r.Preview);
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
    }
}
