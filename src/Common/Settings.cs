using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HPControl
{
    public class CurvePoint
    {
        public double X;
        public double Y;
        public CurvePoint() { }
        public CurvePoint(double x, double y) { X = x; Y = y; }
    }

    /// <summary>Piecewise-linear curve, clamped at the ends.</summary>
    public class Curve
    {
        public bool Enabled = true;
        public List<CurvePoint> Points = new List<CurvePoint>();

        public Curve() { }
        public Curve(bool enabled, params double[] xy)
        {
            Enabled = enabled;
            for (int i = 0; i + 1 < xy.Length; i += 2) Points.Add(new CurvePoint(xy[i], xy[i + 1]));
        }

        public double Eval(double x)
        {
            if (Points == null || Points.Count == 0) return double.NaN;
            var p = Points.OrderBy(q => q.X).ToList();
            if (x <= p[0].X) return p[0].Y;
            for (int i = 1; i < p.Count; i++)
            {
                if (x <= p[i].X)
                {
                    double t = (x - p[i - 1].X) / Math.Max(1e-9, p[i].X - p[i - 1].X);
                    return p[i - 1].Y + t * (p[i].Y - p[i - 1].Y);
                }
            }
            return p[p.Count - 1].Y;
        }

        /// <summary>Inverse of Eval for a rising curve: the lowest X whose value reaches y.</summary>
        public double Inverse(double y)
        {
            if (Points == null || Points.Count == 0) return double.NaN;
            var p = Points.OrderBy(q => q.X).ToList();
            if (y <= p[0].Y) return p[0].X;
            for (int i = 1; i < p.Count; i++)
                if (y <= p[i].Y && p[i].Y > p[i - 1].Y)
                    return p[i - 1].X + (y - p[i - 1].Y) / (p[i].Y - p[i - 1].Y) * (p[i].X - p[i - 1].X);
            return double.PositiveInfinity;   // louder than the map's top point: no limit
        }

        public Curve Clone()
        {
            var c = new Curve { Enabled = Enabled };
            foreach (var q in Points) c.Points.Add(new CurvePoint(q.X, q.Y));
            return c;
        }
    }

    /// <summary>"Use HP mode <Mode> once the control temperature reaches FromTempC".</summary>
    public class LadderStep
    {
        public double FromTempC;
        public string Mode = "Balanced";
        public LadderStep() { }
        public LadderStep(double t, string m) { FromTempC = t; Mode = m; }
    }

    public class Profile
    {
        public string Name = "New profile";
        public string Description = "";
        /// <summary>When true the profile just applies FixedMode and leaves everything to HP firmware.</summary>
        public bool HandsOff;
        public string FixedMode = "SmartSense";

        /// <summary>Temperature -> HP thermal mode (the firmware fan curve family).</summary>
        public List<LadderStep> Ladder = new List<LadderStep>();
        /// <summary>Highest fan speed allowed at any temperature (0 = no limit). Over it, HP Control forces Quiet, then trims CPU power.</summary>
        public int FanMaxRpm;
        /// <summary>Temperature -> CPU performance budget % (100 = uncapped). Pre-emptively trims heat so the fan never needs to ramp.</summary>
        public Curve PerfCap = new Curve(false, 60, 100, 80, 100, 90, 80);
        /// <summary>Temperature -> highest fan speed allowed (advanced; combines with FanMaxRpm, the lower wins).</summary>
        public Curve RpmCap = new Curve(false, 50, 3100, 70, 3600, 85, 4700, 95, 6000);
        /// <summary>CPU load % -> extra "virtual" degrees, so the ladder reacts before the heat arrives.</summary>
        public Curve LoadAnticipation = new Curve(false, 30, 0, 70, 3, 100, 6);
        /// <summary>Busy boost: switch to this HP mode while the laptop is busy (empty = off). Busy = load (CPU % or
        /// chip power) averaging BoostLoadPct over 10 s for BoostAfterSec; it ends once the 30 s average is under
        /// 60 % of that for BoostHoldSec.</summary>
        public string BoostMode = "";
        public double BoostLoadPct = 45;
        public int BoostAfterSec = 8;
        public int BoostHoldSec = 60;
        public bool BoostOnBattery;

        public bool PowerLimiting = true;
        public int MinPerfPct = 45;
        public double HysteresisC = 3;
        public int UpDelaySec = 4;
        public int DownDelaySec = 30;
        public int MinDwellSec = 20;
        public double SmoothingSec = 6;

        public Profile Clone() => Json.Deserialize<Profile>(Json.Serialize(this));
    }

    /// <summary>Context rule. The first enabled matching rule with a Profile wins; fan and CPU limits from all matching rules combine (lowest wins).</summary>
    public class Rule
    {
        public bool Enabled = true;
        public string Name = "Rule";
        /// <summary>AC | Battery | BatteryBelow | QuietHours | Meeting | App | Game | Fullscreen | Tablet | Idle | Always.
        /// Game = a game in front (detected automatically, or listed in Apps) that's using the CPU or graphics.</summary>
        public string When = "Always";
        public string Apps = "";
        public double Value;
        public string From = "22:00";
        public string To = "07:00";
        public string Profile = "";
        public int PerfCapPct;
        /// <summary>Highest fan speed allowed while this rule matches (0 = none).</summary>
        public int FanRpmMax;
        /// <summary>Only while plugged in.</summary>
        public bool PluggedInOnly;

        public bool InTimeWindow(DateTime now)
        {
            if (!TimeSpan.TryParse(From, CultureInfo.InvariantCulture, out var f) || !TimeSpan.TryParse(To, CultureInfo.InvariantCulture, out var t)) return false;
            var n = now.TimeOfDay;
            return f <= t ? (n >= f && n < t) : (n >= f || n < t);
        }
    }


    public class PowerLimitSettings
    {
        /// <summary>Manage EPP (energy performance preference) when budget &lt; 100.</summary>
        public bool UseEpp = true;
        /// <summary>Manage processor boost mode when budget &lt; 100.</summary>
        public bool UseBoost = true;
        /// <summary>Manage maximum processor state when budget &lt; 100.</summary>
        public bool UseMaxState = true;
        /// <summary>Budget below which turbo boost is turned off.</summary>
        public int BoostOffBelow = 70;
        public int StepPct = 10;
        public int LimitHoldSec = 12;
        public int ReleaseHoldSec = 40;
        /// <summary>The fan has to drop this far under its RPM limit before the limiter backs off.</summary>
        public int ReleaseMarginRpm = 300;
        /// <summary>After the limiter forces Quiet, hand control back early only if the limit has risen enough for the
        /// fan speed that caused it; otherwise try again after this many seconds (stops the fan pumping up and down).</summary>
        public int RetrySec = 300;
    }

    public class GeneralSettings
    {
        public bool Notifications = true;
        public bool TrayIcon = true;
        public bool AgentAutostart = true;
        public bool HotkeyEnabled = true;
        /// <summary>Global hotkey to cycle profiles, e.g. "Ctrl+Alt+F".</summary>
        public string Hotkey = "Ctrl+Alt+F";
        public double SafetyTempC = 95;
        public bool RestoreSmartSenseOnStop = true;
        public int PollAcMs = 2000;
        public int PollDcMs = 4000;
        public int HistoryMinutes = 30;
        /// <summary>Show the advanced settings (curve editor, timing, rules, sensors, CPU limiter, engine).</summary>
        public bool AdvancedMode;
        /// <summary>Sensors used for the control temperature (max of): tz0, tz1, tz3, gpu ...</summary>
        public string ControlSensors = "cpu,tz0";
        public Dictionary<string, string> SensorLabels = new Dictionary<string, string> { { "cpu", "CPU (EC)" }, { "case", "Internal (EC)" }, { "tz0", "SoC zone" }, { "tz1", "Board" }, { "tz3", "Rear / VR" }, { "bat", "Battery" } };
        /// <summary>Check GitHub for a new version once a day (the tray helper offers it; installing always asks first).</summary>
        public bool AutoUpdate = true;
        /// <summary>GitHub repository (owner/name) whose latest release is the update source.</summary>
        public string UpdateRepo = "Hellohi3654/HPControl";
    }

    /// <summary>HP Control's own fan curve (via HP's firmware fan override): off while the CPU is cool and idle,
    /// a smooth temperature curve when on, with a heads-up from chip power, ramped so it never jumps. The fan goes
    /// back to the fan controller at the hand-back temperatures or if anything goes wrong.</summary>
    public class FanStopSettings
    {
        public bool Enabled = true;
        /// <summary>Fan comes on when the CPU averages this over 30 s and is still this warm now (or right away from
        /// FastOnC, or when a heavy job has run for 20 s, see PowerRamp)...</summary>
        public double OnAboveC = 60;
        /// <summary>A CPU this hot right now starts the fan straight away (so does 10 °C less held for ~10 s): that's a
        /// real heavy load, not a burst.</summary>
        public double FastOnC = 85;
        /// <summary>...and goes off again (after at least a minute on) when the CPU averages this or less...</summary>
        public double OffBelowC = 55;
        /// <summary>...and the load (CPU % or chip power) is at or under this. Both are 15 s averages, after the fan has
        /// been on for at least MinOnSec.</summary>
        public double OffMaxLoadPct = 30;
        public double MinOnSec = 30;
        /// <summary>The internal sensor only switches the fan on (or keeps it on) at this 1-minute average or more (off
        /// again 3 °C lower). It's slow and stays warm for minutes after a load, so below this it only adds speed while
        /// something is running.</summary>
        public double InternalOnC = 62;
        /// <summary>When switching off, the fan winds down at 3x the ramp-down rate and stops from this speed.</summary>
        public double StopFromRpm = 2300;
        /// <summary>Fan speed (rpm) by CPU temperature (smoothed over ~10 s). 1,950 rpm is the lowest speed there is.</summary>
        public Curve Curve = new Curve(true, 55, 1950, 66, 1950, 72, 2400, 78, 3000, 84, 3800, 88, 4500, 92, 5200, 95, 5800);
        /// <summary>Heads-up from chip power (W → rpm): a heavy job starts the fan gently before the heat arrives. It only
        /// switches the fan on once the power has stayed up for 20 s (so bursts never wake it); while on, it sets a
        /// minimum speed from the 20 s average.</summary>
        public Curve PowerRamp = new Curve(true, 12, 0, 15, 1950, 22, 2300, 30, 2900, 37, 3400);
        /// <summary>Fan speed by the internal (EC) sensor's 1-minute average, so a long heavy session also cools the
        /// inside of the laptop. Reads well above what the case feels like (≈40 °C at idle).</summary>
        public Curve InternalCurve = new Curve(true, 56, 0, 58, 1950, 62, 2400, 66, 3200, 70, 4400);
        /// <summary>Ramp rates. Above 75 °C the ramp-up gets faster (double at 85 °C) so the fan keeps up with a heavy load.</summary>
        public double RampUpRpmPerSec = 120;
        public double RampDownRpmPerSec = 60;
        /// <summary>Give the fan back to the fan controller if the CPU reaches this...</summary>
        /// <summary>Safety: the fan goes back to the fan controller if the CPU stays at or above this for 15 s (or hits
        /// 99 °C), or the internal sensor averages HandBackInternalC over a minute.</summary>
        public double HandBackCpuC = 95;
        /// <summary>...or the internal EC sensor averages this over a minute.</summary>
        public double HandBackInternalC = 72;
    }

    public class BatterySettings
    {
        public bool TopUpPending;
        public bool RestoreHealthModeAfterTopUp = true;
    }

    public class Settings
    {
        public int Version = CurrentVersion;
        /// <summary>HP Control version that last saved this file (backups before an upgrade are named after it).</summary>
        public string SavedBy = "";
        public const int CurrentVersion = 13;
        /// <summary>Manual = fixed HP mode; Auto = profiles + rules + curves.</summary>
        public string ControlMode = "Auto";
        public string ManualHpMode = "SmartSense";
        public string ProfileAC = "Adaptive";
        public string ProfileDC = "Battery smart";
        public List<Profile> Profiles = new List<Profile>();
        public List<Rule> Rules = new List<Rule>();
        public PowerLimitSettings PowerLimit = new PowerLimitSettings();
        public GeneralSettings General = new GeneralSettings();
        public BatterySettings Battery = new BatterySettings();
        public FanStopSettings FanStop = new FanStopSettings();
        /// <summary>HP BIOS thermal-mode byte for each mode name (command 0x5A).</summary>
        public Dictionary<string, int> ModeCodes = new Dictionary<string, int>();

        public static readonly string[] ModeNames = { "SmartSense", "Balanced", "Cool", "Quiet", "PowerSaver", "Performance" };

        public static string ModeLabel(string m)
        {
            switch (m)
            {
                case "SmartSense": return "Smart sense";
                case "PowerSaver": return "Power saver";
                default: return m;
            }
        }

        public Profile FindProfile(string name) => Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        public const string SmartSenseName = "HP Smart Sense", AdaptiveName = "Adaptive", BatterySmartName = "Battery smart", QuietName = "Quiet", MaxPerfName = "Max performance";

        public static Profile SmartSenseProfile() => new Profile { Name = SmartSenseName, Description = "Hands control back to HP firmware, the same as the HP app.", HandsOff = true, FixedMode = "SmartSense" };

        /// <summary>Plugged in: quiet while idle or light, full HP Performance (maximum CPU power) whenever something heavy runs.</summary>
        public static Profile AdaptiveProfile() => new Profile
        {
            Name = AdaptiveName,
            Description = "HP Quiet for everyday use. Switches to HP Performance (35 W) within about 10 seconds of a sustained heavy load (a game, a render, a compile) while plugged in, and back about a minute after it ends. The fan follows HP Control's own smooth curve either way.",
            Ladder = { new LadderStep(0, "Quiet") },
            LoadAnticipation = new Curve(false, 30, 0, 50, 8, 75, 16, 100, 22),
            BoostMode = "Performance", BoostLoadPct = 45, BoostAfterSec = 8, BoostHoldSec = 60, BoostOnBattery = false,
            PowerLimiting = false,
            HysteresisC = 8, UpDelaySec = 20, DownDelaySec = 90, MinDwellSec = 180, SmoothingSec = 6
        };

        /// <summary>On battery: Power saver while idle or light for the best battery life, Balanced when something heavy runs.</summary>
        public static Profile BatterySmartProfile() => new Profile
        {
            Name = BatterySmartName,
            Description = "Power saver for everyday use, for the best battery life. Balanced while something heavy keeps running (about 15 seconds), back to Power saver about 45 seconds after it ends. Never Performance.",
            // load decides, not temperature: with HP Control keeping the fan off, the CPU sits warmer at light loads,
            // and a temperature step would switch to Balanced (more power) for nothing
            Ladder = { new LadderStep(0, "PowerSaver") },
            LoadAnticipation = new Curve(false, 30, 0, 50, 8, 75, 16, 100, 22),
            BoostMode = "Balanced", BoostOnBattery = true, BoostLoadPct = 40, BoostAfterSec = 15, BoostHoldSec = 45,   // 40 % ≈ Power saver's 10 W limit maxed out
            PowerLimiting = false,
            HysteresisC = 8, UpDelaySec = 20, DownDelaySec = 90, MinDwellSec = 180, SmoothingSec = 6
        };

        /// <summary>Keeps the fan at or under a speed limit, trimming CPU power if Quiet mode alone isn't enough.</summary>
        public static Profile QuietProfile() => new Profile
        {
            Name = QuietName,
            Description = "HP Quiet mode with a fan speed limit. If the fan still goes over the limit, CPU power is trimmed until it's back under.",
            Ladder = { new LadderStep(0, "Quiet") },
            FanMaxRpm = 3000,
            PowerLimiting = true, MinPerfPct = 40,
            HysteresisC = 5, UpDelaySec = 8, DownDelaySec = 40, MinDwellSec = 60, SmoothingSec = 4
        };

        public static Profile MaxPerformanceProfile() => new Profile
        {
            Name = MaxPerfName,
            Description = "HP Performance all the time: maximum CPU power, and the fan runs as fast as it needs to.",
            Ladder = { new LadderStep(0, "Performance") },
            PowerLimiting = false
        };

        public static List<Profile> DefaultProfiles() => new List<Profile> { SmartSenseProfile(), AdaptiveProfile(), BatterySmartProfile(), QuietProfile(), MaxPerformanceProfile() };

        public static Settings CreateDefault()
        {
            var s = new Settings();
            s.ModeCodes = new Dictionary<string, int> { { "Performance", 0 }, { "Balanced", 1 }, { "Cool", 2 }, { "Quiet", 3 }, { "PowerSaver", 4 }, { "SmartSense", 5 } };
            s.Profiles.AddRange(DefaultProfiles());
            s.Rules.Add(new Rule { Enabled = false, Name = "Quiet hours", When = "QuietHours", From = "22:00", To = "07:00", FanRpmMax = 3000 });
            s.Rules.Add(new Rule { Enabled = false, Name = "Calls & meetings", When = "Meeting", FanRpmMax = 3000 });
            s.Rules.Add(new Rule { Enabled = true, Name = "Games", When = "Game", Apps = "", Profile = MaxPerfName, PluggedInOnly = true });
            s.Rules.Add(new Rule { Enabled = false, Name = "Low battery", When = "BatteryBelow", Value = 20, Profile = BatterySmartName, PerfCapPct = 70 });
            return s;
        }
    }
}
