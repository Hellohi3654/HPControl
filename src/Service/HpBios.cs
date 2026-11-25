using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace HPControl.Service
{
    public struct BiosResult
    {
        public int ReturnCode;
        public byte[] Data;
        public bool Ok => ReturnCode == 0;
        public override string ToString() => "ret=" + ReturnCode + " data=" + (Data == null ? "" : BitConverter.ToString(Data.Take(32).ToArray()));
    }

    /// <summary>
    /// HP BIOS WMI interface (root\wmi hpqBIntM) — the same channel HP's own System Info HSA service uses.
    /// Command 1 = read, 2 = write. Only documented/observed command types are written.
    /// </summary>
    public sealed class HpBios
    {
        public const uint CmdRead = 1, CmdWrite = 2;
        public const uint TypeThermalMode = 0x4C;   // HP "System control" mode on this platform (byte0: 0 Perf,1 Bal,2 Cool,3 Quiet,4 Saver,5 SmartSense)
        public const uint TypeCpuTemp = 0x28;       // byte1 = CPU temperature (°C)
        public const uint TypeBattery = 0x07;       // battery details (128 bytes)
        public const uint TypeFeatures = 0x0D;

        readonly object _lock = new object();
        ManagementScope _scope;
        ManagementObject _bi;
        ManagementClass _inCls;
        public bool Available { get; private set; }
        public string InitError { get; private set; } = "";

        public bool Init()
        {
            lock (_lock)
            {
                try
                {
                    _scope = new ManagementScope(@"\\.\root\wmi");
                    _scope.Connect();
                    using (var s = new ManagementObjectSearcher(_scope, new ObjectQuery("SELECT * FROM hpqBIntM")))
                    using (var col = s.Get())
                        foreach (ManagementObject o in col) { if (_bi == null) _bi = o; else o.Dispose(); }
                    _inCls = new ManagementClass(_scope, new ManagementPath("hpqBDataIn"), null);
                    Available = _bi != null;
                    if (!Available) InitError = "hpqBIntM not found";
                }
                catch (Exception ex) { Available = false; InitError = ex.Message; }
                return Available;
            }
        }

        public BiosResult Call(uint command, uint type, byte[] input, int outSize)
        {
            lock (_lock)
            {
                if (!Available) return new BiosResult { ReturnCode = -1 };
                if (input == null || input.Length == 0) input = new byte[4];
                using (var inst = _inCls.CreateInstance())
                {
                    inst["Sign"] = new byte[] { 0x53, 0x45, 0x43, 0x55 }; // "SECU"
                    inst["Command"] = command;
                    inst["CommandType"] = type;
                    inst["Size"] = (uint)input.Length;
                    inst["hpqBData"] = input;
                    string method = "hpqBIOSInt" + outSize;
                    using (var inParams = _bi.GetMethodParameters(method))
                    {
                        inParams["InData"] = inst;
                        using (var outParams = _bi.InvokeMethod(method, inParams, null))
                        {
                            using (var od = outParams?["OutData"] as ManagementBaseObject)
                            {
                                if (od == null) return new BiosResult { ReturnCode = -2 };
                                var r = new BiosResult { ReturnCode = Convert.ToInt32(od["rwReturnCode"]) };
                                if (outSize > 0) r.Data = od["Data"] as byte[];
                                return r;
                            }
                        }
                    }
                }
            }
        }

        public BiosResult Read(uint type, int outSize = 4) => Call(CmdRead, type, new byte[4], outSize);

        public int LastModeFlags { get; private set; }

        /// <summary>Current HP thermal mode byte, or -1.</summary>
        public int GetThermalMode()
        {
            try
            {
                var r = Read(TypeThermalMode, 4);
                if (r.Ok && r.Data != null && r.Data.Length > 1) LastModeFlags = r.Data[1];
                return r.Ok && r.Data != null && r.Data.Length > 0 ? r.Data[0] : -1;
            }
            catch { return -1; }
        }

        /// <summary>CPU temperature reported by the embedded controller via BIOS, or NaN.</summary>
        public double GetCpuTemp()
        {
            try
            {
                var r = Read(TypeCpuTemp, 128);
                if (r.Ok && r.Data != null && r.Data.Length > 1 && r.Data[1] > 0 && r.Data[1] < 120) return r.Data[1];
            }
            catch { }
            return double.NaN;
        }

        // Thermal sensor block (cmd 1, type 0x28): data[0] picks the sensor. This is how HP PC Hardware
        // Diagnostics reads them: 0 CPU temp, 1 GPU, 2 system, 3 fan status, 4 battery, 7 fan speed,
        // 8 fan target speed, 9 charging current. Reply: count, then one value per fan.
        public const int SensorFanStatus = 3, SensorFanSpeed = 7, SensorFanTarget = 8;

        /// <summary>Reads a 16-bit-per-entry sensor (fan speed / target) in RPM. This EC reports RPM directly
        /// (e.g. 0x08FC = 2300 rpm in Quiet); older ECs report a tach period, which HP's diagnostics convert with
        /// 245760 / period, so small values are treated as a period. Returns null when unsupported.</summary>
        public int[] ReadFanWords(int sensor, out byte[] raw)
        {
            raw = null;
            try
            {
                var r = Call(CmdRead, TypeCpuTemp, new byte[] { (byte)sensor, 0, 0, 0 }, 128);
                raw = r.Data;
                if (!r.Ok || r.Data == null || r.Data.Length < 4) return null;
                var d = r.Data;
                int count = d[0] + d[1] * 256;
                if (count <= 0 || count > 4) return new int[0];
                var res = new int[count];
                for (int i = 1; i <= count; i++)
                {
                    int pos = i * 2;
                    if (pos + 1 >= d.Length) break;
                    int v = d[pos] + d[pos + 1] * 256;
                    res[i - 1] = (v == 0 || v == 0xFFFF) ? 0 : v < 250 ? (245760 + v / 2) / v : v;
                }
                return res;
            }
            catch { return null; }
        }

        // ---------------- HP fan test interface ----------------
        // ACPI method \HODW, reached as command 3 / type 1 (it emulates the old INT 15h AX=9611h BL=2Bh call that HP's
        // diagnostics use). Read from this laptop's DSDT: sub-function BH=2 sets the embedded controller's software fan
        // override (FSH1=1) and fan target FSW1 = (491520 / CL) / 50 (50 rpm steps; CL=255 = fan off); BH=10h clears FSH1,
        // which hands the fan straight back to the controller. Nothing else is written.
        static byte[] HodwData(byte bh, byte cl)
        {
            var d = new byte[28];
            d[0] = 0x11; d[1] = 0x96;   // EAX = 9611h
            d[4] = 0x2B; d[5] = bh;     // BL = 2Bh (fan), BH = sub-function
            d[8] = cl;                  // CL = parameter
            return d;
        }

        /// <summary>Takes over the fan: 0 = off, otherwise the speed in rpm (the lowest non-zero speed is ~1,940 rpm).</summary>
        public bool FanOverride(int rpm)
        {
            byte cl = rpm <= 0 ? (byte)255 : (byte)Math.Max(1, Math.Min(254, (int)Math.Round(491520.0 / rpm)));
            try
            {
                var r = Call(3, 1, HodwData(2, cl), 128);
                return r.Ok && r.Data != null && r.Data.Length > 16 && r.Data[1] == 0 && r.Data[16] == 0;
            }
            catch { return false; }
        }

        /// <summary>Hands the fan back to the embedded controller. The firmware doesn't fill in the reply for this
        /// sub-function (it always reads "unsupported"), so there's nothing to check.</summary>
        public void FanRelease()
        {
            try { Call(3, 1, HodwData(0x10, 0), 128); } catch { }
        }

        /// <summary>The "system" sensor block (EC THS1, THS2): board = THS1, close to the air inside the case (~34 °C
        /// idle); case = THS2, which follows the chassis slowly (39 → 46 °C over a minute of full load).</summary>
        public void GetSystemTemps(out double board, out double caseT)
        {
            board = caseT = double.NaN;
            try
            {
                var r = Call(CmdRead, TypeCpuTemp, new byte[] { 2, 0, 0, 0 }, 128);
                if (!r.Ok || r.Data == null || r.Data.Length < 3) return;
                if (r.Data[0] >= 1 && r.Data[1] > 0 && r.Data[1] < 120) board = r.Data[1];
                if (r.Data[0] >= 2 && r.Data[2] > 0 && r.Data[2] < 120) caseT = r.Data[2];
            }
            catch { }
        }

        /// <summary>Sets the HP thermal mode (cmd 2, type 0x4C, 4-byte little-endian value), verifying by read-back.</summary>
        public bool SetThermalMode(int code, out string detail)
        {
            detail = "";
            try
            {
                var r = Call(CmdWrite, TypeThermalMode, new byte[] { (byte)code, 0, 0, 0 }, 0);
                detail = "write(4)=" + r.ReturnCode;
                if (!r.Ok)
                {
                    r = Call(CmdWrite, TypeThermalMode, new[] { (byte)code }, 0);
                    detail += " write(1)=" + r.ReturnCode;
                }
                int now = GetThermalMode();
                detail += " readback=" + now;
                return now == code;
            }
            catch (Exception ex) { detail = ex.Message; return false; }
        }

        // ---------------- HP InstrumentedBIOS settings ----------------
        public static string GetBiosSetting(string name, out string[] possible)
        {
            string[] pv0 = new string[0];
            var v = WmiQuery.First(@"root\HP\InstrumentedBIOS", "SELECT CurrentValue, PossibleValues FROM HP_BIOSEnumeration WHERE Name='" + name.Replace("'", "''") + "'", o =>
            {
                if (o["PossibleValues"] is string[] pv) pv0 = pv.Where(x => !string.IsNullOrEmpty(x)).ToArray();
                return Convert.ToString(o["CurrentValue"]);
            }, null);
            possible = pv0;
            return v;
        }

        /// <summary>Sets an HP BIOS setting (no BIOS admin password is set on this machine). Returns the BIOS return code (0 = success).</summary>
        public static int SetBiosSetting(string name, string value) =>
            WmiQuery.First(@"root\HP\InstrumentedBIOS", "SELECT * FROM HP_BIOSSettingInterface", o =>
            {
                using (var p = o.GetMethodParameters("SetBIOSSetting"))
                {
                    p["Name"] = name;
                    p["Value"] = value;
                    p["Password"] = "<utf-16/>";
                    using (var r = o.InvokeMethod("SetBIOSSetting", p, null))
                        return Convert.ToInt32(r["Return"]);
                }
            }, -1);
    }
}
