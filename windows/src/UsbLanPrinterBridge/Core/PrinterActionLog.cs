using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace UsbLanPrinterBridge.Core
{
    public enum ActionLevel { Info, Warn, Error }

    /// <summary>One thing that happened to a printer: a job, a removed cut, a spooler error, a status change.</summary>
    public sealed class PrinterAction
    {
        public DateTime Time { get; set; }
        public ActionLevel Level { get; set; }
        /// <summary>The Windows printer queue concerned.</summary>
        public string Printer { get; set; }
        /// <summary>Who caused it: the client address, "ePOS 192.168.1.5", "spooler", "watch".</summary>
        public string Source { get; set; }
        /// <summary>Short headline: "Cut removed", "Printed", "Printer offline".</summary>
        public string What { get; set; }
        /// <summary>The details: which command, what the job contained, the Windows error text.</summary>
        public string Detail { get; set; }
        /// <summary>For job entries: the ticket as it would look on paper (see <see cref="EscPosTicketText"/>). Empty otherwise.</summary>
        public string Ticket { get; set; }

        public string TimeText { get { return Time.ToString("HH:mm:ss"); } }

        public bool HasTicket { get { return !string.IsNullOrEmpty(Ticket); } }

        public string Line
        {
            get
            {
                string tag = Level == ActionLevel.Error ? "ERROR" : Level == ActionLevel.Warn ? "WARN " : "INFO ";
                return Time.ToString("yyyy-MM-dd HH:mm:ss") + "  " + tag + "  [" + Printer + "]  " + Source + ": " + What
                       + (string.IsNullOrEmpty(Detail) ? "" : "  --  " + Detail);
            }
        }

        /// <summary>The line plus the ticket, indented, for the file and the clipboard.</summary>
        public string FullText
        {
            get
            {
                if (!HasTicket) return Line;
                var sb = new StringBuilder(Line);
                sb.AppendLine();
                sb.AppendLine("      +----------------------------------------------------");
                foreach (string l in Ticket.Split('\n')) sb.Append("      | ").AppendLine(l.TrimEnd('\r'));
                sb.Append("      +----------------------------------------------------");
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// The printer actions log: what the bridge did to each job on its way to the printer and what the printer
    /// (through the Windows spooler) said about it. This is separate from the general log on purpose: the general
    /// log is about the bridge (addresses, ports, firewall), this one is about the printer and the paper.
    ///
    /// Held in memory for the window and appended to a daily file in the log folder.
    /// </summary>
    public static class PrinterActionLog
    {
        private const int MaxInMemory = 2000;

        private static readonly object Gate = new object();
        private static readonly List<PrinterAction> Records = new List<PrinterAction>();
        private static int _errors;
        private static int _warnings;
        private static long _total;

        public static bool FileLoggingEnabled = true;

        public static event Action<PrinterAction> ActionAdded;

        public static List<PrinterAction> Snapshot()
        {
            lock (Gate) return new List<PrinterAction>(Records);
        }

        public static int Count { get { lock (Gate) return Records.Count; } }
        public static int ErrorCount { get { return Volatile.Read(ref _errors); } }
        public static int WarningCount { get { return Volatile.Read(ref _warnings); } }
        /// <summary>Every action ever added this session, including ones that scrolled out of memory.</summary>
        public static long TotalAdded { get { return Interlocked.Read(ref _total); } }

        public static void Clear()
        {
            lock (Gate)
            {
                Records.Clear();
                _errors = 0;
                _warnings = 0;
            }
        }

        public static PrinterAction Info(string printer, string source, string what, string detail) { return Add(ActionLevel.Info, printer, source, what, detail); }
        public static PrinterAction Warn(string printer, string source, string what, string detail) { return Add(ActionLevel.Warn, printer, source, what, detail); }
        public static PrinterAction Error(string printer, string source, string what, string detail) { return Add(ActionLevel.Error, printer, source, what, detail); }

        public static PrinterAction Add(ActionLevel level, string printer, string source, string what, string detail)
        {
            return Add(level, printer, source, what, detail, null);
        }

        /// <param name="ticket">For jobs: the rendered ticket text, shown in the detail pane and written to the file.</param>
        public static PrinterAction Add(ActionLevel level, string printer, string source, string what, string detail, string ticket)
        {
            var a = new PrinterAction
            {
                Time = DateTime.Now,
                Level = level,
                Printer = printer ?? "?",
                Source = source ?? "",
                What = what ?? "",
                Detail = detail ?? "",
                Ticket = ticket ?? ""
            };

            lock (Gate)
            {
                Records.Add(a);
                if (level == ActionLevel.Error) _errors++;
                else if (level == ActionLevel.Warn) _warnings++;
                if (Records.Count > MaxInMemory)
                {
                    int drop = Records.Count - MaxInMemory;
                    for (int i = 0; i < drop; i++)
                    {
                        if (Records[i].Level == ActionLevel.Error) _errors--;
                        else if (Records[i].Level == ActionLevel.Warn) _warnings--;
                    }
                    Records.RemoveRange(0, drop);
                }
            }
            Interlocked.Increment(ref _total);

            if (FileLoggingEnabled) AppendFile(a);

            Action<PrinterAction> h = ActionAdded;
            if (h != null) { try { h(a); } catch { } }
            return a;
        }

        public static string FilePathFor(DateTime day)
        {
            return Path.Combine(ConfigStore.LogDirectory, "printer-actions-" + day.ToString("yyyyMMdd") + ".log");
        }

        private static void AppendFile(PrinterAction a)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(ConfigStore.LogDirectory);
                    using (var w = new StreamWriter(new FileStream(FilePathFor(a.Time), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)))
                        w.WriteLine(a.FullText);
                }
            }
            catch
            {
                // logging must never break printing
            }
        }
    }
}
