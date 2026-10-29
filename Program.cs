using System;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarSwitch
{
    static class Program
    {
        public const string MutexName = "Local\\TaskbarSwitch.Instance";
        public const string ShowEventName = "Local\\TaskbarSwitch.Show";
        public const string ExitEventName = "Local\\TaskbarSwitch.Exit";

        static DateTime lastErrorDialog = DateTime.MinValue;

        [STAThread]
        static void Main(string[] args)
        {
            // Catch everything that would otherwise kill the app silently.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => OnCrash("Unhandled error on the UI thread", e.Exception, true);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                OnCrash("Fatal unhandled error" + (e.IsTerminating ? " (app is closing)" : ""),
                        e.ExceptionObject as Exception, false);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Log.Error("Unobserved background task error", e.Exception);
                e.SetObserved();
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string arg = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            if (arg == "/uninstall")
            {
                Installer.Uninstall();
                return;
            }

            // Emergency switches that work even if the settings window won't open:
            //   TaskbarSwitch.exe /restore   original taskbar from before TaskbarSwitch
            //   TaskbarSwitch.exe /reset     Windows' own defaults
            if (arg == "/restore" || arg == "/reset")
            {
                Signal(ExitEventName);   // stop a running copy so it doesn't switch again
                Thread.Sleep(1500);
                try
                {
                    var cfg = Config.TryLoadExisting();
                    if (cfg != null) { cfg.Enabled = false; cfg.Save(); }
                }
                catch (Exception ex) { Log.Warn("Could not turn off automatic switching", ex); }

                bool ok = arg == "/restore" ? Recovery.RestoreOriginal() : Recovery.ResetToWindowsDefaults();
                MessageBox.Show(ok
                        ? (arg == "/restore" ? "Original taskbar restored." : "Taskbar reset to Windows defaults.") +
                          "\n\nAutomatic switching is turned off until you turn it on again in TaskbarSwitch."
                        : "That didn't work. Details are in " + Log.FilePath,
                    "TaskbarSwitch", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                return;
            }

            if (!Installer.IsRunningFromInstallDir())
            {
                var r = MessageBox.Show(
                    "Install TaskbarSwitch on this PC?\n\n" +
                    "It installs for your user only (no admin needed), starts with Windows, " +
                    "and shows up under Settings > Apps > Installed apps so you can uninstall it normally.\n\n" +
                    "Choose No to just run it this once without installing.",
                    "TaskbarSwitch", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

                if (r == DialogResult.Cancel) return;
                if (r == DialogResult.Yes)
                {
                    // Stop an already-running copy (e.g. when updating) so the exe can be replaced.
                    Signal(ExitEventName);
                    Thread.Sleep(1200);
                    if (Installer.Install()) Installer.LaunchInstalled("/firstrun");
                    return;
                }
            }

            using (var mutex = new Mutex(true, MutexName, out bool created))
            {
                if (!created)
                {
                    // Already running: just open its settings window.
                    Signal(ShowEventName);
                    return;
                }

                var showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                var exitEvt = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
                Application.Run(new TrayApp(showEvt, exitEvt, arg == "/firstrun"));
                GC.KeepAlive(mutex);
            }
        }

        static void OnCrash(string what, Exception ex, bool canContinue)
        {
            Log.Error(what, ex);
            if (!canContinue || (DateTime.Now - lastErrorDialog).TotalSeconds < 60) return;
            lastErrorDialog = DateTime.Now;
            try
            {
                var r = MessageBox.Show(
                    "TaskbarSwitch ran into an error but is still running:\n\n" + ex?.Message +
                    "\n\nThe details are in the log. Open it now?",
                    "TaskbarSwitch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r == DialogResult.Yes) Log.OpenInEditor();
            }
            catch { }
        }

        public static void Signal(string name)
        {
            try
            {
                using (var e = EventWaitHandle.OpenExisting(name)) e.Set();
            }
            catch { /* nothing running */ }
        }
    }
}
