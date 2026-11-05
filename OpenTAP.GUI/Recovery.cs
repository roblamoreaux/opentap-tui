using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace OpenTap.Gui
{
    /// <summary>
    /// Keeps a copy of the unsaved test plan next to the working directory so it can be recovered after a crash.
    /// Port of the TUI's Recovery class (uses its own file name so the two tools don't pick up each other's files).
    /// </summary>
    class Recovery
    {
        public class RecFile
        {
            public string FilePath { get; set; }
            public string TestPlan { get; set; }
        }

        const string Extension = ".GuiRecovery";
        static readonly TapSerializer serializer = new TapSerializer();
        readonly string fileName = $".{Process.GetCurrentProcess().Id}{Extension}";

        public void Save(TestPlan plan)
        {
            try
            {
                string xml;
                using (var ms = new MemoryStream())
                {
                    plan.Save(ms);
                    xml = System.Text.Encoding.UTF8.GetString(ms.ToArray()).TrimStart('﻿');
                }
                File.WriteAllText(fileName, serializer.SerializeToString(new RecFile { FilePath = plan.Path, TestPlan = xml }));
            }
            catch (Exception ex)
            {
                GuiAction.Log.Debug("Could not write recovery file: {0}", ex.Message);
            }
        }

        /// <summary> Finds a recovery file left behind by a GUI that did not close normally. </summary>
        public static RecFile FindLeftover(out string path)
        {
            path = null;
            string[] files;
            try
            {
                files = Directory.GetFiles(".", "." + "*" + Extension);
            }
            catch
            {
                return null;
            }
            foreach (var file in files)
            {
                // Skip files that belong to a GUI that is still running.
                var name = Path.GetFileName(file);
                if (int.TryParse(name.Substring(1, name.Length - 1 - Extension.Length), out var pid) && IsRunning(pid))
                    continue;
                try
                {
                    if (serializer.DeserializeFromFile(file, TypeData.FromType(typeof(RecFile))) is RecFile rec && !string.IsNullOrEmpty(rec.TestPlan))
                    {
                        path = file;
                        return rec;
                    }
                }
                catch
                {
                    // Not a valid recovery file.
                }
            }
            return null;
        }

        static bool IsRunning(int pid)
        {
            if (pid == Process.GetCurrentProcess().Id) return true;
            try
            {
                using var p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch
            {
                return false;
            }
        }

        public static TestPlan Load(RecFile rec)
        {
            using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rec.TestPlan));
            return TestPlan.Load(ms, rec.FilePath);
        }

        public void Remove()
        {
            try { File.Delete(fileName); } catch { }
        }
    }
}
