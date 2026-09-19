using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace UsbLanPrinterBridge.Core
{
    /// <summary>One completed (or failed) print job.</summary>
    public sealed class PrintRecord
    {
        public DateTime Time { get; set; }
        /// <summary>Address of the device that sent the job.</summary>
        public string Source { get; set; }
        public string Printer { get; set; }
        /// <summary>How it arrived: "raw 9100", "ePOS http", "ePOS https", "test".</summary>
        public string Path { get; set; }
        public long Bytes { get; set; }
        /// <summary>"Printed", or the reason it did not.</summary>
        public string Status { get; set; }
        /// <summary>Readable text pulled out of the ESC/POS stream, flattened to one line for the grid and the CSV.</summary>
        public string Preview { get; set; }
        /// <summary>The ticket as it would look on paper, line by line, with markers for images, barcodes, QR codes and the cut.</summary>
        public string Ticket { get; set; }

        public bool Failed { get { return !string.Equals(Status, "Printed", StringComparison.OrdinalIgnoreCase); } }

        public string TimeText { get { return Time.ToString("yyyy-MM-dd HH:mm:ss"); } }
    }

    /// <summary>
    /// Keeps a record of every job the bridge handled: when, from whom, how big, and a readable extract of the
    /// text that went to the printer. Held in memory for the window and appended to a daily CSV so it survives
    /// restarts.
    ///
    /// Raw dumps can be enabled to write the exact bytes of each job to disk, which is what you want when a
    /// client is rejected and the question is what it actually sent.
    /// </summary>
    public static class PrintHistory
    {
        private const int MaxInMemory = 1000;
        private const int PreviewChars = 400;

        private static readonly object Gate = new object();
        private static readonly List<PrintRecord> Records = new List<PrintRecord>();
        private static string _csvDay;

        /// <summary>When true, the raw bytes of every job are written to logs\jobs for inspection.</summary>
        public static bool SaveRawJobs { get; set; }

        public static event Action<PrintRecord> RecordAdded;

        public static string JobDumpDirectory { get { return Path.Combine(ConfigStore.LogDirectory, "jobs"); } }

        public static List<PrintRecord> Snapshot()
        {
            lock (Gate) return new List<PrintRecord>(Records);
        }

        public static int Count
        {
            get { lock (Gate) return Records.Count; }
        }

        public static void Clear()
        {
            lock (Gate) Records.Clear();
        }

        public static PrintRecord Add(string source, string printer, string path, byte[] data, int length, string status)
        {
            var record = new PrintRecord
            {
                Time = DateTime.Now,
                Source = source ?? "?",
                Printer = printer ?? "?",
                Path = path ?? "",
                Bytes = length,
                Status = status ?? "Printed",
                Preview = ExtractText(data, length, PreviewChars),
                Ticket = SafeRender(data, length)
            };

            lock (Gate)
            {
                Records.Add(record);
                if (Records.Count > MaxInMemory) Records.RemoveRange(0, Records.Count - MaxInMemory);
            }

            AppendCsv(record);
            if (SaveRawJobs && data != null && length > 0) DumpRaw(record, data, length);

            Action<PrintRecord> handler = RecordAdded;
            if (handler != null) { try { handler(record); } catch { } }
            return record;
        }

        private static string SafeRender(byte[] data, int length)
        {
            try { return EscPosTicketText.Render(data, length); }
            catch { return ""; }
        }

        /// <summary>
        /// Pulls the human-readable part out of an ESC/POS stream. Control bytes and escape sequences are
        /// dropped, so what is left is roughly the text the customer sees on the receipt.
        /// </summary>
        public static string ExtractText(byte[] data, int length, int maxChars)
        {
            if (data == null || length <= 0) return "";
            var sb = new StringBuilder();
            bool lastWasSpace = false;

            for (int i = 0; i < length && sb.Length < maxChars; i++)
            {
                byte b = data[i];

                if (b == 0x1B || b == 0x1D) // ESC or GS: skip the command and its most common operands
                {
                    i += SkipCommand(data, length, i);
                    continue;
                }

                if (b == 0x0A || b == 0x0D)
                {
                    if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
                    continue;
                }

                if (b >= 0x20 && b <= 0x7E)
                {
                    char c = (char)b;
                    if (c == ' ')
                    {
                        if (lastWasSpace) continue;
                        lastWasSpace = true;
                    }
                    else lastWasSpace = false;
                    sb.Append(c);
                }
                else if (b >= 0xA0)
                {
                    // Accented characters from a code page; show them rather than dropping the word.
                    sb.Append((char)b);
                    lastWasSpace = false;
                }
            }

            string text = sb.ToString().Trim();
            if (text.Length >= maxChars) text += "...";
            return text;
        }

        /// <summary>Number of extra bytes to skip for a command starting at index i. Conservative, not exhaustive.</summary>
        private static int SkipCommand(byte[] data, int length, int i)
        {
            byte lead = data[i];
            byte next = (i + 1 < length) ? data[i + 1] : (byte)0;

            if (lead == 0x1B)
            {
                switch (next)
                {
                    case 0x40: return 1;                 // ESC @
                    case 0x61: case 0x4D: case 0x2D:     // ESC a / M / -
                    case 0x45: case 0x47: case 0x7B:     // ESC E / G / {
                    case 0x33: case 0x4A: case 0x64:     // ESC 3 / J / d
                    case 0x74: case 0x56: case 0x21:     // ESC t / V / !
                        return 2;
                    case 0x70: return 4;                 // ESC p m t1 t2
                    default: return 1;
                }
            }

            // GS
            switch (next)
            {
                case 0x21: case 0x42: case 0x62:         // GS ! / B / b
                case 0x48: case 0x66: case 0x77:         // GS H / f / w
                case 0x68: case 0x72: case 0x49:         // GS h / r / I
                case 0x61:                                // GS a
                    return 2;
                case 0x56: return 3;                     // GS V m (n)
                case 0x6B: return 2;                     // GS k: data follows, handled as text below
                default: return 1;
            }
        }

        private static void AppendCsv(PrintRecord r)
        {
            try
            {
                string dir = ConfigStore.LogDirectory;
                Directory.CreateDirectory(dir);
                string day = r.Time.ToString("yyyyMMdd");
                string file = Path.Combine(dir, "prints-" + day + ".csv");

                bool isNew = !File.Exists(file);
                using (var writer = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(true)))
                {
                    if (isNew || _csvDay != day)
                    {
                        if (isNew) writer.WriteLine("Time,Source,Printer,Path,Bytes,Status,Text");
                        _csvDay = day;
                    }
                    writer.WriteLine(string.Join(",",
                        Csv(r.TimeText), Csv(r.Source), Csv(r.Printer), Csv(r.Path),
                        r.Bytes.ToString(CultureInfo.InvariantCulture), Csv(r.Status), Csv(r.Preview)));
                }
            }
            catch
            {
                // logging must never break printing
            }
        }

        private static string Csv(string value)
        {
            if (value == null) return "\"\"";
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static void DumpRaw(PrintRecord r, byte[] data, int length)
        {
            try
            {
                Directory.CreateDirectory(JobDumpDirectory);
                string name = r.Time.ToString("yyyyMMdd-HHmmss-fff") + "_" + Sanitize(r.Source) + ".bin";
                using (var fs = new FileStream(Path.Combine(JobDumpDirectory, name), FileMode.Create, FileAccess.Write))
                {
                    fs.Write(data, 0, length);
                }
            }
            catch
            {
            }
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '-');
            return sb.ToString();
        }

        /// <summary>Formats bytes as a readable hex dump, used when tracing what a client sent.</summary>
        public static string HexDump(byte[] data, int length, int maxBytes)
        {
            if (data == null || length <= 0) return "(no data)";
            int count = Math.Min(length, maxBytes);
            var sb = new StringBuilder();
            for (int i = 0; i < count; i += 16)
            {
                var hex = new StringBuilder();
                var text = new StringBuilder();
                for (int j = i; j < i + 16 && j < count; j++)
                {
                    hex.Append(data[j].ToString("X2")).Append(' ');
                    text.Append(data[j] >= 0x20 && data[j] <= 0x7E ? (char)data[j] : '.');
                }
                sb.Append(hex.ToString().PadRight(48)).Append(' ').Append(text).AppendLine();
            }
            if (length > count) sb.Append("... ").Append(length - count).Append(" more byte(s)");
            return sb.ToString().TrimEnd();
        }
    }
}
