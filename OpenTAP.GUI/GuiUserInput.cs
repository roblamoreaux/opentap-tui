using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace OpenTap.Gui
{
    /// <summary>
    /// Shows OpenTAP user input requests (e.g. from a Dialog step or an instrument asking a question) as Windows dialogs.
    /// Port of the TUI's TuiUserInput.
    /// </summary>
    public class GuiUserInput : IUserInputInterface
    {
        public void RequestUserInput(object dataObject, TimeSpan timeout, bool modal)
        {
            bool timedOut = false;

            void ShowDialog(IWin32Window owner)
            {
                using var dialog = new UserInputForm(dataObject);
                System.Windows.Forms.Timer timer = null;
                if (timeout != TimeSpan.MaxValue && timeout.TotalMilliseconds < int.MaxValue)
                {
                    timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, (int)timeout.TotalMilliseconds) };
                    timer.Tick += (s, e) =>
                    {
                        timer.Stop();
                        timedOut = true;
                        dialog.Close();
                    };
                    timer.Start();
                }
                try
                {
                    dialog.ShowDialog(owner);
                }
                finally
                {
                    timer?.Dispose();
                }
            }

            var main = GuiAction.MainForm;
            if (main != null && main.IsHandleCreated && !main.IsDisposed)
            {
                if (main.InvokeRequired)
                    main.Invoke(new Action(() => ShowDialog(main)));
                else
                    ShowDialog(main);
            }
            else
            {
                // No main window (yet): show the dialog on its own UI thread.
                var thread = new Thread(() => ShowDialog(null));
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();
            }

            if (timedOut)
                throw new TimeoutException("User input timed out.");
        }
    }

    class UserInputForm : Form
    {
        readonly AnnotationCollection annotations;

        public UserInputForm(object dataObject)
        {
            annotations = AnnotationCollection.Annotate(dataObject);
            var members = annotations.Get<IMembersAnnotation>()?.Members?.ToList() ?? new List<AnnotationCollection>();

            Text = GetTitle(members, dataObject);
            Width = 520;
            Height = 380;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            TopMost = true;

            // Read-only full-row text (the usual "message" of a dialog) is shown as a label instead of a grid row.
            bool IsMessage(AnnotationCollection m)
            {
                var member = m.Get<IMemberAnnotation>()?.Member;
                var layout = member?.GetAttribute<LayoutAttribute>();
                bool readOnly = m.Get<IStringValueAnnotation>() == null || (m.Get<IAccessAnnotation>()?.IsReadOnly ?? false) || !member.Writable;
                return layout != null && layout.Mode.HasFlag(LayoutMode.FullRow) && readOnly
                       && m.Get<IStringReadOnlyValueAnnotation>() != null
                       && m.Get<IAvailableValuesAnnotationProxy>() == null
                       && (m.Get<IAccessAnnotation>()?.IsVisible ?? true);
            }

            var messages = members.Where(IsMessage).Select(AnnotationPropertyGrid.ValueText).Where(s => s.Length > 0).ToList();

            var grid = new AnnotationPropertyGrid { Dock = DockStyle.Fill, HideMember = IsMessage };
            grid.LoadProperties(dataObject);

            if (messages.Any())
            {
                var label = new Label
                {
                    Dock = DockStyle.Top,
                    Text = string.Join(Environment.NewLine + Environment.NewLine, messages),
                    Padding = new Padding(10),
                    AutoSize = false,
                    Font = new Font(Font.FontFamily, 10f)
                };
                label.Height = TextRenderer.MeasureText(label.Text, label.Font, new Size(Width - 40, 0), TextFormatFlags.WordBreak).Height + 24;
                Controls.Add(label);
            }

            // Hide the grid if the dialog has nothing to edit.
            var visible = grid.VisibleMembers(annotations, true).Any();
            if (visible)
                Controls.Add(grid);
            else
                Height = Math.Max(180, (Controls.Count > 0 ? Controls[0].Height : 0) + 110);
            if (visible) grid.BringToFront();

            Controls.Add(CreateSubmitButtons(members));
        }

        Control CreateSubmitButtons(List<AnnotationCollection> members)
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(6) };
            var submit = members.FirstOrDefault(m => (m.Get<IAccessAnnotation>()?.IsVisible ?? true)
                                                     && m.Get<IMemberAnnotation>()?.Member.GetAttribute<SubmitAttribute>() != null);
            var avail = submit?.Get<IAvailableValuesAnnotationProxy>();
            if (avail == null)
            {
                var ok = new Button { Text = "OK", AutoSize = true, MinimumSize = new Size(80, 0) };
                ok.Click += (s, e) => Finish(submit, null, null);
                bar.Controls.Add(ok);
                AcceptButton = ok;
                return bar;
            }

            // One button per choice of the submit member (e.g. Yes / No), in declaration order from the left.
            foreach (var value in avail.AvailableValues.Reverse())
            {
                var captured = value;
                var button = new Button { Text = AnnotationPropertyGrid.ItemText(value), AutoSize = true, MinimumSize = new Size(80, 0) };
                button.Click += (s, e) => Finish(submit, avail, captured);
                bar.Controls.Add(button);
                if (avail.SelectedValue == value)
                    AcceptButton = button;
            }
            return bar;
        }

        void Finish(AnnotationCollection submitMember, IAvailableValuesAnnotationProxy choices, AnnotationCollection choice)
        {
            // Other values were already written by the property grid; only the submit member is left.
            try
            {
                if (choices != null)
                    choices.SelectedValue = choice;
                submitMember?.Write();
            }
            catch (Exception ex)
            {
                GuiAction.Log.Error(ex.Message);
                GuiAction.Log.Debug(ex);
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        static string GetTitle(List<AnnotationCollection> members, object dataObject)
        {
            string ByName(string n) => members.FirstOrDefault(m => m.Get<DisplayAttribute>()?.Name?.Equals(n, StringComparison.OrdinalIgnoreCase) == true)
                ?.Get<IStringValueAnnotation>()?.Value;
            var title = ByName("title") ?? ByName("name");
            if (title == null)
            {
                var typeData = TypeData.GetTypeData(dataObject);
                title = typeData.GetDisplayAttribute()?.Name ?? typeData.Name;
            }
            return title;
        }
    }
}
