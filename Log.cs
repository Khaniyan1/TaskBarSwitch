using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TaskbarSwitch
{
    /// <summary>
    /// Simple file logger: %APPDATA%\TaskbarSwitch\log.txt, rotated to log.old.txt at 1 MB.
    /// Never throws.
    /// </summary>
    static class Log
    {
        static readonly object sync = new object();
        const long MaxBytes = 1_000_000;

        public static string FilePath => Path.Combine(Config.Dir, "log.txt");
        static string OldPath => Path.Combine(Config.Dir, "log.old.txt");

        public static void Info(string msg) => Write("INFO ", msg);
        public static void Warn(string msg) => Write("WARN ", msg);
        public static void Warn(string msg, Exception ex) => Write("WARN ", msg + " | " + Describe(ex, false));
        public static void Error(string msg, Exception ex) => Write("ERROR", msg + "\r\n" + Describe(ex, true));

        /// <summary>Kept for older call sites.</summary>
        public static void Write(string msg) => Info(msg);

        static void Write(string level, string msg)
        {
            try
            {
                lock (sync)
                {
                    Directory.CreateDirectory(Config.Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        if (File.Exists(OldPath)) File.Delete(OldPath);
                        File.Move(FilePath, OldPath);
                    }
                    File.AppendAllText(FilePath,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [t{Thread.CurrentThread.ManagedThreadId}] {msg}\r\n");
                }
            }
            catch { /* logging must never break the app */ }
        }

        static string Describe(Exception ex, bool full)
        {
            if (ex == null) return "(no exception)";
            return full ? ex.ToString() : $"{ex.GetType().Name}: {ex.Message}";
        }

        /// <summary>Writes a header with everything useful for debugging on another machine.</summary>
        public static void SessionHeader()
        {
            string version = "?";
            try { version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3); } catch { }

            string win = Environment.OSVersion.VersionString;
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k != null)
                    {
                        string product = k.GetValue("ProductName") as string ?? "Windows";
                        if (int.TryParse(k.GetValue("CurrentBuild") as string, out int build) && build >= 22000)
                            product = product.Replace("Windows 10", "Windows 11");
                        win = $"{product} {k.GetValue("DisplayVersion")} " +
                              $"build {k.GetValue("CurrentBuild")}.{k.GetValue("UBR")}";
                    }
                }
            }
            catch { }

            string screens = "";
            try
            {
                foreach (var s in Screen.AllScreens)
                    screens += $"\r\n    {s.DeviceName} {s.Bounds.Width}x{s.Bounds.Height} at {s.Bounds.X},{s.Bounds.Y}" +
                               (s.Primary ? " (primary)" : "");
            }
            catch { }

            Info("==================== TaskbarSwitch " + version + " started ====================\r\n" +
                 $"  Windows: {win}\r\n" +
                 $"  .NET CLR: {Environment.Version}, 64-bit process: {Environment.Is64BitProcess}\r\n" +
                 $"  Exe: {Application.ExecutablePath} (installed: {Installer.IsRunningFromInstallDir()})\r\n" +
                 $"  Screens:{screens}");
        }

        public static void OpenInEditor()
        {
            try
            {
                if (!File.Exists(FilePath)) Info("Log opened from the app");
                Process.Start("notepad.exe", $"\"{FilePath}\"");
            }
            catch (Exception ex) { Warn("Could not open log", ex); }
        }

        public static void OpenFolder()
        {
            try { Process.Start("explorer.exe", $"\"{Config.Dir}\""); }
            catch (Exception ex) { Warn("Could not open log folder", ex); }
        }
    }
}
