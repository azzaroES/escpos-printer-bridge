using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace UsbLanPrinterBridge.UI
{
    /// <summary>Where the folded state and the order of the sections are kept between runs.</summary>
    public interface ISectionStore
    {
        bool IsCollapsed(string key);
        void SetCollapsed(string key, bool collapsed);
        string[] GetOrder(string group);
        void SetOrder(string group, string[] keys);
    }

    /// <summary>
    /// An accordion section: a header with a grip to drag it, a chevron and a title that fold it with a click,
    /// a one-line summary shown while it is folded, room for a few controls on the right, and the body below.
    /// Card style draws the bordered panel; sub style is a plain block with a line under its header.
    /// </summary>
    internal sealed class CollapsibleSection : Panel
    {
        public const int HeaderHeight = 24;

        public readonly string Key;
        public readonly bool Card;
        public readonly Panel Body = new Panel { Dock = DockStyle.Fill };

        private readonly Panel _header = new Panel { Dock = DockStyle.Top, Height = HeaderHeight };
        private readonly Label _grip = new Label { Text = "≡", AutoSize = false, Width = 18, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.SizeAll, ForeColor = Color.FromArgb(150, 150, 146) };
        private readonly Label _chevron = new Label { Text = "▾", AutoSize = false, Width = 16, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand };
        private readonly Label _title = new Label { AutoSize = true, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.Hand, Padding = new Padding(0, 5, 8, 0) };
        private readonly Label _summary = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Cursor = Cursors.Hand, Visible = false };
        private readonly FlowLayoutPanel _extras = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0), Padding = new Padding(0) };

        private int _bodyHeight;
        private bool _collapsed;
        private bool _dragging;

        public event EventHandler CollapsedChanged;

        public CollapsibleSection(string key, string title, bool card, int bodyHeight)
        {
            Key = key;
            Card = card;
            _bodyHeight = bodyHeight;
            BackColor = Palette.Surface;
            Padding = card ? new Padding(8, 6, 10, 8) : new Padding(0, 2, 0, 2);
            Margin = new Padding(0);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            _title.Text = title;
            _title.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, card ? 9f : 8.25f, FontStyle.Bold);
            _summary.ForeColor = Palette.Muted;
            _summary.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 8f);
            _grip.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, card ? 11f : 9.5f);
            _chevron.ForeColor = Palette.Muted;

            // Dock order: the last control added docks first, so add from the inside out.
            _header.Controls.Add(_summary);
            _header.Controls.Add(_extras);
            _header.Controls.Add(_title);
            _header.Controls.Add(_chevron);
            _header.Controls.Add(_grip);
            Controls.Add(Body);
            Controls.Add(_header);

            EventHandler toggle = (s, e) => Collapsed = !Collapsed;
            _header.Click += toggle; _title.Click += toggle; _chevron.Click += toggle; _summary.Click += toggle;
            _grip.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { SectionColumn c = Parent as SectionColumn; if (c != null) c.BeginDrag(this); } };
            _grip.MouseMove += (s, e) => { if (e.Button == MouseButtons.Left) { SectionColumn c = Parent as SectionColumn; if (c != null) c.DragTo(Cursor.Position); } };
            _grip.MouseUp += (s, e) => { SectionColumn c = Parent as SectionColumn; if (c != null) c.EndDrag(); };
            var tip = new ToolTip();
            tip.SetToolTip(_grip, "Drag to move this section");
            tip.SetToolTip(_title, "Click to fold or unfold");
            if (!card) _header.Paint += (s, e) => { using (var pen = new Pen(Palette.Grid)) e.Graphics.DrawLine(pen, 0, _header.Height - 1, _header.Width, _header.Height - 1); };
            UpdateHeight();
        }

        public string Title { get { return _title.Text; } }

        /// <summary>The height the body needs while unfolded.</summary>
        public int BodyHeight
        {
            get { return _bodyHeight; }
            set { if (_bodyHeight == value) return; _bodyHeight = value; UpdateHeight(); }
        }

        public bool Collapsed
        {
            get { return _collapsed; }
            set
            {
                if (_collapsed == value) return;
                _collapsed = value;
                _chevron.Text = value ? "▸" : "▾";
                Body.Visible = !value;
                _summary.Visible = value;
                UpdateHeight();
                EventHandler h = CollapsedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        /// <summary>One line shown in the header while folded, so the gist stays visible.</summary>
        public string Summary
        {
            get { return _summary.Text; }
            set { if (_summary.Text != value) _summary.Text = value ?? ""; }
        }

        /// <summary>Adds a control to the right of the header; it stays usable while the section is folded.</summary>
        public void AddHeaderControl(Control c)
        {
            c.Margin = new Padding(6, 2, 0, 0);
            _extras.Controls.Add(c);
        }

        internal bool Dragging
        {
            get { return _dragging; }
            set { if (_dragging == value) return; _dragging = value; Invalidate(); }
        }

        private void UpdateHeight()
        {
            Height = Padding.Vertical + HeaderHeight + (_collapsed ? 0 : _bodyHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Card || _dragging)
            {
                using (var pen = new Pen(_dragging ? Palette.Series1 : Palette.CardBorder, _dragging ? 2f : 1f))
                    e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }
    }

    /// <summary>
    /// A column of sections stacked by hand, so that one can be dragged by its grip to a new place. The dragged
    /// section moves live as the pointer passes the middle of its neighbours, and the enclosing scroll panel
    /// scrolls when the pointer nears its top or bottom edge.
    /// </summary>
    internal sealed class SectionColumn : Panel
    {
        public readonly string Group;
        public int Gap = 8;

        private readonly List<CollapsibleSection> _order = new List<CollapsibleSection>();
        private readonly Timer _edgeScroll = new Timer { Interval = 30 };
        private CollapsibleSection _dragged;
        private bool _laying;

        public event EventHandler OrderChanged;
        public event EventHandler<SectionEventArgs> SectionToggled;

        public SectionColumn(string group)
        {
            Group = group;
            Dock = DockStyle.Top;
            BackColor = Color.Transparent;
            Margin = new Padding(0);
            _edgeScroll.Tick += (s, e) => EdgeScroll();
        }

        public IList<CollapsibleSection> Sections { get { return _order.AsReadOnly(); } }

        public string[] Keys { get { return _order.Select(s => s.Key).ToArray(); } }

        public void Add(CollapsibleSection section)
        {
            _order.Add(section);
            Controls.Add(section);
            section.SizeChanged += (s, e) => Relayout();
            section.CollapsedChanged += (s, e) =>
            {
                EventHandler<SectionEventArgs> h = SectionToggled;
                if (h != null) h(this, new SectionEventArgs(section));
            };
            Relayout();
        }

        /// <summary>Puts the sections in the given order; keys not named keep their relative place at the end.</summary>
        public void ApplyOrder(IEnumerable<string> keys)
        {
            if (keys == null) return;
            var wanted = new List<CollapsibleSection>();
            foreach (string k in keys)
            {
                CollapsibleSection s = _order.FirstOrDefault(x => x.Key == k);
                if (s != null && !wanted.Contains(s)) wanted.Add(s);
            }
            foreach (CollapsibleSection s in _order) if (!wanted.Contains(s)) wanted.Add(s);
            _order.Clear();
            _order.AddRange(wanted);
            Relayout();
        }

        /// <summary>Moves one section to an index, as a drag would. Returns false when the key is unknown.</summary>
        public bool MoveTo(string key, int index)
        {
            CollapsibleSection s = _order.FirstOrDefault(x => x.Key == key);
            if (s == null) return false;
            index = Math.Max(0, Math.Min(_order.Count - 1, index));
            if (_order.IndexOf(s) == index) return true;
            _order.Remove(s);
            _order.Insert(index, s);
            Relayout();
            EventHandler h = OrderChanged;
            if (h != null) h(this, EventArgs.Empty);
            return true;
        }

        public void Relayout()
        {
            if (_laying) return;
            _laying = true;
            try
            {
                int y = 0;
                int w = Math.Max(10, ClientSize.Width);
                foreach (CollapsibleSection s in _order)
                {
                    if (s.Left != 0 || s.Top != y || s.Width != w) s.SetBounds(0, y, w, s.Height);
                    y += s.Height + Gap;
                }
                int total = Math.Max(0, y - Gap);
                if (Height != total) Height = total;
            }
            finally { _laying = false; }
        }

        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);
            Relayout();
        }

        // ------------------------------------------------------------------ dragging

        internal void BeginDrag(CollapsibleSection section)
        {
            _dragged = section;
            section.Dragging = true;
            section.BringToFront();
            _edgeScroll.Start();
        }

        internal void DragTo(Point screen)
        {
            if (_dragged == null) return;
            int y = PointToClient(screen).Y;
            int from = _order.IndexOf(_dragged);
            int to = from;
            for (int i = 0; i < _order.Count; i++)
            {
                if (i == from) continue;
                CollapsibleSection other = _order[i];
                int mid = other.Top + other.Height / 2;
                if (i < from && y < mid) { to = i; break; }
                if (i > from && y > mid) to = i;
            }
            if (to == from) return;
            _order.RemoveAt(from);
            _order.Insert(to, _dragged);
            Relayout();
        }

        internal void EndDrag()
        {
            _edgeScroll.Stop();
            if (_dragged == null) return;
            _dragged.Dragging = false;
            _dragged = null;
            EventHandler h = OrderChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        private void EdgeScroll()
        {
            if (_dragged == null) { _edgeScroll.Stop(); return; }
            if ((Control.MouseButtons & MouseButtons.Left) == 0) { EndDrag(); return; }
            ScrollableControl host = null;
            for (Control c = Parent; c != null; c = c.Parent)
            {
                var sc = c as ScrollableControl;
                if (sc != null && sc.AutoScroll) { host = sc; break; }
            }
            if (host == null) return;
            Point p = host.PointToClient(Cursor.Position);
            int step = p.Y < 36 ? -24 : p.Y > host.ClientSize.Height - 36 ? 24 : 0;
            if (step == 0) return;
            Point at = host.AutoScrollPosition;
            host.AutoScrollPosition = new Point(-at.X, Math.Max(0, -at.Y + step));
            DragTo(Cursor.Position);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _edgeScroll.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class SectionEventArgs : EventArgs
    {
        public readonly CollapsibleSection Section;
        public SectionEventArgs(CollapsibleSection section) { Section = section; }
    }
}
