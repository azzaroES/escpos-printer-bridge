using System;
using System.Collections.Generic;
using System.Text;

namespace UsbLanPrinterBridge.Core
{
    /// <summary>What a command in an ESC/POS stream does, in broad strokes.</summary>
    public enum EscPosKind
    {
        Init,       // ESC @
        Cut,        // GS V, ESC i, ESC m
        Feed,       // ESC d / J / K / e
        Format,     // fonts, alignment, spacing, code pages, barcode parameters, ...
        Drawer,     // ESC p, DLE DC4 1
        Image,      // ESC *, GS v 0, GS ( L, GS 8 L, GS *, GS /, FS p
        Barcode,    // GS k
        Symbol,     // GS ( k : QR Code, PDF417, ...
        Status,     // DLE EOT, GS I, GS r, GS a, GS ( H, ESC u, ESC v
        PageMode,   // ESC L / S / T / W / FF, GS $ / \
        Settings,   // GS ( E : memory switches and customised values, changes the printer's own configuration
        Other,      // known but uninteresting: macros, beeper, real-time requests, ...
        Unknown     // an ESC / GS / FS sequence this scanner does not know
    }

    /// <summary>A complete command header, as classified by <see cref="EscPosStreamScanner"/>.</summary>
    public sealed class EscPosCommand
    {
        public EscPosKind Kind;
        /// <summary>Human-readable name such as "GS V 66 (feed and cut)".</summary>
        public string Name;
        /// <summary>Bytes that follow the header and belong to it (image data, symbol data); they pass through untouched.</summary>
        public long Payload;
        /// <summary>True when the command puts marks on the paper.</summary>
        public bool Prints;

        internal EscPosCommand(EscPosKind kind, string name, long payload, bool prints)
        {
            Kind = kind; Name = name; Payload = payload; Prints = prints;
        }

        /// <summary>Sentinel: the lead byte was ordinary data after all (a DLE not followed by EOT, ENQ or DC4).</summary>
        internal static readonly EscPosCommand NotACommand = new EscPosCommand(EscPosKind.Other, "", 0, false);
    }

    /// <summary>
    /// Incremental ESC/POS command scanner. Bytes are fed in whatever chunks the network delivers; only an
    /// incomplete command header is held back between calls, so this can sit on a live socket.
    ///
    /// Commands that carry a payload, such as raster images, QR codes, barcodes and graphics, are skipped by
    /// their declared length, because image data can legitimately contain the bytes of a cut command. That is
    /// the whole point of parsing rather than pattern matching: a filter that removed "GS V" wherever it saw
    /// it would punch holes in logos.
    ///
    /// Subclasses receive every byte (<see cref="OnData"/>) and every complete command (<see cref="OnCommand"/>)
    /// and decide what to do with them; the scanner itself never drops anything.
    /// </summary>
    public abstract class EscPosStreamScanner
    {
        public const byte DLE = 0x10;
        public const byte ESC = 0x1B;
        public const byte FS = 0x1C;
        public const byte GS = 0x1D;

        /// <summary>A header longer than this is not one we know; release it as data rather than hold forever.</summary>
        private const int MaxHeld = 1024;

        private readonly List<byte> _held = new List<byte>(16);
        private long _skip;

        /// <summary>Feeds bytes through the scanner.</summary>
        public void Process(byte[] data, int offset, int count)
        {
            for (int i = 0; i < count; i++) Accept(data[offset + i]);
        }

        /// <summary>Call once at the end of a job: releases a held, incomplete command and resets the payload skip.</summary>
        public void Finish()
        {
            if (_held.Count > 0)
            {
                byte[] h = _held.ToArray();
                _held.Clear();
                OnIncomplete(h);
            }
            _skip = 0;
        }

        /// <summary>Bytes held back as a possible command header (diagnostics).</summary>
        public int HeldCount { get { return _held.Count; } }

        /// <summary>A byte outside any command header: text, control characters, or (isPayload) part of a command's data.</summary>
        protected abstract void OnData(byte b, bool isPayload);

        /// <summary>A complete command header. Its payload, if any, follows through <see cref="OnData"/> with isPayload = true.</summary>
        protected abstract void OnCommand(byte[] header, EscPosCommand command);

        /// <summary>A held sequence grew past the limit without completing: it is released here, unchanged.</summary>
        protected virtual void OnUnknownReleased(byte[] bytes) { foreach (byte b in bytes) OnData(b, false); }

        /// <summary>The job ended inside a command header: the partial header is released here, unchanged.</summary>
        protected virtual void OnIncomplete(byte[] bytes) { foreach (byte b in bytes) OnData(b, false); }

        private void Accept(byte b)
        {
            if (_skip > 0)
            {
                _skip--;
                OnData(b, true);
                return;
            }

            if (_held.Count == 0)
            {
                if (b == ESC || b == GS || b == FS || b == DLE) { _held.Add(b); return; }
                OnData(b, false);
                return;
            }

            _held.Add(b);
            EscPosCommand c = Classify(_held);
            if (c == null)
            {
                if (_held.Count >= MaxHeld)
                {
                    byte[] h = _held.ToArray();
                    _held.Clear();
                    OnUnknownReleased(h);
                }
                return;
            }

            if (ReferenceEquals(c, EscPosCommand.NotACommand))
            {
                // The lead byte was plain data. Release it and look at the bytes after it afresh.
                byte[] h = _held.ToArray();
                _held.Clear();
                OnData(h[0], false);
                for (int i = 1; i < h.Length; i++) Accept(h[i]);
                return;
            }

            byte[] header = _held.ToArray();
            _held.Clear();
            OnCommand(header, c);
            _skip = c.Payload;
        }

        // ------------------------------------------------------------------ command shapes

        private static EscPosCommand Fixed(List<byte> h, int total, EscPosKind kind, string name, bool prints)
        {
            return h.Count < total ? null : new EscPosCommand(kind, name, 0, prints);
        }

        private static EscPosCommand Fixed(List<byte> h, int total, EscPosKind kind, string name)
        {
            return Fixed(h, total, kind, name, false);
        }

        private static string Hex(byte b) { return "0x" + b.ToString("X2"); }

        /// <summary>Null while the held bytes are still an incomplete command header.</summary>
        private static EscPosCommand Classify(List<byte> h)
        {
            if (h.Count < 2) return null;
            byte n = h[1];
            switch (h[0])
            {
                case ESC: return ClassifyEsc(h, n);
                case GS: return ClassifyGs(h, n);
                case FS: return ClassifyFs(h, n);
                default: return ClassifyDle(h, n);
            }
        }

        private static EscPosCommand ClassifyEsc(List<byte> h, byte n)
        {
            switch (n)
            {
                case 0x69: return new EscPosCommand(EscPosKind.Cut, "ESC i (partial cut)", 0, false);
                case 0x6D: return new EscPosCommand(EscPosKind.Cut, "ESC m (partial cut)", 0, false);
                case 0x40: return new EscPosCommand(EscPosKind.Init, "ESC @ (initialise)", 0, false);
                case 0x32: return new EscPosCommand(EscPosKind.Format, "ESC 2 (default line spacing)", 0, false);
                case 0x4C: return new EscPosCommand(EscPosKind.PageMode, "ESC L (page mode)", 0, false);
                case 0x53: return new EscPosCommand(EscPosKind.PageMode, "ESC S (standard mode)", 0, false);
                case 0x0C: return new EscPosCommand(EscPosKind.PageMode, "ESC FF (print page)", 0, true);
                case 0x76: return new EscPosCommand(EscPosKind.Status, "ESC v (paper sensor status)", 0, false);
                case 0x3C: return new EscPosCommand(EscPosKind.Other, "ESC < (return home)", 0, false);
                case 0x2A:                                                  // ESC * m nL nH d...: bit image
                    {
                        if (h.Count < 5) return null;
                        int m = h[2];
                        long dots = h[3] | (h[4] << 8);
                        return new EscPosCommand(EscPosKind.Image, "ESC * (bit image)", dots * ((m == 0 || m == 1) ? 1 : 3), true);
                    }
                case 0x70: return Fixed(h, 5, EscPosKind.Drawer, "ESC p (drawer pulse)");
                // Feeds flush the line buffer but put nothing on the paper themselves, so they do not count as content.
                case 0x64: return Fixed(h, 3, EscPosKind.Feed, "ESC d (feed lines)");
                case 0x4A: return Fixed(h, 3, EscPosKind.Feed, "ESC J (feed)");
                case 0x4B: return Fixed(h, 3, EscPosKind.Feed, "ESC K (reverse feed)");
                case 0x65: return Fixed(h, 3, EscPosKind.Feed, "ESC e (reverse feed lines)");
                case 0x75: return Fixed(h, 3, EscPosKind.Status, "ESC u (peripheral status)");
                case 0x63: return Fixed(h, 4, EscPosKind.Other, "ESC c (sensor / panel button setting)");
                case 0x66: return Fixed(h, 4, EscPosKind.Other, "ESC f");
                case 0x42: return Fixed(h, 4, EscPosKind.Other, "ESC B (beeper)");
                case 0x24: return Fixed(h, 4, EscPosKind.Format, "ESC $ (absolute position)");
                case 0x5C: return Fixed(h, 4, EscPosKind.Format, "ESC \\ (relative position)");
                case 0x57: return Fixed(h, 10, EscPosKind.PageMode, "ESC W (print area)");
                case 0x54: return Fixed(h, 3, EscPosKind.PageMode, "ESC T (print direction)");
                case 0x44:                                                  // ESC D n1..nk NUL: tab stops
                    return (h.Count >= 3 && h[h.Count - 1] == 0) ? new EscPosCommand(EscPosKind.Format, "ESC D (tab stops)", 0, false) : null;
                case 0x28:                                                  // ESC ( x pL pH d...
                    {
                        if (h.Count < 5) return null;
                        long len = h[3] | (h[4] << 8);
                        return new EscPosCommand(EscPosKind.Other, "ESC ( " + (char)h[2], len, false);
                    }
                case 0x26: return Fixed(h, 5, EscPosKind.Other, "ESC & (user-defined characters)");
                case 0x20: return Fixed(h, 3, EscPosKind.Format, "ESC SP (character spacing)");
                case 0x21: return Fixed(h, 3, EscPosKind.Format, "ESC ! (print mode)");
                case 0x25: return Fixed(h, 3, EscPosKind.Format, "ESC % (user-defined set)");
                case 0x2D: return Fixed(h, 3, EscPosKind.Format, "ESC - (underline)");
                case 0x33: return Fixed(h, 3, EscPosKind.Format, "ESC 3 (line spacing)");
                case 0x3D: return Fixed(h, 3, EscPosKind.Other, "ESC = (peripheral select)");
                case 0x3F: return Fixed(h, 3, EscPosKind.Other, "ESC ? (cancel user-defined character)");
                case 0x45: return Fixed(h, 3, EscPosKind.Format, "ESC E (bold)");
                case 0x47: return Fixed(h, 3, EscPosKind.Format, "ESC G (double strike)");
                case 0x4D: return Fixed(h, 3, EscPosKind.Format, "ESC M (font)");
                case 0x52: return Fixed(h, 3, EscPosKind.Format, "ESC R (character set)");
                case 0x56: return Fixed(h, 3, EscPosKind.Format, "ESC V (rotation)");
                case 0x61: return Fixed(h, 3, EscPosKind.Format, "ESC a (alignment)");
                case 0x72: return Fixed(h, 3, EscPosKind.Format, "ESC r (print colour)");
                case 0x74: return Fixed(h, 3, EscPosKind.Format, "ESC t (code page)");
                case 0x7B: return Fixed(h, 3, EscPosKind.Format, "ESC { (upside down)");
                default:                                                    // unknown: assume one parameter
                    return Fixed(h, 3, EscPosKind.Unknown, "ESC " + Hex(n));
            }
        }

        private static EscPosCommand ClassifyGs(List<byte> h, byte n)
        {
            switch (n)
            {
                case 0x56:                                                  // GS V m [n]: cut
                    {
                        if (h.Count < 3) return null;
                        int m = h[2];
                        if (m == 0 || m == 48) return new EscPosCommand(EscPosKind.Cut, "GS V " + m + " (full cut)", 0, false);
                        if (m == 1 || m == 49) return new EscPosCommand(EscPosKind.Cut, "GS V " + m + " (partial cut)", 0, false);
                        if (h.Count < 4) return null;                       // 65, 66, 97, 98, 103, 104 carry a feed amount
                        return new EscPosCommand(EscPosKind.Cut, "GS V " + m + " " + h[3] + " (feed and cut)", 0, false);
                    }
                case 0x76:                                                  // GS v 0 m xL xH yL yH d...: raster image
                    {
                        if (h.Count < 3) return null;
                        if (h[2] != 0x30) return new EscPosCommand(EscPosKind.Unknown, "GS v " + Hex(h[2]), 0, false);
                        if (h.Count < 8) return null;
                        long widthBytes = h[4] | (h[5] << 8);
                        long rows = h[6] | (h[7] << 8);
                        return new EscPosCommand(EscPosKind.Image, "GS v 0 (raster image " + (widthBytes * 8) + "x" + rows + ")", widthBytes * rows, true);
                    }
                case 0x28:                                                  // GS ( x pL pH d...
                    {
                        if (h.Count < 5) return null;
                        long len = h[3] | (h[4] << 8);
                        switch (h[2])
                        {
                            case 0x6B: return new EscPosCommand(EscPosKind.Symbol, "GS ( k (2D symbol)", len, true);
                            case 0x4C: return new EscPosCommand(EscPosKind.Image, "GS ( L (graphics)", len, true);
                            case 0x48: return new EscPosCommand(EscPosKind.Status, "GS ( H (process id)", len, false);
                            case 0x45: return new EscPosCommand(EscPosKind.Settings, "GS ( E (printer settings)", len, false);
                            case 0x41: return new EscPosCommand(EscPosKind.Other, "GS ( A (test print)", len, true);
                            default: return new EscPosCommand(EscPosKind.Other, "GS ( " + (char)h[2], len, false);
                        }
                    }
                case 0x38:                                                  // GS 8 L p1 p2 p3 p4 d...: large graphics
                    {
                        if (h.Count < 3) return null;
                        if (h[2] != 0x4C) return new EscPosCommand(EscPosKind.Unknown, "GS 8 " + Hex(h[2]), 0, false);
                        if (h.Count < 7) return null;
                        long len = (long)h[3] | ((long)h[4] << 8) | ((long)h[5] << 16) | ((long)h[6] << 24);
                        return new EscPosCommand(EscPosKind.Image, "GS 8 L (graphics)", len, true);
                    }
                case 0x2A:                                                  // GS * x y d...: define a downloaded bit image
                    {
                        if (h.Count < 4) return null;
                        return new EscPosCommand(EscPosKind.Image, "GS * (define bit image)", (long)h[2] * h[3] * 8, false);
                    }
                case 0x6B:                                                  // GS k: barcode
                    {
                        if (h.Count < 3) return null;
                        int m = h[2];
                        if (m < 65) return (h.Count >= 4 && h[h.Count - 1] == 0) ? new EscPosCommand(EscPosKind.Barcode, "GS k (barcode)", 0, true) : null;
                        if (h.Count < 4) return null;
                        return new EscPosCommand(EscPosKind.Barcode, "GS k (barcode)", h[3], true);
                    }
                case 0x2F: return Fixed(h, 3, EscPosKind.Image, "GS / (print bit image)", true);
                case 0x3A: return new EscPosCommand(EscPosKind.Other, "GS : (macro)", 0, false);
                case 0x49: return Fixed(h, 3, EscPosKind.Status, "GS I (printer id)");
                case 0x72: return Fixed(h, 3, EscPosKind.Status, "GS r (status)");
                case 0x61: return Fixed(h, 3, EscPosKind.Status, "GS a (auto status back)");
                case 0x4C: return Fixed(h, 4, EscPosKind.Format, "GS L (left margin)");
                case 0x57: return Fixed(h, 4, EscPosKind.Format, "GS W (print width)");
                case 0x50: return Fixed(h, 4, EscPosKind.Format, "GS P (motion units)");
                case 0x5C: return Fixed(h, 4, EscPosKind.PageMode, "GS \\ (relative vertical position)");
                case 0x24: return Fixed(h, 4, EscPosKind.PageMode, "GS $ (absolute vertical position)");
                case 0x5E: return Fixed(h, 5, EscPosKind.Other, "GS ^ (run macro)");
                case 0x67: return Fixed(h, 5, EscPosKind.Other, "GS g (maintenance counter)");
                case 0x21: return Fixed(h, 3, EscPosKind.Format, "GS ! (character size)");
                case 0x42: return Fixed(h, 3, EscPosKind.Format, "GS B (reverse)");
                case 0x62: return Fixed(h, 3, EscPosKind.Format, "GS b (smoothing)");
                case 0x48: return Fixed(h, 3, EscPosKind.Format, "GS H (HRI position)");
                case 0x66: return Fixed(h, 3, EscPosKind.Format, "GS f (HRI font)");
                case 0x68: return Fixed(h, 3, EscPosKind.Format, "GS h (barcode height)");
                case 0x77: return Fixed(h, 3, EscPosKind.Format, "GS w (barcode width)");
                case 0x54: return Fixed(h, 3, EscPosKind.Format, "GS T (print position)");
                case 0x45: return Fixed(h, 3, EscPosKind.Other, "GS E");
                default:
                    return Fixed(h, 3, EscPosKind.Unknown, "GS " + Hex(n));
            }
        }

        private static EscPosCommand ClassifyFs(List<byte> h, byte n)
        {
            switch (n)
            {
                case 0x26: return new EscPosCommand(EscPosKind.Format, "FS & (Kanji mode on)", 0, false);
                case 0x2E: return new EscPosCommand(EscPosKind.Format, "FS . (Kanji mode off)", 0, false);
                case 0x28:                                                  // FS ( x pL pH d...
                    {
                        if (h.Count < 5) return null;
                        long len = h[3] | (h[4] << 8);
                        return new EscPosCommand(EscPosKind.Other, "FS ( " + (char)h[2], len, false);
                    }
                case 0x53: return Fixed(h, 4, EscPosKind.Format, "FS S (Kanji spacing)");
                case 0x70: return Fixed(h, 4, EscPosKind.Image, "FS p (print stored image)", true);
                case 0x32: return Fixed(h, 4, EscPosKind.Other, "FS 2 (define Kanji)");
                case 0x21: return Fixed(h, 3, EscPosKind.Format, "FS ! (Kanji print mode)");
                case 0x2D: return Fixed(h, 3, EscPosKind.Format, "FS - (Kanji underline)");
                case 0x43: return Fixed(h, 3, EscPosKind.Format, "FS C (Kanji code system)");
                case 0x57: return Fixed(h, 3, EscPosKind.Format, "FS W (Kanji quad size)");
                case 0x71: return Fixed(h, 3, EscPosKind.Other, "FS q (define stored image)");
                default:
                    return Fixed(h, 3, EscPosKind.Unknown, "FS " + Hex(n));
            }
        }

        private static EscPosCommand ClassifyDle(List<byte> h, byte n)
        {
            switch (n)
            {
                case 0x04:                                                  // DLE EOT n [a]: real-time status
                    {
                        if (h.Count < 3) return null;
                        int m = h[2];
                        int total = (m >= 1 && m <= 6) ? 3 : 4;
                        return h.Count < total ? null : new EscPosCommand(EscPosKind.Status, "DLE EOT " + m + " (real-time status)", 0, false);
                    }
                case 0x05: return Fixed(h, 3, EscPosKind.Other, "DLE ENQ (real-time request)");
                case 0x14:                                                  // DLE DC4 fn ...: real-time commands
                    {
                        if (h.Count < 3) return null;
                        switch (h[2])
                        {
                            case 1: return Fixed(h, 5, EscPosKind.Drawer, "DLE DC4 1 (drawer pulse)");
                            case 2: return Fixed(h, 3, EscPosKind.Other, "DLE DC4 2 (power off)");
                            case 3: return Fixed(h, 5, EscPosKind.Other, "DLE DC4 3 (buzzer)");
                            case 7: return Fixed(h, 5, EscPosKind.Other, "DLE DC4 7 (transmit)");
                            case 8: return Fixed(h, 7, EscPosKind.Other, "DLE DC4 8 (clear buffers)");
                            default: return Fixed(h, 3, EscPosKind.Unknown, "DLE DC4 " + h[2]);
                        }
                    }
                default:
                    return EscPosCommand.NotACommand;
            }
        }
    }

    /// <summary>
    /// Removes every cutter command from an ESC/POS stream: GS V in all its forms, and the older ESC i and ESC m.
    /// Everything else passes through byte for byte, including cut-like bytes inside image and symbol payloads.
    ///
    /// Each removed cut can be replaced by a line feed (<see cref="FeedLines"/>), so the receipt still comes out
    /// far enough to be torn off by hand at the tear bar instead of stopping under the print head. The feed is
    /// only emitted when something was printed since the previous cut, so a POS app that sends two cuts in a
    /// row does not produce two blank strips.
    /// </summary>
    public sealed class EscPosCutFilter : EscPosStreamScanner
    {
        private byte[] _out = new byte[8192];
        private int _len;
        private bool _content;

        public EscPosCutFilter() { FeedLines = 4; }

        /// <summary>Lines to feed in place of each removed cut (0 = nothing).</summary>
        public int FeedLines { get; set; }

        public int CutsRemoved { get; private set; }

        /// <summary>Raised for every cut removed, with the command's name, e.g. "GS V 66 0 (feed and cut)".</summary>
        public event Action<string> CutRemoved;

        public byte[] Output { get { return _out; } }
        public int OutputLength { get { return _len; } }

        /// <summary>Filters a chunk. Output/OutputLength hold the bytes to forward (may be fewer or more than the input).</summary>
        public void Filter(byte[] data, int offset, int count)
        {
            _len = 0;
            Process(data, offset, count);
        }

        /// <summary>Releases a held, incomplete header at the end of a job. Output/OutputLength hold the tail.</summary>
        public void Flush()
        {
            _len = 0;
            Finish();
        }

        protected override void OnData(byte b, bool isPayload)
        {
            Emit(b);
            if (!isPayload && (b == 0x0A || b >= 0x20)) _content = true;
        }

        protected override void OnCommand(byte[] header, EscPosCommand command)
        {
            if (command.Kind == EscPosKind.Cut)
            {
                CutsRemoved++;
                if (FeedLines > 0 && _content)
                {
                    Emit(0x1B); Emit(0x64); Emit((byte)Math.Min(FeedLines, 255));      // ESC d n: print and feed n lines
                }
                _content = false;
                var h = CutRemoved;
                if (h != null) { try { h(command.Name); } catch { } }
                return;
            }
            foreach (byte b in header) Emit(b);
            if (command.Prints) _content = true;
        }

        protected override void OnUnknownReleased(byte[] bytes)
        {
            foreach (byte b in bytes) Emit(b);
            _content = true;
        }

        protected override void OnIncomplete(byte[] bytes)
        {
            foreach (byte b in bytes) Emit(b);
        }

        private void Emit(byte b)
        {
            if (_len >= _out.Length) Array.Resize(ref _out, _out.Length * 2);
            _out[_len++] = b;
        }
    }

    /// <summary>
    /// Renders an ESC/POS job as the ticket would look on paper: the text line by line, centred and right-aligned
    /// lines padded to the paper width, blank lines for feeds, and a marker for everything that is not text:
    /// [image 384x200], [barcode: 12345], [QR data: https://...], [drawer opened], and a dashed cut line.
    /// Image and symbol payloads are skipped by length, so pixel bytes never show up as garbage text.
    /// </summary>
    public sealed class EscPosTicketText : EscPosStreamScanner
    {
        /// <summary>Characters per line for the alignment padding: font A on an 80 mm roll.</summary>
        public const int Columns = 48;
        private const string CutLine = "- - - - - - - - - -  cut  - - - - - - - - - -";

        private readonly StringBuilder _out = new StringBuilder();
        private readonly StringBuilder _line = new StringBuilder();
        private readonly List<byte> _collect = new List<byte>();
        private readonly int _maxChars;
        private EscPosCommand _collecting;
        private int _align;
        private bool _truncated;

        private EscPosTicketText(int maxChars) { _maxChars = maxChars; }

        public static string Render(byte[] data, int length)
        {
            return Render(data, length, 8000);
        }

        public static string Render(byte[] data, int length, int maxChars)
        {
            if (data == null || length <= 0) return "";
            var t = new EscPosTicketText(maxChars);
            t.Process(data, 0, length);
            t.Finish();
            t.FlushCollection();
            if (t._line.Length > 0) t.EndLine();
            return t._out.ToString().TrimEnd('\n', ' ');
        }

        protected override void OnData(byte b, bool isPayload)
        {
            if (isPayload)
            {
                if (_collecting != null && _collect.Count < 2048) _collect.Add(b);
                return;
            }
            FlushCollection();
            if (b == 0x0A) { EndLine(); return; }
            if (b == 0x09) { _line.Append("    "); return; }
            if (b >= 0x20 && b <= 0x7E) { _line.Append((char)b); return; }
            if (b >= 0xA0) { _line.Append((char)b); return; }          // Latin-1 accented characters, as the printer's code page mostly is
            if (b >= 0x80) _line.Append('?');
            // other control bytes (CR, DLE, CAN, ...) print nothing
        }

        protected override void OnCommand(byte[] header, EscPosCommand command)
        {
            FlushCollection();
            switch (command.Kind)
            {
                case EscPosKind.Init:
                    _align = 0;
                    break;
                case EscPosKind.Cut:
                    if (_line.Length > 0) EndLine();
                    AppendLine(CutLine);
                    break;
                case EscPosKind.Feed:
                    if (_line.Length > 0) EndLine();
                    int lines = header[1] == 0x64 ? Math.Min((int)header[2], 3) : 1;   // ESC d n, capped; ESC J / K / e = one
                    for (int i = 0; i < lines; i++) AppendLine("");
                    break;
                case EscPosKind.Drawer:
                    if (_line.Length > 0) EndLine();
                    AppendLine("[drawer opened]");
                    break;
                case EscPosKind.Image:
                    if (_line.Length > 0) EndLine();
                    if (header[0] == GS && header[1] == 0x2A) break;          // defining a bit image prints nothing
                    int at = command.Name.IndexOf("raster image ", StringComparison.Ordinal);
                    AppendLine(at >= 0 ? "[image " + command.Name.Substring(at + 13).TrimEnd(')') + "]" : "[image]");
                    break;
                case EscPosKind.Barcode:
                    if (_line.Length > 0) EndLine();
                    if (header.Length > 4 && header[2] < 65)
                    {
                        // GS k m d1..dk NUL: the data is in the header
                        var sb = new StringBuilder();
                        for (int i = 3; i < header.Length - 1; i++) sb.Append(Printable(header[i]));
                        AppendLine("[barcode: " + sb + "]");
                    }
                    else _collecting = command;                                 // GS k m n d1..dn: the data is the payload
                    break;
                case EscPosKind.Symbol:
                    if (_line.Length > 0) EndLine();
                    _collecting = command;
                    break;
                case EscPosKind.Format:
                    if (header[0] == ESC && header[1] == 0x61 && header.Length > 2)   // ESC a n: alignment
                    {
                        int n = header[2];
                        _align = (n == 1 || n == 49) ? 1 : (n == 2 || n == 50) ? 2 : 0;
                    }
                    break;
            }
        }

        private void FlushCollection()
        {
            EscPosCommand c = _collecting;
            if (c == null) return;
            _collecting = null;
            byte[] p = _collect.ToArray();
            _collect.Clear();
            if (c.Kind == EscPosKind.Barcode)
            {
                var sb = new StringBuilder();
                foreach (byte b in p) sb.Append(Printable(b));
                AppendLine("[barcode: " + sb + "]");
                return;
            }
            // GS ( k payload: cn fn [m] d...  cn 49 = QR Code, 48 = PDF417, 50 = MaxiCode, 51 = 2D GS1, 52 = Composite
            if (p.Length < 2) return;
            string kind = p[0] == 49 ? "QR" : p[0] == 48 ? "PDF417" : "2D symbol";
            if (p[1] == 0x50)                                                   // fn 80: store the data
            {
                var sb = new StringBuilder();
                for (int i = 3; i < p.Length; i++) sb.Append(Printable(p[i]));
                if (p.Length >= 2048) sb.Append("...");
                AppendLine("[" + kind + " data: " + sb + "]");
            }
            else if (p[1] == 0x51) AppendLine("[" + kind + " code]");          // fn 81: print it
        }

        private static char Printable(byte b)
        {
            return (b >= 0x20 && b <= 0x7E) ? (char)b : b >= 0xA0 ? (char)b : '.';
        }

        private void EndLine()
        {
            string text = _line.ToString().TrimEnd();
            _line.Length = 0;
            if (text.Length > 0 && text.Length < Columns)
            {
                if (_align == 1) text = new string(' ', (Columns - text.Length) / 2) + text;
                else if (_align == 2) text = new string(' ', Columns - text.Length) + text;
            }
            AppendLine(text);
        }

        private void AppendLine(string text)
        {
            if (_truncated) return;
            if (_out.Length + text.Length > _maxChars)
            {
                _truncated = true;
                _out.Append("[... ticket text truncated]\n");
                return;
            }
            _out.Append(text).Append('\n');
        }
    }

    /// <summary>
    /// Reads a job and says what it told the printer: how many lines of text, images, barcodes, cuts, drawer
    /// pulses, and which commands were not recognised. Also spots jobs that are not ESC/POS at all (PCL,
    /// PostScript, PDF, ZPL), which is the usual reason a receipt printer prints garbage or nothing.
    /// </summary>
    public sealed class EscPosJobSummary : EscPosStreamScanner
    {
        private readonly Dictionary<EscPosKind, int> _counts = new Dictionary<EscPosKind, int>();
        private readonly Dictionary<string, int> _unknown = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> _notes = new List<string>();
        private int _lines;
        private bool _lineHasText;
        private bool _incomplete;
        private int _unknownReleased;

        public string ForeignFormat { get; private set; }

        public static EscPosJobSummary Analyze(byte[] data, int length)
        {
            var s = new EscPosJobSummary();
            if (data == null || length <= 0) return s;
            s.ForeignFormat = DetectForeignFormat(data, length);
            s.Process(data, 0, length);
            s.Finish();
            return s;
        }

        public int Count(EscPosKind kind) { int n; return _counts.TryGetValue(kind, out n) ? n : 0; }
        public int TextLines { get { return _lines + (_lineHasText ? 1 : 0); } }
        public IDictionary<string, int> UnknownCommands { get { return _unknown; } }
        public bool EndedInsideCommand { get { return _incomplete; } }

        /// <summary>One line: "init; 14 lines of text; 1 image; 1 QR code; drawer pulse; cut".</summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (ForeignFormat != null) parts.Add("NOT ESC/POS: looks like " + ForeignFormat);
            if (Count(EscPosKind.Init) > 0) parts.Add("init");
            int lines = TextLines;
            if (lines > 0) parts.Add(lines + (lines == 1 ? " line" : " lines") + " of text");
            Add(parts, EscPosKind.Image, "image", "images");
            Add(parts, EscPosKind.Barcode, "barcode", "barcodes");
            Add(parts, EscPosKind.Symbol, "QR/2D symbol", "QR/2D symbols");
            Add(parts, EscPosKind.Feed, "feed", "feeds");
            Add(parts, EscPosKind.Drawer, "drawer pulse", "drawer pulses");
            Add(parts, EscPosKind.Cut, "cut", "cuts");
            Add(parts, EscPosKind.PageMode, "page-mode command", "page-mode commands");
            Add(parts, EscPosKind.Settings, "printer settings change (GS ( E)", "printer settings changes (GS ( E)");
            Add(parts, EscPosKind.Status, "status query", "status queries");
            if (_unknown.Count > 0)
            {
                var names = new List<string>();
                foreach (var kv in _unknown) names.Add(kv.Key + (kv.Value > 1 ? " x" + kv.Value : ""));
                parts.Add("unknown: " + string.Join(", ", names.ToArray()));
            }
            if (_incomplete) parts.Add("ends inside a command (truncated?)");
            if (parts.Count == 0) parts.Add("no printable content");
            return string.Join("; ", parts.ToArray());
        }

        /// <summary>Things worth a warning: not ESC/POS, unknown commands, truncated, settings changes.</summary>
        public List<string> Problems()
        {
            var list = new List<string>();
            if (ForeignFormat != null)
                list.Add("This job is not ESC/POS, it looks like " + ForeignFormat + ". A receipt printer will print garbage or nothing. The sending device must use an ESC/POS driver or a POS app that talks ESC/POS.");
            if (_unknown.Count > 0)
            {
                var names = new List<string>();
                foreach (var kv in _unknown) names.Add(kv.Key + (kv.Value > 1 ? " x" + kv.Value : ""));
                list.Add("Commands the bridge does not recognise (forwarded unchanged): " + string.Join(", ", names.ToArray())
                         + ". If the printer misbehaves, the sending app may be using another printer's dialect.");
            }
            if (Count(EscPosKind.Settings) > 0)
                list.Add("The job changes the printer's own settings (GS ( E memory switches / customised values). This is persistent on the printer, not something the bridge does.");
            if (_incomplete)
                list.Add("The job ends in the middle of a command: the client disconnected early or the idle timeout split a receipt. Raise the job idle timeout if receipts arrive in pieces.");
            return list;
        }

        private void Add(List<string> parts, EscPosKind kind, string singular, string plural)
        {
            int n = Count(kind);
            if (n == 1) parts.Add(singular);
            else if (n > 1) parts.Add(n + " " + plural);
        }

        protected override void OnData(byte b, bool isPayload)
        {
            if (isPayload) return;
            if (b == 0x0A) { _lines++; _lineHasText = false; }
            else if (b >= 0x20) _lineHasText = true;
        }

        protected override void OnCommand(byte[] header, EscPosCommand command)
        {
            int n;
            _counts.TryGetValue(command.Kind, out n);
            _counts[command.Kind] = n + 1;
            if (command.Kind == EscPosKind.Unknown)
            {
                int u;
                _unknown.TryGetValue(command.Name, out u);
                _unknown[command.Name] = u + 1;
            }
            if (command.Kind == EscPosKind.Feed && _lineHasText) { _lines++; _lineHasText = false; }
        }

        protected override void OnUnknownReleased(byte[] bytes)
        {
            _unknownReleased++;
            string name = bytes.Length > 1 ? Lead(bytes[0]) + " 0x" + bytes[1].ToString("X2") + " (unterminated)" : Lead(bytes[0]);
            int u;
            _unknown.TryGetValue(name, out u);
            _unknown[name] = u + 1;
        }

        protected override void OnIncomplete(byte[] bytes)
        {
            _incomplete = true;
        }

        private static string Lead(byte b)
        {
            switch (b)
            {
                case ESC: return "ESC";
                case GS: return "GS";
                case FS: return "FS";
                case DLE: return "DLE";
                default: return "0x" + b.ToString("X2");
            }
        }

        /// <summary>Signatures of page description languages that turn up when a device is given the wrong driver.</summary>
        private static string DetectForeignFormat(byte[] d, int length)
        {
            if (StartsWith(d, length, "%!PS")) return "PostScript";
            if (StartsWith(d, length, "%PDF")) return "PDF";
            if (StartsWith(d, length, "\x1B%-12345X")) return "PJL/PCL (a laser/inkjet driver)";
            if (StartsWith(d, length, "^XA")) return "ZPL (Zebra label language)";
            if (StartsWith(d, length, "! 0 ")) return "CPCL (Zebra mobile label language)";
            if (StartsWith(d, length, "{\\rtf")) return "RTF text";
            if (StartsWith(d, length, "\x1BE\x1B&") || StartsWith(d, length, "\x1B&l")) return "PCL (a laser/inkjet driver)";
            return null;
        }

        private static bool StartsWith(byte[] d, int length, string sig)
        {
            if (length < sig.Length) return false;
            for (int i = 0; i < sig.Length; i++) if (d[i] != (byte)sig[i]) return false;
            return true;
        }
    }
}
