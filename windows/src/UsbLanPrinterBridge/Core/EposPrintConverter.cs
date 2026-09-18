using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace UsbLanPrinterBridge.Core
{
    /// <summary>
    /// Converts an Epson ePOS-Print XML document (the payload the ePOS SDK for JavaScript/Android POSTs to
    /// /cgi-bin/epos/service.cgi) into ESC/POS byte commands for a generic thermal receipt printer.
    ///
    /// This is a clean-room implementation of the published ePOS-Print element/attribute set
    /// (namespace http://www.epson-pos.com/schemas/2011/03/epos-print); no Epson code is used or redistributed.
    ///
    /// Supported elements: text (+ align/font/bold/underline/reverse/size/double/linespc), feed, cut, pulse,
    /// barcode, symbol (QR Code), image (raster), command (raw hex). Unsupported elements are skipped and
    /// reported through <see cref="Warnings"/> so the caller can still return a success response.
    /// </summary>
    public sealed class EposPrintConverter
    {
        public const string EposNamespace = "http://www.epson-pos.com/schemas/2011/03/epos-print";

        private readonly MemoryStream _out = new MemoryStream();
        private readonly List<string> _warnings = new List<string>();
        private Encoding _encoding = GetEncoding(437);

        public IList<string> Warnings { get { return _warnings; } }

        /// <summary>Parses the document and returns the ESC/POS byte stream. Throws XmlException / FormatException on malformed input.</summary>
        public static byte[] Convert(string eposPrintXml, out IList<string> warnings)
        {
            var c = new EposPrintConverter();
            c.Run(eposPrintXml);
            warnings = c._warnings;
            return c._out.ToArray();
        }

        private void Run(string xml)
        {
            var doc = new XmlDocument();
            doc.XmlResolver = null; // do not resolve external entities
            doc.LoadXml(xml);

            XmlElement root = FindEposPrint(doc.DocumentElement);
            if (root == null) throw new XmlException("No <epos-print> element found.");

            Init();
            foreach (XmlNode node in root.ChildNodes)
            {
                var el = node as XmlElement;
                if (el == null) continue;
                Handle(el);
            }
        }

        private static XmlElement FindEposPrint(XmlElement start)
        {
            if (start == null) return null;
            if (string.Equals(start.LocalName, "epos-print", StringComparison.OrdinalIgnoreCase)) return start;
            // The SDK often wraps it in a SOAP Envelope/Body; search descendants by local name.
            XmlNodeList list = start.GetElementsByTagName("epos-print", EposNamespace);
            if (list.Count > 0) return list[0] as XmlElement;
            list = start.GetElementsByTagName("epos-print");
            return list.Count > 0 ? list[0] as XmlElement : null;
        }

        // ------------------------------------------------------------------ element dispatch

        private void Handle(XmlElement el)
        {
            switch (el.LocalName.ToLowerInvariant())
            {
                case "text": DoText(el); break;
                case "feed": DoFeed(el); break;
                case "cut": DoCut(el); break;
                case "pulse": DoPulse(el); break;
                case "barcode": DoBarcode(el); break;
                case "symbol": DoSymbol(el); break;
                case "image": DoImage(el); break;
                case "command": DoCommand(el); break;
                case "logo": _warnings.Add("<logo> ignored (needs a logo stored in the printer)."); break;
                case "sound": DoSound(el); break;
                case "layout":
                case "recovery":
                case "reset": Emit(0x1B, 0x40); break; // ESC @
                case "page":
                case "area":
                case "direction":
                case "position":
                case "line":
                case "rectangle":
                case "hline":
                case "vline-begin":
                case "vline-end":
                    _warnings.Add("<" + el.LocalName + "> (page-mode/ruled-line) is not supported and was skipped.");
                    break;
                default:
                    _warnings.Add("<" + el.LocalName + "> is not a recognised ePOS-Print element; skipped.");
                    break;
            }
        }

        // ------------------------------------------------------------------ text and styling

        private void DoText(XmlElement el)
        {
            // Attribute-only <text .../> elements set modes; a <text>content</text> also prints.
            if (HasAttr(el, "lang")) SetLanguage(GetAttr(el, "lang"));
            if (HasAttr(el, "align")) EmitAlign(GetAttr(el, "align"));
            if (HasAttr(el, "font")) EmitFont(GetAttr(el, "font"));
            if (HasAttr(el, "linespc")) { Emit(0x1B, 0x33); Emit((byte)ClampByte(GetInt(el, "linespc", 30))); }
            if (HasAttr(el, "smooth")) { Emit(0x1D, 0x62); Emit(GetBool(el, "smooth") ? (byte)1 : (byte)0); }
            if (HasAttr(el, "ul")) { Emit(0x1B, 0x2D); Emit(GetBool(el, "ul") ? (byte)1 : (byte)0); }
            if (HasAttr(el, "em")) { Emit(0x1B, 0x45); Emit(GetBool(el, "em") ? (byte)1 : (byte)0); }
            if (HasAttr(el, "reverse")) { Emit(0x1D, 0x42); Emit(GetBool(el, "reverse") ? (byte)1 : (byte)0); }
            if (HasAttr(el, "rotate")) { Emit(0x1B, 0x56); Emit(GetBool(el, "rotate") ? (byte)1 : (byte)0); }

            bool hasSize = HasAttr(el, "width") || HasAttr(el, "height");
            bool hasDouble = HasAttr(el, "dw") || HasAttr(el, "dh");
            if (hasSize || hasDouble)
            {
                int w = HasAttr(el, "width") ? GetInt(el, "width", 1) : (GetBool(el, "dw") ? 2 : 1);
                int h = HasAttr(el, "height") ? GetInt(el, "height", 1) : (GetBool(el, "dh") ? 2 : 1);
                EmitSize(w, h);
            }

            if (HasAttr(el, "style")) { /* combined style attr rarely used; individual attrs above cover it */ }

            string content = el.InnerText;
            if (!string.IsNullOrEmpty(content) && el.ChildNodes.Count > 0)
            {
                // Only emit text when there is actual character data (not just an empty self-closing style element).
                if (HasTextContent(el)) EmitText(content);
            }
        }

        private static bool HasTextContent(XmlElement el)
        {
            foreach (XmlNode n in el.ChildNodes)
                if (n.NodeType == XmlNodeType.Text || n.NodeType == XmlNodeType.CDATA || n.NodeType == XmlNodeType.Whitespace || n.NodeType == XmlNodeType.SignificantWhitespace)
                    if (!string.IsNullOrEmpty(n.Value)) return true;
            return false;
        }

        private void EmitText(string text)
        {
            // ePOS uses \n for line breaks; printers want CR?LF. Send LF (0x0A); most ESC/POS treat LF as print+feed.
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            byte[] bytes;
            try { bytes = _encoding.GetBytes(text.Replace("\n", "\n")); }
            catch { bytes = Encoding.ASCII.GetBytes(text); }
            // encode line by line so we can turn \n into LF explicitly
            foreach (char ch in text)
            {
                if (ch == '\n') { Emit(0x0A); continue; }
                byte[] b;
                try { b = _encoding.GetBytes(ch.ToString()); }
                catch { b = new[] { (byte)'?' }; }
                _out.Write(b, 0, b.Length);
            }
        }

        private void EmitAlign(string align)
        {
            byte n = align == "center" ? (byte)1 : align == "right" ? (byte)2 : (byte)0;
            Emit(0x1B, 0x61, n);
        }

        private void EmitFont(string font)
        {
            // font_a -> 0, font_b -> 1, others best-effort to A.
            byte n = font == "font_b" ? (byte)1 : (byte)0;
            Emit(0x1B, 0x4D, n);
        }

        private void EmitSize(int width, int height)
        {
            int w = Clamp(width, 1, 8) - 1;
            int h = Clamp(height, 1, 8) - 1;
            byte n = (byte)(((w & 0x07) << 4) | (h & 0x07));
            Emit(0x1D, 0x21, n); // GS ! n
        }

        private void SetLanguage(string lang)
        {
            // Map a few common ePOS language codes to ESC/POS code pages (ESC t n).
            int codepage;
            byte escT;
            switch ((lang ?? "").ToLowerInvariant())
            {
                case "ja": codepage = 932; escT = 1; break;   // Katakana (approx); Shift-JIS printers vary
                case "zh-hans": codepage = 936; escT = 0; break;
                case "ko": codepage = 949; escT = 0; break;
                default: codepage = 1252; escT = 16; break;    // WPC1252 (ESC t 16 on many models); Latin
            }
            Encoding enc = GetEncoding(codepage);
            if (enc != null) _encoding = enc;
            Emit(0x1B, 0x74, escT);
        }

        // ------------------------------------------------------------------ feed / cut / drawer

        private void DoFeed(XmlElement el)
        {
            if (HasAttr(el, "line")) { Emit(0x1B, 0x64); Emit((byte)ClampByte(GetInt(el, "line", 1))); }        // ESC d n
            else if (HasAttr(el, "unit")) { Emit(0x1B, 0x4A); Emit((byte)ClampByte(GetInt(el, "unit", 1))); }   // ESC J n
            else Emit(0x0A);                                                                                     // one line
        }

        private void DoCut(XmlElement el)
        {
            string type = GetAttr(el, "type");
            if (type == "no_feed") Emit(0x1D, 0x56, 0x01);        // GS V 1 partial, no feed
            else Emit(0x1D, 0x56, 0x42, 0x00);                    // GS V 66 0 feed then partial cut
        }

        private void DoPulse(XmlElement el)
        {
            byte m = GetAttr(el, "drawer") == "drawer_2" ? (byte)1 : (byte)0;
            string time = GetAttr(el, "time");
            byte onTime = 50; // x2ms => 100ms
            switch (time)
            {
                case "pulse_200": onTime = 100; break;
                case "pulse_300": onTime = 150; break;
                case "pulse_400": onTime = 200; break;
                case "pulse_500": onTime = 250; break;
            }
            Emit(0x1B, 0x70, m, onTime, onTime); // ESC p m t1 t2
        }

        private void DoSound(XmlElement el)
        {
            int repeat = HasAttr(el, "repeat") ? GetInt(el, "repeat", 1) : 1;
            repeat = Clamp(repeat, 1, 63);
            // ESC ( A buzzer is model-specific; use the widely supported ESC B n t? Fall back to BEL.
            Emit(0x1B, 0x42, (byte)repeat, 3); // ESC B n t (buzzer) on models that support it
        }

        // ------------------------------------------------------------------ barcode / QR

        private void DoBarcode(XmlElement el)
        {
            string data = el.InnerText ?? "";
            string type = GetAttr(el, "type");
            byte m;
            if (!BarcodeType(type, out m)) { _warnings.Add("Barcode type '" + type + "' not supported; skipped."); return; }

            string hri = GetAttr(el, "hri");
            byte hriPos = hri == "above" ? (byte)1 : hri == "below" ? (byte)2 : hri == "both" ? (byte)3 : (byte)0;
            Emit(0x1D, 0x48, hriPos);                                   // GS H (HRI position)
            if (HasAttr(el, "font")) { Emit(0x1D, 0x66); Emit(GetAttr(el, "font") == "font_b" ? (byte)1 : (byte)0); } // GS f
            if (HasAttr(el, "width")) { Emit(0x1D, 0x77); Emit((byte)Clamp(GetInt(el, "width", 3), 2, 6)); }          // GS w
            if (HasAttr(el, "height")) { Emit(0x1D, 0x68); Emit((byte)ClampByte(GetInt(el, "height", 162))); }         // GS h

            byte[] bytes = Encoding.ASCII.GetBytes(data);
            // GS k m n d1..dn  (function B, length-prefixed)
            Emit(0x1D, 0x6B, m, (byte)Math.Min(bytes.Length, 255));
            _out.Write(bytes, 0, Math.Min(bytes.Length, 255));
        }

        private static bool BarcodeType(string type, out byte m)
        {
            // GS k function B codes (65..73)
            switch (type)
            {
                case "upc_a": m = 65; return true;
                case "upc_e": m = 66; return true;
                case "ean13": case "jan13": m = 67; return true;
                case "ean8": case "jan8": m = 68; return true;
                case "code39": m = 69; return true;
                case "itf": m = 70; return true;
                case "codabar": m = 71; return true;
                case "code93": m = 72; return true;
                case "code128": case "code128_auto": case "gs1_128": m = 73; return true;
                default: m = 0; return false;
            }
        }

        private void DoSymbol(XmlElement el)
        {
            string type = GetAttr(el, "type");
            if (type != null && type.StartsWith("qrcode", StringComparison.OrdinalIgnoreCase))
            {
                EmitQr(el);
                return;
            }
            _warnings.Add("2D symbol type '" + type + "' not supported (only QR Code); skipped.");
        }

        private void EmitQr(XmlElement el)
        {
            byte[] data = Encoding.UTF8.GetBytes(el.InnerText ?? "");
            int size = HasAttr(el, "size") ? Clamp(GetInt(el, "size", 3), 1, 16) : 3;
            byte level;
            switch (GetAttr(el, "level"))
            {
                case "level_m": level = 49; break;
                case "level_q": level = 50; break;
                case "level_h": level = 51; break;
                default: level = 48; break; // level_l / default
            }

            // GS ( k, model 2
            Emit(0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00);
            // module size
            Emit(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, (byte)size);
            // error correction level
            Emit(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, level);
            // store data
            int len = data.Length + 3;
            Emit(0x1D, 0x28, 0x6B, (byte)(len & 0xFF), (byte)((len >> 8) & 0xFF), 0x31, 0x50, 0x30);
            _out.Write(data, 0, data.Length);
            // print
            Emit(0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30);
        }

        // ------------------------------------------------------------------ image (raster)

        private void DoImage(XmlElement el)
        {
            int width = GetInt(el, "width", 0);
            int height = GetInt(el, "height", 0);
            if (width <= 0 || height <= 0) { _warnings.Add("<image> missing width/height; skipped."); return; }

            byte[] raster;
            try { raster = System.Convert.FromBase64String((el.InnerText ?? "").Trim()); }
            catch { _warnings.Add("<image> data is not valid base64; skipped."); return; }

            int bytesPerRow = (width + 7) / 8;
            int expected = bytesPerRow * height;
            if (raster.Length < expected)
            {
                _warnings.Add("<image> data shorter than width*height; skipped.");
                return;
            }

            // GS v 0 m xL xH yL yH  (m=0 normal)
            Emit(0x1D, 0x76, 0x30, 0x00);
            Emit((byte)(bytesPerRow & 0xFF), (byte)((bytesPerRow >> 8) & 0xFF));
            Emit((byte)(height & 0xFF), (byte)((height >> 8) & 0xFF));
            _out.Write(raster, 0, expected);
        }

        // ------------------------------------------------------------------ raw command

        private void DoCommand(XmlElement el)
        {
            string hex = (el.InnerText ?? "").Trim();
            byte[] bytes = FromHex(hex);
            if (bytes != null) _out.Write(bytes, 0, bytes.Length);
            else _warnings.Add("<command> is not valid hex; skipped.");
        }

        private static byte[] FromHex(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return new byte[0];
            var clean = new StringBuilder();
            foreach (char c in hex) if (!char.IsWhiteSpace(c)) clean.Append(c);
            string s = clean.ToString();
            if (s.Length % 2 != 0) return null;
            var result = new byte[s.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                if (!byte.TryParse(s.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result[i]))
                    return null;
            }
            return result;
        }

        // ------------------------------------------------------------------ helpers

        private void Init() { Emit(0x1B, 0x40); } // ESC @
        private void Emit(params byte[] bytes) { _out.Write(bytes, 0, bytes.Length); }

        private static bool HasAttr(XmlElement el, string name) { return el.HasAttribute(name); }
        private static string GetAttr(XmlElement el, string name) { return el.HasAttribute(name) ? el.GetAttribute(name) : null; }

        private static bool GetBool(XmlElement el, string name)
        {
            string v = GetAttr(el, name);
            return v == "true" || v == "1";
        }

        private static int GetInt(XmlElement el, string name, int fallback)
        {
            int v;
            return int.TryParse(GetAttr(el, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
        private static int ClampByte(int v) { return Clamp(v, 0, 255); }

        private static Encoding GetEncoding(int codepage)
        {
            try { return Encoding.GetEncoding(codepage); }
            catch { try { return Encoding.GetEncoding(437); } catch { return Encoding.ASCII; } }
        }
    }
}
