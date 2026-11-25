using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace HPControl
{
    public static class AppInfo
    {
        public const string Name = "HP Control";
        public const string Version = "1.8.1";
        public const string PipeName = "HPControl.v1";
        public const string ServiceName = "HPControlSvc";
        public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HPControl");
    }

    /// <summary>Snapshot of everything the UI shows. Sent by the service.</summary>
    public class Telemetry
    {
        public DateTime Time;
        public Dictionary<string, double> Temps = new Dictionary<string, double>();
        public double ControlTemp;
        public double SmoothedTemp;
        public double CpuPct;
        public bool OnAC;
        public int BatteryPct;
        public double PowerW;
        public bool Charging;
        public string HpMode = "";
        public int HpModeCode = -1;
        public string TargetMode = "";
        public string ControlMode = "";
        public string ActiveProfile = "";
        public string ActiveRule = "";
        public int PerfBudget = 100;
        public int FanRpm = -1;          // -1 = no fan sensor
        public int FanTargetRpm = -1;    // what the embedded controller is aiming for
        public int FanLimitRpm;          // active RPM limit (0 = none)
        public double PackageW = -1;     // CPU package power (CPU + graphics), -1 = unknown
        public double LoadPct;           // smoothed load used for the load boost (max of CPU % and chip power)
        public string FanStop = "";      // fan stop state, e.g. "Cool · fan off in 5 s"
        public string FanStopInfo = "";  // what fan stop has learned about this laptop
        public bool FanHeld;             // HP Control is driving the fan
        public int FanCommandRpm = -1;   // what HP Control's fan curve is asking for (-1 = fan controller in charge)
        public bool AgentConnected;
        public string Status = "";
        public string Context = "";
        public bool BiosOk;
        public string LastError = "";
    }

    public class LogEntry
    {
        public DateTime Time;
        public string Text;
    }

    /// <summary>Line-delimited JSON over a named pipe.</summary>
    public class PipeChannel : IDisposable
    {
        readonly Stream _s;
        readonly StreamReader _r;
        readonly StreamWriter _w;
        readonly object _wlock = new object();

        public PipeChannel(Stream s)
        {
            _s = s;
            _r = new StreamReader(s, new UTF8Encoding(false), false, 8192, true);
            _w = new StreamWriter(s, new UTF8Encoding(false), 8192, true) { AutoFlush = true, NewLine = "\n" };
        }

        public void Send(object msg)
        {
            var line = msg is string str ? str : Json.Serialize(msg);
            lock (_wlock) { _w.WriteLine(line); }
        }

        public Dictionary<string, object> Receive()
        {
            var line = _r.ReadLine();
            if (line == null) return null;
            return Json.Parse(line) as Dictionary<string, object>;
        }

        /// <summary>Receive with a timeout; throws TimeoutException (the channel is then unusable).</summary>
        public Dictionary<string, object> Receive(int timeoutMs)
        {
            var t = _r.ReadLineAsync();
            if (!t.Wait(timeoutMs)) throw new TimeoutException("HP Control service did not answer");
            var line = t.Result;
            if (line == null) return null;
            return Json.Parse(line) as Dictionary<string, object>;
        }

        public void Dispose()
        {
            try { _w.Dispose(); } catch { }
            try { _r.Dispose(); } catch { }
            try { _s.Dispose(); } catch { }
        }
    }

    /// <summary>Request/response client used by the UI.</summary>
    public class ServiceClient : IDisposable
    {
        NamedPipeClientStream _pipe;
        PipeChannel _ch;
        int _id;
        readonly object _lock = new object();

        public bool Connected => _pipe != null && _pipe.IsConnected;

        public bool TryConnect(int timeoutMs = 600)
        {
            lock (_lock)
            {
                if (Connected) return true;
                Close();
                try
                {
                    _pipe = new NamedPipeClientStream(".", AppInfo.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    _pipe.Connect(timeoutMs);
                    _ch = new PipeChannel(_pipe);
                    return true;
                }
                catch { Close(); return false; }
            }
        }

        public object Call(string cmd, object args = null)
        {
            lock (_lock)
            {
                if (!Connected && !TryConnect()) throw new IOException("HP Control service is not running");
                int id = ++_id;
                try
                {
                    _ch.Send(new Dictionary<string, object> { { "id", id }, { "cmd", cmd }, { "args", args } });
                    while (true)
                    {
                        Dictionary<string, object> resp;
                        try { resp = _ch.Receive(cmd == "getBattery" || cmd == "setFanAlwaysOn" ? 15000 : 6000); }
                        catch (Exception ex) when (ex is TimeoutException || ex is AggregateException) { throw new IOException("HP Control service did not answer in time", ex); }
                        if (resp == null) throw new IOException("Service closed the connection");
                        if ((int)resp.Num("id") != id) continue;
                        if (!resp.Bool("ok")) throw new InvalidOperationException(resp.Str("error", "error"));
                        resp.TryGetValue("result", out var r);
                        return r;
                    }
                }
                catch (IOException) { Close(); throw; }
            }
        }

        void Close()
        {
            try { _ch?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            _ch = null; _pipe = null;
        }

        public void Dispose() { lock (_lock) Close(); }
    }

    public static class Fmt
    {
        public static string Temp(double c) => double.IsNaN(c) ? "–" : c.ToString("0.0") + " °C";
        public static string Db(double d) => double.IsNaN(d) ? "–" : d.ToString("0.0") + " dBA";
    }
}
