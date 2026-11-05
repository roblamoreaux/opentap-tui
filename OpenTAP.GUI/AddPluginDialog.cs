using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace OpenTap.Gui
{
    /// <summary>
    /// Pick a plugin type (test step, instrument, DUT, ...) from a grouped, searchable tree.
    /// Port of the TUI's NewPluginWindow.
    /// </summary>
    class AddPluginDialog : Form
    {
        readonly TreeView tree;
        readonly TextBox search;
        readonly Label description;
        readonly Button ok;
        readonly ITypeData[] types;

        public ITypeData PluginType { get; private set; }

        public AddPluginDialog(ITypeData baseType, string title, ITypeData parentType)
        {
            Text = title;
            Width = 520;
            Height = 600;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;

            types = TypeData.GetDerivedTypes(baseType)
                .Where(x => x.CanCreateInstance)
                .Where(x => x.GetAttribute<BrowsableAttribute>()?.Browsable ?? true)
                .Where(x => parentType == null || TestStepList.AllowChild(AsTypeData(parentType)?.Type, AsTypeData(x)?.Type))
                .ToArray();

            search = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Search..." };
            search.TextChanged += (s, e) => Fill();
            search.KeyDown += (s, e) =>
            {
                // Let arrow keys move through the results while typing.
                if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
                {
                    tree.Focus();
                    SendKeys.Send(e.KeyCode == Keys.Down ? "{DOWN}" : "{UP}");
                    e.Handled = true;
                }
            };

            tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, Font = new Font(Font, FontStyle.Bold) };
            tree.AfterSelect += (s, e) =>
            {
                var type = e.Node?.Tag as ITypeData;
                description.Text = type?.GetDisplayAttribute()?.Description ?? "";
                ok.Enabled = type != null;
            };
            tree.NodeMouseDoubleClick += (s, e) =>
            {
                if (e.Node?.Tag is ITypeData)
                    Accept();
            };

            description = new Label { Dock = DockStyle.Bottom, Height = 70, Padding = new Padding(4), BorderStyle = BorderStyle.Fixed3D };

            ok = new Button { Text = "Add", Enabled = false, AutoSize = true };
            ok.Click += (s, e) => Accept();
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(4) };
            buttons.Controls.AddRange(new Control[] { cancel, ok });

            Controls.Add(tree);
            Controls.Add(search);
            Controls.Add(description);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;

            Fill();
            Shown += (s, e) => search.Focus();
        }

        void Accept()
        {
            if (tree.SelectedNode?.Tag is ITypeData type)
            {
                PluginType = type;
                DialogResult = DialogResult.OK;
                Close();
            }
        }

        static string TypeName(ITypeData t) => t.GetDisplayAttribute()?.Name ?? t.Name;

        void Fill()
        {
            var filter = search.Text.Trim();
            tree.BeginUpdate();
            tree.Nodes.Clear();
            foreach (var type in types.OrderBy(t => string.Join("\\", t.GetDisplayAttribute()?.Group ?? Array.Empty<string>())).ThenBy(TypeName))
            {
                var display = type.GetDisplayAttribute();
                var groups = display?.Group ?? Array.Empty<string>();
                var fullName = string.Join(" \\ ", groups.Append(TypeName(type)));
                if (filter.Length > 0 && fullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var nodes = tree.Nodes;
                foreach (var group in groups)
                {
                    var groupNode = nodes.Cast<TreeNode>().FirstOrDefault(n => n.Tag == null && n.Text == group);
                    if (groupNode == null)
                    {
                        // Group nodes use the tree's (bold) font; plugin nodes use the regular font. This way round
                        // the TreeView measures every label wide enough (a bold NodeFont on a regular tree gets clipped).
                        groupNode = new TreeNode(group);
                        nodes.Add(groupNode);
                    }
                    nodes = groupNode.Nodes;
                }
                nodes.Add(new TreeNode(TypeName(type)) { Tag = type, ToolTipText = display?.Description, NodeFont = Font });
            }
            if (filter.Length > 0)
                tree.ExpandAll();
            tree.EndUpdate();

            var first = FirstLeaf(tree.Nodes);
            if (first != null && filter.Length > 0)
                tree.SelectedNode = first;
        }

        static TreeNode FirstLeaf(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Tag != null) return n;
                var leaf = FirstLeaf(n.Nodes);
                if (leaf != null) return leaf;
            }
            return null;
        }

        static TypeData AsTypeData(ITypeData typedata)
        {
            while (typedata != null)
            {
                if (typedata is TypeData td)
                    return td;
                typedata = typedata.BaseType;
            }
            return null;
        }
    }
}
