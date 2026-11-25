using System;
using System.ServiceProcess;
using System.Threading;

namespace HPControl.Service
{
    public sealed class HPControlService : ServiceBase
    {
        Engine _engine;
        PipeServer _pipe;

        public HPControlService()
        {
            ServiceName = AppInfo.ServiceName;
            CanStop = true;
            CanShutdown = true;
            CanHandlePowerEvent = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            Log.Init();
            _engine = new Engine();
            _engine.Start();
            _pipe = new PipeServer(_engine);
            _pipe.Start();
        }

        protected override void OnStop()
        {
            try { _pipe?.Stop(); } catch { }
            try { _engine?.Stop(); } catch { }
            _engine?.Dispose();
        }

        protected override void OnShutdown() => OnStop();

        protected override bool OnPowerEvent(PowerBroadcastStatus s)
        {
            if (s == PowerBroadcastStatus.Suspend) _engine?.OnSuspend();
            // re-assert mode after resume, and react immediately to AC plug/unplug
            if (s == PowerBroadcastStatus.ResumeAutomatic || s == PowerBroadcastStatus.ResumeSuspend || s == PowerBroadcastStatus.PowerStatusChange)
                _engine?.Kick(s != PowerBroadcastStatus.PowerStatusChange);
            return true;
        }
    }

    static class Program
    {
        static void Main(string[] args)
        {
            // The service sleeps almost all the time, so a higher priority costs nothing on battery,
            // but it keeps fan control and the app responsive when something is loading every core.
            try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal; } catch { }
            if (args.Length > 0 && args[0] == "--console")
            {
                Log.Console = true;
                Log.Init();
                var e = new Engine();
                e.Start();
                var p = new PipeServer(e);
                p.Start();
                Console.WriteLine("HP Control service running in console mode. Press Enter to stop.");
                Console.ReadLine();
                p.Stop();
                e.Stop();
                return;
            }
            ServiceBase.Run(new HPControlService());
        }
    }
}
