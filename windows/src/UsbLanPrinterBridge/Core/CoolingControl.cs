using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;

namespace UsbLanPrinterBridge.Core
{
    /// <summary>
    /// The two timed actions on the Cooling card, done through the Windows power plan with powercfg, which is
    /// what a program without a vendor driver can do:
    ///
    ///   Fans to max   = system cooling policy Active + maximum processor state 100 %. Most laptops then spin
    ///                   their fans up early and hard. Direct fan RPM control only exists in the maker's tool.
    ///   Throttle      = maximum processor state capped at N %, so the machine runs cooler and quieter.
    ///
    /// The plan's previous values are read first and restored when the timer ends, on Stop, and on exit.
    /// Both need administrator rights, which the bridge normally runs with.
    /// </summary>
    public sealed class CoolingControl : IDisposable
    {
        private const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
        private const string CoolingPolicy = "94d3a615-a899-4ac5-ae2b-e4d8f634367f";   // SYSCOOLPOL: 0 passive, 1 active
        private const string MaxProcessorState = "bc5038f7-23e0-4960-96da-33abaf5935ec"; // PROCTHROTTLEMAX, percent

        private readonly object _gate = new object();
        private Timer _timer;
        private string _scheme;
        private int? _savedCoolAc, _savedCoolDc, _savedMaxAc, _savedMaxDc;

        public bool FanBoostActive { get; private set; }
        public DateTime FanBoostUntil { get; private set; }
        public int FanBoostMinutes { get; private set; }
        public bool ThrottleActive { get; private set; }
        public DateTime ThrottleUntil { get; private set; }
        public int ThrottleMinutes { get; private set; }
        public int ThrottlePercent { get; private set; }

        public bool IsElevated = BridgeManager.DetectElevation();

        public event Action Changed;

        public TimeSpan FanBoostLeft { get { return FanBoostActive ? Max(FanBoostUntil - DateTime.Now) : TimeSpan.Zero; } }
        public TimeSpan ThrottleLeft { get { return ThrottleActive ? Max(ThrottleUntil - DateTime.Now) : TimeSpan.Zero; } }
        private static TimeSpan Max(TimeSpan t) { return t < TimeSpan.Zero ? TimeSpan.Zero : t; }

        public StartOutcome StartFanBoost(int minutes)
        {
            if (!IsElevated) return StartOutcome.Fail("Changing the power plan needs administrator rights. Restart the bridge as administrator.");
            minutes = Math.Max(1, Math.Min(240, minutes));
            lock (_gate)
            {
                StartOutcome prep = Prepare();
                if (!prep.Success) return prep;
                CommandResult r1 = Set(CoolingPolicy, 1, 1);
                CommandResult r2 = Set(MaxProcessorState, 100, 100);
                CommandResult apply = Apply();
                if (!r1.Success || !apply.Success) return StartOutcome.Fail("powercfg refused: " + (r1.Success ? apply.OutputOneLine : r1.OutputOneLine));
                FanBoostActive = true;
                FanBoostMinutes = minutes;
                FanBoostUntil = DateTime.Now.AddMinutes(minutes);
                EnsureTimer();
            }
            Logger.Warn("Cooling: fans to max for " + minutes + " min (cooling policy Active, processor 100 %). The plan is restored when the timer ends.");
            PrinterActionLog.Warn("this PC", "user", "Cooling: fans to max for " + minutes + " min", "System cooling policy set to Active and the maximum processor state to 100 %. Most laptops ramp their fans; the previous plan values are restored when the timer ends or on exit. Direct fan control needs the laptop maker's tool.");
            Raise();
            return StartOutcome.Ok("Fans to max until " + FanBoostUntil.ToString("HH:mm"));
        }

        public StartOutcome StartThrottle(int percent, int minutes)
        {
            if (!IsElevated) return StartOutcome.Fail("Changing the power plan needs administrator rights. Restart the bridge as administrator.");
            percent = Math.Max(5, Math.Min(100, percent));
            minutes = Math.Max(1, Math.Min(240, minutes));
            lock (_gate)
            {
                StartOutcome prep = Prepare();
                if (!prep.Success) return prep;
                CommandResult r = Set(MaxProcessorState, percent, percent);
                CommandResult apply = Apply();
                if (!r.Success || !apply.Success) return StartOutcome.Fail("powercfg refused: " + (r.Success ? apply.OutputOneLine : r.OutputOneLine));
                ThrottleActive = true;
                ThrottlePercent = percent;
                ThrottleMinutes = minutes;
                ThrottleUntil = DateTime.Now.AddMinutes(minutes);
                if (FanBoostActive) { FanBoostActive = false; }   // the two set the same value; the newer one wins
                EnsureTimer();
            }
            Logger.Warn("Throttle: maximum processor state " + percent + " % for " + minutes + " min. Restored when the timer ends.");
            PrinterActionLog.Warn("this PC", "user", "Throttle: CPU capped at " + percent + " % for " + minutes + " min", "Maximum processor state set to " + percent + " % in the active power plan; the previous value is restored when the timer ends or on exit.");
            Raise();
            return StartOutcome.Ok("CPU capped at " + percent + " % until " + ThrottleUntil.ToString("HH:mm"));
        }

        public void StopFanBoost() { Restore("fans to max stopped"); }
        public void StopThrottle() { Restore("throttle stopped"); }

        /// <summary>Puts the plan back the way it was. Safe to call when nothing is active.</summary>
        public void RestoreAll() { Restore("restored on exit"); }

        private void Restore(string why)
        {
            bool had;
            lock (_gate)
            {
                had = FanBoostActive || ThrottleActive;
                FanBoostActive = false;
                ThrottleActive = false;
                if (had && _scheme != null)
                {
                    if (_savedCoolAc.HasValue) Set(CoolingPolicy, _savedCoolAc.Value, _savedCoolDc ?? _savedCoolAc.Value);
                    if (_savedMaxAc.HasValue) Set(MaxProcessorState, _savedMaxAc.Value, _savedMaxDc ?? _savedMaxAc.Value);
                    Apply();
                }
                if (_timer != null && !FanBoostActive && !ThrottleActive) { _timer.Dispose(); _timer = null; }
            }
            if (had)
            {
                Logger.Info("Cooling: power plan restored (" + why + ").");
                PrinterActionLog.Info("this PC", "user", "Cooling: power plan restored", "Cooling policy and maximum processor state are back to their previous values (" + why + ").");
                Raise();
            }
        }

        private StartOutcome Prepare()
        {
            if (_scheme == null)
            {
                CommandResult r = NetworkHelper.Run("powercfg", "/getactivescheme", 5000);
                Match m = Regex.Match(r.Output ?? "", @"[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}");
                if (!r.Success || !m.Success) return StartOutcome.Fail("Could not read the active power plan: " + r.OutputOneLine);
                _scheme = m.Value;
                int ac, dc;
                if (ReadIndices(CoolingPolicy, out ac, out dc)) { _savedCoolAc = ac; _savedCoolDc = dc; }
                if (ReadIndices(MaxProcessorState, out ac, out dc)) { _savedMaxAc = ac; _savedMaxDc = dc; }
                if (!_savedMaxAc.HasValue) { _savedMaxAc = 100; _savedMaxDc = 100; }
                if (!_savedCoolAc.HasValue) { _savedCoolAc = 0; _savedCoolDc = 0; }
            }
            return StartOutcome.Ok(null);
        }

        private bool ReadIndices(string setting, out int ac, out int dc)
        {
            ac = dc = 0;
            CommandResult r = NetworkHelper.Run("powercfg", "/query " + _scheme + " " + SubProcessor + " " + setting, 5000);
            if (!r.Success) return false;
            Match a = Regex.Match(r.Output ?? "", @"AC Power Setting Index:\s*0x([0-9a-fA-F]+)");
            Match d = Regex.Match(r.Output ?? "", @"DC Power Setting Index:\s*0x([0-9a-fA-F]+)");
            if (!a.Success) return false;
            ac = int.Parse(a.Groups[1].Value, NumberStyles.HexNumber);
            dc = d.Success ? int.Parse(d.Groups[1].Value, NumberStyles.HexNumber) : ac;
            return true;
        }

        private CommandResult Set(string setting, int ac, int dc)
        {
            CommandResult r = NetworkHelper.Run("powercfg", "/setacvalueindex " + _scheme + " " + SubProcessor + " " + setting + " " + ac, 5000);
            NetworkHelper.Run("powercfg", "/setdcvalueindex " + _scheme + " " + SubProcessor + " " + setting + " " + dc, 5000);
            return r;
        }

        private CommandResult Apply() { return NetworkHelper.Run("powercfg", "/setactive " + _scheme, 5000); }

        private void EnsureTimer()
        {
            if (_timer == null) _timer = new Timer(_ => CheckTimers(), null, 1000, 1000);
        }

        private void CheckTimers()
        {
            bool fanDue, capDue;
            lock (_gate)
            {
                fanDue = FanBoostActive && DateTime.Now >= FanBoostUntil;
                capDue = ThrottleActive && DateTime.Now >= ThrottleUntil;
            }
            if (fanDue) Restore("fans-to-max timer ended");
            else if (capDue) Restore("throttle timer ended");
            else Raise();
        }

        private void Raise() { Action h = Changed; if (h != null) { try { h(); } catch { } } }

        public void Dispose()
        {
            RestoreAll();
            lock (_gate) { if (_timer != null) { _timer.Dispose(); _timer = null; } }
        }
    }
}
