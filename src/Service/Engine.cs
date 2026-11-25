using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace HPControl.Service
{
    public class AgentContext
    {
        public string ForegroundApp = "";
        public bool GameInFront;         // the tray helper thinks the app in front is a game
        public bool Fullscreen;
        public bool Tablet;
        public int IdleSec;
        public bool MicInUse;
        public bool CamInUse;
        public DateTime Updated;
    }

    public class HistoryPoint
    {
        public DateTime T;
        public double Temp;
        public double Smooth;
        public int Mode;
        public int Budget;
        public double PowerW;
        public double Cpu;
        public int Fan = -1;
    }

    /// <summary>The control loop: sensors -> profile/rules -> HP mode ladder + fan-speed limiter + CPU budget -> firmware.</summary>
    public sealed class Engine : IDisposable
    {
        public Settings S;
        public readonly object Lock = new object();
        public readonly HpBios Bios = new HpBios();
        public Sensors Sens;
        public PowerLimiter Limiter;
        public BatteryManager Battery;
        public FanStop FanStopper;
        public Func<bool> AgentConnected = () => false;
        public Action<object> SendToAgent = _ => { };

        public Telemetry Tel = new Telemetry();
        public AgentContext Ctx = new AgentContext();
        readonly LinkedList<HistoryPoint> _history = new LinkedList<HistoryPoint>();
        DateTime _lastHistory;

        Thread _thread;
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        volatile bool _stop;

        // temperature
        double _smooth = double.NaN;
        DateTime _lastTick = DateTime.MinValue;

        // ladder state
        string _ladderProfile = "";
        int _ladderIdx = -1, _pendingIdx = -1;
        DateTime _pendingSince, _lastSwitch = DateTime.MinValue;

        // firmware mode state
        int _appliedCode = -1;
        DateTime _lastModeRead = DateTime.MinValue, _lastModeFail = DateTime.MinValue;
        int _tick;
        bool _forceApply = true;

        // limiter
        int _limBudget = 100;
        bool _forceQuiet;
        DateTime? _overSince, _underSince;

        public void Start()
        {
            S = Store.Load();
            if (!Bios.Init()) Log.Event("HP BIOS interface unavailable: " + Bios.InitError);
            Sens = new Sensors(Bios);
            Limiter = new PowerLimiter();
            Battery = new BatteryManager(this);
            FanStopper = new FanStop(Bios);
            FanStopper.Release();   // in case a previous run ended while holding the fan
            // watchdog: if the control loop ever stalls while the fan is held off, hand it back (own BIOS connection,
            // so a stuck call on the main one can't block it)
            _watchdog = new Timer(_ =>
            {
                try
                {
                    if (FanStopper == null || !FanStopper.Holding || (DateTime.UtcNow - FanStopper.LastStep).TotalSeconds < 30) return;
                    if (_wdBios == null) { var b = new HpBios(); if (!b.Init()) return; _wdBios = b; }
                    _wdBios.FanRelease();
                    FanStopper.Release();
                    Log.Event("Fan control: the control loop stalled, so the fan was handed back to the fan controller");
                }
                catch { }
            }, null, 10000, 10000);
            Sens.ReadCpuPct();
            _thread = new Thread(Loop) { IsBackground = true, Name = "engine", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
            Log.Event("HP Control service started (" + S.ControlMode + ")");
        }

        public void Stop()
        {
            _stop = true;
            _wake.Set();
            _thread?.Join(5000);
            try { Limiter?.RestoreAll(); } catch { }
            try { FanStopper?.Release(); } catch { }
            try
            {
                if (S != null && S.General.RestoreSmartSenseOnStop && S.ControlMode == "Auto" && Bios.Available)
                    Bios.SetThermalMode(Code("SmartSense"), out _);
            }
            catch { }
            Log.Event("HP Control service stopped");
        }

        public void Dispose() { try { _watchdog?.Dispose(); } catch { } Sens?.Dispose(); }

        Timer _watchdog;
        HpBios _wdBios;

        /// <summary>Before sleep: hand the fan back so the fan controller is in charge while asleep and on resume.</summary>
        public void OnSuspend() { try { FanStopper?.Release(); } catch { } }

        /// <summary>Run the next tick now (e.g. after a settings change).</summary>
        public void Kick(bool forceApply = false) { if (forceApply) _forceApply = true; _wake.Set(); }

        int Code(string mode) => S.ModeCodes.TryGetValue(mode, out var c) ? c : -1;
        string ModeOf(int code) => S.ModeCodes.FirstOrDefault(kv => kv.Value == code).Key ?? (code < 0 ? "" : "Unknown(" + code + ")");

        void Loop()
        {
            while (!_stop)
            {
                int wait;
                try { wait = Tick(); }
                catch (Exception ex) { Log.Write("tick error: " + ex); wait = 5000; }
                _wake.WaitOne(wait);
            }
        }

        int Tick()
        {
            // Sensor reads (WMI / BIOS / perf counters) can be slow when the CPU is flat out, so they happen
            // outside the lock: the app's status requests never wait on them.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // while nobody is looking, only read what control needs: the thermal zones used as control sensors, and
            // the battery's charge rate once a minute
            bool watching = (DateTime.UtcNow - LastUiPoll).TotalSeconds < 5;
            var ctlSensors = (S.General.ControlSensors ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
            var temps = Sens.ReadTemps(watching, ctlSensors);
            long tTemps = sw.ElapsedMilliseconds;
            double cpu = Sens.ReadCpuPct();
            Sens.ReadPackagePower();
            Sens.ReadPower(out bool onAc, out int pct, watching);
            long tPower = sw.ElapsedMilliseconds;
            // fan speed: every tick while the app is open, an RPM limit is active or HP Control's curve has the fan
            // spinning; otherwise every 15 s (enough for the fan-follows check and history, and cheap on battery).
            // The EC's own target speed is only read for the app.
            double cpuT = temps.TryGetValue("cpu", out var ct) ? ct : double.NaN;
            double caseT = temps.TryGetValue("case", out var cst) ? cst : double.NaN;
            var fs = S.FanStop;
            bool fanSpinning = !FanStopper.Holding || FanStopper.TargetRpm > 0;
            bool fanRead = false;
            if (watching || _rpmLimitActive || fanSpinning || (DateTime.UtcNow - _lastFanRead).TotalSeconds >= 15)
            {
                _lastFanRead = DateTime.UtcNow;
                Sens.ReadFan(watching);
                fanRead = true;
            }
            if (!_fanLogged && Sens.FanRaw.Length > 0) { _fanLogged = true; Log.Write("fan sensor: raw " + Sens.FanRaw + " → " + Sens.FanRpm + " rpm"); }
            long sensMs = sw.ElapsedMilliseconds;
            try
            {
                int wait = TickLocked(temps, cpu, onAc, pct);
                // fan stop runs outside the lock: switching the fan off takes a few seconds
                try
                {
                    var msg = FanStopper.Step(fs, DateTime.UtcNow, cpuT, caseT, Sens.BoardTemp, Sens.PackageW, _cpuSmooth, Sens.FanRpm, Sens.FanTargetRpm, fanRead, _fanCap);
                    if (msg != null) Log.Event(msg);
                    Tel.FanStop = FanStopper.State;
                    Tel.FanStopInfo = FanStopper.Info;
                    Tel.FanHeld = FanStopper.Holding;
                    Tel.FanCommandRpm = FanStopper.TargetRpm;
                }
                catch (Exception ex) { Log.Write("fan control: " + ex.Message); try { FanStopper.Release(); } catch { } }
                // tick every 2 s only while the fan is gliding to a new speed; otherwise the normal (battery: slower) rate
                // (and at least every 8 s while HP Control has the fan, so heat is never missed for long)
                return FanStopper.Ramping ? Math.Min(wait, 2000) : FanStopper.Holding ? Math.Min(wait, 8000) : wait;
            }
            finally
            {
                long total = sw.ElapsedMilliseconds;
                if (total > 1500 && (DateTime.UtcNow - _lastSlowLog).TotalSeconds > 60) { _lastSlowLog = DateTime.UtcNow; Log.Write("slow tick: temps " + tTemps + " ms, power " + (tPower - tTemps) + " ms, fan " + (sensMs - tPower) + " ms, total " + total + " ms"); }
            }
        }
        DateTime _lastSlowLog = DateTime.MinValue;
        string _modeWhy = "";
        int _fanCap;                    // active profile's fan speed limit, for HP Control's fan curve
        double _load10 = double.NaN, _load30 = double.NaN;
        bool _boost; DateTime? _boostPending, _boostCalm;
        bool _gameActive; DateTime _gameLastBusy;
        DateTime _lastOutsideNotice = DateTime.MinValue;
        bool _fanLogged, _rpmLimitActive;
        DateTime _lastFanRead = DateTime.MinValue;
        /// <summary>Set by the pipe server whenever the app asks for state; extra sensors are only read while someone is looking.</summary>
        public DateTime LastUiPoll = DateTime.MinValue;

        int TickLocked(Dictionary<string, double> temps, double cpu, bool onAc, int pct)
        {
            lock (Lock)
            {
                var now = DateTime.UtcNow;
                double dt = _lastTick == DateTime.MinValue ? 0 : (now - _lastTick).TotalSeconds;
                _lastTick = now; _tick++;

                // ---------- sensors ----------
                double tc = double.NaN;
                foreach (var k in (S.General.ControlSensors ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    if (temps.TryGetValue(k.Trim(), out var v)) tc = double.IsNaN(tc) ? v : Math.Max(tc, v);
                if (double.IsNaN(tc) && temps.Count > 0) tc = temps.Values.Max();

                // ---------- active profile ----------
                bool auto = S.ControlMode == "Auto";
                string ruleNames = "";
                Profile prof = null;
                int ruleCap = 100;
                double ruleRpm = double.PositiveInfinity;
                if (auto)
                {
                    string pname = onAc ? S.ProfileAC : S.ProfileDC;
                    string overrideName = null;
                    bool gameSeen = false;
                    foreach (var r in S.Rules.Where(r => r.Enabled))
                    {
                        if (r.PluggedInOnly && !onAc) continue;
                        bool m = RuleMatches(r, onAc, pct, DateTime.Now);
                        if (r.When == "Game") gameSeen |= m;
                        if (!m) continue;
                        ruleNames += (ruleNames.Length > 0 ? ", " : "") + r.Name;
                        if (overrideName == null && !string.IsNullOrEmpty(r.Profile) && S.FindProfile(r.Profile) != null) overrideName = r.Profile;
                        if (r.PerfCapPct > 0) ruleCap = Math.Min(ruleCap, r.PerfCapPct);
                        if (r.FanRpmMax > 0) ruleRpm = Math.Min(ruleRpm, r.FanRpmMax);
                    }
                    prof = S.FindProfile(overrideName ?? pname) ?? S.Profiles.FirstOrDefault();
                    if (gameSeen) { if (_load30 >= 8) _gameLastBusy = now; } else _gameActive = false;
                }

                // ---------- smoothing ----------
                double tau = prof != null ? Math.Max(0, prof.SmoothingSec) : 4;
                if (!double.IsNaN(tc))
                    _smooth = double.IsNaN(_smooth) || dt <= 0 ? tc : _smooth + (dt / (tau + dt)) * (tc - _smooth);
                // load averaged over ~8 s, so a few seconds of opening an app or loading a page don't count as "busy".
                // Load = CPU usage or chip power, whichever is higher: games often use little CPU but lots of graphics.
                double load = Math.Max(cpu, PowerLoad(Sens.PackageW));
                _cpuSmooth = double.IsNaN(_cpuSmooth) || dt <= 0 ? load : _cpuSmooth + (dt / (8 + dt)) * (load - _cpuSmooth);
                _load10 = double.IsNaN(_load10) || dt <= 0 ? load : _load10 + (dt / (10 + dt)) * (load - _load10);
                _load30 = double.IsNaN(_load30) || dt <= 0 ? load : _load30 + (dt / (30 + dt)) * (load - _load30);

                // ---------- decide ----------
                string target;
                int budget = 100;
                double rpmLimit = double.PositiveInfinity;
                int curveBudget = 100;
                bool safety = !double.IsNaN(tc) && tc >= S.General.SafetyTempC;
                string ladderMode = "";

                if (!auto)
                {
                    target = S.ManualHpMode;
                    ResetLimiter();
                }
                else if (prof == null || prof.HandsOff)
                {
                    target = prof?.FixedMode ?? "SmartSense";
                    ResetLimiter();
                    _ladderProfile = prof?.Name ?? "";
                }
                else
                {
                    ladderMode = EvaluateLadder(prof, _cpuSmooth, now);
                    target = ladderMode;
                    // busy boost: a sustained heavy load switches to the boost mode (e.g. Performance) right away
                    if (!string.IsNullOrEmpty(prof.BoostMode) && (onAc || prof.BoostOnBattery))
                    {
                        if (!_boost)
                        {
                            if (_load10 >= prof.BoostLoadPct) { if (_boostPending == null) _boostPending = now; } else _boostPending = null;
                            if (_boostPending != null && (now - _boostPending.Value).TotalSeconds >= prof.BoostAfterSec)
                            { _boost = true; _boostCalm = null; Log.Event("Busy (load " + _load10.ToString("0") + " %) -> " + Settings.ModeLabel(prof.BoostMode)); }
                        }
                        else
                        {
                            if (_load30 < prof.BoostLoadPct * 0.6) { if (_boostCalm == null) _boostCalm = now; } else _boostCalm = null;
                            if (_boostCalm != null && (now - _boostCalm.Value).TotalSeconds >= prof.BoostHoldSec)
                            { _boost = false; _boostPending = null; Log.Event("Load ended -> " + Settings.ModeLabel(ladderMode)); }
                        }
                        if (_boost) target = prof.BoostMode;
                    }
                    else { _boost = false; _boostPending = null; }

                    // fan speed limit = lowest of: the profile's max fan speed, its fan-limit curve, and any matching rule
                    if (prof.FanMaxRpm > 0) rpmLimit = prof.FanMaxRpm;
                    if (prof.RpmCap != null && prof.RpmCap.Enabled && !double.IsNaN(_smooth)) rpmLimit = Math.Min(rpmLimit, prof.RpmCap.Eval(_smooth));
                    rpmLimit = Math.Min(rpmLimit, ruleRpm);
                    if (Sens.FanRpm < 0) rpmLimit = double.PositiveInfinity;   // no fan sensor on this machine

                    if (safety) ResetLimiter();
                    else RunLimiter(prof, rpmLimit, ladderMode, now);

                    if (_forceQuiet && target != "PowerSaver" && target != "Quiet") target = "Quiet";
                    if (prof.PerfCap != null && prof.PerfCap.Enabled && !double.IsNaN(_smooth)) curveBudget = (int)Math.Round(Math.Max(20, prof.PerfCap.Eval(_smooth)));
                    curveBudget = Math.Min(curveBudget, ruleCap);
                    budget = Math.Min(curveBudget, prof.PowerLimiting ? _limBudget : 100);
                    if (safety) budget = 100;
                }

                // ---------- apply firmware mode ----------
                _modeWhy = !auto ? "manual" : prof == null ? "no profile" : prof.HandsOff ? "profile " + prof.Name : "profile " + prof.Name + (ruleNames.Length > 0 ? ", rule " + ruleNames : "");
                ApplyMode(target, auto, now);

                // ---------- apply CPU budget ----------
                try { Limiter.Apply(budget, S.PowerLimit, prof?.MinPerfPct ?? 40); }
                catch (Exception ex) { Log.Write("power limit failed: " + ex.Message); }


                // ---------- battery ----------
                Battery.Tick(onAc, pct);

                _rpmLimitActive = !double.IsInfinity(rpmLimit);
                _fanCap = double.IsInfinity(rpmLimit) ? 0 : (int)Math.Round(rpmLimit);

                // ---------- telemetry ----------
                var t = new Telemetry
                {
                    Time = DateTime.Now,
                    Temps = temps,
                    ControlTemp = tc,
                    SmoothedTemp = double.IsNaN(_smooth) ? tc : Math.Round(_smooth, 1),
                    CpuPct = Math.Round(cpu, 0),
                    OnAC = onAc,
                    BatteryPct = pct,
                    PowerW = Sens.PowerW,
                    PackageW = double.IsNaN(Sens.PackageW) ? -1 : Sens.PackageW,
                    LoadPct = double.IsNaN(_cpuSmooth) ? 0 : Math.Round(_cpuSmooth, 0),
                    Charging = Sens.Charging,
                    HpModeCode = _appliedCode,
                    HpMode = ModeOf(_appliedCode),
                    TargetMode = target,
                    ControlMode = S.ControlMode,
                    ActiveProfile = auto ? prof?.Name ?? "" : "",
                    ActiveRule = ruleNames,
                    FanRpm = Sens.FanRpm,
                    FanTargetRpm = Sens.FanTargetRpm,
                    FanLimitRpm = double.IsInfinity(rpmLimit) ? 0 : (int)Math.Round(rpmLimit),
                    PerfBudget = Limiter.CurrentBudget,
                    AgentConnected = AgentConnected(),
                    BiosOk = Bios.Available,
                    Context = DescribeContext(),
                    LastError = Tel.LastError,
                };
                t.Status = auto
                    ? (prof == null ? "Auto" : prof.HandsOff ? "Auto · " + prof.Name + " (HP firmware in control)" : "Auto · " + prof.Name + " · " + Settings.ModeLabel(target) + (_boost ? " (busy)" : "") + (_forceQuiet ? " (fan limit)" : "") + (safety ? " · SAFETY" : ""))
                    : "Manual · " + Settings.ModeLabel(target);
                Tel = t;

                if ((now - _lastHistory).TotalSeconds >= 5)
                {
                    _lastHistory = now;
                    _history.AddLast(new HistoryPoint { T = DateTime.Now, Temp = tc, Smooth = t.SmoothedTemp, Mode = _appliedCode, Budget = t.PerfBudget, PowerW = t.PowerW, Cpu = t.CpuPct, Fan = t.FanRpm });
                    int max = Math.Max(60, S.General.HistoryMinutes * 12);
                    while (_history.Count > max) _history.RemoveFirst();
                }

                int interval = onAc ? Math.Max(1000, S.General.PollAcMs) : Math.Max(1500, S.General.PollDcMs);
                // on battery, when nothing is changing, poll at half rate
                bool calm = !onAc && _limBudget == 100 && !_forceQuiet && _pendingIdx < 0 && !double.IsNaN(tc) && !double.IsNaN(_smooth) && Math.Abs(tc - _smooth) < 1.5;
                return calm ? interval * 2 : interval;
            }
        }

        /// <summary>Chip power as a load %: ~6 W (light use) = 0 %, ~16 W (a game or a heavy job) = 100 %.</summary>
        static double PowerLoad(double w) => double.IsNaN(w) ? 0 : Math.Max(0, Math.Min(100, (w - 6) * 10));

        // ================= ladder =================
        double _cpuSmooth = double.NaN;
        readonly List<DateTime> _switches = new List<DateTime>();
        bool _huntLogged;

        string EvaluateLadder(Profile p, double cpu, DateTime now)
        {
            // anti-hunting: the more often the ladder has switched lately, the longer it waits before switching again
            _switches.RemoveAll(t => (now - t).TotalSeconds > 600);
            int recent = _switches.Count(t => (now - t).TotalSeconds < 300);
            double damp = recent >= 4 ? 3 : recent >= 3 ? 2 : 1;
            if (damp > 1 && !_huntLogged) { _huntLogged = true; Log.Event("Fan was switching often — waiting longer before the next change"); }
            if (damp == 1) _huntLogged = false;
            double upDelay = p.UpDelaySec * damp, downDelay = p.DownDelaySec * damp, dwell = p.MinDwellSec * damp;

            var steps = (p.Ladder ?? new List<LadderStep>()).OrderBy(s => s.FromTempC).ToList();
            if (steps.Count == 0) return "Balanced";
            if (_ladderProfile != p.Name) { _ladderProfile = p.Name; _ladderIdx = -1; _pendingIdx = -1; }
            if (double.IsNaN(_smooth)) return steps[Math.Max(0, _ladderIdx)].Mode;

            double tv = _smooth + (p.LoadAnticipation != null && p.LoadAnticipation.Enabled ? Math.Max(0, p.LoadAnticipation.Eval(cpu)) : 0);
            int desired = 0;
            for (int i = 0; i < steps.Count; i++) if (tv >= steps[i].FromTempC) desired = i;

            if (_ladderIdx < 0 || _ladderIdx >= steps.Count) { _ladderIdx = desired; _lastSwitch = now; _pendingIdx = -1; return steps[_ladderIdx].Mode; }

            if (desired > _ladderIdx)
            {
                if (_pendingIdx != desired || _pendingIdx <= _ladderIdx) { _pendingIdx = desired; _pendingSince = now; }
                if ((now - _pendingSince).TotalSeconds >= upDelay) { Switch(desired, steps, now, "rising"); }
            }
            else if (desired < _ladderIdx)
            {
                int down = 0;
                for (int i = 0; i < steps.Count; i++) if (tv + p.HysteresisC >= steps[i].FromTempC) down = i;
                if (down < _ladderIdx)
                {
                    if (_pendingIdx != down || _pendingIdx >= _ladderIdx) { _pendingIdx = down; _pendingSince = now; }
                    if ((now - _pendingSince).TotalSeconds >= downDelay && (now - _lastSwitch).TotalSeconds >= dwell)
                        Switch(down, steps, now, "cooling");
                }
                else _pendingIdx = -1;
            }
            else _pendingIdx = -1;
            return steps[_ladderIdx].Mode;
        }

        void Switch(int idx, List<LadderStep> steps, DateTime now, string why)
        {
            Log.Event(string.Format(CultureInfo.InvariantCulture, "Ladder {0} -> {1} ({2}, {3:0.0} °C)", Settings.ModeLabel(steps[_ladderIdx].Mode), Settings.ModeLabel(steps[idx].Mode), why, _smooth));
            _ladderIdx = idx; _pendingIdx = -1; _lastSwitch = now;
            _switches.Add(now);
        }

        // ================= fan-speed limiter =================
        void ResetLimiter()
        {
            _limBudget = 100; _forceQuiet = false; _overSince = null; _underSince = null;
        }

        /// <summary>Fan-speed limiter: over the limit → force Quiet, then trim CPU budget step by step;
        /// comfortably under → give the budget back, then release Quiet. The fan reacts slowly, so each step
        /// needs the condition to hold for LimitHoldSec / ReleaseHoldSec.</summary>
        double _clampRpm; DateTime _clampTime = DateTime.MinValue;

        void RunLimiter(Profile p, double rpmLimit, string ladderMode, DateTime now)
        {
            var cfg = S.PowerLimit;
            if (double.IsInfinity(rpmLimit)) { ResetLimiter(); return; }
            int rpm = Sens.FanRpm;
            // with HP Control driving the fan, the curve is capped at the limit, so judge by what it wanted
            if (FanStopper != null && FanStopper.Holding) rpm = Math.Max(rpm, FanStopper.WantedRpm);
            // the ladder itself wants Quiet again (load gone): nothing left to hold back
            if (_forceQuiet && _limBudget >= 100 && (ladderMode == "Quiet" || ladderMode == "PowerSaver") && rpm < rpmLimit - cfg.ReleaseMarginRpm)
            {
                _forceQuiet = false; _overSince = null; _underSince = null;
                return;
            }
            if (rpm > rpmLimit + 100)
            {
                _underSince = null;
                if (_overSince == null) _overSince = now;
                if ((now - _overSince.Value).TotalSeconds >= cfg.LimitHoldSec)
                {
                    if (!_forceQuiet)
                    {
                        _forceQuiet = true; _clampRpm = rpm; _clampTime = now;
                        Log.Event(string.Format(CultureInfo.InvariantCulture, "Fan {0:N0} rpm over the {1:N0} rpm limit — forcing Quiet mode", rpm, rpmLimit));
                    }
                    else if (p.PowerLimiting && _limBudget > p.MinPerfPct) { _limBudget = Math.Max(p.MinPerfPct, _limBudget - cfg.StepPct); }
                    _overSince = now;
                }
            }
            else if (rpm < rpmLimit - cfg.ReleaseMarginRpm)
            {
                _overSince = null;
                if (_underSince == null) _underSince = now;
                if ((now - _underSince.Value).TotalSeconds >= cfg.ReleaseHoldSec)
                {
                    if (_limBudget < 100) _limBudget = Math.Min(100, _limBudget + cfg.StepPct);
                    else if (_forceQuiet)
                    {
                        // only hand back to the louder mode if the limit now allows the speed it ran at, or after RetrySec
                        bool fits = rpmLimit - cfg.ReleaseMarginRpm >= _clampRpm;
                        bool retry = (now - _clampTime).TotalSeconds >= Math.Max(60, cfg.RetrySec);
                        if (fits || retry)
                        {
                            _forceQuiet = false;
                            Log.Event(fits ? "Fan limit has room again — releasing forced Quiet" : "Retrying the louder mode (forced Quiet for " + (int)(now - _clampTime).TotalMinutes + " min)");
                        }
                    }
                    _underSince = now;
                }
            }
            else { _overSince = null; _underSince = null; }
        }

        // ================= firmware mode =================
        void ApplyMode(string target, bool auto, DateTime now)
        {
            if (!Bios.Available) return;
            int want = Code(target);
            if (want < 0) return;

            // periodically read back to notice changes made by the HP app / hotkeys
            if (_appliedCode < 0 || (now - _lastModeRead).TotalSeconds >= 10)
            {
                _lastModeRead = now;
                int cur = Bios.GetThermalMode();
                if (cur >= 0)
                {
                    if (_appliedCode >= 0 && cur != _appliedCode && !_forceApply)
                    {
                        Log.Event("Mode changed outside HP Control (" + Settings.ModeLabel(ModeOf(_appliedCode)) + " -> " + Settings.ModeLabel(ModeOf(cur)) + ")");
                        if (auto && (now - _lastOutsideNotice).TotalMinutes >= 10)
                        {
                            _lastOutsideNotice = now;
                            SendToAgent(new Dictionary<string, object> { { "cmd", "notify" }, { "args", new Dictionary<string, object> {
                                { "title", "HP Control" }, { "text", "Something else (usually the HP app) changed the fan mode, so HP Control switched it back. Close the HP app's System control page to stop them fighting." } } } });
                        }
                        if (!auto)
                        {
                            // follow the change in manual mode (e.g. picked in the HP app)
                            S.ManualHpMode = ModeOf(cur);
                            Store.Save(S);
                            _appliedCode = cur;
                            return;
                        }
                    }
                    _appliedCode = cur;
                }
            }

            if (want == _appliedCode && !_forceApply) return;
            if (!_forceApply && (now - _lastModeFail).TotalSeconds < (target == "PowerSaver" ? 300 : 30)) return;
            _forceApply = false;
            if (want == _appliedCode) return;
            if (Bios.SetThermalMode(want, out string detail))
            {
                Log.Event("HP mode -> " + Settings.ModeLabel(target) + (string.IsNullOrEmpty(_modeWhy) ? "" : " (" + _modeWhy + ")"));
                _appliedCode = want;
                Tel.LastError = "";
            }
            else
            {
                _lastModeFail = now;
                // Power saver is refused by the firmware while plugged in (the HP app greys it out too) — fall back to Quiet
                int alt = target == "PowerSaver" ? Code("Quiet") : -1;
                if (alt >= 0 && Bios.SetThermalMode(alt, out _))
                {
                    _appliedCode = alt;
                    Tel.LastError = "Power saver isn't available right now (plugged in?) — using Quiet";
                    Log.Event(Tel.LastError);
                    return;
                }
                Tel.LastError = "Could not set HP mode " + target + " (" + detail + ")";
                Log.Event(Tel.LastError);
            }
        }

        // ================= rules =================
        bool RuleMatches(Rule r, bool onAc, int pct, DateTime local)
        {
            bool agent = AgentConnected();
            switch (r.When)
            {
                case "AC": return onAc;
                case "Battery": return !onAc;
                case "BatteryBelow": return !onAc && pct < r.Value;
                case "QuietHours": return r.InTimeWindow(local);
                case "Meeting": return agent && (Ctx.MicInUse || Ctx.CamInUse);
                case "Fullscreen": return agent && Ctx.Fullscreen;
                case "Game":
                    // a game in front (detected automatically, or listed in Apps) that is actually using the CPU or
                    // graphics a bit (30 s average load, so menus and short pauses don't flip it back and forth)
                    if (!agent) return false;
                    bool listed = !string.IsNullOrEmpty(Ctx.ForegroundApp) && !string.IsNullOrWhiteSpace(r.Apps) &&
                        r.Apps.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries).Any(a => a.Trim().ToLowerInvariant().Replace(".exe", "") == Ctx.ForegroundApp.ToLowerInvariant().Replace(".exe", ""));
                    if (!(Ctx.GameInFront || listed)) return false;
                    if (_gameActive) return _load30 >= 8 || (DateTime.UtcNow - _gameLastBusy).TotalSeconds < 120;
                    if (_load30 >= 20) { _gameActive = true; _gameLastBusy = DateTime.UtcNow; return true; }
                    return false;
                case "Tablet": return agent && Ctx.Tablet;
                case "Idle": return agent && Ctx.IdleSec >= r.Value * 60;
                case "App":
                    if (!agent || string.IsNullOrEmpty(Ctx.ForegroundApp) || string.IsNullOrWhiteSpace(r.Apps)) return false;
                    var fg = Ctx.ForegroundApp.ToLowerInvariant().Replace(".exe", "");
                    return r.Apps.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Any(a => a.Trim().ToLowerInvariant().Replace(".exe", "") == fg);
                case "Always": return true;
            }
            return false;
        }

        string DescribeContext()
        {
            if (!AgentConnected()) return "tray helper not running";
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Ctx.ForegroundApp)) parts.Add(Ctx.ForegroundApp);
            if (Ctx.Fullscreen) parts.Add("fullscreen");
            if (Ctx.MicInUse) parts.Add("mic in use");
            if (Ctx.CamInUse) parts.Add("camera in use");
            if (Ctx.Tablet) parts.Add("tablet");
            if (Ctx.IdleSec > 60) parts.Add("idle " + (Ctx.IdleSec / 60) + " min");
            return string.Join(" · ", parts);
        }

        public List<HistoryPoint> History(int minutes)
        {
            lock (Lock)
            {
                var from = DateTime.Now.AddMinutes(-minutes);
                return _history.Where(h => h.T >= from).ToList();
            }
        }

        // ================= commands from UI / agent =================
        public void UpdateSettings(Settings s)
        {
            lock (Lock)
            {
                S = s;
                _ladderIdx = -1;
                Store.Save(S);
            }
            Kick(true);
        }

        public void CycleProfile(int dir)
        {
            lock (Lock)
            {
                if (S.ControlMode == "Auto")
                {
                    var names = S.Profiles.Select(p => p.Name).ToList();
                    bool onAc = Tel.OnAC;
                    string cur = onAc ? S.ProfileAC : S.ProfileDC;
                    int i = names.FindIndex(n => n == cur);
                    string next = names[((i < 0 ? 0 : i) + dir + names.Count) % names.Count];
                    if (onAc) S.ProfileAC = next; else S.ProfileDC = next;
                    SendToAgent(new Dictionary<string, object> { { "cmd", "osd" }, { "args", new Dictionary<string, object> { { "title", "Profile" }, { "text", next } } } });
                }
                else
                {
                    var modes = new[] { "Quiet", "Balanced", "Cool", "Performance", "PowerSaver", "SmartSense" };
                    int i = Array.IndexOf(modes, S.ManualHpMode);
                    S.ManualHpMode = modes[((i < 0 ? 0 : i) + dir + modes.Length) % modes.Length];
                    SendToAgent(new Dictionary<string, object> { { "cmd", "osd" }, { "args", new Dictionary<string, object> { { "title", "HP mode" }, { "text", Settings.ModeLabel(S.ManualHpMode) } } } });
                }
                Store.Save(S);
            }
            Kick(true);
        }
    }
}
