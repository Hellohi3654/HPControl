using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace HPControl.App
{
    /// <summary>
    /// Talks to HP's own audio-effect components (DTS APO 4.x for presets/EQ, Fortemedia for mic noise
    /// reduction / mic mode) by loading the WinRT DLLs shipped in the HP app package. Runs in a short-lived
    /// child process ("HPControl.exe --audiofx ...") so a vendor DLL problem can never take the UI down.
    /// </summary>
    static class AudioFxHost
    {
        // ------------------------------------------------------------------ client side (UI)
        public static Dictionary<string, object> Call(params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(System.Windows.Forms.Application.ExecutablePath, "--audiofx " + string.Join(" ", args.Select(a => "\"" + a + "\"")))
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
                using (var p = Process.Start(psi))
                {
                    // read in the background so a stuck helper can't hang this thread: give up (and end it) after 15 s
                    var read = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(15000) || !read.Wait(2000)) { try { p.Kill(); } catch { } return new Dictionary<string, object> { { "error", "timeout" } }; }
                    var outp = read.Result;
                    var line = outp.Split('\n').LastOrDefault(l => l.TrimStart().StartsWith("{"));
                    return line == null ? new Dictionary<string, object> { { "error", "no output (exit " + p.ExitCode + ")" } } : (Dictionary<string, object>)Json.Parse(line);
                }
            }
            catch (Exception ex) { return new Dictionary<string, object> { { "error", ex.Message } }; }
        }

        const int DtsModeOff = 7;

        /// <summary>DTS mode, EQ on/off and bands as the driver reports them (so the UI always shows the real state).</summary>
        static void DtsState(IntPtr d, Dictionary<string, object> res)
        {
            Check(Slot<F_OutInt>(d, DTS_GetMode)(d, out int mode), "GetOperatingMode");
            res["dtsMode"] = mode;
            Check(Slot<F_IntOutInt>(d, DTS_GetControl)(d, GeqEnable, out int en), "geq");
            res["geqEnable"] = en;
            var bands = new List<object>();
            for (int i = 0; i < 10; i++) { Slot<F_IntOutInt>(d, DTS_GetControl)(d, GeqBand0 + i, out int v); bands.Add(v); }
            res["geq"] = bands;
            Slot<F_IntOutInt>(d, DTS_GetMin)(d, GeqBand0, out int mn); Slot<F_IntOutInt>(d, DTS_GetMax)(d, GeqBand0, out int mx);
            res["geqMin"] = mn; res["geqMax"] = mx;
            res["dts"] = true;
        }

        // ------------------------------------------------------------------ native plumbing
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr[] fullNames, ref uint bufLen, char[] buf);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetPackagePathByFullName(string fullName, ref uint len, StringBuilder path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string dir);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string path, IntPtr h, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr AddDllDirectory(string dir);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr mod, string name);
        [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string s, int len, out IntPtr hs);
        [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hs);
        [DllImport("combase.dll")] static extern IntPtr WindowsGetStringRawBuffer(IntPtr hs, out uint len);
        [DllImport("combase.dll")] static extern int RoInitialize(int type);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DllGetActivationFactoryFn(IntPtr classId, out IntPtr factory);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ActivateInstanceFn(IntPtr self, out IntPtr inst);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr ppv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int F_OutBool(IntPtr self, out byte r);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int F_InBool(IntPtr self, byte v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int F_InInt(IntPtr self, int v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int F_OutInt(IntPtr self, out int r);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int F_IntInt(IntPtr self, int a, int b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int F_IntOutInt(IntPtr self, int a, out int r);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int F_StrOutBool(IntPtr self, IntPtr s, out byte r);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int F_OutStr(IntPtr self, out IntPtr hs);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int F_3OutBool(IntPtr self, out byte a, out byte b, out byte c, out byte r);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int F_BufOutBool(IntPtr self, StringBuilder buf, int size, out byte r);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int F_OutPtr(IntPtr self, out IntPtr r);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int F_Void(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int F_GetIids(IntPtr self, out uint count, out IntPtr iids);

        static T Slot<T>(IntPtr obj, int slot) where T : class
        {
            var vtbl = Marshal.ReadIntPtr(obj);
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size), typeof(T)) as T;
        }

        static string PackagePath(string family, string archHint = null)
        {
            uint count = 0, len = 0;
            GetPackagesByPackageFamily(family, ref count, null, ref len, null);
            if (count == 0) return null;
            var names = new IntPtr[count]; var buf = new char[len];
            if (GetPackagesByPackageFamily(family, ref count, names, ref len, buf) != 0) return null;
            var fulls = new List<string>();
            var s = new string(buf);
            foreach (var part in s.Split('\0')) if (part.Length > 0) fulls.Add(part);
            var pick = fulls.Where(f => archHint == null || f.Contains(archHint)).OrderByDescending(f => f).FirstOrDefault() ?? fulls.FirstOrDefault();
            if (pick == null) return null;
            uint pl = 0; GetPackagePathByFullName(pick, ref pl, null);
            var sb = new StringBuilder((int)pl + 1);
            return GetPackagePathByFullName(pick, ref pl, sb) == 0 ? sb.ToString() : null;
        }

        static string _hpDir;
        static string FxDir => Path.Combine(Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath), "fx");

        /// <summary>Prefers the copy the installer placed in .\fx (bundled with its runtime DLLs), else the HP app's package folder.</summary>
        static string FindComponent(string dll)
        {
            var local = Path.Combine(FxDir, dll);
            if (File.Exists(local)) return local;
            if (_hpDir == null) _hpDir = PackagePath("AD2F1837.myHP_v10z8vjag6ke6", "_x64_") ?? "";
            var p = Path.Combine(_hpDir, dll);
            return _hpDir.Length > 0 && File.Exists(p) ? p : null;
        }

        static IntPtr Activate(string dll, string classId, Guid iid)
        {
            var factory = GetFactory(dll, classId);
            int hr = Slot<ActivateInstanceFn>(factory, 6)(factory, out var inst);
            Marshal.Release(factory);
            if (hr != 0) throw new InvalidOperationException("ActivateInstance 0x" + hr.ToString("X"));
            var g = iid;
            hr = Slot<QueryInterfaceFn>(inst, 0)(inst, ref g, out var itf);
            Marshal.Release(inst);
            if (hr != 0) throw new InvalidOperationException("QueryInterface 0x" + hr.ToString("X"));
            return itf;
        }

        static IntPtr GetFactory(string dll, string classId)
        {
            var path = FindComponent(dll) ?? throw new FileNotFoundException(dll + " not found (HP app not installed?)");
            var vclibs = PackagePath("Microsoft.VCLibs.140.00_8wekyb3d8bbwe", "_x64_");
            // LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRS
            if (Directory.Exists(FxDir)) AddDllDirectory(FxDir);
            if (vclibs != null) AddDllDirectory(vclibs);
            var mod = LoadLibraryEx(path, IntPtr.Zero, 0x100 | 0x1000 | 0x400);
            if (mod == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (vclibs != null) SetDllDirectory(vclibs);
                mod = LoadLibraryEx(path, IntPtr.Zero, 0x8);
                if (mod == IntPtr.Zero) throw new InvalidOperationException("LoadLibrary failed (" + err + "/" + Marshal.GetLastWin32Error() + ") for " + path + " vclibs=" + (vclibs ?? "not found"));
            }
            var f = GetProcAddress(mod, "DllGetActivationFactory");
            var get = (DllGetActivationFactoryFn)Marshal.GetDelegateForFunctionPointer(f, typeof(DllGetActivationFactoryFn));
            WindowsCreateString(classId, classId.Length, out var hs);
            int hr = get(hs, out var factory);
            WindowsDeleteString(hs);
            if (hr != 0) throw new InvalidOperationException("activation factory 0x" + hr.ToString("X"));
            return factory;
        }

        static IntPtr QI(IntPtr p, Guid iid, string what)
        {
            var g = iid;
            int hr = Slot<QueryInterfaceFn>(p, 0)(p, ref g, out var r);
            if (hr != 0) throw new InvalidOperationException(what + ": QueryInterface 0x" + hr.ToString("X"));
            return r;
        }

        /// <summary>Calls a getter that returns a runtime-class object and casts it to the wanted interface.</summary>
        static IntPtr GetObj(IntPtr o, int slot, Guid iid, string what)
        {
            Check(Slot<F_OutPtr>(o, slot)(o, out var p), what);
            if (p == IntPtr.Zero) throw new InvalidOperationException(what + " is not available");
            var r = QI(p, iid, what);
            Marshal.Release(p);
            return r;
        }
        static int GetI(IntPtr o, int slot) { Check(Slot<F_OutInt>(o, slot)(o, out int v), "get" + slot); return v; }

        // ------------------------------------------------------------------ Realtek (what this laptop's mic and speakers actually use)
        // RtkAudioCore.winmd: RAudioCore.Current -> MMPlatform -> internal mic / speaker device -> BSS capture / render effect
        static readonly Guid IID_RtkStatics = new Guid("572d6781-74ff-3fbf-9876-72d60b43d24c"), IID_RtkCore = new Guid("b9ce05c0-3085-3108-b65b-54ce55b9e21d"),
            IID_RtkPlat = new Guid("621392e3-18bd-33bc-b9cc-0588e58f3745"), IID_RtkDev = new Guid("351e421a-e0e9-385d-967e-5aaa53aceee1"),
            IID_RtkBss = new Guid("16973969-a802-33e7-8ad8-ba436c3751ba"), IID_RtkRender = new Guid("924a900a-7ee9-35c8-a61f-1cc701ce308c");
        // RMMPlatform
        const int RP_RenderEnumDone = 42, RP_CaptureEnumDone = 44, RP_InternalRender = 39, RP_InternalOrDefaultRender = 40, RP_InternalCapture = 41, RP_DefaultCapture = 38,
                  RP_GlobalRenderAinr = 65;
        // RAudioDevice
        const int RD_Name = 10, RD_RenderEffect = 51, RD_CaptureBss = 55;
        // RRtkCaptureBssApoEffect
        const int RB_IsIntMic = 22, RB_IsSupport = 24, RB_UniAinrVisible = 44, RB_UniAinr = 45, RB_OmniAinrVisible = 47, RB_OmniAinr = 48, RB_BothAinrVisible = 51, RB_BothAinr = 52,
                  RB_HighQualityVisible = 41, RB_AdaptiveSupport = 63, RB_Adaptive = 64, RB_MultiVoiceVisible = 74, RB_MultiVoiceEnabled = 75, RB_MultiVoice = 76,
                  RB_Conference = 78, RB_Personal = 80, RB_StudioRec = 82, RB_PersonalCall = 90, RB_HpNrMode = 91, RB_TseEnroll = 93;
        // RRtkRenderApoEffect
        const int RR_RenderNsVisible = 102, RR_RenderNs = 105, RR_AinrVisible = 109, RR_Ainr = 110;

        sealed class RtkSession { public IntPtr Plat, Mic, Bss, Spk, Render; }

        static RtkSession OpenRtk(Dictionary<string, object> diag)
        {
            var st = GetFactory("RtkAudioCore.dll", "RtkAudioCore.RAudioCore");
            st = QI(st, IID_RtkStatics, "RAudioCore statics");
            try { Slot<F_Void>(st, 9)(st); } catch (Exception ex) { if (diag != null) diag["rtkInit"] = ex.Message; }
            byte rpc = 0; try { Slot<F_OutBool>(st, 10)(st, out rpc); } catch { }
            if (diag != null) diag["rtkRpc"] = rpc;
            var r = new RtkSession();
            // RMMPlatform.Current from its own statics (RAudioCore.Init above sets everything up)
            var ps = QI(GetFactory("RtkAudioCore.dll", "RtkAudioCore.WASAPI.RMMPlatform"), new Guid("673d12e1-779d-306a-87d8-08a3229a81bd"), "RMMPlatform statics");
            Check(Slot<F_OutPtr>(ps, 6)(ps, out var platObj), "RMMPlatform.Current");
            if (platObj == IntPtr.Zero) throw new InvalidOperationException("RMMPlatform.Current is null");
            if (diag != null) diag["rtkPlatClass"] = ClassName(platObj);
            r.Plat = QI(platObj, IID_RtkPlat, "RMMPlatform");
            // device lists fill in asynchronously
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until && !(TryB(r.Plat, RP_RenderEnumDone) && TryB(r.Plat, RP_CaptureEnumDone)))
            {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(50);
            }
            try { r.Mic = GetObj(r.Plat, RP_InternalCapture, IID_RtkDev, "internal mic"); } catch { try { r.Mic = GetObj(r.Plat, RP_DefaultCapture, IID_RtkDev, "default mic"); } catch (Exception ex) { if (diag != null) diag["rtkMicError"] = ex.Message; } }
            try { r.Spk = GetObj(r.Plat, RP_InternalRender, IID_RtkDev, "internal speaker"); } catch { try { r.Spk = GetObj(r.Plat, RP_InternalOrDefaultRender, IID_RtkDev, "speaker"); } catch (Exception ex) { if (diag != null) diag["rtkSpkError"] = ex.Message; } }
            if (r.Mic != IntPtr.Zero) try { r.Bss = GetObj(r.Mic, RD_CaptureBss, IID_RtkBss, "mic effects"); } catch (Exception ex) { if (diag != null) diag["rtkBssError"] = ex.Message; }
            if (r.Spk != IntPtr.Zero) try { r.Render = GetObj(r.Spk, RD_RenderEffect, IID_RtkRender, "speaker effects"); } catch (Exception ex) { if (diag != null) diag["rtkRenderError"] = ex.Message; }
            if (diag != null)
            {
                try { if (r.Mic != IntPtr.Zero) { Check(Slot<F_OutStr>(r.Mic, RD_Name)(r.Mic, out var hs), "name"); diag["rtkMic"] = HStr(hs); } } catch { }
                try { if (r.Spk != IntPtr.Zero) { Check(Slot<F_OutStr>(r.Spk, RD_Name)(r.Spk, out var hs), "name"); diag["rtkSpk"] = HStr(hs); } } catch { }
            }
            return r;
        }

        static void RtkStatus(Dictionary<string, object> res)
        {
            var r = OpenRtk(res);
            res["rtk"] = true;
            if (r.Bss != IntPtr.Zero)
            {
                var b = r.Bss;
                bool uniV = TryB(b, RB_UniAinrVisible), omniV = TryB(b, RB_OmniAinrVisible), bothV = TryB(b, RB_BothAinrVisible);
                res["rtkMicSupport"] = TryB(b, RB_IsSupport);
                res["rtkModeVisible"] = TryB(b, RB_MultiVoiceVisible) || TryB(b, RB_MultiVoiceEnabled);
                res["rtkPersonal"] = TryB(b, RB_Personal);
                res["rtkConference"] = TryB(b, RB_Conference);
                res["rtkStudio"] = TryB(b, RB_StudioRec);
                res["rtkStudioVisible"] = TryB(b, RB_HighQualityVisible);
                res["rtkMicNrVisible"] = uniV || omniV || bothV;
                res["rtkMicNr"] = bothV ? TryB(b, RB_BothAinr) : (TryB(b, RB_Conference) ? TryB(b, RB_OmniAinr) : TryB(b, RB_UniAinr));
                res["rtkRaw"] = string.Join(",", new[] { RB_IsIntMic, RB_IsSupport, RB_UniAinrVisible, RB_UniAinr, RB_OmniAinrVisible, RB_OmniAinr, RB_BothAinrVisible, RB_BothAinr,
                    RB_HighQualityVisible, RB_AdaptiveSupport, RB_Adaptive, RB_MultiVoiceVisible, RB_MultiVoiceEnabled, RB_MultiVoice, RB_Conference, RB_Personal, RB_StudioRec, RB_PersonalCall, RB_TseEnroll }
                    .Select(sl => sl + "=" + (TryB(b, sl) ? 1 : 0))) + ",hpNr=" + SafeI(b, RB_HpNrMode);
            }
            if (r.Render != IntPtr.Zero)
            {
                res["rtkSpkNrVisible"] = TryB(r.Render, RR_AinrVisible) || TryB(r.Render, RR_RenderNsVisible);
                res["rtkSpkNr"] = TryB(r.Render, RR_AinrVisible) ? TryB(r.Render, RR_Ainr) : TryB(r.Render, RR_RenderNs);
                res["rtkSpkRaw"] = RR_RenderNsVisible + "=" + (TryB(r.Render, RR_RenderNsVisible) ? 1 : 0) + "," + RR_RenderNs + "=" + (TryB(r.Render, RR_RenderNs) ? 1 : 0) + "," +
                    RR_AinrVisible + "=" + (TryB(r.Render, RR_AinrVisible) ? 1 : 0) + "," + RR_Ainr + "=" + (TryB(r.Render, RR_Ainr) ? 1 : 0) + ",global=" + (TryB(r.Plat, RP_GlobalRenderAinr) ? 1 : 0);
            }
        }

        static void RtkSet(string[] a, Dictionary<string, object> res)
        {
            var r = OpenRtk(null);
            bool on = a.Length > 1 && a[1] == "1";
            switch (a[0])
            {
                case "rtk-mode":
                    {
                        if (r.Bss == IntPtr.Zero) throw new InvalidOperationException("mic effects not available");
                        int slot = a[1] == "conference" ? RB_Conference : a[1] == "studio" ? RB_StudioRec : RB_Personal;
                        SetB(r.Bss, slot + 1, true);
                        res["value"] = TryB(r.Bss, slot); break;
                    }
                case "rtk-micnr":
                    {
                        var b = r.Bss; if (b == IntPtr.Zero) throw new InvalidOperationException("mic effects not available");
                        int slot = TryB(b, RB_BothAinrVisible) ? RB_BothAinr : TryB(b, RB_Conference) ? RB_OmniAinr : RB_UniAinr;
                        SetB(b, slot + 1, on);
                        res["value"] = TryB(b, slot); break;
                    }
                case "rtk-spknr":
                    {
                        var e = r.Render; if (e == IntPtr.Zero) throw new InvalidOperationException("speaker effects not available");
                        int slot = TryB(e, RR_AinrVisible) ? RR_Ainr : RR_RenderNs;
                        SetB(e, slot + 1, on);
                        res["value"] = TryB(e, slot); break;
                    }
            }
            res["ok"] = true;
        }

        static string ClassName(IntPtr o)
        {
            try { Check(Slot<F_OutStr>(o, 4)(o, out var hs), "GetRuntimeClassName"); return HStr(hs); } catch (Exception ex) { return "? " + ex.Message; }
        }

        static string SafeI(IntPtr o, int slot) { try { return GetI(o, slot).ToString(); } catch { return "?"; } }


        static string HStr(IntPtr hs) { if (hs == IntPtr.Zero) return ""; var p = WindowsGetStringRawBuffer(hs, out uint l); var s = Marshal.PtrToStringUni(p, (int)l); WindowsDeleteString(hs); return s; }

        // ------------------------------------------------------------------ DTS
        static readonly Guid IID_Dts = new Guid("050efd17-16b7-3453-a00c-f0af65530c2f");
        const int DTS_Init = 6, DTS_Connected = 9, DTS_SetControl = 12, DTS_SetControlAll = 13, DTS_SetMode = 17, DTS_GetMax = 20, DTS_GetMin = 21, DTS_GetControl = 24, DTS_GetMode = 28;
        public const int GeqEnable = 18, GeqBand0 = 20;

        static IntPtr Dts()
        {
            var d = Activate("DTSApo4xWinRTComponent.dll", "DTSApo4xWinRTComponent.DtsRpcInterface", IID_Dts);
            Check(Slot<F_OutBool>(d, DTS_Init)(d, out byte ok), "InitializeConnection");
            if (ok == 0) throw new InvalidOperationException("DTS service connection refused");
            return d;
        }

        // ------------------------------------------------------------------ Fortemedia
        static readonly Guid IID_Fm = new Guid("7fb0f483-f959-3e7d-914c-7fa35f75090c");
        // vtable slots of the HP-specific Fortemedia properties (getter; setter = getter + 1)
        // (from FMAPOCTL.winmd: each feature has Enable get/set, then IsVisible, then IsSupport)
        const int FM_Conf = 298, FM_ConfVisible = 300, FM_ConfSupport = 301, FM_Private = 302, FM_PrivateVisible = 304, FM_PrivateSupport = 305,
                  FM_Studio = 306, FM_StudioVisible = 308, FM_StudioSupport = 309, FM_MicAiNs = 310, FM_MicAiNsVisible = 312, FM_MicAiNsSupport = 313,
                  FM_SpkAiNs = 331, FM_SpkAiNsVisible = 333, FM_SpkAiNsSupport = 334,
                  FM_Adv = 248, FM_AdvVisible = 259,           // "Advanced personal mode" in the HP app
                  FM_UpdateSupportProperty = 14, FM_DriverInfo = 330;

        /// <summary>Fortemedia control bound to the built-in mic (or the speakers): init, pick the endpoint,
        /// and refresh its feature flags the way the HP app does before reading them. The HP app is a UWP app,
        /// so it passes the endpoint as a device-interface path (\\?\SWD#MMDEVAPI#{id}#{category}); the driver only
        /// recognises the internal mic / speakers in that form, so try it first and fall back to the bare id.</summary>
        static IntPtr Fm(out string ep, bool speaker = false) => Fm(out ep, speaker, null);

        static IntPtr Fm(out string ep, bool speaker, Dictionary<string, object> diag)
        {
            var f = Activate("FMAPOCTL.dll", "FMAPOCTL.FMAPOControl", IID_Fm);
            Check(Slot<F_OutBool>(f, 6)(f, out byte ok), "init");
            string id = speaker ? RenderEndpoint() : CaptureEndpoint();
            string cat = speaker ? "{e6327cad-dcec-4949-ae8a-991e976a79d2}" : "{2eef81be-33fa-4800-9670-1cd474972c3f}";
            var forms = new[] { @"\\?\SWD#MMDEVAPI#" + id + "#" + cat, id };
            int visibleSlot = speaker ? FM_SpkAiNsVisible : FM_MicAiNsVisible;
            ep = id;
            for (int i = 0; i < forms.Length; i++)
            {
                WindowsCreateString(forms[i], forms[i].Length, out var hs);
                Slot<F_StrOutBool>(f, 8)(f, hs, out byte okEp);                          // initEndpoint
                Slot<F_StrOutBool>(f, FM_UpdateSupportProperty)(f, hs, out byte okUp);   // refresh supported / visible flags
                WindowsDeleteString(hs);
                bool vis = TryB(f, visibleSlot);
                if (diag != null) diag[(speaker ? "spk" : "mic") + "Form" + i] = "init=" + okEp + " update=" + okUp + " visible=" + (vis ? 1 : 0);
                ep = forms[i];
                if (vis) break;
            }
            return f;
        }

        static string RenderEndpoint()
        {
            var all = CoreAudio.List(EDataFlow.Render);
            var spk = all.FirstOrDefault(d => d.Name.IndexOf("Speaker", StringComparison.OrdinalIgnoreCase) >= 0 && d.Name.IndexOf("Realtek", StringComparison.OrdinalIgnoreCase) >= 0)
                      ?? all.FirstOrDefault(d => d.IsDefault);
            return spk?.Id ?? "";
        }

        static bool TryB(IntPtr o, int slot) { try { return GetB(o, slot); } catch { return false; } }

        static string CaptureEndpoint()
        {
            var mic = CoreAudio.List(EDataFlow.Capture).FirstOrDefault(d => d.Name.IndexOf("Microphone Array", StringComparison.OrdinalIgnoreCase) >= 0) ?? CoreAudio.List(EDataFlow.Capture).FirstOrDefault(d => d.IsDefault);
            return mic?.Id ?? "";
        }

        static void Check(int hr, string what) { if (hr != 0) throw new InvalidOperationException(what + " failed 0x" + hr.ToString("X")); }
        static bool GetB(IntPtr o, int slot) { Check(Slot<F_OutBool>(o, slot)(o, out byte r), "get" + slot); return r != 0; }
        static void SetB(IntPtr o, int slot, bool v) => Check(Slot<F_InBool>(o, slot)(o, (byte)(v ? 1 : 0)), "set" + slot);

        // ------------------------------------------------------------------ child-process entry point
        public static int Run(string[] args)
        {
            var res = new Dictionary<string, object>();
            try
            {
                RoInitialize(0);
                var a = args.SkipWhile(x => x != "--audiofx").Skip(1).ToArray();
                string cmd = a.Length > 0 ? a[0] : "status";
                switch (cmd)
                {
                    case "status":
                        try { DtsState(Dts(), res); }
                        catch (Exception ex) { res["dts"] = false; res["dtsError"] = ex.Message; }
                        try
                        {
                            var f = Fm(out string ep, false, res);
                            res["micEndpoint"] = ep;
                            // a feature is usable when the driver says it's supported or the HP app would show it
                            res["micAiNs"] = GetB(f, FM_MicAiNs);
                            res["micAiNsSupported"] = TryB(f, FM_MicAiNsSupport) || TryB(f, FM_MicAiNsVisible);
                            res["private"] = GetB(f, FM_Private);
                            res["privateSupported"] = TryB(f, FM_PrivateSupport) || TryB(f, FM_PrivateVisible);
                            res["conference"] = GetB(f, FM_Conf);
                            res["conferenceSupported"] = TryB(f, FM_ConfSupport) || TryB(f, FM_ConfVisible);
                            res["studio"] = TryB(f, FM_Studio);
                            res["studioSupported"] = TryB(f, FM_StudioSupport) || TryB(f, FM_StudioVisible);
                            res["adv"] = TryB(f, FM_Adv);
                            res["advSupported"] = TryB(f, FM_AdvVisible);
                            res["fmRaw"] = string.Join(",", new[] { FM_MicAiNsSupport, FM_MicAiNsVisible, FM_PrivateSupport, FM_PrivateVisible, FM_ConfSupport, FM_ConfVisible, FM_StudioSupport, FM_StudioVisible, FM_AdvVisible }
                                .Select(sl => sl + "=" + (TryB(f, sl) ? 1 : 0)));
                            try { Check(Slot<F_OutStr>(f, FM_DriverInfo)(f, out IntPtr hs), "info"); res["fmInfo"] = HStr(hs); } catch { }
                            res["fm"] = true;
                        }
                        catch (Exception ex) { res["fm"] = false; res["fmError"] = ex.Message; }
                        try
                        {
                            var g = Fm(out string sep, true, res);
                            res["spkEndpoint"] = sep;
                            res["spkAiNs"] = GetB(g, FM_SpkAiNs);
                            res["spkAiNsSupported"] = TryB(g, FM_SpkAiNsSupport) || TryB(g, FM_SpkAiNsVisible);
                            res["spkRaw"] = FM_SpkAiNsSupport + "=" + (TryB(g, FM_SpkAiNsSupport) ? 1 : 0) + "," + FM_SpkAiNsVisible + "=" + (TryB(g, FM_SpkAiNsVisible) ? 1 : 0);
                        }
                        catch (Exception ex) { res["spkAiNsSupported"] = false; res["spkError"] = ex.Message; }
                        try
                        {
                            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HPControl");
                            System.IO.Directory.CreateDirectory(dir);
                            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "audiofx-status.json"), Json.Serialize(res, true));
                        }
                        catch { }
                        break;
                    case "dts-mode":
                        {
                            // the driver turns the EQ off with DTS processing; a[2] == "1" turns it back on when leaving Off
                            var d = Dts(); int m = int.Parse(a[1]);
                            Check(Slot<F_InInt>(d, DTS_SetMode)(d, m), "SetOperatingMode");
                            if (m != DtsModeOff && a.Length > 2 && a[2] == "1") Slot<F_IntInt>(d, DTS_SetControl)(d, GeqEnable, 1);
                            DtsState(d, res); res["ok"] = true; break;
                        }
                    case "dts-geq":
                        { var d = Dts(); Check(Slot<F_IntInt>(d, DTS_SetControl)(d, GeqEnable, a[1] == "1" ? 1 : 0), "SetControlValue"); DtsState(d, res); res["ok"] = true; break; }
                    case "dts-eq":
                        { var d = Dts(); Check(Slot<F_IntInt>(d, DTS_SetControl)(d, GeqBand0 + int.Parse(a[1]), int.Parse(a[2])), "SetControlValue"); res["ok"] = true; break; }
                    case "dts-eqall":
                        {
                            var d = Dts();
                            var vals = a[1].Split(',').Select(int.Parse).ToArray();
                            Slot<F_IntInt>(d, DTS_SetControl)(d, GeqEnable, 1);
                            for (int i = 0; i < Math.Min(10, vals.Length); i++) Check(Slot<F_IntInt>(d, DTS_SetControl)(d, GeqBand0 + i, vals[i]), "SetControlValue");
                            DtsState(d, res); res["ok"] = true; break;
                        }
                    case "fm-ns":
                        { var f = Fm(out _); SetB(f, FM_MicAiNs + 1, a[1] == "1"); res["value"] = GetB(f, FM_MicAiNs); res["ok"] = true; break; }
                    case "fm-spkns":
                        { var f = Fm(out _, speaker: true); SetB(f, FM_SpkAiNs + 1, a[1] == "1"); res["value"] = GetB(f, FM_SpkAiNs); res["ok"] = true; break; }
                    case "rtk-status":
                        {
                            try { RtkStatus(res); }
                            catch (Exception ex)
                            {
                                res["rtk"] = false; res["rtkError"] = ex.Message;
                                // the Realtek objects may live in the multithreaded apartment: try again from an MTA thread
                                var mta = new Dictionary<string, object>();
                                var th = new System.Threading.Thread(() => { try { RoInitialize(1); RtkStatus(mta); } catch (Exception ex2) { mta["rtk"] = false; mta["rtkError"] = ex2.Message; } });
                                th.SetApartmentState(System.Threading.ApartmentState.MTA); th.Start(); th.Join(10000);
                                if (mta.ContainsKey("rtk") && Convert.ToBoolean(mta["rtk"])) { foreach (var kv in mta) res[kv.Key] = kv.Value; res["rtkMta"] = true; res.Remove("rtkError"); }
                                else res["rtkErrorMta"] = mta.ContainsKey("rtkError") ? mta["rtkError"] : "timeout";
                            }
                            try
                            {
                                var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HPControl");
                                System.IO.Directory.CreateDirectory(dir);
                                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "audiofx-rtk.json"), Json.Serialize(res, true));
                            }
                            catch { }
                            break;
                        }
                    case "rtk-mode":
                    case "rtk-micnr":
                    case "rtk-spknr":
                        {
                            var cmdArgs = a;
                            Action<Dictionary<string, object>> act = rr => RtkSet(cmdArgs, rr);
                            try { act(res); }
                            catch (Exception ex)
                            {
                                var m = new Dictionary<string, object>(); Exception err = null;
                                var th = new System.Threading.Thread(() => { try { RoInitialize(1); act(m); } catch (Exception ex2) { err = ex2; } });
                                th.SetApartmentState(System.Threading.ApartmentState.MTA); th.Start(); th.Join(10000);
                                if (err != null || m.Count == 0) throw new InvalidOperationException(ex.Message + (err != null ? " / " + err.Message : ""));
                                foreach (var kv in m) res[kv.Key] = kv.Value;
                            }
                            break;
                        }
                    case "fm-adv":
                        { var f = Fm(out _); SetB(f, FM_Adv + 1, a[1] == "1"); res["value"] = TryB(f, FM_Adv); res["ok"] = true; break; }
                    case "fm-mode":
                        {
                            // exactly one of the HP mic modes is on, the same as the HP app does it
                            var f = Fm(out _);
                            string m = a[1];
                            if (m != "conference") SetB(f, FM_Conf + 1, false);
                            if (m != "private" && (TryB(f, FM_StudioSupport) || TryB(f, FM_StudioVisible))) SetB(f, FM_Studio + 1, false);
                            if (m != "private") SetB(f, FM_Private + 1, false);
                            if (m == "conference") SetB(f, FM_Conf + 1, true);
                            else if (m == "studio") SetB(f, FM_Studio + 1, true);
                            else SetB(f, FM_Private + 1, true);
                            res["ok"] = true; break;
                        }
                    default: res["error"] = "unknown command"; break;
                }
            }
            catch (Exception ex) { res["error"] = ex.Message; }
            Console.Out.WriteLine(Json.Serialize(res));
            Console.Out.Flush();
            return 0;
        }
    }
}
