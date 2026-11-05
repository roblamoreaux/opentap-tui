using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using OpenTap.Package;

namespace OpenTap.Gui
{
    /// <summary> One package (or one version of a package) as shown in the package manager. Port of the TUI's PackageViewModel. </summary>
    class PackageInfo : IPackageIdentifier
    {
        public string Name { get; set; }
        public SemanticVersion Version { get; set; }
        public CpuArchitecture Architecture { get; set; }
        public string OS { get; set; }
        public string Group { get; set; }
        public string Owner { get; set; }
        public string SourceUrl { get; set; }
        public string Description { get; set; }
        public List<PackageDependency> Dependencies { get; set; }

        public static PackageInfo From(PackageDef p) => new PackageInfo
        {
            Name = p.Name, Version = p.Version, Architecture = p.Architecture, OS = p.OS, Group = p.Group,
            Owner = p.Owner, SourceUrl = p.SourceUrl, Description = p.Description, Dependencies = p.Dependencies
        };

        /// <summary> Reads one element of a package repository GraphQL "packages" response. </summary>
        public static PackageInfo FromJson(JsonElement e)
        {
            string Str(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var info = new PackageInfo
            {
                Name = Str("name"), Group = Str("group"), Owner = Str("owner"), SourceUrl = Str("sourceUrl"),
                Description = Str("description"), OS = Str("oS")
            };
            if (SemanticVersion.TryParse(Str("version"), out var version))
                info.Version = version;
            if (Enum.TryParse(Str("architecture"), out CpuArchitecture arch))
                info.Architecture = arch;
            if (e.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
            {
                info.Dependencies = new List<PackageDependency>();
                foreach (var d in deps.EnumerateArray())
                {
                    var depName = d.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var depVersion = d.TryGetProperty("version", out var v) ? v.GetString() : null;
                    if (!string.IsNullOrEmpty(depName) && VersionSpecifier.TryParse(depVersion, out var spec))
                        info.Dependencies.Add(new PackageDependency(depName, spec));
                }
            }
            return info;
        }

        public bool IsCompatibleWith(PackageDef installedOpenTap)
        {
            var compatible = this.IsPlatformCompatible();
            var openTapDependency = Dependencies?.FirstOrDefault(d => d.Name.Equals("OpenTAP", StringComparison.OrdinalIgnoreCase));
            if (openTapDependency != null && installedOpenTap != null && !openTapDependency.Version.IsCompatible(installedOpenTap.Version))
                compatible = false;
            return compatible;
        }
    }

    /// <summary> Reads packages from the repositories enabled in the Package Manager settings. </summary>
    static class PackageQueries
    {
        /// <summary> Latest version of every package (plus installed packages no repository has). </summary>
        public static List<PackageInfo> GetPackages(List<PackageDef> installed, CancellationToken cancel)
        {
            var result = new List<PackageInfo>();
            var names = new HashSet<string>();
            foreach (var repository in EnabledRepositories())
            {
                try
                {
                    GuiAction.Log.Info("Loading packages from: " + repository.Url);
                    IEnumerable<PackageInfo> packages;
                    if (repository.Manager is HttpPackageRepository http)
                        packages = Query(http, "packages(distinctName: true) { name version group owner sourceUrl description }");
                    else
                        packages = repository.Manager.GetPackages(new PackageSpecifier(null, null), cancel)
                            .GroupBy(p => p.Name)
                            .Select(g => PackageInfo.From(g.OrderByDescending(p => p.Version).First()));
                    foreach (var p in packages)
                        if (p.Name != null && names.Add(p.Name))
                            result.Add(p);
                }
                catch (Exception ex)
                {
                    GuiAction.Log.Error("Could not load packages from {0}: {1}", repository.Url, ex.Message);
                    GuiAction.Log.Debug(ex);
                }
            }
            foreach (var p in installed)
                if (names.Add(p.Name))
                    result.Add(PackageInfo.From(p));
            return result;
        }

        /// <summary> All versions of one package that are available for this OS/architecture, newest first. </summary>
        public static List<PackageInfo> GetVersions(string name, PackageDef installedOpenTap, CancellationToken cancel)
        {
            var result = new List<PackageInfo>();
            var versions = new HashSet<SemanticVersion>();
            foreach (var repository in EnabledRepositories())
            {
                try
                {
                    GuiAction.Log.Info("Loading versions of {0} from: {1}", name, repository.Url);
                    IEnumerable<PackageInfo> packages;
                    if (repository.Manager is HttpPackageRepository http)
                    {
                        packages = Query(http,
                            $"packages(name: \"{Escape(name)}\", os: \"{Escape(installedOpenTap.OS)}\", architecture: \"{installedOpenTap.Architecture}\") " +
                            "{ architecture oS version owner sourceUrl description dependencies { name version } }");
                    }
                    else
                    {
                        var list = new List<PackageInfo>();
                        foreach (var v in repository.Manager.GetPackageVersions(name, cancel, installedOpenTap))
                        {
                            var def = repository.Manager.GetPackages(new PackageSpecifier(name, VersionSpecifier.Parse(v.Version.ToString())), cancel).FirstOrDefault();
                            if (def != null) list.Add(PackageInfo.From(def));
                        }
                        packages = list;
                    }
                    foreach (var p in packages)
                    {
                        p.Name = name;
                        if (p.Version != null && versions.Add(p.Version))
                            result.Add(p);
                    }
                }
                catch (Exception ex)
                {
                    GuiAction.Log.Error("Could not load versions from {0}: {1}", repository.Url, ex.Message);
                    GuiAction.Log.Debug(ex);
                }
            }
            return result.OrderByDescending(p => p.Version).ToList();
        }

        static IEnumerable<RepositorySettingEntry> EnabledRepositories() =>
            PackageManagerSettings.Current.Repositories.Where(r => r.IsEnabled && r.Manager != null)
                .OrderByDescending(r => r.Manager is FilePackageRepository).ToList();

        static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        static List<PackageInfo> Query(HttpPackageRepository repository, string body)
        {
            object response = repository.QueryGraphQL("query Query { " + body + " }");
            var json = response switch
            {
                string s => s,
                JsonDocument d => d.RootElement.GetRawText(),
                _ => response?.ToString()
            };
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data)) root = data;
            if (!root.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array)
                return new List<PackageInfo>();
            return packages.EnumerateArray().Select(PackageInfo.FromJson).ToList();
        }

        /// <summary> Formats package details. Port of the TUI's PackageDetailsView.LoadPackage. </summary>
        public static string Details(PackageInfo package, List<PackageDef> installed, PackageDef installedOpenTap, bool latest)
        {
            if (package == null) return "";
            var installedVersion = installed.FirstOrDefault(i => i.Name == package.Name)?.Version;
            var sb = new StringBuilder();
            sb.AppendLine($"Name: {package.Name}{(installedVersion != null ? $"  (installed: {installedVersion})" : "")}");
            if (package.Owner != null) sb.AppendLine($"Owner: {package.Owner}");
            if (package.SourceUrl != null) sb.AppendLine($"Source: {package.SourceUrl}");
            if (package.Version != null) sb.AppendLine($"{(latest ? "Latest " : "")}Version: {package.Version}");
            if (package.Architecture != CpuArchitecture.Unspecified) sb.AppendLine($"Architecture: {package.Architecture}");
            if (package.OS != null) sb.AppendLine($"OS: {package.OS}");
            if (package.Dependencies != null && package.Dependencies.Any())
                sb.AppendLine($"Dependencies: {string.Join(", ", package.Dependencies.Select(d => $"{d.Name} {d.Version}"))}");
            if (package.OS != null || package.Architecture != CpuArchitecture.Unspecified)
                sb.AppendLine($"Compatible: {(package.IsCompatibleWith(installedOpenTap) ? "Yes" : "No")}");
            sb.AppendLine();
            sb.AppendLine("Description:");
            sb.Append(FormatDescription(package.Description));
            return sb.ToString().Replace("\r", "").Replace("\n", Environment.NewLine);
        }

        /// <summary> Package descriptions may contain XML elements such as Status, Links and Contacts. </summary>
        static string FormatDescription(string description)
        {
            if (string.IsNullOrWhiteSpace(description)) return "";
            var sb = new StringBuilder();
            try
            {
                var xml = new XmlDocument();
                xml.LoadXml($"<Description>{description}</Description>");

                string Attr(XmlElement el, string name) =>
                    el.Attributes.Cast<XmlAttribute>().FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.InnerText?.Trim();

                void Parse(XmlNode node)
                {
                    if (node is XmlElement element)
                    {
                        if (element.Name.Equals("Link", StringComparison.OrdinalIgnoreCase))
                            sb.AppendLine($"{Attr(element, "name")}: {Attr(element, "url")}");
                        else if (element.Name.Equals("Contact", StringComparison.OrdinalIgnoreCase))
                            sb.AppendLine($"{Attr(element, "name")}: {Attr(element, "email")}");
                        else
                        {
                            sb.AppendLine();
                            sb.AppendLine(element.Name + ":");
                            foreach (XmlNode child in element.ChildNodes)
                                Parse(child);
                        }
                    }
                    else if (node is XmlText text)
                    {
                        foreach (var line in text.InnerText.Split('\n').Select(x => x.Trim()).Where(x => x != ""))
                            sb.AppendLine(line);
                    }
                }

                foreach (XmlNode node in xml["Description"].ChildNodes)
                    Parse(node);
            }
            catch (XmlException)
            {
                return description.Trim();
            }
            return sb.ToString();
        }
    }

    /// <summary> Browse the package repositories and open a package to install, update or uninstall it. Port of the TUI's PackageManagerWindow. </summary>
    class PackageManagerForm : Form
    {
        readonly TreeView tree;
        readonly TextBox search;
        readonly Label status;
        readonly RichTextBox details;
        readonly Button manageButton;
        readonly Installation installation = Installation.Current;
        readonly PackageDef installedOpenTap;
        List<PackageDef> installed;
        List<PackageInfo> packages = new List<PackageInfo>();
        CancellationTokenSource loading;

        public PackageManagerForm()
        {
            Text = "Package Manager";
            Size = new Size(1100, 750);
            StartPosition = FormStartPosition.CenterScreen;
            installedOpenTap = installation.GetOpenTapPackage();
            installed = installation.GetPackages();

            var menu = new MenuStrip();
            var settingsMenu = new ToolStripMenuItem("&Settings");
            var pmSettings = new ToolStripMenuItem(TypeData.FromType(typeof(PackageManagerSettings)).GetDisplayAttribute().Name + "...");
            pmSettings.Click += (s, e) =>
            {
                using (var form = new ComponentSettingsForm(PackageManagerSettings.Current, pmSettings.Text.TrimEnd('.')))
                    form.ShowDialog(this);
                LoadPackages();
            };
            var refresh = new ToolStripMenuItem("&Refresh", null, (s, e) => LoadPackages()) { ShortcutKeys = Keys.F5 };
            settingsMenu.DropDownItems.AddRange(new ToolStripItem[] { pmSettings, refresh });
            menu.Items.Add(settingsMenu);
            MainMenuStrip = menu;

            // Left: searchable package tree.
            search = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Search packages..." };
            search.TextChanged += (s, e) => FillTree();
            status = new Label { Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) };
            tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, Font = new Font(Font, FontStyle.Bold) };
            tree.AfterSelect += (s, e) => ShowDetails();
            tree.NodeMouseDoubleClick += (s, e) => { if (e.Node.Tag is PackageInfo) ManageSelected(); };
            tree.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ManageSelected(); e.SuppressKeyPress = true; } };

            // Right: details and the install/uninstall button.
            details = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = true, BackColor = SystemColors.Window };
            details.LinkClicked += (s, e) => OpenUrl(e.LinkText);
            manageButton = new Button { Text = "Install / Uninstall...", AutoSize = true, Enabled = false };
            manageButton.Click += (s, e) => ManageSelected();
            var buttonBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
            buttonBar.Controls.Add(manageButton);

            var topSplit = new SplitContainer { Dock = DockStyle.Fill };
            topSplit.Panel1.Controls.Add(tree);
            topSplit.Panel1.Controls.Add(search);
            topSplit.Panel1.Controls.Add(status);
            topSplit.Panel2.Controls.Add(details);
            topSplit.Panel2.Controls.Add(buttonBar);
            topSplit.Panel2.Controls.Add(new Label { Text = "Package Details", Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) });

            var mainSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            mainSplit.Panel1.Controls.Add(topSplit);
            mainSplit.Panel2.Controls.Add(new LogPanel { Dock = DockStyle.Fill });
            mainSplit.Panel2.Controls.Add(new Label { Text = "Log", Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) });

            Controls.Add(mainSplit);
            Controls.Add(menu);

            Load += (s, e) =>
            {
                mainSplit.SplitterDistance = (int)(mainSplit.Height * 0.72);
                topSplit.SplitterDistance = (int)(topSplit.Width * 0.36);
                LoadPackages();
            };
            FormClosed += (s, e) => loading?.Cancel();
        }

        /// <summary> Reloads the packages in the background. </summary>
        void LoadPackages()
        {
            loading?.Cancel();
            var cts = loading = new CancellationTokenSource();
            status.Text = "Packages (loading...)";
            tree.Nodes.Clear();
            Task.Run(() =>
            {
                List<PackageInfo> result;
                try
                {
                    result = PackageQueries.GetPackages(installation.GetPackages(), cts.Token);
                }
                catch (Exception ex)
                {
                    GuiAction.Log.Error(ex.Message);
                    GuiAction.Log.Debug(ex);
                    result = new List<PackageInfo>();
                }
                if (cts.IsCancellationRequested || IsDisposed) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        packages = result;
                        FillTree();
                    }));
                }
                catch (InvalidOperationException)
                {
                    // Window closed.
                }
            });
        }

        void FillTree()
        {
            installed = installation.GetPackages();
            var installedNames = new HashSet<string>(installed.Select(p => p.Name));
            var filter = search.Text.Trim();
            var selectedName = (tree.SelectedNode?.Tag as PackageInfo)?.Name;

            var visible = packages
                .Where(p => filter.Length == 0 || $"{p.Group} {p.Name}".IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(p => installedNames.Contains(p.Name))
                .ThenBy(p => p.Group + p.Name)
                .ToList();

            tree.BeginUpdate();
            tree.Nodes.Clear();
            TreeNode select = null;
            foreach (var package in visible)
            {
                var nodes = tree.Nodes;
                foreach (var group in (package.Group ?? "").Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var groupNode = nodes.Cast<TreeNode>().FirstOrDefault(n => n.Tag == null && n.Text == group) ?? nodes.Add(group);
                    nodes = groupNode.Nodes;
                }
                var isInstalled = installedNames.Contains(package.Name);
                var node = nodes.Add(package.Name + (isInstalled ? "  (installed)" : ""));
                node.Tag = package;
                node.NodeFont = Font;
                if (isInstalled) node.ForeColor = Color.DarkGreen;
                if (package.Name == selectedName) select = node;
            }
            if (filter.Length > 0) tree.ExpandAll();
            tree.EndUpdate();
            if (select != null) tree.SelectedNode = select;

            status.Text = $"Packages ({visible.Count}{(filter.Length > 0 ? $" of {packages.Count}" : "")})";
            ShowDetails();
        }

        void ShowDetails()
        {
            var package = tree.SelectedNode?.Tag as PackageInfo;
            details.Text = PackageQueries.Details(package, installed, installedOpenTap, true);
            manageButton.Enabled = package != null;
        }

        void ManageSelected()
        {
            if (tree.SelectedNode?.Tag is not PackageInfo package) return;
            using (var form = new PackageVersionsForm(package, installation, installedOpenTap))
                form.ShowDialog(this);
            FillTree();
        }

        internal static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { GuiAction.Log.Warning("Could not open {0}: {1}", url, ex.Message); }
        }
    }

    /// <summary> Choose a version of one package and install or uninstall it. Port of the TUI's PackageVersionSelectorWindow. </summary>
    class PackageVersionsForm : Form
    {
        readonly PackageInfo package;
        readonly Installation installation;
        readonly PackageDef installedOpenTap;
        readonly ListBox versionList;
        readonly RichTextBox details;
        readonly Button actionButton;
        readonly ProgressBar progress;
        readonly Label progressText;
        List<PackageInfo> versions = new List<PackageInfo>();
        PackageDef installedVersion;
        TapThread runningThread;
        readonly CancellationTokenSource loading = new CancellationTokenSource();

        public PackageVersionsForm(PackageInfo package, Installation installation, PackageDef installedOpenTap)
        {
            this.package = package;
            this.installation = installation;
            this.installedOpenTap = installedOpenTap;
            Text = "Install Package - " + package.Name;
            Size = new Size(950, 650);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;

            versionList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            versionList.SelectedIndexChanged += (s, e) => UpdateSelection();

            details = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = true, BackColor = SystemColors.Window };
            details.LinkClicked += (s, e) => PackageManagerForm.OpenUrl(e.LinkText);

            actionButton = new Button { Text = "Install", AutoSize = true, MinimumSize = new Size(90, 0), Enabled = false };
            actionButton.Click += (s, e) => ActionClicked();
            progress = new ProgressBar { Width = 250, Visible = false };
            progressText = new Label { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, Padding = new Padding(4) };
            bar.Controls.AddRange(new Control[] { actionButton, progress, progressText });
            var closeBar = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, Padding = new Padding(4) };
            closeBar.Controls.Add(close);
            CancelButton = close;

            var split = new SplitContainer { Dock = DockStyle.Fill };
            split.Panel1.Controls.Add(versionList);
            split.Panel1.Controls.Add(new Label { Text = "Versions", Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) });
            split.Panel2.Controls.Add(details);
            split.Panel2.Controls.Add(new Label { Text = "Package Details", Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) });

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            bottom.Controls.Add(bar);
            bottom.Controls.Add(closeBar);

            var mainSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            mainSplit.Panel1.Controls.Add(split);
            mainSplit.Panel1.Controls.Add(bottom);
            mainSplit.Panel2.Controls.Add(new LogPanel { Dock = DockStyle.Fill });

            Controls.Add(mainSplit);

            Load += (s, e) =>
            {
                mainSplit.SplitterDistance = (int)(mainSplit.Height * 0.68);
                split.SplitterDistance = 240;
                LoadVersions();
            };
        }

        void LoadVersions()
        {
            versionList.Items.Clear();
            versionList.Items.Add("Loading...");
            Task.Run(() =>
            {
                List<PackageInfo> result;
                try
                {
                    result = PackageQueries.GetVersions(package.Name, installedOpenTap, loading.Token);
                }
                catch (Exception ex)
                {
                    GuiAction.Log.Error("Error getting package versions: '{0}'", ex.Message);
                    GuiAction.Log.Debug(ex);
                    result = new List<PackageInfo>();
                }
                if (loading.IsCancellationRequested) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        versions = result;
                        if (versions.Count == 0 && installation.GetPackages().All(p => p.Name != package.Name))
                        {
                            // This happens if the architecture or OS does not match any of the packages.
                            MessageBox.Show(this, "No compatible plugin packages available.", "No Plugin Packages Available", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            Close();
                            return;
                        }
                        UpdateVersions();
                    }));
                }
                catch (InvalidOperationException)
                {
                    // Window closed.
                }
            });
        }

        void UpdateVersions()
        {
            installedVersion = installation.GetPackages().FirstOrDefault(p => p.Name == package.Name);
            // An installed version that no repository offers (any more) can still be uninstalled.
            if (installedVersion != null && versions.All(v => v.Version != installedVersion.Version))
                versions.Insert(0, PackageInfo.From(installedVersion));

            var selected = (versionList.SelectedIndex >= 0 && versionList.SelectedIndex < versions.Count) ? versions[versionList.SelectedIndex].Version : null;
            versionList.BeginUpdate();
            versionList.Items.Clear();
            foreach (var v in versions)
                versionList.Items.Add($"{v.Version}{(installedVersion?.Version == v.Version ? "  (installed)" : "")}");
            versionList.EndUpdate();
            var index = selected == null ? -1 : versions.FindIndex(v => v.Version == selected);
            versionList.SelectedIndex = versions.Count == 0 ? -1 : Math.Max(0, index);
            UpdateSelection();
        }

        PackageInfo SelectedVersion => versionList.SelectedIndex >= 0 && versionList.SelectedIndex < versions.Count ? versions[versionList.SelectedIndex] : null;

        void UpdateSelection()
        {
            var version = SelectedVersion;
            details.Text = PackageQueries.Details(version, installation.GetPackages(), installedOpenTap, false);
            if (runningThread != null) return;
            actionButton.Enabled = version != null;
            actionButton.Text = version != null && installedVersion?.Version == version.Version ? "Uninstall" : "Install";
        }

        void ActionClicked()
        {
            if (runningThread != null)
            {
                runningThread.Abort();
                return;
            }
            var version = SelectedVersion;
            if (version == null) return;

            bool uninstall = installedVersion?.Version == version.Version;
            if (uninstall && MessageBox.Show(this, $"Uninstall {package.Name} {version.Version}?", "Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            PackageAction action = uninstall
                ? new PackageUninstallAction { Packages = new[] { package.Name } }
                : new PackageInstallAction { Packages = new[] { package.Name }, Version = version.Version.ToString() };
            var verb = uninstall ? "Uninstall" : "Installation";
            action.Error += ex => BeginInvoke(new Action(() =>
                MessageBox.Show(this, ex.Message, $"{verb} Error", MessageBoxButtons.OK, MessageBoxIcon.Error)));
            action.ProgressUpdate += (percent, message) => BeginInvoke(new Action(() =>
            {
                progress.Value = Math.Max(0, Math.Min(100, percent));
                progressText.Text = message;
            }));

            actionButton.Text = "Cancel";
            progress.Value = 0;
            progress.Visible = true;
            progressText.Text = uninstall ? "Uninstalling..." : "Installing...";
            runningThread = TapThread.Start(() =>
            {
                int exitCode = 1;
                try
                {
                    exitCode = action.Execute(TapThread.Current.AbortToken);
                }
                catch (Exception ex)
                {
                    GuiAction.Log.Error(ex.Message);
                    GuiAction.Log.Debug(ex);
                }
                try
                {
                    BeginInvoke(new Action(() => Finished(exitCode == 0, uninstall)));
                }
                catch (InvalidOperationException)
                {
                    // Window closed.
                }
            }, "Package Action");
        }

        void Finished(bool success, bool uninstall)
        {
            runningThread = null;
            progress.Visible = false;
            progressText.Text = success ? (uninstall ? "Uninstalled." : "Installed.") : "Failed - see the log.";
            UpdateVersions();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (runningThread != null)
            {
                if (MessageBox.Show(this, "A package operation is still running. Cancel it?", "Close", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                runningThread.Abort();
            }
            loading.Cancel();
            base.OnFormClosing(e);
        }
    }
}
