using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace OpenTap.Gui
{
    /// <summary>
    /// The list part of the log panel: a virtual list view with time, level, source and message columns, colored by
    /// level, that can be filtered by level, source and text, highlight search matches and follow the newest line.
    /// </summary>
    class LogView : ListView
    {
        readonly List<LogEntry> all = new List<LogEntry>();
        readonly List<LogEntry> visible = new List<LogEntry>();
        readonly HashSet<string> sources = new HashSet<string>();
        readonly Timer timer = new Timer { Interval = 200 };
        long nextSequence;

        /// <summary> Levels to show. </summary>
        public readonly HashSet<LogEventType> Levels = new HashSet<LogEventType>
            { LogEventType.Error, LogEventType.Warning, LogEventType.Information, LogEventType.Debug };

        /// <summary> Sources the user unticked. Sources seen for the first time are shown. </summary>
        public readonly HashSet<string> HiddenSources = new HashSet<string>();

        /// <summary> Only show entries whose message or source contains this text (empty = all). </summary>
        public string FilterText = "";

        /// <summary> Entries containing this text are highlighted (the Find box). </summary>
        public string HighlightText = "";

        /// <summary> Keep the last line visible as new messages arrive. </summary>
        public bool AutoScroll = true;

        /// <summary> Raised when the number of shown/total entries changes. </summary>
        public event Action CountsChanged;

        public IEnumerable<string> Sources => sources.OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        public int TotalCount => all.Count;
        public int VisibleCount => visible.Count;

        public LogView()
        {
            View = View.Details;
            VirtualMode = true;
            FullRowSelect = true;
            HideSelection = false;
            DoubleBuffered = true;
            Font = new Font(FontFamily.GenericMonospace, 9f);
            Columns.Add("Time", 100);
            Columns.Add("Level", 70);
            Columns.Add("Source", 120);
            Columns.Add("Message", 900);
            RetrieveVirtualItem += OnRetrieveVirtualItem;

            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Copy", null, (s, e) => CopySelected()) { ShortcutKeyDisplayString = "Ctrl+C" });
            menu.Items.Add(new ToolStripMenuItem("Select All", null, (s, e) => SelectAll()) { ShortcutKeyDisplayString = "Ctrl+A" });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Clear Log", null, (s, e) => ClearLog());
            ContextMenuStrip = menu;

            timer.Tick += (s, e) => PullNewEntries();
            timer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }

        public void ClearLog()
        {
            all.Clear();
            visible.Clear();
            SelectedIndices.Clear();
            VirtualListSize = 0;
            Invalidate();
            CountsChanged?.Invoke();
        }

        public void SelectAll()
        {
            for (int i = 0; i < visible.Count; i++)
                SelectedIndices.Add(i);
        }

        public void CopySelected()
        {
            if (SelectedIndices.Count == 0) return;
            var sb = new StringBuilder();
            foreach (int i in SelectedIndices)
                if (i < visible.Count) sb.AppendLine(Format(visible[i]));
            Clipboard.SetText(sb.ToString());
        }

        static string Format(LogEntry e) => $"{e.Time:HH:mm:ss.fff} ; {e.Source,-12} ; {LevelName(e.Level),-7} ; {e.Message}";

        static string LevelName(LogEventType level) => level switch
        {
            LogEventType.Information => "Info",
            _ => level.ToString()
        };

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C) { CopySelected(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.A) { SelectAll(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        static bool Contains(LogEntry e, string text) =>
            (e.Message?.IndexOf(text, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
            || (e.Source?.IndexOf(text, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;

        bool Matches(LogEntry e) =>
            Levels.Contains(e.Level)
            && !HiddenSources.Contains(e.Source ?? "")
            && (string.IsNullOrEmpty(FilterText) || Contains(e, FilterText));

        /// <summary> Re-applies the level, source and text filters, keeping the selected entry selected if it is still shown. </summary>
        public void ApplyFilter()
        {
            var selected = SelectedIndices.Count > 0 && SelectedIndices[0] < visible.Count ? visible[SelectedIndices[0]] : null;
            var matching = all.Where(Matches).ToList();
            SelectedIndices.Clear();
            visible.Clear();
            visible.AddRange(matching);
            VirtualListSize = visible.Count;
            var index = selected == null ? -1 : visible.IndexOf(selected);
            if (index >= 0)
            {
                SelectedIndices.Add(index);
                EnsureVisible(index);
            }
            else if (AutoScroll)
                ScrollToEnd();
            Invalidate();
            CountsChanged?.Invoke();
        }

        /// <summary> Selects the next (or previous) shown entry containing the text. Returns false if there is none. </summary>
        public bool Find(string text, bool forward)
        {
            if (string.IsNullOrEmpty(text) || visible.Count == 0) return false;
            int start = SelectedIndices.Count > 0 && SelectedIndices[0] < visible.Count ? SelectedIndices[0] : (forward ? -1 : visible.Count);
            for (int n = 1; n <= visible.Count; n++)
            {
                int i = ((start + (forward ? n : -n)) % visible.Count + visible.Count) % visible.Count;
                if (Contains(visible[i], text))
                {
                    SelectedIndices.Clear();
                    SelectedIndices.Add(i);
                    EnsureVisible(i);
                    return true;
                }
            }
            return false;
        }

        public void ScrollToEnd()
        {
            if (visible.Count > 0) EnsureVisible(visible.Count - 1);
        }

        void PullNewEntries()
        {
            var fresh = LogStore.GetSince(ref nextSequence);
            if (fresh.Count == 0) return;

            foreach (var e in fresh)
            {
                all.Add(e);
                sources.Add(e.Source ?? "");
                if (Matches(e))
                    visible.Add(e);
            }

            var limit = GuiSettings.Current.LogScrollbackLimit;
            if (limit.IsEnabled && limit.Value > 0 && all.Count > limit.Value)
            {
                var removed = new HashSet<LogEntry>(all.Take(all.Count - limit.Value));
                all.RemoveRange(0, removed.Count);
                visible.RemoveAll(removed.Contains);
                SelectedIndices.Clear();
            }

            VirtualListSize = visible.Count;
            if (AutoScroll)
                ScrollToEnd();
            CountsChanged?.Invoke();
        }

        void OnRetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            // While the filter changes, the list can ask for a row that no longer exists before its new size takes effect.
            if (e.ItemIndex < 0 || e.ItemIndex >= visible.Count)
            {
                e.Item = new ListViewItem(new[] { "", "", "", "" });
                return;
            }
            var entry = visible[e.ItemIndex];
            var item = new ListViewItem(entry.Time.ToString("HH:mm:ss.fff"));
            item.SubItems.Add(LevelName(entry.Level));
            item.SubItems.Add(entry.Source);
            item.SubItems.Add(entry.Message);
            item.ForeColor = entry.Level switch
            {
                LogEventType.Debug => Color.Gray,
                LogEventType.Warning => Color.DarkOrange,
                LogEventType.Error => Color.Red,
                _ => SystemColors.WindowText
            };
            if (!string.IsNullOrEmpty(HighlightText) && Contains(entry, HighlightText))
                item.BackColor = Color.FromArgb(255, 241, 150);
            e.Item = item;
        }
    }

    /// <summary>
    /// Log panel: level check boxes, a source drop-down, a filter box, a find box, an auto scroll check box and the log list.
    /// </summary>
    class LogPanel : UserControl
    {
        readonly LogView view;
        readonly ToolStripDropDownButton sourcesButton;
        readonly CheckBox autoScroll;
        readonly ToolStripTextBox filterBox, findBox;
        readonly ToolStripLabel countLabel;
        readonly Timer filterDelay = new Timer { Interval = 300 };

        public LogPanel()
        {
            view = new LogView { Dock = DockStyle.Fill };
            var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };

            // Level check boxes.
            bar.Items.Add(new ToolStripLabel("Show:"));
            foreach (var (level, text, color) in new[]
                     {
                         (LogEventType.Error, "Error", Color.Red),
                         (LogEventType.Warning, "Warning", Color.DarkOrange),
                         (LogEventType.Information, "Information", SystemColors.ControlText),
                         (LogEventType.Debug, "Debug", Color.Gray)
                     })
            {
                var box = new CheckBox { Text = text, Checked = true, AutoSize = true, ForeColor = color, BackColor = Color.Transparent, Padding = new Padding(4, 0, 0, 0) };
                box.CheckedChanged += (s, e) =>
                {
                    if (box.Checked) view.Levels.Add(level); else view.Levels.Remove(level);
                    view.ApplyFilter();
                };
                bar.Items.Add(new ToolStripControlHost(box) { ToolTipText = $"Show {text} messages" });
            }
            bar.Items.Add(new ToolStripSeparator());

            // Sources drop-down with a check box per source.
            sourcesButton = new ToolStripDropDownButton("Sources") { ToolTipText = "Choose which sources to show" };
            sourcesButton.DropDown.Closing += (s, e) =>
            {
                // Keep the list open while ticking several sources.
                if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
            };
            sourcesButton.DropDownOpening += (s, e) => BuildSourcesMenu();
            sourcesButton.DropDownItems.Add("(none yet)"); // so the arrow opens a list before the first refresh
            bar.Items.Add(sourcesButton);
            bar.Items.Add(new ToolStripSeparator());

            // Filter: only show matching entries.
            bar.Items.Add(new ToolStripLabel("Filter:"));
            filterBox = new ToolStripTextBox { Width = 130, ToolTipText = "Only show messages containing this text" };
            filterBox.TextChanged += (s, e) => { filterDelay.Stop(); filterDelay.Start(); };
            filterDelay.Tick += (s, e) =>
            {
                filterDelay.Stop();
                view.FilterText = filterBox.Text;
                view.ApplyFilter();
            };
            bar.Items.Add(filterBox);
            bar.Items.Add(new ToolStripSeparator());

            // Find: highlight matches and jump between them.
            bar.Items.Add(new ToolStripLabel("Find:"));
            findBox = new ToolStripTextBox { Width = 130, ToolTipText = "Highlight messages containing this text. Enter finds the next one, Shift+Enter the previous one." };
            findBox.TextChanged += (s, e) =>
            {
                view.HighlightText = findBox.Text;
                view.Invalidate();
            };
            findBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                Find(!e.Shift);
                e.SuppressKeyPress = true;
            };
            bar.Items.Add(findBox);
            var prev = new ToolStripButton("▲") { ToolTipText = "Find previous (Shift+Enter)" };
            prev.Click += (s, e) => Find(false);
            var next = new ToolStripButton("▼") { ToolTipText = "Find next (Enter)" };
            next.Click += (s, e) => Find(true);
            bar.Items.Add(prev);
            bar.Items.Add(next);
            bar.Items.Add(new ToolStripSeparator());

            // Auto scroll.
            autoScroll = new CheckBox { Text = "Auto scroll", Checked = true, AutoSize = true, BackColor = Color.Transparent, Padding = new Padding(4, 0, 0, 0) };
            autoScroll.CheckedChanged += (s, e) =>
            {
                view.AutoScroll = autoScroll.Checked;
                if (autoScroll.Checked) view.ScrollToEnd();
            };
            bar.Items.Add(new ToolStripControlHost(autoScroll) { ToolTipText = "Keep the last line visible" });

            var clear = new ToolStripButton("Clear") { ToolTipText = "Clear the log" };
            clear.Click += (s, e) => view.ClearLog();
            bar.Items.Add(clear);

            countLabel = new ToolStripLabel { Alignment = ToolStripItemAlignment.Right, ForeColor = SystemColors.GrayText };
            bar.Items.Add(countLabel);
            view.CountsChanged += () =>
                countLabel.Text = view.VisibleCount == view.TotalCount ? $"{view.TotalCount} messages" : $"{view.VisibleCount} of {view.TotalCount} shown";

            Controls.Add(view);
            Controls.Add(bar);
        }

        public void ClearLog() => view.ClearLog();

        void Find(bool forward)
        {
            if (view.Find(findBox.Text, forward))
                autoScroll.Checked = false; // stay on the match instead of jumping to new messages
            else if (!string.IsNullOrEmpty(findBox.Text))
                System.Media.SystemSounds.Beep.Play();
        }

        void BuildSourcesMenu()
        {
            var items = sourcesButton.DropDownItems;
            items.Clear();
            var showAll = new ToolStripMenuItem("Show All");
            showAll.Click += (s, e) => { view.HiddenSources.Clear(); view.ApplyFilter(); BuildSourcesMenu(); };
            var hideAll = new ToolStripMenuItem("Hide All");
            hideAll.Click += (s, e) => { foreach (var src in view.Sources) view.HiddenSources.Add(src); view.ApplyFilter(); BuildSourcesMenu(); };
            items.Add(showAll);
            items.Add(hideAll);
            items.Add(new ToolStripSeparator());
            foreach (var source in view.Sources)
            {
                var name = source;
                var item = new ToolStripMenuItem(name.Length == 0 ? "(no source)" : name) { CheckOnClick = true, Checked = !view.HiddenSources.Contains(name) };
                item.CheckedChanged += (s, e) =>
                {
                    if (item.Checked) view.HiddenSources.Remove(name); else view.HiddenSources.Add(name);
                    view.ApplyFilter();
                    UpdateSourcesText();
                };
                items.Add(item);
            }
            UpdateSourcesText();
        }

        void UpdateSourcesText() =>
            sourcesButton.Text = view.HiddenSources.Count == 0 ? "Sources" : $"Sources ({view.HiddenSources.Count} hidden)";

        protected override void Dispose(bool disposing)
        {
            if (disposing) filterDelay.Dispose();
            base.Dispose(disposing);
        }
    }
}
