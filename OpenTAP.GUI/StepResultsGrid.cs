using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace OpenTap.Gui
{
    /// <summary>
    /// Every step of the selected run(s) with its results, limits and verdict - one row per result row, plus one row
    /// for each step that published no results. Limits are the step's "Limits" settings (Minimum Value, Maximum Value,
    /// Check Limits) as saved with the step run, the same columns the TestRunViewerSqlite tool reads.
    /// </summary>
    class StepResultsGrid : DataGridView
    {
        static readonly Color FailBack = Color.FromArgb(255, 228, 228);
        static readonly Color FailText = Color.FromArgb(170, 0, 0);
        static readonly Color PassText = Color.FromArgb(0, 120, 0);

        public StepResultsGrid()
        {
            ReadOnly = true;
            AllowUserToAddRows = false;
            AllowUserToDeleteRows = false;
            AllowUserToResizeRows = false;
            RowHeadersVisible = false;
            SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithAutoHeaderText;
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            BackgroundColor = SystemColors.Window;
            BorderStyle = BorderStyle.None;
            DoubleBuffered = true;
            RowTemplate.Height = 22;
            SortCompare += OnSortCompare;
        }

        static string Parameter(IData run, string group, string name) =>
            run.Parameters?.FirstOrDefault(p => p.Name == name && (p.Group ?? "") == group)?.Value?.ToString();

        /// <summary>
        /// The final verdict of a run. Result stores can keep two "Verdict" parameters: the NotSet value recorded when
        /// the run started (as a number) and the final verdict (as text), so prefer the last text value.
        /// </summary>
        internal static string VerdictOf(IData run)
        {
            var values = run.Parameters?.Where(p => p.Name == "Verdict" && string.IsNullOrEmpty(p.Group)).Select(p => p.Value).ToList();
            if (values == null || values.Count == 0) return null;
            var text = values.OfType<string>().LastOrDefault();
            if (text != null) return text;
            try { return ((Verdict)values.Last().ToInt32(CultureInfo.InvariantCulture)).ToString(); }
            catch { return values.Last()?.ToString(); }
        }

        static string StepPath(IData step)
        {
            var names = new List<string>();
            for (var p = step.Parent; p?.Parent != null; p = p.Parent) // stop below the plan run
                names.Insert(0, Parameter(p, "", "Name") ?? p.Name);
            return string.Join(" \\ ", names);
        }

        static bool TryNumber(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

        static string Cell(IResultColumn column, int row) =>
            column != null && row < column.Data.Length ? Convert.ToString(column.Data.GetValue(row), CultureInfo.CurrentCulture) : "";

        public void ShowRuns(List<RunInfo> runs)
        {
            SuspendLayout();
            Rows.Clear();
            Columns.Clear();

            bool multipleRuns = runs.Count > 1;
            var rows = new List<string[]>();
            var verdicts = new List<string>();
            var outOfLimits = new List<bool>();

            foreach (var run in runs)
            {
                foreach (var step in run.Steps.OrderBy(s => s.GetID()))
                {
                    var name = Parameter(step, "", "Name") ?? step.Name;
                    var path = StepPath(step);
                    var time = Parameter(step, "", "StartTime") ?? Parameter(step, "", "Date") ?? "";
                    var duration = Parameter(step, "", "Duration") ?? "";
                    var verdict = VerdictOf(step) ?? "";
                    var min = Parameter(step, "Limits", "Minimum Value") ?? "";
                    var max = Parameter(step, "Limits", "Maximum Value") ?? "";
                    var check = Parameter(step, "Limits", "Check Limits") ?? "";

                    void Add(string result, string signalName, string signal, string valueName, string value, string other)
                    {
                        rows.Add(new[] { run.Id, path, name, verdict, result, signalName, signal, valueName, value, min, max, check, other, time, duration });
                        verdicts.Add(verdict);
                        outOfLimits.Add(IsOutOfLimits(value, min, max, check));
                    }

                    if (!run.TablesByStep.TryGetValue(step.GetID(), out var tables) || tables.Count == 0)
                    {
                        Add("", "", "", "", "", "");
                        continue;
                    }
                    foreach (var table in tables)
                    {
                        var columns = table.Columns ?? Array.Empty<IResultColumn>();
                        var c0 = columns.ElementAtOrDefault(0);
                        var c1 = columns.ElementAtOrDefault(1);
                        var rowCount = columns.Length == 0 ? 0 : columns.Max(c => c.Data.Length);
                        for (int i = 0; i < Math.Max(1, rowCount); i++)
                        {
                            var other = string.Join("; ", columns.Skip(2).Select(c => $"{c.Name}: {Cell(c, i)}"));
                            Add(table.Name, c0?.Name ?? "", Cell(c0, i), c1?.Name ?? "", Cell(c1, i), other);
                        }
                    }
                }
            }

            var headers = new[] { "Run", "Parent", "Step", "Verdict", "Result", "Signal Name", "Signal", "Value Name", "Value", "Min Limit", "Max Limit", "Check Limits", "Other Columns", "Start Time", "Duration (s)" };
            var widths = new[] { 50, 160, 190, 70, 130, 100, 140, 85, 100, 80, 80, 90, 150, 140, 80 };
            for (int i = 0; i < headers.Length; i++)
                Columns.Add(new DataGridViewTextBoxColumn { Name = headers[i], HeaderText = headers[i], Width = widths[i], SortMode = DataGridViewColumnSortMode.Automatic });
            Columns["Run"].Visible = multipleRuns;
            Columns["Other Columns"].Visible = rows.Any(r => r[12].Length > 0);
            Columns["Parent"].Visible = rows.Any(r => r[1].Length > 0);
            Columns["Value"].DefaultCellStyle.Font = new Font(Font, FontStyle.Bold);

            var valueIndex = Columns["Value"].Index;
            var verdictIndex = Columns["Verdict"].Index;
            for (int i = 0; i < rows.Count; i++)
            {
                var index = Rows.Add(rows[i]);
                var row = Rows[index];
                bool failed = verdicts[i] == "Fail" || verdicts[i] == "Error";
                if (failed)
                {
                    row.DefaultCellStyle.BackColor = FailBack;
                    row.Cells[verdictIndex].Style.ForeColor = FailText;
                    row.Cells[verdictIndex].Style.Font = new Font(Font, FontStyle.Bold);
                }
                else if (verdicts[i] == "Pass")
                    row.Cells[verdictIndex].Style.ForeColor = PassText;
                if (outOfLimits[i])
                {
                    row.Cells[valueIndex].Style.BackColor = Color.FromArgb(255, 190, 190);
                    row.Cells[valueIndex].Style.ForeColor = FailText;
                    row.Cells[valueIndex].ToolTipText = "Outside the Min/Max limits";
                }
            }
            ResumeLayout();
        }

        /// <summary> A numeric value outside numeric limits, when the step's Check Limits setting is on. </summary>
        static bool IsOutOfLimits(string value, string min, string max, string check)
        {
            if (!string.Equals(check, "True", StringComparison.OrdinalIgnoreCase)) return false;
            if (!TryNumber(value, out var v)) return false;
            if (TryNumber(min, out var lo) && v < lo) return true;
            if (TryNumber(max, out var hi) && v > hi) return true;
            return false;
        }

        /// <summary> Sort numbers as numbers, everything else as text. </summary>
        void OnSortCompare(object sender, DataGridViewSortCompareEventArgs e)
        {
            var a = e.CellValue1 as string ?? "";
            var b = e.CellValue2 as string ?? "";
            if (TryNumber(a, out var x) && TryNumber(b, out var y))
                e.SortResult = x.CompareTo(y);
            else
                e.SortResult = string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
            e.Handled = true;
        }
    }
}
