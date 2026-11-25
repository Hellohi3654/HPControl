using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace HPControl.App
{
    /// <summary>
    /// Updates from the GitHub repository's latest release. Checking is one small HTTPS request (at most once a day
    /// from the tray helper, or when asked). Installing is always started by the user: the release zip is downloaded,
    /// checked against the SHA-256 written in the release notes, extracted to a fresh folder, and its install.cmd
    /// run, which asks for admin (UAC) like a manual install.
    /// </summary>
    static class Updater
    {
        public sealed class Release
        {
            public Version Version;
            public string Tag = "", ZipName = "", ZipUrl = "", Sha256 = "", Notes = "", PageUrl = "";
            public long Size;
        }

        public static Version Current => Version.TryParse(AppInfo.Version, out var v) ? v : new Version(0, 0);
        static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HPControl");
        static string StateFile => Path.Combine(Dir, "update.txt");

        static Updater() { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }

        // ------------------------------------------------------------ state (last check, last version offered)
        public static DateTime LastCheck => ReadState().Item1;
        public static string LastOffered => ReadState().Item2;

        static Tuple<DateTime, string> ReadState()
        {
            try
            {
                var l = File.ReadAllLines(StateFile);
                return Tuple.Create(DateTime.Parse(l[0], null, System.Globalization.DateTimeStyles.RoundtripKind), l.Length > 1 ? l[1] : "");
            }
            catch { return Tuple.Create(DateTime.MinValue, ""); }
        }

        public static void SaveState(DateTime checkedAt, string offered)
        {
            try { Directory.CreateDirectory(Dir); File.WriteAllLines(StateFile, new[] { checkedAt.ToString("o"), offered ?? "" }); } catch { }
        }

        // ------------------------------------------------------------ check
        /// <summary>The newest release if it's newer than this version, otherwise null. Throws on network errors.
        /// Blocking: call it on a worker thread. Reads the release's small latest.json first (a plain download, not
        /// counted against GitHub's API limit, which is shared by everyone behind the same internet address), and
        /// falls back to the GitHub API for releases that don't have one.</summary>
        public static Release Check(string repo)
        {
            if (string.IsNullOrWhiteSpace(repo) || !Regex.IsMatch(repo, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")) throw new InvalidOperationException("No update source set");
            Release r;
            try { r = FromManifest(Get("https://github.com/" + repo + "/releases/latest/download/latest.json", false)); }
            catch { r = FromApi(Get("https://api.github.com/repos/" + repo + "/releases/latest", true)); }
            return r == null || r.Version <= Current || string.IsNullOrEmpty(r.ZipUrl) ? null : r;
        }

        static Release FromManifest(string json)
        {
            var d = Json.Parse(json) as Dictionary<string, object>;
            if (d == null) throw new FormatException("bad latest.json");
            var r = new Release { Tag = d.Str("tag", ""), ZipName = d.Str("zip", ""), ZipUrl = d.Str("url", ""), Sha256 = d.Str("sha256", "").ToLowerInvariant(), Notes = d.Str("notes", ""), PageUrl = d.Str("page", "") };
            if (!Version.TryParse(d.Str("version", ""), out r.Version)) throw new FormatException("bad version in latest.json");
            if (!Regex.IsMatch(r.ZipName, @"^HPControl-[\d.]+\.zip$", RegexOptions.IgnoreCase) || !Regex.IsMatch(r.Sha256, "^[0-9a-f]{64}$")) throw new FormatException("bad latest.json");
            return r;
        }

        static Release FromApi(string json)
        {
            var d = Json.Parse(json) as Dictionary<string, object>;
            if (d == null || d.Bool("draft") || d.Bool("prerelease")) return null;
            var r = new Release { Tag = d.Str("tag_name", ""), Notes = d.Str("body", ""), PageUrl = d.Str("html_url", "") };
            if (!Version.TryParse(r.Tag.TrimStart('v', 'V'), out r.Version)) return null;
            var asset = (d.Arr("assets") ?? new List<object>()).OfType<Dictionary<string, object>>()
                .FirstOrDefault(a => Regex.IsMatch(a.Str("name", ""), @"^HPControl-[\d.]+\.zip$", RegexOptions.IgnoreCase));
            if (asset == null) return null;
            r.ZipName = asset.Str("name"); r.ZipUrl = asset.Str("browser_download_url"); r.Size = (long)asset.Num("size");
            var m = Regex.Match(r.Notes, @"SHA-?256[^0-9A-Fa-f]{0,20}([0-9A-Fa-f]{64})");
            r.Sha256 = m.Success ? m.Groups[1].Value.ToLowerInvariant() : "";
            return r;
        }

        static string Get(string url, bool api)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "HPControl/" + AppInfo.Version;
            if (api) req.Accept = "application/vnd.github+json";
            req.Timeout = 15000; req.ReadWriteTimeout = 15000;
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream())) return sr.ReadToEnd();
        }

        // ------------------------------------------------------------ install
        /// <summary>Downloads, verifies and extracts the release, then starts its installer (which asks for admin and
        /// closes HP Control while it runs). Blocking: call it on a worker thread. progress gets 0-100.</summary>
        public static void DownloadAndInstall(Release r, Action<int> progress)
        {
            // the tray helper and the window could both be asked: only one update at a time
            using (var gate = new System.Threading.Mutex(false, "HPControl.Update." + Environment.UserName))
            {
                bool mine;
                try { mine = gate.WaitOne(0); } catch (System.Threading.AbandonedMutexException) { mine = true; }
                if (!mine) throw new InvalidOperationException("an update is already being installed");
                try { DownloadAndInstallCore(r, progress); }
                finally { gate.ReleaseMutex(); }
            }
        }

        static void DownloadAndInstallCore(Release r, Action<int> progress)
        {
            if (string.IsNullOrEmpty(r.Sha256)) throw new InvalidOperationException("the release has no SHA-256 checksum, so it can't be verified");
            var uri = new Uri(r.ZipUrl);
            if (uri.Scheme != "https" || !(uri.Host == "github.com" || uri.Host.EndsWith(".githubusercontent.com"))) throw new InvalidOperationException("unexpected download address " + uri.Host);

            var root = Path.Combine(Dir, "updates");
            var zip = Path.Combine(root, r.ZipName);
            var dir = Path.Combine(root, r.Version.ToString());
            Directory.CreateDirectory(root);
            // old downloads aren't needed once a newer one is here
            foreach (var old in Directory.GetFileSystemEntries(root)) try { if (File.Exists(old)) File.Delete(old); else Directory.Delete(old, true); } catch { }

            var req = (HttpWebRequest)WebRequest.Create(uri);
            req.UserAgent = "HPControl/" + AppInfo.Version;
            req.Timeout = 30000; req.ReadWriteTimeout = 30000;
            using (var resp = req.GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = File.Create(zip))
            {
                long total = resp.ContentLength > 0 ? resp.ContentLength : r.Size, got = 0;
                var buf = new byte[81920]; int n, last = -1;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n); got += n;
                    if (got > 50L * 1024 * 1024) throw new InvalidOperationException("download is far too big");
                    int pct = total > 0 ? (int)(got * 100 / total) : 0;
                    if (pct != last) { last = pct; progress?.Invoke(pct); }
                }
            }

            string hash;
            using (var sha = SHA256.Create()) using (var f = File.OpenRead(zip)) hash = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
            if (hash != r.Sha256) { try { File.Delete(zip); } catch { } throw new InvalidOperationException("the download didn't match its checksum (damaged or changed), so it wasn't installed"); }

            ZipFile.ExtractToDirectory(zip, dir);   // fresh folder; refuses entries outside it
            var installer = Directory.GetFiles(dir, "install.cmd", SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault();
            if (installer == null || !File.Exists(Path.Combine(Path.GetDirectoryName(installer), "bin", "HPControl.Service.exe")))
                throw new InvalidOperationException("the update doesn't contain the installer");
            Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installer) })?.Dispose();
        }
    }
}
