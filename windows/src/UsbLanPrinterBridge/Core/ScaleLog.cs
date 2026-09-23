using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace UsbLanPrinterBridge.Core
{
    public enum ScaleLevel { Info, Warn, Error }

    /// <summary>One thing that happened on the scale channel: connected, a weight, a read error, a served request.</summary>
    public sealed class ScaleEvent
    {
        public DateTime Time { get; set; }
        public ScaleLevel Level { get; set; }
        public string What { get; set; }
        public string Detail { get; set; }

        public string TimeText { get { return Time.ToString("HH:mm:ss"); } }

        public string Line
        {
            get
            {
                string tag = Level == ScaleLevel.Error ? "ERROR" : Level == ScaleLevel.Warn ? "WARN " : "INFO ";
                return Time.ToString("yyyy-MM-dd HH:mm:ss") + "  " + tag + "  " + (What ?? "")
                       + (string.IsNullOrEmpty(Detail) ? "" : "  --  " + Detail);
            }
        }
    }

    /// <summary>
    /// The scale's own log, kept separate from the printer logs on purpose: this one is about the weighing channel
    /// (the port, the connection, the readings). In memory for the window, appended to a daily file in the log folder.
    /// </summary>
    public static class ScaleLog
    {
        private const int MaxInMemory = 1000;

        private static readonly object Gate = new object();
        private static readonly List<ScaleEvent> Records = new List<ScaleEvent>();
        private static int _errors;
        private static long _total;

        public static bool FileLoggingEnabled = true;

        public static event Action<ScaleEvent> EventAdded;

        public static List<ScaleEvent> Snapshot() { lock (Gate) return new List<ScaleEvent>(Records); }
        public static int Count { get { lock (Gate) return Records.Count; } }
        public static int ErrorCount { get { return Volatile.Read(ref _errors); } }
        public static long TotalAdded { get { return Interlocked.Read(ref _total); } }

        public static void Clear() { lock (Gate) { Records.Clear(); _errors = 0; } }

        public static ScaleEvent Info(string what, string detail) { return Add(ScaleLevel.Info, what, detail); }
        public static ScaleEvent Warn(string what, string detail) { return Add(ScaleLevel.Warn, what, detail); }
        public static ScaleEvent Error(string what, string detail) { return Add(ScaleLevel.Error, what, detail); }

        public static ScaleEvent Add(ScaleLevel level, string what, string detail)
        {
            var e = new ScaleEvent { Time = DateTime.Now, Level = level, What = what ?? "", Detail = detail ?? "" };
            lock (Gate)
            {
                Records.Add(e);
                if (level == ScaleLevel.Error) _errors++;
                if (Records.Count > MaxInMemory)
                {
                    int drop = Records.Count - MaxInMemory;
                    for (int i = 0; i < drop; i++) if (Records[i].Level == ScaleLevel.Error) _errors--;
                    Records.RemoveRange(0, drop);
                }
            }
            Interlocked.Increment(ref _total);
            if (FileLoggingEnabled) AppendFile(e);
            Action<ScaleEvent> h = EventAdded;
            if (h != null) { try { h(e); } catch { } }
            return e;
        }

        public static string FilePathFor(DateTime day)
        {
            return Path.Combine(ConfigStore.LogDirectory, "scale-" + day.ToString("yyyyMMdd") + ".log");
        }

        private static void AppendFile(ScaleEvent e)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(ConfigStore.LogDirectory);
                    using (var w = new StreamWriter(new FileStream(FilePathFor(e.Time), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)))
                        w.WriteLine(e.Line);
                }
            }
            catch { /* logging must never break the scale */ }
        }
    }
}
