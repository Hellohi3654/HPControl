using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HPControl.App
{
    enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public int pid; public PROPERTYKEY(Guid g, int p) { fmtid = g; pid = p; } }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
        [FieldOffset(8)] public int i;
        [FieldOffset(8)] public short b;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow flow, uint mask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint c);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr n);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr n);
        [PreserveSig] int GetChannelCount(out uint c);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float v, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float v);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float v, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float v);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
        [PreserveSig] int VolumeStepUp(ref Guid ctx);
        [PreserveSig] int VolumeStepDown(ref Guid ctx);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float min, out float max, out float inc);
    }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string id, IntPtr f);
        [PreserveSig] int GetDeviceFormat(string id, int def, IntPtr f);
        [PreserveSig] int ResetDeviceFormat(string id);
        [PreserveSig] int SetDeviceFormat(string id, IntPtr f1, IntPtr f2);
        [PreserveSig] int GetProcessingPeriod(string id, int def, IntPtr a, IntPtr b);
        [PreserveSig] int SetProcessingPeriod(string id, IntPtr a);
        [PreserveSig] int GetShareMode(string id, IntPtr m);
        [PreserveSig] int SetShareMode(string id, IntPtr m);
        [PreserveSig] int GetPropertyValue(string id, IntPtr k, IntPtr v);
        [PreserveSig] int SetPropertyValue(string id, IntPtr k, IntPtr v);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, ERole role);
        [PreserveSig] int SetEndpointVisibility(string id, int visible);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }

    class AudioDeviceInfo
    {
        public string Id, Name;
        public bool IsDefault;
        public override string ToString() => Name;
    }

    static class CoreAudio
    {
        const uint CLSCTX_ALL = 23;
        public static readonly PROPERTYKEY FriendlyName = new PROPERTYKEY(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
        static readonly Guid IID_Volume = typeof(IAudioEndpointVolume).GUID;
        static Guid _ctx = Guid.Empty;

        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

        public static IMMDeviceEnumerator Enumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();

        public static string GetString(IMMDevice d, PROPERTYKEY key)
        {
            if (d.OpenPropertyStore(0, out var ps) != 0) return null;
            try
            {
                var k = key;
                if (ps.GetValue(ref k, out var pv) != 0) return null;
                try { return pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) : null; }
                finally { PropVariantClear(ref pv); }
            }
            finally { Marshal.ReleaseComObject(ps); }
        }

        public static List<AudioDeviceInfo> List(EDataFlow flow)
        {
            var res = new List<AudioDeviceInfo>();
            var en = Enumerator();
            try
            {
                string def = null;
                if (en.GetDefaultAudioEndpoint(flow, ERole.Multimedia, out var dd) == 0) { dd.GetId(out def); Marshal.ReleaseComObject(dd); }
                if (en.EnumAudioEndpoints(flow, 1 /*active*/, out var col) != 0) return res;
                col.GetCount(out uint n);
                for (uint i = 0; i < n; i++)
                {
                    if (col.Item(i, out var d) != 0) continue;
                    d.GetId(out string id);
                    res.Add(new AudioDeviceInfo { Id = id, Name = GetString(d, FriendlyName) ?? id, IsDefault = id == def });
                    Marshal.ReleaseComObject(d);
                }
                Marshal.ReleaseComObject(col);
            }
            finally { Marshal.ReleaseComObject(en); }
            return res;
        }

        public static IMMDevice Device(string id, EDataFlow flow, ERole role = ERole.Multimedia)
        {
            var en = Enumerator();
            try
            {
                IMMDevice d;
                int hr = string.IsNullOrEmpty(id) ? en.GetDefaultAudioEndpoint(flow, role, out d) : en.GetDevice(id, out d);
                return hr == 0 ? d : null;
            }
            finally { Marshal.ReleaseComObject(en); }
        }

        static T Activate<T>(IMMDevice d, Guid iid) where T : class
        {
            var g = iid;
            return d.Activate(ref g, CLSCTX_ALL, IntPtr.Zero, out object o) == 0 ? o as T : null;
        }

        public static IAudioEndpointVolume Volume(IMMDevice d) => Activate<IAudioEndpointVolume>(d, IID_Volume);

        public static void WithVolume(string id, EDataFlow flow, Action<IAudioEndpointVolume> act)
        {
            var d = Device(id, flow); if (d == null) return;
            try { var v = Volume(d); if (v != null) { try { act(v); } finally { Marshal.ReleaseComObject(v); } } }
            finally { Marshal.ReleaseComObject(d); }
        }

        public static float GetVolume(string id, EDataFlow flow) { float r = 0; WithVolume(id, flow, v => v.GetMasterVolumeLevelScalar(out r)); return r; }
        public static void SetVolume(string id, EDataFlow flow, float s) => WithVolume(id, flow, v => v.SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(1, s)), ref _ctx));
        public static bool GetMute(string id, EDataFlow flow) { bool m = false; WithVolume(id, flow, v => v.GetMute(out m)); return m; }
        public static void SetMute(string id, EDataFlow flow, bool m) => WithVolume(id, flow, v => v.SetMute(m, ref _ctx));

        public static void SetDefault(string id)
        {
            var pc = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                pc.SetDefaultEndpoint(id, ERole.Console);
                pc.SetDefaultEndpoint(id, ERole.Multimedia);
                pc.SetDefaultEndpoint(id, ERole.Communications);
            }
            finally { Marshal.ReleaseComObject(pc); }
        }
    }
}
