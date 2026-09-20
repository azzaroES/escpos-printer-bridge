using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UsbLanPrinterBridge.Core
{
    /// <summary>What the Windows spooler knows about a printer queue right now.</summary>
    public sealed class PrinterQueueStatus
    {
        public string Printer { get; set; }
        /// <summary>False when the queue could not even be opened (not installed, spooler stopped, access denied).</summary>
        public bool Available { get; set; }
        public string Error { get; set; }
        public uint StatusFlags { get; set; }
        public uint Attributes { get; set; }
        /// <summary>Jobs waiting in the Windows queue. More than a couple means the printer is not taking data.</summary>
        public int QueuedJobs { get; set; }

        // PRINTER_STATUS_* (winspool.h)
        private const uint Paused = 0x1, ErrorFlag = 0x2, PendingDeletion = 0x4, PaperJam = 0x8, PaperOut = 0x10,
            ManualFeed = 0x20, PaperProblem = 0x40, Offline = 0x80, OutputBinFull = 0x800, NotAvailable = 0x1000,
            TonerLow = 0x20000, NoToner = 0x40000, UserIntervention = 0x100000, OutOfMemory = 0x200000, DoorOpen = 0x400000,
            ServerUnknown = 0x800000;
        // PRINTER_ATTRIBUTE_WORK_OFFLINE: the "Use Printer Offline" tick in the queue window.
        private const uint WorkOffline = 0x400;

        public bool IsPaused { get { return (StatusFlags & Paused) != 0; } }
        public bool IsWorkOffline { get { return (Attributes & WorkOffline) != 0; } }
        /// <summary>The port monitor reports the printer as offline (unplugged, switched off).</summary>
        public bool IsOffline { get { return (StatusFlags & Offline) != 0; } }

        /// <summary>Everything wrong, in words: "Offline, Paper out". Empty when the queue is fine.</summary>
        public List<string> Problems()
        {
            var p = new List<string>();
            if (!Available) { p.Add(Error ?? "not available"); return p; }
            if ((StatusFlags & Paused) != 0) p.Add("Paused");
            if ((StatusFlags & Offline) != 0) p.Add("Offline");
            if ((Attributes & WorkOffline) != 0) p.Add("\"Use Printer Offline\" is ticked");
            if ((StatusFlags & PaperOut) != 0) p.Add("Paper out");
            if ((StatusFlags & PaperJam) != 0) p.Add("Paper jam");
            if ((StatusFlags & PaperProblem) != 0) p.Add("Paper problem");
            if ((StatusFlags & DoorOpen) != 0) p.Add("Cover open");
            if ((StatusFlags & ErrorFlag) != 0) p.Add("Error");
            if ((StatusFlags & UserIntervention) != 0) p.Add("Needs attention");
            if ((StatusFlags & NotAvailable) != 0) p.Add("Not available");
            if ((StatusFlags & OutputBinFull) != 0) p.Add("Output bin full");
            if ((StatusFlags & ManualFeed) != 0) p.Add("Manual feed");
            if ((StatusFlags & NoToner) != 0) p.Add("No toner/ink");
            if ((StatusFlags & TonerLow) != 0) p.Add("Toner/ink low");
            if ((StatusFlags & OutOfMemory) != 0) p.Add("Out of memory");
            if ((StatusFlags & PendingDeletion) != 0) p.Add("Being deleted");
            if ((StatusFlags & ServerUnknown) != 0) p.Add("Server unknown");
            return p;
        }

        public bool HasProblem { get { return Problems().Count > 0; } }

        public string Describe()
        {
            List<string> p = Problems();
            string text = p.Count == 0 ? "Ready" : string.Join(", ", p.ToArray());
            if (Available && QueuedJobs > 0) text += "; " + QueuedJobs + " job" + (QueuedJobs == 1 ? "" : "s") + " in the Windows queue";
            return text;
        }
    }

    /// <summary>Asks winspool for a queue's status (GetPrinter level 2). Cheap: a local RPC, a few milliseconds.</summary>
    public static class PrinterStatusProbe
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PRINTER_INFO_2
        {
            public IntPtr pServerName;
            public IntPtr pPrinterName;
            public IntPtr pShareName;
            public IntPtr pPortName;
            public IntPtr pDriverName;
            public IntPtr pComment;
            public IntPtr pLocation;
            public IntPtr pDevMode;
            public IntPtr pSepFile;
            public IntPtr pPrintProcessor;
            public IntPtr pDatatype;
            public IntPtr pParameters;
            public IntPtr pSecurityDescriptor;
            public uint Attributes;
            public uint Priority;
            public uint DefaultPriority;
            public uint StartTime;
            public uint UntilTime;
            public uint Status;
            public uint cJobs;
            public uint AveragePPM;
        }

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool OpenPrinter([MarshalAs(UnmanagedType.LPWStr)] string szPrinter, out IntPtr hPrinter, IntPtr pDefault);

        [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetPrinter(IntPtr hPrinter, int level, IntPtr pPrinter, int cbBuf, out int pcbNeeded);

        private const int ErrorInsufficientBuffer = 122;

        public static PrinterQueueStatus Query(string printerName)
        {
            var result = new PrinterQueueStatus { Printer = printerName ?? "?" };
            if (string.IsNullOrWhiteSpace(printerName)) { result.Error = "no printer name"; return result; }

            IntPtr h = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (!OpenPrinter(printerName, out h, IntPtr.Zero) || h == IntPtr.Zero)
                {
                    int code = Marshal.GetLastWin32Error();
                    result.Error = code == 1801 ? "Printer \"" + printerName + "\" is not installed on this PC"
                                                : new Win32Exception(code).Message + " (error " + code + ")";
                    return result;
                }

                int needed;
                GetPrinter(h, 2, IntPtr.Zero, 0, out needed);
                if (needed <= 0)
                {
                    int code = Marshal.GetLastWin32Error();
                    if (code != ErrorInsufficientBuffer) { result.Error = new Win32Exception(code).Message; return result; }
                }
                buffer = Marshal.AllocHGlobal(needed);
                if (!GetPrinter(h, 2, buffer, needed, out needed))
                {
                    result.Error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    return result;
                }
                var info = (PRINTER_INFO_2)Marshal.PtrToStructure(buffer, typeof(PRINTER_INFO_2));
                result.Available = true;
                result.StatusFlags = info.Status;
                result.Attributes = info.Attributes;
                result.QueuedJobs = (int)info.cJobs;
                return result;
            }
            catch (Exception ex)
            {
                result.Available = false;
                result.Error = ex.Message;
                return result;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (h != IntPtr.Zero) ClosePrinter(h);
            }
        }
    }

    /// <summary>
    /// Keeps an eye on the printers in use and writes a line to the printer actions log whenever a queue's
    /// state changes: it went offline, ran out of paper, got paused, stopped taking jobs, or came back.
    /// Only changes are logged, so a healthy printer produces one "Ready" line and then silence.
    /// </summary>
    public static class PrinterWatch
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, string> Last = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Jobs waiting in the queue before it counts as "stuck". One is normal right after a job is sent.</summary>
        public const int StuckQueueJobs = 3;

        public static void Forget(string printer)
        {
            lock (Gate) Last.Remove(printer ?? "");
        }

        /// <summary>Checks every printer named and logs the ones whose state changed. Call from a worker thread.</summary>
        public static void Poll(IEnumerable<string> printers, string source)
        {
            if (printers == null) return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in printers)
            {
                if (string.IsNullOrEmpty(p) || !seen.Add(p)) continue;
                Check(p, source);
            }
        }

        /// <summary>Queries one printer now and logs if its state differs from the last time it was seen.</summary>
        public static PrinterQueueStatus Check(string printer, string source)
        {
            PrinterQueueStatus s = PrinterStatusProbe.Query(printer);
            List<string> problems = s.Problems();
            bool stuck = s.Available && s.QueuedJobs >= StuckQueueJobs;

            string key = string.Join(", ", problems.ToArray()) + (stuck ? "|stuck" : "");
            string previous;
            bool changed;
            lock (Gate)
            {
                changed = !Last.TryGetValue(printer, out previous) || previous != key;
                Last[printer] = key;
            }
            if (!changed) return s;

            if (!s.Available)
            {
                PrinterActionLog.Error(printer, source, "Printer not reachable", s.Error);
            }
            else if (problems.Count > 0 || stuck)
            {
                string what = problems.Count > 0 ? "Printer reports: " + string.Join(", ", problems.ToArray()) : "Printer is not taking jobs";
                string detail = stuck
                    ? s.QueuedJobs + " jobs are waiting in the Windows queue. The printer is not accepting data: check it is switched on, the USB cable is in, the cover is closed and the queue is not paused."
                    : Advice(problems);
                PrinterActionLog.Warn(printer, source, what, detail);
            }
            else if (previous != null)
            {
                PrinterActionLog.Info(printer, source, "Printer ready again", "The Windows queue reports no problem" + (s.QueuedJobs > 0 ? "; " + s.QueuedJobs + " job(s) still spooling" : "") + ".");
            }
            else
            {
                PrinterActionLog.Info(printer, source, "Printer ready", "The Windows queue reports no problem.");
            }
            return s;
        }

        private static string Advice(List<string> problems)
        {
            foreach (string p in problems)
            {
                if (p.StartsWith("Paused")) return "Open the printer's queue window in Windows and choose Printer -> Pause Printing to untick it.";
                if (p.StartsWith("\"Use Printer Offline\"")) return "Open the printer's queue window in Windows and untick Printer -> Use Printer Offline.";
                if (p.StartsWith("Offline")) return "Windows cannot talk to the printer: check power and the USB cable, then wait a few seconds.";
                if (p.StartsWith("Paper out")) return "Load a new paper roll and close the cover.";
                if (p.StartsWith("Paper jam") || p.StartsWith("Paper problem")) return "Open the cover, clear the paper path, close the cover.";
                if (p.StartsWith("Cover open")) return "Close the printer cover.";
            }
            return "See the printer's queue window in Windows for details.";
        }
    }
}
