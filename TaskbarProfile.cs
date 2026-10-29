using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace TaskbarSwitch
{
    /// <summary>One set of taskbar settings (mirrors Settings > Personalization > Taskbar).</summary>
    class TaskbarProfile
    {
        public int Position = 3;            // 0=left 1=top 2=right 3=bottom
        public bool AutoHide = false;
        public int Alignment = 1;           // 0=left 1=center
        public int Combine = 0;             // 0=always (hide labels) 1=when full 2=never
        public bool ShowOnAllDisplays = false;
        public int MultiMode = 0;           // 0=all taskbars 1=main + where open 2=where open
        public int SearchMode = 1;          // 0=hide 1=icon 2=box 3=icon+label
        public bool TaskView = true;

        public override string ToString() =>
            $"position={new[] { "left", "top", "right", "bottom" }[Math.Max(0, Math.Min(3, Position))]} autohide={AutoHide} " +
            $"align={(Alignment == 0 ? "left" : "center")} combine={Combine} allDisplays={ShowOnAllDisplays} " +
            $"multiMode={MultiMode} search={SearchMode} taskView={TaskView}";

        public TaskbarProfile Clone() => (TaskbarProfile)MemberwiseClone();

        public bool SameAs(TaskbarProfile o) =>
            o != null && Position == o.Position && AutoHide == o.AutoHide && Alignment == o.Alignment &&
            Combine == o.Combine && ShowOnAllDisplays == o.ShowOnAllDisplays && MultiMode == o.MultiMode &&
            SearchMode == o.SearchMode && TaskView == o.TaskView;

        public IEnumerable<string> ToLines(string prefix)
        {
            yield return $"{prefix}.Position={Position}";
            yield return $"{prefix}.AutoHide={(AutoHide ? 1 : 0)}";
            yield return $"{prefix}.Alignment={Alignment}";
            yield return $"{prefix}.Combine={Combine}";
            yield return $"{prefix}.ShowOnAllDisplays={(ShowOnAllDisplays ? 1 : 0)}";
            yield return $"{prefix}.MultiMode={MultiMode}";
            yield return $"{prefix}.SearchMode={SearchMode}";
            yield return $"{prefix}.TaskView={(TaskView ? 1 : 0)}";
        }

        public static TaskbarProfile FromDict(Dictionary<string, string> d, string prefix, TaskbarProfile fallback)
        {
            var p = fallback.Clone();
            int Get(string key, int def) =>
                d.TryGetValue(prefix + "." + key, out var s) && int.TryParse(s, out var v) ? v : def;
            p.Position = Get("Position", p.Position);
            p.AutoHide = Get("AutoHide", p.AutoHide ? 1 : 0) == 1;
            p.Alignment = Get("Alignment", p.Alignment);
            p.Combine = Get("Combine", p.Combine);
            p.ShowOnAllDisplays = Get("ShowOnAllDisplays", p.ShowOnAllDisplays ? 1 : 0) == 1;
            p.MultiMode = Get("MultiMode", p.MultiMode);
            p.SearchMode = Get("SearchMode", p.SearchMode);
            p.TaskView = Get("TaskView", p.TaskView ? 1 : 0) == 1;
            return p;
        }
    }

    /// <summary>Reads and writes the taskbar's registry settings.</summary>
    static class TaskbarRegistry
    {
        internal const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
        internal const string StuckKey = ExplorerKey + @"\StuckRects3";
        internal const string AdvancedKey = ExplorerKey + @"\Advanced";
        internal const string SearchKey = @"Software\Microsoft\Windows\CurrentVersion\Search";

        static readonly object applyLock = new object();

        public static TaskbarProfile ReadCurrent()
        {
            var p = new TaskbarProfile();
            var b = ReadStuck();
            if (b != null && b.Length > 12)
            {
                p.Position = b[12] <= 3 ? b[12] : 3;
                p.AutoHide = (b[8] & 1) == 1;
            }
            using (var k = Registry.CurrentUser.OpenSubKey(AdvancedKey))
            {
                // Newer Windows 11 builds (movable taskbar) keep the real position here and
                // overwrite StuckRects3 from it, so this one wins when it exists.
                int loc = GetInt(k, "TaskbarLocation", -1);
                if (loc >= 0 && loc <= 3) p.Position = loc;
                p.Alignment = GetInt(k, "TaskbarAl", 1);
                p.Combine = GetInt(k, "TaskbarGlomLevel", 0);
                p.ShowOnAllDisplays = GetInt(k, "MMTaskbarEnabled", 0) == 1;
                p.MultiMode = GetInt(k, "MMTaskbarMode", 0);
                p.TaskView = GetInt(k, "ShowTaskViewButton", 1) == 1;
            }
            using (var k = Registry.CurrentUser.OpenSubKey(SearchKey))
                p.SearchMode = GetInt(k, "SearchboxTaskbarMode", 1);
            return p;
        }

        /// <summary>Applies the profile. Restarts Explorer only if something actually changed.</summary>
        public static bool Apply(TaskbarProfile p)
        {
            lock (applyLock)
            {
                string invalid = Validate(p);
                if (invalid != null)
                {
                    Log.Warn("Refusing to apply invalid settings (" + invalid + "): " + p);
                    return false;
                }

                var cur = ReadCurrent();
                if (cur.SameAs(p))
                {
                    Log.Info("Taskbar already matches the wanted settings; nothing to do");
                    return false;
                }
                Log.Info("Changing taskbar\r\n    from: " + cur + "\r\n    to:   " + p);

                // Never change anything we couldn't back up first.
                if (!Recovery.BackupBeforeChange())
                {
                    Log.Warn("Change cancelled because the backup failed");
                    return false;
                }

                var b = ReadStuck();
                if (b == null || b.Length < 48 || b.Length > 64)
                    Log.Warn($"StuckRects3\\Settings has an unexpected layout (length {b?.Length ?? 0}); " +
                             "position/auto-hide left alone to be safe");
                else
                    Log.Info($"StuckRects3 before: byte8=0x{b[8]:X2} byte12={b[12]} (length {b.Length})");
                if (b != null && b.Length >= 48 && b.Length <= 64)
                {
                    b[12] = (byte)p.Position;
                    b[8] = (byte)(p.AutoHide ? (b[8] | 1) : (b[8] & ~1));
                    using (var k = Registry.CurrentUser.OpenSubKey(StuckKey, true))
                        k?.SetValue("Settings", b, RegistryValueKind.Binary);
                }
                using (var k = Registry.CurrentUser.CreateSubKey(AdvancedKey))
                {
                    int oldLoc = GetInt(k, "TaskbarLocation", -1);
                    if (oldLoc != p.Position)
                    {
                        k.SetValue("TaskbarLocation", p.Position, RegistryValueKind.DWord);
                        Log.Info($"TaskbarLocation {oldLoc} -> {p.Position}");
                    }
                    SetIfDiff(k, "TaskbarAl", p.Alignment, cur.Alignment);
                    SetIfDiff(k, "TaskbarGlomLevel", p.Combine, cur.Combine);
                    SetIfDiff(k, "MMTaskbarEnabled", p.ShowOnAllDisplays ? 1 : 0, cur.ShowOnAllDisplays ? 1 : 0);
                    SetIfDiff(k, "MMTaskbarMode", p.MultiMode, cur.MultiMode);
                    SetIfDiff(k, "ShowTaskViewButton", p.TaskView ? 1 : 0, cur.TaskView ? 1 : 0);
                }
                using (var k = Registry.CurrentUser.CreateSubKey(SearchKey))
                    SetIfDiff(k, "SearchboxTaskbarMode", p.SearchMode, cur.SearchMode);

                var written = ReadCurrent();
                if (!written.SameAs(p))
                    Log.Warn("Registry did not take all values. Read back: " + written);

                RestartExplorer();

                // Also set auto-hide the way the Settings app does, on the live taskbar.
                if (WaitForTaskbar(10000))
                {
                    Thread.Sleep(1000);
                    SetAutoHideLive(p.AutoHide);
                }
                else Log.Warn("Taskbar window did not appear within 10 s after restarting Explorer");

                // Explorer can write values back once it has settled; check twice.
                Thread.Sleep(2000);
                Verify(p, "3 s after restart");
                Thread.Sleep(8000);
                Verify(p, "11 s after restart");
                return true;
            }
        }

        static void Verify(TaskbarProfile wanted, string when)
        {
            var actual = ReadCurrent();
            bool liveHide = GetAutoHideLive(out bool known);
            string live = known ? $" (live taskbar auto-hide: {liveHide})" : "";
            if (actual.SameAs(wanted) && (!known || liveHide == wanted.AutoHide))
                Log.Info($"Check {when}: OK{live}");
            else
                Log.Warn($"Check {when}: taskbar differs from what was set{live}\r\n    wanted: {wanted}\r\n    actual: {actual}");
        }

        // ---- Live auto-hide via SHAppBarMessage (what Settings uses) ----

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [DllImport("shell32.dll")]
        static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        const uint ABM_GETSTATE = 0x4, ABM_SETSTATE = 0xA;
        const int ABS_AUTOHIDE = 0x1, ABS_ALWAYSONTOP = 0x2;

        static bool WaitForTaskbar(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (FindWindow("Shell_TrayWnd", null) != IntPtr.Zero) return true;
                Thread.Sleep(200);
            }
            return false;
        }

        static bool GetAutoHideLive(out bool known)
        {
            known = false;
            try
            {
                var tray = FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero) return false;
                var abd = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = tray };
                ulong state = SHAppBarMessage(ABM_GETSTATE, ref abd).ToUInt64();
                known = true;
                return (state & ABS_AUTOHIDE) != 0;
            }
            catch (Exception ex) { Log.Warn("Reading live auto-hide failed", ex); return false; }
        }

        static void SetAutoHideLive(bool on)
        {
            try
            {
                var tray = FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero) { Log.Warn("No taskbar window; live auto-hide not set"); return; }
                bool before = GetAutoHideLive(out bool known);
                if (known && before == on) return;
                var abd = new APPBARDATA
                {
                    cbSize = Marshal.SizeOf(typeof(APPBARDATA)),
                    hWnd = tray,
                    lParam = (IntPtr)(on ? ABS_AUTOHIDE : ABS_ALWAYSONTOP)
                };
                SHAppBarMessage(ABM_SETSTATE, ref abd);
                Log.Info($"Live auto-hide {(known ? before.ToString() : "?")} -> {on}");
            }
            catch (Exception ex) { Log.Warn("Setting live auto-hide failed", ex); }
        }

        internal static void RestartExplorer()
        {
            var sw = Stopwatch.StartNew();
            var procs = Process.GetProcessesByName("explorer");
            foreach (var pr in procs)
            {
                try { pr.Kill(); pr.WaitForExit(3000); }
                catch (Exception ex) { Log.Warn($"Could not stop explorer pid {pr.Id}", ex); }
            }
            // Windows normally restarts the shell by itself; start it if it doesn't.
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(250);
                if (Process.GetProcessesByName("explorer").Length > 0)
                {
                    Log.Info($"Explorer restarted by Windows ({procs.Length} process(es) stopped, {sw.ElapsedMilliseconds} ms)");
                    return;
                }
            }
            try
            {
                Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
                Log.Info($"Explorer did not come back by itself; started it ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex) { Log.Error("Could not start Explorer again", ex); }
        }

        static string Validate(TaskbarProfile p)
        {
            if (p == null) return "no settings";
            if (p.Position < 0 || p.Position > 3) return "position " + p.Position;
            if (p.Alignment < 0 || p.Alignment > 1) return "alignment " + p.Alignment;
            if (p.Combine < 0 || p.Combine > 2) return "combine " + p.Combine;
            if (p.MultiMode < 0 || p.MultiMode > 2) return "multi-display mode " + p.MultiMode;
            if (p.SearchMode < 0 || p.SearchMode > 3) return "search mode " + p.SearchMode;
            return null;
        }

        static byte[] ReadStuck()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(StuckKey))
                return k?.GetValue("Settings") as byte[];
        }

        static int GetInt(RegistryKey k, string name, int def)
        {
            if (k == null) return def;
            var v = k.GetValue(name);
            return v is int i ? i : def;
        }

        static void SetIfDiff(RegistryKey k, string name, int value, int current)
        {
            if (value != current || k.GetValue(name) == null)
                k.SetValue(name, value, RegistryValueKind.DWord);
        }
    }
}
