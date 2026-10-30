using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TaskbarSwitch
{
    class Config
    {
        public bool Enabled = true;
        public string UpdateChannel = "stable";   // "stable" or "beta"
        public bool AutoUpdate = true;            // false: just tell me
        public bool GatherWindows = true;         // move stranded windows to the laptop screen on disconnect
        public bool RestoreWindows = true;        // put them back when the monitor is reconnected
        public TaskbarProfile Laptop;
        public TaskbarProfile Docked;
        public TaskbarProfile Original;   // taskbar as it was before first run

        public static string Dir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarSwitch");
        static string FilePath => Path.Combine(Dir, "settings.ini");

        public static Config TryLoadExisting() => File.Exists(FilePath) ? Load() : null;

        public static Config Load()
        {
            Directory.CreateDirectory(Dir);
            var cur = TaskbarRegistry.ReadCurrent();

            if (!File.Exists(FilePath))
            {
                // First run: remember the current taskbar, and start both setups from it
                // with Jacob-friendly defaults for position and auto-hide.
                Recovery.EnsureOriginal();
                var c = Fresh(cur);
                c.Save();
                Log.Info("First run. Original taskbar: " + cur);
                return c;
            }

            try
            {
                return Parse(cur);
            }
            catch (Exception ex)
            {
                Log.Error("settings.ini could not be read; keeping a copy as settings.bad.ini and starting fresh", ex);
                try { File.Copy(FilePath, Path.Combine(Dir, "settings.bad.ini"), true); File.Delete(FilePath); }
                catch (Exception ex2) { Log.Warn("Could not move the bad settings file", ex2); }
                var c = Fresh(cur);
                try { c.Save(); } catch (Exception ex3) { Log.Error("Could not write new settings.ini", ex3); }
                return c;
            }
        }

        static Config Fresh(TaskbarProfile cur)
        {
            var c = new Config { Original = cur.Clone(), Laptop = cur.Clone(), Docked = cur.Clone() };
            c.Laptop.Position = 3; c.Laptop.AutoHide = true;
            c.Docked.Position = 0; c.Docked.AutoHide = false;
            return c;
        }

        static Config Parse(TaskbarProfile cur)
        {
            var d = File.ReadAllLines(FilePath)
                .Select(l => l.Split(new[] { '=' }, 2))
                .Where(a => a.Length == 2)
                .GroupBy(a => a[0].Trim())
                .ToDictionary(g => g.Key, g => g.Last()[1].Trim());

            return new Config
            {
                Enabled = !d.TryGetValue("Enabled", out var e) || e != "0",
                UpdateChannel = d.TryGetValue("UpdateChannel", out var ch) && ch == "beta" ? "beta" : "stable",
                AutoUpdate = !d.TryGetValue("AutoUpdate", out var au) || au != "0",
                GatherWindows = !d.TryGetValue("GatherWindows", out var gw) || gw != "0",
                RestoreWindows = !d.TryGetValue("RestoreWindows", out var rw) || rw != "0",
                Laptop = TaskbarProfile.FromDict(d, "Laptop", cur),
                Docked = TaskbarProfile.FromDict(d, "Docked", cur),
                Original = TaskbarProfile.FromDict(d, "Original", cur)
            };
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            var lines = new List<string>
            {
                "Enabled=" + (Enabled ? "1" : "0"),
                "UpdateChannel=" + UpdateChannel,
                "AutoUpdate=" + (AutoUpdate ? "1" : "0"),
                "GatherWindows=" + (GatherWindows ? "1" : "0"),
                "RestoreWindows=" + (RestoreWindows ? "1" : "0")
            };
            lines.AddRange(Laptop.ToLines("Laptop"));
            lines.AddRange(Docked.ToLines("Docked"));
            lines.AddRange(Original.ToLines("Original"));
            File.WriteAllLines(FilePath, lines);
        }
    }
}
