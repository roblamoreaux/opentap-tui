using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Keysight.OpenTap.Wpf;

namespace OpenTap.Gui
{
    /// <summary>
    /// Finds editor panel plugins (Operator Panel, Live Results, AI Assist, ...). These implement Keysight's
    /// ITapDockPanel from the "WPF Controls" package. Only OpenTAP's plugin metadata is used here, so nothing
    /// Keysight-specific is loaded when that package is not installed.
    /// </summary>
    static class PanelCatalog
    {
        const string PanelInterface = "Keysight.OpenTap.Wpf.ITapDockPanel";

        public static List<ITypeData> AvailablePanels()
        {
            var baseType = TypeData.GetTypeData(PanelInterface);
            if (baseType == null) return new List<ITypeData>();
            return TypeData.GetDerivedTypes(baseType)
                .Where(t => t.CanCreateInstance && (t.GetAttribute<BrowsableAttribute>()?.Browsable ?? true))
                .Where(t => !IsEditorOwnPanel(t))
                .OrderBy(t => t.GetDisplayAttribute()?.Name ?? t.Name)
                .ToList();
        }

        /// <summary>
        /// The Keysight Editor program (Editor.dll) defines its own window parts as panels too: Test Plan, Test Steps,
        /// Test Step Settings, Test Plan Settings, Log and Welcome. They need the Editor's main window and fail
        /// anywhere else; the GUI has its own versions of them, so they are left out.
        /// </summary>
        static bool IsEditorOwnPanel(ITypeData type)
        {
            var assembly = (type as TypeData)?.Assembly?.Name;
            return string.Equals(assembly, "Editor", StringComparison.OrdinalIgnoreCase);
        }

        public static string NameOf(ITypeData type) => type.GetDisplayAttribute()?.Name?.Trim() ?? type.Name;
    }

    /// <summary> What panels see of the GUI: the test plan, settings, result listeners, selection and Run. </summary>
    class GuiDockContext : ITapDockContext2, ITapDockContextMap
    {
        readonly MainForm form;
        readonly Dictionary<Type, object> services = new Dictionary<Type, object>();

        public GuiDockContext(MainForm form)
        {
            this.form = form;
            Settings = new TapSettings();
        }

        public TestPlan Plan
        {
            get => form.Plan;
            set => form.SetPlanFromPanel(value);
        }

        public TapSettings Settings { get; }

        /// <summary> Extra result listeners added by panels (e.g. Live Results); included in runs started from the GUI. </summary>
        public List<IResultListener> ResultListeners { get; } = new List<IResultListener>();

        public TapThread Run() => form.RunFromPanel();

        public IEnumerable<object> SelectedItems
        {
            get => form.SelectedForPanels();
            set => form.SelectFromPanel(value);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        internal void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

        public void Set<T>(T value) => services[typeof(T)] = value;

        public bool TryGet<T>(out T value)
        {
            if (services.TryGetValue(typeof(T), out var v) && v is T t)
            {
                value = t;
                return true;
            }
            value = default;
            return false;
        }

        public bool Remove<T>() => services.Remove(typeof(T));
    }

    /// <summary>
    /// Opens editor panels next to Settings (as tabs) or in their own floating windows, and remembers which panels
    /// were open and where.
    /// </summary>
    class PanelHost
    {
        class OpenPanel
        {
            public ITypeData Type;
            public string Name;
            public ITapDockPanel Panel;
            public ElementHost Host;
            public TabPage Tab;
            public Form FloatWindow;
        }

        readonly MainForm form;
        readonly TabControl tabs;
        readonly Action layoutChanged;
        readonly List<OpenPanel> open = new List<OpenPanel>();
        GuiDockContext context;
        bool closingAll;

        public PanelHost(MainForm form, TabControl tabs, Action layoutChanged)
        {
            this.form = form;
            this.tabs = tabs;
            this.layoutChanged = layoutChanged;

            var menu = new ContextMenuStrip();
            menu.Opening += (s, e) => BuildTabMenu(menu, e);
            tabs.ContextMenuStrip = menu;
            tabs.MouseDoubleClick += (s, e) =>
            {
                var panel = PanelAt(e.Location);
                if (panel != null) Float(panel);
            };
            tabs.SelectedIndexChanged += (s, e) =>
            {
                var panel = open.FirstOrDefault(p => p.Tab != null && p.Tab == tabs.SelectedTab);
                if (panel?.Panel is ITapDockPanelFocusNotify notify)
                {
                    try { notify.OnFocused(); } catch (Exception ex) { GuiAction.Log.Debug(ex); }
                }
            };
        }

        GuiDockContext Context
        {
            get
            {
                if (context != null) return context;
                // WPF content hosted in WinForms: panels may expect a WPF Application (resources, dispatcher).
                if (System.Windows.Application.Current == null)
                    new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                LoadEditorResources();
                // A panel that fails while WPF lays it out (after CreateElement returned) must not take the GUI down.
                System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException += OnPanelError;
                context = new GuiDockContext(form);
                // Let plugins put shared services into the context, as the Editor does.
                var initializerBase = TypeData.FromType(typeof(ITapDockContextInitializer));
                foreach (var type in TypeData.GetDerivedTypes(initializerBase).Where(t => t.CanCreateInstance))
                {
                    try { ((ITapDockContextInitializer)type.CreateInstance()).Initialize(context); }
                    catch (Exception ex) { GuiAction.Log.Debug("Panel context initializer '{0}' failed: {1}", type.Name, ex.Message); GuiAction.Log.Debug(ex); }
                }
                return context;
            }
        }

        /// <summary>
        /// Panels use the icons, colors and styles the Keysight Editor loads at startup (e.g. "Tap.GearVisual"),
        /// so load the same resource dictionaries into the WPF application. Each one is optional.
        /// </summary>
        static void LoadEditorResources()
        {
            var app = System.Windows.Application.Current;
            try
            {
                // Keysight's common look and feel, which the Editor's theme builds on (e.g. "TreeArrow").
                app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Keysight.Ccl.Wsl;component/ui/resources/skins/caranuskin.xaml")
                });
            }
            catch (Exception ex)
            {
                GuiAction.Log.Debug("Keysight base skin not loaded: {0}", ex.Message);
            }
            try
            {
                // Colors and brushes of the Editor's light theme (a class, so it cannot be loaded by file name).
                var skinType = typeof(ITapDockPanel).Assembly.GetType("Keysight.OpenTap.Wpf.Themes.LightThemeSkin");
                if (skinType != null && Activator.CreateInstance(skinType) is System.Windows.ResourceDictionary skin)
                    app.Resources.MergedDictionaries.Add(skin);
            }
            catch (Exception ex)
            {
                GuiAction.Log.Debug("Panel theme not loaded: {0}", ex.Message);
            }
            foreach (var uri in new[]
                     {
                         "pack://application:,,,/Keysight.OpenTap.Wpf;component/themes/generic.xaml",
                         "pack://application:,,,/Keysight.OpenTap.Wpf;component/icons.xaml",
                         "pack://application:,,,/Keysight.OpenTap.Wpf;component/defaultstyle.xaml",
                         "pack://application:,,,/Editor;component/simpledocktheme/simpledocktheme.xaml",
                         "pack://application:,,,/Editor;component/style.xaml",
                     })
            {
                try
                {
                    app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri(uri) });
                }
                catch (Exception ex)
                {
                    GuiAction.Log.Debug("Panel resources '{0}' not loaded: {1}", uri, ex.Message);
                }
            }
            // The Editor's main window defines a base button style that panels build on; provide a plain one.
            var buttonBase = typeof(System.Windows.Controls.Primitives.ButtonBase);
            if (!app.Resources.Contains(buttonBase))
                app.Resources[buttonBase] = new System.Windows.Style(buttonBase);
        }

        /// <summary> The panel being created/laid out right now; errors during that time are blamed on it. </summary>
        OpenPanel loading;
        DateTime lastErrorLogged;

        void OnPanelError(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            var culprit = loading;
            var message = e.Exception.InnerException?.Message ?? e.Exception.Message;
            if (culprit != null)
            {
                loading = null;
                GuiAction.Log.Error("Panel '{0}' failed and was closed: {1}", culprit.Name, message);
                GuiAction.Log.Debug(e.Exception);
                form.BeginInvoke(new Action(() =>
                {
                    if (open.Contains(culprit)) Close(culprit);
                    MessageBox.Show(form,
                        $"The panel '{culprit.Name}' could not be shown and was closed.\n\n{message}\n\n" +
                        "Some panels are written for the Keysight Editor and only work inside it.",
                        "Panels", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }));
            }
            else if (DateTime.Now - lastErrorLogged > TimeSpan.FromSeconds(5))
            {
                // An open panel failed later on; keep the GUI running and say so in the log.
                lastErrorLogged = DateTime.Now;
                GuiAction.Log.Error("A panel reported an error: {0}", message);
                GuiAction.Log.Debug(e.Exception);
            }
        }

        public bool IsOpen(ITypeData type) => open.Any(p => p.Type.Name == type.Name);

        public IEnumerable<IResultListener> ExtraResultListeners() =>
            context == null ? Enumerable.Empty<IResultListener>() : context.ResultListeners.ToList();

        public void PlanChanged() => context?.Raise(nameof(ITapDockContext.Plan));

        public void SelectionChanged() => context?.Raise(nameof(ITapDockContext2.SelectedItems));

        public void Toggle(ITypeData type)
        {
            var existing = open.FirstOrDefault(p => p.Type.Name == type.Name);
            if (existing != null) Close(existing);
            else Open(type, false);
        }

        /// <summary> Creates the panel and shows it as a tab next to Settings, or floating. </summary>
        public bool Open(ITypeData type, bool floating)
        {
            if (IsOpen(type)) return true;
            var name = PanelCatalog.NameOf(type);
            try
            {
                var panel = (ITapDockPanel)type.CreateInstance();
                if (panel is ITapDockPanelPreLoad preLoad) preLoad.OnPreLoad();
                var element = panel.CreateElement(Context);
                if (element == null) throw new InvalidOperationException("The panel did not create any content.");
                var item = new OpenPanel
                {
                    Type = type,
                    Name = name,
                    Panel = panel,
                    Host = new ElementHost { Dock = DockStyle.Fill, Child = element }
                };
                open.Add(item);
                loading = item;
                if (floating) ShowFloating(item);
                else ShowDocked(item);
                // Once WPF has finished laying the panel out, later errors are no longer blamed on opening it.
                element.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(() => { if (loading == item) loading = null; }));
                GuiAction.Log.Info("Opened panel '{0}'.", name);
                layoutChanged();
                return true;
            }
            catch (Exception ex)
            {
                GuiAction.Log.Error("Could not open panel '{0}': {1}", name, ex.Message);
                GuiAction.Log.Debug(ex);
                MessageBox.Show(form, $"Could not open the panel '{name}'.\n\n{ex.Message}", "Panels", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        void ShowDocked(OpenPanel item)
        {
            item.Tab = new TabPage(item.Name) { ToolTipText = "Right-click or double-click the tab to float it" };
            item.Tab.Controls.Add(item.Host);
            tabs.TabPages.Add(item.Tab);
            tabs.SelectedTab = item.Tab;
        }

        void ShowFloating(OpenPanel item, Size? contentSize = null)
        {
            // Size: what the panel had while docked, else what it asks for, else a sensible default.
            static int Pick(double? desired, int fallback) =>
                desired is double d && !double.IsNaN(d) && d >= 200 ? (int)Math.Round(d) : fallback;
            var w = contentSize?.Width >= 200 ? contentSize.Value.Width : Pick(item.Panel.DesiredWidth, 520);
            var h = contentSize?.Height >= 150 ? contentSize.Value.Height : Pick(item.Panel.DesiredHeight, 560);
            var window = new Form
            {
                Text = item.Name,
                ClientSize = new Size(Math.Min(1600, w), Math.Min(1200, h + 25)),
                MinimumSize = new Size(300, 200),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(Math.Max(0, form.Right - 40 - Math.Min(form.Width / 2, w)), form.Top + 80),
                ShowInTaskbar = false,
                Icon = form.Icon
            };
            var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            var dock = new ToolStripButton("Dock next to Settings") { ToolTipText = "Put this panel back into the main window" };
            dock.Click += (s, e) => Dock(item);
            bar.Items.Add(dock);
            window.Controls.Add(item.Host);
            window.Controls.Add(bar);
            window.FormClosed += (s, e) =>
            {
                // Closing the floating window closes the panel (unless it is being docked).
                if (item.FloatWindow == window && !closingAll) Close(item);
            };
            item.FloatWindow = window;
            window.Show(form);
        }

        void Float(OpenPanel item)
        {
            if (item.FloatWindow != null) return;
            var dockedSize = item.Host.Size;
            if (item.Tab != null)
            {
                item.Tab.Controls.Remove(item.Host);
                tabs.TabPages.Remove(item.Tab);
                item.Tab.Dispose();
                item.Tab = null;
            }
            ShowFloating(item, dockedSize);
            layoutChanged();
        }

        void Dock(OpenPanel item)
        {
            var window = item.FloatWindow;
            if (window == null) return;
            item.FloatWindow = null;
            window.Controls.Remove(item.Host);
            window.Close();
            ShowDocked(item);
            layoutChanged();
        }

        void Close(OpenPanel item)
        {
            open.Remove(item);
            if (item.Tab != null)
            {
                tabs.TabPages.Remove(item.Tab);
                item.Tab.Dispose();
            }
            if (item.FloatWindow != null)
            {
                var window = item.FloatWindow;
                item.FloatWindow = null;
                window.Close();
            }
            try { item.Host.Dispose(); } catch (Exception ex) { GuiAction.Log.Debug(ex); }
            (item.Panel as IDisposable)?.Dispose();
            if (!closingAll) layoutChanged();
        }

        /// <summary> Closes every panel when the main window closes, without forgetting the layout. </summary>
        public void CloseAll()
        {
            closingAll = true;
            foreach (var item in open.ToList())
                Close(item);
            closingAll = false;
        }

        OpenPanel PanelAt(Point location)
        {
            for (int i = 0; i < tabs.TabCount; i++)
                if (tabs.GetTabRect(i).Contains(location))
                    return open.FirstOrDefault(p => p.Tab == tabs.TabPages[i]);
            return null;
        }

        void BuildTabMenu(ContextMenuStrip menu, CancelEventArgs e)
        {
            menu.Items.Clear();
            var panel = PanelAt(tabs.PointToClient(Cursor.Position)) ?? open.FirstOrDefault(p => p.Tab != null && p.Tab == tabs.SelectedTab);
            if (panel == null)
            {
                e.Cancel = true;
                return;
            }
            menu.Items.Add($"Float '{panel.Name}'", null, (s, a) => Float(panel));
            menu.Items.Add($"Close '{panel.Name}'", null, (s, a) => Close(panel));
        }

        /// <summary> Open panels and where they are, e.g. "Type.Name:docked|Other.Type:float". </summary>
        public string Layout => string.Join("|", open.Select(p => $"{p.Type.Name}:{(p.FloatWindow != null ? "float" : "docked")}"));

        public void RestoreLayout(string layout)
        {
            if (string.IsNullOrWhiteSpace(layout)) return;
            var available = PanelCatalog.AvailablePanels();
            foreach (var entry in layout.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split(':');
                var type = available.FirstOrDefault(t => t.Name == parts[0]);
                if (type != null) Open(type, parts.Length > 1 && parts[1] == "float");
            }
            if (tabs.TabCount > 0) tabs.SelectedIndex = 0;
        }
    }
}
