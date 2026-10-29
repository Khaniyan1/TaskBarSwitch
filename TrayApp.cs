using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TaskbarSwitch
{
    static class AppIcon
    {
        static Icon Load(Size size)
        {
            try
            {
                using (var s = typeof(AppIcon).Assembly.GetManifestResourceStream("TaskbarSwitch.app.ico"))
                    return new Icon(s, size);
            }
            catch (Exception ex) { Log.Warn("Could not load the app icon", ex); return SystemIcons.Application; }
        }
        public static Icon Small => Load(SystemInformation.SmallIconSize);
        public static Icon Large => Load(SystemInformation.IconSize);
    }

    class TrayApp : ApplicationContext
    {
        readonly NotifyIcon tray;
        readonly Control invoker;
        readonly System.Windows.Forms.Timer debounce;
        readonly System.Windows.Forms.Timer poll;
        readonly ToolStripMenuItem statusItem;
        readonly ToolStripMenuItem autoItem;
        Config config;
        SettingsForm form;
        bool? lastExternal;
        Displays.Info lastInfo;
        int applying;
        volatile bool recheckAfterApply;
        string lastDetails;

        public TrayApp(EventWaitHandle showEvt, EventWaitHandle exitEvt, bool firstRun)
        {
            invoker = new Control();
            var _ = invoker.Handle; // create the handle so BeginInvoke works

            config = Config.Load();
            Recovery.UpgradeOriginal(config.Original.Position);
            Installer.FixAutostartPath();

            statusItem = new ToolStripMenuItem("Detecting…") { Enabled = false };
            autoItem = new ToolStripMenuItem("Switch automatically") { Checked = config.Enabled, CheckOnClick = true };
            autoItem.CheckedChanged += (s, e) =>
            {
                config.Enabled = autoItem.Checked;
                config.Save();
                if (config.Enabled) Evaluate(true);
                UpdateStatus();
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Settings…", null, (s, e) => ShowSettings());
            menu.Items.Add(autoItem);
            menu.Items.Add("Open log", null, (s, e) => Log.OpenInEditor());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitApp());

            tray = new NotifyIcon
            {
                Icon = AppIcon.Small,
                Text = "TaskbarSwitch",
                ContextMenuStrip = menu,
                Visible = true
            };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };

            debounce = new System.Windows.Forms.Timer { Interval = 2000 };
            debounce.Tick += (s, e) => { debounce.Stop(); Evaluate(false); };

            poll = new System.Windows.Forms.Timer { Interval = 10000 };
            poll.Tick += (s, e) => Evaluate(false);
            poll.Start();

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            ThreadPool.RegisterWaitForSingleObject(showEvt,
                (st, to) => invoker.BeginInvoke((Action)ShowSettings), null, -1, false);
            ThreadPool.RegisterWaitForSingleObject(exitEvt,
                (st, to) => invoker.BeginInvoke((Action)ExitApp), null, -1, false);

            Log.SessionHeader();
            Evaluate(false);
            if (firstRun) ShowSettings();
        }

        void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            invoker.BeginInvoke((Action)(() => { debounce.Stop(); debounce.Start(); }));
        }

        /// <summary>Checks the monitor setup; applies the matching profile when it changed (or when forced).</summary>
        void Evaluate(bool force)
        {
            var info = Displays.Detect();
            bool ext = info.ExternalConnected;
            bool changed = lastExternal != ext;
            lastExternal = ext;
            lastInfo = info;
            if (changed || info.Details != lastDetails)
            {
                Log.Info($"Display setup: {info} -> {(ext ? "external" : "laptop")} profile\r\n{info.Details}");
                lastDetails = info.Details;
            }
            UpdateStatus();

            if (!config.Enabled || !(changed || force)) return;
            if (Interlocked.Exchange(ref applying, 1) == 1)
            {
                Log.Info("A change is already being applied; will re-check when it finishes");
                recheckAfterApply = true;
                return;
            }
            Log.Info($"Applying {(ext ? "external monitor" : "laptop")} profile ({(force ? "forced" : "display change")})");

            var profile = (ext ? config.Docked : config.Laptop).Clone();
            Task.Run(() =>
            {
                try { TaskbarRegistry.Apply(profile); }
                catch (Exception ex) { Log.Error("Applying taskbar settings failed", ex); }
                finally
                {
                    Interlocked.Exchange(ref applying, 0);
                    // The screens may have changed again while we were busy.
                    if (recheckAfterApply)
                    {
                        recheckAfterApply = false;
                        invoker.BeginInvoke((Action)(() => Evaluate(true)));
                    }
                }
            });
        }

        string StatusText =>
            (lastExternal == true ? "External monitor connected" : "Laptop screen only") +
            (config.Enabled ? "" : " (paused)");

        void UpdateStatus()
        {
            statusItem.Text = StatusText;
            string t = "TaskbarSwitch: " + StatusText;
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
            autoItem.Checked = config.Enabled;
            form?.SetStatus(lastExternal == true, lastInfo.ToString());
        }

        void ShowSettings()
        {
            if (form != null && !form.IsDisposed)
            {
                form.WindowState = FormWindowState.Normal;
                form.Activate();
                return;
            }
            form = new SettingsForm(config);
            form.SetStatus(lastExternal == true, lastInfo.ToString());
            form.Saved += () =>
            {
                config.Save();
                UpdateStatus();
                Evaluate(true);
            };
            form.RestoreRequested += () =>
            {
                config.Enabled = false;
                config.Save();
                UpdateStatus();
                var orig = config.Original.Clone();
                Task.Run(() =>
                {
                    try
                    {
                        if (!Recovery.RestoreOriginal())
                        {
                            Log.Warn("Falling back to the saved settings snapshot");
                            TaskbarRegistry.Apply(orig);
                        }
                    }
                    catch (Exception ex) { Log.Error("Restoring the original taskbar failed", ex); }
                });
            };
            form.ResetRequested += () =>
            {
                config.Enabled = false;
                config.Save();
                UpdateStatus();
                Task.Run(() => Recovery.ResetToWindowsDefaults());
            };
            form.FormClosed += (s, e) => form = null;
            form.Show();
            form.Activate();
        }

        void ExitApp()
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            poll.Stop();
            debounce.Stop();
            form?.Close();
            tray.Visible = false;
            tray.Dispose();
            Log.Info("Exited");
            ExitThread();
        }
    }
}
