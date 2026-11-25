using System;
using System.Collections.Generic;
using System.Linq;

namespace HPControl.Service
{
    /// <summary>
    /// HP Control's own fan curve.
    ///
    /// The fan controller (EC) is either loud or annoying: it runs the fan whenever the air inside the laptop is
    /// above ~33 °C (nearly always), and in Performance mode it jumps straight to high speeds. HP's firmware fan test
    /// interface (HpBios.FanOverride, ACPI \HODW: EC software fan override + fan target in 50 rpm steps) lets HP Control
    /// drive the fan itself:
    ///  - off while the CPU is cool and the laptop is idle (on above ~60 °C, off again below ~55 °C - averages, so a
    ///    short burst never wakes it);
    ///  - when on, the speed follows a temperature curve, plus a "heads-up" from chip power so a heavy job starts the
    ///    fan gently before the heat arrives;
    ///  - speed changes are ramped (up ~120 rpm/s, down ~60 rpm/s, quicker once the load has gone), so it never jumps.
    /// The HP mode then only decides CPU power, not fan noise.
    ///
    /// Safety: the fan goes back to the EC (which runs it as usual) if the CPU gets very hot, the internal sensor stays
    /// hot, a sensor stops answering, the fan doesn't follow, when the service stops or shuts down, before sleep, when
    /// HP Control starts, if fan control is turned off or "Fan always on" is enabled, and via an engine watchdog if the
    /// control loop stalls. The CPU also protects itself at its temperature limit.
    /// Runs outside the engine lock.
    /// </summary>
    public sealed class FanStop
    {
        struct Sample { public DateTime T; public double Cpu, Case, W, Load; }

        public const int MinRpm = 1950;   // lowest speed the firmware accepts other than off
        readonly HpBios _bios;
        readonly List<Sample> _hist = new List<Sample>();
        volatile bool _holding;            // HP Control is driving the fan
        bool _on, _fanAlwaysOn;
        double _cmd;                       // current (ramped) fan command, rpm
        int _sent = -1;                    // last value written to the firmware
        double _tSmooth = double.NaN;
        DateTime _onSince, _faoChecked = DateTime.MinValue, _handedBackAt = DateTime.MinValue, _lastSend = DateTime.MinValue, _lastTick = DateTime.MinValue;
        DateTime? _mismatchSince, _resumeOkSince, _hotSince;
        bool _intHot;
        static string FmtDur(TimeSpan t) => t.TotalSeconds < 90 ? t.TotalSeconds.ToString("0") + " s" : t.TotalMinutes.ToString("0.0") + " min";
        bool _safetyHandBack;
        public bool Holding => _holding;
        public bool FanOn => _holding && _on;
        public int TargetRpm => _holding ? (int)Math.Round(_cmd) : -1;
        public int WantedRpm { get; private set; }   // what the curve asks for before any max-fan-speed cap
        public DateTime LastStep = DateTime.UtcNow;
        public string State = "";
        public string Info = "";

        public FanStop(HpBios bios) { _bios = bios; }

        /// <summary>The fan is gliding towards a new speed (the engine ticks faster until it gets there).</summary>
        public bool Ramping { get; private set; }

        static double Avg(List<Sample> h, Func<Sample, double> f, double seconds, DateTime now, double def)
        {
            var v = h.Where(x => (now - x.T).TotalSeconds <= seconds).Select(f).Where(x => !double.IsNaN(x)).ToList();
            return v.Count == 0 ? def : v.Average();
        }

        /// <summary>One step, every engine tick. maxRpm = the active profile's fan speed limit (0 = none).</summary>
        public string Step(FanStopSettings s, DateTime now, double cpuT, double caseT, double boardT, double chipW, double loadPct, int fanRpm, int targetRpm, bool fanFresh, int maxRpm = 0)
        {
            LastStep = now;
            double dt = _lastTick == DateTime.MinValue ? 2 : Math.Max(0.5, Math.Min(10, (now - _lastTick).TotalSeconds));
            _lastTick = now;
            _hist.Add(new Sample { T = now, Cpu = cpuT, Case = caseT, W = chipW, Load = loadPct });
            while (_hist.Count > 0 && (now - _hist[0].T).TotalSeconds > 120) _hist.RemoveAt(0);
            if (!double.IsNaN(cpuT)) _tSmooth = double.IsNaN(_tSmooth) ? cpuT : _tSmooth + dt / (10 + dt) * (cpuT - _tSmooth);

            if (s == null || !s.Enabled || !_bios.Available) { State = ""; return _holding ? HandBack(now, "fan control was turned off", false) : null; }
            if ((now - _faoChecked).TotalMinutes >= 5)
            {
                _faoChecked = now;
                try { _fanAlwaysOn = string.Equals(HpBios.GetBiosSetting("Fan Always On", out _), "Enable", StringComparison.OrdinalIgnoreCase); } catch { }
            }
            if (_fanAlwaysOn) { State = "Fan always on is enabled"; return _holding ? HandBack(now, "Fan always on is enabled", false) : null; }

            double cpuAvg = Avg(_hist, x => x.Cpu, 30, now, cpuT), caseAvg = Avg(_hist, x => x.Case, 60, now, caseT);

            // ---------------- the EC has the fan (start-up, or after a safety hand-back) ----------------
            if (!_holding)
            {
                if (double.IsNaN(cpuT)) { State = "Fan controller in charge (no CPU temperature)"; return null; }
                if (_safetyHandBack)
                {
                    bool ok = cpuAvg <= s.HandBackCpuC - 15 && (double.IsNaN(caseAvg) || caseAvg <= s.HandBackInternalC - 5);
                    if (ok) { if (_resumeOkSince == null) _resumeOkSince = now; } else _resumeOkSince = null;
                    if (_resumeOkSince == null || (now - _resumeOkSince.Value).TotalSeconds < 60 || (now - _handedBackAt).TotalMinutes < 2)
                    { State = "Fan controller in charge (cooling down after a safety hand-back)"; return null; }
                }
                // take over: start from the speed the fan is already running at, so nothing jumps
                _holding = true; _safetyHandBack = false; _resumeOkSince = null; _mismatchSince = null;
                _on = fanFresh && fanRpm > 300;
                _cmd = _on ? Math.Max(MinRpm, fanRpm) : 0; _onSince = now; _sent = -1;
                Send(now);
                return "Fan control: HP Control is driving the fan (" + (_on ? "starting at " + (int)_cmd + " rpm" : "off") + ")";
            }

            // ---------------- safety: give the fan back to the EC ----------------
            if (double.IsNaN(cpuT)) return HandBack(now, "the CPU temperature stopped answering", true);
            if (cpuT >= s.HandBackCpuC) { if (_hotSince == null) _hotSince = now; } else _hotSince = null;
            if (cpuT >= Math.Max(99, s.HandBackCpuC) || (_hotSince != null && (now - _hotSince.Value).TotalSeconds >= 15))
                return HandBack(now, "CPU at " + cpuT + " °C" + (_hotSince != null && cpuT < 99 ? " for 15 s" : ""), true);
            if (!double.IsNaN(caseAvg) && caseAvg >= s.HandBackInternalC) return HandBack(now, "internal sensor averaged " + caseAvg.ToString("0") + " °C", true);
            if (fanFresh)
            {
                bool mismatch = (_sent >= MinRpm && fanRpm < 400) || (_sent == 0 && fanRpm > 600);
                if (mismatch) { if (_mismatchSince == null) _mismatchSince = now; } else _mismatchSince = null;
                if (_mismatchSince != null && (now - _mismatchSince.Value).TotalSeconds > 30)
                    return HandBack(now, "the fan isn't following (asked " + _sent + " rpm, running " + fanRpm + " rpm)", true);
            }

            // ---------------- on / off ----------------
            // chip power: switches the fan on only when it has stayed up for 20 s (lowest reading), sets a minimum speed
            // from the 20 s average once on. Internal sensor: 1-minute average.
            var w20 = _hist.Where(x => (now - x.T).TotalSeconds <= 20 && !double.IsNaN(x.W)).ToList();
            double wMin20 = w20.Count >= 2 && (now - w20[0].T).TotalSeconds >= 14 ? w20.Min(x => x.W) : double.NaN;
            double ffOn = double.IsNaN(wMin20) || s.PowerRamp == null ? 0 : Math.Max(0, s.PowerRamp.Eval(wMin20));
            double wAvg10 = Avg(_hist, x => x.W, 10, now, double.NaN);
            double ff = double.IsNaN(wAvg10) || s.PowerRamp == null ? 0 : Math.Max(0, s.PowerRamp.Eval(wAvg10));
            double fi = double.IsNaN(caseAvg) || s.InternalCurve == null ? 0 : Math.Max(0, s.InternalCurve.Eval(caseAvg));
            double cpu15 = Avg(_hist, x => x.Cpu, 15, now, cpuT), w15 = Avg(_hist, x => x.W, 15, now, double.NaN), load15 = Avg(_hist, x => x.Load, 15, now, loadPct);
            double ff15 = double.IsNaN(w15) || s.PowerRamp == null ? 0 : Math.Max(0, s.PowerRamp.Eval(w15));
            bool busy = cpu15 > s.OffBelowC || ff15 >= MinRpm || load15 > s.OffMaxLoadPct;
            // the internal sensor is slow and stays warm for minutes after a load, so it only starts or holds the fan
            // when it's genuinely warm (InternalOnC); below that it just adds speed while the fan is on anyway
            if (double.IsNaN(caseAvg)) _intHot = false;
            else if (caseAvg >= s.InternalOnC) _intHot = true;
            else if (caseAvg < s.InternalOnC - 3) _intHot = false;
            bool internalHot = _intHot;
            // load gone: the slow internal sensor doesn't hold the fan up (just the lowest speed while it's really warm)
            if (!busy) fi = internalHot ? MinRpm : 0;
            string reason = null;
            if (!_on)
            {
                bool hot = cpuAvg >= s.OnAboveC && cpuT >= s.OnAboveC - 2;     // warm for a while, and still warm now
                bool veryHot = s.FastOnC > 0 && (cpuT >= s.FastOnC || _tSmooth >= s.FastOnC - 10);
                if (veryHot) reason = "CPU " + cpuT.ToString("0") + " °C";
                else if (hot) reason = "CPU averaged " + cpuAvg.ToString("0") + " °C";
                else if (ffOn >= MinRpm) reason = "heavy load (chip " + wMin20.ToString("0") + " W+ for 20 s)";
                else if (internalHot) reason = "internal sensor averaged " + caseAvg.ToString("0") + " °C";
                if (reason != null) { _on = true; _onSince = now; }
            }
            else
            {
                // off once the CPU has cooled and the load is gone (15 s averages, so it reacts quickly after a load)
                if ((now - _onSince).TotalSeconds >= s.MinOnSec && !busy && !internalHot)
                {
                    _on = false;
                    reason = "off: CPU " + cpu15.ToString("0") + " °C, " + (double.IsNaN(w15) ? "idle" : "chip " + w15.ToString("0.0") + " W") +
                             " (was on " + FmtDur(now - _onSince) + ")";
                }
            }
            if (reason != null && !reason.StartsWith("off")) reason = "on: " + reason;

            // ---------------- speed ----------------
            double want = 0;
            if (_on)
            {
                double curve = s.Curve != null && !double.IsNaN(_tSmooth) ? s.Curve.Eval(_tSmooth) : MinRpm;
                want = Math.Max(MinRpm, Math.Max(double.IsNaN(curve) ? MinRpm : curve, Math.Max(ff, fi)));
            }
            WantedRpm = (int)Math.Round(want);
            if (maxRpm > 0 && want > maxRpm) want = Math.Max(MinRpm, maxRpm);
            // ramp: off -> lowest speed is a single step (nothing lower exists); everything else glides
            // winding down to off: 3x the normal rate, and from a low speed straight to off (the fan coasts down quietly)
            if (want <= 0) _cmd = _cmd <= s.StopFromRpm ? 0 : Math.Max(MinRpm, _cmd - s.RampDownRpmPerSec * 3 * dt);
            else if (_cmd < MinRpm) _cmd = MinRpm;
            else if (want > _cmd) _cmd = Math.Min(want, _cmd + s.RampUpRpmPerSec * (1 + Math.Max(0, Math.Min(2, (Math.Max(double.IsNaN(_tSmooth) ? 0 : _tSmooth, cpuT) - 75) / 10))) * dt);
            else _cmd = Math.Max(want, _cmd - s.RampDownRpmPerSec * (busy ? 1 : 3) * dt);   // load gone: wind down quicker
            if (!_on && _cmd <= Math.Max(MinRpm, s.StopFromRpm)) _cmd = 0;
            Ramping = Math.Abs(Math.Max(0, want) - _cmd) > 25;
            Send(now);

            State = _cmd <= 0 ? "Fan off · on above " + s.OnAboveC.ToString("0") + " °C" : "HP Control fan curve · " + (int)Math.Round(_cmd) + " rpm" + (want > _cmd + 50 ? " (ramping up)" : want < _cmd - 50 ? " (ramping down)" : "");
            // while the fan is off the curves aren't in use: show what switches it on instead of curve speeds
            if (!_on && _cmd <= 0)
            {
                Info = "Fan off · CPU " + (double.IsNaN(cpuAvg) ? "?" : cpuAvg.ToString("0")) + " °C (30 s average, on at " + s.OnAboveC.ToString("0") + ")" +
                       (double.IsNaN(w15) ? "" : " · chip " + w15.ToString("0.0") + " W") +
                       (double.IsNaN(caseAvg) ? "" : " · internal " + caseAvg.ToString("0") + " °C");
                return reason == null ? null : "Fan " + reason;
            }
            Info = "CPU " + (double.IsNaN(_tSmooth) ? "?" : _tSmooth.ToString("0")) + " °C (smoothed) → " + (s.Curve != null && !double.IsNaN(_tSmooth) ? Math.Max(0, s.Curve.Eval(_tSmooth)).ToString("0") : "-") + " rpm" +
                   (double.IsNaN(wAvg10) ? "" : " · chip " + wAvg10.ToString("0.0") + " W → " + ff.ToString("0") + " rpm") +
                   (double.IsNaN(caseAvg) ? "" : " · internal " + caseAvg.ToString("0") + " °C → " + fi.ToString("0") + " rpm") + (maxRpm > 0 ? " · max " + maxRpm : "");
            return reason == null ? null : "Fan " + reason;
        }

        void Send(DateTime now)
        {
            int v = _cmd <= 0 ? 0 : (int)(Math.Round(_cmd / 50.0) * 50);
            // write when it changed, and re-assert every 30 s (60 s when off; harmless, covers the EC having reset itself)
            if (v == _sent && (now - _lastSend).TotalSeconds < (v == 0 ? 60 : 30)) return;
            if (_bios.FanOverride(v)) { _sent = v; _lastSend = now; }
        }

        string HandBack(DateTime now, string reason, bool safety)
        {
            _bios.FanRelease();
            _holding = false; _on = false; _cmd = 0; _sent = -1; _mismatchSince = null; _hotSince = null; Ramping = false;
            _safetyHandBack = safety; _handedBackAt = now; _resumeOkSince = null;
            State = "Fan controller in charge";
            return "Fan control: fan handed back to the fan controller (" + reason + ")";
        }

        /// <summary>Hands the fan back to the controller (service start/stop, sleep, watchdog). Harmless when not held.</summary>
        public void Release()
        {
            _holding = false; _on = false; _cmd = 0; _sent = -1; _hotSince = null; Ramping = false;
            if (_bios.Available) _bios.FanRelease();
        }
    }
}
