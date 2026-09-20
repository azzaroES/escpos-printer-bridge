using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using UsbLanPrinterBridge.Core;

namespace UsbLanPrinterBridge.UI
{
    /// <summary>Colours shared by the Device tab. Series colours are the validated categorical set; status colours are fixed.</summary>
    internal static class Palette
    {
        public static readonly Color Surface = Color.FromArgb(252, 252, 251);
        public static readonly Color CardBorder = Color.FromArgb(217, 217, 214);
        public static readonly Color Text = Color.FromArgb(11, 11, 11);
        public static readonly Color Muted = Color.FromArgb(82, 81, 78);
        public static readonly Color Grid = Color.FromArgb(230, 230, 227);
        public static readonly Color Axis = Color.FromArgb(207, 207, 203);
        public static readonly Color Series1 = Color.FromArgb(42, 120, 214);
        public static readonly Color Series2 = Color.FromArgb(235, 104, 52);
        public static readonly Color Series3 = Color.FromArgb(27, 175, 122);
        public static readonly Color Series4 = Color.FromArgb(237, 161, 0);
        public static readonly Color Good = Color.FromArgb(12, 163, 12);
        public static readonly Color Warning = Color.FromArgb(250, 178, 25);
        public static readonly Color Serious = Color.FromArgb(236, 131, 90);
        public static readonly Color Critical = Color.FromArgb(208, 59, 59);
        public static readonly Color ChipLiveBack = Color.FromArgb(220, 243, 220), ChipLiveFore = Color.FromArgb(10, 90, 10);
        public static readonly Color ChipEstBack = Color.FromArgb(253, 235, 201), ChipEstFore = Color.FromArgb(122, 74, 0);
        public static readonly Color ChipNaBack = Color.FromArgb(229, 229, 226), ChipNaFore = Color.FromArgb(82, 81, 78);

        public static Color ForEvent(DeviceEventKind k)
        {
            switch (k) { case DeviceEventKind.Printed: return Good; case DeviceEventKind.Warning: return Warning; case DeviceEventKind.Offline: return Serious; default: return Critical; }
        }
    }

    /// <summary>One reading: label, big value, detail line, a LIVE/EST/N/A chip and a sparkline of the last minute.</summary>
    internal sealed class TileControl : Control
    {
        public string Label = "";
        public string Value = "";
        public string Sub = "";
        public ReadingQuality Quality = ReadingQuality.Live;
        public double?[] Spark;

        public TileControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        public void Set(string label, string value, string sub, ReadingQuality q, double?[] spark)
        {
            Label = label; Value = value; Sub = sub; Quality = q; Spark = spark;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            using (var pen = new Pen(Palette.CardBorder)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            using (var small = new Font(Font.FontFamily, 7f))
            using (var big = new Font(Font.FontFamily, 12.5f, FontStyle.Bold))
            using (var muted = new SolidBrush(Palette.Muted))
            using (var text = new SolidBrush(Palette.Text))
            {
                g.DrawString(Label.ToUpperInvariant(), small, muted, 8, 6);
                g.DrawString(Value, big, text, 6, 18);
                g.DrawString(Sub, small, muted, new RectangleF(8, 40, Width - 16, 14), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });

                string chip = Quality == ReadingQuality.Live ? "LIVE" : Quality == ReadingQuality.Estimated ? "EST" : "N/A";
                Color cb = Quality == ReadingQuality.Live ? Palette.ChipLiveBack : Quality == ReadingQuality.Estimated ? Palette.ChipEstBack : Palette.ChipNaBack;
                Color cf = Quality == ReadingQuality.Live ? Palette.ChipLiveFore : Quality == ReadingQuality.Estimated ? Palette.ChipEstFore : Palette.ChipNaFore;
                using (var chipFont = new Font(Font.FontFamily, 6.5f, FontStyle.Bold))
                {
                    SizeF sz = g.MeasureString(chip, chipFont);
                    var r = new RectangleF(Width - sz.Width - 12, 6, sz.Width + 6, sz.Height + 1);
                    using (var b = new SolidBrush(cb)) g.FillRectangle(b, r);
                    using (var b = new SolidBrush(cf)) g.DrawString(chip, chipFont, b, r.X + 3, r.Y);
                }

                if (Spark != null && Spark.Length > 1)
                {
                    double lo = double.MaxValue, hi = double.MinValue;
                    foreach (double? v in Spark) if (v.HasValue) { lo = Math.Min(lo, v.Value); hi = Math.Max(hi, v.Value); }
                    if (hi > lo || hi != double.MinValue)
                    {
                        if (hi <= lo) { hi = lo + 1; }
                        float left = 8, right = Width - 8, top = Height - 20, bottom = Height - 6;
                        var pts = new List<PointF>();
                        using (var pen = new Pen(Palette.Series1, 1.4f))
                        {
                            for (int i = 0; i < Spark.Length; i++)
                            {
                                if (!Spark[i].HasValue) { if (pts.Count > 1) g.DrawLines(pen, pts.ToArray()); pts.Clear(); continue; }
                                float x = left + (right - left) * i / (Spark.Length - 1);
                                float y = (float)(bottom - (Spark[i].Value - lo) / (hi - lo) * (bottom - top));
                                pts.Add(new PointF(x, y));
                            }
                            if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
                        }
                    }
                }
            }
        }
    }

    internal enum SeriesStyle { Line, Area, Bars }

    internal sealed class ChartSeries
    {
        public string Name;
        public Color Color;
        public double?[] Values;
        public SeriesStyle Style = SeriesStyle.Line;
        public string Unit = "";
    }

    internal sealed class ChartMarker
    {
        public double Position;      // 0 = oldest edge, 1 = newest edge
        public DeviceEventKind Kind;
        public string Text;
        public DateTime Time;
    }

    /// <summary>
    /// A small line chart painted by hand: title, legend, recessive grid, thin 2 px lines, an optional threshold,
    /// event markers on the baseline, and a crosshair with a tooltip on hover. Null values leave a gap.
    /// </summary>
    internal sealed class LineChartControl : Control
    {
        public string Title = "";
        public readonly List<ChartSeries> Series = new List<ChartSeries>();
        public double Min = 0, Max = 100;
        public string[] AxisLabels = { "100", "50", "0" };
        public string[] TimeLabels = { "60 s ago", "30 s", "now" };
        public double? Threshold; public string ThresholdLabel = "";
        public readonly List<ChartMarker> Markers = new List<ChartMarker>();
        public Func<int, string> TimeAt;   // index -> label for the tooltip
        public string EmptyText = "";

        private int _hover = -1;
        private int _markerHover = -1;

        public LineChartControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Palette.Surface;
        }

        private const int PadLeft = 34, PadRight = 8, PadTop = 26, PadBottom = 30;
        private RectangleF Plot { get { return new RectangleF(PadLeft, PadTop, Math.Max(10, Width - PadLeft - PadRight), Math.Max(10, Height - PadTop - PadBottom)); } }

        private int Count { get { int n = 0; foreach (ChartSeries s in Series) if (s.Values != null) n = Math.Max(n, s.Values.Length); return n; } }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            RectangleF p = Plot;
            int n = Count;
            int before = _hover, mb = _markerHover;
            _markerHover = -1;
            for (int i = 0; i < Markers.Count; i++)
            {
                float mx = p.Left + (float)(Markers[i].Position * p.Width);
                float my = p.Bottom + 8;
                if (Math.Abs(e.X - mx) <= 8 && Math.Abs(e.Y - my) <= 9) { _markerHover = i; break; }
            }
            _hover = (n > 1 && e.X >= p.Left - 4 && e.X <= p.Right + 4 && e.Y <= p.Bottom + 2 && _markerHover < 0) ? Math.Max(0, Math.Min(n - 1, (int)Math.Round((e.X - p.Left) / p.Width * (n - 1)))) : -1;
            if (before != _hover || mb != _markerHover) Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hover != -1 || _markerHover != -1) { _hover = -1; _markerHover = -1; Invalidate(); }
            base.OnMouseLeave(e);
        }

        public void HighlightMarker(int index) { if (_markerHover != index) { _markerHover = index; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            RectangleF p = Plot;
            using (var titleFont = new Font(Font.FontFamily, 8f, FontStyle.Bold))
            using (var small = new Font(Font.FontFamily, 7f))
            using (var text = new SolidBrush(Palette.Text))
            using (var muted = new SolidBrush(Palette.Muted))
            {
                g.DrawString(Title, titleFont, text, 2, 4);
                float lx = 2 + g.MeasureString(Title, titleFont).Width + 12;
                foreach (ChartSeries s in Series)
                {
                    using (var b = new SolidBrush(s.Color)) g.FillRectangle(b, lx, 11, 12, 2.5f);
                    g.DrawString(s.Name, small, muted, lx + 15, 6);
                    lx += 15 + g.MeasureString(s.Name, small).Width + 10;
                }

                using (var grid = new Pen(Palette.Grid))
                using (var axis = new Pen(Palette.Axis))
                {
                    for (int i = 0; i <= 4; i++)
                    {
                        float y = p.Top + p.Height * i / 4f;
                        g.DrawLine(i == 4 ? axis : grid, p.Left, y, p.Right, y);
                    }
                    if (AxisLabels != null && AxisLabels.Length >= 3)
                    {
                        g.DrawString(AxisLabels[0], small, muted, 2, p.Top - 6);
                        g.DrawString(AxisLabels[1], small, muted, 2, p.Top + p.Height / 2 - 6);
                        g.DrawString(AxisLabels[2], small, muted, 2, p.Bottom - 7);
                    }
                    if (TimeLabels != null && TimeLabels.Length >= 3)
                    {
                        g.DrawString(TimeLabels[0], small, muted, p.Left, p.Bottom + 14);
                        SizeF mid = g.MeasureString(TimeLabels[1], small);
                        g.DrawString(TimeLabels[1], small, muted, p.Left + p.Width / 2 - mid.Width / 2, p.Bottom + 14);
                        SizeF end = g.MeasureString(TimeLabels[2], small);
                        g.DrawString(TimeLabels[2], small, muted, p.Right - end.Width, p.Bottom + 14);
                    }
                }

                if (Threshold.HasValue && Threshold.Value > Min && Threshold.Value < Max)
                {
                    float y = Y(Threshold.Value, p);
                    using (var pen = new Pen(Palette.Critical) { DashStyle = DashStyle.Dash })
                    using (var b = new SolidBrush(Palette.Critical))
                    {
                        g.DrawLine(pen, p.Left, y, p.Right, y);
                        SizeF sz = g.MeasureString(ThresholdLabel, small);
                        g.DrawString(ThresholdLabel, small, b, p.Right - sz.Width, y - sz.Height);
                    }
                }

                int n = Count;
                bool any = false;
                foreach (ChartSeries s in Series)
                {
                    if (s.Values == null || s.Values.Length < 2) continue;
                    if (s.Style == SeriesStyle.Bars) { DrawBars(g, s, p); any = true; continue; }
                    var pts = new List<PointF>();
                    var segments = new List<PointF[]>();
                    for (int i = 0; i < s.Values.Length; i++)
                    {
                        if (!s.Values[i].HasValue) { if (pts.Count > 1) segments.Add(pts.ToArray()); pts.Clear(); continue; }
                        pts.Add(new PointF(X(i, s.Values.Length, p), Y(s.Values[i].Value, p)));
                    }
                    if (pts.Count > 1) segments.Add(pts.ToArray());
                    if (segments.Count == 0 && pts.Count == 1) segments.Add(new[] { pts[0], new PointF(pts[0].X + 1, pts[0].Y) });
                    foreach (PointF[] seg in segments)
                    {
                        any = true;
                        if (s.Style == SeriesStyle.Area)
                        {
                            var path = new GraphicsPath();
                            path.AddLines(seg);
                            path.AddLine(seg[seg.Length - 1], new PointF(seg[seg.Length - 1].X, p.Bottom));
                            path.AddLine(new PointF(seg[seg.Length - 1].X, p.Bottom), new PointF(seg[0].X, p.Bottom));
                            path.CloseFigure();
                            using (var b = new SolidBrush(Color.FromArgb(30, s.Color))) g.FillPath(b, path);
                        }
                        using (var pen = new Pen(s.Color, 2f) { LineJoin = LineJoin.Round }) g.DrawLines(pen, seg);
                    }
                }
                if (!any && EmptyText.Length > 0)
                {
                    SizeF sz = g.MeasureString(EmptyText, small);
                    g.DrawString(EmptyText, small, muted, p.Left + p.Width / 2 - sz.Width / 2, p.Top + p.Height / 2 - sz.Height / 2);
                }

                // event markers on the baseline
                for (int i = 0; i < Markers.Count; i++)
                {
                    ChartMarker m = Markers[i];
                    float mx = p.Left + (float)(m.Position * p.Width), my = p.Bottom + 8;
                    DrawMarker(g, m.Kind, mx, my, i == _markerHover ? 6.5f : 5f);
                }

                if (_markerHover >= 0 && _markerHover < Markers.Count)
                {
                    ChartMarker m = Markers[_markerHover];
                    float mx = p.Left + (float)(m.Position * p.Width);
                    DrawTooltip(g, small, mx, p.Top + 4, new[] { m.Time.ToString("HH:mm:ss") + "  " + m.Text }, null);
                }
                else if (_hover >= 0 && n > 1)
                {
                    float x = X(_hover, n, p);
                    using (var pen = new Pen(Palette.Text) { DashStyle = DashStyle.Dot }) g.DrawLine(pen, x, p.Top - 2, x, p.Bottom);
                    var lines = new List<string>();
                    var colors = new List<Color>();
                    string when = TimeAt != null ? TimeAt(_hover) : "";
                    if (when.Length > 0) { lines.Add(when); colors.Add(Color.Empty); }
                    foreach (ChartSeries s in Series)
                    {
                        if (s.Values == null || _hover >= s.Values.Length) continue;
                        double? v = s.Values[_hover];
                        lines.Add(s.Name + "  " + (v.HasValue ? Format(v.Value) + s.Unit : "no reading"));
                        colors.Add(s.Color);
                        if (v.HasValue && s.Style != SeriesStyle.Bars)
                        {
                            float y = Y(v.Value, p);
                            using (var b = new SolidBrush(s.Color)) g.FillEllipse(b, x - 4, y - 4, 8, 8);
                            using (var pen = new Pen(Palette.Surface, 2f)) g.DrawEllipse(pen, x - 4, y - 4, 8, 8);
                        }
                    }
                    DrawTooltip(g, small, x, p.Top + 4, lines.ToArray(), colors.ToArray());
                }
            }
        }

        private static string Format(double v) { return Math.Abs(v) >= 100 || v == Math.Round(v) ? Math.Round(v).ToString("0") : v.ToString("0.0"); }

        private void DrawBars(Graphics g, ChartSeries s, RectangleF p)
        {
            int n = s.Values.Length;
            float zero = Y(0, p);
            float w = Math.Max(2, p.Width / n - 2);
            for (int i = 0; i < n; i++)
            {
                if (!s.Values[i].HasValue) continue;
                float x = X(i, n, p) - w / 2, y = Y(s.Values[i].Value, p);
                Color c = s.Values[i].Value >= 0 ? Palette.Series1 : Palette.Series2;
                using (var b = new SolidBrush(c)) g.FillRectangle(b, x, Math.Min(y, zero), w, Math.Max(1, Math.Abs(zero - y)));
            }
            using (var axis = new Pen(Palette.Axis)) g.DrawLine(axis, p.Left, zero, p.Right, zero);
        }

        public static void DrawMarker(Graphics g, DeviceEventKind kind, float x, float y, float r)
        {
            using (var b = new SolidBrush(Palette.ForEvent(kind)))
            using (var ring = new Pen(Palette.Surface, 1.5f))
            {
                switch (kind)
                {
                    case DeviceEventKind.Warning:
                        var pts = new[] { new PointF(x, y - r), new PointF(x + r, y), new PointF(x, y + r), new PointF(x - r, y) };
                        g.FillPolygon(b, pts); g.DrawPolygon(ring, pts); break;
                    case DeviceEventKind.Failed:
                        g.FillRectangle(b, x - r + 1, y - r + 1, 2 * r - 2, 2 * r - 2); g.DrawRectangle(ring, x - r + 1, y - r + 1, 2 * r - 2, 2 * r - 2); break;
                    default:
                        g.FillEllipse(b, x - r, y - r, 2 * r, 2 * r); g.DrawEllipse(ring, x - r, y - r, 2 * r, 2 * r); break;
                }
            }
        }

        private void DrawTooltip(Graphics g, Font font, float x, float y, string[] lines, Color[] colors)
        {
            float w = 0, h = 4;
            foreach (string l in lines) { SizeF s = g.MeasureString(l, font); w = Math.Max(w, s.Width); h += s.Height; }
            w += 22;
            float bx = x + 10; if (bx + w > Width - 4) bx = x - w - 10; if (bx < 2) bx = 2;
            var r = new RectangleF(bx, y, w, h);
            using (var b = new SolidBrush(Color.FromArgb(235, 11, 11, 11))) g.FillRectangle(b, r);
            float ly = y + 2;
            for (int i = 0; i < lines.Length; i++)
            {
                if (colors != null && i < colors.Length && colors[i] != Color.Empty) using (var b = new SolidBrush(colors[i])) g.FillRectangle(b, bx + 5, ly + 5, 8, 3);
                g.DrawString(lines[i], font, Brushes.White, bx + 16, ly);
                ly += g.MeasureString(lines[i], font).Height;
            }
        }

        private static float X(int i, int n, RectangleF p) { return p.Left + p.Width * i / Math.Max(1, n - 1); }
        private float Y(double v, RectangleF p) { double f = (v - Min) / (Max - Min); f = Math.Max(0, Math.Min(1, f)); return (float)(p.Bottom - f * p.Height); }
    }

    /// <summary>Rows of on/off bands on the same time axis as the charts: green while connected, a gap while not.</summary>
    internal sealed class StripControl : Control
    {
        public readonly List<KeyValuePair<string, bool?[]>> Rows = new List<KeyValuePair<string, bool?[]>>();
        public string Note = "";

        public StripControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Palette.Surface;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            using (var small = new Font(Font.FontFamily, 7f))
            using (var muted = new SolidBrush(Palette.Muted))
            using (var off = new SolidBrush(Color.FromArgb(239, 239, 236)))
            using (var on = new SolidBrush(Palette.Good))
            using (var none = new SolidBrush(Color.FromArgb(225, 225, 222)))
            {
                float y = 4;
                foreach (KeyValuePair<string, bool?[]> row in Rows)
                {
                    g.DrawString(row.Key, small, muted, 2, y - 1);
                    float left = 70, w = Width - left - 8;
                    g.FillRectangle(off, left, y, w, 9);
                    bool?[] v = row.Value;
                    if (v != null && v.Length > 0)
                    {
                        float step = w / v.Length;
                        for (int i = 0; i < v.Length; i++)
                        {
                            if (!v[i].HasValue) continue;
                            g.FillRectangle(v[i].Value ? on : none, left + i * step, y, step + 0.5f, 9);
                        }
                    }
                    y += 15;
                }
                if (Note.Length > 0) g.DrawString(Note, small, muted, new RectangleF(2, y, Width - 4, Height - y));
            }
        }
    }

    /// <summary>A panel with a front and a back; Flip() squashes one face away and the other in, like a card turning.</summary>
    internal sealed class FlipPanel : Panel
    {
        public readonly Panel Front = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Surface };
        public readonly Panel Back = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Surface, Visible = false };
        private readonly Timer _anim = new Timer { Interval = 15 };
        private Bitmap _shot;
        private int _step;
        private bool _showingBack;

        public bool ShowingBack { get { return _showingBack; } }

        public FlipPanel()
        {
            DoubleBuffered = true;
            Controls.Add(Front);
            Controls.Add(Back);
            _anim.Tick += (s, e) => Animate();
        }

        public void Flip()
        {
            if (_anim.Enabled) return;
            Control from = _showingBack ? Back : Front;
            try
            {
                _shot = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
                from.DrawToBitmap(_shot, new Rectangle(0, 0, Width, Height));
            }
            catch { _shot = null; }
            Front.Visible = Back.Visible = false;
            _step = 0;
            _anim.Start();
        }

        private void Animate()
        {
            _step++;
            if (_step == 8)
            {
                _showingBack = !_showingBack;
                Control to = _showingBack ? Back : Front;
                to.Visible = true;
                try { _shot = new Bitmap(Math.Max(1, Width), Math.Max(1, Height)); to.DrawToBitmap(_shot, new Rectangle(0, 0, Width, Height)); } catch { _shot = null; }
                to.Visible = false;
            }
            if (_step >= 16)
            {
                _anim.Stop();
                (_showingBack ? Back : Front).Visible = true;
                if (_shot != null) { _shot.Dispose(); _shot = null; }
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!_anim.Enabled || _shot == null) return;
            // 0..8 squash to the middle, 8..16 grow back out
            float scale = _step < 8 ? 1f - _step / 8f : (_step - 8) / 8f;
            float w = Math.Max(1, Width * scale);
            e.Graphics.Clear(Palette.Surface);
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            e.Graphics.DrawImage(_shot, new RectangleF((Width - w) / 2, 0, w, Height));
        }
    }

    /// <summary>The Device tab: what this PC can tell about itself, the printers on its ports, the battery, and cooling.</summary>
    public sealed class DeviceTab : UserControl
    {
        private DeviceMonitor _monitor;
        private CoolingControl _cooling;
        private readonly Timer _refresh = new Timer { Interval = 1000 };

        private readonly TileControl _tUsb = new TileControl(), _tNet = new TileControl(), _tBt = new TileControl(), _tCpu = new TileControl(), _tRam = new TileControl(), _tGpu = new TileControl(), _tTemp = new TileControl();
        private readonly LineChartControl _load = new LineChartControl(), _temps = new LineChartControl(), _batLevel = new LineChartControl(), _batMa = new LineChartControl();
        private readonly StripControl _strips = new StripControl();
        private readonly ListView _events = new ListView();
        private readonly Label _telemetryInfo = new Label();
        private readonly FlipPanel _flip = new FlipPanel();
        private readonly Label _batBig = new Label(), _batState = new Label(), _batNone = new Label();
        private readonly Button _flipButton = new Button();
        private readonly Dictionary<string, Label> _batBack = new Dictionary<string, Label>();
        private readonly Label _batChip = new Label();
        private NumericUpDown _fanMin, _capPct, _capMin;
        private Button _fanStart, _capStart;
        private ProgressBar _fanBar, _capBar;
        private Label _fanStatus, _capStatus;
        private readonly Label _cardHint = new Label();

        private static readonly string[] BackKeys = { "State", "Power source", "Current now", "Power now", "Voltage", "Temperature", "Time remaining", "Design capacity", "Full charge capacity", "Wear", "Cycle count", "Chemistry", "Manufacturer", "Charge rate limit" };

        public DeviceTab()
        {
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(244, 244, 242);
            Font = SystemFonts.MessageBoxFont;
            BuildUi();
            _refresh.Tick += (s, e) => RefreshFromMonitor();
        }

        public void Attach(DeviceMonitor monitor, CoolingControl cooling)
        {
            _monitor = monitor;
            _cooling = cooling;
            _refresh.Start();
            RefreshFromMonitor();
        }

        private Panel _scroll;
        private SectionColumn _cards, _sysColumn, _batColumn, _coolColumn;
        private CollapsibleSection _secSys, _secBat, _secCool;
        private readonly Dictionary<string, CollapsibleSection> _sections = new Dictionary<string, CollapsibleSection>();
        private readonly List<SectionColumn> _columns = new List<SectionColumn>();
        private ISectionStore _store;

        /// <summary>Selecting the tab hands focus to a control deep in the page, which scrolls it out of place; put it back.</summary>
        public void ScrollToTop()
        {
            if (_scroll != null) _scroll.AutoScrollPosition = new Point(0, 0);
        }

        // ------------------------------------------------------------------ sections: fold, drag, remember

        /// <summary>Restores which sections are folded and the order they were dragged into, then keeps the store up to date.</summary>
        public void AttachStore(ISectionStore store)
        {
            _store = null;
            if (store != null)
            {
                foreach (SectionColumn c in _columns) c.ApplyOrder(store.GetOrder(c.Group));
                foreach (CollapsibleSection s in _sections.Values) s.Collapsed = store.IsCollapsed(s.Key);
            }
            _store = store;
        }

        /// <summary>Folds or unfolds a section by key; returns whether it is folded now.</summary>
        public bool ToggleSection(string key)
        {
            CollapsibleSection s;
            if (!_sections.TryGetValue(key, out s)) return false;
            s.Collapsed = !s.Collapsed;
            return s.Collapsed;
        }

        public bool IsSectionCollapsed(string key) { CollapsibleSection s; return _sections.TryGetValue(key, out s) && s.Collapsed; }
        public int SectionHeight(string key) { CollapsibleSection s; return _sections.TryGetValue(key, out s) ? s.Height : -1; }
        public string[] SectionKeys { get { return _sections.Keys.ToArray(); } }
        public string[] SectionGroups { get { return _columns.Select(c => c.Group).ToArray(); } }

        public string[] SectionOrder(string group)
        {
            SectionColumn c = _columns.FirstOrDefault(x => x.Group == group);
            return c == null ? new string[0] : c.Keys;
        }

        /// <summary>Moves a section to an index within its group, exactly as dragging its grip does.</summary>
        public bool MoveSection(string group, string key, int index)
        {
            SectionColumn c = _columns.FirstOrDefault(x => x.Group == group);
            return c != null && c.MoveTo(key, index);
        }

        private CollapsibleSection Section(string key, string title, bool card, int bodyHeight, Control content)
        {
            var s = new CollapsibleSection(key, title, card, bodyHeight);
            if (content != null) { content.Dock = DockStyle.Fill; s.Body.Controls.Add(content); }
            _sections[key] = s;
            return s;
        }

        private SectionColumn Column(string group, int gap)
        {
            var c = new SectionColumn(group) { Gap = gap };
            c.SectionToggled += (s, e) => { if (_store != null) _store.SetCollapsed(e.Section.Key, e.Section.Collapsed); };
            c.OrderChanged += (s, e) => { if (_store != null) _store.SetOrder(c.Group, c.Keys); };
            _columns.Add(c);
            return c;
        }

        private Label Chip(string text, Color back, Color fore)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font(Font.FontFamily, 6.5f, FontStyle.Bold), Padding = new Padding(3, 1, 3, 1), BackColor = back, ForeColor = fore };
        }

        // ------------------------------------------------------------------ layout

        private void BuildUi()
        {
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };
            _scroll = scroll;
            scroll.VisibleChanged += (s, e) => { if (scroll.Visible) BeginInvoke(new Action(ScrollToTop)); };

            // ---- System card: a column of blocks, each foldable and draggable
            _sysColumn = Column("sys", 4);

            var tiles = new TableLayoutPanel { ColumnCount = 7, Margin = new Padding(0), Padding = new Padding(0, 4, 0, 0) };
            for (int i = 0; i < 7; i++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 7));
            foreach (TileControl t in new[] { _tUsb, _tNet, _tBt, _tCpu, _tRam, _tGpu, _tTemp }) { t.Dock = DockStyle.Fill; t.Margin = new Padding(0, 0, 6, 0); tiles.Controls.Add(t); }
            _sysColumn.Add(Section("sys.tiles", "Tiles", false, 80, tiles));

            _load.Title = ""; _load.Margin = new Padding(0);
            _load.Series.Add(new ChartSeries { Name = "CPU", Color = Palette.Series1, Unit = " %" });
            _load.Series.Add(new ChartSeries { Name = "RAM", Color = Palette.Series2, Unit = " %" });
            _load.Series.Add(new ChartSeries { Name = "GPU", Color = Palette.Series3, Unit = " %" });
            _load.Series.Add(new ChartSeries { Name = "Wi-Fi signal", Color = Palette.Series4, Unit = " %" });
            _load.TimeAt = SecondsAgo;
            _sysColumn.Add(Section("sys.load", "Load, last 60 s (%)  ·  markers underneath are printer events", false, 190, _load));

            _temps.Title = ""; _temps.Margin = new Padding(0);
            _temps.Series.Add(new ChartSeries { Name = "CPU", Color = Palette.Series1, Unit = " °C" });
            _temps.Series.Add(new ChartSeries { Name = "GPU", Color = Palette.Series2, Unit = " °C" });
            _temps.Series.Add(new ChartSeries { Name = "Battery", Color = Palette.Series3, Unit = " °C" });
            _temps.Threshold = 85; _temps.ThresholdLabel = "throttle 85°";
            _temps.TimeAt = SecondsAgo;
            _temps.EmptyText = "No temperature sensor this PC exposes to programs (ACPI thermal zone, nvidia-smi)";
            _sysColumn.Add(Section("sys.temps", "Temperatures, last 60 s (°C)", false, 190, _temps));

            _strips.Note = "Green = connected; a gap is a drop-out.";
            _sysColumn.Add(Section("sys.strips", "USB and Bluetooth printers, last 60 s", false, 48, _strips));

            _events.View = View.Details; _events.FullRowSelect = true; _events.HeaderStyle = ColumnHeaderStyle.None; _events.Dock = DockStyle.Fill; _events.BorderStyle = BorderStyle.None; _events.BackColor = Palette.Surface; _events.Margin = new Padding(0);
            _events.Columns.Add("Time", 62); _events.Columns.Add("", 18); _events.Columns.Add("Event", 600);
            _events.OwnerDraw = true;
            _events.DrawColumnHeader += (s, e) => e.DrawDefault = true;
            _events.DrawSubItem += DrawEventSubItem;
            _events.MouseMove += (s, e) => { ListViewItem it = _events.GetItemAt(e.X, e.Y); _load.HighlightMarker(it == null ? -1 : it.Index); };
            _events.MouseLeave += (s, e) => _load.HighlightMarker(-1);
            var evBox = new Panel { Margin = new Padding(0) };
            var evLegend = new Label { Dock = DockStyle.Top, Height = 18, ForeColor = Palette.Muted, Font = new Font(Font.FontFamily, 7.5f), Text = "Same timeline as the load graph: ● printed  ◆ cut removed, NO CUT, cooling  ● offline / paper out / ready  ■ failed. Hover a row to find its marker.", Padding = new Padding(0, 2, 0, 0) };
            evBox.Controls.Add(_events);
            evBox.Controls.Add(evLegend);
            _sysColumn.Add(Section("sys.events", "Latest printer events", false, 170, evBox));

            var telemetry = new Panel { Margin = new Padding(0), Padding = new Padding(0, 2, 0, 0) };
            _telemetryInfo.Dock = DockStyle.Top; _telemetryInfo.AutoSize = false; _telemetryInfo.Height = 34; _telemetryInfo.ForeColor = Palette.Muted; _telemetryInfo.Font = new Font(Font.FontFamily, 7.5f);
            var tButtons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, Margin = new Padding(0) };
            tButtons.Controls.Add(Btn("Open log folder", (s, e) => OpenFolder(DeviceMonitor.TelemetryDirectory)));
            tButtons.Controls.Add(Btn("Clear telemetry logs…", (s, e) => ClearTelemetry()));
            telemetry.Controls.Add(tButtons);
            telemetry.Controls.Add(_telemetryInfo);
            _sysColumn.Add(Section("sys.telemetry", "Telemetry log", false, 70, telemetry));

            _secSys = Section("sys", "System", true, 0, null);
            var legend = new Label { Dock = DockStyle.Top, Height = 18, ForeColor = Palette.Muted, Font = new Font(Font.FontFamily, 7.5f), Padding = new Padding(0, 4, 0, 0), Text = "LIVE = read directly     EST = best effort, may be missing on some PCs     N/A = Windows gives an app no such reading     ·     sampled every second, hover a graph for values" };
            _secSys.Body.Controls.Add(legend);
            _secSys.Body.Controls.Add(_sysColumn);
            Action sizeSys = () => _secSys.BodyHeight = _sysColumn.Height + 20;
            _sysColumn.SizeChanged += (s, e) => sizeSys();
            sizeSys();

            // ---- Battery card
            _batColumn = Column("bat", 4);
            _batLevel.Title = ""; _batLevel.TimeLabels = new[] { "60 min ago", "30 min", "now" }; _batLevel.TimeAt = MinutesAgo;
            _batLevel.Series.Add(new ChartSeries { Name = "level", Color = Palette.Series1, Style = SeriesStyle.Area, Unit = " %" });
            _batLevel.EmptyText = "Collecting: one point per minute while the bridge runs";
            _batColumn.Add(Section("bat.level", "Charge, last 60 min (%)", false, 138, _batLevel));
            _batMa.Title = ""; _batMa.Min = -3000; _batMa.Max = 3000; _batMa.AxisLabels = new[] { "+3 A", "0", "−3 A" }; _batMa.TimeLabels = new[] { "60 min ago", "30 min", "now" }; _batMa.TimeAt = MinutesAgo;
            _batMa.Series.Add(new ChartSeries { Name = "current", Color = Palette.Series1, Style = SeriesStyle.Bars, Unit = " mA" });
            _batColumn.Add(Section("bat.current", "Current, last 60 min (mA, + charging / − discharging)", false, 120, _batMa));

            _secBat = Section("battery", "Battery", true, 0, null);
            _batChip.AutoSize = true; _batChip.Font = new Font(Font.FontFamily, 6.5f, FontStyle.Bold); _batChip.Padding = new Padding(3, 1, 3, 1); _batChip.BackColor = Palette.ChipLiveBack; _batChip.ForeColor = Palette.ChipLiveFore; _batChip.Text = "LIVE";
            _secBat.AddHeaderControl(_batChip);
            _batChip.Margin = new Padding(6, 6, 0, 0);
            _flipButton.Text = "↻ Flip: details"; _flipButton.AutoSize = true; _flipButton.AutoSizeMode = AutoSizeMode.GrowAndShrink; _flipButton.MinimumSize = new Size(0, 22);
            _flipButton.Click += (s, e) => { _flip.Flip(); _flipButton.Text = _flip.ShowingBack ? "↻ Flip: details" : "↻ Flip: graphs"; };
            _secBat.AddHeaderControl(_flipButton);
            _flipButton.Margin = new Padding(8, 0, 0, 0);
            _flip.Dock = DockStyle.Top;
            // front
            _batBig.Font = new Font(Font.FontFamily, 20f, FontStyle.Bold); _batBig.AutoSize = true; _batBig.Location = new Point(0, 2);
            _batState.AutoSize = true; _batState.ForeColor = Palette.Muted; _batState.Location = new Point(100, 14);
            _batNone.AutoSize = false; _batNone.Dock = DockStyle.Fill; _batNone.TextAlign = ContentAlignment.MiddleCenter; _batNone.ForeColor = Palette.Muted; _batNone.Visible = false; _batNone.Text = "No battery in this PC.";
            var frontTop = new Panel { Dock = DockStyle.Top, Height = 40 };
            frontTop.Controls.Add(_batBig); frontTop.Controls.Add(_batState);
            _flip.Front.Controls.Add(_batNone); _flip.Front.Controls.Add(_batColumn); _flip.Front.Controls.Add(frontTop);
            // back
            var backTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 7, Padding = new Padding(0, 4, 0, 0) };
            backTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22)); backTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28)); backTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22)); backTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            for (int i = 0; i < BackKeys.Length; i++)
            {
                int col = (i / 7) * 2, r = i % 7;
                backTable.Controls.Add(new Label { Text = BackKeys[i], ForeColor = Palette.Muted, AutoSize = true, Margin = new Padding(0, 4, 0, 0) }, col, r);
                var v = new Label { Text = "—", AutoSize = true, Margin = new Padding(0, 4, 0, 0), Font = new Font(Font, FontStyle.Bold) };
                _batBack[BackKeys[i]] = v;
                backTable.Controls.Add(v, col + 1, r);
            }
            _flip.Back.Controls.Add(backTable);
            _flip.Back.Controls.Add(new Label { Dock = DockStyle.Bottom, Height = 30, ForeColor = Palette.Muted, Font = new Font(Font.FontFamily, 7.5f), Text = "Source: the Windows battery classes (BatteryStatus, BatteryStaticData, BatteryFullChargedCapacity, BatteryCycleCount) and SystemInformation.PowerStatus. Fields the laptop does not report show —." });
            _flip.Back.Controls.Add(new Label { Dock = DockStyle.Top, Height = 18, Text = "Everything Windows reports about this battery", Font = new Font(Font, FontStyle.Bold) });
            _secBat.Body.Controls.Add(_flip);
            Action sizeBat = () => { _flip.Height = Math.Max(44 + _batColumn.Height, 226); _secBat.BodyHeight = _flip.Height + 4; };
            _batColumn.SizeChanged += (s, e) => sizeBat();
            sizeBat();

            // ---- Cooling card
            _coolColumn = Column("cool", 4);
            _coolColumn.Add(Section("cool.fans", "Fans to max for a while", false, 122, BuildCoolingGroup(true)));
            _coolColumn.Add(Section("cool.cap", "Throttle the CPU for a while", false, 122, BuildCoolingGroup(false)));
            _secCool = Section("cooling", "Cooling", true, 0, null);
            Label est = Chip("EST", Palette.ChipEstBack, Palette.ChipEstFore);
            _secCool.AddHeaderControl(est);
            est.Margin = new Padding(6, 6, 0, 0);
            _cardHint.Dock = DockStyle.Top; _cardHint.Height = 34; _cardHint.ForeColor = Palette.Muted; _cardHint.Font = new Font(Font.FontFamily, 7.5f); _cardHint.Padding = new Padding(0, 4, 0, 0);
            _cardHint.Text = "Both actions and their restore are written to the Printer actions tab and the event timeline. The plan is restored when the timer ends, on Stop, and when the bridge exits.";
            _secCool.Body.Controls.Add(_cardHint);
            _secCool.Body.Controls.Add(_coolColumn);
            Action sizeCool = () => _secCool.BodyHeight = _coolColumn.Height + 38;
            _coolColumn.SizeChanged += (s, e) => sizeCool();
            sizeCool();

            // ---- the three cards, themselves foldable and draggable
            _cards = Column("device", 8);
            _cards.Add(_secSys);
            _cards.Add(_secBat);
            _cards.Add(_secCool);
            var foot = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Palette.Muted, Font = new Font(Font.FontFamily, 7.5f), Padding = new Padding(2, 6, 0, 0), Text = "Click a heading to fold it; drag ≡ to move a card or a block inside it. Both are remembered." };
            scroll.Controls.Add(foot);
            scroll.Controls.Add(_cards);
            Controls.Add(scroll);
        }

        private Panel BuildCoolingGroup(bool fan)
        {
            var box = new Panel { Dock = DockStyle.Top, Height = 118, BackColor = Color.White, Padding = new Padding(8, 6, 8, 6), Margin = new Padding(0) };
            box.Paint += (s, e) => { using (var pen = new Pen(Palette.CardBorder)) e.Graphics.DrawRectangle(pen, 0, 0, box.Width - 1, box.Height - 1); };
            var line = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 28, WrapContents = false, Margin = new Padding(0) };
            if (fan)
            {
                line.Controls.Add(new Label { Text = "Fans to max for", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 6, 4, 0) });
                _fanMin = new NumericUpDown { Minimum = 1, Maximum = 240, Value = 10, Width = 48, Margin = new Padding(0, 3, 4, 0) };
                line.Controls.Add(_fanMin);
                line.Controls.Add(new Label { Text = "min", AutoSize = true, Margin = new Padding(0, 6, 12, 0) });
                _fanStart = Btn("Start", (s, e) => ToggleFan());
                line.Controls.Add(_fanStart);
            }
            else
            {
                line.Controls.Add(new Label { Text = "Throttle: CPU at", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 6, 4, 0) });
                _capPct = new NumericUpDown { Minimum = 5, Maximum = 100, Value = 50, Increment = 5, Width = 48, Margin = new Padding(0, 3, 4, 0) };
                line.Controls.Add(_capPct);
                line.Controls.Add(new Label { Text = "% for", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
                _capMin = new NumericUpDown { Minimum = 1, Maximum = 240, Value = 15, Width = 48, Margin = new Padding(0, 3, 4, 0) };
                line.Controls.Add(_capMin);
                line.Controls.Add(new Label { Text = "min", AutoSize = true, Margin = new Padding(0, 6, 12, 0) });
                _capStart = Btn("Start", (s, e) => ToggleCap());
                line.Controls.Add(_capStart);
            }
            var bar = new ProgressBar { Dock = DockStyle.Top, Height = 6, Margin = new Padding(0), Style = ProgressBarStyle.Continuous };
            var status = new Label { Dock = DockStyle.Top, Height = 16, ForeColor = Palette.Muted, Font = new Font(Font.FontFamily, 7.5f) };
            var how = new Label { Dock = DockStyle.Fill, ForeColor = Palette.Muted, Font = new Font(Font.FontFamily, 7.5f) };
            how.Text = fan
                ? "How: Windows has no general fan API. The bridge sets the plan's system cooling policy to Active and the processor to 100 %, which makes most laptops spin the fans up. Direct RPM control only exists in the maker's own tool."
                : "How: the plan's maximum processor state is set to the cap (powercfg), so the laptop cools down and stays quiet.";
            if (fan) { _fanBar = bar; _fanStatus = status; } else { _capBar = bar; _capStatus = status; }
            box.Controls.Add(how);
            box.Controls.Add(status);
            box.Controls.Add(bar);
            box.Controls.Add(line);
            return box;
        }

        private static Button Btn(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 0, 6, 0), MinimumSize = new Size(0, 24), Margin = new Padding(0, 2, 6, 0) };
            b.Click += onClick;
            return b;
        }

        private static string SecondsAgo(int i) { int ago = DeviceMonitor.HistorySeconds - 1 - i; return ago == 0 ? "now" : ago + " s ago"; }
        private static string MinutesAgo(int i) { int ago = DeviceMonitor.HistoryMinutes - 1 - i; return ago == 0 ? "now" : ago + " min ago"; }

        private void DrawEventSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            var ev = e.Item.Tag as DeviceEvent;
            e.DrawBackground();
            if (e.ColumnIndex == 1 && ev != null)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                LineChartControl.DrawMarker(e.Graphics, ev.Kind, e.Bounds.Left + 8, e.Bounds.Top + e.Bounds.Height / 2f, 4.5f);
                return;
            }
            using (var b = new SolidBrush(e.ColumnIndex == 0 ? Palette.Muted : Palette.Text))
                e.Graphics.DrawString(e.SubItem.Text, _events.Font, b, e.Bounds, new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center });
        }

        // ------------------------------------------------------------------ refresh

        private void RefreshFromMonitor()
        {
            if (_monitor == null || IsDisposed) return;
            DeviceSnapshot s = _monitor.Snapshot();
            if (s == null || s.Cpu == null) return;

            _tUsb.Set("USB", s.UsbPrinters == 0 ? "none" : s.UsbPrintersOnline + " of " + s.UsbPrinters + " online",
                s.UsbPrinters == 0 ? "no USB printer installed" : Short(s.UsbPrinterNames, 34) + " · " + s.BridgeKBps.ToString("0.0") + " KB/s through the bridge", ReadingQuality.Live, ToSpark(s.UsbOn));
            _tNet.Set(s.NetLabel, s.WifiSignalPercent.HasValue ? s.WifiSignalPercent + " %" : (s.NetUp ? "up" : "down"),
                (s.WifiSsid.Length > 0 ? s.WifiSsid + " · " : "") + (s.WifiSignalDbm.HasValue ? s.WifiSignalDbm + " dBm · " : "") + (s.WifiLinkMbps.HasValue ? s.WifiLinkMbps + " Mbps · " : "") + "↓" + Rate(s.NetRxKBps) + " ↑" + Rate(s.NetTxKBps),
                ReadingQuality.Live, s.Wifi);
            _tBt.Set("Bluetooth", s.BtPrinters == 0 ? "none" : s.BtPrintersOnline + " of " + s.BtPrinters + " online", s.BtPrinters == 0 ? "no printer on a Bluetooth port" : Short(s.BtPrinterNames, 40), ReadingQuality.Live, ToSpark(s.BtOn));
            _tCpu.Set("Processor", s.CpuQuality == ReadingQuality.NotAvailable ? "—" : Math.Round(s.CpuPercent) + " %", s.Cores + " cores" + (s.CpuGhz > 0 ? " · " + s.CpuGhz.ToString("0.0") + " GHz" : ""), s.CpuQuality, s.Cpu);
            _tRam.Set("RAM", Math.Round(s.RamPercent) + " %", s.RamUsedGb.ToString("0.0") + " / " + s.RamTotalGb.ToString("0.0") + " GB", ReadingQuality.Live, s.Ram);
            _tGpu.Set("Video (GPU)", s.GpuPercent.HasValue ? Math.Round(s.GpuPercent.Value) + " %" : "—", (s.GpuName.Length > 0 ? Short(s.GpuName, 26) : "no GPU counters (Windows 10 1709+)") + (s.GpuMemGb.HasValue ? " · " + s.GpuMemGb.Value.ToString("0.0") + " GB used" : ""), s.GpuQuality, s.Gpu);
            _tTemp.Set("Temperatures", s.CpuTempC.HasValue ? Math.Round(s.CpuTempC.Value) + " °C" : (s.GpuTempC.HasValue ? Math.Round(s.GpuTempC.Value) + " °C GPU" : "—"),
                s.TempQuality == ReadingQuality.NotAvailable ? "no sensor exposed to programs" : (s.CpuTempC.HasValue ? "CPU" : "") + (s.GpuTempC.HasValue ? " · GPU " + Math.Round(s.GpuTempC.Value) + " °C" : "") + (s.BatteryTempC.HasValue ? " · battery " + s.BatteryTempC.Value.ToString("0.0") + " °C" : "") + " · " + s.TempSource,
                s.TempQuality, s.CpuTempC.HasValue ? s.TCpu : s.TGpu);

            _load.Series[0].Values = s.Cpu; _load.Series[1].Values = s.Ram; _load.Series[2].Values = s.Gpu; _load.Series[3].Values = s.Wifi;
            _temps.Series[0].Values = s.TCpu; _temps.Series[1].Values = s.TGpu; _temps.Series[2].Values = s.TBat;
            _load.Markers.Clear();
            DateTime now = s.Time;
            foreach (DeviceEvent ev in s.Events)
            {
                double age = (now - ev.Time).TotalSeconds;
                if (age < 0 || age > DeviceMonitor.HistorySeconds) continue;
                _load.Markers.Add(new ChartMarker { Position = 1 - age / DeviceMonitor.HistorySeconds, Kind = ev.Kind, Text = ev.Text, Time = ev.Time });
            }
            _load.Invalidate(); _temps.Invalidate();

            _strips.Rows.Clear();
            _strips.Rows.Add(new KeyValuePair<string, bool?[]>("USB printer", s.UsbOn));
            _strips.Rows.Add(new KeyValuePair<string, bool?[]>("Bluetooth", s.BtOn));
            _strips.Invalidate();

            RefreshEvents(s.Events);

            _telemetryInfo.Text = "Every tile, both graphs and every event go to " + Path.GetFileName(s.TelemetryFile) + " in the log folder, one row every " + DeviceMonitor.TelemetryEverySeconds
                + " s with a full timestamp, kept " + DeviceMonitor.KeepTelemetryDays + " days. Today: " + BridgeListener.FormatBytes(s.TelemetryBytesToday) + ", " + s.Events.Count + " event(s); " + s.TelemetryFiles + " file(s).";

            // what each folded section says in its heading
            string temp = s.CpuTempC.HasValue ? Math.Round(s.CpuTempC.Value) + " °C" : s.GpuTempC.HasValue ? Math.Round(s.GpuTempC.Value) + " °C GPU" : "";
            _secSys.Summary = (s.CpuQuality == ReadingQuality.NotAvailable ? "" : "CPU " + Math.Round(s.CpuPercent) + " %  ·  ") + "RAM " + Math.Round(s.RamPercent) + " %"
                + (s.GpuPercent.HasValue ? "  ·  GPU " + Math.Round(s.GpuPercent.Value) + " %" : "") + (temp.Length > 0 ? "  ·  " + temp : "") + "  ·  " + s.Events.Count + " event(s)";
            _sections["sys.tiles"].Summary = "USB " + s.UsbPrintersOnline + "/" + s.UsbPrinters + "  ·  " + s.NetLabel + (s.WifiSignalPercent.HasValue ? " " + s.WifiSignalPercent + " %" : "") + "  ·  BT " + s.BtPrintersOnline + "/" + s.BtPrinters;
            _sections["sys.load"].Summary = (s.CpuQuality == ReadingQuality.NotAvailable ? "" : "CPU " + Math.Round(s.CpuPercent) + " %  ·  ") + "RAM " + Math.Round(s.RamPercent) + " %" + (s.GpuPercent.HasValue ? "  ·  GPU " + Math.Round(s.GpuPercent.Value) + " %" : "");
            _sections["sys.temps"].Summary = temp.Length > 0 ? temp : "no sensor exposed";
            _sections["sys.strips"].Summary = "USB " + s.UsbPrintersOnline + " of " + s.UsbPrinters + " online  ·  Bluetooth " + s.BtPrintersOnline + " of " + s.BtPrinters;
            _sections["sys.events"].Summary = s.Events.Count == 0 ? "none yet" : s.Events.Count + " event(s)  ·  last " + s.Events[0].Time.ToString("HH:mm:ss") + "  " + Short(s.Events[0].Text, 70);
            _sections["sys.telemetry"].Summary = Path.GetFileName(s.TelemetryFile) + "  ·  " + BridgeListener.FormatBytes(s.TelemetryBytesToday) + " today";

            RefreshBattery(s);
            RefreshCooling();
        }

        private void RefreshEvents(List<DeviceEvent> events)
        {
            int show = Math.Min(events.Count, 30);
            _events.BeginUpdate();
            while (_events.Items.Count > show) _events.Items.RemoveAt(_events.Items.Count - 1);
            for (int i = 0; i < show; i++)
            {
                DeviceEvent ev = events[i];
                if (i < _events.Items.Count)
                {
                    ListViewItem it = _events.Items[i];
                    if (ReferenceEquals(it.Tag, ev)) continue;
                    it.Tag = ev; it.SubItems[0].Text = ev.Time.ToString("HH:mm:ss"); it.SubItems[2].Text = ev.Text;
                }
                else
                {
                    var it = new ListViewItem(new[] { ev.Time.ToString("HH:mm:ss"), "", ev.Text }) { Tag = ev };
                    _events.Items.Add(it);
                }
            }
            if (_events.Columns.Count == 3) _events.Columns[2].Width = Math.Max(200, _events.ClientSize.Width - 84);
            _events.EndUpdate();
        }

        private void RefreshBattery(DeviceSnapshot s)
        {
            BatteryInfo b = s.Battery ?? new BatteryInfo();
            bool present = b.Present;
            _batNone.Visible = !present;
            _batBig.Visible = _batState.Visible = _batColumn.Visible = present;
            _flipButton.Enabled = present;
            _batChip.Text = present ? "LIVE" : "N/A";
            _batChip.BackColor = present ? Palette.ChipLiveBack : Palette.ChipNaBack;
            _batChip.ForeColor = present ? Palette.ChipLiveFore : Palette.ChipNaFore;
            _secBat.Summary = present ? b.Percent + " %  ·  " + b.StateText : "no battery in this PC";
            if (!present) return;
            _batBig.Text = b.Percent + "%";
            _batState.Text = b.StateText;
            _batState.Location = new Point(_batBig.Right + 8, 14);
            _batLevel.Series[0].Values = s.BatteryLevelByMinute;
            _batMa.Series[0].Values = s.BatteryMaByMinute;
            _batLevel.Invalidate(); _batMa.Invalidate();

            _batBack["State"].Text = b.StateText;
            _batBack["Power source"].Text = b.SourceText;
            _batBack["Current now"].Text = b.MilliAmps.HasValue ? (b.MilliAmps > 0 ? "+" : "") + Math.Round(b.MilliAmps.Value) + " mA" : "—";
            _batBack["Power now"].Text = b.Watts.HasValue ? Math.Abs(b.Watts.Value).ToString("0.0") + " W" : "—";
            _batBack["Voltage"].Text = b.Volts.HasValue ? b.Volts.Value.ToString("0.00") + " V" : "—";
            _batBack["Temperature"].Text = b.TempC.HasValue ? b.TempC.Value.ToString("0.0") + " °C" : "not reported";
            _batBack["Time remaining"].Text = b.RemainingText.Length > 0 ? b.RemainingText : "—";
            _batBack["Design capacity"].Text = b.DesignMwh.HasValue ? b.DesignMwh.Value.ToString("N0") + " mWh" : "—";
            _batBack["Full charge capacity"].Text = b.FullMwh.HasValue ? b.FullMwh.Value.ToString("N0") + " mWh" : "—";
            _batBack["Wear"].Text = b.WearPercent.HasValue ? b.WearPercent.Value.ToString("0.0") + " %" : "—";
            _batBack["Cycle count"].Text = b.CycleCount.HasValue && b.CycleCount > 0 ? b.CycleCount.Value.ToString() : "not reported";
            _batBack["Chemistry"].Text = b.Chemistry.Length > 0 ? b.Chemistry : "—";
            _batBack["Manufacturer"].Text = b.Manufacturer.Length > 0 ? b.Manufacturer : "—";
            _batBack["Charge rate limit"].Text = "Windows managed";
        }

        private void RefreshCooling()
        {
            if (_cooling == null) return;
            bool fan = _cooling.FanBoostActive;
            _fanStart.Text = fan ? "Stop · " + LeftText(_cooling.FanBoostLeft) + " left" : "Start";
            _fanMin.Enabled = !fan;
            _fanBar.Value = fan ? (int)Math.Max(0, Math.Min(100, 100 * _cooling.FanBoostLeft.TotalSeconds / Math.Max(1, _cooling.FanBoostMinutes * 60))) : 0;
            _fanStatus.Text = fan ? "Cooling policy: Active · processor 100 % · fans ramping. Restored automatically at " + _cooling.FanBoostUntil.ToString("HH:mm") + "." : (_cooling.IsElevated ? "Cooling policy as set in the power plan. Not running." : "Needs administrator rights (restart the bridge as administrator).");
            bool cap = _cooling.ThrottleActive;
            _capStart.Text = cap ? "Stop · " + LeftText(_cooling.ThrottleLeft) + " left" : "Start";
            _capPct.Enabled = _capMin.Enabled = !cap;
            _capBar.Value = cap ? (int)Math.Max(0, Math.Min(100, 100 * _cooling.ThrottleLeft.TotalSeconds / Math.Max(1, _cooling.ThrottleMinutes * 60))) : 0;
            _capStatus.Text = cap ? "Maximum processor state " + _cooling.ThrottlePercent + " % until " + _cooling.ThrottleUntil.ToString("HH:mm") + "." : (_cooling.IsElevated ? "Maximum processor state 100 %. Not throttled." : "Needs administrator rights.");
            _sections["cool.fans"].Summary = fan ? "running  ·  " + LeftText(_cooling.FanBoostLeft) + " left" : "not running";
            _sections["cool.cap"].Summary = cap ? "CPU at " + _cooling.ThrottlePercent + " %  ·  " + LeftText(_cooling.ThrottleLeft) + " left" : "not throttled";
            _secCool.Summary = fan ? "fans to max  ·  " + LeftText(_cooling.FanBoostLeft) + " left" : cap ? "CPU capped at " + _cooling.ThrottlePercent + " %  ·  " + LeftText(_cooling.ThrottleLeft) + " left" : "not running";
        }

        private static string LeftText(TimeSpan t) { return (int)t.TotalMinutes + ":" + t.Seconds.ToString("00"); }

        private void ToggleFan()
        {
            if (_cooling == null) return;
            if (_cooling.FanBoostActive) { _cooling.StopFanBoost(); RefreshCooling(); return; }
            StartOutcome r = _cooling.StartFanBoost((int)_fanMin.Value);
            if (!r.Success) MessageBox.Show(this, r.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshCooling();
        }

        private void ToggleCap()
        {
            if (_cooling == null) return;
            if (_cooling.ThrottleActive) { _cooling.StopThrottle(); RefreshCooling(); return; }
            StartOutcome r = _cooling.StartThrottle((int)_capPct.Value, (int)_capMin.Value);
            if (!r.Success) MessageBox.Show(this, r.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshCooling();
        }

        private void ClearTelemetry()
        {
            if (_monitor == null) return;
            DialogResult r = MessageBox.Show(this, "Delete every telemetry file (" + DeviceMonitor.KeepTelemetryDays + " days at most) and empty the event list?\n\nA new file starts with the next sample.", Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
            int n = _monitor.ClearTelemetry();
            _events.Items.Clear();
            Logger.Info("Telemetry logs cleared: " + n + " file(s) deleted.");
        }

        private static string Short(string s, int max) { return string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max - 1) + "…"; }
        private static string Rate(double kbps) { return kbps >= 1024 ? (kbps / 1024).ToString("0.0") + " MB/s" : kbps.ToString("0") + " KB/s"; }
        private static double?[] ToSpark(bool?[] on) { if (on == null) return null; var r = new double?[on.Length]; for (int i = 0; i < on.Length; i++) r[i] = on[i].HasValue ? (on[i].Value ? 1 : 0) : (double?)null; return r; }

        private static void OpenFolder(string path)
        {
            try { Directory.CreateDirectory(path); Process.Start("explorer.exe", "\"" + path + "\""); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _refresh.Dispose();
            base.Dispose(disposing);
        }
    }
}
