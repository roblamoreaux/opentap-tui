# OpenTAP GUI (Windows Forms port of the TUI)

A Windows desktop version of the OpenTAP TUI in this repository. Like the TUI it is an OpenTAP plugin,
so it runs inside `tap.exe` with every installed plugin, instrument and setting available:

```
tap gui [plan.TapPlan]   # test plan editor (Tools menu also opens the two below)
tap gui-pm               # package manager
tap gui-results          # results viewer
```

When started from a shortcut or Explorer, the console window `tap.exe` opens is hidden automatically;
when typed into a terminal, the terminal is left alone (and still shows the log).

Requires OpenTAP 9.33 (its `tap.exe` runs on the .NET 9 Windows Desktop runtime, which WinForms needs).

## Build

```
dotnet build OpenTAP.GUI/OpenTap.Gui.csproj
```

The Debug output (`OpenTAP.GUI/bin/Debug`) is a self-contained OpenTAP 9.33 install for testing:
`OpenTAP.GUI\bin\Debug\tap.exe gui`. A Release build also produces `GUI.<version>.TapPackage`
that can be installed into a bench with `tap package install`.

## What maps to what

| TUI | GUI |
|---|---|
| `MainWindow` / `TUI` action | `MainForm` / `GuiAction` (`tap gui`) |
| `TestPlanView` (tree, move-selection mode, copy/paste) | Tree on the left: context menu, drag and drop, Ctrl+click multi-select, Cut/Copy/Paste, Move Up/Down, Space to enable/disable |
| `PropertiesView` + `PropEditProviders/*` | `AnnotationPropertyGrid`: standard Windows PropertyGrid driven by OpenTAP annotations (drop-downs, multi-select, Enabled&lt;T&gt;, file/folder browse, secure strings, actions, right-click Parameterize/Assign Output, validation ⚠) |
| `DataGridEditProvider` | `CollectionEditorForm` (table editor for list properties) |
| `NewPluginWindow` | `AddPluginDialog` (grouped, searchable) |
| `ComponentSettingsWindow`, `ResourceSettingsWindow`, `ConnectionSettingsWindow` | `ComponentSettingsForm`, `ListSettingsForm` (Bench / Settings menus) |
| `SettingsProfileWindow` | `ProfilesForm` (Bench → Profiles) |
| `TuiUserInput` | `GuiUserInput` (dialog steps, plan-load questions, ...) |
| `LogPanelView` | `LogView` (colored by level, copy, clear) |
| `Recovery` | `Recovery` (offers to recover unsaved plans after a crash) |
| `TuiSettings` (log panel options) | `GuiSettings` (Settings → GUI Settings) |
| `TuiPm`, `PackageManagerWindow`, `PackageListView`, `PackageDetailsView` | `gui-pm`, `PackageManagerForm` (searchable package list from the repositories in Package Manager settings, details, log) |
| `PackageVersionSelectorWindow` | `PackageVersionsForm` (pick a version, Install / Uninstall with progress and Cancel) |
| `TuiResults`, `ResultsViewerWindow` (RunExplorerWindow.cs) | `gui-results`, `ResultsViewerForm` (runs from the result stores in Results settings, check runs to plot) |
| `ResultsWindow`, `ChartSettings`, `Plot`, `ExportDialogInput` | `ResultsForm`, `ChartSettings`, `ChartControl` (scatter / line / column, log axes, zoom, pan, tooltips, export through table-export plugins such as CSV) |

New in the GUI (not in the TUI): the results viewer's **Step Results** grid (`StepResultsGrid`). Select one or
more runs to list every step with its verdict, result values, Min/Max limits and Check Limits. The limits come
from each step's "Limits" settings as saved with the step run (the same data the TestRunViewerSqlite tool reads).
Failed steps are highlighted, values outside their limits are marked red when Check Limits is on, columns sort
(numbers numerically) and Ctrl+C copies the selected rows with headers.

Differences from the TUI worth knowing:
- The results viewer also plots text columns whose values are all numbers (e.g. values captured by a
  "Run Program" regular expression, or stored as text by a result listener); the TUI skipped those.
- The package manager asks each repository through its own URL (the TUI's version list always asked
  packages.opentap.io).

Not ported: the TUI's color themes and key-map settings (Windows menus/shortcuts replace them) and the
"focus mode" easter egg.
