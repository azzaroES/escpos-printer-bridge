using System;
using System.Collections.Generic;
using System.IO;

namespace UsbLanPrinterBridge.Core
{
    /// <summary>One open print job. Bytes written in order; Complete() releases it to the printer.</summary>
    public interface IPrintJob : IDisposable
    {
        void Write(byte[] buffer, int offset, int count);
        /// <summary>Ends the job normally. Safe to call once; Dispose() calls it if it hasn't been called.</summary>
        void Complete();
        /// <summary>Discards the job (used when the printer stopped accepting data mid-job).</summary>
        void Abort();
    }

    /// <summary>Something that can accept print jobs: the Windows spooler, or an in-memory sink for tests.</summary>
    public interface IPrintTarget
    {
        string Name { get; }
        IPrintJob StartJob(string documentName);
    }

    /// <summary>Sends RAW jobs to a Windows printer queue through winspool.drv.</summary>
    public sealed class SpoolerPrintTarget : IPrintTarget
    {
        private readonly string _printerName;
        private readonly string _outputFile;

        public SpoolerPrintTarget(string printerName) : this(printerName, null) { }

        /// <param name="outputFile">Optional. When set, the spooler redirects the job to this file ("print to file").</param>
        public SpoolerPrintTarget(string printerName, string outputFile)
        {
            if (string.IsNullOrWhiteSpace(printerName)) throw new ArgumentException("Printer name is empty.", "printerName");
            _printerName = printerName;
            _outputFile = outputFile;
        }

        public string Name { get { return _printerName; } }

        public IPrintJob StartJob(string documentName)
        {
            return new SpoolerJob(_printerName, documentName, _outputFile);
        }

        /// <summary>Convenience for one-shot jobs such as test pages.</summary>
        public void PrintBytes(string documentName, byte[] data)
        {
            using (IPrintJob job = StartJob(documentName))
            {
                job.Write(data, 0, data.Length);
                job.Complete();
            }
        }

        private sealed class SpoolerJob : IPrintJob
        {
            private readonly string _printerName;
            private readonly string _documentName;
            private IntPtr _handle;
            private bool _completed;
            private bool _failed;
            private long _bytes;

            public uint JobId { get; private set; }

            public SpoolerJob(string printerName, string documentName, string outputFile)
            {
                _printerName = printerName;
                _documentName = documentName ?? "";
                try
                {
                    _handle = RawPrinterHelper.Open(printerName);
                }
                catch (Exception ex)
                {
                    PrinterActionLog.Error(printerName, _documentName, "Cannot open the printer queue", Explain(ex));
                    throw;
                }
                try
                {
                    JobId = RawPrinterHelper.StartRawDocument(_handle, documentName, outputFile);
                }
                catch (Exception ex)
                {
                    RawPrinterHelper.Close(_handle);
                    _handle = IntPtr.Zero;
                    PrinterActionLog.Error(printerName, _documentName, "Windows refused to start the job", Explain(ex));
                    throw;
                }
            }

            public void Write(byte[] buffer, int offset, int count)
            {
                if (_handle == IntPtr.Zero) throw new ObjectDisposedException("SpoolerJob");
                try
                {
                    RawPrinterHelper.Write(_handle, buffer, offset, count);
                    _bytes += count;
                }
                catch (Exception ex)
                {
                    _failed = true;
                    PrinterActionLog.Error(_printerName, _documentName, "Printer stopped accepting data after " + _bytes + " bytes", Explain(ex));
                    throw;
                }
            }

            public void Complete()
            {
                if (_completed || _handle == IntPtr.Zero) return;
                _completed = true;
                try
                {
                    RawPrinterHelper.EndRawDocument(_handle);
                }
                catch (Exception ex)
                {
                    PrinterActionLog.Error(_printerName, _documentName, "Windows could not finish the job", Explain(ex));
                    throw;
                }
                finally
                {
                    RawPrinterHelper.Close(_handle);
                    _handle = IntPtr.Zero;
                    // The job is in the Windows queue now; this is the moment to notice a printer that is not taking it.
                    try { PrinterWatch.Check(_printerName, "spooler"); } catch { }
                }
            }

            public void Abort()
            {
                if (_completed || _handle == IntPtr.Zero) return;
                _completed = true;
                try
                {
                    RawPrinterHelper.AbortDocument(_handle);
                    PrinterActionLog.Warn(_printerName, _documentName, "Job discarded", _bytes + " bytes had been written to the queue; the job was cancelled and will not print.");
                }
                finally
                {
                    RawPrinterHelper.Close(_handle);
                    _handle = IntPtr.Zero;
                }
            }

            /// <summary>Adds what the Win32 error usually means for a receipt printer.</summary>
            private static string Explain(Exception ex)
            {
                string msg = ex.Message;
                var w = ex as System.ComponentModel.Win32Exception;
                if (w == null) return msg;
                switch (w.NativeErrorCode)
                {
                    case 1801: return msg + " -> the printer queue does not exist under this name any more. Pick the printer again in the mapping.";
                    case 5: return msg + " -> access denied: the queue's security settings do not allow this account to print.";
                    case 1722: case 1723: case 1727: return msg + " -> the Print Spooler service is not running or was restarted. Start it (services.msc) and try again.";
                    case 6: return msg + " -> the printer handle is no longer valid; the queue may have been deleted or the spooler restarted.";
                    case 1906: return msg + " -> the printer or port is offline.";
                    default: return msg;
                }
            }

            public void Dispose()
            {
                if (_completed || _handle == IntPtr.Zero) return;
                if (_failed)
                {
                    Abort();
                    return;
                }
                try { Complete(); }
                catch { /* best effort on dispose */ }
            }
        }
    }

    /// <summary>Collects jobs in memory. Used by the self-test harness and by the "dry run" mode.</summary>
    public sealed class MemoryPrintTarget : IPrintTarget
    {
        private readonly object _gate = new object();
        private readonly List<byte[]> _jobs = new List<byte[]>();

        public MemoryPrintTarget() : this("memory") { }
        public MemoryPrintTarget(string name) { Name = name; }

        public string Name { get; private set; }

        public event Action<byte[]> JobCompleted;

        public IPrintJob StartJob(string documentName)
        {
            return new MemoryJob(this);
        }

        public byte[][] Jobs
        {
            get { lock (_gate) return _jobs.ToArray(); }
        }

        public int JobCount
        {
            get { lock (_gate) return _jobs.Count; }
        }

        private void Add(byte[] data)
        {
            lock (_gate) _jobs.Add(data);
            var handler = JobCompleted;
            if (handler != null) handler(data);
        }

        private sealed class MemoryJob : IPrintJob
        {
            private readonly MemoryPrintTarget _owner;
            private readonly MemoryStream _buffer = new MemoryStream();
            private bool _done;

            public MemoryJob(MemoryPrintTarget owner) { _owner = owner; }

            public void Write(byte[] buffer, int offset, int count) { _buffer.Write(buffer, offset, count); }

            public void Complete()
            {
                if (_done) return;
                _done = true;
                _owner.Add(_buffer.ToArray());
            }

            public void Abort() { _done = true; }

            public void Dispose() { Complete(); }
        }
    }
}
