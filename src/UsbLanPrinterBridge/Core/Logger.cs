using System;
using System.IO;
using System.Text;

namespace UsbLanPrinterBridge.Core
{
    public enum LogLevel { Info, Warn, Error }

    public sealed class LogEntry
    {
        public DateTime Time { get; set; }
        public LogLevel Level { get; set; }
        public string Message { get; set; }

        public string Line
        {
            get
            {
                string tag = Level == LogLevel.Error ? "ERROR" : Level == LogLevel.Warn ? "WARN " : "INFO ";
                return Time.ToString("yyyy-MM-dd HH:mm:ss") + "  " + tag + "  " + Message;
            }
        }
    }

    /// <summary>Process-wide log: raises an event for the UI and appends to a daily file under the data directory.</summary>
    public static class Logger
    {
        private static readonly object Gate = new object();
        private static string _currentFile;
        private static StreamWriter _writer;
        private static DateTime _currentDay;

        public static bool FileLoggingEnabled = true;

        public static event Action<LogEntry> EntryAdded;

        public static void Info(string message) { Write(LogLevel.Info, message); }
        public static void Warn(string message) { Write(LogLevel.Warn, message); }
        public static void Error(string message) { Write(LogLevel.Error, message); }

        public static void Error(string message, Exception ex)
        {
            Write(LogLevel.Error, message + (ex == null ? "" : ": " + ex.Message));
        }

        public static void Write(LogLevel level, string message)
        {
            var entry = new LogEntry { Time = DateTime.Now, Level = level, Message = message ?? "" };

            if (FileLoggingEnabled)
            {
                lock (Gate)
                {
                    try
                    {
                        EnsureWriter(entry.Time);
                        if (_writer != null)
                        {
                            _writer.WriteLine(entry.Line);
                            _writer.Flush();
                        }
                    }
                    catch
                    {
                        // never let logging take the bridge down
                    }
                }
            }

            var h = EntryAdded;
            if (h != null) { try { h(entry); } catch { } }
        }

        private static void EnsureWriter(DateTime now)
        {
            if (_writer != null && _currentDay == now.Date) return;
            if (_writer != null) { try { _writer.Dispose(); } catch { } _writer = null; }

            string dir = ConfigStore.LogDirectory;
            Directory.CreateDirectory(dir);
            _currentDay = now.Date;
            _currentFile = Path.Combine(dir, "bridge-" + now.ToString("yyyyMMdd") + ".log");
            _writer = new StreamWriter(new FileStream(_currentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            CleanupOldLogs(dir, 14);
        }

        private static void CleanupOldLogs(string dir, int keepDays)
        {
            try
            {
                foreach (string file in Directory.GetFiles(dir, "bridge-*.log"))
                {
                    if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-keepDays))
                        File.Delete(file);
                }
            }
            catch { }
        }

        public static void Shutdown()
        {
            lock (Gate)
            {
                if (_writer != null) { try { _writer.Dispose(); } catch { } _writer = null; }
            }
        }
    }
}
