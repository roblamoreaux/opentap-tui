using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace OpenTap.Gui
{
    /// <summary> What the GUI knows about a step in the current (or last) run. </summary>
    class StepRunState
    {
        public DateTime Start;
        public TimeSpan? Duration;
        public Verdict? Verdict;
        public bool Running;
        public int Completions;
    }

    /// <summary> Data carried while dragging steps inside the test plan grid. </summary>
    class StepDragData
    {
        public List<ITestStep> Steps;
    }

    /// <summary>
    /// The test plan as a tree with columns: step numbers, alternating row shading, expand/collapse, multi-select,
    /// drag and drop, the running step highlighted (and scrolled into view), plus optional columns for duration,
    /// verdict, type, progress, outputs and any step setting.
    /// </summary>
    class TestPlanGrid : DataGridView
    {
        class Row
        {
            public object Item;   // the TestPlan (first row) or an ITestStep
            public int Depth;
            public int Number;    // 1-based position of the step in the whole plan (0 for the plan row)
            public bool HasChildren;
            public bool Expanded;
        }

        public const string MemberPrefix = "member:";

        /// <summary> Built-in optional columns: key, header, width, tooltip. </summary>
        public static readonly (string Key, string Header, int Width, string Description)[] BuiltInColumns =
        {
            ("Duration", "Duration", 80, "How long the step took. Updates while the step is running."),
            ("Verdict", "Verdict", 85, "The verdict of the step in the current or last run."),
            ("Type", "Type", 170, "The kind of test step."),
            ("Progress", "Progress", 110, "How far the step has come: the share of its child steps that have completed, or for a step without children, its running time compared with the last run."),
            ("Outputs", "Outputs", 230, "The step's output values by name."),
        };

        const int Indent = 16;
        static readonly Color RunningBack = Color.FromArgb(255, 232, 140);
        static readonly Color RunningSelectedBack = Color.FromArgb(214, 150, 0);
        static readonly Color PausedBack = Color.FromArgb(255, 200, 150);
        static readonly Color PausedSelectedBack = Color.FromArgb(220, 100, 30);
        static readonly Color BreakpointColor = Color.FromArgb(205, 30, 30);
        static readonly Color PassColor = Color.FromArgb(0, 128, 0);
        static readonly Color FailColor = Color.FromArgb(200, 0, 0);
        static readonly Color WarnColor = Color.FromArgb(200, 110, 0);

        readonly List<Row> rows = new List<Row>();
        readonly Dictionary<object, int> rowIndex = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        readonly Dictionary<(object, string), string> textCache = new Dictionary<(object, string), string>();
        readonly Dictionary<ITestStep, AnnotationCollection> annotationCache = new Dictionary<ITestStep, AnnotationCollection>(ReferenceEqualityComparer.Instance);
        readonly Font boldFont;
        TestPlan plan;
        List<string> columnKeys = new List<string>();
        bool suppressSelectionEvents, selectionEventQueued;

        // Run state.
        readonly Dictionary<ITestStep, StepRunState> runStates = new Dictionary<ITestStep, StepRunState>(ReferenceEqualityComparer.Instance);
        readonly Dictionary<ITestStep, TimeSpan> previousDurations = new Dictionary<ITestStep, TimeSpan>(ReferenceEqualityComparer.Instance);
        readonly Dictionary<ITestStep, List<ITestStep>> descendants = new Dictionary<ITestStep, List<ITestStep>>(ReferenceEqualityComparer.Instance);
        readonly List<ITestStep> runningSteps = new List<ITestStep>();
        StepRunState planState;
        int highlightRow = -1;

        // Mouse / drag and drop.
        int mouseDownRow = -1;
        Point mouseDownPoint;
        MouseEventArgs deferredMouseDown;
        bool dragStarted;
        (ITestStepParent Parent, int Index, int Row, int Position)? dropIndicator; // Position: -1 before, 0 into, 1 after

        // Hooks for the main window (fields rather than properties: these are not designer settings).
        public Func<string> PlanText = () => "";
        public Func<bool> CanDrag = () => true;
        public Func<List<ITestStep>, ITestStepParent, bool> CanDrop = (s, p) => true;
        public Func<Keys, bool> CommandKey;
        public event Action<List<ITestStep>, ITestStepParent, int> StepsDropped;
        public event Action<IDataObject> FileDropped;
        /// <summary> Raised once after the selection or current row has changed. </summary>
        public event Action SelectionSettled;

        /// <summary> Scroll the running step into view. </summary>
        public bool FollowRunningStep = true;

        /// <summary> Whether a step has a breakpoint (drawn as a red dot next to its number). </summary>
        public Func<ITestStep, bool> HasBreakpoint = s => false;

        int pausedRow = -1;
        ITestStep pausedStep;

        /// <summary> The row showing a step: its own, or its nearest shown parent's when it is collapsed. </summary>
        int VisibleRowFor(ITestStep step)
        {
            for (var item = (ITestStepParent)step; item != null; item = (item as ITestStep)?.Parent)
                if (item is ITestStep && rowIndex.TryGetValue(item, out var i)) return i;
            return -1;
        }

        /// <summary> Marks the step the run is paused at (or none), and scrolls it into view. </summary>
        public void SetPaused(ITestStep step)
        {
            pausedStep = step;
            var newRow = step == null ? -1 : VisibleRowFor(step);
            if (pausedRow >= 0 && pausedRow < rows.Count) InvalidateRow(pausedRow);
            pausedRow = newRow;
            if (pausedRow >= 0)
            {
                InvalidateRow(pausedRow);
                EnsureRowVisible(pausedRow);
            }
        }

        public TestPlanGrid()
        {
            VirtualMode = true;
            ReadOnly = true;
            AllowUserToAddRows = false;
            AllowUserToDeleteRows = false;
            AllowUserToResizeRows = false;
            AllowUserToOrderColumns = true;
            AllowDrop = true;
            RowHeadersVisible = false;
            SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            MultiSelect = true;
            EditMode = DataGridViewEditMode.EditProgrammatically;
            BackgroundColor = SystemColors.Window;
            BorderStyle = BorderStyle.None;
            CellBorderStyle = DataGridViewCellBorderStyle.None;
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            EnableHeadersVisualStyles = true;
            StandardTab = true;
            ShowCellToolTips = true;
            DoubleBuffered = true;
            RowTemplate.Height = 22;
            AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(242, 245, 250);
            boldFont = new Font(Font, FontStyle.Bold);

            CellValueNeeded += OnCellValueNeeded;
            CellFormatting += OnCellFormatting;
            CellPainting += OnCellPainting;
            CellToolTipTextNeeded += OnCellToolTipTextNeeded;
            CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.RowIndex < rows.Count && e.ColumnIndex >= 0 && Columns[e.ColumnIndex].Name == "Step" && rows[e.RowIndex].Item is ITestStep && rows[e.RowIndex].HasChildren)
                    Toggle(e.RowIndex);
            };
            CurrentCellChanged += (s, e) => QueueSelectionSettled();
            ColumnDisplayIndexChanged += (s, e) =>
            {
                // Fires once per shifted column; report once, and keep "#" first.
                if (buildingColumns || columnOrderQueued || !IsHandleCreated) return;
                columnOrderQueued = true;
                BeginInvoke(new Action(() =>
                {
                    columnOrderQueued = false;
                    if (Columns.Contains("#") && Columns["#"].DisplayIndex != 0)
                    {
                        buildingColumns = true;
                        Columns["#"].DisplayIndex = 0;
                        buildingColumns = false;
                    }
                    ColumnOrderChanged?.Invoke();
                }));
            };

            SetColumns(new[] { "Duration", "Verdict" });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) boldFont.Dispose();
            base.Dispose(disposing);
        }

        #region Columns

        public IReadOnlyList<string> ColumnKeys => columnKeys;

        public static bool IsMemberColumn(string key) => key.StartsWith(MemberPrefix, StringComparison.Ordinal);

        /// <summary> Header text of a column key: the built-in name, or the display name of the step setting. </summary>
        public string HeaderFor(string key)
        {
            var builtIn = BuiltInColumns.FirstOrDefault(c => c.Key == key);
            if (builtIn.Key != null) return builtIn.Header;
            if (!IsMemberColumn(key)) return key;
            var name = key.Substring(MemberPrefix.Length);
            if (plan != null)
                foreach (var step in plan.ChildTestSteps.RecursivelyGetAllTestSteps(TestStepSearch.All))
                {
                    var member = TypeData.GetTypeData(step).GetMember(name);
                    if (member != null) return member.GetDisplayAttribute()?.Name ?? name;
                }
            return name;
        }

        /// <summary> All columns after "#" in the order they are shown, including "Step" (can be rearranged by dragging headers). </summary>
        public List<string> ColumnOrder =>
            Columns.Cast<DataGridViewColumn>().Where(c => c.Name != "#").OrderBy(c => c.DisplayIndex).Select(c => c.Name).ToList();

        /// <summary> Raised after the user dragged a column header to a new position. </summary>
        public event Action ColumnOrderChanged;
        bool buildingColumns, columnOrderQueued;

        /// <summary> Sets the columns after "#", in order. "Step" may appear anywhere; if missing it comes first. </summary>
        public void SetColumns(IEnumerable<string> keys)
        {
            var order = keys.Where(k => !string.IsNullOrWhiteSpace(k) && k != "#").Distinct().ToList();
            if (!order.Contains("Step")) order.Insert(0, "Step");
            columnKeys = order.Where(k => k != "Step").ToList();
            // Removing the columns also removes the rows, so remember the selection and scroll position.
            var keepSelection = rows.Count > 0 ? SelectedObjects : null;
            var keepCurrent = CurrentObject;
            var firstShown = FirstDisplayedScrollingRowIndex;
            suppressSelectionEvents = true;
            buildingColumns = true;
            Columns.Clear();
            Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "#", HeaderText = "#", Width = 46, Frozen = true, SortMode = DataGridViewColumnSortMode.NotSortable, Resizable = DataGridViewTriState.False,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight, ForeColor = SystemColors.GrayText },
                ToolTipText = "Step number. A red dot marks a breakpoint (F9)."
            });
            foreach (var key in order)
            {
                if (key == "Step")
                {
                    Columns.Add(new DataGridViewTextBoxColumn
                    {
                        Name = "Step", HeaderText = "Step", MinimumWidth = 160, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable
                    });
                    continue;
                }
                var builtIn = BuiltInColumns.FirstOrDefault(c => c.Key == key);
                Columns.Add(new DataGridViewTextBoxColumn
                {
                    Name = key,
                    HeaderText = HeaderFor(key),
                    Width = builtIn.Key != null ? builtIn.Width : 120,
                    SortMode = DataGridViewColumnSortMode.NotSortable,
                    ToolTipText = (builtIn.Description ?? "Step setting") + " Drag the header to move the column."
                });
            }
            buildingColumns = false;
            try
            {
                if (keepSelection != null)
                {
                    RowCount = rows.Count;
                    var currentIndex = keepCurrent != null && rowIndex.TryGetValue(keepCurrent, out var ci) ? ci : 0;
                    SetSelection(keepSelection.Select(o => rowIndex.TryGetValue(o, out var i) ? i : -1).Where(i => i >= 0), currentIndex);
                    TrySetFirstDisplayed(firstShown);
                }
            }
            finally
            {
                suppressSelectionEvents = false;
            }
            ClearValueCache();
            Invalidate();
        }

        #endregion

        #region Rows, selection, expand/collapse

        public object CurrentObject => CurrentCell != null && CurrentCell.RowIndex >= 0 && CurrentCell.RowIndex < rows.Count ? rows[CurrentCell.RowIndex].Item : null;

        /// <summary> Selected items (the plan and/or steps) in plan order. </summary>
        public List<object> SelectedObjects =>
            SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).Where(i => i >= 0 && i < rows.Count).OrderBy(i => i).Select(i => rows[i].Item).ToList();

        /// <summary> Rebuilds the rows from the plan, keeping (or setting) the selection and scroll position. </summary>
        public void Rebuild(TestPlan newPlan, IEnumerable<object> select = null, object current = null)
        {
            bool planChanged = !ReferenceEquals(plan, newPlan);
            var keepSelection = select?.ToList() ?? (planChanged ? new List<object>() : SelectedObjects);
            var keepCurrent = current ?? (planChanged ? null : CurrentObject);
            object firstShown = null;
            if (!planChanged && FirstDisplayedScrollingRowIndex >= 0 && FirstDisplayedScrollingRowIndex < rows.Count)
                firstShown = rows[FirstDisplayedScrollingRowIndex].Item;
            if (planChanged)
            {
                runStates.Clear();
                previousDurations.Clear();
                runningSteps.Clear();
                planState = null;
            }

            plan = newPlan;
            allEnabled = null;
            if (planChanged)
                foreach (DataGridViewColumn column in Columns)
                    if (IsMemberColumn(column.Name)) column.HeaderText = HeaderFor(column.Name);
            rows.Clear();
            rowIndex.Clear();
            descendants.Clear();
            ClearValueCache();

            int number = 0;
            var numbers = new Dictionary<ITestStep, int>(ReferenceEqualityComparer.Instance);
            foreach (var step in plan.ChildTestSteps.RecursivelyGetAllTestSteps(TestStepSearch.All))
                numbers[step] = ++number;

            rows.Add(new Row { Item = plan, Depth = 0, HasChildren = plan.ChildTestSteps.Count > 0, Expanded = true });
            void AddSteps(TestStepList steps, int depth)
            {
                foreach (var step in steps)
                {
                    bool hasChildren = step.ChildTestSteps.Count > 0;
                    bool expanded = hasChildren && ChildItemVisibility.GetVisibility(step) == ChildItemVisibility.Visibility.Visible;
                    rows.Add(new Row { Item = step, Depth = depth, Number = numbers.TryGetValue(step, out var n) ? n : 0, HasChildren = hasChildren, Expanded = expanded });
                    if (expanded) AddSteps(step.ChildTestSteps, depth + 1);
                }
            }
            AddSteps(plan.ChildTestSteps, 1);
            for (int i = 0; i < rows.Count; i++)
                rowIndex[rows[i].Item] = i;

            suppressSelectionEvents = true;
            try
            {
                RowCount = 0;
                RowCount = rows.Count;
                var currentIndex = keepCurrent != null && rowIndex.TryGetValue(keepCurrent, out var ci) ? ci
                    : keepSelection.Select(o => rowIndex.TryGetValue(o, out var si) ? si : -1).FirstOrDefault(i => i >= 0);
                SetSelection(keepSelection.Select(o => rowIndex.TryGetValue(o, out var i) ? i : -1).Where(i => i >= 0), currentIndex);
                if (firstShown != null && rowIndex.TryGetValue(firstShown, out var fi))
                    TrySetFirstDisplayed(fi);
                EnsureRowVisible(currentIndex);
            }
            finally
            {
                suppressSelectionEvents = false;
            }
            UpdateHighlight(false);
            pausedRow = pausedStep == null ? -1 : VisibleRowFor(pausedStep);
            QueueSelectionSettled();
        }

        void SetSelection(IEnumerable<int> selected, int current)
        {
            if (rows.Count == 0) return;
            current = Math.Max(0, Math.Min(rows.Count - 1, current));
            CurrentCell = Rows[current].Cells[1];
            ClearSelection();
            bool any = false;
            foreach (var i in selected)
            {
                Rows[i].Selected = true;
                any = true;
            }
            if (!any) Rows[current].Selected = true;
        }

        /// <summary> Selects the given items, making <paramref name="current"/> the current row. </summary>
        public void SelectObjects(IEnumerable<object> items, object current)
        {
            var indices = items.Select(o => o != null && rowIndex.TryGetValue(o, out var i) ? i : -1).Where(i => i >= 0).ToList();
            var currentIndex = current != null && rowIndex.TryGetValue(current, out var c) ? c : indices.FirstOrDefault();
            suppressSelectionEvents = true;
            try
            {
                SetSelection(indices, currentIndex);
                EnsureRowVisible(currentIndex);
            }
            finally
            {
                suppressSelectionEvents = false;
            }
            QueueSelectionSettled();
        }

        protected override void OnSelectionChanged(EventArgs e)
        {
            base.OnSelectionChanged(e);
            if (!suppressSelectionEvents) QueueSelectionSettled();
        }

        void QueueSelectionSettled()
        {
            if (suppressSelectionEvents || selectionEventQueued) return;
            if (!IsHandleCreated)
            {
                SelectionSettled?.Invoke();
                return;
            }
            selectionEventQueued = true;
            BeginInvoke(new Action(() =>
            {
                selectionEventQueued = false;
                SelectionSettled?.Invoke();
            }));
        }

        void Toggle(int index)
        {
            if (index < 0 || index >= rows.Count || rows[index].Item is not ITestStep step || !rows[index].HasChildren) return;
            ChildItemVisibility.SetVisibility(step, rows[index].Expanded ? ChildItemVisibility.Visibility.Collapsed : ChildItemVisibility.Visibility.Visible);
            Rebuild(plan);
        }

        public void SetAllExpanded(bool expanded)
        {
            if (plan == null) return;
            foreach (var step in plan.ChildTestSteps.RecursivelyGetAllTestSteps(TestStepSearch.All).Where(s => s.ChildTestSteps.Count > 0))
                ChildItemVisibility.SetVisibility(step, expanded ? ChildItemVisibility.Visibility.Visible : ChildItemVisibility.Visibility.Collapsed);
            Rebuild(plan);
        }

        void TrySetFirstDisplayed(int index)
        {
            try
            {
                if (index >= 0 && index < rows.Count) FirstDisplayedScrollingRowIndex = index;
            }
            catch (InvalidOperationException)
            {
                // Not laid out yet.
            }
        }

        void EnsureRowVisible(int index)
        {
            if (index < 0 || index >= rows.Count) return;
            try
            {
                int first = FirstDisplayedScrollingRowIndex;
                int count = Math.Max(1, DisplayedRowCount(false));
                if (first < 0) return;
                if (index < first || index >= first + count)
                    FirstDisplayedScrollingRowIndex = Math.Max(0, index - count / 2);
            }
            catch (InvalidOperationException)
            {
                // Not laid out yet.
            }
        }

        /// <summary> Forgets cached cell texts (call after settings or steps changed). </summary>
        public void RefreshValues()
        {
            ClearValueCache();
            Invalidate();
        }

        void ClearValueCache()
        {
            textCache.Clear();
            annotationCache.Clear();
        }

        void ForgetStep(ITestStep step)
        {
            annotationCache.Remove(step);
            foreach (var key in textCache.Keys.Where(k => ReferenceEquals(k.Item1, step)).ToList())
                textCache.Remove(key);
        }

        void InvalidateItem(object item)
        {
            if (item != null && rowIndex.TryGetValue(item, out var i))
                InvalidateRow(i);
        }

        #endregion

        #region Cell values and painting

        static string StepName(ITestStep step)
        {
            try { return step.GetFormattedName(); }
            catch { return step.Name; }
        }

        internal static string FormatDuration(TimeSpan t)
        {
            if (t.TotalSeconds < 1) return $"{t.TotalMilliseconds:0} ms";
            if (t.TotalSeconds < 60) return $"{t.TotalSeconds:0.00} s";
            if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
            return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        }

        StepRunState StateOf(object item)
        {
            if (item is TestPlan) return planState;
            return item is ITestStep step && runStates.TryGetValue(step, out var state) ? state : null;
        }

        AnnotationCollection AnnotationOf(ITestStep step)
        {
            if (!annotationCache.TryGetValue(step, out var a))
                annotationCache[step] = a = AnnotationCollection.Annotate(step);
            return a;
        }

        string CachedText(ITestStep step, string key, Func<string> compute)
        {
            if (!textCache.TryGetValue((step, key), out var text))
            {
                try { text = compute(); }
                catch { text = ""; }
                textCache[(step, key)] = text;
            }
            return text;
        }

        string ColumnText(object item, string key)
        {
            var state = StateOf(item);
            switch (key)
            {
                case "Duration":
                    if (state == null) return "";
                    return FormatDuration(state.Running ? DateTime.Now - state.Start : state.Duration ?? TimeSpan.Zero);
                case "Verdict":
                    if (state == null) return "";
                    return state.Running ? "Running..." : state.Verdict?.ToString() ?? "";
                case "Progress":
                    var p = ProgressOf(item);
                    return p == null ? "" : p < 0 ? "Running" : $"{p}%";
            }
            if (item is not ITestStep step) return "";
            switch (key)
            {
                case "Type":
                    return CachedText(step, key, () => TypeData.GetTypeData(step).GetDisplayAttribute()?.GetFullName() ?? step.GetType().Name);
                case "Outputs":
                    return CachedText(step, key, () => string.Join("; ",
                        (AnnotationOf(step).Get<IMembersAnnotation>()?.Members ?? Enumerable.Empty<AnnotationCollection>())
                        .Where(m => m.Get<IMemberAnnotation>()?.Member is IMemberData member && member.HasAttribute<OutputAttribute>()
                                    // Every step's own Verdict is an output too; it already has the Verdict column.
                                    && !(member.Name == nameof(TestStep.Verdict) && member.DeclaringType.DescendsTo(TypeData.FromType(typeof(TestStep)))))
                        .Select(m => $"{m.Get<DisplayAttribute>()?.Name}: {AnnotationPropertyGrid.ValueText(m)}")));
            }
            if (IsMemberColumn(key))
            {
                var name = key.Substring(MemberPrefix.Length);
                return CachedText(step, key, () =>
                {
                    var member = AnnotationOf(step).Get<IMembersAnnotation>()?.Members
                        .FirstOrDefault(m => m.Get<IMemberAnnotation>()?.Member.Name == name);
                    return member == null ? "" : AnnotationPropertyGrid.ValueText(member);
                });
            }
            return "";
        }

        /// <summary>
        /// Percent complete, -1 when running without an estimate, null when the step has not run.
        /// Steps with children: the share of their enabled child steps (at any depth) that have completed.
        /// Other steps: running time compared with how long the step took last run.
        /// </summary>
        int? ProgressOf(object item)
        {
            var state = StateOf(item);
            if (state == null) return null;
            if (!state.Running) return 100;
            var children = item is TestPlan ? AllEnabledSteps() : DescendantsOf((ITestStep)item);
            if (children.Count > 0)
            {
                int done = children.Count(c => runStates.TryGetValue(c, out var cs) && cs.Completions > 0);
                return Math.Min(99, done * 100 / children.Count);
            }
            if (item is ITestStep step && previousDurations.TryGetValue(step, out var previous) && previous.TotalMilliseconds > 1)
                return (int)Math.Min(95, (DateTime.Now - state.Start).TotalMilliseconds * 100 / previous.TotalMilliseconds);
            return -1;
        }

        List<ITestStep> DescendantsOf(ITestStep step)
        {
            if (!descendants.TryGetValue(step, out var list))
                descendants[step] = list = step.ChildTestSteps.RecursivelyGetAllTestSteps(TestStepSearch.EnabledOnly).ToList();
            return list;
        }

        List<ITestStep> allEnabled;
        List<ITestStep> AllEnabledSteps() => allEnabled ??= plan.ChildTestSteps.RecursivelyGetAllTestSteps(TestStepSearch.EnabledOnly).ToList();

        void OnCellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= rows.Count) return;
            var row = rows[e.RowIndex];
            var key = Columns[e.ColumnIndex].Name;
            e.Value = key switch
            {
                "#" => row.Number > 0 ? row.Number.ToString() : "",
                "Step" => row.Item is ITestStep step ? StepName(step) : PlanText(),
                _ => ColumnText(row.Item, key)
            };
        }

        static Color VerdictColor(Verdict? verdict) => verdict switch
        {
            Verdict.Pass => PassColor,
            Verdict.Fail => FailColor,
            Verdict.Error => FailColor,
            Verdict.Aborted => WarnColor,
            Verdict.Inconclusive => WarnColor,
            _ => SystemColors.GrayText
        };

        void OnCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= rows.Count) return;
            var row = rows[e.RowIndex];
            if (row.Item is TestPlan) e.CellStyle.Font = boldFont;
            if (row.Item is ITestStep step && !step.Enabled && Columns[e.ColumnIndex].Name != "#")
                e.CellStyle.ForeColor = SystemColors.GrayText;
            if (e.RowIndex == pausedRow)
            {
                e.CellStyle.BackColor = PausedBack;
                e.CellStyle.SelectionBackColor = PausedSelectedBack;
                e.CellStyle.Font = boldFont;
            }
            else if (e.RowIndex == highlightRow)
            {
                e.CellStyle.BackColor = RunningBack;
                e.CellStyle.SelectionBackColor = RunningSelectedBack;
                e.CellStyle.Font = boldFont;
            }
            if (Columns[e.ColumnIndex].Name == "Verdict")
            {
                var state = StateOf(row.Item);
                if (state != null && !state.Running)
                {
                    e.CellStyle.ForeColor = VerdictColor(state.Verdict);
                    if (state.Verdict == Verdict.Fail || state.Verdict == Verdict.Error) e.CellStyle.Font = boldFont;
                }
            }
        }

        Rectangle GlyphRect(Rectangle cellBounds, Row row) =>
            new Rectangle(cellBounds.Left + 4 + Math.Max(0, row.Depth - 1) * Indent, cellBounds.Top + (cellBounds.Height - 11) / 2, 11, 11);

        void OnCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= rows.Count || e.ColumnIndex < 0) return;
            var key = Columns[e.ColumnIndex].Name;
            if (key != "Step" && key != "Progress" && key != "#") return;

            var row = rows[e.RowIndex];
            bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
            e.PaintBackground(e.ClipBounds, selected);
            var b = e.CellBounds;
            var g = e.Graphics;

            if (key == "#")
            {
                // Breakpoint marker to the left of the step number.
                if (row.Item is ITestStep step && HasBreakpoint(step))
                {
                    var mode = g.SmoothingMode;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    var d = Math.Min(11, b.Height - 8);
                    var dot = new Rectangle(b.Left + 4, b.Top + (b.Height - d) / 2, d, d);
                    using (var brush = new SolidBrush(BreakpointColor)) g.FillEllipse(brush, dot);
                    using (var pen = new Pen(Color.White, 1)) g.DrawEllipse(pen, dot);
                    g.SmoothingMode = mode;
                }
                var color = selected ? e.CellStyle.SelectionForeColor : e.CellStyle.ForeColor;
                TextRenderer.DrawText(g, e.Value as string ?? "", e.CellStyle.Font, new Rectangle(b.Left, b.Top, b.Width - 4, b.Height), color,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            }
            else if (key == "Step")
            {
                int x;
                if (row.Item is TestPlan)
                    x = b.Left + 4;
                else
                {
                    var glyph = GlyphRect(b, row);
                    if (row.HasChildren) DrawGlyph(g, glyph, row.Expanded, selected ? e.CellStyle.SelectionForeColor : Color.FromArgb(90, 90, 90));
                    x = glyph.Right + 5;
                }
                var color = selected ? e.CellStyle.SelectionForeColor : e.CellStyle.ForeColor;
                TextRenderer.DrawText(g, e.Value as string ?? "", e.CellStyle.Font, new Rectangle(x, b.Top, Math.Max(0, b.Right - x - 2), b.Height), color,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            else
            {
                var p = ProgressOf(row.Item);
                if (p != null)
                {
                    var bar = new Rectangle(b.Left + 4, b.Top + 5, b.Width - 9, b.Height - 11);
                    if (bar.Width > 4 && bar.Height > 2)
                    {
                        using (var back = new SolidBrush(Color.FromArgb(225, 225, 225))) g.FillRectangle(back, bar);
                        var state = StateOf(row.Item);
                        var fill = state != null && !state.Running && (state.Verdict == Verdict.Fail || state.Verdict == Verdict.Error) ? FailColor
                            : state != null && state.Running ? Color.FromArgb(0, 120, 215) : Color.FromArgb(60, 170, 60);
                        if (p < 0)
                        {
                            using var hatch = new HatchBrush(HatchStyle.WideUpwardDiagonal, fill, Color.FromArgb(225, 225, 225));
                            g.FillRectangle(hatch, bar);
                        }
                        else
                        {
                            using var brush = new SolidBrush(fill);
                            g.FillRectangle(brush, bar.Left, bar.Top, bar.Width * p.Value / 100, bar.Height);
                        }
                        g.DrawRectangle(Pens.Silver, bar);
                    }
                }
            }
            e.Handled = true;
        }

        static void DrawGlyph(Graphics g, Rectangle r, bool expanded, Color color)
        {
            var mode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            if (expanded)
                g.FillPolygon(brush, new[] { new PointF(r.Left + 1, r.Top + 3), new PointF(r.Right - 1, r.Top + 3), new PointF(r.Left + r.Width / 2f, r.Bottom - 2) });
            else
                g.FillPolygon(brush, new[] { new PointF(r.Left + 3, r.Top + 1), new PointF(r.Right - 2, r.Top + r.Height / 2f), new PointF(r.Left + 3, r.Bottom - 1) });
            g.SmoothingMode = mode;
        }

        void OnCellToolTipTextNeeded(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= rows.Count || e.ColumnIndex < 0) return;
            var row = rows[e.RowIndex];
            var key = Columns[e.ColumnIndex].Name;
            if (key == "Step" && row.Item is ITestStep step)
                e.ToolTipText = TypeData.GetTypeData(step).GetDisplayAttribute()?.GetFullName();
            else if (key == "Step" && row.Item is TestPlan p)
                e.ToolTipText = p.Path ?? "Not saved yet";
            else if (key == "Progress")
                e.ToolTipText = ColumnText(row.Item, key);
            else if (key != "#")
                e.ToolTipText = ColumnText(row.Item, key);
        }

        #endregion

        #region Running

        /// <summary> Clears the results of the previous run (keeping its durations as estimates for the progress bars). </summary>
        public void BeginRun()
        {
            foreach (var pair in runStates)
                if (pair.Value.Duration is TimeSpan d) previousDurations[pair.Key] = d;
            runStates.Clear();
            runningSteps.Clear();
            descendants.Clear();
            allEnabled = null;
            planState = new StepRunState { Start = DateTime.Now, Running = true };
            UpdateHighlight(false);
            Invalidate();
        }

        public void StepStarted(ITestStep step, DateTime start)
        {
            if (!runStates.TryGetValue(step, out var state))
                runStates[step] = state = new StepRunState();
            state.Running = true;
            state.Start = start;
            runningSteps.Remove(step);
            runningSteps.Add(step);
            UpdateHighlight(true);
            InvalidateItem(step);
        }

        public void StepCompleted(ITestStep step, TimeSpan duration, Verdict verdict)
        {
            if (!runStates.TryGetValue(step, out var state))
                runStates[step] = state = new StepRunState { Start = DateTime.Now - duration };
            state.Running = false;
            state.Duration = duration;
            state.Verdict = verdict;
            state.Completions++;
            runningSteps.Remove(step);
            ForgetStep(step); // outputs may have changed
            UpdateHighlight(true);
            InvalidateItem(step);
        }

        public void EndRun(Verdict? verdict)
        {
            if (planState != null)
            {
                planState.Running = false;
                planState.Duration = DateTime.Now - planState.Start;
                planState.Verdict = verdict;
            }
            foreach (var step in runningSteps)
                if (runStates.TryGetValue(step, out var state)) state.Running = false;
            runningSteps.Clear();
            ClearValueCache();
            UpdateHighlight(false);
            Invalidate();
        }

        /// <summary> Called regularly while running so durations and progress bars move. </summary>
        public void RunTick()
        {
            foreach (var key in new[] { "Duration", "Progress" })
                if (Columns.Contains(key)) InvalidateColumn(Columns[key].Index);
        }

        /// <summary> Highlights the running step, or its nearest shown parent when it is collapsed. </summary>
        void UpdateHighlight(bool scroll)
        {
            int newRow = -1;
            for (var item = (ITestStepParent)runningSteps.LastOrDefault(); item != null; item = (item as ITestStep)?.Parent)
            {
                if (item is ITestStep && rowIndex.TryGetValue(item, out var i))
                {
                    newRow = i;
                    break;
                }
            }
            if (newRow != highlightRow)
            {
                if (highlightRow >= 0 && highlightRow < rows.Count) InvalidateRow(highlightRow);
                highlightRow = newRow;
                if (highlightRow >= 0) InvalidateRow(highlightRow);
            }
            if (scroll && FollowRunningStep && highlightRow >= 0)
                EnsureRowVisible(highlightRow);
        }

        #endregion

        #region Mouse and keyboard

        bool GlyphHit(int rowIndexHit, Point location)
        {
            var row = rows[rowIndexHit];
            if (!row.HasChildren || row.Item is not ITestStep) return false;
            var cell = GetCellDisplayRectangle(Columns["Step"].Index, rowIndexHit, false);
            var glyph = GlyphRect(cell, row);
            glyph.Inflate(4, 4);
            return glyph.Contains(location);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            dragStarted = false;
            deferredMouseDown = null;
            mouseDownRow = -1;
            var hit = HitTest(e.X, e.Y);
            if (hit.Type == DataGridViewHitTestType.Cell && hit.RowIndex >= 0 && hit.RowIndex < rows.Count)
            {
                if (e.Button == MouseButtons.Left && Columns[hit.ColumnIndex].Name == "Step" && GlyphHit(hit.RowIndex, e.Location))
                {
                    Focus();
                    Toggle(hit.RowIndex);
                    return;
                }
                if (e.Button == MouseButtons.Right)
                {
                    // Right-click selects the row unless it is part of the selection (the context menu opens on mouse up).
                    Focus();
                    if (!Rows[hit.RowIndex].Selected)
                        SelectObjects(new[] { rows[hit.RowIndex].Item }, rows[hit.RowIndex].Item);
                    return;
                }
                if (e.Button == MouseButtons.Left)
                {
                    mouseDownRow = hit.RowIndex;
                    mouseDownPoint = e.Location;
                    // Clicking one of several selected rows keeps the selection until we know it is not a drag.
                    if (ModifierKeys == Keys.None && Rows[hit.RowIndex].Selected && SelectedRows.Count > 1)
                    {
                        deferredMouseDown = e;
                        Focus();
                        return;
                    }
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && mouseDownRow >= 0)
            {
                if (!dragStarted && (Math.Abs(e.X - mouseDownPoint.X) > SystemInformation.DragSize.Width || Math.Abs(e.Y - mouseDownPoint.Y) > SystemInformation.DragSize.Height))
                {
                    dragStarted = true;
                    StartDrag(mouseDownRow);
                }
                return; // no drag-to-select: steps are dragged instead (Shift+click selects ranges)
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (deferredMouseDown != null && !dragStarted)
                base.OnMouseDown(deferredMouseDown);
            deferredMouseDown = null;
            mouseDownRow = -1;
            base.OnMouseUp(e);
        }

        void StartDrag(int row)
        {
            var downRow = row;
            mouseDownRow = -1;
            deferredMouseDown = null;
            if (CanDrag() && rows[downRow].Item is ITestStep step)
            {
                var steps = SelectedObjects.OfType<ITestStep>().ToList();
                if (!steps.Contains(step)) steps = new List<ITestStep> { step };
                DoDragDrop(new StepDragData { Steps = TopLevelOnly(steps) }, DragDropEffects.Move);
                SetDropIndicator(null);
            }
            // The drag swallowed the mouse-up; let the grid finish its own click handling.
            base.OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, mouseDownPoint.X, mouseDownPoint.Y, 0));
        }

        /// <summary> Removes steps whose parent is also in the list (moving/deleting the parent covers them). </summary>
        public static List<ITestStep> TopLevelOnly(List<ITestStep> steps)
        {
            var set = new HashSet<ITestStep>(steps);
            return steps.Where(s =>
            {
                for (var p = s.Parent as ITestStep; p != null; p = p.Parent as ITestStep)
                    if (set.Contains(p)) return false;
                return true;
            }).ToList();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (CommandKey != null && CommandKey(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override bool ProcessDataGridViewKey(KeyEventArgs e)
        {
            if (CurrentCell != null && CurrentCell.RowIndex >= 0 && CurrentCell.RowIndex < rows.Count && e.Modifiers == Keys.None)
            {
                var index = CurrentCell.RowIndex;
                var row = rows[index];
                if (e.KeyCode == Keys.Right && row.Item is ITestStep && row.HasChildren && !row.Expanded)
                {
                    Toggle(index);
                    return true;
                }
                if (e.KeyCode == Keys.Left)
                {
                    if (row.Item is ITestStep && row.HasChildren && row.Expanded)
                        Toggle(index);
                    else if (row.Item is ITestStep step && step.Parent != null)
                        SelectObjects(new object[] { step.Parent }, step.Parent);
                    return true;
                }
            }
            return base.ProcessDataGridViewKey(e);
        }

        #endregion

        #region Drag and drop

        static bool IsFileDrop(IDataObject data) =>
            data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1;

        (ITestStepParent Parent, int Index, int Row, int Position)? DropTargetAt(Point point)
        {
            var hit = HitTest(point.X, point.Y);
            if (hit.RowIndex < 0 || hit.RowIndex >= rows.Count)
                return (plan, plan.ChildTestSteps.Count, rows.Count - 1, 1);
            var row = rows[hit.RowIndex];
            if (row.Item is TestPlan)
                return (plan, plan.ChildTestSteps.Count, hit.RowIndex, 0);
            var step = (ITestStep)row.Item;
            var index = step.Parent.ChildTestSteps.IndexOf(step);
            var rect = GetRowDisplayRectangle(hit.RowIndex, false);
            var relative = (point.Y - rect.Top) / (double)Math.Max(1, rect.Height);
            bool canHaveChildren = TypeData.GetTypeData(step) is var t && (t.HasAttribute<AllowAnyChildAttribute>() || t.HasAttribute<AllowChildrenOfTypeAttribute>());
            if (relative < 0.25)
                return (step.Parent, index, hit.RowIndex, -1);
            if (relative > 0.75 && !(row.Expanded && row.HasChildren))
                return (step.Parent, index + 1, hit.RowIndex, 1);
            if (canHaveChildren)
                return (step, step.ChildTestSteps.Count, hit.RowIndex, 0);
            return relative < 0.5 ? (step.Parent, index, hit.RowIndex, -1) : (step.Parent, index + 1, hit.RowIndex, 1);
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            if (IsFileDrop(e.Data))
            {
                e.Effect = DragDropEffects.Copy;
                return;
            }
            if (e.Data.GetData(typeof(StepDragData)) is not StepDragData drag)
            {
                e.Effect = DragDropEffects.None;
                return;
            }
            var point = PointToClient(new Point(e.X, e.Y));
            AutoScrollWhileDragging(point);
            var target = DropTargetAt(point);
            if (target != null && !CanDrop(drag.Steps, target.Value.Parent))
                target = null;
            e.Effect = target == null ? DragDropEffects.None : DragDropEffects.Move;
            SetDropIndicator(target);
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            SetDropIndicator(null);
            if (IsFileDrop(e.Data))
            {
                FileDropped?.Invoke(e.Data);
                return;
            }
            if (e.Data.GetData(typeof(StepDragData)) is not StepDragData drag) return;
            var target = DropTargetAt(PointToClient(new Point(e.X, e.Y)));
            if (target == null || !CanDrop(drag.Steps, target.Value.Parent)) return;
            StepsDropped?.Invoke(drag.Steps, target.Value.Parent, target.Value.Index);
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            SetDropIndicator(null);
        }

        void AutoScrollWhileDragging(Point point)
        {
            try
            {
                if (point.Y < ColumnHeadersHeight + RowTemplate.Height / 2 && FirstDisplayedScrollingRowIndex > 0)
                    FirstDisplayedScrollingRowIndex--;
                else if (point.Y > ClientSize.Height - RowTemplate.Height / 2 && FirstDisplayedScrollingRowIndex + DisplayedRowCount(false) < rows.Count)
                    FirstDisplayedScrollingRowIndex++;
            }
            catch (InvalidOperationException) { }
        }

        void SetDropIndicator((ITestStepParent Parent, int Index, int Row, int Position)? target)
        {
            if (dropIndicator?.Row == target?.Row && dropIndicator?.Position == target?.Position) return;
            dropIndicator = target;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (dropIndicator is not { } target || target.Row < 0 || target.Row >= rows.Count) return;
            var rect = GetRowDisplayRectangle(target.Row, false);
            if (rect.IsEmpty) return;
            int left = Columns.Contains("Step") ? GetColumnDisplayRectangle(Columns["Step"].Index, false).Left + 4 + Math.Max(0, rows[target.Row].Depth - 1) * Indent : rect.Left;
            using var pen = new Pen(SystemColors.Highlight, 2);
            if (target.Position == 0)
                e.Graphics.DrawRectangle(pen, rect.Left + 1, rect.Top + 1, rect.Width - 3, rect.Height - 3);
            else
            {
                var y = target.Position < 0 ? rect.Top + 1 : rect.Bottom - 1;
                e.Graphics.DrawLine(pen, left, y, rect.Right, y);
            }
        }

        #endregion
    }
}
