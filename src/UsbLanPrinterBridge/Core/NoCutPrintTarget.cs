using System;
using System.Threading;

namespace UsbLanPrinterBridge.Core
{
    /// <summary>
    /// Settings shared by every NO CUT filter: how much to feed in place of a removed cut, and a running count.
    /// Whether the filter is on is decided per printer (<see cref="MappingConfig.NoCut"/>), not here.
    /// </summary>
    public static class NoCutSettings
    {
        private static int _feedLines = 4;
        private static long _cutsRemoved;

        public const int MaxFeedLines = 30;

        /// <summary>Lines fed in place of each removed cut, so the receipt clears the tear bar. 0 = nothing.</summary>
        public static int FeedLines
        {
            get { return _feedLines; }
            set { _feedLines = value < 0 ? 0 : value > MaxFeedLines ? MaxFeedLines : value; }
        }

        public static long CutsRemoved { get { return Interlocked.Read(ref _cutsRemoved); } }

        internal static void CountRemoved() { Interlocked.Increment(ref _cutsRemoved); }
    }

    /// <summary>
    /// Wraps a print target so the NO CUT switch of its printer applies to it: while the switch is on, every
    /// cutter command is removed from every job written through it, whatever the sending app asked for.
    /// Every path through the bridge, raw 9100, ePOS and the test pages, ends in a print target, so this one
    /// wrapper covers them all.
    ///
    /// The switch is read on every write, not once per job, so ticking it takes effect immediately, even on
    /// a job that is already streaming.
    /// </summary>
    public sealed class NoCutPrintTarget : IPrintTarget
    {
        private readonly IPrintTarget _inner;
        private readonly Func<bool> _enabled;

        /// <param name="enabled">Asked on every write whether cuts are to be removed right now.</param>
        public NoCutPrintTarget(IPrintTarget inner, Func<bool> enabled)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            if (enabled == null) throw new ArgumentNullException("enabled");
            _inner = inner;
            _enabled = enabled;
        }

        public IPrintTarget Inner { get { return _inner; } }

        public static IPrintTarget Unwrap(IPrintTarget target)
        {
            var w = target as NoCutPrintTarget;
            return w == null ? target : w._inner;
        }

        public string Name { get { return _inner.Name; } }

        public IPrintJob StartJob(string documentName)
        {
            return new Job(_inner.StartJob(documentName), _inner.Name, documentName, _enabled);
        }

        private sealed class Job : IPrintJob
        {
            private readonly IPrintJob _inner;
            private readonly string _printer;
            private readonly string _source;
            private readonly Func<bool> _enabled;
            private EscPosCutFilter _filter;
            private bool _done;

            public Job(IPrintJob inner, string printer, string source, Func<bool> enabled)
            {
                _inner = inner;
                _printer = printer;
                _source = source ?? "";
                _enabled = enabled;
            }

            public void Write(byte[] buffer, int offset, int count)
            {
                if (count <= 0) return;
                bool on;
                try { on = _enabled(); } catch { on = false; }
                if (on)
                {
                    if (_filter == null)
                    {
                        _filter = new EscPosCutFilter();
                        _filter.CutRemoved += OnCutRemoved;
                    }
                    _filter.FeedLines = NoCutSettings.FeedLines;
                    _filter.Filter(buffer, offset, count);
                    if (_filter.OutputLength > 0) _inner.Write(_filter.Output, 0, _filter.OutputLength);
                    return;
                }

                // Switched off while this job was streaming: release whatever the filter was holding, then pass through.
                if (_filter != null) FlushFilter();
                _inner.Write(buffer, offset, count);
            }

            private void OnCutRemoved(string command)
            {
                NoCutSettings.CountRemoved();
                int feed = NoCutSettings.FeedLines;
                PrinterActionLog.Info(_printer, _source, "Cut removed (NO CUT is on for this printer)",
                    command + " was not sent to the printer" + (feed > 0 ? "; a " + feed + "-line feed was sent instead so the paper reaches the tear bar." : "."));
            }

            private void FlushFilter()
            {
                EscPosCutFilter f = _filter;
                _filter = null;
                f.Flush();
                if (f.OutputLength > 0) _inner.Write(f.Output, 0, f.OutputLength);
            }

            public void Complete()
            {
                if (_done) return;
                _done = true;
                if (_filter != null) FlushFilter();
                _inner.Complete();
            }

            public void Abort()
            {
                _done = true;
                _filter = null;
                _inner.Abort();
            }

            public void Dispose()
            {
                if (!_done && _filter != null)
                {
                    try { FlushFilter(); } catch { /* the inner job decides how to end on dispose */ }
                }
                _done = true;
                _inner.Dispose();
            }
        }
    }
}
