using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TaskbarSwitch
{
    /// <summary>Version like 1.2.0 or 1.2.0-beta.3. A release is newer than its own pre-releases.</summary>
    class SemVer : IComparable<SemVer>
    {
        public readonly Version Core;
        public readonly string Pre;   // "" for a stable release

        SemVer(Version core, string pre) { Core = core; Pre = pre; }

        public bool IsPrerelease => Pre.Length > 0;

        public static SemVer TryParse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().TrimStart('v', 'V');
            int plus = s.IndexOf('+');
            if (plus >= 0) s = s.Substring(0, plus);
            int dash = s.IndexOf('-');
            string core = dash >= 0 ? s.Substring(0, dash) : s;
            string pre = dash >= 0 ? s.Substring(dash + 1) : "";
            if (!Version.TryParse(core, out var v)) return null;
            return new SemVer(new Version(v.Major, v.Minor, Math.Max(0, v.Build)), pre);
        }

        public int CompareTo(SemVer o)
        {
            int c = Core.CompareTo(o.Core);
            if (c != 0) return c;
            if (!IsPrerelease || !o.IsPrerelease) return IsPrerelease == o.IsPrerelease ? 0 : (IsPrerelease ? -1 : 1);
            var a = Pre.Split('.');
            var b = o.Pre.Split('.');
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                bool an = int.TryParse(a[i], out int ai), bn = int.TryParse(b[i], out int bi);
                c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
                if (c != 0) return c;
            }
            return a.Length.CompareTo(b.Length);
        }

        public override string ToString() => Core.ToString(3) + (IsPrerelease ? "-" + Pre : "");
    }

    class ReleaseInfo
    {
        public string Tag;
        public SemVer Version;
        public string ExeUrl;
        public string ShaUrl;
        public string Digest;   // "sha256:..." from the GitHub API, when it provides one
        public override string ToString() => Tag;
    }

    /// <summary>
    /// Checks GitHub releases, downloads and verifies a new exe, and swaps it in.
    /// Stable channel = /releases/latest (never a pre-release). Beta channel = newest of all releases.
    /// </summary>
    static class Updater
    {
        const string Repo = "Khaniyan1/TaskBarSwitch";
        const string ExeAsset = "TaskbarSwitch.exe";
        const string ShaAsset = "TaskbarSwitch.exe.sha256";

        public static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarSwitch", "update");
        static string BadVersionsFile => Path.Combine(Dir, "bad-versions.txt");
        public static string PreviousExe => Path.Combine(Installer.InstallDir, "TaskbarSwitch.prev.exe");

        public static string CurrentVersion
        {
            get
            {
                try
                {
                    var a = Assembly.GetExecutingAssembly();
                    var info = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                    return SemVer.TryParse(info)?.ToString() ?? a.GetName().Version.ToString(3);
                }
                catch { return "0.0.0"; }
            }
        }

        // ---------------------------------------------------------------- check

        /// <summary>Returns a release newer than this one on the given channel, or null. Throws on network errors.</summary>
        public static ReleaseInfo FindUpdate(string channel)
        {
            bool beta = channel == "beta";
            Log.Info($"Checking for updates ({(beta ? "beta" : "stable")} channel, running {CurrentVersion})");

            List<ReleaseInfo> found;
            if (beta)
            {
                var list = (object[])Parse(GetString($"https://api.github.com/repos/{Repo}/releases?per_page=30"));
                found = list.Cast<Dictionary<string, object>>().Select(ToRelease).Where(r => r != null).ToList();
            }
            else
            {
                string json;
                try { json = GetString($"https://api.github.com/repos/{Repo}/releases/latest"); }
                catch (WebException ex) when ((ex.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
                {
                    Log.Info("No stable release published yet");
                    return null;
                }
                var r = ToRelease((Dictionary<string, object>)Parse(json));
                found = r != null && !r.Version.IsPrerelease ? new List<ReleaseInfo> { r } : new List<ReleaseInfo>();
            }

            var current = SemVer.TryParse(CurrentVersion);
            var bad = BadVersions();
            var best = found
                .Where(r => r.Version.CompareTo(current) > 0)
                .Where(r => !bad.Contains(r.Version.ToString()))
                .OrderByDescending(r => r.Version)
                .FirstOrDefault();

            Log.Info(best != null ? "Update available: " + best.Tag : "No newer version (newest seen: " +
                (found.OrderByDescending(r => r.Version).FirstOrDefault()?.Tag ?? "none") + ")");
            return best;
        }

        static ReleaseInfo ToRelease(Dictionary<string, object> d)
        {
            if (d.TryGetValue("draft", out var draft) && draft is bool isDraft && isDraft) return null;
            string tag = d.TryGetValue("tag_name", out var t) ? t as string : null;
            var version = SemVer.TryParse(tag);
            if (version == null) return null;

            var r = new ReleaseInfo { Tag = tag, Version = version };
            if (d.TryGetValue("assets", out var a) && a is object[] assets)
            {
                foreach (Dictionary<string, object> asset in assets.OfType<Dictionary<string, object>>())
                {
                    string name = asset.TryGetValue("name", out var n) ? n as string : null;
                    string url = asset.TryGetValue("browser_download_url", out var u) ? u as string : null;
                    if (name == ExeAsset)
                    {
                        r.ExeUrl = url;
                        r.Digest = asset.TryGetValue("digest", out var dg) ? dg as string : null;
                    }
                    else if (name == ShaAsset) r.ShaUrl = url;
                }
            }
            if (r.ExeUrl == null || r.ShaUrl == null)
            {
                Log.Warn($"Release {tag} is missing {ExeAsset} or {ShaAsset}; ignored");
                return null;
            }
            return r;
        }

        // ------------------------------------------------------------- download

        /// <summary>Downloads and verifies the release. Returns the path of the verified exe, or null.</summary>
        public static string Download(ReleaseInfo r)
        {
            Directory.CreateDirectory(Dir);
            foreach (var old in Directory.GetFiles(Dir, "*.exe*"))
            {
                try { File.Delete(old); } catch { /* the updater from last time may still be closing */ }
            }

            string shaText = GetString(r.ShaUrl);
            var m = Regex.Match(shaText, "\\b[0-9a-fA-F]{64}\\b");
            if (!m.Success)
            {
                Log.Warn($"Update {r.Tag}: {ShaAsset} doesn't contain a SHA-256 hash; not installing");
                return null;
            }
            string expected = m.Value.ToLowerInvariant();
            if (r.Digest != null && r.Digest.StartsWith("sha256:") &&
                !string.Equals(r.Digest.Substring(7), expected, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"Update {r.Tag}: {ShaAsset} ({expected}) doesn't match GitHub's digest ({r.Digest}); not installing");
                return null;
            }

            string tmp = Path.Combine(Dir, "TaskbarSwitch-" + r.Version + ".exe.download");
            string path = Path.Combine(Dir, "TaskbarSwitch-" + r.Version + ".exe");
            Log.Info($"Downloading {r.Tag} from {r.ExeUrl}");
            using (var wc = NewClient()) wc.DownloadFile(r.ExeUrl, tmp);

            string actual;
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(tmp))
                actual = string.Concat(sha.ComputeHash(f).Select(b => b.ToString("x2")));
            if (actual != expected)
            {
                Log.Warn($"Update {r.Tag}: SHA-256 mismatch (expected {expected}, got {actual}); deleted");
                File.Delete(tmp);
                return null;
            }

            var fileVersion = SemVer.TryParse(FileVersionInfo.GetVersionInfo(tmp).ProductVersion);
            if (fileVersion == null || fileVersion.CompareTo(r.Version) != 0 ||
                fileVersion.CompareTo(SemVer.TryParse(CurrentVersion)) <= 0)
            {
                Log.Warn($"Update {r.Tag}: the exe says it is version {fileVersion?.ToString() ?? "?"}; not installing");
                File.Delete(tmp);
                return null;
            }

            File.Move(tmp, path);
            Log.Info($"Downloaded and verified {r.Tag} (SHA-256 {actual})");
            return path;
        }

        /// <summary>Starts the downloaded exe in updater mode. It tells this process to exit once it's running.</summary>
        public static bool Launch(string downloadedExe)
        {
            try
            {
                string args = $"/update \"{Installer.InstalledExe}\" {Process.GetCurrentProcess().Id} {CurrentVersion}";
                Log.Info($"Starting updater: {downloadedExe} {args}");
                Process.Start(downloadedExe, args);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Could not start the updater", ex);
                return false;
            }
        }

        // ----------------------------------------------------------------- swap

        /// <summary>
        /// Runs in the new exe: TaskbarSwitch.exe /update "installed exe" oldPid oldVersion.
        /// Replaces the installed exe with this one, keeps the old one as TaskbarSwitch.prev.exe,
        /// and rolls back if the new version doesn't start cleanly.
        /// </summary>
        public static void RunSwap(string[] args)
        {
            string target = args.Length > 1 ? args[1] : Installer.InstalledExe;
            int oldPid = args.Length > 2 && int.TryParse(args[2], out int p) ? p : 0;
            string oldVersion = args.Length > 3 ? args[3] : "?";
            string self = Application.ExecutablePath;
            Log.Info($"Updater {CurrentVersion}: replacing {target} ({oldVersion})");

            if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(Installer.InstalledExe), StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn("Updater refused: target is not the installed exe");
                return;
            }

            bool replaced = false;
            using (var started = new EventWaitHandle(false, EventResetMode.ManualReset, Program.StartedEventName))
            {
                try
                {
                    // Ask the running copy to exit, and wait until it has.
                    Process old = null;
                    try { if (oldPid != 0) old = Process.GetProcessById(oldPid); } catch { /* already gone */ }
                    Program.Signal(Program.ExitEventName);
                    if (old != null && !old.WaitForExit(30000))
                    {
                        Log.Warn("Old version did not exit within 30 s; stopping it");
                        old.Kill();
                        old.WaitForExit(5000);
                    }

                    File.Copy(target, PreviousExe, true);
                    replaced = true;
                    Installer.CopyWithRetry(self, target);
                    Installer.SetDisplayVersion(CurrentVersion);
                    Log.Info($"Installed {CurrentVersion}; previous version kept as {PreviousExe}");

                    started.Reset();
                    var proc = Process.Start(target, "/updated");
                    for (int waited = 0; waited < 60000; waited += 500)
                    {
                        if (started.WaitOne(500))
                        {
                            Log.Info($"Update to {CurrentVersion} finished; new version is running");
                            return;
                        }
                        if (proc.HasExited) break;
                    }
                    Log.Warn($"New version {CurrentVersion} did not start cleanly " +
                             (proc.HasExited ? $"(exit code {proc.ExitCode})" : "(no sign of life after 60 s)"));
                    try { if (!proc.HasExited) { proc.Kill(); proc.WaitForExit(5000); } } catch { }
                }
                catch (Exception ex)
                {
                    Log.Error("Update failed", ex);
                }
            }

            // Something went wrong: put the old exe back and start it.
            try
            {
                if (replaced)
                {
                    Installer.CopyWithRetry(PreviousExe, target);
                    Installer.SetDisplayVersion(oldVersion);
                    MarkBad(CurrentVersion);
                    Log.Info($"Rolled back to {oldVersion}; {CurrentVersion} will be skipped from now on");
                }
            }
            catch (Exception ex) { Log.Error("Rolling back the update failed", ex); }
            Installer.LaunchInstalled("");
        }

        // -------------------------------------------------------------- helpers

        static HashSet<string> BadVersions()
        {
            try
            {
                if (File.Exists(BadVersionsFile))
                    return new HashSet<string>(File.ReadAllLines(BadVersionsFile).Select(l => l.Trim()).Where(l => l.Length > 0));
            }
            catch (Exception ex) { Log.Warn("Could not read " + BadVersionsFile, ex); }
            return new HashSet<string>();
        }

        static void MarkBad(string version)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(BadVersionsFile, version + "\r\n");
            }
            catch (Exception ex) { Log.Warn("Could not write " + BadVersionsFile, ex); }
        }

        static WebClient NewClient(bool api = false)
        {
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "TaskbarSwitch/" + CurrentVersion;
            if (api) wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            return wc;
        }

        static string GetString(string url)
        {
            using (var wc = NewClient(url.StartsWith("https://api.github.com/"))) return wc.DownloadString(url);
        }

        static object Parse(string json) => new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.DeserializeObject(json);
    }
}
