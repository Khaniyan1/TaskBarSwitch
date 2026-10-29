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
        int applying;   // 1 while a taskbar change or an update install is in progress
        volatile bool recheckAfterApply;
        string lastDetails;

        readonly System.Windows.Forms.Timer updateTimer;
        readonly ToolStripMenuItem checkUpdateItem;
        readonly ToolStripMenuItem installUpdateItem;
        DateTime lastUpdateCheck = DateTime.MinValue;
        ReleaseInfo availableUpdate;   // found but not installed (when the user wants to be asked first)
        bool updateBusy;

        public TrayApp(EventWaitHandle showEvt, EventWaitHandle exitEvt, bool firstRun, bool justUpdated)
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
            installUpdateItem = new ToolStripMenuItem("Install update", null, (s, e) => InstallAvailableUpdate()) { Visible = false };
            checkUpdateItem = new ToolStripMenuItem("Check for updates", null, (s, e) => CheckForUpdates(true));
            menu.Items.Add(installUpdateItem);
            menu.Items.Add(checkUpdateItem);
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
            tray.BalloonTipClicked += (s, e) => { if (availableUpdate != null) InstallAvailableUpdate(); };

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

            // First check a minute after start, then once a day (looked at hourly, so sleep doesn't skip it).
            updateTimer = new System.Windows.Forms.Timer { Interval = 60 * 1000 };
            updateTimer.Tick += (s, e) =>
            {
                updateTimer.Interval = 60 * 60 * 1000;
                if (DateTime.Now - lastUpdateCheck >= TimeSpan.FromHours(24)) CheckForUpdates(false);
            };
            updateTimer.Start();

            Log.SessionHeader();
            Evaluate(false);
            if (firstRun) ShowSettings();
            if (justUpdated)
                tray.ShowBalloonTip(5000, "TaskbarSwitch updated", "Now running version " + Updater.CurrentVersion + ".", ToolTipIcon.Info);

            // Tell a waiting updater that this version started cleanly (no-op when nobody is waiting).
            var started = new System.Windows.Forms.Timer { Interval = 5000 };
            started.Tick += (s, e) => { started.Stop(); started.Dispose(); Program.Signal(Program.StartedEventName); };
            started.Start();
        }

        // ------------------------------------------------------------- updates

        void CheckForUpdates(bool manual)
        {
            if (updateBusy)
            {
                if (manual) tray.ShowBalloonTip(3000, "TaskbarSwitch", "Already checking for updates.", ToolTipIcon.Info);
                return;
            }
            if (!Installer.IsRunningFromInstallDir())
            {
                Log.Info("Skipping update check: not running from the install folder");
                if (manual)
                    MessageBox.Show("Updates only work for the installed copy of TaskbarSwitch.", "TaskbarSwitch",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            updateBusy = true;
            checkUpdateItem.Enabled = false;
            lastUpdateCheck = DateTime.Now;
            string channel = config.UpdateChannel;
            bool auto = config.AutoUpdate;
            Task.Run(() =>
            {
                ReleaseInfo r = null;
                Exception error = null;
                try { r = Updater.FindUpdate(channel); }
                catch (Exception ex) { error = ex; Log.Warn("Update check failed", ex); }

                invoker.BeginInvoke((Action)(() =>
                {
                    updateBusy = false;
                    checkUpdateItem.Enabled = true;
                    if (error != null)
                    {
                        if (manual) tray.ShowBalloonTip(5000, "TaskbarSwitch", "Couldn't check for updates: " + error.Message, ToolTipIcon.Warning);
                        return;
                    }
                    if (r == null)
                    {
                        if (manual) tray.ShowBalloonTip(3000, "TaskbarSwitch",
                            $"You have the newest {(channel == "beta" ? "beta" : "stable")} version ({Updater.CurrentVersion}).", ToolTipIcon.Info);
                        return;
                    }
                    availableUpdate = r;
                    installUpdateItem.Text = "Install update " + r.Version;
                    installUpdateItem.Visible = true;
                    if (auto) InstallAvailableUpdate();
                    else tray.ShowBalloonTip(10000, "TaskbarSwitch " + r.Version + " is available",
                        "Click here to install it.", ToolTipIcon.Info);
                }));
            });
        }

        void InstallAvailableUpdate()
        {
            var r = availableUpdate;
            if (r == null || updateBusy) return;
            updateBusy = true;
            checkUpdateItem.Enabled = installUpdateItem.Enabled = false;
            Task.Run(() =>
            {
                string path = null;
                try { path = Updater.Download(r); }
                catch (Exception ex) { Log.Warn($"Downloading {r.Tag} failed", ex); }
                invoker.BeginInvoke((Action)(() =>
                {
                    if (path == null)
                    {
                        EndUpdateAttempt();
                        tray.ShowBalloonTip(5000, "TaskbarSwitch", $"Update {r.Version} could not be downloaded or verified. Details are in the log.", ToolTipIcon.Warning);
                        return;
                    }
                    StartSwap(path, r, 0);
                }));
            });
        }

        void StartSwap(string path, ReleaseInfo r, int attempt)
        {
            // Never update in the middle of a taskbar change: take the same slot a change uses.
            if (Interlocked.CompareExchange(ref applying, 1, 0) != 0)
            {
                if (attempt == 0) Log.Info("A taskbar change is in progress; the update waits for it to finish");
                var later = new System.Windows.Forms.Timer { Interval = 5000 };
                later.Tick += (s, e) => { later.Stop(); later.Dispose(); StartSwap(path, r, attempt + 1); };
                later.Start();
                return;
            }

            // The updater signals our exit event when it's running. If it never does, carry on as before.
            if (Updater.Launch(path))
            {
                var giveUp = new System.Windows.Forms.Timer { Interval = 60 * 1000 };
                giveUp.Tick += (s, e) =>
                {
                    giveUp.Stop(); giveUp.Dispose();
                    Log.Warn("The updater didn't take over within 60 s; still running " + Updater.CurrentVersion);
                    ReleaseApplySlot();
                    EndUpdateAttempt();
                };
                giveUp.Start();
            }
            else
            {
                ReleaseApplySlot();
                EndUpdateAttempt();
            }
        }

        void EndUpdateAttempt()
        {
            updateBusy = false;
            checkUpdateItem.Enabled = installUpdateItem.Enabled = true;
        }

        void ReleaseApplySlot()
        {
            Interlocked.Exchange(ref applying, 0);
            if (recheckAfterApply)
            {
                recheckAfterApply = false;
                Evaluate(true);
            }
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
            form.ChannelChanged += () => CheckForUpdates(false);
            form.CheckUpdatesRequested += () => CheckForUpdates(true);
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
            updateTimer.Stop();
            form?.Close();
            tray.Visible = false;
            tray.Dispose();
            Log.Info("Exited");
            ExitThread();
        }
    }
}
