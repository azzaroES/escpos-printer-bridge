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
            private IntPtr _handle;
            private bool _completed;
            private bool _failed;

            public uint JobId { get; private set; }

            public SpoolerJob(string printerName, string documentName, string outputFile)
            {
                _handle = RawPrinterHelper.Open(printerName);
                try
                {
                    JobId = RawPrinterHelper.StartRawDocument(_handle, documentName, outputFile);
                }
                catch
                {
                    RawPrinterHelper.Close(_handle);
                    _handle = IntPtr.Zero;
                    throw;
                }
            }

            public void Write(byte[] buffer, int offset, int count)
            {
                if (_handle == IntPtr.Zero) throw new ObjectDisposedException("SpoolerJob");
                try
                {
                    RawPrinterHelper.Write(_handle, buffer, offset, count);
                }
                catch
                {
                    _failed = true;
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
                finally
                {
                    RawPrinterHelper.Close(_handle);
                    _handle = IntPtr.Zero;
                }
            }

            public void Abort()
            {
                if (_completed || _handle == IntPtr.Zero) return;
                _completed = true;
                try
                {
                    RawPrinterHelper.AbortDocument(_handle);
                }
                finally
                {
                    RawPrinterHelper.Close(_handle);
                    _handle = IntPtr.Zero;
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
