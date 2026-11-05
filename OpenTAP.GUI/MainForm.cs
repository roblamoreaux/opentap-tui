using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
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

        // Test plan tree
        readonly TreeView tree;
        readonly Dictionary<object, TreeNode> nodes = new Dictionary<object, TreeNode>(ReferenceEqualityComparer.Instance);
        readonly HashSet<ITestStep> marked = new HashSet<ITestStep>();
        bool rebuilding, keepMarks;

        readonly AnnotationPropertyGrid properties;
        readonly LogView log;
        readonly SplitContainer mainSplit, topSplit;

        // Status bar
        readonly ToolStripStatusLabel statusPath, statusRun;

        // Running
        TapThread runThread;
        DateTime runStart;
        bool running;
        readonly Timer runTimer = new Timer { Interval = 500 };

        // Menu/toolbar items that are enabled or disabled depending on the state.
        readonly List<(ToolStripItem Item, Func<bool> CanExecute)> commands = new List<(ToolStripItem, Func<bool>)>();

        // Drag and drop
        TreeNode dropHighlight;
        (ITestStepParent Parent, int Index, TreeNode Node, DropPosition Position)? dropTarget;
        enum DropPosition { Before, After, Into }

        public TestPlan Plan => plan;

        public MainForm(string planPath)
        {
            startupPath = planPath;
            Text = "OpenTAP GUI";
            Size = new Size(1300, 850);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            LoadIcon();

            // --- Tree ---
            tree = new TreeView
            {
                Dock = DockStyle.Fill,
                HideSelection = false,
                AllowDrop = true,
                ShowNodeToolTips = true,
                ItemHeight = 20,
            };
            tree.AfterSelect += OnTreeAfterSelect;
            tree.NodeMouseClick += OnTreeNodeMouseClick;
            tree.KeyDown += OnTreeKeyDown;
            tree.AfterExpand += (s, e) => { if (!rebuilding && e.Node.Tag is ITestStep st) ChildItemVisibility.SetVisibility(st, ChildItemVisibility.Visibility.Visible); };
            tree.AfterCollapse += (s, e) => { if (!rebuilding && e.Node.Tag is ITestStep st) ChildItemVisibility.SetVisibility(st, ChildItemVisibility.Visibility.Collapsed); };
            tree.ItemDrag += OnTreeItemDrag;
            tree.DragOver += OnTreeDragOver;
            tree.DragDrop += OnTreeDragDrop;
            tree.DragLeave += (s, e) => ClearDropHighlight();
            tree.ContextMenuStrip = BuildTreeContextMenu();

            // --- Properties ---
            properties = new AnnotationPropertyGrid { Dock = DockStyle.Fill, IsReadOnly = () => plan.Locked };
            properties.PropertiesChanged += () =>
            {
                MarkChanged();
                UpdateNodeTexts();
            };

            // --- Log ---
            log = new LogView { Dock = DockStyle.Fill };

            topSplit = new SplitContainer { Dock = DockStyle.Fill };
            topSplit.Panel1.Controls.Add(tree);
            topSplit.Panel1.Controls.Add(new Label { Text = "Test Plan", Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) });
            topSplit.Panel2.Controls.Add(properties);
            topSplit.Panel2.Controls.Add(new Label { Text = "Settings", Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) });

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

            runTimer.Tick += (s, e) => statusRun.Text = $"Running...  {DateTime.Now - runStart:hh\\:mm\\:ss}";

            // Opening .TapPlan files by dropping them on the window.
            DragEnter += (s, e) => e.Effect = IsPlanFileDrop(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (s, e) => OpenDroppedFile(e.Data);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            mainSplit.SplitterDistance = (int)(mainSplit.Height * 0.68);
            topSplit.SplitterDistance = (int)(topSplit.Width * 0.45);
            LoadStartupPlan();
            UpdateCommandState();
            tree.Focus();
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
            file.DropDownItems.AddRange(new ToolStripItem[]
            {
                Item("&New", NewPlan, Keys.Control | Keys.N, canExecute: () => !running),
                Item("&Open...", OpenPlan, Keys.Control | Keys.O, canExecute: () => !running),
                Item("&Save", () => SavePlan(plan.Path), Keys.Control | Keys.S),
                Item("Save &As...", () => SavePlan(null), Keys.Control | Keys.Shift | Keys.S),
                new ToolStripSeparator(),
                Item("E&xit", Close, Keys.Alt | Keys.F4),
            });

            // Clipboard / delete / move keys are only handled while the tree has focus (see OnTreeKeyDown), so they
            // are display-only here and keep working normally in text boxes.
            var edit = new ToolStripMenuItem("&Edit");
            edit.DropDownItems.AddRange(new ToolStripItem[]
            {
                Item("&Insert Step...", () => InsertStep(false), Keys.Control | Keys.I, canExecute: CanEdit),
                Item("Insert &Child Step...", () => InsertStep(true), Keys.Control | Keys.Shift | Keys.I, canExecute: () => CanEdit() && CanHaveChildren(tree.SelectedNode?.Tag)),
                new ToolStripSeparator(),
                Item("Cu&t", CutStep, shortcutDisplay: "Ctrl+X", canExecute: () => CanEdit() && tree.SelectedNode?.Tag is ITestStep),
                Item("&Copy", CopyStep, shortcutDisplay: "Ctrl+C", canExecute: () => tree.SelectedNode?.Tag is ITestStep),
                Item("&Paste", PasteStep, shortcutDisplay: "Ctrl+V", canExecute: CanEdit),
                Item("&Delete", DeleteSteps, shortcutDisplay: "Del", canExecute: () => CanEdit() && HasSelectedStep()),
                new ToolStripSeparator(),
                Item("Move &Up", () => MoveSelected(-1), shortcutDisplay: "Alt+Up", canExecute: () => CanEdit() && tree.SelectedNode?.Tag is ITestStep),
                Item("Move Do&wn", () => MoveSelected(1), shortcutDisplay: "Alt+Down", canExecute: () => CanEdit() && tree.SelectedNode?.Tag is ITestStep),
                Item("&Enable / Disable", ToggleEnabled, shortcutDisplay: "Space", canExecute: () => CanEdit() && HasSelectedStep()),
                new ToolStripSeparator(),
                Item("Test &Plan Settings", () => SelectObject(plan)),
            });

            var view = new ToolStripMenuItem("&View");
            view.DropDownItems.AddRange(new ToolStripItem[]
            {
                Item("&Expand All", () => tree.ExpandAll()),
                Item("&Collapse All", () => { tree.CollapseAll(); if (tree.Nodes.Count > 0) tree.Nodes[0].Expand(); }),
                new ToolStripSeparator(),
                Item("C&lear Log", log.ClearLog),
            });

            var run = new ToolStripMenuItem("&Run");
            run.DropDownItems.AddRange(new ToolStripItem[]
            {
                Item("&Run Test Plan", () => RunPlan(false), Keys.F5, canExecute: () => !running),
                Item("Run &Selected Steps", () => RunPlan(true), Keys.Control | Keys.F5, canExecute: () => !running && HasSelectedStep()),
                new ToolStripSeparator(),
                Item("&Abort", AbortPlan, Keys.Shift | Keys.F5, canExecute: () => running),
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
                ToolButton("Insert Child", "Insert a new step as a child of the selected step (Ctrl+Shift+I)", () => InsertStep(true), () => CanEdit() && CanHaveChildren(tree.SelectedNode?.Tag)),
                ToolButton("Delete", "Delete the selected steps (Del)", DeleteSteps, () => CanEdit() && HasSelectedStep()),
                ToolButton("▲", "Move up (Alt+Up)", () => MoveSelected(-1), () => CanEdit() && tree.SelectedNode?.Tag is ITestStep),
                ToolButton("▼", "Move down (Alt+Down)", () => MoveSelected(1), () => CanEdit() && tree.SelectedNode?.Tag is ITestStep),
                new ToolStripSeparator(),
                ToolButton("▶ Run", "Run the test plan (F5)", () => RunPlan(false), () => !running),
                ToolButton("■ Abort", "Abort the running test plan (Shift+F5)", AbortPlan, () => running),
            });
            return bar;
        }

        void UpdateCommandState()
        {
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
                Item("Insert Child Step...", () => InsertStep(true), canExecute: () => CanEdit() && CanHaveChildren(tree.SelectedNode?.Tag)),
                new ToolStripSeparator(),
                Item("Cut", CutStep, shortcutDisplay: "Ctrl+X", canExecute: () => CanEdit() && tree.SelectedNode?.Tag is ITestStep),
                Item("Copy", CopyStep, shortcutDisplay: "Ctrl+C", canExecute: () => tree.SelectedNode?.Tag is ITestStep),
                Item("Paste", PasteStep, shortcutDisplay: "Ctrl+V", canExecute: CanEdit),
                Item("Delete", DeleteSteps, shortcutDisplay: "Del", canExecute: () => CanEdit() && HasSelectedStep()),
                new ToolStripSeparator(),
                Item("Enable / Disable", ToggleEnabled, shortcutDisplay: "Space", canExecute: () => CanEdit() && HasSelectedStep()),
                Item("Run Selected Steps", () => RunPlan(true), canExecute: () => !running && HasSelectedStep()),
            });
            menu.Opening += (s, e) => UpdateCommandState();
            return menu;
        }

        #endregion

        #region Test plan tree

        void RebuildTree(object select = null)
        {
            select ??= tree.SelectedNode?.Tag ?? plan;
            var top = tree.TopNode?.Tag;
            rebuilding = true;
            tree.BeginUpdate();
            try
            {
                tree.Nodes.Clear();
                nodes.Clear();
                var root = new TreeNode { Tag = plan, NodeFont = new Font(tree.Font, FontStyle.Bold) };
                nodes[plan] = root;
                AddStepNodes(root.Nodes, plan.ChildTestSteps);
                tree.Nodes.Add(root);
                root.Expand();
                ApplyExpansion(root.Nodes);
                marked.RemoveWhere(s => !nodes.ContainsKey(s));
                UpdateNodeTexts();
                if (top != null && nodes.TryGetValue(top, out var topNode)) tree.TopNode = topNode;
            }
            finally
            {
                tree.EndUpdate();
                rebuilding = false;
            }
            SelectObject(nodes.ContainsKey(select) ? select : plan);
        }

        void AddStepNodes(TreeNodeCollection collection, TestStepList steps)
        {
            foreach (var step in steps)
            {
                var node = new TreeNode { Tag = step };
                nodes[step] = node;
                collection.Add(node);
                AddStepNodes(node.Nodes, step.ChildTestSteps);
            }
        }

        static void ApplyExpansion(TreeNodeCollection collection)
        {
            foreach (TreeNode node in collection)
            {
                if (node.Nodes.Count > 0 && node.Tag is ITestStep step && ChildItemVisibility.GetVisibility(step) == ChildItemVisibility.Visibility.Visible)
                    node.Expand();
                ApplyExpansion(node.Nodes);
            }
        }

        /// <summary> Updates names, enabled look and multi-selection highlight without rebuilding the tree. </summary>
        void UpdateNodeTexts()
        {
            foreach (var pair in nodes)
            {
                var node = pair.Value;
                string text;
                if (pair.Key is TestPlan p)
                {
                    text = p.Name + (unsaved ? " *" : "");
                    node.ToolTipText = p.Path ?? "Not saved yet";
                }
                else
                {
                    var step = (ITestStep)pair.Key;
                    try { text = step.GetFormattedName(); }
                    catch { text = step.Name; }
                    node.ForeColor = step.Enabled ? Color.Empty : SystemColors.GrayText;
                    node.BackColor = marked.Count > 1 && marked.Contains(step) ? Color.LightSteelBlue : Color.Empty;
                    node.ToolTipText = TypeData.GetTypeData(step).GetDisplayAttribute()?.GetFullName();
                }
                if (node.Text != text) node.Text = text;
            }
        }

        void SelectObject(object obj)
        {
            if (obj != null && nodes.TryGetValue(obj, out var node))
            {
                if (tree.SelectedNode == node)
                    ShowSelectionProperties();
                else
                    tree.SelectedNode = node;
                node.EnsureVisible();
            }
        }

        /// <summary> Selected steps in plan order: the Ctrl+click selection, or the single selected step. </summary>
        List<ITestStep> SelectedSteps()
        {
            if (marked.Count > 0)
                return plan.ChildTestSteps.RecursivelyGetAllTestSteps(TestStepSearch.All).Where(marked.Contains).ToList();
            if (tree.SelectedNode?.Tag is ITestStep step)
                return new List<ITestStep> { step };
            return new List<ITestStep>();
        }

        /// <summary> Removes steps whose parent is also in the list (moving/deleting the parent covers them). </summary>
        static List<ITestStep> TopLevelOnly(List<ITestStep> steps)
        {
            var set = new HashSet<ITestStep>(steps);
            return steps.Where(s =>
            {
                for (var p = s.Parent as ITestStep; p != null; p = p.Parent as ITestStep)
                    if (set.Contains(p)) return false;
                return true;
            }).ToList();
        }

        void OnTreeAfterSelect(object sender, TreeViewEventArgs e)
        {
            if (rebuilding) return;
            if (!keepMarks && (ModifierKeys & Keys.Control) == 0)
            {
                marked.Clear();
                if (e.Node?.Tag is ITestStep step) marked.Add(step);
                UpdateNodeTexts();
            }
            ShowSelectionProperties();
            UpdateCommandState();
        }

        void OnTreeNodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                // Keep a multi-selection when right-clicking one of its steps.
                keepMarks = e.Node.Tag is ITestStep s && marked.Contains(s);
                tree.SelectedNode = e.Node;
                keepMarks = false;
                return;
            }
            if (e.Button == MouseButtons.Left && (ModifierKeys & Keys.Control) != 0 && e.Node.Tag is ITestStep step)
            {
                if (!marked.Remove(step))
                    marked.Add(step);
                UpdateNodeTexts();
                ShowSelectionProperties();
                UpdateCommandState();
            }
        }

        void ShowSelectionProperties()
        {
            var steps = SelectedSteps();
            if (steps.Count > 1)
                properties.LoadProperties(steps.ToArray());
            else if (steps.Count == 1)
                properties.LoadProperties(steps[0]);
            else if (tree.SelectedNode?.Tag is TestPlan)
                properties.LoadProperties(plan);
            else
                properties.LoadProperties(null);
        }

        void OnTreeKeyDown(object sender, KeyEventArgs e)
        {
            bool handled = true;
            if (e.KeyCode == Keys.Delete && !e.Modifiers.HasFlag(Keys.Shift)) { if (CanEdit()) DeleteSteps(); }
            else if (e.Control && e.KeyCode == Keys.C) CopyStep();
            else if (e.Control && e.KeyCode == Keys.X) { if (CanEdit()) CutStep(); }
            else if (e.Control && e.KeyCode == Keys.V) { if (CanEdit()) PasteStep(); }
            else if (e.Alt && e.KeyCode == Keys.Up) { if (CanEdit()) MoveSelected(-1); }
            else if (e.Alt && e.KeyCode == Keys.Down) { if (CanEdit()) MoveSelected(1); }
            else if (e.KeyCode == Keys.Space && e.Modifiers == Keys.None) { if (CanEdit()) ToggleEnabled(); }
            else if (e.KeyCode == Keys.Enter) properties.Focus();
            else handled = false;
            if (handled)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
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
            if (nodes.TryGetValue(plan, out var root))
                root.Text = plan.Name + (unsaved ? " *" : "");
        }

        void InsertStep(bool asChild)
        {
            if (!CanEdit()) return;
            var selected = tree.SelectedNode?.Tag;
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

        void DeleteSteps(bool confirm)
        {
            var steps = TopLevelOnly(SelectedSteps());
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
            marked.Clear();
            MarkChanged();
            RebuildTree(next);
        }

        void DeleteSteps() => DeleteSteps(true);

        void CopyStep()
        {
            if (tree.SelectedNode?.Tag is not ITestStep step) return;
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
            if (tree.SelectedNode?.Tag is not ITestStep step) return;
            CopyStep();
            marked.Clear();
            marked.Add(step);
            DeleteSteps(false);
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
            if (tree.SelectedNode?.Tag is ITestStep target)
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
            if (tree.SelectedNode?.Tag is not ITestStep step) return;
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

        class StepDrag
        {
            public List<ITestStep> Steps;
        }

        void OnTreeItemDrag(object sender, ItemDragEventArgs e)
        {
            if (!CanEdit() || e.Item is not TreeNode node || node.Tag is not ITestStep step) return;
            var steps = SelectedSteps();
            if (!steps.Contains(step))
                steps = new List<ITestStep> { step };
            DoDragDrop(new StepDrag { Steps = TopLevelOnly(steps) }, DragDropEffects.Move);
            ClearDropHighlight();
        }

        void OnTreeDragOver(object sender, DragEventArgs e)
        {
            if (IsPlanFileDrop(e.Data))
            {
                e.Effect = DragDropEffects.Copy;
                return;
            }
            var drag = e.Data.GetData(typeof(StepDrag)) as StepDrag;
            if (drag == null)
            {
                e.Effect = DragDropEffects.None;
                return;
            }

            var point = tree.PointToClient(new Point(e.X, e.Y));
            AutoScrollTree(point);
            var target = GetDropTarget(point);
            if (target != null && !IsValidDrop(drag.Steps, target.Value.Parent))
                target = null;
            e.Effect = target == null ? DragDropEffects.None : DragDropEffects.Move;
            ShowDropHighlight(target);
        }

        void OnTreeDragDrop(object sender, DragEventArgs e)
        {
            if (IsPlanFileDrop(e.Data))
            {
                OpenDroppedFile(e.Data);
                return;
            }
            var drag = e.Data.GetData(typeof(StepDrag)) as StepDrag;
            var target = GetDropTarget(tree.PointToClient(new Point(e.X, e.Y)));
            ClearDropHighlight();
            if (drag == null || target == null || !IsValidDrop(drag.Steps, target.Value.Parent)) return;
            MoveSteps(drag.Steps, target.Value.Parent, target.Value.Index);
        }

        (ITestStepParent Parent, int Index, TreeNode Node, DropPosition Position)? GetDropTarget(Point point)
        {
            var node = tree.GetNodeAt(point);
            if (node == null || node.Tag is TestPlan)
                return (plan, plan.ChildTestSteps.Count, node, DropPosition.Into);
            var step = (ITestStep)node.Tag;
            var index = step.Parent.ChildTestSteps.IndexOf(step);
            var relative = (point.Y - node.Bounds.Top) / (double)Math.Max(1, node.Bounds.Height);
            if (relative < 0.25)
                return (step.Parent, index, node, DropPosition.Before);
            if (relative > 0.75 && !(node.IsExpanded && node.Nodes.Count > 0))
                return (step.Parent, index + 1, node, DropPosition.After);
            if (CanHaveChildren(step))
                return (step, step.ChildTestSteps.Count, node, DropPosition.Into);
            return relative < 0.5 ? (step.Parent, index, node, DropPosition.Before) : (step.Parent, index + 1, node, DropPosition.After);
        }

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
            marked.Clear();
            RebuildTree(steps[0]);
            if (steps.Count > 1)
            {
                foreach (var s in steps) marked.Add(s);
                UpdateNodeTexts();
                ShowSelectionProperties();
            }
        }

        void ShowDropHighlight((ITestStepParent Parent, int Index, TreeNode Node, DropPosition Position)? target)
        {
            if (dropTarget?.Node == target?.Node && dropTarget?.Position == target?.Position) return;
            ClearDropHighlight();
            dropTarget = target;
            if (target?.Node == null) return;
            var node = target.Value.Node;
            if (target.Value.Position == DropPosition.Into)
            {
                dropHighlight = node;
                node.BackColor = Color.LightSkyBlue;
            }
            else
            {
                tree.Update();
                using var g = tree.CreateGraphics();
                var y = target.Value.Position == DropPosition.Before ? node.Bounds.Top : node.Bounds.Bottom - 1;
                using var pen = new Pen(SystemColors.Highlight, 2);
                g.DrawLine(pen, node.Bounds.Left, y, tree.ClientSize.Width, y);
            }
        }

        void ClearDropHighlight()
        {
            if (dropHighlight != null)
            {
                dropHighlight.BackColor = dropHighlight.Tag is ITestStep s && marked.Count > 1 && marked.Contains(s) ? Color.LightSteelBlue : Color.Empty;
                dropHighlight = null;
            }
            if (dropTarget != null)
                tree.Invalidate();
            dropTarget = null;
        }

        void AutoScrollTree(Point point)
        {
            var node = tree.GetNodeAt(point);
            if (node == null) return;
            if (point.Y < tree.ItemHeight && node.PrevVisibleNode != null)
                node.PrevVisibleNode.EnsureVisible();
            else if (point.Y > tree.ClientSize.Height - tree.ItemHeight && node.NextVisibleNode != null)
                node.NextVisibleNode.EnsureVisible();
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
            marked.Clear();
            RebuildTree(plan);
            UpdateTitle();
            if (isUnsaved) recovery.Save(plan);
            else recovery.Remove();
        }

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
            recovery.Remove();
            runTimer.Dispose();
            base.OnFormClosed(e);
        }

        #endregion

        #region Running

        void RunPlan(bool selectedOnly)
        {
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
            runThread = TapThread.Start(() =>
            {
                Verdict? verdict = null;
                try
                {
                    verdict = plan.Execute(ResultSettings.Current, null, stepsOverride).Verdict;
                }
                catch (Exception ex)
                {
                    GuiAction.Log.Error("Test plan run failed: {0}", ex.Message);
                    GuiAction.Log.Debug(ex);
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
            properties.ReloadValues();
            UpdateNodeTexts();
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
