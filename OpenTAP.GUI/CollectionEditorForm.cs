using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace OpenTap.Gui
{
    /// <summary>
    /// Table editor for list properties. Port of the TUI's DataGridEditProvider: one column per member of the
    /// element type (or a single column for simple lists), with Add Row / Remove Row.
    /// Like the TUI, edits apply immediately; the dialog only has a Close button.
    /// </summary>
    class CollectionEditorForm : Form
    {
        readonly AnnotationCollection annotation;
        readonly ICollectionAnnotation collection;
        readonly bool readOnly;
        readonly bool fixedSize;
        readonly DataGridView grid;
        readonly Button addButton, removeButton;
        List<AnnotationCollection> items;
        List<(string Key, string Header)> columns;
        bool isPolymorphic;
        bool loading;

        public CollectionEditorForm(AnnotationCollection annotation, bool readOnly, string title)
        {
            this.annotation = annotation;
            this.readOnly = readOnly;
            collection = annotation.Get<ICollectionAnnotation>();
            fixedSize = annotation.Get<IFixedSizeCollectionAnnotation>()?.IsFixedSize ?? false;

            Text = title ?? "Edit list";
            Width = 700;
            Height = 450;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;

            grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                ReadOnly = readOnly,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
                BackgroundColor = SystemColors.Window
            };
            grid.CellValueChanged += OnCellValueChanged;
            grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                // Commit check boxes and combo boxes immediately instead of waiting for the cell to lose focus.
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.DataError += (s, e) => e.ThrowException = false;

            addButton = new Button { Text = "Add Row", AutoSize = true, Enabled = !readOnly && !fixedSize };
            addButton.Click += (s, e) => AddRow();
            removeButton = new Button { Text = "Remove Row", AutoSize = true, Enabled = !readOnly && !fixedSize };
            removeButton.Click += (s, e) => RemoveRow();
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };

            var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, Padding = new Padding(4) };
            left.Controls.AddRange(new Control[] { addButton, removeButton });
            var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, Padding = new Padding(4) };
            right.Controls.Add(close);
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            bottom.Controls.Add(left);
            bottom.Controls.Add(right);

            Controls.Add(grid);
            Controls.Add(bottom);
            AcceptButton = close;
            CancelButton = close;

            Reload();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            grid.EndEdit();
            try
            {
                collection.AnnotatedElements = items;
                annotation.Write();
            }
            catch (Exception ex)
            {
                GuiAction.Log.Error(ex.Message);
                GuiAction.Log.Debug(ex);
            }
            base.OnFormClosing(e);
        }

        void Reload()
        {
            items = collection.AnnotatedElements.ToList();
            bool placeholder = false;
            if (items.Count == 0)
            {
                // Add a placeholder element just to find out which columns to create (as the TUI does).
                var proto = collection.NewElement();
                if (proto != null)
                {
                    placeholder = true;
                    items = new List<AnnotationCollection> { proto };
                    collection.AnnotatedElements = items;
                }
            }

            columns = GetColumns(items);
            isPolymorphic = IsPolymorphic();

            if (placeholder)
            {
                annotation.Read();
                items = collection.AnnotatedElements.ToList();
            }
            FillGrid();
        }

        List<(string, string)> GetColumns(List<AnnotationCollection> elements)
        {
            var result = new List<(string, string)>();
            if (elements.Any(x => x.Get<IMembersAnnotation>() != null))
            {
                var orders = new Dictionary<string, double>();
                var headers = new Dictionary<string, string>();
                foreach (var element in elements)
                {
                    foreach (var m in element.Get<IMembersAnnotation>()?.Members ?? Enumerable.Empty<AnnotationCollection>())
                    {
                        var member = m.Get<IMemberAnnotation>()?.Member;
                        var key = ColumnKey(member);
                        if (key == null) continue;
                        var display = member.GetDisplayAttribute();
                        headers[key] = display.GetFullName();
                        orders[key] = display.Order;
                    }
                }
                foreach (var key in headers.Keys.OrderBy(k => orders[k]).ThenBy(k => k))
                    result.Add((key, headers[key]));
            }
            else
            {
                var name = annotation.Get<DisplayAttribute>()?.Name ?? "Value";
                result.Add((name, name));
            }
            return result;
        }

        static string ColumnKey(IMemberData member)
        {
            if (member == null || AnnotationPropertyGrid.FilterMember(member) == false) return null;
            var display = member.GetDisplayAttribute();
            return display == null ? null : display.GetFullName() + member.TypeDescriptor.Name;
        }

        bool IsPolymorphic()
        {
            var type = collection.NewElement()?.Get<IReflectionAnnotation>()?.ReflectionInfo;
            if (type is TypeData td) return (td.DerivedTypes?.Any() ?? false);
            return type != null && type.CanCreateInstance == false;
        }

        AnnotationCollection Cell(AnnotationCollection item, int column)
        {
            var members = item.Get<IMembersAnnotation>()?.Members;
            if (members == null) return item;
            return members.FirstOrDefault(m => ColumnKey(m.Get<IMemberAnnotation>()?.Member) == columns[column].Key);
        }

        void FillGrid()
        {
            loading = true;
            try
            {
                grid.Rows.Clear();
                grid.Columns.Clear();
                foreach (var (key, header) in columns)
                    grid.Columns.Add(new DataGridViewTextBoxColumn { Name = key, HeaderText = header, SortMode = DataGridViewColumnSortMode.NotSortable });

                foreach (var item in items)
                {
                    var row = new DataGridViewRow();
                    for (int c = 0; c < columns.Count; c++)
                        row.Cells.Add(CreateCell(Cell(item, c)));
                    grid.Rows.Add(row);
                    for (int c = 0; c < columns.Count; c++)
                    {
                        var cellAnnotation = Cell(item, c);
                        row.Cells[c].ReadOnly = readOnly || cellAnnotation == null || !IsEditable(cellAnnotation);
                    }
                }
            }
            finally
            {
                loading = false;
            }
            removeButton.Enabled = !readOnly && !fixedSize && items.Count > 0;
        }

        static bool IsEditable(AnnotationCollection cell)
        {
            if (cell.Get<IAccessAnnotation>()?.IsReadOnly == true) return false;
            if (cell.Get<IEnabledAnnotation>()?.IsEnabled == false) return false;
            return cell.Get<IAvailableValuesAnnotationProxy>() != null
                   || cell.Get<IStringValueAnnotation>() != null
                   || IsBool(cell);
        }

        static bool IsBool(AnnotationCollection cell) =>
            cell.Get<IReflectionAnnotation>()?.ReflectionInfo?.DescendsTo(TypeData.FromType(typeof(bool))) == true
            && cell.Get<IObjectValueAnnotation>() != null;

        static DataGridViewCell CreateCell(AnnotationCollection cell)
        {
            if (cell == null) return new DataGridViewTextBoxCell { Value = "" };
            if (IsBool(cell))
                return new DataGridViewCheckBoxCell { Value = cell.Get<IObjectValueAnnotation>().Value as bool? ?? false };
            var avail = cell.Get<IAvailableValuesAnnotationProxy>();
            if (avail != null && cell.Get<IMultiSelectAnnotationProxy>() == null)
            {
                var combo = new DataGridViewComboBoxCell { FlatStyle = FlatStyle.Flat };
                var options = avail.AvailableValues.Select(AnnotationPropertyGrid.ItemText).ToList();
                var current = AnnotationPropertyGrid.ValueText(cell);
                if (!options.Contains(current)) options.Add(current);
                combo.Items.AddRange(options.Cast<object>().ToArray());
                combo.Value = current;
                return combo;
            }
            return new DataGridViewTextBoxCell { Value = AnnotationPropertyGrid.ValueText(cell) };
        }

        void OnCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (loading || e.RowIndex < 0 || e.RowIndex >= items.Count) return;
            var cell = Cell(items[e.RowIndex], e.ColumnIndex);
            if (cell == null) return;
            var gridCell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            try
            {
                if (IsBool(cell))
                    cell.Get<IObjectValueAnnotation>().Value = gridCell.Value is bool b && b;
                else if (cell.Get<IAvailableValuesAnnotationProxy>() is IAvailableValuesAnnotationProxy avail)
                {
                    var text = gridCell.Value as string;
                    var match = avail.AvailableValues.FirstOrDefault(v => AnnotationPropertyGrid.ItemText(v) == text);
                    if (match != null) avail.SelectedValue = match;
                }
                else if (cell.Get<IStringValueAnnotation>() is IStringValueAnnotation str)
                    str.Value = gridCell.Value as string ?? "";
                cell.Write();
                cell.Read();
                grid.Rows[e.RowIndex].ErrorText = "";
            }
            catch (Exception ex)
            {
                grid.Rows[e.RowIndex].ErrorText = ex.Message;
                GuiAction.Log.Error(ex.Message);
                GuiAction.Log.Debug(ex);
            }
            loading = true;
            gridCell.Value = IsBool(cell) ? cell.Get<IObjectValueAnnotation>().Value : AnnotationPropertyGrid.ValueText(cell);
            loading = false;
        }

        void AddRow()
        {
            grid.EndEdit();
            try
            {
                if (isPolymorphic)
                {
                    var type = (annotation.Get<IReflectionAnnotation>().ReflectionInfo as TypeData)?.Load();
                    var elementType = type == null ? null : GetEnumerableElementType(type);
                    if (elementType == null) return;
                    using var picker = new AddPluginDialog(TypeData.FromType(elementType), "Add Element", null);
                    if (picker.ShowDialog(this) != DialogResult.OK || picker.PluginType == null) return;
                    items.Add(AnnotationCollection.Annotate(picker.PluginType.CreateInstance()));
                }
                else
                {
                    items.Add(collection.NewElement());
                }
                collection.AnnotatedElements = items;
                annotation.Write();
                annotation.Read();
                items = collection.AnnotatedElements.ToList();
                columns = GetColumns(items);
                FillGrid();
                if (grid.Rows.Count > 0)
                    grid.CurrentCell = grid.Rows[grid.Rows.Count - 1].Cells[0];
            }
            catch (Exception ex)
            {
                GuiAction.Log.Error(ex.Message);
                GuiAction.Log.Debug(ex);
                MessageBox.Show(this, ex.Message, "Add Row", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void RemoveRow()
        {
            grid.EndEdit();
            var rows = grid.SelectedCells.Cast<DataGridViewCell>().Select(c => c.RowIndex)
                .Concat(grid.CurrentCell != null ? new[] { grid.CurrentCell.RowIndex } : Array.Empty<int>())
                .Where(r => r >= 0 && r < items.Count).Distinct().OrderByDescending(r => r).ToList();
            if (rows.Count == 0) return;
            foreach (var r in rows)
                items.RemoveAt(r);
            collection.AnnotatedElements = items;
            items = collection.AnnotatedElements.ToList();
            FillGrid();
        }

        public static Type GetEnumerableElementType(Type enumType)
        {
            if (enumType.IsArray)
                return enumType.GetElementType();
            var ienum = enumType.GetInterface("IEnumerable`1");
            return ienum?.GetGenericArguments().FirstOrDefault() ?? typeof(object);
        }
    }
}
