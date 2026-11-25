using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HPControl.Service
{
    public static class Log
    {
        static readonly object _lock = new object();
        static readonly LinkedList<LogEntry> _ring = new LinkedList<LogEntry>();
        static string _path;
        public static bool Console;

        static int _writes;

        public static void Init()
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            _path = Path.Combine(AppInfo.DataDir, "service.log");
            Rotate();
        }

        /// <summary>Keeps service.log under ~512 KB (one old copy kept as service.log.1). Called at start and every 200 writes,
        /// because the service can run for weeks without restarting.</summary>
        static void Rotate()
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > 512 * 1024)
                {
                    var old = _path + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(_path, old);
                }
            }
            catch { }
        }

        /// <summary>Writes to the log file only.</summary>
        public static void Write(string s)
        {
            lock (_lock)
            {
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s;
                if (Console) System.Console.WriteLine(line);
                if (_path == null) return;
                if (++_writes % 200 == 0) Rotate();
                try { File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8); } catch { }
            }
        }

        /// <summary>Writes to the log file and the event list shown in the app.</summary>
        public static void Event(string s)
        {
            Write(s);
            lock (_lock)
            {
                _ring.AddLast(new LogEntry { Time = DateTime.Now, Text = s });
                while (_ring.Count > 200) _ring.RemoveFirst();
            }
        }

        public static List<LogEntry> Recent(int n)
        {
            lock (_lock)
            {
                var l = new List<LogEntry>(_ring);
                if (l.Count > n) l = l.GetRange(l.Count - n, n);
                return l;
            }
        }
    }
}
