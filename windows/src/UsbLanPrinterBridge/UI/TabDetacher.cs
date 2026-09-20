using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UsbLanPrinterBridge.UI
{
    /// <summary>
    /// Lets the tabs of a TabControl be pulled out into windows of their own, for a second monitor or to keep the
    /// print log beside the printer list. Double-click a tab, drag it off the strip, or right-click it; close the
    /// window (or press Dock back) and the tab returns to where it was.
    ///
    /// The page's controls are moved, not recreated, so whatever the tab was showing carries on unchanged.
    /// </summary>
    internal sealed class TabDetacher
    {
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 2;

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private readonly TabControl _tabs;
        private readonly List<TabPage> _order;
        private readonly Dictionary<TabPage, FloatingTabForm> _floating = new Dictionary<TabPage, FloatingTabForm>();
        private readonly Icon _icon;
        private TabPage _pressed;
        private Point _pressedAt;
        private bool _closingAll;

        /// <summary>Raised after a tab was pulled out, docked back, or its window was moved or resized.</summary>
        public event EventHandler Changed;

        public TabDetacher(TabControl tabs, Icon icon)
        {
            _tabs = tabs;
            _icon = icon;
            _order = tabs.TabPages.Cast<TabPage>().ToList();
            tabs.ShowToolTips = true;
            foreach (TabPage p in _order) p.ToolTipText = "Double-click, or drag this tab off the strip, to open it in its own window";
            tabs.MouseDoubleClick += (s, e) => { TabPage p = PageAt(e.Location); if (p != null && e.Button == MouseButtons.Left) Detach(p, null, false); };
            tabs.MouseDown += (s, e) => { _pressed = e.Button == MouseButtons.Left ? PageAt(e.Location) : null; _pressedAt = e.Location; };
            tabs.MouseUp += OnMouseUp;
            tabs.MouseMove += OnMouseMove;
        }

        public IList<TabPage> AllPages { get { return _order.AsReadOnly(); } }

        public bool IsFloating(TabPage page) { return _floating.ContainsKey(page); }

        public Form WindowOf(TabPage page) { FloatingTabForm f; return _floating.TryGetValue(page, out f) ? f : null; }

        /// <summary>The bounds of a floating tab's window (its restored bounds when maximised), or null when docked.</summary>
        public Rectangle? BoundsOf(TabPage page)
        {
            FloatingTabForm f;
            if (!_floating.TryGetValue(page, out f)) return null;
            return f.WindowState == FormWindowState.Normal ? f.Bounds : f.RestoreBounds;
        }

        private TabPage PageAt(Point client)
        {
            for (int i = 0; i < _tabs.TabCount; i++)
                if (_tabs.GetTabRect(i).Contains(client)) return _tabs.TabPages[i];
            return null;
        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            _pressed = null;
            if (e.Button != MouseButtons.Right) return;
            TabPage p = PageAt(e.Location);
            if (p == null) return;
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open \"" + p.Text + "\" in its own window", null, (s2, e2) => Detach(p, null, false));
            if (_floating.Count > 0) menu.Items.Add("Dock every window back", null, (s2, e2) => DockAll());
            menu.Show(_tabs, e.Location);
        }

        /// <summary>A tab dragged well clear of the strip comes off, and the new window keeps following the mouse.</summary>
        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_pressed == null || e.Button != MouseButtons.Left) return;
            Rectangle strip = _tabs.TabCount > 0 ? Rectangle.Union(_tabs.GetTabRect(0), _tabs.GetTabRect(_tabs.TabCount - 1)) : Rectangle.Empty;
            strip.Inflate(30, 30);
            if (strip.Contains(e.Location)) return;
            if (Math.Abs(e.Y - _pressedAt.Y) < 30 && Math.Abs(e.X - _pressedAt.X) < 60) return;
            TabPage page = _pressed;
            _pressed = null;
            Detach(page, null, true);
        }

        /// <summary>Pulls a tab out. With bounds it opens there (a restored layout); otherwise near the mouse.</summary>
        public Form Detach(TabPage page, Rectangle? bounds, bool followMouse)
        {
            if (page == null || _floating.ContainsKey(page) || !_tabs.TabPages.Contains(page)) return WindowOf(page);
            var form = new FloatingTabForm(page.Text, _icon);
            Control[] content = page.Controls.Cast<Control>().ToArray();
            _tabs.TabPages.Remove(page);
            foreach (Control c in content) form.Host.Controls.Add(c);
            _floating[page] = form;

            Rectangle wanted = bounds ?? new Rectangle(Cursor.Position.X - 200, Cursor.Position.Y - 12, 1000, 600);
            Rectangle area = Screen.FromPoint(new Point(wanted.X + wanted.Width / 2, wanted.Y + 20)).WorkingArea;
            wanted.Width = Math.Min(Math.Max(wanted.Width, 500), area.Width);
            wanted.Height = Math.Min(Math.Max(wanted.Height, 300), area.Height);
            wanted.X = Math.Max(area.Left, Math.Min(wanted.X, area.Right - wanted.Width));
            wanted.Y = Math.Max(area.Top, Math.Min(wanted.Y, area.Bottom - wanted.Height));
            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = wanted;

            EventHandler retitle = (s, e) => form.Text = page.Text;
            page.TextChanged += retitle;
            form.DockRequested += (s, e) => form.Close();
            form.FormClosed += (s, e) =>
            {
                page.TextChanged -= retitle;
                Reattach(page, form);
            };
            form.ResizeEnd += (s, e) => RaiseChanged();
            form.Show();
            RaiseChanged();

            if (followMouse && (Control.MouseButtons & MouseButtons.Left) != 0)
            {
                // Hand the drag over to the new window, so it moves with the mouse until the button is released.
                ReleaseCapture();
                SendMessage(form.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                RaiseChanged();
            }
            return form;
        }

        private void Reattach(TabPage page, FloatingTabForm form)
        {
            if (!_floating.Remove(page)) return;
            Control[] content = form.Host.Controls.Cast<Control>().ToArray();
            foreach (Control c in content) page.Controls.Add(c);
            if (_closingAll || _tabs.IsDisposed) return;
            int index = 0;
            foreach (TabPage p in _order)
            {
                if (p == page) break;
                if (_tabs.TabPages.Contains(p)) index++;
            }
            _tabs.TabPages.Insert(Math.Min(index, _tabs.TabCount), page);
            _tabs.SelectedTab = page;
            RaiseChanged();
        }

        public void Dock(TabPage page)
        {
            FloatingTabForm f;
            if (_floating.TryGetValue(page, out f)) f.Close();
        }

        public void DockAll()
        {
            foreach (FloatingTabForm f in _floating.Values.ToArray()) f.Close();
        }

        /// <summary>Closes the floating windows without touching the saved layout; used when the application exits.</summary>
        public void CloseForExit()
        {
            _closingAll = true;
            foreach (FloatingTabForm f in _floating.Values.ToArray()) f.Close();
        }

        private void RaiseChanged()
        {
            if (_closingAll) return;
            EventHandler h = Changed;
            if (h != null) h(this, EventArgs.Empty);
        }
    }

    /// <summary>The window a pulled-out tab lives in: a thin strip with "Dock back", and the tab's content below it.</summary>
    internal sealed class FloatingTabForm : Form
    {
        public readonly Panel Host = new Panel { Dock = DockStyle.Fill };
        public event EventHandler DockRequested;

        public FloatingTabForm(string title, Icon icon)
        {
            Text = title;
            if (icon != null) Icon = icon;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            MinimumSize = new Size(500, 300);
            ShowInTaskbar = true;
            KeyPreview = true;

            var strip = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 24, BackColor = SystemColors.Control, WrapContents = false, Padding = new Padding(4, 4, 0, 0) };
            var dock = new LinkLabel { Text = "⇲ Dock back into the main window", AutoSize = true, Margin = new Padding(2, 0, 10, 0), LinkBehavior = LinkBehavior.HoverUnderline };
            dock.LinkClicked += (s, e) => { EventHandler h = DockRequested; if (h != null) h(this, EventArgs.Empty); };
            var hint = new Label { Text = "Closing this window does the same.", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0) };
            strip.Controls.Add(dock);
            strip.Controls.Add(hint);
            Controls.Add(Host);
            Controls.Add(strip);
        }
    }
}
