using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TaskbarSwitch
{
    static class Installer
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TaskbarSwitch";
        const string AppName = "TaskbarSwitch";

        public static string InstallDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
        public static string InstalledExe => Path.Combine(InstallDir, AppName + ".exe");
        static string StartMenuShortcut => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

        public static bool IsRunningFromInstallDir() =>
            string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(InstalledExe),
                StringComparison.OrdinalIgnoreCase);

        public static bool Install()
        {
            try
            {
                Directory.CreateDirectory(InstallDir);
                CopyWithRetry(Application.ExecutablePath, InstalledExe);
                SetAutostart(true);
                CreateShortcut();

                string version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
                using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    k.SetValue("DisplayName", AppName);
                    k.SetValue("DisplayVersion", version);
                    k.SetValue("DisplayIcon", InstalledExe);
                    k.SetValue("InstallLocation", InstallDir);
                    k.SetValue("UninstallString", $"\"{InstalledExe}\" /uninstall");
                    k.SetValue("Publisher", AppName);
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
                }
                Log.Info("Installed " + version + " to " + InstallDir);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Installation failed", ex);
                MessageBox.Show("Installation failed:\n\n" + ex.Message, AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        public static void LaunchInstalled(string args)
        {
            try { Process.Start(InstalledExe, args); }
            catch (Exception ex) { Log.Error("Could not start the installed copy", ex); }
        }

        public static void Uninstall()
        {
            var r = MessageBox.Show(
                "Uninstall TaskbarSwitch?\n\nYes: uninstall and put the taskbar back the way it was before TaskbarSwitch.\n" +
                "No: uninstall and keep the taskbar as it is now.\n\n" +
                "Your taskbar backups are kept either way, in " + Recovery.BackupDir,
                AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return;

            Program.Signal(Program.ExitEventName);
            Thread.Sleep(1500);

            if (r == DialogResult.Yes)
            {
                try
                {
                    if (!Recovery.RestoreOriginal())
                    {
                        var cfg = Config.TryLoadExisting();
                        if (cfg?.Original != null) TaskbarRegistry.Apply(cfg.Original);
                    }
                }
                catch (Exception ex) { Log.Error("Restoring the original taskbar during uninstall failed", ex); }
            }

            Log.Info("Uninstalling");
            try { SetAutostart(false); } catch (Exception ex) { Log.Warn("Removing autostart failed", ex); }
            try { if (File.Exists(StartMenuShortcut)) File.Delete(StartMenuShortcut); }
            catch (Exception ex) { Log.Warn("Removing Start menu shortcut failed", ex); }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); }
            catch (Exception ex) { Log.Warn("Removing uninstall entry failed", ex); }
            // Settings and logs go last, so everything above is still logged. Backups are kept.
            foreach (var name in new[] { "settings.ini", "settings.bad.ini", "log.txt", "log.old.txt" })
            {
                try { File.Delete(Path.Combine(Config.Dir, name)); } catch { }
            }

            // The exe can't delete itself while running; remove the folder right after we exit.
            try
            {
                var psi = new ProcessStartInfo("cmd.exe",
                    $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{InstallDir}\"")
                { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() };
                Process.Start(psi);
            }
            catch { /* log folder is already gone */ }

            MessageBox.Show("TaskbarSwitch has been uninstalled.\n\nTaskbar backups (with a README on how to use them) are still in:\n" +
                Recovery.BackupDir, AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static bool IsAutostart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k?.GetValue(AppName) != null;
        }

        public static void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(AppName, $"\"{Application.ExecutablePath}\"");
                else k.DeleteValue(AppName, false);
            }
        }

        public static void FixAutostartPath()
        {
            // If installed, make sure autostart points at the installed copy.
            if (IsRunningFromInstallDir() && IsAutostart()) SetAutostart(true);
        }

        static void CopyWithRetry(string src, string dst)
        {
            if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) return;
            for (int i = 0; ; i++)
            {
                try { File.Copy(src, dst, true); return; }
                catch (IOException) when (i < 10) { Thread.Sleep(500); }
            }
        }

        static void CreateShortcut()
        {
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                object shell = Activator.CreateInstance(t);
                object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                    new object[] { StartMenuShortcut });
                var lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { InstalledExe });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { InstallDir });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk,
                    new object[] { "Taskbar settings per monitor setup" });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { InstalledExe + ",0" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            }
            catch (Exception ex) { Log.Warn("Creating the Start menu shortcut failed", ex); }
        }
    }
}
