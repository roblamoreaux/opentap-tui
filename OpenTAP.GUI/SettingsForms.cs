using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows.Forms;

namespace OpenTap.Gui
{
    /// <summary> Edits one ComponentSettings object (Engine, Results, GUI settings, ...). Saved when closed, like the TUI. </summary>
    class ComponentSettingsForm : Form
    {
        readonly ComponentSettings settings;

        public ComponentSettingsForm(ComponentSettings settings, string title)
        {
            this.settings = settings;
            Text = title;
            Width = 600;
            Height = 550;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;

            var grid = new AnnotationPropertyGrid { Dock = DockStyle.Fill };
            grid.LoadProperties(settings);
            Controls.Add(grid);
            Controls.Add(Dialogs.CloseButtonBar(this));
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try
            {
                settings.Save();
            }
            catch (Exception ex)
            {
                GuiAction.Log.Error("Could not save settings: {0}", ex.Message);
                GuiAction.Log.Debug(ex);
            }
            base.OnFormClosed(e);
        }
    }

    /// <summary>
    /// Edits a settings list such as DUTs, Instruments or Connections: list on the left, properties on the right.
    /// Port of the TUI's ResourceSettingsWindow / ConnectionSettingsWindow.
    /// </summary>
    class ListSettingsForm : Form
    {
        readonly IList list;
        readonly ListBox listBox;
        readonly AnnotationPropertyGrid details;
        readonly string title;

        public ListSettingsForm(string title, IList list)
        {
            this.title = title;
            this.list = list;
            Text = title;
            Width = 850;
            Height = 550;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;

            listBox = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            listBox.SelectedIndexChanged += (s, e) => details.LoadProperties(listBox.SelectedIndex >= 0 ? list[listBox.SelectedIndex] : null);
            listBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };

            var add = new Button { Text = "Add...", AutoSize = true };
            add.Click += (s, e) => AddNew();
            var remove = new Button { Text = "Remove", AutoSize = true };
            remove.Click += (s, e) => RemoveSelected();
            var up = new Button { Text = "▲", Width = 32 };
            up.Click += (s, e) => MoveItem(-1);
            var down = new Button { Text = "▼", Width = 32 };
            down.Click += (s, e) => MoveItem(1);
            var listButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
            listButtons.Controls.AddRange(new Control[] { add, remove, up, down });

            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            Load += (s, e) => split.SplitterDistance = 260;
            split.Panel1.Controls.Add(listBox);
            split.Panel1.Controls.Add(listButtons);

            details = new AnnotationPropertyGrid { Dock = DockStyle.Fill };
            details.PropertiesChanged += RefreshNames;
            split.Panel2.Controls.Add(details);

            Controls.Add(split);
            Controls.Add(Dialogs.CloseButtonBar(this));

            RefreshNames();
            if (list.Count > 0) listBox.SelectedIndex = 0;
        }

        static string ItemName(object item)
        {
            if (item is IResource r)
            {
                if (string.IsNullOrWhiteSpace(r.Name))
                    return "Unnamed: " + (TypeData.GetTypeData(r).GetDisplayAttribute()?.GetFullName() ?? r.GetType().FullName);
                return r.Name;
            }
            return item?.ToString() ?? "";
        }

        void RefreshNames()
        {
            var selected = listBox.SelectedIndex;
            listBox.BeginUpdate();
            for (int i = 0; i < list.Count; i++)
            {
                var name = ItemName(list[i]);
                if (i < listBox.Items.Count)
                {
                    if ((string)listBox.Items[i] != name) listBox.Items[i] = name;
                }
                else listBox.Items.Add(name);
            }
            while (listBox.Items.Count > list.Count)
                listBox.Items.RemoveAt(listBox.Items.Count - 1);
            listBox.EndUpdate();
            if (selected < listBox.Items.Count) listBox.SelectedIndex = selected;
        }

        void AddNew()
        {
            var elementType = CollectionEditorForm.GetEnumerableElementType(list.GetType());
            using var picker = new AddPluginDialog(TypeData.FromType(elementType), "Add " + title, null);
            if (picker.ShowDialog(this) != DialogResult.OK || picker.PluginType == null) return;
            try
            {
                list.Add(picker.PluginType.CreateInstance());
                RefreshNames();
                listBox.SelectedIndex = list.Count - 1;
            }
            catch (Exception ex)
            {
                GuiAction.Log.Error(ex);
                MessageBox.Show(this, ex.Message, "Add " + title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void RemoveSelected()
        {
            var index = listBox.SelectedIndex;
            if (index < 0) return;
            if (MessageBox.Show(this, $"Remove '{ItemName(list[index])}'?", "Remove", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            list.RemoveAt(index);
            RefreshNames();
            if (list.Count > 0)
                listBox.SelectedIndex = Math.Min(index, list.Count - 1);
            else
                details.LoadProperties(null);
        }

        void MoveItem(int offset)
        {
            var index = listBox.SelectedIndex;
            var target = index + offset;
            if (index < 0 || target < 0 || target >= list.Count) return;
            var item = list[index];
            list.RemoveAt(index);
            list.Insert(target, item);
            RefreshNames();
            listBox.SelectedIndex = target;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try
            {
                ComponentSettings.SaveAllCurrentSettings();
            }
            catch (Exception ex)
            {
                GuiAction.Log.Error("Could not save settings: {0}", ex.Message);
                GuiAction.Log.Debug(ex);
            }
            base.OnFormClosed(e);
        }
    }

    /// <summary> Settings profiles for a settings group (e.g. Bench). Port of the TUI's SettingsProfileWindow. </summary>
    class ProfilesForm : Form
    {
        readonly string group;
        readonly ListBox listBox;
        readonly Button deleteButton;
        string settingsDir;
        List<string> profiles = new List<string>();
        string currentProfile;
        bool loading;

        public ProfilesForm(string group)
        {
            this.group = group;
            Text = group + " Profiles";
            Width = 480;
            Height = 360;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;

            listBox = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            listBox.SelectedIndexChanged += (s, e) => SelectProfile();

            var newButton = new Button { Text = "New Profile...", Width = 130 };
            newButton.Click += (s, e) => NewProfile();
            deleteButton = new Button { Text = "Delete Profile", Width = 130 };
            deleteButton.Click += (s, e) => DeleteProfile();
            var exportButton = new Button { Text = "Export Profile...", Width = 130 };
            exportButton.Click += (s, e) => ExportProfile();
            var importButton = new Button { Text = "Import Profile...", Width = 130 };
            importButton.Click += (s, e) => ImportProfile();
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, Width = 145, Padding = new Padding(4) };
            buttons.Controls.AddRange(new Control[] { newButton, deleteButton, exportButton, importButton });

            var hint = new Label { Dock = DockStyle.Top, Text = "Select a profile to make it active.", Padding = new Padding(4), Height = 24 };

            Controls.Add(listBox);
            Controls.Add(buttons);
            Controls.Add(hint);
            Controls.Add(Dialogs.CloseButtonBar(this));

            LoadProfiles();
        }

        void LoadProfiles()
        {
            loading = true;
            try
            {
                settingsDir = ComponentSettings.GetSettingsDirectory(group, false);
                if (!Directory.Exists(settingsDir))
                    Directory.CreateDirectory(settingsDir);
                profiles = Directory.GetDirectories(settingsDir).Select(Path.GetFileName).ToList();
                if (!profiles.Any())
                    profiles.Add("Default");
                currentProfile = ComponentSettings.GetSettingsDirectory(group)
                    .Substring(Path.Combine(ComponentSettings.SettingsDirectoryRoot, group).Length + 1);

                listBox.Items.Clear();
                foreach (var p in profiles)
                    listBox.Items.Add(p);
                listBox.SelectedIndex = profiles.IndexOf(currentProfile);
                deleteButton.Enabled = profiles.Count > 1;
            }
            finally
            {
                loading = false;
            }
        }

        void SelectProfile()
        {
            if (loading || listBox.SelectedIndex < 0) return;
            var profile = profiles[listBox.SelectedIndex];
            if (profile == currentProfile) return;
            currentProfile = profile;
            ComponentSettings.SetSettingsProfile(group, Path.Combine(settingsDir, currentProfile));
            GuiAction.Log.Info("Switched {0} profile to '{1}'.", group, currentProfile);
        }

        void NewProfile()
        {
            var name = Dialogs.Prompt(this, "New Profile", "Profile name:");
            if (string.IsNullOrWhiteSpace(name)) return;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show(this, "The profile name contains characters that are not allowed in a folder name.", "New Profile", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var path = Path.Combine(settingsDir, name);
            Directory.CreateDirectory(path);
            ComponentSettings.SetSettingsProfile(group, path);
            LoadProfiles();
        }

        void DeleteProfile()
        {
            if (MessageBox.Show(this, $"Delete the profile '{currentProfile}' and all its settings files?", "Delete Profile",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            var toDelete = currentProfile;
            ComponentSettings.SetSettingsProfile(group, Path.Combine(settingsDir, profiles.FirstOrDefault(p => p != toDelete) ?? "Default"));
            try
            {
                Directory.Delete(Path.Combine(settingsDir, toDelete), true);
            }
            catch (Exception ex)
            {
                GuiAction.Log.Warning("Could not delete profile folder: {0}", ex.Message);
            }
            LoadProfiles();
        }

        void ExportProfile()
        {
            using var dlg = new SaveFileDialog { Title = "Export Profile", Filter = "Zip files (*.zip)|*.zip", FileName = currentProfile + ".zip" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
                ZipFile.CreateFromDirectory(Path.Combine(settingsDir, currentProfile), dlg.FileName);
                GuiAction.Log.Info("Exported profile '{0}' to '{1}'.", currentProfile, dlg.FileName);
            }
            catch (Exception ex)
            {
                GuiAction.Log.Error(ex.Message);
                MessageBox.Show(this, ex.Message, "Export Profile", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void ImportProfile()
        {
            using var dlg = new OpenFileDialog { Title = "Import Profile", Filter = "Zip files (*.zip)|*.zip|All files (*.*)|*.*" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                ZipFile.ExtractToDirectory(dlg.FileName, Path.Combine(settingsDir, Path.GetFileNameWithoutExtension(dlg.FileName)));
                LoadProfiles();
            }
            catch (Exception ex)
            {
                GuiAction.Log.Error(ex.Message);
                MessageBox.Show(this, ex.Message, "Import Profile", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    static class Dialogs
    {
        public static Control CloseButtonBar(Form form)
        {
            var close = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(4) };
            bar.Controls.Add(close);
            form.AcceptButton = close;
            form.CancelButton = close;
            return bar;
        }

        /// <summary> Simple one-line text prompt. Returns null when cancelled. </summary>
        public static string Prompt(IWin32Window owner, string title, string label, string initial = "")
        {
            using var form = new Form
            {
                Text = title, Width = 380, Height = 150, FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
            };
            var text = new TextBox { Left = 12, Top = 32, Width = 340, Text = initial };
            var lbl = new Label { Left = 12, Top = 10, AutoSize = true, Text = label };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 196, Top = 70, Width = 75 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 277, Top = 70, Width = 75 };
            form.Controls.AddRange(new Control[] { lbl, text, ok, cancel });
            form.AcceptButton = ok;
            form.CancelButton = cancel;
            return form.ShowDialog(owner) == DialogResult.OK ? text.Text.Trim() : null;
        }
    }
}
