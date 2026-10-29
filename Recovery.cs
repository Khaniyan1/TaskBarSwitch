using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace TaskbarSwitch
{
    /// <summary>
    /// Backups and ways back. Only the exact registry values TaskbarSwitch touches are ever
    /// backed up or restored, so nothing else in Explorer's settings is reverted.
    ///
    ///   backups\original.reg          taskbar as it was before TaskbarSwitch first ran (never overwritten)
    ///   backups\before-*.reg          taskbar right before each change (last 10 kept)
    ///   backups\README.txt            how to recover by hand, even without the app
    ///
    /// The backups folder is kept on uninstall.
    /// </summary>
    static class Recovery
    {
        const string HKCU = "HKEY_CURRENT_USER";

        // Every value TaskbarSwitch can change: (key, value name)
        static readonly (string Key, string Name)[] Touched =
        {
            (TaskbarRegistry.StuckKey, "Settings"),
            (TaskbarRegistry.AdvancedKey, "TaskbarLocation"),
            (TaskbarRegistry.AdvancedKey, "TaskbarAl"),
            (TaskbarRegistry.AdvancedKey, "TaskbarGlomLevel"),
            (TaskbarRegistry.AdvancedKey, "MMTaskbarEnabled"),
            (TaskbarRegistry.AdvancedKey, "MMTaskbarMode"),
            (TaskbarRegistry.AdvancedKey, "ShowTaskViewButton"),
            (TaskbarRegistry.SearchKey, "SearchboxTaskbarMode"),
        };

        public static string BackupDir => Path.Combine(Config.Dir, "backups");
        public static string OriginalFile => Path.Combine(BackupDir, "original.reg");
        const int KeepBeforeBackups = 10;

        /// <summary>First run: save how the taskbar looked before we touched anything.</summary>
        public static void EnsureOriginal()
        {
            try
            {
                Directory.CreateDirectory(BackupDir);
                WriteReadme();
                if (File.Exists(OriginalFile)) return;
                WriteRegFile(OriginalFile);
                Log.Info("Saved original taskbar backup: " + OriginalFile);
            }
            catch (Exception ex) { Log.Error("Could not save the original taskbar backup", ex); }
        }

        /// <summary>
        /// Backups from version 1.0.0 lack TaskbarLocation. Add it from the position recorded
        /// on first run, so "Restore original" also restores the position on newer Windows.
        /// </summary>
        public static void UpgradeOriginal(int originalPosition)
        {
            try
            {
                if (!File.Exists(OriginalFile)) return;
                string text = File.ReadAllText(OriginalFile, Encoding.Unicode);
                if (text.Contains("\"TaskbarLocation\"")) return;
                text = text.TrimEnd() + $"\r\n\r\n[{HKCU}\\{TaskbarRegistry.AdvancedKey}]\r\n" +
                       $"\"TaskbarLocation\"=dword:{originalPosition:x8}\r\n";
                File.WriteAllText(OriginalFile, text, Encoding.Unicode);
                Log.Info($"Added TaskbarLocation={originalPosition} to the original backup");
            }
            catch (Exception ex) { Log.Warn("Could not update the original backup", ex); }
        }

        /// <summary>Saves the current state before a change. Returns false if the backup failed.</summary>
        public static bool BackupBeforeChange()
        {
            try
            {
                Directory.CreateDirectory(BackupDir);
                if (!File.Exists(OriginalFile)) EnsureOriginal();
                string f = Path.Combine(BackupDir, $"before-{DateTime.Now:yyyyMMdd-HHmmss}.reg");
                WriteRegFile(f);

                foreach (var old in new DirectoryInfo(BackupDir).GetFiles("before-*.reg")
                             .OrderByDescending(x => x.Name).Skip(KeepBeforeBackups))
                    old.Delete();
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Backup before change failed", ex);
                return false;
            }
        }

        /// <summary>Puts back exactly the values that were there before TaskbarSwitch first ran.</summary>
        public static bool RestoreOriginal()
        {
            if (!File.Exists(OriginalFile))
            {
                Log.Warn("Restore requested but no original backup exists at " + OriginalFile);
                return false;
            }
            try
            {
                BackupBeforeChange();
                Log.Info("Restoring original taskbar from " + OriginalFile);
                var psi = new ProcessStartInfo("reg.exe", $"import \"{OriginalFile}\"")
                {
                    CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardError = true, RedirectStandardOutput = true
                };
                using (var p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(10000);
                    if (p.ExitCode != 0)
                    {
                        Log.Warn($"reg import failed (exit {p.ExitCode}): {err.Trim()}");
                        return false;
                    }
                }
                TaskbarRegistry.RestartExplorer();
                Log.Info("Original restored. Taskbar now: " + TaskbarRegistry.ReadCurrent());
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Restoring the original taskbar failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Back to Windows' own defaults: deletes the values so Explorer recreates them itself.
        /// (Deleting StuckRects3\Settings is the standard way to reset taskbar position and auto-hide.)
        /// </summary>
        public static bool ResetToWindowsDefaults()
        {
            try
            {
                BackupBeforeChange();
                Log.Info("Resetting taskbar to Windows defaults");
                foreach (var (key, name) in Touched)
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(key, true))
                        k?.DeleteValue(name, false);
                }
                TaskbarRegistry.RestartExplorer();
                Log.Info("Reset done. Taskbar now: " + TaskbarRegistry.ReadCurrent());
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Reset to Windows defaults failed", ex);
                return false;
            }
        }

        public static void OpenBackupFolder()
        {
            try
            {
                Directory.CreateDirectory(BackupDir);
                Process.Start("explorer.exe", $"\"{BackupDir}\"");
            }
            catch (Exception ex) { Log.Warn("Could not open the backups folder", ex); }
        }

        /// <summary>
        /// Writes a .reg file with just our values. Values that don't exist are written as "name"=-
        /// so importing the file also removes anything we added.
        /// </summary>
        static void WriteRegFile(string file)
        {
            var sb = new StringBuilder();
            sb.Append("Windows Registry Editor Version 5.00\r\n");
            foreach (var group in Touched.GroupBy(t => t.Key))
            {
                sb.Append($"\r\n[{HKCU}\\{group.Key}]\r\n");
                using (var k = Registry.CurrentUser.OpenSubKey(group.Key))
                {
                    foreach (var (_, name) in group)
                    {
                        object v = k?.GetValue(name);
                        if (v is byte[] b)
                            sb.Append($"\"{name}\"=hex:{string.Join(",", b.Select(x => x.ToString("x2")))}\r\n");
                        else if (v is int i)
                            sb.Append($"\"{name}\"=dword:{unchecked((uint)i):x8}\r\n");
                        else if (v == null)
                            sb.Append($"\"{name}\"=-\r\n");
                        else
                            Log.Warn($"Unexpected type {v.GetType().Name} for {group.Key}\\{name}; left out of backup");
                    }
                }
            }
            File.WriteAllText(file, sb.ToString(), Encoding.Unicode);
        }

        static void WriteReadme()
        {
            string f = Path.Combine(BackupDir, "README.txt");
            if (File.Exists(f)) return;
            File.WriteAllText(f,
@"TaskbarSwitch backups
=====================

These files contain ONLY the taskbar values TaskbarSwitch changes
(position incl. TaskbarLocation, auto-hide, alignment, combine buttons, multi-display, search, task view).
Nothing else in your registry is touched.

original.reg    How your taskbar was before TaskbarSwitch ever ran.
before-*.reg    How it was right before each change (the last 10).

To recover by hand, even if TaskbarSwitch is uninstalled or won't start:
  1. Exit TaskbarSwitch (right-click the tray icon > Exit), so it doesn't switch again.
  2. Double-click original.reg (or a before-*.reg) and confirm.
  3. Sign out and back in, or restart Explorer from Task Manager.

To go back to Windows' own defaults instead, run:
  ""%LOCALAPPDATA%\Programs\TaskbarSwitch\TaskbarSwitch.exe"" /reset

This folder is kept when you uninstall TaskbarSwitch. Delete it whenever you like.
");
        }
    }
}
