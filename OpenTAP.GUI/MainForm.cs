using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using OpenTap.Plugins;

namespace OpenTap.Gui
{
    /// <summary>
    /// The main window: test plan tree on the left, properties of the selected step(s) on the right and the log at the bottom.
    /// Port of the TUI's MainWindow / TestPlanView / PropertiesView / LogPanelView.
    /// </summary>
    public class MainForm : Form
    {
        TestPlan plan = new TestPlan();
        readonly Recovery recovery = new Recovery();
        readonly string startupPath;
        bool unsaved;

        // Test plan view
        readonly TestPlanGrid planGrid;
        readonly GuiRunTracker runTracker = new GuiRunTracker();
        Dictionary<Guid, ITestStep> stepsById = new Dictionary<Guid, ITestStep>();
        ToolStripDropDownButton columnsButton;

        readonly AnnotationPropertyGrid properties;
        readonly LogPanel log;
        readonly SplitContainer mainSplit, topSplit;

        // Status bar
        readonly ToolStripStatusLabel statusPath, statusRun;

        // Running
        TapThread runThread;
        DateTime runStart;
        bool running;
        readonly Timer runTimer = new Timer { Interval = 500 };

        // Breakpoints (by step ID, so they survive reloading the same plan) and pausing.
        readonly HashSet<Guid> breakpoints = new HashSet<Guid>();
        readonly System.Threading.ManualResetEventSlim resumeRun = new System.Threading.ManualResetEventSlim(true);
        bool paused;
        string pausedStepName;
        ToolStripMenuItem runMenuItem;
        ToolStripButton runToolButton;

        // Settings pane width is remembered once the window has its final layout.
        bool layoutReady;

        // After "Jump to Source", the output to select once the source step is shown.
        string pendingSelectMember;

        // Menu/toolbar items that are enabled or disabled depending on the state.
        readonly List<(ToolStripItem Item, Func<bool> CanExecute)> commands = new List<(ToolStripItem, Func<bool>)>();

        public TestPlan Plan => plan;

        // Editor panels (Operator Panel, Live Results, ...) shown as tabs next to Settings or floating.
        TabControl settingsTabs;
        PanelHost panels;
        ToolStripMenuItem panelsMenu;

        public MainForm(string planPath)
        {
            startupPath = planPath;
            Text = "OpenTAP GUI";
            Size = new Size(1300, 850);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            LoadIcon();

            // --- Test plan view ---
            planGrid = new TestPlanGrid
            {
                Dock = DockStyle.Fill,
                PlanText = () => plan.Name + (unsaved ? " *" : ""),
                CanDrag = CanEdit,
                CanDrop = IsValidDrop,
                CommandKey = OnPlanKey,
                FollowRunningStep = GuiSettings.Current.FollowRunningStep,
                HasBreakpoint = s => { lock (breakpoints) return breakpoints.Contains(s.Id); },
            };
            planGrid.SetColumns((GuiSettings.Current.TestPlanColumns ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
            planGrid.SelectionSettled += () =>
            {
                ShowSelectionProperties();
                UpdateCommandState();
                NotifyPanels(selection: true);
            };
            planGrid.ColumnOrderChanged += SaveColumnOrder;
            planGrid.StepsDropped += MoveSteps;
            planGrid.FileDropped += OpenDroppedFile;
            planGrid.ContextMenuStrip = BuildTreeContextMenu();

            // Live run information for the test plan view (result listener callbacks come from other threads).
            runTracker.StepStarted = run => OnUi(() =>
            {
                if (stepsById.TryGetValue(run.TestStepId, out var step))
                    planGrid.StepStarted(step, run.StartTime.Kind == DateTimeKind.Utc ? run.StartTime.ToLocalTime() : run.StartTime);
            });
            runTracker.StepCompleted = run => OnUi(() =>
            {
                if (stepsById.TryGetValue(run.TestStepId, out var step))
                    planGrid.StepCompleted(step, run.Duration, run.Verdict);
            });

            // --- Properties ---
            properties = new AnnotationPropertyGrid { Dock = DockStyle.Fill, IsReadOnly = () => plan.Locked };
            properties.PropertiesChanged += () =>
            {
                MarkChanged();
                UpdateNodeTexts();
            };
            properties.ExtraMenuItems = PropertyMenuItems;

            // --- Log ---
            log = new LogPanel { Dock = DockStyle.Fill };

            topSplit = new SplitContainer { Dock = DockStyle.Fill };
            topSplit.SplitterMoved += (s, e) => RememberSettingsPaneWidth();
            topSplit.Panel1.Controls.Add(planGrid);
            topSplit.Panel1.Controls.Add(BuildPlanToolbar());
            settingsTabs = new TabControl { Dock = DockStyle.Fill, ShowToolTips = true };
            var settingsTab = new TabPage("Settings");
            settingsTab.Controls.Add(properties);
            settingsTabs.TabPages.Add(settingsTab);
            topSplit.Panel2.Controls.Add(settingsTabs);

            mainSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            mainSplit.Panel1.Controls.Add(topSplit);
            mainSplit.Panel2.Controls.Add(log);
            mainSplit.Panel2.Controls.Add(new Label { Text = "Log", Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) });

            // --- Status bar ---
            var status = new StatusStrip();
            statusPath = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            statusRun = new ToolStripStatusLabel { Text = "Ready", BorderSides = ToolStripStatusLabelBorderSides.Left };
            status.Items.AddRange(new ToolStripItem[] { statusPath, statusRun });

            // Docking: the last added control is docked first (outermost).
            Controls.Add(mainSplit);
            Controls.Add(BuildToolbar());
            var menu = BuildMenu();
            Controls.Add(menu);
            MainMenuStrip = menu;
            Controls.Add(status);

            runTimer.Tick += (s, e) =>
            {
                statusRun.Text = paused
                    ? $"Paused at breakpoint: {pausedStepName}  -  F5 to continue"
                    : $"Running...  {DateTime.Now - runStart:hh\\:mm\\:ss}";
                planGrid.RunTick();
            };

            // Opening .TapPlan files by dropping them on the window.
            DragEnter += (s, e) => e.Effect = IsPlanFileDrop(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (s, e) => OpenDroppedFile(e.Data);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            mainSplit.SplitterDistance = (int)(mainSplit.Height * 0.68);
            var settingsPercent = Math.Max(10, Math.Min(90, GuiSettings.Current.SettingsPanePercent));
            topSplit.SplitterDistance = (int)(topSplit.Width * (100 - settingsPercent) / 100);
            layoutReady = true;
            LoadStartupPlan();
            RestorePanels();
            UpdateCommandState();
            planGrid.Focus();
        }

        void LoadIcon()
        {
            try
            {
                var dir = Path.GetDirectoryName(typeof(MainForm).Assembly.Location) ?? "";
                var file = new[] { Path.Combine(dir, "icon.png"), Path.Combine(dir, "Packages", "GUI", "icon.png") }.FirstOrDefault(File.Exists);
                if (file == null) return;
                using var source = new Bitmap(file);
                using var small = new Bitmap(source, new Size(32, 32));
                Icon = Icon.FromHandle(small.GetHicon());
            }
            catch
            {
                // Default icon.
            }
        }

        #region Menus and toolbar

        ToolStripMenuItem Item(string text, Action action, Keys shortcut = Keys.None, string shortcutDisplay = null, Func<bool> canExecute = null)
        {
            var item = new ToolStripMenuItem(text) { ShortcutKeys = shortcut };
            if (shortcutDisplay != null) item.ShortcutKeyDisplayString = shortcutDisplay;
            item.Click += (s, e) => action();
            if (canExecute != null) commands.Add((item, canExecute));
            return item;
        }

        ToolStripButton ToolButton(string text, string tooltip, Action action, Func<bool> canExecute = null)
        {
            var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = tooltip };
            b.Click += (s, e) => action();
            if (canExecute != null) commands.Add((b, canExecute));
            return b;
        }

        bool CanEdit() => !running && !plan.Locked;
        bool HasSelectedStep() => SelectedSteps().Count > 0;

        MenuStrip BuildMenu()
        {
            var menu = new MenuStrip();

            var file = new ToolStripMenuItem("&File");
            recentMenu = new ToolStripMenuItem("&Recent Test Plans");
            recentMenu.DropDownItems.Add("(none)");
            file.DropDownItems.AddRange(new ToolStripItem[]
            {
                Item("&New", NewPlan, Keys.Control | Keys.N, canExecute: () => !running),
                Item("&Open...", OpenPlan, Keys.Control | Keys.O, canExecute: () => !running),
                Item("&Save", () => SavePlan(plan.Path), Keys.Control | Keys.S),
                Item("Save &As...", () => SavePlan(null), Keys.Control | Keys.Shift | Keys.S),
                new ToolStripSeparator(),
                recentMenu,
                new ToolStripSeparator(),
                Item("E&xit", Close, Keys.Alt | Keys.F4),
            });
            file.DropDownOpening += (s, e) => BuildRecentMenu();

            // Clipboard / delete / move keys are only handled while the tree has focus (see OnTreeKeyDown), so they
            // are display-only here and keep working normally in text boxes.
            var edit = new ToolStripMenuItem("&Edit");
            edit.DropDownItems.AddRange(new ToolStripItem[]
            {
                Item("&Insert Step...", () => InsertStep(false), Keys.Control | Keys.I, canExecute: CanEdit),
                Item("Insert &Child Step...", () => InsertStep(true), Keys.Control | Keys.Shift | Keys.I, canExecute: () => CanEdit() && CanHaveChildren(planGrid.CurrentObject)),
                new ToolStripSeparator(),
                Item("Cu&t", CutStep, shortcutDisplay: "Ctrl+X", canExecute: () => CanEdit() && planGrid.CurrentObject is ITestStep),
                Item("&Copy", CopyStep, shortcutDisplay: "Ctrl+C", canExecute: () => planGrid.CurrentObject is ITestStep),
                Item("&Paste", PasteStep, shortcutDisplay: "Ctrl+V", canExecute: CanEdit),
                Item("&Delete", DeleteSteps, shortcutDisplay: "Del", canExecute: () => CanEdit() && HasSelectedStep()),
                new ToolStripSeparator(),
                Item("Move &Up", () => MoveSelected(-1), shortcutDisplay: "Alt+Up", canExecute: () => CanEdit() && planGrid.CurrentObject is ITestStep),
                Item("Move Do&wn", () => MoveSelected(1), shortcutDisplay: "Alt+Down", canExecute: () => CanEdit() && planGrid.CurrentObject is ITestStep),
                Item("&Enable / Disable", ToggleEnabled, shortcutDisplay: "Space", canExecute: () => CanEdit() && HasSelectedStep()),
                new ToolStripSeparator(),
                Item("Test &Plan Settings", () => SelectObject(plan)),
            });

            var view = new ToolStripMenuItem("&View");
            view.DropDownOpening += (s, e) => BuildPanelsMenu();
            view.DropDownItems.AddRange(new ToolStripItem[]
            {
                Item("&Expand All", () => planGrid.SetAllExpanded(true)),
                Item("&Collapse All", () => planGrid.SetAllExpanded(false)),
                new ToolStripSeparator(),
                Item("C&lear Log", log.ClearLog),
                new ToolStripSeparator(),
                (panelsMenu = new ToolStripMenuItem("&Panels")),
            });

            var run = new ToolStripMenuItem("&Run");
            run.DropDownItems.AddRange(new ToolStripItem[]
            {
                (runMenuItem = Item("&Run Test Plan", () => RunPlan(false), Keys.F5, canExecute: () => !running || paused)),
                Item("Run &Selected Steps", () => RunPlan(true), Keys.Control | Keys.F5, canExecute: () => !running && HasSelectedStep()),
                new ToolStripSeparator(),
                Item("&Abort", AbortPlan, Keys.Shift | Keys.F5, canExecute: () => running),
                new ToolStripSeparator(),
                Item("Toggle &Breakpoint", ToggleBreakpoint, Keys.F9, canExecute: HasSelectedStep),
                Item("&Clear All Breakpoints", ClearBreakpoints, canExecute: () => { lock (breakpoints) return breakpoints.Count > 0; }),
            });

            menu.Items.AddRange(new ToolStripItem[] { file, edit, view, run });
            foreach (var group in BuildSettingsMenus())
                menu.Items.Add(group);

            var tools = new ToolStripMenuItem("&Tools");
            tools.DropDownItems.AddRange(new ToolStripItem[]
            {
                Item("&Results Viewer...", () => ShowTool(ref resultsViewer, () => new ResultsViewerForm())),
                Item("&Package Manager...", () => ShowTool(ref packageManager, () => new PackageManagerForm())),
            });
            menu.Items.Add(tools);

            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.AddRange(new ToolStripItem[]
            {
                Item("OpenTAP &Documentation", () => OpenUrl("https://doc.opentap.io")),
                Item("&About OpenTAP GUI", ShowAbout),
            });
            menu.Items.Add(help);

            foreach (ToolStripMenuItem top in menu.Items)
                top.DropDownOpening += (s, e) => UpdateCommandState();

            return menu;
        }

        /// <summary> One menu per settings group (Bench, Settings, ...), like the TUI. </summary>
        IEnumerable<ToolStripMenuItem> BuildSettingsMenus()
        {
            var groups = new Dictionary<string, List<ToolStripMenuItem>>();
            var settingsTypes = TypeData.GetDerivedTypes<ComponentSettings>()
                .Where(x => x.CanCreateInstance && (x.GetAttribute<BrowsableAttribute>()?.Browsable ?? true))
                .OfType<TypeData>();
            foreach (var type in settingsTypes)
            {
                try
                {
                    var group = type.GetAttribute<SettingsGroupAttribute>()?.GroupName ?? "Settings";
                    var name = type.GetDisplayAttribute().Name;
                    var captured = type;
                    var item = new ToolStripMenuItem(name + "...");
                    item.Click += (s, e) => OpenSettings(captured, name);
                    if (!groups.TryGetValue(group, out var list))
                        groups[group] = list = new List<ToolStripMenuItem>();
                    list.Add(item);
                }
                catch (Exception ex)
                {
                    GuiAction.Log.Debug(ex);
                }
            }

            if (!groups.ContainsKey("Bench"))
                groups["Bench"] = new List<ToolStripMenuItem>();

            foreach (var group in groups.OrderBy(g => g.Key == "Bench" ? 0 : 1).ThenBy(g => g.Key))
            {
                var menu = new ToolStripMenuItem(group.Key);
                menu.DropDownItems.AddRange(group.Value.OrderBy(i => i.Text).ToArray());
                if (group.Key == "Bench")
                {
                    if (menu.DropDownItems.Count > 0) menu.DropDownItems.Add(new ToolStripSeparator());
                    menu.DropDownItems.Add(Item("&Profiles...", () =>
                    {
                        using var form = new ProfilesForm("Bench");
                        form.ShowDialog(this);
                        AfterSettingsChanged();
                    }));
                }
                yield return menu;
            }
        }

        Form resultsViewer, packageManager;

        /// <summary> Opens a tool window next to the main window, or brings the open one to the front. </summary>
        void ShowTool(ref Form window, Func<Form> create)
        {
            if (window == null || window.IsDisposed)
            {
                window = create();
                window.StartPosition = FormStartPosition.CenterParent;
                window.Show(this);
            }
            else
            {
                if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
                window.Activate();
            }
        }

        void OpenSettings(TypeData type, string name)
        {
            try
            {
                var settings = ComponentSettings.GetCurrent(type.Load());
                if (settings == null) return;
                using Form form = settings is IList list ? new ListSettingsForm(name, list) : new ComponentSettingsForm(settings, name);
                form.ShowDialog(this);
            }
            catch (Exception ex)
            {
                GuiAction.Log.Error(ex);
                MessageBox.Show(this, ex.Message, name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            AfterSettingsChanged();
        }

        void AfterSettingsChanged()
        {
            // Instruments/DUTs may have been renamed, added or removed.
            ShowSelectionProperties();
            UpdateNodeTexts();
        }

        ToolStrip BuildToolbar()
        {
            var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            bar.Items.AddRange(new ToolStripItem[]
            {
                ToolButton("New", "New test plan (Ctrl+N)", NewPlan, () => !running),
                ToolButton("Open", "Open test plan (Ctrl+O)", OpenPlan, () => !running),
                ToolButton("Save", "Save test plan (Ctrl+S)", () => SavePlan(plan.Path)),
                new ToolStripSeparator(),
                ToolButton("Insert Step", "Insert a new step after the selected step (Ctrl+I)", () => InsertStep(false), CanEdit),
                ToolButton("Insert Child", "Insert a new step as a child of the selected step (Ctrl+Shift+I)", () => InsertStep(true), () => CanEdit() && CanHaveChildren(planGrid.CurrentObject)),
                ToolButton("Delete", "Delete the selected steps (Del)", DeleteSteps, () => CanEdit() && HasSelectedStep()),
                ToolButton("▲", "Move up (Alt+Up)", () => MoveSelected(-1), () => CanEdit() && planGrid.CurrentObject is ITestStep),
                ToolButton("▼", "Move down (Alt+Down)", () => MoveSelected(1), () => CanEdit() && planGrid.CurrentObject is ITestStep),
                new ToolStripSeparator(),
                (runToolButton = ToolButton("▶ Run", "Run the test plan (F5)", () => RunPlan(false), () => !running || paused)),
                ToolButton("■ Abort", "Abort the running test plan (Shift+F5)", AbortPlan, () => running),
            });
            return bar;
        }

        void UpdateCommandState()
        {
            if (runMenuItem != null) runMenuItem.Text = paused ? "&Continue" : "&Run Test Plan";
            if (runToolButton != null)
            {
                runToolButton.Text = paused ? "▶ Continue" : "▶ Run";
                runToolButton.ToolTipText = paused ? "Continue from the breakpoint (F5)" : "Run the test plan (F5)";
            }
            foreach (var (item, canExecute) in commands)
            {
                try { item.Enabled = canExecute(); }
                catch { item.Enabled = false; }
            }
        }

        ContextMenuStrip BuildTreeContextMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[]
            {
                Item("Insert Step...", () => InsertStep(false), canExecute: CanEdit),
                Item("Insert Child Step...", () => InsertStep(true), canExecute: () => CanEdit() && CanHaveChildren(planGrid.CurrentObject)),
                new ToolStripSeparator(),
                Item("Cut", CutStep, shortcutDisplay: "Ctrl+X", canExecute: () => CanEdit() && planGrid.CurrentObject is ITestStep),
                Item("Copy", CopyStep, shortcutDisplay: "Ctrl+C", canExecute: () => planGrid.CurrentObject is ITestStep),
                Item("Paste", PasteStep, shortcutDisplay: "Ctrl+V", canExecute: CanEdit),
                Item("Delete", DeleteSteps, shortcutDisplay: "Del", canExecute: () => CanEdit() && HasSelectedStep()),
                new ToolStripSeparator(),
                Item("Enable / Disable", ToggleEnabled, shortcutDisplay: "Space", canExecute: () => CanEdit() && HasSelectedStep()),
                Item("Run Selected Steps", () => RunPlan(true), canExecute: () => !running && HasSelectedStep()),
                new ToolStripSeparator(),
                Item("Toggle Breakpoint", ToggleBreakpoint, shortcutDisplay: "F9", canExecute: HasSelectedStep),
            });
            menu.Opening += (s, e) => UpdateCommandState();
            return menu;
        }

        #endregion

        #region Test plan view

        void RebuildTree(object select = null) =>
            planGrid.Rebuild(plan, select == null ? null : new[] { select }, select);

        void UpdateNodeTexts() => planGrid.RefreshValues();

        void SelectObject(object obj) => planGrid.SelectObjects(new[] { obj }, obj);

        /// <summary> Selected steps in plan order. </summary>
        List<ITestStep> SelectedSteps() => planGrid.SelectedObjects.OfType<ITestStep>().ToList();

        static List<ITestStep> TopLevelOnly(List<ITestStep> steps) => TestPlanGrid.TopLevelOnly(steps);

        void ShowSelectionProperties()
        {
            var steps = SelectedSteps();
            if (steps.Count > 1)
                properties.LoadProperties(steps.ToArray());
            else if (steps.Count == 1)
                properties.LoadProperties(steps[0]);
            else if (planGrid.CurrentObject is TestPlan)
                properties.LoadProperties(plan);
            else
                properties.LoadProperties(null);
            if (pendingSelectMember != null)
            {
                properties.SelectMember(pendingSelectMember);
                pendingSelectMember = null;
            }
        }

        /// <summary> Keys handled while the test plan view has focus. </summary>
        bool OnPlanKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Delete: if (CanEdit()) DeleteSteps(); return true;
                case Keys.Control | Keys.C: CopyStep(); return true;
                case Keys.Control | Keys.X: if (CanEdit()) CutStep(); return true;
                case Keys.Control | Keys.V: if (CanEdit()) PasteStep(); return true;
                case Keys.Alt | Keys.Up: if (CanEdit()) MoveSelected(-1); return true;
                case Keys.Alt | Keys.Down: if (CanEdit()) MoveSelected(1); return true;
                case Keys.Space: if (CanEdit()) ToggleEnabled(); return true;
                case Keys.Enter: properties.Focus(); return true;
                default: return false;
            }
        }

        static bool CanHaveChildren(object obj)
        {
            if (obj is TestPlan) return true;
            if (obj is not ITestStep step) return false;
            var type = TypeData.GetTypeData(step);
            return type.HasAttribute<AllowAnyChildAttribute>() || type.HasAttribute<AllowChildrenOfTypeAttribute>();
        }

        static bool AllowChild(ITestStepParent parent, ITestStep child) =>
            TestStepList.AllowChild(TypeData.GetTypeData(parent), TypeData.GetTypeData(child));

        /// <summary> "Test Plan" title with the Columns drop-down and the Follow running step check box. </summary>
        ToolStrip BuildPlanToolbar()
        {
            var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
            bar.Items.Add(new ToolStripLabel("Test Plan") { Font = new Font(Font, FontStyle.Bold) });

            columnsButton = new ToolStripDropDownButton("Columns") { Alignment = ToolStripItemAlignment.Right, ToolTipText = "Choose the columns shown for each step" };
            columnsButton.DropDown.Closing += (s, e) =>
            {
                // Keep the list open while ticking several columns.
                if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
            };
            columnsButton.DropDownOpening += (s, e) => BuildColumnsMenu();
            columnsButton.DropDownItems.Add("...");

            var follow = new CheckBox { Text = "Follow running step", Checked = planGrid.FollowRunningStep, AutoSize = true, BackColor = Color.Transparent, Padding = new Padding(4, 0, 0, 0) };
            follow.CheckedChanged += (s, e) =>
            {
                planGrid.FollowRunningStep = follow.Checked;
                GuiSettings.Current.FollowRunningStep = follow.Checked;
                SaveGuiSettings();
            };

            bar.Items.Add(columnsButton);
            bar.Items.Add(new ToolStripControlHost(follow) { Alignment = ToolStripItemAlignment.Right, ToolTipText = "Scroll so the running step is always visible" });
            return bar;
        }

        void BuildColumnsMenu()
        {
            var items = columnsButton.DropDownItems;
            items.Clear();
            foreach (var column in TestPlanGrid.BuiltInColumns)
            {
                var key = column.Key;
                var item = new ToolStripMenuItem(column.Header) { CheckOnClick = true, Checked = planGrid.ColumnKeys.Contains(key), ToolTipText = column.Description };
                item.CheckedChanged += (s, e) => SetColumnVisible(key, item.Checked);
                items.Add(item);
            }
            var memberColumns = planGrid.ColumnKeys.Where(TestPlanGrid.IsMemberColumn).ToList();
            if (memberColumns.Any())
            {
                items.Add(new ToolStripSeparator());
                foreach (var key in memberColumns)
                {
                    var item = new ToolStripMenuItem(planGrid.HeaderFor(key)) { CheckOnClick = true, Checked = true, ToolTipText = "Step setting. Untick to remove the column." };
                    item.CheckedChanged += (s, e) => SetColumnVisible(key, item.Checked);
                    items.Add(item);
                }
            }
            items.Add(new ToolStripSeparator());
            items.Add(new ToolStripMenuItem("Tip: right-click a step setting and choose \"Show as Column\"") { Enabled = false });
        }

        void SetColumnVisible(string key, bool visible)
        {
            // Keep the order the user arranged; new columns go at the end.
            var keys = planGrid.ColumnOrder;
            if (visible && !keys.Contains(key)) keys.Add(key);
            if (!visible) keys.Remove(key);
            planGrid.SetColumns(keys);
            SaveColumnOrder();
        }

        void SaveColumnOrder()
        {
            GuiSettings.Current.TestPlanColumns = string.Join(",", planGrid.ColumnOrder);
            SaveGuiSettings();
        }

        void RememberSettingsPaneWidth()
        {
            if (!layoutReady || topSplit.Width <= 0) return;
            GuiSettings.Current.SettingsPanePercent = Math.Round(100.0 * (topSplit.Width - topSplit.SplitterDistance) / topSplit.Width, 1);
            SaveGuiSettings();
        }

        #region Recent test plans

        ToolStripMenuItem recentMenu;
        const int RecentCount = 5;

        static List<string> RecentPlans() =>
            (GuiSettings.Current.RecentPlans ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();

        static void AddRecentPlan(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { path = Path.GetFullPath(path); } catch { }
            var list = RecentPlans();
            list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, path);
            GuiSettings.Current.RecentPlans = string.Join("|", list.Take(RecentCount));
            SaveGuiSettings();
        }

        void BuildRecentMenu()
        {
            recentMenu.DropDownItems.Clear();
            var list = RecentPlans();
            if (list.Count == 0)
            {
                recentMenu.DropDownItems.Add(new ToolStripMenuItem("(none yet)") { Enabled = false });
                return;
            }
            for (int i = 0; i < list.Count; i++)
            {
                var path = list[i];
                var item = new ToolStripMenuItem($"&{i + 1}  {Path.GetFileName(path)}") { ToolTipText = path, Enabled = !running };
                item.ShortcutKeyDisplayString = Path.GetDirectoryName(path);
                item.Click += (s, e) => OpenRecent(path);
                recentMenu.DropDownItems.Add(item);
            }
        }

        void OpenRecent(string path)
        {
            if (running) return;
            if (!File.Exists(path))
            {
                if (MessageBox.Show(this, $"'{path}' no longer exists.\n\nRemove it from the list?", "Recent Test Plans", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    GuiSettings.Current.RecentPlans = string.Join("|", RecentPlans().Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
                    SaveGuiSettings();
                }
                return;
            }
            if (!ConfirmDiscard()) return;
            LoadPlanFile(path);
        }

        #endregion

        #region Breakpoints

        void ToggleBreakpoint()
        {
            var steps = SelectedSteps();
            if (steps.Count == 0) return;
            lock (breakpoints)
            {
                // If any selected step has no breakpoint, set them all; otherwise clear them all.
                bool set = steps.Any(s => !breakpoints.Contains(s.Id));
                foreach (var step in steps)
                    if (set) breakpoints.Add(step.Id); else breakpoints.Remove(step.Id);
            }
            planGrid.Invalidate();
            UpdateCommandState();
        }

        void ClearBreakpoints()
        {
            lock (breakpoints) breakpoints.Clear();
            planGrid.Invalidate();
            UpdateCommandState();
        }

        /// <summary> Called on the test plan thread as each step is about to start; blocks while paused at a breakpoint. </summary>
        void OnBreakOffered(object sender, BreakOfferedEventArgs e)
        {
            if (!e.IsTestStepStarting || e.TestStepRun == null) return;
            bool hit;
            lock (breakpoints) hit = breakpoints.Contains(e.TestStepRun.TestStepId);
            if (!hit) return;

            resumeRun.Reset();
            var id = e.TestStepRun.TestStepId;
            OnUi(() => EnterPause(id));
            GuiAction.Log.Info("Paused at breakpoint before '{0}'. Press F5 (Continue) to resume.",
                stepsById.TryGetValue(id, out var s) ? s.GetFormattedName() : id.ToString());
            System.Threading.WaitHandle.WaitAny(new[] { resumeRun.WaitHandle, TapThread.Current.AbortToken.WaitHandle });
            OnUi(LeavePause);
        }

        void EnterPause(Guid stepId)
        {
            paused = true;
            stepsById.TryGetValue(stepId, out var step);
            pausedStepName = step?.GetFormattedName() ?? "?";
            planGrid.SetPaused(step);
            statusRun.ForeColor = Color.FromArgb(200, 90, 0);
            statusRun.Text = $"Paused at breakpoint: {pausedStepName}  -  F5 to continue";
            UpdateCommandState();
            if (step != null) planGrid.SelectObjects(new[] { step }, step);
        }

        void LeavePause()
        {
            paused = false;
            planGrid.SetPaused(null);
            statusRun.ForeColor = SystemColors.ControlText;
            UpdateCommandState();
        }

        void ContinueRun()
        {
            if (paused) resumeRun.Set();
        }

        #endregion

        static void SaveGuiSettings()
        {
            try { GuiSettings.Current.Save(); }
            catch (Exception ex) { GuiAction.Log.Debug(ex); }
        }

        /// <summary>
        /// Right-click on a step setting: "Jump to Source" when the value comes from another step's output, and
        /// show it as a test plan column (or remove that column).
        /// </summary>
        IEnumerable<ToolStripItem> PropertyMenuItems(AnnotationPropertyDescriptor descriptor)
        {
            var member = descriptor.Annotation.Get<IMemberAnnotation>()?.Member;
            var firstStep = properties.Source as ITestStep ?? (properties.Source as ITestStep[])?.FirstOrDefault();
            if (member == null || firstStep == null || TypeData.GetTypeData(firstStep).GetMember(member.Name) == null)
                yield break;

            if (properties.Source is ITestStep step)
            {
                var source = OutputSourceOf(step, member, descriptor.Annotation);
                if (source != null)
                {
                    var (sourceStep, sourceMember) = source.Value;
                    var stepName = sourceStep is ITestStep ss ? ss.GetFormattedName() : "Test Plan";
                    var outputName = sourceMember?.GetDisplayAttribute()?.Name ?? sourceMember?.Name;
                    var jump = new ToolStripMenuItem($"Jump to Source: {stepName}{(outputName != null ? " \u2192 " + outputName : "")}")
                    {
                        Font = new Font(Font, FontStyle.Bold)
                    };
                    jump.Click += (s, e) => JumpTo(sourceStep, sourceMember?.Name);
                    yield return jump;
                    yield return new ToolStripSeparator();
                }
            }

            var key = TestPlanGrid.MemberPrefix + member.Name;
            bool shown = planGrid.ColumnKeys.Contains(key);
            var name = member.GetDisplayAttribute()?.Name ?? member.Name;
            var item = new ToolStripMenuItem(shown ? $"Remove '{name}' Column from Test Plan" : $"Show '{name}' as Column in Test Plan");
            item.Click += (s, e) => SetColumnVisible(key, !shown);
            yield return item;
        }

        /// <summary> Where a setting gets its value: an assigned output (Assign Output) or an Input&lt;T&gt; setting's step. </summary>
        static (ITestStepParent Step, IMemberData Member)? OutputSourceOf(ITestStep step, IMemberData member, AnnotationCollection annotation)
        {
            try
            {
                var relation = InputOutputRelation.GetRelations(step).FirstOrDefault(r => ReferenceEquals(r.InputObject, step) && r.InputMember?.Name == member.Name);
                if (relation?.OutputObject != null)
                    return (relation.OutputObject, relation.OutputMember);
            }
            catch (Exception ex)
            {
                GuiAction.Log.Debug(ex);
            }
            if (annotation.Get<IObjectValueAnnotation>()?.Value is IInput input && input.Step != null)
                return (input.Step, input.Property);
            return null;
        }

        /// <summary> Shows and selects a step (expanding its parents) and then selects one of its settings. </summary>
        void JumpTo(ITestStepParent target, string memberName)
        {
            for (var p = (target as ITestStep)?.Parent as ITestStep; p != null; p = p.Parent as ITestStep)
                ChildItemVisibility.SetVisibility(p, ChildItemVisibility.Visibility.Visible);
            pendingSelectMember = memberName;
            planGrid.Rebuild(plan, new object[] { target }, target);
        }

        void OnUi(Action action)
        {
            try
            {
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // Window closed.
            }
        }

        #endregion

        #region Editing

        void MarkChanged()
        {
            unsaved = true;
            UpdateTitle();
            recovery.Save(plan);
        }

        void UpdateTitle()
        {
            Text = $"{plan.Name}{(unsaved ? " *" : "")} - OpenTAP GUI";
            statusPath.Text = plan.Path ?? "Not saved yet";
            if (planGrid.RowCount > 0)
                planGrid.InvalidateRow(0);
        }

        void InsertStep(bool asChild)
        {
            if (!CanEdit()) return;
            var selected = planGrid.CurrentObject;
            ITestStepParent parent;
            int index;
            if (asChild)
            {
                if (!CanHaveChildren(selected)) return;
                parent = (ITestStepParent)selected;
                index = parent.ChildTestSteps.Count;
            }
            else if (selected is ITestStep s)
            {
                parent = s.Parent;
                index = parent.ChildTestSteps.IndexOf(s) + 1;
            }
            else
            {
                parent = plan;
                index = plan.ChildTestSteps.Count;
            }

            using var dialog = new AddPluginDialog(TypeData.FromType(typeof(ITestStep)), asChild ? "Insert Child Step" : "Insert Step", TypeData.GetTypeData(parent));
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.PluginType == null)
                return;
            try
            {
                var step = (ITestStep)dialog.PluginType.CreateInstance();
                parent.ChildTestSteps.Insert(index, step);
                if (parent is ITestStep parentStep)
                    ChildItemVisibility.SetVisibility(parentStep, ChildItemVisibility.Visibility.Visible);
                MarkChanged();
                RebuildTree(step);
            }
            catch (Exception ex)
            {
                GuiAction.Log.Error(ex.Message);
                GuiAction.Log.Debug(ex);
                MessageBox.Show(this, ex.Message, "Insert Step", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void DeleteSteps(bool confirm, List<ITestStep> toDelete = null)
        {
            var steps = TopLevelOnly(toDelete ?? SelectedSteps());
            if (steps.Count == 0) return;
            if (confirm)
            {
                var question = steps.Count == 1 ? $"Delete '{steps[0].GetFormattedName()}'?" : $"Delete the {steps.Count} selected steps?";
                if (MessageBox.Show(this, question, "Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
            }
            // Select the next sibling (or the parent) afterwards.
            var first = steps[0];
            var siblings = first.Parent.ChildTestSteps;
            object next = siblings.Skip(siblings.IndexOf(first) + 1).FirstOrDefault(s => !steps.Contains(s)) ?? (object)first.Parent;

            foreach (var step in steps)
                step.Parent?.ChildTestSteps.Remove(step);
            MarkChanged();
            RebuildTree(next);
        }

        void DeleteSteps() => DeleteSteps(true);

        void CopyStep()
        {
            if (planGrid.CurrentObject is not ITestStep step) return;
            try
            {
                Clipboard.SetText(new TapSerializer().SerializeToString(step));
            }
            catch (Exception ex)
            {
                GuiAction.Log.Error("Could not copy step: {0}", ex.Message);
                GuiAction.Log.Debug(ex);
            }
        }

        void CutStep()
        {
            if (planGrid.CurrentObject is not ITestStep step) return;
            CopyStep();
            DeleteSteps(false, new List<ITestStep> { step });
        }

        void PasteStep()
        {
            string xml;
            try { xml = Clipboard.GetText(); }
            catch { return; }
            if (string.IsNullOrWhiteSpace(xml)) return;

            ITestStep newStep;
            try
            {
                // Serialize/deserialize to get a new instance, exactly as the TUI does.
                var serializer = new TapSerializer();
                serializer.GetSerializer<TestStepSerializer>().AddKnownStepHeirarchy(plan);
                newStep = serializer.DeserializeFromString(xml, TypeData.FromType(typeof(TestPlan)), path: plan.Path) as ITestStep;
            }
            catch (Exception ex)
            {
                GuiAction.Log.Debug(ex);
                newStep = null;
            }
            if (newStep == null)
            {
                GuiAction.Log.Warning("The clipboard does not contain a test step.");
                return;
            }

            ITestStepParent parent;
            int index;
            if (planGrid.CurrentObject is ITestStep target)
            {
                parent = target.Parent;
                index = parent.ChildTestSteps.IndexOf(target) + 1;
            }
            else
            {
                parent = plan;
                index = plan.ChildTestSteps.Count;
            }
            if (!AllowChild(parent, newStep))
            {
                GuiAction.Log.Warning("'{0}' cannot be placed there.", newStep.GetFormattedName());
                return;
            }
            parent.ChildTestSteps.Insert(index, newStep);
            MarkChanged();
            RebuildTree(newStep);
        }

        void MoveSelected(int offset)
        {
            if (planGrid.CurrentObject is not ITestStep step) return;
            var list = step.Parent.ChildTestSteps;
            var index = list.IndexOf(step);
            var target = index + offset;
            if (target < 0 || target >= list.Count) return;
            list.RemoveAt(index);
            list.Insert(target, step);
            MarkChanged();
            RebuildTree(step);
        }

        void ToggleEnabled()
        {
            var steps = SelectedSteps();
            if (steps.Count == 0) return;
            var enable = !steps[0].Enabled;
            foreach (var step in steps)
                step.Enabled = enable;
            MarkChanged();
            UpdateNodeTexts();
            properties.ReloadValues();
        }

        #endregion

        #region Drag and drop

        static bool IsValidDrop(List<ITestStep> steps, ITestStepParent parent)
        {
            foreach (var step in steps)
            {
                // Not into itself or one of its own children.
                for (var p = parent; p != null; p = (p as ITestStep)?.Parent)
                    if (ReferenceEquals(p, step)) return false;
                if (!AllowChild(parent, step)) return false;
            }
            return true;
        }

        void MoveSteps(List<ITestStep> steps, ITestStepParent parent, int index)
        {
            foreach (var step in steps)
            {
                if (ReferenceEquals(step.Parent, parent) && parent.ChildTestSteps.IndexOf(step) < index)
                    index--;
                step.Parent.ChildTestSteps.Remove(step);
            }
            foreach (var step in steps)
                parent.ChildTestSteps.Insert(index++, step);
            if (parent is ITestStep parentStep)
                ChildItemVisibility.SetVisibility(parentStep, ChildItemVisibility.Visibility.Visible);

            MarkChanged();
            planGrid.Rebuild(plan, steps.Cast<object>(), steps[0]);
        }

        static bool IsPlanFileDrop(IDataObject data) =>
            data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1;

        void OpenDroppedFile(IDataObject data)
        {
            if (running || data.GetData(DataFormats.FileDrop) is not string[] files || files.Length != 1) return;
            if (!ConfirmDiscard()) return;
            LoadPlanFile(files[0]);
        }

        #endregion

        #region File handling

        void LoadStartupPlan()
        {
            if (!string.IsNullOrEmpty(startupPath))
            {
                try
                {
                    // Like the TUI: a path that does not exist yet becomes a new, empty plan file.
                    if (!File.Exists(startupPath))
                        new TestPlan().Save(startupPath);
                    LoadPlanFile(startupPath);
                    return;
                }
                catch (Exception ex)
                {
                    GuiAction.Log.Warning("Unable to load plan {0}: {1}", startupPath, ex.Message);
                }
            }

            var leftover = Recovery.FindLeftover(out var leftoverPath);
            if (leftover != null)
            {
                var name = string.IsNullOrEmpty(leftover.FilePath) ? "an unsaved test plan" : $"'{Path.GetFileName(leftover.FilePath)}'";
                var answer = MessageBox.Show(this,
                    $"The GUI did not close normally last time and has unsaved changes to {name}.\n\nDo you want to recover them?",
                    "Recover Test Plan", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                try { File.Delete(leftoverPath); } catch { }
                if (answer == DialogResult.Yes)
                {
                    try
                    {
                        SetPlan(Recovery.Load(leftover), true);
                        return;
                    }
                    catch (Exception ex)
                    {
                        GuiAction.Log.Error("Could not recover the test plan: {0}", ex.Message);
                    }
                }
            }
            SetPlan(new TestPlan(), false);
        }

        void SetPlan(TestPlan newPlan, bool isUnsaved)
        {
            plan = newPlan;
            unsaved = isUnsaved;
            planGrid.Rebuild(plan, new object[] { plan }, plan);
            UpdateTitle();
            if (isUnsaved) recovery.Save(plan);
            else recovery.Remove();
            NotifyPanels(plan: true);
        }

        #region Editor panels

        /// <summary> View > Panels: one item per editor panel plugin; ticked when open. </summary>
        void BuildPanelsMenu()
        {
            panelsMenu.DropDownItems.Clear();
            var available = PanelCatalog.AvailablePanels();
            if (available.Count == 0)
            {
                panelsMenu.DropDownItems.Add(new ToolStripMenuItem("No panel plugins installed (they need the WPF Controls package)") { Enabled = false });
                return;
            }
            foreach (var type in available)
            {
                var captured = type;
                var item = new ToolStripMenuItem(PanelCatalog.NameOf(type))
                {
                    Checked = PanelIsOpen(type),
                    ToolTipText = type.GetDisplayAttribute()?.Description
                };
                item.Click += (s, e) => TogglePanel(captured);
                panelsMenu.DropDownItems.Add(item);
            }
            panelsMenu.DropDownItems.Add(new ToolStripSeparator());
            panelsMenu.DropDownItems.Add(new ToolStripMenuItem("Tip: right-click or double-click a panel's tab to float it") { Enabled = false });
        }

        // The methods below touch Keysight's WPF panel types; they only run when panel plugins exist.

        [MethodImpl(MethodImplOptions.NoInlining)]
        PanelHost Panels() => panels ??= new PanelHost(this, settingsTabs, SavePanelLayout);

        [MethodImpl(MethodImplOptions.NoInlining)]
        bool PanelIsOpen(ITypeData type) => panels != null && panels.IsOpen(type);

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TogglePanel(ITypeData type)
        {
            try { Panels().Toggle(type); }
            catch (Exception ex) { GuiAction.Log.Error("Panels are not available: {0}", ex.Message); GuiAction.Log.Debug(ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void RestorePanels()
        {
            var layout = GuiSettings.Current.OpenPanels;
            if (string.IsNullOrWhiteSpace(layout) || PanelCatalog.AvailablePanels().Count == 0) return;
            try { Panels().RestoreLayout(layout); }
            catch (Exception ex) { GuiAction.Log.Warning("Could not reopen panels: {0}", ex.Message); GuiAction.Log.Debug(ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void NotifyPanels(bool plan = false, bool selection = false)
        {
            if (panels == null) return;
            try
            {
                if (plan) panels.PlanChanged();
                if (selection) panels.SelectionChanged();
            }
            catch (Exception ex) { GuiAction.Log.Debug(ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        IEnumerable<IResultListener> PanelResultListeners() =>
            panels == null ? Enumerable.Empty<IResultListener>() : panels.ExtraResultListeners();

        [MethodImpl(MethodImplOptions.NoInlining)]
        void ClosePanels()
        {
            panels?.CloseAll();
        }

        void SavePanelLayout()
        {
            if (panels == null) return;
            GuiSettings.Current.OpenPanels = panels.Layout;
            SaveGuiSettings();
        }

        // Called by panels through their context.

        internal void SetPlanFromPanel(TestPlan newPlan)
        {
            if (newPlan == null || ReferenceEquals(newPlan, plan) || running) return;
            if (!ConfirmDiscard()) return;
            SetPlan(newPlan, false);
            AddRecentPlan(newPlan.Path);
        }

        internal TapThread RunFromPanel()
        {
            if (!running) RunPlan(false);
            return runThread;
        }

        internal IEnumerable<object> SelectedForPanels()
        {
            var steps = SelectedSteps();
            return steps.Count > 0 ? steps.Cast<object>().ToList() : new List<object> { plan };
        }

        internal void SelectFromPanel(IEnumerable<object> items)
        {
            var list = items?.Where(i => i is ITestStep || i is TestPlan).ToList();
            if (list == null || list.Count == 0) return;
            planGrid.SelectObjects(list, list[0]);
        }

        #endregion

        bool ConfirmDiscard()
        {
            if (!unsaved) return true;
            switch (MessageBox.Show(this, $"Do you want to save the changes to '{plan.Name}'?", "Unsaved Changes",
                        MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning))
            {
                case DialogResult.Yes: return SavePlan(plan.Path);
                case DialogResult.No: return true;
                default: return false;
            }
        }

        void NewPlan()
        {
            if (running || !ConfirmDiscard()) return;
            SetPlan(new TestPlan(), false);
        }

        void OpenPlan()
        {
            if (running || !ConfirmDiscard()) return;
            using var dialog = new OpenFileDialog
            {
                Title = "Open Test Plan",
                Filter = "Test plans (*.TapPlan)|*.TapPlan|All files (*.*)|*.*",
                InitialDirectory = plan.Directory ?? Environment.CurrentDirectory
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                LoadPlanFile(dialog.FileName);
        }

        void LoadPlanFile(string path)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                SetPlan(TestPlan.Load(path), false);
                AddRecentPlan(path);
                GuiAction.Log.Info("Loaded test plan '{0}'.", path);
            }
            catch (Exception ex)
            {
                GuiAction.Log.Error("Could not load test plan '{0}': {1}", path, ex.Message);
                GuiAction.Log.Debug(ex);
                MessageBox.Show(this, $"Could not load '{path}'.\n\n{ex.Message}", "Open Test Plan", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        bool SavePlan(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                using var dialog = new SaveFileDialog
                {
                    Title = "Save Test Plan",
                    Filter = "Test plans (*.TapPlan)|*.TapPlan|All files (*.*)|*.*",
                    DefaultExt = "TapPlan",
                    FileName = plan.Path == null ? "Untitled.TapPlan" : Path.GetFileName(plan.Path),
                    InitialDirectory = plan.Directory ?? Environment.CurrentDirectory
                };
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return false;
                path = dialog.FileName;
            }
            try
            {
                plan.Save(path);
                unsaved = false;
                UpdateTitle();
                recovery.Remove();
                AddRecentPlan(plan.Path);
                GuiAction.Log.Info("Saved test plan to '{0}'.", plan.Path);
                return true;
            }
            catch (Exception ex)
            {
                GuiAction.Log.Error(ex.Message);
                GuiAction.Log.Debug(ex);
                MessageBox.Show(this, $"Could not save '{path}'.\n\n{ex.Message}", "Save Test Plan", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (running)
            {
                if (MessageBox.Show(this, "A test plan is running. Abort it and exit?", "Exit", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                runThread?.Abort();
            }
            if (!ConfirmDiscard())
            {
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            RememberSettingsPaneWidth();
            try { ClosePanels(); } catch (Exception ex) { GuiAction.Log.Debug(ex); }
            recovery.Remove();
            runTimer.Dispose();
            base.OnFormClosed(e);
        }

        #endregion

        #region Running

        void RunPlan(bool selectedOnly)
        {
            if (paused && !selectedOnly)
            {
                ContinueRun();
                return;
            }
            if (running) return;
            HashSet<ITestStep> stepsOverride = null;
            if (selectedOnly)
            {
                var steps = SelectedSteps();
                if (steps.Count == 0) return;
                stepsOverride = new HashSet<ITestStep>(steps);
            }

            if (GuiSettings.Current.ClearOnRun)
                log.ClearLog();

            running = true;
            runStart = DateTime.Now;
            statusRun.ForeColor = SystemColors.ControlText;
            statusRun.Text = "Running...";
            runTimer.Start();
            UpdateCommandState();

            plan.PrintTestPlanRunSummary = true;
            stepsById = new Dictionary<Guid, ITestStep>();
            foreach (var step in plan.ChildTestSteps.RecursivelyGetAllTestSteps(TestStepSearch.All))
                stepsById[step.Id] = step;
            planGrid.BeginRun();
            var listeners = ResultSettings.Current.Cast<IResultListener>().Append(runTracker).Concat(PanelResultListeners()).Distinct().ToList();
            var runPlan = plan;
            resumeRun.Set();
            runPlan.BreakOffered += OnBreakOffered;
            runThread = TapThread.Start(() =>
            {
                Verdict? verdict = null;
                try
                {
                    verdict = runPlan.Execute(listeners, null, stepsOverride).Verdict;
                }
                catch (Exception ex)
                {
                    GuiAction.Log.Error("Test plan run failed: {0}", ex.Message);
                    GuiAction.Log.Debug(ex);
                }
                finally
                {
                    runPlan.BreakOffered -= OnBreakOffered;
                }
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke(new Action(() => RunFinished(verdict)));
                }
                catch (InvalidOperationException)
                {
                    // Window closed while the plan was running.
                }
            }, "Test Plan Run");
        }

        void RunFinished(Verdict? verdict)
        {
            running = false;
            if (paused) LeavePause();
            runTimer.Stop();
            var elapsed = DateTime.Now - runStart;
            statusRun.Text = verdict == null ? "Run failed" : $"Last run: {verdict}  ({elapsed:hh\\:mm\\:ss})";
            statusRun.ForeColor = verdict switch
            {
                Verdict.Pass => Color.Green,
                Verdict.Fail => Color.Red,
                Verdict.Error => Color.Red,
                Verdict.Aborted => Color.DarkOrange,
                Verdict.Inconclusive => Color.DarkOrange,
                _ => SystemColors.ControlText
            };
            planGrid.EndRun(verdict);
            properties.ReloadValues();
            UpdateCommandState();
        }

        void AbortPlan()
        {
            if (!running) return;
            if (MessageBox.Show(this, "Are you sure you want to abort the test plan?", "Abort Test Plan", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                runThread?.Abort();
        }

        #endregion

        #region Help

        static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                GuiAction.Log.Warning("Could not open {0}: {1}", url, ex.Message);
            }
        }

        void ShowAbout()
        {
            var openTapVersion = typeof(TestPlan).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                                 ?? typeof(TestPlan).Assembly.GetName().Version?.ToString();
            var guiVersion = typeof(MainForm).Assembly.GetName().Version;
            MessageBox.Show(this,
                $"OpenTAP GUI {guiVersion}\nA Windows Forms port of the OpenTAP TUI.\n\nOpenTAP {openTapVersion}",
                "About OpenTAP GUI", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        #endregion
    }
}
