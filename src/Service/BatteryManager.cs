using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text;

namespace HPControl.Service
{
    public sealed class BatteryManager
    {
        const string HealthSetting = "Battery Health Manager";
        const string HealthOn = "Maximize my battery health On";
        const string HealthOff = "Maximize my battery health Off";
        const string ExtenderSetting = "Adaptive Battery Extender";

        readonly Engine _e;
        DateTime _fullSince = DateTime.MinValue;

        public BatteryManager(Engine e) { _e = e; }

        static double Wmi(string cls, string prop) =>
            WmiQuery.First(@"root\wmi", "SELECT " + prop + " FROM " + cls, o => Convert.ToDouble(o[prop]), double.NaN);

        static string WmiStr(string cls, string prop) =>
            WmiQuery.First(@"root\wmi", "SELECT " + prop + " FROM " + cls, o => Convert.ToString(o[prop])?.Trim(), null);

        DateTime _staticTime = DateTime.MinValue, _settingsTime = DateTime.MinValue;
        double _design = double.NaN;
        string _mfr, _dev, _hm, _ae, _aes;

        public Dictionary<string, object> GetInfo()
        {
            var d = new Dictionary<string, object>();
            Native.GetSystemPowerStatus(out var ps);
            d["percent"] = ps.BatteryLifePercent <= 100 ? ps.BatteryLifePercent : 0;
            d["onAC"] = ps.ACLineStatus == 1;
            d["runtimeMin"] = ps.BatteryLifeTime > 0 ? ps.BatteryLifeTime / 60 : -1;

            double full = Wmi("BatteryFullChargedCapacity", "FullChargedCapacity");
            double rem = Wmi("BatteryStatus", "RemainingCapacity");
            double volt = Wmi("BatteryStatus", "Voltage");
            double cr = Wmi("BatteryStatus", "ChargeRate"), dr = Wmi("BatteryStatus", "DischargeRate");
            double cycles = Wmi("BatteryCycleCount", "CycleCount");
            // static data and BIOS settings barely change but are slow to query: cache them
            var now = DateTime.UtcNow;
            bool refreshStatic = (now - _staticTime).TotalMinutes >= 10;
            if (refreshStatic)
            {
                _design = Wmi("BatteryStaticData", "DesignedCapacity");
                _mfr = WmiStr("BatteryStaticData", "ManufactureName");
                _dev = WmiStr("BatteryStaticData", "DeviceName");
                _staticTime = now;
            }
            double design = _design;
            d["fullChargeWh"] = double.IsNaN(full) ? 0 : Math.Round(full / 1000.0, 2);
            d["remainingWh"] = double.IsNaN(rem) ? 0 : Math.Round(rem / 1000.0, 2);
            d["voltageV"] = double.IsNaN(volt) ? 0 : Math.Round(volt / 1000.0, 2);
            d["chargeW"] = double.IsNaN(cr) ? 0 : Math.Round(cr / 1000.0, 2);
            d["dischargeW"] = double.IsNaN(dr) ? 0 : Math.Round(dr / 1000.0, 2);
            d["cycles"] = double.IsNaN(cycles) ? -1 : (int)cycles;

            // HP BIOS battery block (same data the HP app shows under "Advanced information")
            try
            {
                var r = _e.Bios.Read(HpBios.TypeBattery, 128);
                if (r.Ok && r.Data != null && r.Data.Length >= 64)
                {
                    var b = r.Data;
                    int designMah = BitConverter.ToUInt16(b, 0), fullMah = BitConverter.ToUInt16(b, 2);
                    int designMv = BitConverter.ToUInt16(b, 16);
                    d["designMah"] = designMah;
                    d["fullMah"] = fullMah;
                    d["temperatureC"] = BitConverter.ToUInt16(b, 10);
                    d["currentA"] = Math.Round(BitConverter.ToInt16(b, 14) / 1000.0, 3);
                    d["designVoltageV"] = Math.Round(designMv / 1000.0, 2);
                    var cells = new List<double>();
                    for (int i = 0; i < 4; i++) { int mv = BitConverter.ToUInt16(b, 20 + i * 2); if (mv > 0) cells.Add(mv / 1000.0); }
                    d["cellsV"] = cells;
                    if (cycles < 0 || double.IsNaN(cycles)) d["cycles"] = (int)BitConverter.ToUInt16(b, 8);
                    var ascii = Encoding.ASCII.GetString(b, 28, b.Length - 28);
                    int z = ascii.IndexOf('\0'); if (z >= 0) ascii = ascii.Substring(0, z);
                    if (ascii.Length >= 16) { d["serial"] = ascii.Substring(0, 5).Trim(); d["manufactureDate"] = ascii.Substring(6, 10); d["batteryId"] = ascii.Substring(16).Trim(); }
                    if (double.IsNaN(design) || design <= 0) design = designMah * (double)designMv / 1000.0;
                }
            }
            catch { }
            d["manufacturer"] = string.Join(" · ", new[] { _mfr, _dev }.Where(x => !string.IsNullOrEmpty(x)));
            d["designWh"] = double.IsNaN(design) ? 0 : Math.Round(design / 1000.0, 2);
            double health = (!double.IsNaN(full) && design > 0) ? Math.Min(100, 100.0 * full / design) : double.NaN;
            d["healthPct"] = double.IsNaN(health) ? -1 : Math.Round(health, 1);
            d["healthText"] = double.IsNaN(health) ? "Unknown" : health >= 80 ? "Normal" : health >= 60 ? "Fair" : "Weak — consider replacement";

            if ((now - _settingsTime).TotalSeconds >= 60)
            {
                _hm = HpBios.GetBiosSetting(HealthSetting, out _);
                _ae = HpBios.GetBiosSetting(ExtenderSetting, out _);
                _aes = HpBios.GetBiosSetting(ExtenderSetting + " Status", out _);
                _settingsTime = now;
            }
            d["healthManager"] = _hm == null ? "unsupported" : (_hm.EndsWith("On") ? "on" : "off");
            d["adaptiveExtender"] = _ae == null ? "unsupported" : (_ae.StartsWith("Enable") ? "on" : "off");
            d["adaptiveExtenderStatus"] = _aes ?? "";
            d["topUpPending"] = _e.S.Battery.TopUpPending;
            return d;
        }

        public bool SetHealthMode(bool on)
        {
            int rc = HpBios.SetBiosSetting(HealthSetting, on ? HealthOn : HealthOff);
            _settingsTime = DateTime.MinValue;
            Log.Event("Maximize battery health " + (on ? "ON" : "OFF") + (rc == 0 ? "" : " failed (BIOS code " + rc + ")"));
            if (rc != 0) throw new InvalidOperationException("BIOS refused the change (code " + rc + ")");
            return true;
        }

        public bool SetAdaptiveExtender(bool on)
        {
            int rc = HpBios.SetBiosSetting(ExtenderSetting, on ? "Enable" : "Disable");
            _settingsTime = DateTime.MinValue;
            Log.Event("Adaptive battery extender " + (on ? "enabled" : "disabled") + (rc == 0 ? "" : " failed (BIOS code " + rc + ")"));
            if (rc != 0) throw new InvalidOperationException("BIOS refused the change (code " + rc + ")");
            return true;
        }

        /// <summary>Temporarily allows charging to 100 %, then turns "Maximize battery health" back on once unplugged (or after 4 h at full).</summary>
        public bool StartTopUp()
        {
            SetHealthMode(false);
            lock (_e.Lock) { _e.S.Battery.TopUpPending = true; Store.Save(_e.S); }
            Log.Event("Top-up to 100% started — battery health mode will be restored automatically");
            return true;
        }

        public bool CancelTopUp()
        {
            lock (_e.Lock) { _e.S.Battery.TopUpPending = false; Store.Save(_e.S); }
            SetHealthMode(true);
            return true;
        }

        /// <summary>Called from the engine loop (under the engine lock).</summary>
        public void Tick(bool onAc, int pct)
        {
            if (!_e.S.Battery.TopUpPending) { _fullSince = DateTime.MinValue; return; }
            if (pct >= 100 && _fullSince == DateTime.MinValue) _fullSince = DateTime.UtcNow;
            bool done = !onAc || (_fullSince != DateTime.MinValue && (DateTime.UtcNow - _fullSince).TotalHours >= 4);
            if (!done) return;
            _e.S.Battery.TopUpPending = false;
            Store.Save(_e.S);
            if (_e.S.Battery.RestoreHealthModeAfterTopUp)
            {
                try { SetHealthMode(true); } catch (Exception ex) { Log.Event("Could not restore battery health mode: " + ex.Message); }
            }
        }
    }
}
