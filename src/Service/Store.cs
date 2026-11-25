using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HPControl.Service
{
    /// <summary>
    /// Settings file and upgrades. Rules for upgrades (MigrateToVn): back up first (see Backup), and only change a
    /// value or a profile that is still exactly at the old default, so anything the user changed is left alone.
    /// Every change is logged, and the Settings page can import any backup to undo it.
    /// </summary>
    public static class Store
    {
        static string SettingsPath => Path.Combine(AppInfo.DataDir, "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var def = Settings.CreateDefault();
                    var loaded = new Settings();
                    var raw = Json.Parse(File.ReadAllText(SettingsPath)) as Dictionary<string, object> ?? new Dictionary<string, object>();
                    Json.Populate(loaded, raw);
                    // keep defaults for anything missing
                    if (loaded.Profiles == null || loaded.Profiles.Count == 0) loaded.Profiles = def.Profiles;
                    if (loaded.Rules == null) loaded.Rules = def.Rules;
                    loaded.ModeCodes = def.ModeCodes;   // only the verified firmware codes (0-5) are ever written
                    if (loaded.PowerLimit == null) loaded.PowerLimit = def.PowerLimit;
                    if (loaded.General == null) loaded.General = def.General;
                    if (loaded.Battery == null) loaded.Battery = def.Battery;
                    if (loaded.FanStop == null) loaded.FanStop = def.FanStop;
                    // a different HP Control version saved this file (an update or a reinstall): keep a copy first,
                    // named after the version it came from, so any change below can be undone by importing it
                    if (loaded.Version != Settings.CurrentVersion || loaded.SavedBy != AppInfo.Version) Backup(loaded);
                    if (loaded.Version < 2)
                    {
                        // v2: performance whenever busy, Power saver on battery when light
                        if (loaded.FindProfile("Adaptive") == null) loaded.Profiles.Insert(Math.Min(1, loaded.Profiles.Count), Settings.AdaptiveProfile());
                        if (loaded.FindProfile("Battery smart") == null) loaded.Profiles.Insert(Math.Min(2, loaded.Profiles.Count), Settings.BatterySmartProfile());
                        loaded.ProfileAC = "Adaptive";
                        loaded.ProfileDC = "Battery smart";
                        loaded.ControlMode = "Auto";
                        loaded.Version = 2;
                        Save(loaded);
                        Log.Event("Settings updated: Adaptive (plugged in) and Battery smart (on battery) profiles added and selected, Auto on");
                    }
                    if (loaded.Version < 3) MigrateToV3(loaded, raw);
                    if (loaded.Version < 4) MigrateToV4(loaded);
                    if (loaded.Version < 5) MigrateToV5(loaded);
                    if (loaded.Version < 6) MigrateToV6(loaded);
                    if (loaded.Version < 7) MigrateToV7(loaded);
                    if (loaded.Version < 8) MigrateToV8(loaded);
                    if (loaded.Version < 9) MigrateToV9(loaded);
                    if (loaded.Version < 10) MigrateToV10(loaded);
                    if (loaded.Version < 11) MigrateToV11(loaded);
                    if (loaded.Version < 12) MigrateToV12(loaded);
                    if (loaded.Version < 13) MigrateToV13(loaded);
                    return loaded;
                }
            }
            catch (Exception ex) { Log.Event("Settings file unreadable, using defaults: " + ex.Message); }
            return Settings.CreateDefault();
        }

        /// <summary>v3: everything works in fan RPM (no dBA / noise map), and a smaller set of profiles.</summary>
        static void MigrateToV3(Settings s, Dictionary<string, object> raw)
        {
            // the old noise map (rpm → dBA) turns any dBA limits the user had into rpm limits
            var map = new Curve(true, 0, 22, 2000, 23.5, 2500, 26, 3000, 29, 3500, 33, 4000, 37, 4500, 42, 5000, 44, 5500, 45, 6500, 45);
            try
            {
                var pts = raw.Obj("Fan")?.Obj("NoiseMap")?.Arr("Points");
                if (pts != null && pts.Count >= 2) { map = new Curve(true); foreach (var o in pts.OfType<Dictionary<string, object>>()) map.Points.Add(new CurvePoint(o.Num("X"), o.Num("Y"))); }
            }
            catch { }
            Func<double, int> toRpm = db => { var r = map.Inverse(db); return double.IsNaN(r) || double.IsInfinity(r) ? 0 : (int)(Math.Round(r / 50) * 50); };

            var rename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Silent", Settings.QuietName }, { "Balanced+", Settings.AdaptiveName }, { "Cool lap", Settings.AdaptiveName },
                { "Performance", Settings.MaxPerfName }, { "Battery", Settings.BatterySmartName }
            };
            Func<string, string> map2 = n => string.IsNullOrEmpty(n) ? n : rename.TryGetValue(n, out var m) ? m : n;

            var rawProfiles = raw.Arr("Profiles");
            var list = Settings.DefaultProfiles();
            for (int i = 0; i < s.Profiles.Count; i++)
            {
                var p = s.Profiles[i];
                if (rename.ContainsKey(p.Name)) continue;   // old built-in profile, replaced by the new set
                // match the raw entry by name (earlier migrations may have inserted profiles, so indexes can differ)
                var rp = rawProfiles?.OfType<Dictionary<string, object>>().FirstOrDefault(d => string.Equals(d.Str("Name", ""), p.Name, StringComparison.OrdinalIgnoreCase));
                if (rp != null)
                {
                    double ceil = rp.Num("NoiseCeilingDb", 0);
                    if (ceil > 0 && p.FanMaxRpm == 0) p.FanMaxRpm = toRpm(ceil);
                    var na = rp.Obj("NoiseAllow");
                    if (na != null && na.Bool("Enabled") && !(p.RpmCap?.Enabled ?? false) && na.Arr("Points") != null)
                    {
                        var c = new Curve(true);
                        foreach (var q in na.Arr("Points").OfType<Dictionary<string, object>>()) { int r = toRpm(q.Num("Y")); c.Points.Add(new CurvePoint(q.Num("X"), r == 0 ? 6500 : r)); }
                        p.RpmCap = c;
                    }
                }
                int idx = list.FindIndex(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) list[idx] = p; else list.Add(p);   // keeps the user's own Adaptive / Battery smart tweaks and any profiles they made
            }
            s.Profiles = list;
            s.ProfileAC = map2(s.ProfileAC); s.ProfileDC = map2(s.ProfileDC);
            if (s.FindProfile(s.ProfileAC) == null) s.ProfileAC = Settings.AdaptiveName;
            if (s.FindProfile(s.ProfileDC) == null) s.ProfileDC = Settings.BatterySmartName;

            var rawRules = raw.Arr("Rules");
            var rules = new List<Rule>();
            for (int i = 0; i < s.Rules.Count; i++)
            {
                var r = s.Rules[i];
                if (r.Name == "Tablet / tent" && !r.Enabled) continue;   // dropped from the defaults
                r.Profile = map2(r.Profile);
                var rr = rawRules != null && i < rawRules.Count ? rawRules[i] as Dictionary<string, object> : null;
                double c = rr?.Num("NoiseCeilingDb", 0) ?? 0;
                if (c > 0 && r.FanRpmMax == 0) r.FanRpmMax = toRpm(c);
                rules.Add(r);
            }
            s.Rules = rules;
            s.Version = 3;
            Save(s);
            Log.Event("Settings updated: fan limits are now in rpm, and the profiles are Adaptive, Battery smart, Quiet, Max performance and HP Smart Sense");
        }

        /// <summary>v4: calmer Adaptive / Battery smart (only switch for sustained heavy load, stay longer).
        /// Only replaces them if the user hasn't changed the old timing; keeps their fan limits.</summary>
        static void MigrateToV4(Settings s)
        {
            foreach (var name in new[] { Settings.AdaptiveName, Settings.BatterySmartName })
            {
                int i = s.Profiles.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (i < 0) continue;
                var old = s.Profiles[i];
                bool untouched = old.Ladder.Count == 2 && Math.Abs(old.Ladder[1].FromTempC - 56) < 0.1 && old.DownDelaySec == 40 && old.MinDwellSec == 60;
                if (!untouched) continue;
                var p2 = name == Settings.AdaptiveName ? Settings.AdaptiveProfile() : Settings.BatterySmartProfile();
                p2.FanMaxRpm = old.FanMaxRpm; p2.RpmCap = old.RpmCap; p2.PerfCap = old.PerfCap;
                s.Profiles[i] = p2;
            }
            s.Version = 4;
            Save(s);
            Log.Event("Settings updated: Adaptive and Battery smart now only switch for sustained heavy loads and stay longer (less fan changing)");
        }

        /// <summary>v5: Adaptive uses HP Smart Sense instead of Quiet for everyday use (Performance under heavy load is
        /// unchanged), and fan stop is on. Only changes the ladder if it's still the v4 default.</summary>
        static void MigrateToV5(Settings s)
        {
            var p = s.FindProfile(Settings.AdaptiveName);
            if (p != null && p.Ladder.Count == 2 && p.Ladder[0].Mode == "Quiet" && p.Ladder[0].FromTempC <= 0.1 && p.Ladder[1].Mode == "Performance")
            {
                p.Ladder[0].Mode = "SmartSense";
                p.Description = Settings.AdaptiveProfile().Description;
            }
            if (s.FanStop == null) s.FanStop = new FanStopSettings();
            s.Version = 5;
            Save(s);
            Log.Event("Settings updated: Adaptive now uses HP Smart Sense for everyday use (Performance under heavy load), and the fan switches off once the laptop has cooled (fan stop)");
        }

        /// <summary>v6: fan stop thresholds (superseded by v10's fan curve).</summary>
        static void MigrateToV6(Settings s) { s.Version = 6; }

        /// <summary>v7: Adaptive goes back to Quiet for everyday use (the fan controller keeps the fan off at higher
        /// temperatures in Quiet than in Smart Sense). Only if the ladder is still the v5 default.</summary>
        static void MigrateToV7(Settings s)
        {
            var p = s.FindProfile(Settings.AdaptiveName);
            if (p != null && p.Ladder.Count == 2 && p.Ladder[0].Mode == "SmartSense" && p.Ladder[0].FromTempC <= 0.1 && p.Ladder[1].Mode == "Performance")
            {
                p.Ladder[0].Mode = "Quiet";
                p.Description = Settings.AdaptiveProfile().Description;
            }
            s.Version = 7;
            Save(s);
            Log.Event("Settings updated: Adaptive uses HP Quiet for everyday use again (fan stays off at higher temperatures), Performance under heavy load");
        }

        /// <summary>v8: fan stop thresholds (superseded by v10's fan curve).</summary>
        static void MigrateToV8(Settings s) { s.Version = 8; }

        /// <summary>v9: fan off below ~60 °C CPU. Decisions use averages (no reaction to short bursts); the "case"
        /// sensor is really an internal EC sensor, so it's only a safety limit. Only changes untouched values.</summary>
        static void MigrateToV9(Settings s)
        {
            var lbl = s.General?.SensorLabels;
            if (lbl != null && (!lbl.TryGetValue("case", out var l) || l == "Case (EC)")) lbl["case"] = "Internal (EC)";
            s.Version = 9;
            Save(s);
            Log.Event("Settings updated: the fan stays off up to ~60 °C CPU and only comes back on for sustained heat or load");
        }

        /// <summary>v10: HP Control's own fan curve (smooth, ramped) replaces "hold off / hand back", and Adaptive
        /// switches to Performance on sustained load (plugged in) instead of by temperature.</summary>
        static void MigrateToV10(Settings s)
        {
            s.FanStop = new FanStopSettings { Enabled = s.FanStop == null || s.FanStop.Enabled };
            int i = s.Profiles.FindIndex(p => p.Name.Equals(Settings.AdaptiveName, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                var old = s.Profiles[i];
                if (old.Ladder.Count == 2 && old.Ladder[0].Mode == "Quiet" && old.Ladder[1].Mode == "Performance")
                {
                    var p2 = Settings.AdaptiveProfile();
                    p2.FanMaxRpm = old.FanMaxRpm; p2.RpmCap = old.RpmCap; p2.PerfCap = old.PerfCap;
                    s.Profiles[i] = p2;
                }
            }
            // the old placeholder "Games" rule (specific apps, off, no apps listed) becomes the auto-detecting one
            var g = s.Rules.FirstOrDefault(r => r.Name == "Games" && r.When == "App" && !r.Enabled && string.IsNullOrWhiteSpace(r.Apps));
            if (g != null) { g.When = "Game"; g.Enabled = true; g.PluggedInOnly = true; if (string.IsNullOrEmpty(g.Profile)) g.Profile = Settings.MaxPerfName; }
            else if (!s.Rules.Any(r => r.When == "Game"))
                s.Rules.Add(new Rule { Enabled = true, Name = "Games", When = "Game", Profile = Settings.MaxPerfName, PluggedInOnly = true });
            s.Version = 10;
            Save(s);
            Log.Event("Settings updated: HP Control now drives the fan with its own smooth curve, and Adaptive switches to Performance as soon as a heavy load is sustained (plugged in), and games are detected automatically");
        }

        /// <summary>v11: the fan switches off sooner after a load (faster wind-down). Only changes untouched values.</summary>
        static void MigrateToV11(Settings s)
        {
            if (s.FanStop != null && Math.Abs(s.FanStop.RampDownRpmPerSec - 40) < 0.1) s.FanStop.RampDownRpmPerSec = 60;
            s.Version = 11;
            Save(s);
            Log.Event("Settings updated: the fan switches off sooner once a load has ended");
        }

        /// <summary>v12: the fan stays at its lowest speed (1,950 rpm) up to ~66 °C, with gentler chip-power and
        /// internal-sensor floors. Only replaces curves still at their v10 defaults.</summary>
        static void MigrateToV12(Settings s)
        {
            var f = s.FanStop; var d = new FanStopSettings();
            if (f != null)
            {
                string Sig(Curve c) => c == null ? "" : string.Join(";", c.Points.Select(q => q.X + ":" + q.Y));
                if (Sig(f.Curve) == "55:1950;62:2300;68:2800;74:3400;80:4000;86:4700;90:5300;94:5800") f.Curve = d.Curve;
                if (Sig(f.PowerRamp) == "10:0;14:2100;20:2700;28:3300;35:3800") f.PowerRamp = d.PowerRamp;
                if (Sig(f.InternalCurve) == "50:0;54:2300;58:3000;62:3800;66:4600;70:5600") f.InternalCurve = d.InternalCurve;
            }
            s.Version = 12;
            Save(s);
            Log.Event("Settings updated: the fan stays at its lowest speed (1,950 rpm) up to about 66 °C");
        }

        /// <summary>v13: Battery smart switches to Balanced on sustained load instead of CPU temperature (the CPU runs
        /// warmer with the fan off, which was switching to Balanced at light loads). Only if its ladder is untouched.</summary>
        static void MigrateToV13(Settings s)
        {
            var b = s.FindProfile(Settings.BatterySmartName);
            if (b != null && b.Ladder.Count == 2 && b.Ladder[0].Mode == "PowerSaver" && b.Ladder[0].FromTempC <= 0 &&
                b.Ladder[1].Mode == "Balanced" && Math.Abs(b.Ladder[1].FromTempC - 64) < 0.5)
            {
                var d = Settings.BatterySmartProfile();
                b.Ladder = d.Ladder; b.LoadAnticipation = d.LoadAnticipation; b.Description = d.Description;
                b.BoostMode = d.BoostMode; b.BoostOnBattery = d.BoostOnBattery; b.BoostLoadPct = d.BoostLoadPct; b.BoostAfterSec = d.BoostAfterSec; b.BoostHoldSec = d.BoostHoldSec;
            }
            s.Version = 13;
            Save(s);
            Log.Event("Settings updated: Battery smart now switches to Balanced for sustained heavy loads only, not for CPU temperature");
        }

        public static string BackupDir => Path.Combine(AppInfo.DataDir, "backups");

        /// <summary>Copies settings.json to backups\ as "settings from HP Control 1.7.5 (v13) 2026-11-25 1830.json".
        /// Keeps the 15 newest.</summary>
        static void Backup(Settings loaded)
        {
            try
            {
                Directory.CreateDirectory(BackupDir);
                string from = string.IsNullOrEmpty(loaded.SavedBy) ? "an older HP Control" : "HP Control " + loaded.SavedBy;
                string name = "settings from " + from + " (v" + loaded.Version + ") " + DateTime.Now.ToString("yyyy-MM-dd HHmm") + ".json";
                File.Copy(SettingsPath, Path.Combine(BackupDir, name), true);
                foreach (var old in new DirectoryInfo(BackupDir).GetFiles("settings from *.json").OrderByDescending(f => f.CreationTimeUtc).Skip(15))
                    try { old.Delete(); } catch { }
                Log.Event("Settings backed up before updating: backups\\" + name);
            }
            catch (Exception ex) { Log.Write("settings backup failed: " + ex.Message); }
        }

        public static void Save(Settings s)
        {
            try
            {
                s.SavedBy = AppInfo.Version;
                Directory.CreateDirectory(AppInfo.DataDir);
                var tmp = SettingsPath + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(s, true));
                if (File.Exists(SettingsPath)) File.Replace(tmp, SettingsPath, null);
                else File.Move(tmp, SettingsPath);
            }
            catch (Exception ex) { Log.Write("settings save failed: " + ex.Message); }
        }
    }
}
