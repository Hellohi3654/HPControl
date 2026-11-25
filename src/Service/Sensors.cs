using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;

namespace HPControl.Service
{
    /// <summary>Cheap sensor reads. Nothing here needs a kernel driver.</summary>
    public sealed class Sensors : IDisposable
    {
        readonly HpBios _bios;
        readonly List<KeyValuePair<string, PerformanceCounter>> _zones = new List<KeyValuePair<string, PerformanceCounter>>();
        ulong _lastIdle, _lastTotal;
        DateTime _lastBatt = DateTime.MinValue;
        DateTime _lastBiosBatt = DateTime.MinValue;
        public double PowerW;
        public bool Charging;
        public double BatteryTemp = double.NaN;
        public int FanRpm = -1, FanTargetRpm = -1;
        /// <summary>CPU package power (CPU + graphics + memory controller) in W, from Windows' "Energy Meter"
        /// counters (Intel RAPL). NaN when unavailable.</summary>
        public double PackageW = double.NaN;
        public double BoardTemp = double.NaN;
        PerformanceCounter _pkg; bool _pkgTried;
        public string FanRaw = "";
        int _fanFails; DateTime _fanRetry = DateTime.MinValue;

        public Sensors(HpBios bios)
        {
            _bios = bios;
            try
            {
                var cat = new PerformanceCounterCategory("Thermal Zone Information");
                foreach (var inst in cat.GetInstanceNames().OrderBy(x => x))
                {
                    // \_TZ.TZS0 -> tz0
                    string key = "tz" + new string(inst.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
                    if (key == "tz") key = inst;
                    var pc = new PerformanceCounter("Thermal Zone Information", "High Precision Temperature", inst, true);
                    _zones.Add(new KeyValuePair<string, PerformanceCounter>(key, pc));
                }
            }
            catch (Exception ex) { Log.Write("Thermal zone counters unavailable: " + ex.Message); }
        }

        /// <summary>CPU and EC temperatures every time; the Windows thermal zones only when <paramref name="allZones"/>
        /// (someone is looking) or when listed in <paramref name="zonesWanted"/> (used for control).</summary>
        public Dictionary<string, double> ReadTemps(bool allZones = true, ICollection<string> zonesWanted = null)
        {
            var d = new Dictionary<string, double>();
            double cpu = _bios.Available ? _bios.GetCpuTemp() : double.NaN;
            if (!double.IsNaN(cpu)) d["cpu"] = cpu;
            double board = double.NaN, cs = double.NaN;
            if (_bios.Available) _bios.GetSystemTemps(out board, out cs);
            if (!double.IsNaN(cs)) d["case"] = cs;
            BoardTemp = board;
            foreach (var z in _zones)
            {
                if (!allZones && (zonesWanted == null || !zonesWanted.Contains(z.Key))) continue;
                try
                {
                    double t = z.Value.NextValue() / 10.0 - 273.15;
                    if (t > -20 && t < 130) d[z.Key] = Math.Round(t, 1);
                }
                catch { }
            }
            if (!double.IsNaN(BatteryTemp)) d["bat"] = BatteryTemp;
            return d;
        }

        /// <summary>Fan speed from the BIOS thermal sensor block (the same call HP's diagnostics use).
        /// Gives up after a few failures and retries every 5 minutes, so unsupported machines cost nothing.</summary>
        public void ReadFan(bool withTarget)
        {
            if (!_bios.Available) return;
            var now = DateTime.UtcNow;
            if (_fanFails >= 3 && now < _fanRetry) return;
            var v = _bios.ReadFanWords(HpBios.SensorFanSpeed, out var raw);
            FanRaw = raw == null ? "" : BitConverter.ToString(raw, 0, Math.Min(8, raw.Length));
            if (v == null || v.Length == 0)
            {
                if (++_fanFails >= 3) { _fanRetry = now.AddMinutes(5); FanRpm = -1; FanTargetRpm = -1; }
                return;
            }
            _fanFails = 0;
            FanRpm = v.Max();
            if (withTarget)
            {
                var t = _bios.ReadFanWords(HpBios.SensorFanTarget, out _);
                FanTargetRpm = t != null && t.Length > 0 ? t.Max() : -1;
            }
        }

        public void ReadPackagePower()
        {
            if (_pkg == null && !_pkgTried)
            {
                _pkgTried = true;
                try
                {
                    var cat = new PerformanceCounterCategory("Energy Meter");
                    var inst = cat.GetInstanceNames().FirstOrDefault(n => n.IndexOf("PKG", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (inst != null) { _pkg = new PerformanceCounter("Energy Meter", "Power", inst, true); _pkg.NextValue(); }
                }
                catch (Exception ex) { Log.Write("package power counter unavailable: " + ex.Message); }
            }
            if (_pkg == null) return;
            try { double w = _pkg.NextValue() / 1000.0; PackageW = w >= 0 && w < 200 ? Math.Round(w, 1) : double.NaN; }
            catch { PackageW = double.NaN; }
        }

        public double ReadCpuPct()
        {
            if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
            ulong i = idle.Value, total = kernel.Value + user.Value;
            ulong di = i - _lastIdle, dt = total - _lastTotal;
            _lastIdle = i; _lastTotal = total;
            if (dt == 0) return 0;
            return Math.Max(0, Math.Min(100, 100.0 * (1.0 - (double)di / dt)));
        }

        /// <summary>AC/battery state every time (cheap); the battery's charge/discharge rate (a WMI query) every 10 s
        /// while someone is looking, otherwise once a minute.</summary>
        public void ReadPower(out bool onAc, out int pct, bool watching = true)
        {
            onAc = true; pct = 100;
            if (Native.GetSystemPowerStatus(out var s))
            {
                onAc = s.ACLineStatus == 1;
                pct = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : 100;
            }
            var now = DateTime.UtcNow;
            if ((now - _lastBatt).TotalSeconds >= (watching ? 10 : 60))
            {
                _lastBatt = now;
                WmiQuery.First(@"root\wmi", "SELECT ChargeRate, DischargeRate, Charging FROM BatteryStatus", o =>
                {
                    double cr = Convert.ToDouble(o["ChargeRate"]), dr = Convert.ToDouble(o["DischargeRate"]);
                    Charging = Convert.ToBoolean(o["Charging"]);
                    PowerW = Math.Round((dr > 0 ? dr : -cr) / 1000.0, 2);
                    return true;
                }, false);
            }
            if ((now - _lastBiosBatt).TotalSeconds >= 30 && _bios.Available)
            {
                _lastBiosBatt = now;
                try
                {
                    var r = _bios.Read(HpBios.TypeBattery, 128);
                    if (r.Ok && r.Data != null && r.Data.Length > 12) BatteryTemp = BitConverter.ToUInt16(r.Data, 10);
                }
                catch { }
            }
        }

        public void Dispose()
        {
            foreach (var z in _zones) try { z.Value.Dispose(); } catch { }
            try { _pkg?.Dispose(); } catch { }
        }
    }
}
