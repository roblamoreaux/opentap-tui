using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpenTap.Plugins.BasicSteps;

namespace OpenTap.Gui
{
    /// <summary> One test plan run found in a result store, with its result tables grouped by name. Port of the TUI's IRunViewModel. </summary>
    class RunInfo
    {
        public IData Run;
        public IResultStore Store;
        public Dictionary<string, List<IResultTable>> Series = new Dictionary<string, List<IResultTable>>();
        /// <summary> Every test step run in this plan run (at any depth). </summary>
        public List<IData> Steps = new List<IData>();
        /// <summary> Result tables by the ID of the step run that published them. </summary>
        public Dictionary<long, List<IResultTable>> TablesByStep = new Dictionary<long, List<IResultTable>>();

        string Parameter(string name) => Run.Parameters?.FirstOrDefault(p => p.Name == name)?.Value?.ToString();
        public string Id => Parameter("Run ID") ?? Run.GetID().ToString();
        public string Name => Run.Name;
        public string Verdict => StepResultsGrid.VerdictOf(Run);
        public string StartTime => Parameter("StartTime") ?? Parameter("Start Time") ?? Parameter("Date");
        public string Source => Store.Name;
    }

    /// <summary>
    /// Lists the test plan runs in the result stores configured under Settings → Results and plots the chosen ones.
    /// Port of the TUI's ResultsViewerWindow (RunExplorerWindow.cs).
    /// </summary>
    class ResultsViewerForm : Form
    {
        readonly ListView list;
        readonly ToolStripStatusLabel status;
        readonly ToolStripButton plotButton;
        readonly StepResultsGrid stepGrid;
        readonly Label stepTitle;
        bool stepGridUpdateQueued;
        List<RunInfo> runs = new List<RunInfo>();
        bool loading;

        public ResultsViewerForm()
        {
            Text = "Results Viewer";
            Size = new Size(1300, 850);
            StartPosition = FormStartPosition.CenterScreen;

            list = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false };
            list.Columns.Add("Run ID", 80);
            list.Columns.Add("Name", 250);
            list.Columns.Add("Verdict", 90);
            list.Columns.Add("Start Time", 170);
            list.Columns.Add("Results", 200);
            list.Columns.Add("Source", 150);
            list.ItemChecked += (s, e) => UpdateButtons();
            list.ItemActivate += (s, e) =>
            {
                // Double-click / Enter plots the activated run on its own if nothing is checked.
                if (list.CheckedItems.Count == 0 && list.FocusedItem?.Tag is RunInfo run)
                    Plot(new List<RunInfo> { run });
            };

            var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            plotButton = new ToolStripButton("Plot Results (F5)") { Enabled = false, ToolTipText = "Plot the checked runs" };
            plotButton.Click += (s, e) => PlotChecked();
            var refresh = new ToolStripButton("Refresh") { ToolTipText = "Read the result stores again" };
            refresh.Click += (s, e) => LoadRuns();
            var resultSettings = new ToolStripButton("Result Settings...") { ToolTipText = "Configure result listeners and result stores" };
            resultSettings.Click += (s, e) =>
            {
                using (var form = new ListSettingsForm("Results", ResultSettings.Current))
                    form.ShowDialog(this);
                LoadRuns();
            };
            var selectAll = new ToolStripButton("Check All");
            selectAll.Click += (s, e) => { foreach (ListViewItem i in list.Items) i.Checked = true; };
            var selectNone = new ToolStripButton("Uncheck All");
            selectNone.Click += (s, e) => { foreach (ListViewItem i in list.Items) i.Checked = false; };
            toolbar.Items.AddRange(new ToolStripItem[] { plotButton, new ToolStripSeparator(), selectAll, selectNone, new ToolStripSeparator(), refresh, resultSettings });

            var statusStrip = new StatusStrip();
            status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusStrip.Items.Add(status);

            // Step results grid for the selected run(s).
            stepGrid = new StepResultsGrid { Dock = DockStyle.Fill };
            stepTitle = new Label { Text = "Step Results - select a run above", Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) };
            list.SelectedIndexChanged += (s, e) =>
            {
                // SelectedIndexChanged fires once per item when the selection changes; update once afterwards.
                if (stepGridUpdateQueued) return;
                stepGridUpdateQueued = true;
                BeginInvoke(new Action(ShowSelectedSteps));
            };

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(list);
            split.Panel2.Controls.Add(stepGrid);
            split.Panel2.Controls.Add(stepTitle);

            Controls.Add(split);
            Controls.Add(toolbar);
            Controls.Add(statusStrip);
            Load += (s, e) => split.SplitterDistance = (int)(split.Height * 0.4);

            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.F5) { PlotChecked(); e.Handled = true; } };
            Load += (s, e) => LoadRuns();
        }

        void UpdateButtons() => plotButton.Enabled = list.CheckedItems.Count > 0;

        void ShowSelectedSteps()
        {
            stepGridUpdateQueued = false;
            var selected = list.SelectedItems.Cast<ListViewItem>().Select(i => (RunInfo)i.Tag).ToList();
            stepGrid.ShowRuns(selected);
            stepTitle.Text = selected.Count switch
            {
                0 => "Step Results - select a run above",
                1 => $"Step Results - run {selected[0].Id}: {selected[0].Name}  ({stepGrid.Rows.Count} rows)",
                _ => $"Step Results - {selected.Count} runs  ({stepGrid.Rows.Count} rows)"
            };
        }

        void LoadRuns()
        {
            if (loading) return;
            loading = true;
            list.Items.Clear();
            UpdateButtons();
            status.Text = "Loading runs...";
            Task.Run(() =>
            {
                var result = new List<RunInfo>();
                var stores = ResultSettings.Current.OfType<IResultStore>().ToList();
                foreach (var store in stores)
                    result.AddRange(ReadStore(store));
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        loading = false;
                        runs = result;
                        Fill(stores.Count);
                    }));
                }
                catch (InvalidOperationException)
                {
                    // Window closed.
                }
            });
        }

        /// <summary>
        /// Same traversal as the TUI (plan runs, plus every result table attached to its top-most plan run),
        /// and additionally keeps the step runs for the step results grid.
        /// </summary>
        static List<RunInfo> ReadStore(IResultStore store)
        {
            var runs = new Dictionary<long, RunInfo>();
            RunInfo PlanRunOf(IData entry)
            {
                var planRun = entry;
                while (planRun.Parent != null)
                    planRun = planRun.Parent;
                if (!runs.TryGetValue(planRun.GetID(), out var run))
                    runs[planRun.GetID()] = run = new RunInfo { Run = planRun, Store = store };
                return run;
            }
            try
            {
                store.Open();
                foreach (var entry in store.GetEntries(SearchCondition.All(), new List<string>(), true))
                {
                    if (entry.ObjectType == "Plan Run")
                    {
                        PlanRunOf(entry);
                    }
                    else if (entry is IResultTable table)
                    {
                        var run = PlanRunOf(entry);
                        if (!run.Series.TryGetValue(table.Name, out var tables))
                            run.Series[table.Name] = tables = new List<IResultTable>();
                        tables.Add(table);
                        if (table.Parent != null)
                        {
                            if (!run.TablesByStep.TryGetValue(table.Parent.GetID(), out var stepTables))
                                run.TablesByStep[table.Parent.GetID()] = stepTables = new List<IResultTable>();
                            stepTables.Add(table);
                        }
                    }
                    else if (entry.Parent != null)
                    {
                        // A test step run.
                        PlanRunOf(entry).Steps.Add(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                GuiAction.Log.Error("Could not read results from '{0}': {1}", store.Name, ex.Message);
                GuiAction.Log.Debug(ex);
            }
            finally
            {
                try { store.Close(); } catch { }
            }
            return runs.Values.ToList();
        }

        void Fill(int storeCount)
        {
            list.BeginUpdate();
            list.Items.Clear();
            stepGrid.ShowRuns(new List<RunInfo>());
            stepTitle.Text = "Step Results - select a run above";
            foreach (var run in runs)
            {
                var item = new ListViewItem(new[]
                {
                    run.Id, run.Name, run.Verdict ?? "", run.StartTime ?? "",
                    string.Join(", ", run.Series.Keys), run.Source
                }) { Tag = run };
                if (run.Series.Count == 0) item.ForeColor = SystemColors.GrayText;
                list.Items.Add(item);
            }
            list.EndUpdate();
            status.Text = storeCount == 0
                ? "No result stores are configured. Add one (for example SQLite) with Result Settings."
                : $"{runs.Count} run(s) from {storeCount} result store(s). Select a run to see its steps; check runs and press Plot Results to plot.";
            UpdateButtons();
        }

        void PlotChecked() => Plot(list.CheckedItems.Cast<ListViewItem>().Select(i => (RunInfo)i.Tag).ToList());

        void Plot(List<RunInfo> selected)
        {
            if (selected.Count == 0) return;
            if (selected.All(r => r.Series.Count == 0))
            {
                MessageBox.Show(this, "The selected runs have no results to plot.", "Plot Results", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            new ResultsForm(selected).Show(this);
        }
    }

    /// <summary> Chart of the results of one or more runs with plot settings on the side. Port of the TUI's ResultsWindow. </summary>
    class ResultsForm : Form
    {
        readonly ChartSettings settings;
        readonly ChartControl chart;
        readonly AnnotationPropertyGrid settingsGrid;

        public ResultsForm(List<RunInfo> runs)
        {
            Size = new Size(1200, 700);
            StartPosition = FormStartPosition.CenterParent;
            ShowIcon = false;
            settings = new ChartSettings(runs);

            chart = new ChartControl { Dock = DockStyle.Fill };
            settingsGrid = new AnnotationPropertyGrid { Dock = DockStyle.Fill, ToolbarVisible = false };
            settingsGrid.LoadProperties(settings);
            settingsGrid.PropertiesChanged += () =>
            {
                if (settings.Sync())
                    settingsGrid.LoadProperties(settings); // the step filter was reset
                Replot();
            };

            var exporters = TypeData.GetDerivedTypes<ITableExport>().Where(t => t.CanCreateInstance).ToList();
            var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            var export = new ToolStripButton("Export...") { Enabled = exporters.Any(), ToolTipText = exporters.Any() ? "Export the plotted values (Ctrl+E)" : "No table exporter plugins are installed." };
            export.Click += (s, e) => Export(exporters);
            var zoomIn = new ToolStripButton("Zoom In (F6)");
            zoomIn.Click += (s, e) => chart.Zoom(0.8);
            var zoomOut = new ToolStripButton("Zoom Out (F7)");
            zoomOut.Click += (s, e) => chart.Zoom(1.25);
            var reset = new ToolStripButton("Reset Zoom") { ToolTipText = "Fit all data (or double-click the chart)" };
            reset.Click += (s, e) => chart.ResetZoom();
            var hint = new ToolStripLabel("Mouse wheel: zoom   Drag: pan   Double-click: fit") { ForeColor = SystemColors.GrayText, Alignment = ToolStripItemAlignment.Right };
            toolbar.Items.AddRange(new ToolStripItem[] { export, new ToolStripSeparator(), zoomIn, zoomOut, reset, hint });

            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2 };
            split.Panel1.Controls.Add(chart);
            split.Panel2.Controls.Add(settingsGrid);
            split.Panel2.Controls.Add(new Label { Text = "Plot Settings", Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) });

            Controls.Add(split);
            Controls.Add(toolbar);

            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F6) chart.Zoom(0.8);
                else if (e.KeyCode == Keys.F7) chart.Zoom(1.25);
                else if (e.Control && e.KeyCode == Keys.E && export.Enabled) Export(exporters);
                else return;
                e.Handled = true;
            };
            Load += (s, e) =>
            {
                split.SplitterDistance = Math.Max(100, split.Width - 330);
                Replot();
            };
        }

        void Replot()
        {
            var plots = settings.GetPlots();
            chart.SetData(plots, settings.ChartType, settings.LogXAxis, settings.LogYAxis, settings.XAxis?.Name, settings.YAxis?.Name);
            Text = "Results - " + (settings.ResultNames.Any() ? string.Join(", ", settings.ResultNames) : "(none selected)");
        }

        void Export(List<ITypeData> exporters)
        {
            var request = new ExportDialogInput
            {
                AvailableExporters = exporters.Select(e => e.CreateInstance()).OfType<ITableExport>().ToList()
            };
            if (request.AvailableExporters.Count == 1)
                request.Exporter = request.AvailableExporters[0];
            UserInput.Request(request, true);
            if (request.Submit != ExportDialogInput.ExportSubmit.Ok || request.Exporter == null || string.IsNullOrWhiteSpace(request.Path))
                return;

            var plots = settings.GetPlots();
            var rows = plots.Count == 0 ? 0 : plots.Max(p => p.Points.Count);
            var table = new string[rows + 1][];
            table[0] = plots.SelectMany(p => new[] { $"{p.Name} {settings.XAxis?.Name}", $"{p.Name} {settings.YAxis?.Name}" }).ToArray();
            for (int i = 0; i < rows; i++)
            {
                table[i + 1] = plots.SelectMany(p => i < p.Points.Count
                    ? new[] { p.Points[i].X.ToString(CultureInfo.CurrentCulture), p.Points[i].Y.ToString(CultureInfo.CurrentCulture) }
                    : new[] { "", "" }).ToArray();
            }
            try
            {
                request.Exporter.ExportTableValues(table, request.Path);
                GuiAction.Log.Info("Exported {0} rows to '{1}'.", rows, request.Path);
            }
            catch (Exception ex)
            {
                GuiAction.Log.Error(ex.Message);
                MessageBox.Show(this, ex.Message, "Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    /// <summary> Plot settings edited in the property grid next to the chart. Port of the TUI's ChartSettings. </summary>
    [Display("Plot Settings")]
    public class ChartSettings
    {
        readonly Dictionary<string, List<(IResultTable Table, string Run)>> allSeries;
        readonly bool multipleRuns;
        string[] lastResultNames;

        internal ChartSettings(List<RunInfo> runs)
        {
            multipleRuns = runs.Count > 1;
            allSeries = runs.SelectMany(r => r.Series.SelectMany(s => s.Value.Select(t => (s.Key, Table: t, Run: r.Id))))
                .GroupBy(x => x.Key)
                .ToDictionary(g => g.Key, g => g.Select(x => (x.Table, x.Run)).ToList());
            ResultNames = allSeries.Keys.ToList();
            FilterStepName = AvailableStepNames;
            lastResultNames = ResultNames.ToArray();
        }

        [Display("Chart Type", Order: 0)]
        public ChartType ChartType { get; set; }

        [Browsable(false)]
        public List<string> AvailableResults => allSeries.Keys.ToList();

        [AvailableValues(nameof(AvailableResults))]
        [Display("Result Names", "Which result tables to plot.", Order: 1)]
        public List<string> ResultNames { get; set; } = new List<string>();

        [Browsable(false)]
        public List<string> AvailableStepNames => SelectedTables(false).Select(t => StepName(t.Table)).Distinct().ToList();

        [AvailableValues(nameof(AvailableStepNames))]
        [Display("Step Name", "Only plot results from these steps.", Group: "Filter")]
        public List<string> FilterStepName { get; set; } = new List<string>();

        [Browsable(false)]
        public List<IResultColumn> AvailableResultColumns
        {
            get
            {
                var columns = new List<IResultColumn>();
                foreach (var (table, _) in SelectedTables(true))
                {
                    if (columns.Count == 0)
                        columns.Add(IndexColumn(table));
                    foreach (var column in table.Columns)
                        if (columns.All(c => c.Name != column.Name))
                            columns.Add(column);
                }
                return columns;
            }
        }

        IResultColumn xAxis;
        [Display("X-Axis", Group: "Plot Data", Order: 1)]
        [AvailableValues(nameof(AvailableResultColumns))]
        public IResultColumn XAxis
        {
            get
            {
                var available = AvailableResultColumns;
                if (xAxis == null || !available.Any(c => c.Name == xAxis.Name && ResultColumnAnnotation.CanConvertToDouble(c)))
                    xAxis = available.FirstOrDefault(ResultColumnAnnotation.CanConvertToDouble);
                return xAxis;
            }
            set => xAxis = value;
        }

        IResultColumn yAxis;
        [Display("Y-Axis", Group: "Plot Data", Order: 2)]
        [AvailableValues(nameof(AvailableResultColumns))]
        public IResultColumn YAxis
        {
            get
            {
                var available = AvailableResultColumns;
                if (yAxis == null || !available.Any(c => c.Name == yAxis.Name && ResultColumnAnnotation.CanConvertToDouble(c)))
                    yAxis = available.FirstOrDefault(c => ResultColumnAnnotation.CanConvertToDouble(c) && c.Name != XAxis?.Name)
                            ?? available.FirstOrDefault(ResultColumnAnnotation.CanConvertToDouble);
                return yAxis;
            }
            set => yAxis = value;
        }

        [Display("Log X-Axis", Group: "Plot Data", Order: 3)]
        public bool LogXAxis { get; set; }

        [Display("Log Y-Axis", Group: "Plot Data", Order: 4)]
        public bool LogYAxis { get; set; }

        static string StepName(IResultTable table) => table.Parent?.Name ?? table.Name;

        static IResultColumn IndexColumn(IResultTable table) =>
            new ResultColumn("[Index]", Enumerable.Range(0, table.Columns.FirstOrDefault()?.Data.Length ?? 0).ToArray());

        IEnumerable<(IResultTable Table, string Run)> SelectedTables(bool applyStepFilter) =>
            allSeries.Where(s => ResultNames.Contains(s.Key)).SelectMany(s => s.Value)
                .Where(t => !applyStepFilter || FilterStepName.Contains(StepName(t.Table)));

        /// <summary> Like the TUI: choosing other result names resets the step filter to all their steps. Returns true if it did. </summary>
        internal bool Sync()
        {
            if (ResultNames.SequenceEqual(lastResultNames)) return false;
            lastResultNames = ResultNames.ToArray();
            FilterStepName = AvailableStepNames;
            return true;
        }

        internal List<ChartSeries> GetPlots()
        {
            var plots = new List<ChartSeries>();
            var x = XAxis;
            var y = YAxis;
            if (x == null || y == null) return plots;
            foreach (var (table, run) in SelectedTables(true))
            {
                var xc = x.Name == "[Index]" ? IndexColumn(table) : table.Columns.FirstOrDefault(c => c.Name == x.Name);
                var yc = y.Name == "[Index]" ? IndexColumn(table) : table.Columns.FirstOrDefault(c => c.Name == y.Name);
                if (xc == null || yc == null) continue;
                var name = StepName(table) + (multipleRuns ? $" (run {run})" : "");
                var plot = new ChartSeries { Name = name };
                int skipped = 0;
                for (int i = 0; i < Math.Min(xc.Data.Length, yc.Data.Length); i++)
                {
                    if (ResultColumnAnnotation.TryToDouble(xc.Data.GetValue(i), out var xv) && ResultColumnAnnotation.TryToDouble(yc.Data.GetValue(i), out var yv))
                        plot.Points.Add(new PointD(xv, yv));
                    else
                        skipped++;
                }
                if (skipped > 0)
                    GuiAction.Log.Warning("Skipped {0} value(s) in '{1}' that are not numbers.", skipped, name);
                plots.Add(plot);
            }
            return plots;
        }
    }

    /// <summary> Shows result columns as "Name (N values)" in drop-downs. Port of the TUI's IResultColumnAnnotation. </summary>
    public class ResultColumnAnnotation : IStringReadOnlyValueAnnotation
    {
        readonly AnnotationCollection annotation;
        public ResultColumnAnnotation(AnnotationCollection a) => annotation = a;

        public string Value
        {
            get
            {
                if (annotation.Get<IObjectValueAnnotation>()?.Value is not IResultColumn column) return "";
                var title = column.Name;
                if (column.Data.Length > 0)
                    title += $" ({column.Data.Length} values)";
                if (!CanConvertToDouble(column))
                    title += " [Unsupported Type]";
                return title;
            }
        }

        public static bool CanConvertToDouble(IResultColumn column)
        {
            var type = column.Data.GetType();
            var elementType = type.GetElementType();
            if (!type.IsArray || elementType == null) return false;
            if (elementType != typeof(string) && elementType != typeof(object) && elementType.GetInterface(nameof(IConvertible)) != null)
                return true;
            // Text columns (for example numbers captured by a regular expression) count when every value is a number.
            if (column.Data.Length == 0) return false;
            foreach (var value in column.Data)
                if (!TryToDouble(value, out _)) return false;
            return true;
        }

        public static bool TryToDouble(object value, out double result)
        {
            result = 0;
            switch (value)
            {
                case null: return false;
                case string s:
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
                           || double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out result);
                case IConvertible c:
                    try { result = c.ToDouble(CultureInfo.InvariantCulture); return true; }
                    catch { return false; }
                default: return false;
            }
        }
    }

    public class ResultColumnAnnotator : IAnnotator
    {
        public double Priority => 1;

        public void Annotate(AnnotationCollection annotations)
        {
            if (annotations.Get<IObjectValueAnnotation>()?.Value is IResultColumn)
                annotations.Add(new ResultColumnAnnotation(annotations));
        }
    }

    /// <summary> Asked when exporting plotted values. Port of the TUI's ExportDialogInput. </summary>
    [Display("Export Results")]
    public class ExportDialogInput
    {
        public enum ExportSubmit
        {
            Ok = 1,
            Cancel = 2,
        }

        [AvailableValues(nameof(AvailableExporters))]
        public ITableExport Exporter { get; set; }

        [Browsable(false)]
        public List<ITableExport> AvailableExporters { get; set; }

        [FilePath(FilePathAttribute.BehaviorChoice.Save)]
        public string Path { get; set; }

        [Submit]
        [Layout(LayoutMode.FullRow | LayoutMode.FloatBottom, 1, 1000)]
        public ExportSubmit Submit { get; set; } = ExportSubmit.Cancel;
    }
}
