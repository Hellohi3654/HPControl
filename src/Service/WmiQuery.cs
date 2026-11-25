using System;
using System.Collections.Generic;
using System.Management;

namespace HPControl.Service
{
    /// <summary>
    /// Small WMI helper for the service. Keeps one connected scope per namespace (connecting is the slow part)
    /// and always disposes the result collection and every object in it: leaving them to the finalizer
    /// leaks COM enumerators in WmiPrvSE over days of polling.
    /// </summary>
    static class WmiQuery
    {
        static readonly Dictionary<string, ManagementScope> _scopes = new Dictionary<string, ManagementScope>();
        static readonly object _lock = new object();

        static ManagementScope Scope(string ns)
        {
            lock (_lock)
            {
                if (!_scopes.TryGetValue(ns, out var s) || !s.IsConnected)
                {
                    s = new ManagementScope(@"\\.\" + ns);
                    s.Connect();
                    _scopes[ns] = s;
                }
                return s;
            }
        }

        /// <summary>Runs the query and hands the first result to <paramref name="read"/>; returns <paramref name="def"/> when there is none or on error.</summary>
        public static T First<T>(string ns, string query, Func<ManagementObject, T> read, T def)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(Scope(ns), new ObjectQuery(query)))
                using (var results = searcher.Get())
                {
                    T value = def; bool got = false;
                    foreach (ManagementObject o in results)
                        using (o) { if (!got) { value = read(o); got = true; } }
                    return value;
                }
            }
            catch
            {
                lock (_lock) _scopes.Remove(ns);   // reconnect next time (e.g. after the WMI service restarted)
                return def;
            }
        }
    }
}
