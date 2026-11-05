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

}
