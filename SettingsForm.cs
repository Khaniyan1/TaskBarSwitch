using System;
using System.Drawing;
using System.Windows.Forms;

namespace TaskbarSwitch
{
    /// <summary>A drop-down whose items map to registry values.</summary>
    class Choice
    {
        public readonly ComboBox Box;
        readonly int[] values;

        public Choice(string[] labels, int[] values)
        {
            this.values = values;
            Box = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 300,
                Margin = new Padding(3, 2, 3, 6)
            };
            Box.Items.AddRange(labels);
            Box.SelectedIndex = 0;
        }

        public int Value
        {
            get => values[Math.Max(0, Box.SelectedIndex)];
            set { int i = Array.IndexOf(values, value); Box.SelectedIndex = i >= 0 ? i : 0; }
        }
    }

    /// <summary>The controls for one setup (laptop only / external connected).</summary>
    class ProfileEditor : GroupBox
    {
        readonly Choice position = new Choice(
            new[] { "Bottom", "Left", "Top", "Right" }, new[] { 3, 0, 1, 2 });
        readonly Choice alignment = new Choice(
            new[] { "Center", "Left / top" }, new[] { 1, 0 });
        readonly Choice combine = new Choice(
            new[] { "Always", "When taskbar is full", "Never" }, new[] { 0, 1, 2 });
        readonly Choice multiMode = new Choice(
            new[] { "All taskbars", "Main taskbar and taskbar where window is open", "Taskbar where window is open" },
            new[] { 0, 1, 2 });
        readonly Choice search = new Choice(
            new[] { "Hide", "Search icon only", "Search icon and label", "Search box" }, new[] { 0, 1, 3, 2 });
        readonly CheckBox autoHide = new CheckBox { Text = "Automatically hide the taskbar", AutoSize = true };
        readonly CheckBox allDisplays = new CheckBox { Text = "Show my taskbar on all displays", AutoSize = true };
        readonly CheckBox taskView = new CheckBox { Text = "Show Task view button", AutoSize = true };
        readonly Label multiLabel;
        readonly string baseTitle;

        public ProfileEditor(string title)
        {
            baseTitle = title;
            Text = title;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(10, 6, 10, 10);
            Margin = new Padding(6);

            var t = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill
            };

            AddLabeled(t, "Taskbar position", position.Box);
            AddCheck(t, autoHide);
            AddLabeled(t, "Taskbar alignment", alignment.Box);
            AddLabeled(t, "Combine taskbar buttons and hide labels", combine.Box);
            AddLabeled(t, "Search", search.Box);
            AddCheck(t, taskView);
            AddCheck(t, allDisplays);
            multiLabel = AddLabeled(t, "When using multiple displays, show my taskbar apps on", multiMode.Box);

            var capture = new Button
            {
                Text = "Copy current Windows settings",
                AutoSize = true,
                Margin = new Padding(3, 10, 3, 0)
            };
            capture.Click += (s, e) => LoadProfile(TaskbarRegistry.ReadCurrent());
            t.Controls.Add(capture);

            allDisplays.CheckedChanged += (s, e) => UpdateEnabled();
            Controls.Add(t);
        }

        static Label AddLabeled(TableLayoutPanel t, string label, Control c)
        {
            var l = new Label { Text = label, AutoSize = true, Margin = new Padding(3, 8, 3, 2) };
            t.Controls.Add(l);
            t.Controls.Add(c);
            return l;
        }

        static void AddCheck(TableLayoutPanel t, CheckBox c)
        {
            c.Margin = new Padding(3, 6, 3, 2);
            t.Controls.Add(c);
        }

        void UpdateEnabled()
        {
            multiMode.Box.Enabled = allDisplays.Checked;
            multiLabel.Enabled = allDisplays.Checked;
        }

        public void LoadProfile(TaskbarProfile p)
        {
            position.Value = p.Position;
            autoHide.Checked = p.AutoHide;
            alignment.Value = p.Alignment;
            combine.Value = p.Combine;
            search.Value = p.SearchMode;
            taskView.Checked = p.TaskView;
            allDisplays.Checked = p.ShowOnAllDisplays;
            multiMode.Value = p.MultiMode;
            UpdateEnabled();
        }

        public void SaveInto(TaskbarProfile p)
        {
            p.Position = position.Value;
            p.AutoHide = autoHide.Checked;
            p.Alignment = alignment.Value;
            p.Combine = combine.Value;
            p.SearchMode = search.Value;
            p.TaskView = taskView.Checked;
            p.ShowOnAllDisplays = allDisplays.Checked;
            p.MultiMode = multiMode.Value;
        }

        public void SetActive(bool active)
        {
            Text = active ? baseTitle + " (in use now)" : baseTitle;
        }
    }

    class SettingsForm : Form
    {
        readonly Config config;
        readonly ProfileEditor laptopEd = new ProfileEditor("Laptop screen only");
        readonly ProfileEditor dockedEd = new ProfileEditor("External monitor connected");
        readonly CheckBox enabledBox = new CheckBox { Text = "Switch automatically when monitors change", AutoSize = true };
        readonly CheckBox autostartBox = new CheckBox { Text = "Start with Windows", AutoSize = true };
        readonly Label statusLabel = new Label { AutoSize = true };
        readonly Label detailLabel = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };

        public event Action Saved;
        public event Action RestoreRequested;
        public event Action ResetRequested;

        public SettingsForm(Config cfg)
        {
            config = cfg;
            Text = "TaskbarSwitch";
            Font = SystemFonts.MessageBoxFont;
            Icon = AppIcon.Large;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            statusLabel.Font = new Font(Font, FontStyle.Bold);
            statusLabel.Margin = new Padding(6, 0, 6, 0);
            detailLabel.Margin = new Padding(6, 2, 6, 6);

            var root = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill
            };
            root.Controls.Add(statusLabel);
            root.Controls.Add(detailLabel);

            var cols = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
            cols.Controls.Add(laptopEd, 0, 0);
            cols.Controls.Add(dockedEd, 1, 0);
            root.Controls.Add(cols);

            enabledBox.Margin = new Padding(9, 8, 3, 2);
            autostartBox.Margin = new Padding(9, 4, 3, 2);
            root.Controls.Add(enabledBox);
            root.Controls.Add(autostartBox);

            var note = new Label
            {
                Text = "Applying changes restarts the taskbar for a second; open File Explorer windows will close.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(9, 10, 3, 4)
            };
            root.Controls.Add(note);

            var bottom = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 6, 3, 0)
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            LinkLabel Link(string text, Action a, int left)
            {
                var l = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(left, 8, 3, 3) };
                l.LinkClicked += (s, e) => a();
                return l;
            }
            var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left };
            links.Controls.Add(Link("Restore original taskbar…", OnRestore, 6));
            links.Controls.Add(Link("Reset to Windows defaults…", OnReset, 14));
            links.Controls.Add(Link("Backups", Recovery.OpenBackupFolder, 14));
            links.Controls.Add(Link("Log", Log.OpenInEditor, 14));
            bottom.Controls.Add(links, 0, 0);

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            var save = new Button { Text = "Save and apply", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
            var cancel = new Button { Text = "Close", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
            save.Click += (s, e) => OnSave();
            cancel.Click += (s, e) => Close();
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            bottom.Controls.Add(buttons, 1, 0);
            root.Controls.Add(bottom);

            Controls.Add(root);
            AcceptButton = save;
            CancelButton = cancel;

            laptopEd.LoadProfile(config.Laptop);
            dockedEd.LoadProfile(config.Docked);
            enabledBox.Checked = config.Enabled;
            autostartBox.Checked = Installer.IsAutostart();
        }

        public void SetStatus(bool externalConnected, string detail)
        {
            statusLabel.Text = "Right now: " + (externalConnected ? "External monitor connected" : "Laptop screen only");
            detailLabel.Text = "Detected " + detail;
            laptopEd.SetActive(!externalConnected);
            dockedEd.SetActive(externalConnected);
        }

        void OnSave()
        {
            laptopEd.SaveInto(config.Laptop);
            dockedEd.SaveInto(config.Docked);
            config.Enabled = enabledBox.Checked;
            try { Installer.SetAutostart(autostartBox.Checked); }
            catch (Exception ex) { Log.Error("Changing Start with Windows failed", ex); }
            Log.Info("Settings saved\r\n    laptop: " + config.Laptop + "\r\n    docked: " + config.Docked +
                     $"\r\n    auto={config.Enabled} startWithWindows={autostartBox.Checked}");
            Saved?.Invoke();
        }

        void OnRestore()
        {
            var r = MessageBox.Show(this,
                "Put the taskbar back the way it was before TaskbarSwitch was first started?\n\n" +
                "Automatic switching will be turned off. You can turn it on again here.",
                "TaskbarSwitch", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r != DialogResult.OK) return;
            enabledBox.Checked = false;
            RestoreRequested?.Invoke();
        }

        void OnReset()
        {
            var r = MessageBox.Show(this,
                "Reset the taskbar to Windows' own default settings?\n\n" +
                "This removes the taskbar values TaskbarSwitch can change, so Windows recreates them. " +
                "A backup of the current state is saved first.\n\n" +
                "Automatic switching will be turned off. You can turn it on again here.",
                "TaskbarSwitch", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r != DialogResult.OK) return;
            enabledBox.Checked = false;
            ResetRequested?.Invoke();
        }
    }
}
