using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace UsbLanPrinterBridge.Core
{
    /// <summary>How trustworthy a reading is: read directly, best effort, or not something this platform gives an app.</summary>
    public enum ReadingQuality { Live, Estimated, NotAvailable }

    public enum DeviceEventKind { Printed, Warning, Offline, Failed }

    /// <summary>Something printer-related that happened at a known time, so it can be drawn on the same timeline as the graphs.</summary>
    public sealed class DeviceEvent
    {
        public DateTime Time { get; set; }
        public DeviceEventKind Kind { get; set; }
        public string Text { get; set; }
        public string Printer { get; set; }
    }

    public sealed class BatteryInfo
    {
        public bool Present;
        public int Percent;
        public bool Charging;
        public bool OnAc;
        /// <summary>Signed: positive charging, negative discharging. Null when Windows gives no rate.</summary>
        public double? MilliAmps;
        public double? Watts;
        public double? Volts;
        public double? TempC;
        public string RemainingText = "";
        public int? DesignMwh;
        public int? FullMwh;
        public int? CycleCount;
        public string Chemistry = "";
        public string Manufacturer = "";
        public double? WearPercent { get { return DesignMwh.HasValue && FullMwh.HasValue && DesignMwh > 0 ? (double?)Math.Max(0, 100.0 - 100.0 * FullMwh.Value / DesignMwh.Value) : null; } }
        public string StateText { get { return !Present ? "No battery" : Charging ? "Charging" + (RemainingText.Length > 0 ? " · " + RemainingText : "") : (OnAc ? "On mains, not charging" : "Discharging" + (RemainingText.Length > 0 ? " · " + RemainingText : "")); } }
        public string SourceText { get { return OnAc ? "AC adapter" : "Battery"; } }
    }

    /// <summary>An immutable copy of everything the Device tab shows. Histories are oldest first.</summary>
    public sealed class DeviceSnapshot
    {
        public DateTime Time;
        public double CpuPercent; public int Cores; public double CpuGhz; public ReadingQuality CpuQuality;
        public double RamPercent; public double RamUsedGb; public double RamTotalGb;
        public double? GpuPercent; public string GpuName = ""; public double? GpuMemGb; public ReadingQuality GpuQuality;
        public double? CpuTempC; public double? GpuTempC; public double? BatteryTempC; public ReadingQuality TempQuality = ReadingQuality.NotAvailable; public string TempSource = "";
        public string NetLabel = "Network"; public bool NetUp; public string WifiSsid = ""; public int? WifiSignalPercent; public int? WifiSignalDbm; public int? WifiLinkMbps; public double NetRxKBps; public double NetTxKBps;
        public int UsbPrinters; public int UsbPrintersOnline; public string UsbPrinterNames = ""; public double BridgeKBps;
        public int BtPrinters; public int BtPrintersOnline; public string BtPrinterNames = "";
        public BatteryInfo Battery = new BatteryInfo();
        public double?[] Cpu, Ram, Gpu, Wifi, TCpu, TGpu, TBat;
        public bool?[] UsbOn, BtOn;
        public double?[] BatteryLevelByMinute, BatteryMaByMinute;
        public List<DeviceEvent> Events = new List<DeviceEvent>();
        public string TelemetryFile = ""; public long TelemetryBytesToday; public int TelemetryFiles;
    }

    /// <summary>
    /// Reads what this PC can tell about itself once a second, keeps the last minute of it, turns printer actions
    /// into timestamped events, and writes a telemetry row every five seconds. Everything Windows does not expose
    /// to a program without a vendor driver is marked as such rather than guessed.
    ///
    /// All sampling happens on a worker thread; <see cref="Snapshot"/> hands the UI a copy under a lock.
    /// </summary>
    public sealed class DeviceMonitor : IDisposable
    {
        public const int HistorySeconds = 60;
        public const int HistoryMinutes = 60;
        public const int TelemetryEverySeconds = 5;
        public const int KeepTelemetryDays = 14;
        private const int MaxEvents = 200;

        private readonly object _gate = new object();
        private readonly Func<long> _bridgeBytes;
        private System.Threading.Timer _timer;
        private int _tick;
        private bool _sampling;

        // histories, ring buffered
        private readonly double?[] _cpu = new double?[HistorySeconds], _ram = new double?[HistorySeconds], _gpu = new double?[HistorySeconds], _wifi = new double?[HistorySeconds];
        private readonly double?[] _tCpu = new double?[HistorySeconds], _tGpu = new double?[HistorySeconds], _tBat = new double?[HistorySeconds];
        private readonly bool?[] _usbOn = new bool?[HistorySeconds], _btOn = new bool?[HistorySeconds];
        private int _head;
        private readonly double?[] _batLevelMin = new double?[HistoryMinutes], _batMaMin = new double?[HistoryMinutes];
        private int _minHead;
        private DateTime _lastMinuteSample = DateTime.MinValue;

        private readonly List<DeviceEvent> _events = new List<DeviceEvent>();
        private DeviceSnapshot _current = new DeviceSnapshot();

        // sources
        private PerformanceCounter _cpuCounter;
        private readonly Dictionary<string, PerformanceCounter> _gpuCounters = new Dictionary<string, PerformanceCounter>(StringComparer.Ordinal);
        private bool _gpuCategoryChecked, _gpuCategoryExists;
        private DateTime _gpuEnumerated = DateTime.MinValue;
        private string _gpuName = "";
        private string _nvidiaSmi;
        private bool _nvidiaChecked;
        private bool _acpiTempSupported = true;
        private bool _batteryTempSupported = true;
        private NetworkInterface _nic;
        private long _lastRx, _lastTx; private DateTime _lastNetSample = DateTime.MinValue;
        private long _lastBridgeBytes; private DateTime _lastBridgeSample = DateTime.MinValue;
        private List<PrinterInfo> _printers = new List<PrinterInfo>();
        private DateTime _printersEnumerated = DateTime.MinValue;
        private bool _lastUsbOn, _lastBtOn;

        // telemetry
        private StreamWriter _telemetry;
        private DateTime _telemetryDay = DateTime.MinValue;
        private readonly List<string> _pendingEventTexts = new List<string>();

        public event Action Updated;

        /// <param name="bridgeBytes">Total bytes the bridge has received so far, for the "through the bridge" rate.</param>
        public DeviceMonitor(Func<long> bridgeBytes)
        {
            _bridgeBytes = bridgeBytes ?? (() => 0);
            PrinterActionLog.ActionAdded += OnPrinterAction;
        }

        public bool TelemetryEnabled = true;

        public void Start()
        {
            if (_timer != null) return;
            _timer = new System.Threading.Timer(_ => Tick(), null, 200, 1000);
        }

        public void Stop()
        {
            System.Threading.Timer t = _timer;
            _timer = null;
            if (t != null) t.Dispose();
            PrinterActionLog.ActionAdded -= OnPrinterAction;
            lock (_gate) { if (_telemetry != null) { try { _telemetry.Dispose(); } catch { } _telemetry = null; } }
        }

        public void Dispose() { Stop(); }

        public DeviceSnapshot Snapshot()
        {
            lock (_gate) return _current;
        }

        /// <summary>Takes one full sample right now (slow sources included) and writes a telemetry row for it. For diagnostics and the self-tests.</summary>
        public DeviceSnapshot SampleOnce()
        {
            Sample(1);
            DeviceSnapshot s = Snapshot();
            if (TelemetryEnabled) WriteTelemetry(s);
            return s;
        }

        public static string TelemetryDirectory { get { return ConfigStore.LogDirectory; } }

        public static string TelemetryFileFor(DateTime day) { return Path.Combine(TelemetryDirectory, "telemetry-" + day.ToString("yyyyMMdd") + ".csv"); }

        /// <summary>Deletes every telemetry file and forgets the events. A new file starts with the next sample.</summary>
        public int ClearTelemetry()
        {
            int deleted = 0;
            lock (_gate)
            {
                if (_telemetry != null) { try { _telemetry.Dispose(); } catch { } _telemetry = null; }
                _telemetryDay = DateTime.MinValue;
                try
                {
                    if (Directory.Exists(TelemetryDirectory))
                        foreach (string f in Directory.GetFiles(TelemetryDirectory, "telemetry-*.csv"))
                        {
                            try { File.Delete(f); deleted++; } catch { }
                        }
                }
                catch { }
                _events.Clear();
                _pendingEventTexts.Clear();
            }
            PrinterActionLog.Info("all printers", "user", "Telemetry logs cleared", deleted + " file(s) deleted; the event list was emptied. A new telemetry file starts with the next sample.");
            return deleted;
        }

        // ------------------------------------------------------------------ events

        private void OnPrinterAction(PrinterAction a)
        {
            DeviceEvent e = ToEvent(a);
            if (e == null) return;
            lock (_gate)
            {
                _events.Insert(0, e);
                if (_events.Count > MaxEvents) _events.RemoveRange(MaxEvents, _events.Count - MaxEvents);
                _pendingEventTexts.Add(e.Time.ToString("HH:mm:ss") + " " + e.Text);
            }
        }

        /// <summary>Turns a printer action into a timeline event, or null for the ones not worth a marker.</summary>
        public static DeviceEvent ToEvent(PrinterAction a)
        {
            if (a == null) return null;
            string what = a.What ?? "";
            DeviceEventKind kind;
            string text;
            if (what.StartsWith("Sent ", StringComparison.Ordinal))
            {
                kind = DeviceEventKind.Printed;
                text = what.Replace("Sent ", "Printed ").Replace(" to the printer", "") + " · " + a.Printer + " · from " + a.Source;
            }
            else if (what.StartsWith("Cut removed", StringComparison.Ordinal)) { kind = DeviceEventKind.Warning; text = "Cut removed (NO CUT) · " + a.Printer + " · " + FirstClause(a.Detail); }
            else if (what.StartsWith("NO CUT switched", StringComparison.Ordinal)) { kind = DeviceEventKind.Warning; text = what + " · " + a.Printer; }
            else if (what.StartsWith("Cooling", StringComparison.Ordinal) || what.StartsWith("Throttle", StringComparison.Ordinal)) { kind = DeviceEventKind.Warning; text = what + (string.IsNullOrEmpty(a.Detail) ? "" : " · " + FirstClause(a.Detail)); }
            else if (what.StartsWith("Printer reports", StringComparison.Ordinal) || what.StartsWith("Printer is not taking", StringComparison.Ordinal) || what.StartsWith("Printer not reachable", StringComparison.Ordinal)) { kind = DeviceEventKind.Offline; text = what + " · " + a.Printer; }
            else if (what.StartsWith("Printer ready", StringComparison.Ordinal)) { kind = DeviceEventKind.Offline; text = what + " · " + a.Printer; }
            else if (what.StartsWith("ePOS element not printed", StringComparison.Ordinal)) { kind = DeviceEventKind.Warning; text = "ePOS element skipped · " + a.Printer + " · " + FirstClause(a.Detail); }
            else if (what.StartsWith("Answered ", StringComparison.Ordinal) || what.StartsWith("Telemetry", StringComparison.Ordinal)) return null;
            else if (a.Level == ActionLevel.Error) { kind = DeviceEventKind.Failed; text = what + " · " + a.Printer + " · " + FirstClause(a.Detail); }
            else if (a.Level == ActionLevel.Warn) { kind = DeviceEventKind.Warning; text = what + " · " + a.Printer; }
            else return null;
            return new DeviceEvent { Time = a.Time, Kind = kind, Text = text, Printer = a.Printer };
        }

        private static string FirstClause(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int cut = s.IndexOfAny(new[] { ';', '.' });
            string t = cut > 0 ? s.Substring(0, cut) : s;
            return t.Length > 90 ? t.Substring(0, 87) + "..." : t;
        }

        // ------------------------------------------------------------------ sampling

        private void Tick()
        {
            if (_sampling) return;
            _sampling = true;
            try
            {
                int tick = Interlocked.Increment(ref _tick);
                Sample(tick);
            }
            catch (Exception ex)
            {
                Logger.Warn("Device sampling failed: " + ex.Message);
            }
            finally { _sampling = false; }
            Action h = Updated;
            if (h != null) { try { h(); } catch { } }
        }

        private void Sample(int tick)
        {
            var s = new DeviceSnapshot { Time = DateTime.Now };
            DeviceSnapshot prev = Snapshot();
            bool slow = tick % 5 == 1;

            // CPU
            try
            {
                if (_cpuCounter == null) _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                s.CpuPercent = Math.Max(0, Math.Min(100, _cpuCounter.NextValue()));
                s.CpuQuality = ReadingQuality.Live;
            }
            catch { s.CpuPercent = 0; s.CpuQuality = ReadingQuality.NotAvailable; }
            s.Cores = Environment.ProcessorCount;
            s.CpuGhz = slow || prev.CpuGhz == 0 ? ReadCpuGhz(prev.CpuGhz) : prev.CpuGhz;

            // RAM
            var mem = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(mem))
            {
                s.RamTotalGb = mem.ullTotalPhys / 1073741824.0;
                s.RamUsedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
                s.RamPercent = mem.dwMemoryLoad;
            }

            // GPU
            SampleGpu(s, prev, slow);

            // Temperatures
            if (slow || tick < 3) SampleTemps(s); else { s.CpuTempC = prev.CpuTempC; s.GpuTempC = prev.GpuTempC ?? s.GpuTempC; s.BatteryTempC = prev.BatteryTempC; s.TempQuality = prev.TempQuality; s.TempSource = prev.TempSource; }

            // Network
            SampleNetwork(s, prev, slow);

            // Printers on USB / Bluetooth
            SamplePrinters(s, prev, slow);

            // Battery
            SampleBattery(s, prev, slow);

            // Bridge throughput
            long bytes = _bridgeBytes();
            DateTime now = DateTime.Now;
            if (_lastBridgeSample != DateTime.MinValue)
            {
                double secs = Math.Max(0.5, (now - _lastBridgeSample).TotalSeconds);
                s.BridgeKBps = Math.Max(0, bytes - _lastBridgeBytes) / 1024.0 / secs;
            }
            _lastBridgeBytes = bytes; _lastBridgeSample = now;

            lock (_gate)
            {
                _cpu[_head] = s.CpuQuality == ReadingQuality.NotAvailable ? (double?)null : s.CpuPercent;
                _ram[_head] = s.RamPercent;
                _gpu[_head] = s.GpuPercent;
                _wifi[_head] = s.WifiSignalPercent;
                _tCpu[_head] = s.CpuTempC; _tGpu[_head] = s.GpuTempC; _tBat[_head] = s.BatteryTempC;
                _usbOn[_head] = s.UsbPrinters > 0 ? (bool?)(s.UsbPrintersOnline > 0) : null;
                _btOn[_head] = s.BtPrinters > 0 ? (bool?)(s.BtPrintersOnline > 0) : null;
                _head = (_head + 1) % HistorySeconds;

                if (s.Battery.Present && (now - _lastMinuteSample).TotalSeconds >= 60)
                {
                    _batLevelMin[_minHead] = s.Battery.Percent;
                    _batMaMin[_minHead] = s.Battery.MilliAmps;
                    _minHead = (_minHead + 1) % HistoryMinutes;
                    _lastMinuteSample = now;
                }

                s.Cpu = Ordered(_cpu, _head); s.Ram = Ordered(_ram, _head); s.Gpu = Ordered(_gpu, _head); s.Wifi = Ordered(_wifi, _head);
                s.TCpu = Ordered(_tCpu, _head); s.TGpu = Ordered(_tGpu, _head); s.TBat = Ordered(_tBat, _head);
                s.UsbOn = Ordered(_usbOn, _head); s.BtOn = Ordered(_btOn, _head);
                s.BatteryLevelByMinute = Ordered(_batLevelMin, _minHead); s.BatteryMaByMinute = Ordered(_batMaMin, _minHead);
                s.Events = new List<DeviceEvent>(_events);
                _current = s;
            }

            if (TelemetryEnabled && tick % TelemetryEverySeconds == 0) WriteTelemetry(s);
            s.TelemetryFile = TelemetryFileFor(DateTime.Now);
            try
            {
                if (File.Exists(s.TelemetryFile)) s.TelemetryBytesToday = new FileInfo(s.TelemetryFile).Length;
                if (Directory.Exists(TelemetryDirectory)) s.TelemetryFiles = Directory.GetFiles(TelemetryDirectory, "telemetry-*.csv").Length;
            }
            catch { }
        }

        private static T[] Ordered<T>(T[] ring, int head)
        {
            var r = new T[ring.Length];
            for (int i = 0; i < ring.Length; i++) r[i] = ring[(head + i) % ring.Length];
            return r;
        }

        private static double ReadCpuGhz(double previous)
        {
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT CurrentClockSpeed FROM Win32_Processor"))
                    foreach (ManagementBaseObject o in q.Get()) { object v = o["CurrentClockSpeed"]; if (v != null) return Convert.ToDouble(v) / 1000.0; }
            }
            catch { }
            return previous;
        }

        private void SampleGpu(DeviceSnapshot s, DeviceSnapshot prev, bool slow)
        {
            s.GpuName = _gpuName;
            try
            {
                if (!_gpuCategoryChecked)
                {
                    _gpuCategoryChecked = true;
                    _gpuCategoryExists = PerformanceCounterCategory.Exists("GPU Engine");
                    if (_gpuCategoryExists) _gpuName = ReadGpuName();
                    s.GpuName = _gpuName;
                }
                if (!_gpuCategoryExists) { s.GpuPercent = null; s.GpuQuality = ReadingQuality.NotAvailable; return; }

                if ((DateTime.Now - _gpuEnumerated).TotalSeconds > 15)
                {
                    _gpuEnumerated = DateTime.Now;
                    var cat = new PerformanceCounterCategory("GPU Engine");
                    var names = new HashSet<string>(cat.GetInstanceNames().Where(n => n.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) >= 0), StringComparer.Ordinal);
                    foreach (string stale in _gpuCounters.Keys.Where(k => !names.Contains(k)).ToList()) { try { _gpuCounters[stale].Dispose(); } catch { } _gpuCounters.Remove(stale); }
                    foreach (string n in names) if (!_gpuCounters.ContainsKey(n)) { var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", n, true); c.NextValue(); _gpuCounters[n] = c; }
                }
                double total = 0;
                foreach (PerformanceCounter c in _gpuCounters.Values) { try { total += c.NextValue(); } catch { } }
                s.GpuPercent = Math.Min(100, total);
                s.GpuQuality = ReadingQuality.Estimated;
                s.GpuMemGb = slow || prev.GpuMemGb == null ? ReadGpuMemoryGb() : prev.GpuMemGb;
            }
            catch
            {
                s.GpuPercent = null; s.GpuQuality = ReadingQuality.NotAvailable;
            }
        }

        private static string ReadGpuName()
        {
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                {
                    string first = null;
                    foreach (ManagementBaseObject o in q.Get())
                    {
                        string n = Convert.ToString(o["Name"]);
                        if (string.IsNullOrEmpty(n)) continue;
                        if (first == null) first = n;
                        if (n.IndexOf("Basic", StringComparison.OrdinalIgnoreCase) < 0) return n;
                    }
                    return first ?? "";
                }
            }
            catch { return ""; }
        }

        private static double? ReadGpuMemoryGb()
        {
            try
            {
                if (!PerformanceCounterCategory.Exists("GPU Adapter Memory")) return null;
                var cat = new PerformanceCounterCategory("GPU Adapter Memory");
                double total = 0;
                foreach (string inst in cat.GetInstanceNames())
                    using (var c = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", inst, true)) total += c.NextValue();
                return total / 1073741824.0;
            }
            catch { return null; }
        }

        private void SampleTemps(DeviceSnapshot s)
        {
            var sources = new List<string>();
            if (_acpiTempSupported)
            {
                try
                {
                    double best = double.MinValue;
                    using (var q = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                        foreach (ManagementBaseObject o in q.Get())
                        {
                            double c = Convert.ToDouble(o["CurrentTemperature"]) / 10.0 - 273.15;
                            if (c > -20 && c < 150 && c > best) best = c;
                        }
                    if (best > double.MinValue) { s.CpuTempC = Math.Round(best, 1); sources.Add("ACPI thermal zone"); }
                    else _acpiTempSupported = false;
                }
                catch { _acpiTempSupported = false; }
            }
            if (!_nvidiaChecked)
            {
                _nvidiaChecked = true;
                foreach (string p in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI\nvidia-smi.exe") })
                    if (File.Exists(p)) { _nvidiaSmi = p; break; }
            }
            if (_nvidiaSmi != null)
            {
                try
                {
                    CommandResult r = NetworkHelper.Run(_nvidiaSmi, "--query-gpu=temperature.gpu,utilization.gpu --format=csv,noheader,nounits", 2500);
                    string line = (r.Output ?? "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0);
                    if (line != null)
                    {
                        string[] parts = line.Split(',');
                        double t; if (parts.Length > 0 && double.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out t)) { s.GpuTempC = t; sources.Add("nvidia-smi"); }
                    }
                }
                catch { }
            }
            if (_batteryTempSupported)
            {
                try
                {
                    using (var q = new ManagementObjectSearcher(@"root\WMI", "SELECT Temperature FROM BatteryTemperature"))
                    {
                        bool any = false;
                        foreach (ManagementBaseObject o in q.Get())
                        {
                            double c = Convert.ToDouble(o["Temperature"]) / 10.0 - 273.15;
                            if (c > -20 && c < 120) { s.BatteryTempC = Math.Round(c, 1); any = true; }
                        }
                        if (!any) _batteryTempSupported = false;
                    }
                }
                catch { _batteryTempSupported = false; }
            }
            s.TempQuality = sources.Count > 0 ? ReadingQuality.Estimated : ReadingQuality.NotAvailable;
            s.TempSource = string.Join(" + ", sources.ToArray());
        }

        private void SampleNetwork(DeviceSnapshot s, DeviceSnapshot prev, bool slow)
        {
            try
            {
                if (_nic == null || slow)
                {
                    NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces();
                    _nic = all.FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                           ?? all.FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType == NetworkInterfaceType.Ethernet && n.Name.IndexOf("vEthernet", StringComparison.OrdinalIgnoreCase) < 0)
                           ?? _nic;
                }
                if (_nic == null) { s.NetLabel = "Network"; s.NetUp = false; return; }
                bool wifi = _nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                s.NetLabel = wifi ? "Wi-Fi" : "Ethernet";
                s.NetUp = _nic.OperationalStatus == OperationalStatus.Up;
                IPv4InterfaceStatistics st = _nic.GetIPv4Statistics();
                DateTime now = DateTime.Now;
                if (_lastNetSample != DateTime.MinValue)
                {
                    double secs = Math.Max(0.5, (now - _lastNetSample).TotalSeconds);
                    s.NetRxKBps = Math.Max(0, st.BytesReceived - _lastRx) / 1024.0 / secs;
                    s.NetTxKBps = Math.Max(0, st.BytesSent - _lastTx) / 1024.0 / secs;
                }
                _lastRx = st.BytesReceived; _lastTx = st.BytesSent; _lastNetSample = now;
                if (!wifi) { s.WifiLinkMbps = (int)(_nic.Speed / 1000000); return; }

                if (slow || prev.WifiSignalPercent == null) ReadWlan(s); else { s.WifiSsid = prev.WifiSsid; s.WifiSignalPercent = prev.WifiSignalPercent; s.WifiSignalDbm = prev.WifiSignalDbm; s.WifiLinkMbps = prev.WifiLinkMbps; }
            }
            catch { }
        }

        private static void ReadWlan(DeviceSnapshot s)
        {
            try
            {
                CommandResult r = NetworkHelper.Run("netsh", "wlan show interfaces", 3000);
                foreach (string raw in (r.Output ?? "").Split('\n'))
                {
                    string line = raw.Trim();
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    string key = line.Substring(0, colon).Trim(), val = line.Substring(colon + 1).Trim();
                    if (key.Equals("SSID", StringComparison.OrdinalIgnoreCase)) s.WifiSsid = val;
                    else if (key.StartsWith("Signal", StringComparison.OrdinalIgnoreCase)) { int p; if (int.TryParse(val.TrimEnd('%'), out p)) { s.WifiSignalPercent = p; s.WifiSignalDbm = p / 2 - 100; } }
                    else if (key.StartsWith("Receive rate", StringComparison.OrdinalIgnoreCase)) { double m; if (double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out m)) s.WifiLinkMbps = (int)m; }
                }
            }
            catch { }
        }

        private void SamplePrinters(DeviceSnapshot s, DeviceSnapshot prev, bool slow)
        {
            try
            {
                if ((DateTime.Now - _printersEnumerated).TotalSeconds > 30)
                {
                    _printersEnumerated = DateTime.Now;
                    _printers = PrinterEnumerator.GetPrinters();
                }
                List<PrinterInfo> usb = _printers.Where(p => p.IsUsb).ToList();
                List<PrinterInfo> bt = _printers.Where(p => !string.IsNullOrEmpty(p.PortName) && (p.PortName.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) || p.PortName.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                s.UsbPrinters = usb.Count; s.BtPrinters = bt.Count;
                s.UsbPrinterNames = string.Join(", ", usb.Select(p => p.Name).ToArray());
                s.BtPrinterNames = string.Join(", ", bt.Select(p => p.Name).ToArray());
                if (slow || prev.UsbPrinters != s.UsbPrinters || prev.BtPrinters != s.BtPrinters)
                {
                    s.UsbPrintersOnline = usb.Count(p => IsOnline(p));
                    s.BtPrintersOnline = bt.Count(p => IsOnline(p));
                    _lastUsbOn = s.UsbPrintersOnline > 0; _lastBtOn = s.BtPrintersOnline > 0;
                }
                else
                {
                    s.UsbPrintersOnline = _lastUsbOn ? Math.Max(1, prev.UsbPrintersOnline) : 0;
                    s.BtPrintersOnline = _lastBtOn ? Math.Max(1, prev.BtPrintersOnline) : 0;
                }
            }
            catch { }
        }

        private static bool IsOnline(PrinterInfo p)
        {
            try
            {
                PrinterQueueStatus st = PrinterStatusProbe.Query(p.Name);
                return st.Available && !st.IsOffline && !st.IsWorkOffline;
            }
            catch { return false; }
        }

        private void SampleBattery(DeviceSnapshot s, DeviceSnapshot prev, bool slow)
        {
            var b = new BatteryInfo();
            try
            {
                PowerStatus ps = SystemInformation.PowerStatus;
                b.Present = (ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0 && (ps.BatteryChargeStatus != BatteryChargeStatus.Unknown || ps.BatteryLifePercent <= 1.0f);
                if (!b.Present) { s.Battery = b; return; }
                b.Percent = (int)Math.Round(ps.BatteryLifePercent * 100);
                b.OnAc = ps.PowerLineStatus == PowerLineStatus.Online;
                b.Charging = (ps.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
                if (ps.BatteryLifeRemaining > 0) b.RemainingText = Duration(ps.BatteryLifeRemaining) + " left";
            }
            catch { s.Battery = b; return; }

            if (!slow && prev.Battery != null && prev.Battery.Present)
            {
                b.MilliAmps = prev.Battery.MilliAmps; b.Watts = prev.Battery.Watts; b.Volts = prev.Battery.Volts; b.TempC = s.BatteryTempC ?? prev.Battery.TempC;
                b.DesignMwh = prev.Battery.DesignMwh; b.FullMwh = prev.Battery.FullMwh; b.CycleCount = prev.Battery.CycleCount; b.Chemistry = prev.Battery.Chemistry; b.Manufacturer = prev.Battery.Manufacturer;
                if (b.RemainingText.Length == 0) b.RemainingText = prev.Battery.RemainingText;
                s.Battery = b; return;
            }

            try
            {
                using (var q = new ManagementObjectSearcher(@"root\WMI", "SELECT Charging, Discharging, PowerOnline, ChargeRate, DischargeRate, Voltage, RemainingCapacity FROM BatteryStatus"))
                    foreach (ManagementBaseObject o in q.Get())
                    {
                        double mv = Convert.ToDouble(o["Voltage"]);
                        double charge = Convert.ToDouble(o["ChargeRate"]), discharge = Convert.ToDouble(o["DischargeRate"]);
                        double mw = charge > 0 ? charge : -discharge;
                        if (mv > 0) { b.Volts = Math.Round(mv / 1000.0, 2); b.MilliAmps = Math.Round(mw / (mv / 1000.0)); }
                        b.Watts = Math.Round(mw / 1000.0, 1);
                        bool charging = Convert.ToBoolean(o["Charging"]);
                        if (charging) b.Charging = true;
                        double remaining = Convert.ToDouble(o["RemainingCapacity"]);
                        if (b.RemainingText.Length == 0)
                        {
                            if (charging && charge > 0 && b.FullMwh.HasValue) b.RemainingText = Duration((int)((b.FullMwh.Value - remaining) / charge * 3600)) + " to full";
                            else if (!charging && discharge > 0) b.RemainingText = Duration((int)(remaining / discharge * 3600)) + " left";
                        }
                        break;
                    }
                using (var q = new ManagementObjectSearcher(@"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"))
                    foreach (ManagementBaseObject o in q.Get()) { b.FullMwh = Convert.ToInt32(o["FullChargedCapacity"]); break; }
                using (var q = new ManagementObjectSearcher(@"root\WMI", "SELECT DesignedCapacity, Chemistry, ManufactureName FROM BatteryStaticData"))
                    foreach (ManagementBaseObject o in q.Get())
                    {
                        b.DesignMwh = Convert.ToInt32(o["DesignedCapacity"]);
                        b.Chemistry = Convert.ToString(o["Chemistry"]) ?? "";
                        b.Manufacturer = Convert.ToString(o["ManufactureName"]) ?? "";
                        break;
                    }
                using (var q = new ManagementObjectSearcher(@"root\WMI", "SELECT CycleCount FROM BatteryCycleCount"))
                    foreach (ManagementBaseObject o in q.Get()) { b.CycleCount = Convert.ToInt32(o["CycleCount"]); break; }
            }
            catch { /* the WMI battery classes are optional; the basic status above still shows */ }
            b.TempC = s.BatteryTempC;
            s.Battery = b;
        }

        public static string Duration(int seconds)
        {
            if (seconds < 0) return "";
            int h = seconds / 3600, m = (seconds % 3600) / 60;
            return h > 0 ? h + " h " + m.ToString("00") + " min" : m + " min";
        }

        // ------------------------------------------------------------------ telemetry file

        private void WriteTelemetry(DeviceSnapshot s)
        {
            lock (_gate)
            {
                try
                {
                    DateTime day = DateTime.Now.Date;
                    if (_telemetry == null || _telemetryDay != day)
                    {
                        if (_telemetry != null) { try { _telemetry.Dispose(); } catch { } _telemetry = null; }
                        Directory.CreateDirectory(TelemetryDirectory);
                        string path = TelemetryFileFor(day);
                        bool isNew = !File.Exists(path);
                        _telemetry = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                        if (isNew) _telemetry.WriteLine("Time,CpuPct,CpuGHz,RamPct,RamUsedGB,GpuPct,GpuMemGB,CpuTempC,GpuTempC,BatteryTempC,Net,WifiSignalPct,WifiLinkMbps,NetRxKBps,NetTxKBps,BridgeKBps,UsbPrintersOnline,UsbPrinters,BtPrintersOnline,BtPrinters,BatteryPct,BatteryState,BatteryMa,BatteryW,BatteryV,Events");
                        _telemetryDay = day;
                        CleanupOld();
                    }
                    var ci = CultureInfo.InvariantCulture;
                    string events = _pendingEventTexts.Count == 0 ? "" : string.Join(" | ", _pendingEventTexts.ToArray()).Replace("\"", "\"\"");
                    _pendingEventTexts.Clear();
                    _telemetry.WriteLine(string.Join(",",
                        s.Time.ToString("yyyy-MM-dd HH:mm:ss", ci), N(s.CpuQuality == ReadingQuality.NotAvailable ? (double?)null : s.CpuPercent, 0), N(s.CpuGhz, 2), N(s.RamPercent, 0), N(s.RamUsedGb, 2),
                        N(s.GpuPercent, 0), N(s.GpuMemGb, 2), N(s.CpuTempC, 1), N(s.GpuTempC, 1), N(s.BatteryTempC, 1),
                        s.NetLabel, N(s.WifiSignalPercent, 0), N(s.WifiLinkMbps, 0), N(s.NetRxKBps, 1), N(s.NetTxKBps, 1), N(s.BridgeKBps, 1),
                        s.UsbPrintersOnline.ToString(ci), s.UsbPrinters.ToString(ci), s.BtPrintersOnline.ToString(ci), s.BtPrinters.ToString(ci),
                        s.Battery.Present ? s.Battery.Percent.ToString(ci) : "", s.Battery.Present ? (s.Battery.Charging ? "charging" : s.Battery.OnAc ? "ac" : "discharging") : "none",
                        N(s.Battery.MilliAmps, 0), N(s.Battery.Watts, 1), N(s.Battery.Volts, 2), "\"" + events + "\""));
                    _telemetry.Flush();
                }
                catch { /* telemetry must never break the bridge */ }
            }
        }

        private static string N(double? v, int decimals) { return v.HasValue ? Math.Round(v.Value, decimals).ToString("F" + decimals, CultureInfo.InvariantCulture) : ""; }
        private static string N(int? v, int decimals) { return v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : ""; }

        private static void CleanupOld()
        {
            try
            {
                foreach (string f in Directory.GetFiles(TelemetryDirectory, "telemetry-*.csv"))
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-KeepTelemetryDays)) File.Delete(f);
            }
            catch { }
        }

        // ------------------------------------------------------------------ native

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
    }
}
