using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HPControl.Service
{
    /// <summary>
    /// Trims CPU heat output by temporarily adjusting processor power settings of the ACTIVE power plan
    /// (EPP, boost mode, max processor state). Every original value is journalled to disk and restored when
    /// the budget returns to 100 %, the service stops, or after a crash/reboot. If the user edits one of the
    /// settings while it is being managed, the user's value wins and is never overwritten.
    /// </summary>
    public sealed class PowerLimiter
    {
        static readonly Guid SubProcessor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        static readonly Guid Epp = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
        static readonly Guid Epp1 = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6864");
        static readonly Guid BoostMode = new Guid("be337238-0d82-4146-a960-4f3749d470c7");
        static readonly Guid MaxState = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec");
        static readonly Guid MaxState1 = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ed");

        public class JournalEntry
        {
            public string Scheme;
            public string Setting;
            public bool Ac;
            public int Original;
            public int Applied;
        }

        public class Journal { public List<JournalEntry> Entries = new List<JournalEntry>(); }

        readonly string _path = Path.Combine(AppInfo.DataDir, "power-journal.json");
        Journal _j = new Journal();
        public int CurrentBudget { get; private set; } = 100;

        public PowerLimiter()
        {
            try { if (File.Exists(_path)) _j = Json.Deserialize<Journal>(File.ReadAllText(_path)) ?? new Journal(); } catch { _j = new Journal(); }
            if (_j.Entries.Count > 0)
            {
                Log.Event("Restoring CPU power settings left over from the previous session");
                RestoreAll();
            }
        }

        void Save()
        {
            try
            {
                if (_j.Entries.Count == 0) { if (File.Exists(_path)) File.Delete(_path); }
                else File.WriteAllText(_path, Json.Serialize(_j, true));
            }
            catch (Exception ex) { Log.Write("power journal save failed: " + ex.Message); }
        }

        static bool ReadIdx(Guid scheme, Guid setting, bool ac, out int v)
        {
            var sch = scheme; var sub = SubProcessor; var set = setting; uint val;
            uint r = ac ? Native.PowerReadACValueIndex(IntPtr.Zero, ref sch, ref sub, ref set, out val)
                        : Native.PowerReadDCValueIndex(IntPtr.Zero, ref sch, ref sub, ref set, out val);
            v = (int)val;
            return r == 0;
        }

        static bool WriteIdx(Guid scheme, Guid setting, bool ac, int v)
        {
            var sch = scheme; var sub = SubProcessor; var set = setting;
            uint r = ac ? Native.PowerWriteACValueIndex(IntPtr.Zero, ref sch, ref sub, ref set, (uint)v)
                        : Native.PowerWriteDCValueIndex(IntPtr.Zero, ref sch, ref sub, ref set, (uint)v);
            return r == 0;
        }

        static void Activate(Guid scheme) { var s = scheme; Native.PowerSetActiveScheme(IntPtr.Zero, ref s); }

        /// <summary>Sets a managed value, journalling the original the first time.</summary>
        void Manage(Guid scheme, Guid setting, bool ac, int target)
        {
            if (!ReadIdx(scheme, setting, ac, out int cur)) return;
            var e = _j.Entries.FirstOrDefault(x => x.Scheme == scheme.ToString() && x.Setting == setting.ToString() && x.Ac == ac);
            if (e == null)
            {
                if (cur == target) return;
                e = new JournalEntry { Scheme = scheme.ToString(), Setting = setting.ToString(), Ac = ac, Original = cur, Applied = cur };
                _j.Entries.Add(e);
            }
            else if (cur != e.Applied)
            {
                // User (or another tool) changed it while we managed it: adopt their value as the new original.
                Log.Event("A CPU power setting was changed outside HP Control; keeping your value");
                e.Original = cur;
            }
            if (cur != target && WriteIdx(scheme, setting, ac, target)) e.Applied = target;
            else e.Applied = cur;
        }

        void Release(JournalEntry e)
        {
            var scheme = new Guid(e.Scheme); var setting = new Guid(e.Setting);
            if (ReadIdx(scheme, setting, e.Ac, out int cur) && cur == e.Applied && cur != e.Original)
                WriteIdx(scheme, setting, e.Ac, e.Original);
        }

        public void RestoreAll()
        {
            var schemes = _j.Entries.Select(x => x.Scheme).Distinct().ToList();
            foreach (var e in _j.Entries.ToList()) { try { Release(e); } catch { } }
            _j.Entries.Clear();
            Save();
            var active = Native.ActiveScheme();
            if (schemes.Contains(active.ToString())) Activate(active);
            CurrentBudget = 100;
        }

        /// <summary>Applies a CPU performance budget (100 = untouched).</summary>
        public void Apply(int budget, PowerLimitSettings cfg, int minPct)
        {
            budget = Math.Max(minPct, Math.Min(100, budget));
            if (budget == CurrentBudget) return;
            if (budget >= 100) { RestoreAll(); Log.Event("CPU power limit released"); return; }

            var scheme = Native.ActiveScheme();
            if (scheme == Guid.Empty) return;
            // drop entries for other schemes (user switched plans) — restore them first
            foreach (var e in _j.Entries.Where(x => x.Scheme != scheme.ToString()).ToList()) { Release(e); _j.Entries.Remove(e); }

            foreach (bool ac in new[] { true, false })
            {
                if (cfg.UseEpp)
                {
                    foreach (var g in new[] { Epp, Epp1 })
                    {
                        int orig = OriginalOrCurrent(scheme, g, ac);
                        if (orig < 0) continue;
                        int target = (int)Math.Round(orig + (100 - budget) * Math.Max(0, 90 - orig) / 60.0);
                        Manage(scheme, g, ac, Math.Min(90, Math.Max(orig, target)));
                    }
                }
                if (cfg.UseBoost)
                {
                    int orig = OriginalOrCurrent(scheme, BoostMode, ac);
                    if (orig >= 0)
                    {
                        int target = orig;
                        if (budget < cfg.BoostOffBelow) target = 0;               // disabled
                        else if (budget < 90 && orig != 0) target = 3;              // efficient enabled
                        Manage(scheme, BoostMode, ac, target);
                    }
                }
                if (cfg.UseMaxState)
                {
                    foreach (var g in new[] { MaxState, MaxState1 })
                    {
                        int orig = OriginalOrCurrent(scheme, g, ac);
                        if (orig < 0) continue;
                        int target = budget < cfg.BoostOffBelow ? Math.Max(minPct, budget + (100 - cfg.BoostOffBelow)) : 100;
                        Manage(scheme, g, ac, Math.Min(orig, target));
                    }
                }
            }
            Save();
            Activate(scheme);
            Log.Event("CPU power budget " + budget + "%");
            CurrentBudget = budget;
        }

        int OriginalOrCurrent(Guid scheme, Guid setting, bool ac)
        {
            var e = _j.Entries.FirstOrDefault(x => x.Scheme == scheme.ToString() && x.Setting == setting.ToString() && x.Ac == ac);
            if (e != null) return e.Original;
            return ReadIdx(scheme, setting, ac, out int v) ? v : -1;
        }
    }
}
