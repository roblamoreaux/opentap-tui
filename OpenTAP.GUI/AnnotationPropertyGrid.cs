using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Design;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Windows.Forms;
using System.Windows.Forms.Design;
using System.Xml.Serialization;

namespace OpenTap.Gui
{
    /// <summary>
    /// A standard Windows PropertyGrid that edits any object through OpenTAP's annotation system
    /// (the same system the TUI's PropertiesView and edit providers use), so available values, enabled/visible-if,
    /// parameterization, validation errors etc. all behave as in other OpenTAP editors.
    /// </summary>
    public class AnnotationPropertyGrid : PropertyGrid
    {
        AnnotationCollection root;

        /// <summary> The object (or array of objects) being edited. </summary>
        public object Source { get; private set; }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<bool> IsReadOnly { get; set; } = () => false;

        /// <summary> Extra filter for members to hide (used by user input dialogs to show messages as labels). </summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<AnnotationCollection, bool> HideMember { get; set; } = _ => false;

        /// <summary> Raised after a value has been written to the object. </summary>
        public event Action PropertiesChanged;

        bool refreshQueued;

        public AnnotationPropertyGrid()
        {
            PropertySort = PropertySort.Categorized;
            ToolbarVisible = true;
            HelpVisible = true;
            var menu = new ContextMenuStrip();
            menu.Opening += BuildContextMenu;
            ContextMenuStrip = menu;
        }

        public void LoadProperties(object obj)
        {
            Source = obj;
            root = obj == null ? null : AnnotationCollection.Annotate(obj);
            SelectedObject = root == null ? null : new AnnotatedObject(this, root);
        }

        /// <summary> Re-reads all values from the object (e.g. after a test plan run changed something). </summary>
        public void ReloadValues()
        {
            if (root == null) return;
            root.Read();
            Refresh();
        }

        internal IEnumerable<AnnotationCollection> VisibleMembers(AnnotationCollection parent, bool isRoot)
        {
            var members = parent?.Get<IMembersAnnotation>()?.Members;
            if (members == null) return Enumerable.Empty<AnnotationCollection>();
            return members.Where(x =>
            {
                var member = x.Get<IMemberAnnotation>()?.Member;
                if (member == null) return false;
                bool isResourceName = member.DeclaringType.DescendsTo(resourceType) && member.Name == nameof(Resource.Name);
                if (!isResourceName && (x.Get<IAccessAnnotation>()?.IsVisible ?? false) == false)
                    return false;
                // Submit members with choices are shown as dialog buttons instead.
                if (member.HasAttribute<SubmitAttribute>() && x.Get<IAvailableValuesAnnotationProxy>() != null)
                    return false;
                if (isRoot && HideMember(x))
                    return false;
                return FilterMember(member);
            }).ToArray();
        }

        static readonly ITypeData resourceType = TypeData.FromType(typeof(IResource));

        /// <summary> Same member filter as the TUI's PropertiesView.FilterMember. </summary>
        public static bool FilterMember(IMemberData member)
        {
            if (member.DeclaringType.DescendsTo(resourceType) && member.Name == nameof(Resource.Name))
                return true;
            if (member.GetAttribute<BrowsableAttribute>() is BrowsableAttribute attr)
                return attr.Browsable;
            if (member.HasAttribute<OutputAttribute>())
                return true;
            return member.Attributes.Any(a => a is XmlIgnoreAttribute) == false && member.Writable;
        }

        /// <summary> Writes the annotation tree back to the object and refreshes the grid. </summary>
        internal void Commit()
        {
            if (root == null) return;
            try
            {
                root.Write();
            }
            finally
            {
                root.Read();
            }
            // Refresh after the PropertyGrid has finished its own edit handling; visibility and
            // enabled state of other members may have changed.
            if (!refreshQueued && IsHandleCreated)
            {
                refreshQueued = true;
                BeginInvoke(new Action(() =>
                {
                    refreshQueued = false;
                    Refresh();
                    PropertiesChanged?.Invoke();
                }));
            }
            else if (!IsHandleCreated)
            {
                PropertiesChanged?.Invoke();
            }
        }

        /// <summary> Right-click menu: the member's own actions (Parameterize, Assign Output, ...). </summary>
        void BuildContextMenu(object sender, CancelEventArgs e)
        {
            var menu = (ContextMenuStrip)sender;
            menu.Items.Clear();
            var descriptor = SelectedGridItem?.PropertyDescriptor as AnnotationPropertyDescriptor;
            var items = descriptor?.Annotation.Get<MenuAnnotation>()?.MenuItems?.ToArray();
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item.Get<IAccessAnnotation>()?.IsVisible == false) continue;
                    var name = item.Get<DisplayAttribute>()?.Name ?? "?";
                    var menuItem = new ToolStripMenuItem(name)
                    {
                        Enabled = item.Get<IEnabledAnnotation>()?.IsEnabled != false && !IsReadOnly(),
                        ToolTipText = item.Get<DisplayAttribute>()?.Description
                    };
                    var captured = item;
                    menuItem.Click += (s, a) =>
                    {
                        try
                        {
                            captured.Get<IMethodAnnotation>()?.Invoke();
                        }
                        catch (Exception ex)
                        {
                            GuiAction.Log.Error("Error executing action: {0}", ex.Message);
                            GuiAction.Log.Debug(ex);
                        }
                        // Menu actions such as Parameterize change the object structure, so annotate it again.
                        LoadProperties(Source);
                        PropertiesChanged?.Invoke();
                    };
                    menu.Items.Add(menuItem);
                }
            }
            if (menu.Items.Count == 0)
                e.Cancel = true;
        }

        #region Helpers shared by descriptors and the collection editor

        internal static string ItemText(AnnotationCollection a)
        {
            if (a == null) return "";
            var text = a.Get<IStringReadOnlyValueAnnotation>()?.Value;
            if (text != null) return text;
            var value = a.Get<IObjectValueAnnotation>()?.Value;
            if (value == null) return "";
            text = value.ToString();
            // Objects without their own ToString (e.g. plugin instances) show their display name instead of the class name.
            if (text == value.GetType().FullName)
                text = TypeData.GetTypeData(value)?.GetDisplayAttribute()?.Name ?? text;
            return text;
        }

        internal static string ValueText(AnnotationCollection a)
        {
            try
            {
                var multi = a.Get<IMultiSelectAnnotationProxy>();
                if (multi != null)
                    return string.Join(", ", multi.SelectedValues.Select(ItemText));
                var avail = a.Get<IAvailableValuesAnnotationProxy>();
                if (avail != null)
                    return ItemText(avail.SelectedValue);
                var value = a.Get<IStringValueAnnotation>()?.Value
                            ?? a.Get<IStringReadOnlyValueAnnotation>()?.Value
                            ?? (a.Get<IObjectValueAnnotation>()?.Value is MacroString ms ? ms.Text : null)
                            ?? a.Get<IObjectValueAnnotation>()?.Value?.ToString()
                            ?? "";
                return value.Replace("\r", "").Replace("\n", " ");
            }
            catch
            {
                return "";
            }
        }

        internal static string Errors(AnnotationCollection a)
        {
            var errors = a.GetAll<IErrorAnnotation>().SelectMany(x => x.Errors).Where(x => string.IsNullOrWhiteSpace(x) == false);
            return string.Join(Environment.NewLine, errors);
        }

        #endregion
    }

    /// <summary> Wraps an annotation so the PropertyGrid sees its members as properties. </summary>
    class AnnotatedObject : CustomTypeDescriptor
    {
        public readonly AnnotationPropertyGrid Grid;
        public readonly AnnotationCollection Annotation;

        public AnnotatedObject(AnnotationPropertyGrid grid, AnnotationCollection annotation)
        {
            Grid = grid;
            Annotation = annotation;
        }

        public override PropertyDescriptorCollection GetProperties() => GetProperties(null);

        public override PropertyDescriptorCollection GetProperties(Attribute[] attributes)
        {
            var names = new HashSet<string>();
            var list = new List<PropertyDescriptor>();
            foreach (var member in Grid.VisibleMembers(Annotation, true))
            {
                var name = member.Get<IMemberAnnotation>()?.Member.Name ?? "member";
                var unique = name;
                for (int i = 2; !names.Add(unique); i++) unique = name + i;
                list.Add(new AnnotationPropertyDescriptor(Grid, member, unique, null));
            }
            return new PropertyDescriptorCollection(list.ToArray());
        }

        public override object GetPropertyOwner(PropertyDescriptor pd) => this;
        public override string GetClassName() => TypeData.GetTypeData(Annotation.Get<IObjectValueAnnotation>()?.Value)?.GetDisplayAttribute()?.Name;
        public override string GetComponentName() => null;
        public override string ToString() => AnnotationPropertyGrid.ValueText(Annotation);
    }

    enum MemberKind { Text, Bool, Choice, MultiChoice, Enabled, Secure, Collection, Expandable, Action, ReadOnlyText }

    /// <summary> One annotated member as a PropertyGrid property. </summary>
    class AnnotationPropertyDescriptor : PropertyDescriptor
    {
        public readonly AnnotationCollection Annotation;
        readonly AnnotationPropertyGrid grid;
        readonly string displayName;
        public readonly MemberKind Kind;

        public AnnotationPropertyDescriptor(AnnotationPropertyGrid grid, AnnotationCollection annotation, string name, string displayNameOverride)
            : base(name, null)
        {
            this.grid = grid;
            Annotation = annotation;
            displayName = displayNameOverride;
            Kind = GetKind(annotation);
        }

        static MemberKind GetKind(AnnotationCollection a)
        {
            var type = a.Get<IReflectionAnnotation>()?.ReflectionInfo;
            if (a.Get<IMethodAnnotation>() != null || a.Get<IObjectValueAnnotation>()?.Value is Action)
                return MemberKind.Action;
            if (a.Get<IMultiSelectAnnotationProxy>() != null && a.Get<IAvailableValuesAnnotationProxy>() != null)
                return MemberKind.MultiChoice;
            if (a.Get<IAvailableValuesAnnotationProxy>() != null)
                return MemberKind.Choice;
            if (a.Get<IEnabledValueAnnotation>() != null)
                return MemberKind.Enabled;
            if (type != null && type.DescendsTo(TypeData.FromType(typeof(bool))) && a.Get<IObjectValueAnnotation>() != null)
                return MemberKind.Bool;
            if (type != null && type.DescendsTo(TypeData.FromType(typeof(SecureString))))
                return MemberKind.Secure;
            if (a.Get<IStringValueAnnotation>() != null)
                return MemberKind.Text;
            if (a.Get<IObjectValueAnnotation>()?.Value is MacroString)
                return MemberKind.Text;
            if (a.Get<ICollectionAnnotation>() != null)
                return a.Get<ReadOnlyMemberAnnotation>() != null ? MemberKind.ReadOnlyText : MemberKind.Collection;
            if (a.Get<IMembersAnnotation>() != null && a.Get<IObjectValueAnnotation>()?.Value != null)
                return MemberKind.Expandable;
            return MemberKind.ReadOnlyText;
        }

        IMemberData Member => Annotation.Get<IMemberAnnotation>()?.Member;

        public override string DisplayName
        {
            get
            {
                if (displayName != null) return displayName;
                var sb = new StringBuilder();
                var icons = new HashSet<string>(Annotation.GetAll<IIconAnnotation>().Select(i => i.IconName));
                if (icons.Contains(IconNames.OutputAssigned)) sb.Append("● ");
                else if (icons.Contains(IconNames.Output)) sb.Append("○ ");
                if (icons.Contains(IconNames.Input)) sb.Append("→ ");
                if (icons.Contains(IconNames.Parameterized)) sb.Append("♦ ");
                if (Member is IParameterMemberData) sb.Append("◊ ");
                sb.Append(Annotation.Get<DisplayAttribute>()?.Name ?? Name);
                if (AnnotationPropertyGrid.Errors(Annotation).Length > 0)
                    sb.Append("  ⚠");
                return sb.ToString();
            }
        }

        public override string Category
        {
            get
            {
                var group = Annotation.Get<DisplayAttribute>()?.Group;
                return group == null || group.Length == 0 ? " General" : string.Join(" \\ ", group);
            }
        }

        public override string Description
        {
            get
            {
                var description = Annotation.Get<DisplayAttribute>()?.Description ?? "";
                var errors = AnnotationPropertyGrid.Errors(Annotation);
                return errors.Length > 0 ? $"⚠ {errors}{Environment.NewLine}{description}" : description;
            }
        }

        public override bool IsReadOnly
        {
            get
            {
                if (Kind == MemberKind.ReadOnlyText) return true;
                if (Kind == MemberKind.Expandable) return true; // edited through its children
                if (grid.IsReadOnly()) return true;
                if (Annotation.Get<IEnabledAnnotation>()?.IsEnabled == false) return true;
                if (Kind == MemberKind.Action) return false;
                return Annotation.Get<IAccessAnnotation>()?.IsReadOnly ?? false;
            }
        }

        public override Type ComponentType => typeof(AnnotatedObject);

        public override Type PropertyType => Kind switch
        {
            MemberKind.Bool => typeof(bool),
            MemberKind.Enabled => typeof(AnnotatedObject),
            MemberKind.Expandable => typeof(AnnotatedObject),
            _ => typeof(string)
        };

        public override AttributeCollection Attributes
        {
            get
            {
                var attrs = new List<Attribute>();
                if (Kind == MemberKind.Secure)
                    attrs.Add(new PasswordPropertyTextAttribute(true));
                return new AttributeCollection(attrs.ToArray());
            }
        }

        public override TypeConverter Converter
        {
            get
            {
                switch (Kind)
                {
                    case MemberKind.Choice: return new ChoiceConverter(Annotation, true);
                    case MemberKind.Bool: return new BooleanConverter();
                    case MemberKind.Enabled:
                    case MemberKind.Expandable:
                        return new NestedConverter(grid, Annotation, Kind);
                    case MemberKind.Text when Annotation.Get<ISuggestedValuesAnnotationProxy>() != null:
                        return new ChoiceConverter(Annotation, false);
                    default:
                        return new StringConverter();
                }
            }
        }

        public override object GetEditor(Type editorBaseType)
        {
            if (editorBaseType != typeof(UITypeEditor))
                return base.GetEditor(editorBaseType);
            switch (Kind)
            {
                case MemberKind.Action: return new ActionEditor();
                case MemberKind.MultiChoice: return new MultiSelectEditor();
                case MemberKind.Collection: return new CollectionEditor();
                case MemberKind.Text:
                    var member = Member;
                    if (member?.GetAttribute<FilePathAttribute>() is FilePathAttribute fp) return new PathEditor(fp, false);
                    if (member?.HasAttribute<DirectoryPathAttribute>() == true) return new PathEditor(null, true);
                    if ((member?.GetAttribute<LayoutAttribute>()?.RowHeight ?? 0) > 1) return new MultilineEditor();
                    break;
            }
            return null;
        }

        public override bool CanResetValue(object component) => false;
        public override void ResetValue(object component) { }
        public override bool ShouldSerializeValue(object component) => false;

        public override object GetValue(object component)
        {
            switch (Kind)
            {
                case MemberKind.Bool:
                    return Annotation.Get<IObjectValueAnnotation>()?.Value as bool? ?? false;
                case MemberKind.Enabled:
                case MemberKind.Expandable:
                    return new AnnotatedObject(grid, Annotation);
                case MemberKind.Secure:
                    return SecureToString(Annotation.Get<IObjectValueAnnotation>()?.Value as SecureString);
                case MemberKind.Action:
                    return "Click ... to run";
                case MemberKind.Collection:
                    var count = Annotation.Get<ICollectionAnnotation>()?.AnnotatedElements?.Count() ?? 0;
                    return $"({count} item{(count == 1 ? "" : "s")})";
                default:
                    return AnnotationPropertyGrid.ValueText(Annotation);
            }
        }

        public override void SetValue(object component, object value)
        {
            switch (Kind)
            {
                case MemberKind.Bool:
                    Annotation.Get<IObjectValueAnnotation>().Value = value is bool b && b;
                    break;
                case MemberKind.Choice:
                {
                    var avail = Annotation.Get<IAvailableValuesAnnotationProxy>();
                    var text = value as string ?? "";
                    var match = avail.AvailableValues.FirstOrDefault(v => AnnotationPropertyGrid.ItemText(v) == text);
                    if (match == null)
                        throw new ArgumentException($"'{text}' is not one of the available values.");
                    avail.SelectedValue = match;
                    break;
                }
                case MemberKind.Secure:
                {
                    var sec = new SecureString();
                    foreach (var c in value as string ?? "") sec.AppendChar(c);
                    Annotation.Get<IObjectValueAnnotation>().Value = sec;
                    break;
                }
                case MemberKind.Text:
                {
                    var text = (value as string ?? "").Replace("\r", "");
                    var str = Annotation.Get<IStringValueAnnotation>();
                    if (str != null)
                        str.Value = text;
                    else if (Annotation.Get<IObjectValueAnnotation>()?.Value is MacroString ms)
                        ms.Text = text;
                    break;
                }
                default:
                    // Editors for the other kinds modify the annotation directly.
                    break;
            }
            grid.Commit();
        }

        internal void CommitFromEditor() => grid.Commit();
        internal AnnotationPropertyGrid Grid => grid;

        static string SecureToString(SecureString s)
        {
            if (s == null) return "";
            IntPtr ptr = IntPtr.Zero;
            try
            {
                ptr = Marshal.SecureStringToGlobalAllocUnicode(s);
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                if (ptr != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(ptr);
            }
        }
    }

    /// <summary> Drop-down list of available values (exclusive) or suggested values (free text allowed). </summary>
    class ChoiceConverter : StringConverter
    {
        readonly AnnotationCollection annotation;
        readonly bool exclusive;

        public ChoiceConverter(AnnotationCollection annotation, bool exclusive)
        {
            this.annotation = annotation;
            this.exclusive = exclusive;
        }

        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) => exclusive;

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            IEnumerable<AnnotationCollection> values = exclusive
                ? annotation.Get<IAvailableValuesAnnotationProxy>()?.AvailableValues
                : annotation.Get<ISuggestedValuesAnnotationProxy>()?.SuggestedValues;
            return new StandardValuesCollection((values ?? Enumerable.Empty<AnnotationCollection>())
                .Select(AnnotationPropertyGrid.ItemText).ToArray());
        }
    }

    /// <summary> Expandable rows: embedded objects and Enabled&lt;T&gt; values. </summary>
    class NestedConverter : TypeConverter
    {
        readonly AnnotationPropertyGrid grid;
        readonly AnnotationCollection annotation;
        readonly MemberKind kind;

        public NestedConverter(AnnotationPropertyGrid grid, AnnotationCollection annotation, MemberKind kind)
        {
            this.grid = grid;
            this.annotation = annotation;
            this.kind = kind;
        }

        public override bool GetPropertiesSupported(ITypeDescriptorContext context) => true;

        public override PropertyDescriptorCollection GetProperties(ITypeDescriptorContext context, object value, Attribute[] attributes)
        {
            if (kind == MemberKind.Enabled)
            {
                var ev = annotation.Get<IEnabledValueAnnotation>();
                return new PropertyDescriptorCollection(new PropertyDescriptor[]
                {
                    new AnnotationPropertyDescriptor(grid, ev.IsEnabled, "IsEnabled", "Enabled"),
                    new AnnotationPropertyDescriptor(grid, ev.Value, "Value", "Value"),
                });
            }
            return new PropertyDescriptorCollection(grid.VisibleMembers(annotation, false)
                .Select(m => (PropertyDescriptor)new AnnotationPropertyDescriptor(grid, m, m.Get<IMemberAnnotation>().Member.Name, null))
                .ToArray());
        }

        public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType) => destinationType == typeof(string);

        public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
        {
            if (destinationType != typeof(string)) return base.ConvertTo(context, culture, value, destinationType);
            if (kind == MemberKind.Enabled)
            {
                var ev = annotation.Get<IEnabledValueAnnotation>();
                bool enabled = ev.IsEnabled.Get<IObjectValueAnnotation>()?.Value as bool? ?? false;
                return enabled ? AnnotationPropertyGrid.ValueText(ev.Value) : "(disabled)";
            }
            return AnnotationPropertyGrid.ValueText(annotation);
        }
    }

    #region Editors

    /// <summary> "..." button that runs a method / Action member. </summary>
    class ActionEditor : UITypeEditor
    {
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context) => UITypeEditorEditStyle.Modal;

        public override object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value)
        {
            if (context?.PropertyDescriptor is not AnnotationPropertyDescriptor d) return value;
            var name = d.Annotation.Get<DisplayAttribute>()?.Name;
            try
            {
                var method = d.Annotation.Get<IMethodAnnotation>();
                if (method != null) method.Invoke();
                else (d.Annotation.Get<IObjectValueAnnotation>()?.Value as Action)?.Invoke();
                GuiAction.Log.Info("'{0}' was executed.", name);
            }
            catch (Exception ex)
            {
                GuiAction.Log.Error(ex.Message);
                GuiAction.Log.Debug(ex);
                MessageBox.Show(ex.Message, name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            d.Grid.ReloadValues();
            return value;
        }
    }

    /// <summary> Drop-down checked list for multi-select members. </summary>
    class MultiSelectEditor : UITypeEditor
    {
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context) => UITypeEditorEditStyle.DropDown;

        public override object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value)
        {
            if (context?.PropertyDescriptor is not AnnotationPropertyDescriptor d) return value;
            var service = provider?.GetService(typeof(IWindowsFormsEditorService)) as IWindowsFormsEditorService;
            if (service == null) return value;

            var multi = d.Annotation.Get<IMultiSelectAnnotationProxy>();
            var available = d.Annotation.Get<IAvailableValuesAnnotationProxy>().AvailableValues.ToArray();
            var selected = new HashSet<AnnotationCollection>(multi.SelectedValues);
            var list = new CheckedListBox { CheckOnClick = true, BorderStyle = BorderStyle.None, IntegralHeight = true };
            foreach (var v in available)
                list.Items.Add(AnnotationPropertyGrid.ItemText(v), selected.Contains(v));
            list.Height = Math.Min(300, Math.Max(1, available.Length) * list.ItemHeight + 4);
            service.DropDownControl(list);

            try
            {
                multi.SelectedValues = list.CheckedIndices.Cast<int>().Select(i => available[i]).ToList();
                d.CommitFromEditor();
            }
            catch (Exception ex)
            {
                GuiAction.Log.Error(ex.Message);
                GuiAction.Log.Debug(ex);
            }
            return AnnotationPropertyGrid.ValueText(d.Annotation);
        }
    }

    /// <summary> "..." button opening the table editor for lists. </summary>
    class CollectionEditor : UITypeEditor
    {
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context) => UITypeEditorEditStyle.Modal;

        public override object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value)
        {
            if (context?.PropertyDescriptor is not AnnotationPropertyDescriptor d) return value;
            using (var form = new CollectionEditorForm(d.Annotation, d.IsReadOnly, d.Annotation.Get<DisplayAttribute>()?.Name))
                form.ShowDialog(d.Grid.FindForm());
            d.CommitFromEditor();
            return value;
        }
    }

    /// <summary> "..." button with a file or folder browser. </summary>
    class PathEditor : UITypeEditor
    {
        readonly FilePathAttribute file;
        readonly bool folder;

        public PathEditor(FilePathAttribute file, bool folder)
        {
            this.file = file;
            this.folder = folder;
        }

        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context) => UITypeEditorEditStyle.Modal;

        public override object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value)
        {
            var current = value as string ?? "";
            if (folder)
            {
                using var dlg = new FolderBrowserDialog { SelectedPath = current, UseDescriptionForTitle = true, Description = context?.PropertyDescriptor?.DisplayName };
                return dlg.ShowDialog() == DialogResult.OK ? dlg.SelectedPath : value;
            }

            FileDialog fd = file?.Behavior == FilePathAttribute.BehaviorChoice.Save
                ? new SaveFileDialog { OverwritePrompt = file.OverwritePrompt }
                : new OpenFileDialog { CheckFileExists = false };
            using (fd)
            {
                fd.Title = context?.PropertyDescriptor?.DisplayName;
                try { fd.FileName = System.IO.Path.GetFileName(current); } catch { }
                var ext = file?.FileExtension;
                fd.Filter = string.IsNullOrWhiteSpace(ext)
                    ? "All files (*.*)|*.*"
                    : (ext.Contains("|") ? ext : $"{ext} files (*.{ext.TrimStart('.')})|*.{ext.TrimStart('.')}|All files (*.*)|*.*");
                return fd.ShowDialog() == DialogResult.OK ? fd.FileName : value;
            }
        }
    }

    /// <summary> "..." button with a multi-line text box (members with Layout RowHeight &gt; 1). </summary>
    class MultilineEditor : UITypeEditor
    {
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context) => UITypeEditorEditStyle.Modal;

        public override object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value)
        {
            using var form = new Form
            {
                Text = context?.PropertyDescriptor?.DisplayName ?? "Edit",
                Width = 500, Height = 350,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
            };
            var text = new TextBox { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Text = (value as string ?? "").Replace("\n", Environment.NewLine) };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(4) };
            buttons.Controls.AddRange(new Control[] { cancel, ok });
            form.Controls.Add(text);
            form.Controls.Add(buttons);
            form.CancelButton = cancel;
            return form.ShowDialog() == DialogResult.OK ? text.Text.Replace("\r", "") : value;
        }
    }

    #endregion
}
