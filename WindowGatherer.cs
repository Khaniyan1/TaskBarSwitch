using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarSwitch
{
    /// <summary>
    /// Moves app windows that are mostly off every connected screen (e.g. left behind on an external
    /// monitor that was just unplugged) onto the primary screen, and puts windows back where they were
    /// when that monitor is reconnected. Safe to call from any thread.
    /// </summary>
    static class WindowGatherer
    {
        static readonly object sync = new object();

        /// <summary>Returns how many windows were moved.</summary>
        public static int Gather(string why) => Run("Gathering windows", 0, () => GatherCore(why));

        /// <summary>Runs f under the lock, in real pixels on every monitor whatever each screen's DPI.</summary>
        static T Run<T>(string what, T onError, Func<T> f)
        {
            lock (sync)
            {
                IntPtr oldContext = IntPtr.Zero;
                try { oldContext = SetThreadDpiAwarenessContext(PerMonitorAwareV2); }
                catch (EntryPointNotFoundException) { }
                try { return f(); }
                catch (Exception ex) { Log.Error(what + " failed", ex); return onError; }
                finally { if (oldContext != IntPtr.Zero) SetThreadDpiAwarenessContext(oldContext); }
            }
        }

        static int GatherCore(string why)
        {
            var monitors = Monitors();
            var primary = monitors.Find(m => m.Primary);
            if (primary == null)
            {
                Log.Warn("Gathering windows: no primary monitor found");
                return 0;
            }

            int moved = 0, failed = 0;
            var report = new StringBuilder();
            foreach (var h in TopLevelWindows())
            {
                if (!IsCandidate(h)) continue;
                var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
                if (!GetWindowPlacement(h, ref wp)) continue;

                bool minimized = IsIconic(h);
                bool maximized = !minimized && IsZoomed(h);

                // rcNormalPosition is in workspace coordinates: (0,0) is the top-left of the primary work area.
                RECT normal = Offset(wp.rcNormalPosition, primary.Work.Left, primary.Work.Top);
                RECT current = normal;
                if (!minimized && !GetWindowRect(h, out current)) continue;
                // Also check where a minimized/maximized window goes when restored.
                if (VisibleFraction(current, monitors) >= 0.5 && VisibleFraction(normal, monitors) >= 0.5) continue;

                RECT target = FitInto(normal, primary.Work);
                wp.rcNormalPosition = Offset(target, -primary.Work.Left, -primary.Work.Top);
                wp.flags = (wp.flags & WPF_RESTORETOMAXIMIZED) | WPF_ASYNCWINDOWPLACEMENT;
                wp.showCmd = minimized ? SW_SHOWMINNOACTIVE : maximized ? SW_SHOWMAXIMIZED : SW_SHOWNOACTIVATE;

                string name = Describe(h);
                if (SetWindowPlacement(h, ref wp))
                {
                    moved++;
                    report.Append($"\r\n    {name}: {current} -> {target}" +
                                  (minimized ? " (minimized)" : maximized ? " (maximized)" : ""));
                }
                else
                {
                    // Typically a window of an app running as administrator.
                    failed++;
                    report.Append($"\r\n    {name}: could not move (error {Marshal.GetLastWin32Error()})");
                }
            }

            if (moved > 0 || failed > 0)
                Log.Info($"Gathered {moved} window(s) onto the primary screen ({why})" +
                         (failed > 0 ? $", {failed} could not be moved" : "") + report);
            else
                Log.Info($"Gathering windows ({why}): none were off-screen");
            return moved;
        }

        // ------------------------------------------------- remember and restore

        /// <summary>Where a window was while docked, and where it ended up after the disconnect.</summary>
        class Saved
        {
            public IntPtr Hwnd;
            public uint Pid;
            public string Name;        // for the log; filled in at disconnect
            public RECT Normal;        // restored position, screen coordinates
            public int State;          // SW_SHOWNORMAL / SW_SHOWMINIMIZED / SW_SHOWMAXIMIZED
            public bool RestoreToMax;  // minimized from maximized
            public RECT Monitor;       // bounds of the monitor it was on
            public RECT AfterNormal;
            public int AfterState;
        }

        static Dictionary<IntPtr, Saved> lastSnapshot = new Dictionary<IntPtr, Saved>();
        static List<Saved> displaced = new List<Saved>();

        /// <summary>Records where every window is. Called every 10 s while an external monitor is connected.</summary>
        public static void Remember() => Run("Remembering window positions", 0, () =>
        {
            var monitors = Monitors();
            var primary = monitors.Find(m => m.Primary);
            // With one screen left, the external one may already be gone; keep the last good snapshot.
            if (monitors.Count < 2 || primary == null) return 0;
            var snap = new Dictionary<IntPtr, Saved>();
            foreach (var h in TopLevelWindows())
            {
                if (!IsCandidate(h)) continue;
                var s = Read(h, primary.Work);
                if (s == null) continue;
                RECT where = s.State == SW_SHOWMINIMIZED || !GetWindowRect(h, out var r) ? s.Normal : r;
                s.Monitor = MonitorBounds(where);
                snap[h] = s;
            }
            lastSnapshot = snap;
            return 0;
        });

        /// <summary>
        /// Call right after the external monitor goes away, before gathering: keeps the windows that were on
        /// a monitor that no longer exists, to put back on reconnect.
        /// </summary>
        public static void NoteDisconnect() => Run("Noting window positions at disconnect", 0, () =>
        {
            var monitors = Monitors();
            displaced = lastSnapshot.Values
                .Where(s => !monitors.Any(m => m.Bounds.Same(s.Monitor)))
                .ToList();
            foreach (var s in displaced) s.Name = Describe(s.Hwnd);   // looked up here, not every 10 s
            lastSnapshot = new Dictionary<IntPtr, Saved>();
            Log.Info($"Remembered {displaced.Count} window(s) from the disconnected monitor" +
                     string.Concat(displaced.Select(s => $"\r\n    {s.Name}: {s.Normal} {StateName(s.State)}")));
            return 0;
        });

        /// <summary>Call once windows have settled after the disconnect (after gathering).</summary>
        public static void NoteSettled() => Run("Noting window positions after disconnect", 0, () =>
        {
            var primary = Monitors().Find(m => m.Primary);
            if (primary == null) return 0;
            foreach (var s in displaced.ToList())
            {
                var now = Valid(s) ? Read(s.Hwnd, primary.Work) : null;
                if (now == null) { displaced.Remove(s); continue; }
                s.AfterNormal = now.Normal;
                s.AfterState = now.State;
            }
            return 0;
        });

        /// <summary>
        /// Puts remembered windows back on the reconnected monitor, unless they have been moved since
        /// the disconnect. Returns how many were restored.
        /// </summary>
        public static int RestoreRemembered() => Run("Restoring window positions", 0, () =>
        {
            if (displaced.Count == 0) return 0;
            var monitors = Monitors();
            var primary = monitors.Find(m => m.Primary);
            if (primary == null) return 0;

            int restored = 0;
            var report = new StringBuilder();
            foreach (var s in displaced.ToList())
            {
                if (!monitors.Any(m => m.Bounds.Same(s.Monitor))) continue;   // a different monitor; keep for later
                displaced.Remove(s);
                var now = Valid(s) ? Read(s.Hwnd, primary.Work) : null;
                if (now == null) continue;
                if (now.State != s.AfterState || !now.Normal.Near(s.AfterNormal, 64))
                {
                    report.Append($"\r\n    {s.Name}: moved since the disconnect; left alone");
                    continue;
                }

                var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
                if (!GetWindowPlacement(s.Hwnd, ref wp)) continue;
                wp.rcNormalPosition = Offset(s.Normal, -primary.Work.Left, -primary.Work.Top);
                wp.flags = (s.RestoreToMax ? WPF_RESTORETOMAXIMIZED : 0) | WPF_ASYNCWINDOWPLACEMENT;
                wp.showCmd = s.State == SW_SHOWMINIMIZED ? SW_SHOWMINNOACTIVE
                           : s.State == SW_SHOWMAXIMIZED ? SW_SHOWMAXIMIZED : SW_SHOWNOACTIVATE;
                if (SetWindowPlacement(s.Hwnd, ref wp))
                {
                    restored++;
                    report.Append($"\r\n    {s.Name}: -> {s.Normal} {StateName(s.State)}");
                }
                else report.Append($"\r\n    {s.Name}: could not move (error {Marshal.GetLastWin32Error()})");
            }
            Log.Info($"Put {restored} window(s) back on the reconnected monitor" + report);
            return restored;
        });

        // ---------------------------------------------------------- change hooks

        static readonly List<IntPtr> hooks = new List<IntPtr>();
        static WinEventProc hookProc;   // kept in a field so the GC doesn't collect it while hooked
        static Action windowChanged;

        /// <summary>
        /// Calls onChange when an app window finishes moving/resizing, is minimized or restored, or appears.
        /// These events are rare (unlike "location changed", which fires on every mouse move). Pass null to stop.
        /// Call on the UI thread: the callbacks arrive through its message loop.
        /// </summary>
        public static void Watch(Action onChange)
        {
            windowChanged = onChange;
            if ((onChange != null) == (hooks.Count > 0)) return;
            if (onChange == null)
            {
                foreach (var h in hooks) UnhookWinEvent(h);
                hooks.Clear();
                Log.Info("Stopped watching window moves");
                return;
            }
            hookProc = OnWinEvent;
            foreach (var (first, last) in new[]
            {
                (EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND),
                (EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND),
                (EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW)
            })
            {
                var h = SetWinEventHook(first, last, IntPtr.Zero, hookProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
                if (h != IntPtr.Zero) hooks.Add(h);
            }
            Log.Info($"Watching window moves ({hooks.Count} of 3 hooks set)");
        }

        static void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
            if (!IsCandidate(hwnd)) return;
            windowChanged?.Invoke();
        }

        static Saved Read(IntPtr h, RECT primaryWork)
        {
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
            if (!GetWindowPlacement(h, ref wp)) return null;
            GetWindowThreadProcessId(h, out uint pid);
            return new Saved
            {
                Hwnd = h,
                Pid = pid,
                Normal = Offset(wp.rcNormalPosition, primaryWork.Left, primaryWork.Top),
                State = IsIconic(h) ? SW_SHOWMINIMIZED : IsZoomed(h) ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL,
                RestoreToMax = (wp.flags & WPF_RESTORETOMAXIMIZED) != 0
            };
        }

        /// <summary>Still the same window? Handles get reused after a window closes.</summary>
        static bool Valid(Saved s)
        {
            if (!IsWindow(s.Hwnd)) return false;
            GetWindowThreadProcessId(s.Hwnd, out uint pid);
            return pid == s.Pid;
        }

        static RECT MonitorBounds(RECT r)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            return GetMonitorInfo(MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST), ref mi) ? mi.rcMonitor : default(RECT);
        }

        static string StateName(int state) =>
            state == SW_SHOWMINIMIZED ? "(minimized)" : state == SW_SHOWMAXIMIZED ? "(maximized)" : "";

        static List<IntPtr> TopLevelWindows()
        {
            var windows = new List<IntPtr>();
            EnumWindowsProc collect = (h, l) => { windows.Add(h); return true; };
            EnumWindows(collect, IntPtr.Zero);
            return windows;
        }

        /// <summary>Visible, unowned app windows, like the ones Alt+Tab shows.</summary>
        static bool IsCandidate(IntPtr h)
        {
            if (!IsWindowVisible(h) || IsHungAppWindow(h)) return false;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
            if (GetWindow(h, GW_OWNER) != IntPtr.Zero && (ex & WS_EX_APPWINDOW) == 0) return false;
            if (GetWindowTextLength(h) == 0) return false;
            // Cloaked: UWP frames in the background, or windows on another virtual desktop.
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
            var cls = new StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            switch (cls.ToString())
            {
                case "Progman":
                case "WorkerW":
                case "Shell_TrayWnd":
                case "Shell_SecondaryTrayWnd":
                    return false;
            }
            return true;
        }

        static double VisibleFraction(RECT r, List<Monitor> monitors)
        {
            long area = (long)r.Width * r.Height;
            if (area <= 0) return 1;   // nothing to rescue
            long visible = 0;
            foreach (var m in monitors)
            {
                long w = Math.Min(r.Right, m.Bounds.Right) - Math.Max(r.Left, m.Bounds.Left);
                long hgt = Math.Min(r.Bottom, m.Bounds.Bottom) - Math.Max(r.Top, m.Bounds.Top);
                if (w > 0 && hgt > 0) visible += w * hgt;
            }
            return (double)visible / area;
        }

        /// <summary>Same size if it fits (shrunk if not), centred in the work area.</summary>
        static RECT FitInto(RECT r, RECT work)
        {
            int w = Math.Min(Math.Max(r.Width, 200), work.Width);
            int h = Math.Min(Math.Max(r.Height, 150), work.Height);
            int left = work.Left + (work.Width - w) / 2;
            int top = work.Top + (work.Height - h) / 2;
            return new RECT { Left = left, Top = top, Right = left + w, Bottom = top + h };
        }

        static RECT Offset(RECT r, int dx, int dy) =>
            new RECT { Left = r.Left + dx, Top = r.Top + dy, Right = r.Right + dx, Bottom = r.Bottom + dy };

        static string Describe(IntPtr h)
        {
            var title = new StringBuilder(256);
            GetWindowText(h, title, title.Capacity);
            string t = title.ToString();
            if (t.Length > 60) t = t.Substring(0, 57) + "...";
            string process = "?";
            try
            {
                GetWindowThreadProcessId(h, out uint pid);
                using (var p = Process.GetProcessById((int)pid)) process = p.ProcessName;
            }
            catch { }
            return $"\"{t}\" ({process})";
        }

        class Monitor
        {
            public RECT Bounds, Work;
            public bool Primary;
        }

        static List<Monitor> Monitors()
        {
            var list = new List<Monitor>();
            MonitorEnumProc collect = (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(hMon, ref mi))
                    list.Add(new Monitor { Bounds = mi.rcMonitor, Work = mi.rcWork, Primary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0 });
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, collect, IntPtr.Zero);
            return list;
        }

        // ---------------------------------------------------------------- Win32

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
            public bool Same(RECT o) => Left == o.Left && Top == o.Top && Right == o.Right && Bottom == o.Bottom;
            public bool Near(RECT o, int tolerance) =>
                Math.Abs(Left - o.Left) <= tolerance && Math.Abs(Top - o.Top) <= tolerance &&
                Math.Abs(Right - o.Right) <= tolerance && Math.Abs(Bottom - o.Bottom) <= tolerance;
            public override string ToString() => $"{Left},{Top} {Width}x{Height}";
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
        const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
        const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        const uint EVENT_OBJECT_SHOW = 0x8002;
        const uint WINEVENT_OUTOFCONTEXT = 0;
        const uint WINEVENT_SKIPOWNPROCESS = 2;
        const int OBJID_WINDOW = 0;
        delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x80;
        const int WS_EX_APPWINDOW = 0x40000;
        const uint GW_OWNER = 4;
        const int DWMWA_CLOAKED = 14;
        const int MONITORINFOF_PRIMARY = 1;
        const int WPF_RESTORETOMAXIMIZED = 2;
        const int WPF_ASYNCWINDOWPLACEMENT = 4;   // don't block on apps that are slow to respond
        const int MONITOR_DEFAULTTONEAREST = 2;
        const int SW_SHOWNORMAL = 1;
        const int SW_SHOWMINIMIZED = 2;
        const int SW_SHOWMAXIMIZED = 3;
        const int SW_SHOWNOACTIVATE = 4;
        const int SW_SHOWMINNOACTIVE = 7;
        static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT wp);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT wp);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT rect, int flags);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder s, int max);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    }
}
