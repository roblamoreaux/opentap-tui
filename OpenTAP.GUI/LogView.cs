using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using OpenTap.Diagnostic;

namespace OpenTap.Gui
{
    class LogEntry
    {
        public DateTime Time;
        public LogEventType Level;
        public string Source;
        public string Message;
    }

    /// <summary>
    /// Collects OpenTAP log messages from the moment the GUI starts, so every log panel (main window, package
    /// manager, ...) can show them. Thread safe.
    /// </summary>
    class LogStore : ILogListener
    {
        const int MaxHistory = 100000;
        static readonly object sync = new object();
        static LogStore instance;
        static readonly List<LogEntry> history = new List<LogEntry>();
        /// <summary> Sequence number of history[0]. </summary>
        static long firstSequence;

        public static void EnsureListening()
        {
            lock (sync)
            {
                if (instance != null) return;
                instance = new LogStore();
                Log.AddListener(instance);
            }
        }

        /// <summary> Returns the messages logged since <paramref name="next"/> and advances it. Start with 0 to get the history. </summary>
        public static List<LogEntry> GetSince(ref long next)
        {
            lock (sync)
            {
                var start = (int)Math.Max(0, next - firstSequence);
                var result = start < history.Count ? history.GetRange(start, history.Count - start) : new List<LogEntry>();
                next = firstSequence + history.Count;
                return result;
            }
        }

        public void EventsLogged(IEnumerable<Event> events)
        {
            lock (sync)
            {
                if (history.Count > MaxHistory)
                {
                    var remove = history.Count - MaxHistory / 2;
                    history.RemoveRange(0, remove);
                    firstSequence += remove;
                }
                foreach (var e in events)
                {
                    history.Add(new LogEntry
                    {
                        Time = new DateTime(e.Timestamp),
                        Level = (LogEventType)e.EventType,
                        Source = e.Source,
                        Message = (e.Message ?? "").Replace("\r", "").Replace("\n", "  ")
                    });
                }
            }
        }

        public void Flush() { }
    }

    /// <summary> Log panel: a virtual list view with time, level, source and message columns, colored by level. </summary>
    class LogView : ListView
    {
        readonly List<LogEntry> entries = new List<LogEntry>();
        readonly Timer timer = new Timer { Interval = 200 };
        long nextSequence;

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
            var copy = new ToolStripMenuItem("Copy", null, (s, e) => CopySelected()) { ShortcutKeyDisplayString = "Ctrl+C" };
            var selectAll = new ToolStripMenuItem("Select All", null, (s, e) => SelectAll()) { ShortcutKeyDisplayString = "Ctrl+A" };
            menu.Items.Add(copy);
            menu.Items.Add(selectAll);
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
            entries.Clear();
            VirtualListSize = 0;
            Invalidate();
        }

        public void SelectAll()
        {
            for (int i = 0; i < entries.Count; i++)
                SelectedIndices.Add(i);
        }

        public void CopySelected()
        {
            if (SelectedIndices.Count == 0) return;
            var sb = new StringBuilder();
            foreach (int i in SelectedIndices)
                sb.AppendLine(Format(entries[i]));
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

        void PullNewEntries()
        {
            var fresh = LogStore.GetSince(ref nextSequence);
            if (fresh.Count == 0) return;

            // Keep following the bottom of the log unless the user scrolled up to read something.
            bool follow = entries.Count == 0 || IsLastItemVisible();
            entries.AddRange(fresh);

            var limit = GuiSettings.Current.LogScrollbackLimit;
            if (limit.IsEnabled && limit.Value > 0 && entries.Count > limit.Value)
            {
                entries.RemoveRange(0, entries.Count - limit.Value);
                SelectedIndices.Clear();
            }

            VirtualListSize = entries.Count;
            if (follow && entries.Count > 0)
                EnsureVisible(entries.Count - 1);
        }

        bool IsLastItemVisible()
        {
            if (VirtualListSize == 0) return true;
            try
            {
                var rect = GetItemRect(VirtualListSize - 1);
                return rect.Top < ClientSize.Height;
            }
            catch
            {
                return true;
            }
        }

        void OnRetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            var entry = entries[e.ItemIndex];
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
            e.Item = item;
        }
    }
}
