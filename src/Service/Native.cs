using System;
using System.Runtime.InteropServices;

namespace HPControl.Service
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME { public uint Low, High; public ulong Value => ((ulong)High << 32) | Low; }

        [DllImport("kernel32.dll")]
        public static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

        [DllImport("powrprof.dll")]
        public static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);

        [DllImport("powrprof.dll")]
        public static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);

        [DllImport("powrprof.dll")]
        public static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);

        [DllImport("powrprof.dll")]
        public static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);

        [DllImport("powrprof.dll")]
        public static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);

        [DllImport("powrprof.dll")]
        public static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);

        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr p);

        public static Guid ActiveScheme()
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var p) != 0) return Guid.Empty;
            try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); }
            finally { LocalFree(p); }
        }
    }
}
