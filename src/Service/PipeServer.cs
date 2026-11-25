using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace HPControl.Service
{
    /// <summary>Named-pipe server. UI clients do request/response; the tray agent keeps one duplex connection open.</summary>
    public sealed class PipeServer
    {
        readonly Engine _e;
        volatile bool _stop;
        PipeChannel _agent;
        readonly object _agentLock = new object();
        Thread _listener;

        public PipeServer(Engine e)
        {
            _e = e;
            _e.AgentConnected = () => { lock (_agentLock) return _agent != null; };
            _e.SendToAgent = msg => { PipeChannel a; lock (_agentLock) a = _agent; try { a?.Send(msg); } catch { } };
        }

        public void Start()
        {
            _listener = new Thread(Listen) { IsBackground = true, Name = "pipe-listen" };
            _listener.Start();
        }

        public void Stop()
        {
            _stop = true;
            // poke the listener so WaitForConnection returns
            try { using (var c = new NamedPipeClientStream(".", AppInfo.PipeName)) c.Connect(200); } catch { }
        }

        static PipeSecurity Security()
        {
            var ps = new PipeSecurity();
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            return ps;
        }

        void Listen()
        {
            while (!_stop)
            {
                NamedPipeServerStream srv = null;
                try
                {
                    srv = new NamedPipeServerStream(AppInfo.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 8192, 8192, Security());
                    srv.WaitForConnection();
                    if (_stop) { srv.Dispose(); break; }
                    var s = srv;
                    new Thread(() => Serve(s)) { IsBackground = true, Name = "pipe-client" }.Start();
                }
                catch (Exception ex)
                {
                    Log.Write("pipe listen error: " + ex.Message);
                    try { srv?.Dispose(); } catch { }
                    Thread.Sleep(1000);
                }
            }
        }

        void Serve(NamedPipeServerStream s)
        {
            var ch = new PipeChannel(s);
            bool isAgent = false;
            try
            {
                while (!_stop && s.IsConnected)
                {
                    var req = ch.Receive();
                    if (req == null) break;
                    string cmd = req.Str("cmd", "");
                    var args = req.Obj("args");
                    req.TryGetValue("id", out var id);

                    if (cmd == "agentHello")
                    {
                        lock (_agentLock) { try { _agent?.Dispose(); } catch { } _agent = ch; }
                        isAgent = true;
                        Log.Event("Tray helper connected");
                        ch.Send(new Dictionary<string, object> { { "cmd", "config" }, { "args", AgentConfig() } });
                        _e.Kick();
                        continue;
                    }
                    if (cmd == "noiseResult") continue;   // old tray helper during an upgrade
                    if (cmd == "context") { UpdateContext(args); continue; }

                    object result = null; string error = null;
                    try { result = Dispatch(cmd, args); }
                    catch (Exception ex) { error = ex.Message; }
                    if (id != null)
                        ch.Send(new Dictionary<string, object> { { "id", id }, { "ok", error == null }, { "result", result }, { "error", error } });
                }
            }
            catch (IOException) { }
            catch (Exception ex) { Log.Write("pipe client error: " + ex.Message); }
            finally
            {
                if (isAgent) lock (_agentLock) { if (_agent == ch) { _agent = null; Log.Event("Tray helper disconnected"); } }
                ch.Dispose();
            }
        }

        const string FanAlwaysOn = "Fan Always On";

        public Dictionary<string, object> AgentConfig()
        {
            var g = _e.S.General;
            return new Dictionary<string, object> { { "hotkeyEnabled", g.HotkeyEnabled }, { "hotkey", g.Hotkey }, { "notifications", g.Notifications }, { "trayIcon", g.TrayIcon } };
        }

        public void PushAgentConfig() => _e.SendToAgent(new Dictionary<string, object> { { "cmd", "config" }, { "args", AgentConfig() } });

        void UpdateContext(Dictionary<string, object> a)
        {
            if (a == null) return;
            lock (_e.Lock)
            {
                var c = _e.Ctx;
                c.ForegroundApp = a.Str("app", c.ForegroundApp);
                c.GameInFront = a.Bool("game", false);
                c.Fullscreen = a.Bool("fullscreen", c.Fullscreen);
                c.Tablet = a.Bool("tablet", c.Tablet);
                c.IdleSec = (int)a.Num("idleSec", c.IdleSec);
                c.MicInUse = a.Bool("mic", c.MicInUse);
                c.CamInUse = a.Bool("cam", c.CamInUse);
                c.Updated = DateTime.UtcNow;
            }
            _e.Kick();
        }

        object Dispatch(string cmd, Dictionary<string, object> a)
        {
            switch (cmd)
            {
                case "ping": return "pong";
                case "getState":
                    // the app just opened (or was restored): tick now so it gets the full sensor set straight away
                    bool wasIdle = (DateTime.UtcNow - _e.LastUiPoll).TotalSeconds >= 5;
                    _e.LastUiPoll = DateTime.UtcNow;
                    if (wasIdle) _e.Kick();
                    lock (_e.Lock) return new Dictionary<string, object> { { "tel", _e.Tel }, { "events", Log.Recent((int)a.Num("events", 15)) } };
                case "getSettings":
                    lock (_e.Lock) return Json.Parse(Json.Serialize(_e.S));
                case "setSettings":
                    {
                        var s = new Settings();
                        Json.Populate(s, a.Obj("settings"));
                        if (s.Profiles == null || s.Profiles.Count == 0) throw new InvalidOperationException("settings must contain at least one profile");
                        var def = Settings.CreateDefault();
                        s.ModeCodes = def.ModeCodes;   // only the verified firmware codes are ever written
                        // an imported file or restored backup is used exactly as it is: mark it current so the
                        // upgrade steps don't run on it again at the next start (missing sections get defaults)
                        if (s.Version != Settings.CurrentVersion) { Log.Event("Settings imported from v" + s.Version + (string.IsNullOrEmpty(s.SavedBy) ? "" : " (HP Control " + s.SavedBy + ")")); s.Version = Settings.CurrentVersion; }
                        if (s.General == null) s.General = def.General;
                        if (s.Battery == null) s.Battery = def.Battery;
                        if (s.FanStop == null) s.FanStop = def.FanStop;
                        if (s.PowerLimit == null) s.PowerLimit = def.PowerLimit;
                        if (s.Rules == null) s.Rules = def.Rules;
                        _e.UpdateSettings(s);
                        PushAgentConfig();
                        return true;
                    }
                case "setManualMode":
                    lock (_e.Lock) { _e.S.ControlMode = "Manual"; _e.S.ManualHpMode = a.Str("mode", "SmartSense"); Store.Save(_e.S); }
                    Log.Event("App: manual mode " + a.Str("mode", ""));
                    _e.Kick(true); return true;
                case "setControlMode":
                    lock (_e.Lock) { _e.S.ControlMode = a.Str("mode", "Manual") == "Auto" ? "Auto" : "Manual"; Store.Save(_e.S); }
                    Log.Event("App: control mode " + a.Str("mode", ""));
                    _e.Kick(true); return true;
                case "setProfile":
                    lock (_e.Lock)
                    {
                        string name = a.Str("name");
                        if (_e.S.FindProfile(name) == null) throw new InvalidOperationException("unknown profile");
                        string which = a.Str("which", "both");
                        if (which != "dc") _e.S.ProfileAC = name;
                        if (which != "ac") _e.S.ProfileDC = name;
                        _e.S.ControlMode = "Auto";
                        Store.Save(_e.S);
                    }
                    Log.Event("App: profile " + a.Str("name") + " (" + a.Str("which", "both") + ")");
                    _e.Kick(true); return true;
                case "cycleProfile": _e.CycleProfile(a.Num("dir", 1) >= 0 ? 1 : -1); return true;
                case "getHistory": return _e.History((int)a.Num("minutes", 10));
                case "getBattery": return _e.Battery.GetInfo();
                case "setBatteryHealth": return _e.Battery.SetHealthMode(a.Bool("on"));
                case "setAdaptiveExtender": return _e.Battery.SetAdaptiveExtender(a.Bool("on"));
                case "topUp": return _e.Battery.StartTopUp();
                case "cancelTopUp": return _e.Battery.CancelTopUp();
                case "getFan":
                    {
                        var v = HpBios.GetBiosSetting(FanAlwaysOn, out var pv);
                        return new Dictionary<string, object> { { "alwaysOnSupported", v != null }, { "alwaysOn", v != null && v.Equals("Enable", StringComparison.OrdinalIgnoreCase) } };
                    }
                case "setFanAlwaysOn":
                    {
                        bool on = a.Bool("on");
                        if (HpBios.GetBiosSetting(FanAlwaysOn, out _) == null) throw new InvalidOperationException("This BIOS has no Fan Always On setting");
                        int rc = HpBios.SetBiosSetting(FanAlwaysOn, on ? "Enable" : "Disable");
                        Log.Event("Fan always on " + (on ? "enabled" : "disabled") + (rc == 0 ? "" : " failed (BIOS code " + rc + ")"));
                        if (rc != 0) throw new InvalidOperationException("BIOS refused the change (code " + rc + ")");
                        var now = HpBios.GetBiosSetting(FanAlwaysOn, out _);
                        return new Dictionary<string, object> { { "alwaysOn", now != null && now.Equals("Enable", StringComparison.OrdinalIgnoreCase) } };
                    }
                case "restorePower": lock (_e.Lock) _e.Limiter.RestoreAll(); return true;
                case "resetSettings":
                    _e.UpdateSettings(Settings.CreateDefault()); PushAgentConfig(); return true;
            }
            throw new InvalidOperationException("unknown command " + cmd);
        }
    }
}
