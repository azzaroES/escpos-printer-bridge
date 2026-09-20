using System;
using System.Drawing;
using System.Windows.Forms;

namespace UsbLanPrinterBridge.UI
{
    /// <summary>
    /// The print log in a window of its own. The main window shows the same view in its "Print log" tab, which can
    /// be pulled out; this form remains for callers that simply want the log in a window.
    /// </summary>
    public sealed class PrintLogForm : Form
    {
        private readonly PrintLogView _view = new PrintLogView();

        public PrintLogForm()
        {
            Text = "Print log";
            Icon = Program.LoadIcon(32);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(760, 420);
            ClientSize = new Size(1000, 560);
            Controls.Add(_view);
            _view.CountChanged += (s, e) => UpdateTitle();
            Load += (s, e) => { _view.Reload(true); UpdateTitle(); };
        }

        private void UpdateTitle()
        {
            Text = "Print log  —  " + _view.JobCount + " job(s)";
        }
    }
}
