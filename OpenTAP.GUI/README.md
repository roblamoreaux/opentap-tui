# OpenTAP GUI (Windows Forms port of the TUI)

Ported from the OpenTAP TUI by **Claude** (Anthropic's AI assistant).

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

For a user guide with a step-by-step tutorial, see the [User Manual & Tutorial](../doc/gui/Manual.md).

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

New in the GUI (not in the TUI):

- **Test plan view** (`TestPlanGrid`): step numbers (position in the whole plan, so they don't change when groups
  are collapsed), alternating row shading, the running step highlighted and kept in view ("Follow running step"),
  and a **Columns** drop-down for Duration and Verdict (shown by default, live while running), Type, Progress
  (share of child steps completed, or running time compared with the last run) and Outputs. Right-click any
  setting of a step in the Settings pane and choose **Show as Column in Test Plan** to add it as a column.
  Column choices are remembered (GUI Settings). Live run information comes from `GuiRunTracker`, a hidden result
  listener added to runs started from the GUI.
- **Editor panels** (`Panels.cs`): View → Panels lists every panel plugin (anything implementing Keysight's
  `ITapDockPanel` from the "WPF Controls" package: Operator Panel, Live Results, Documentation, AI Assist, ...). The Keysight Editor's own window parts (Test Plan, Test Steps, Test Step Settings, Test Plan Settings, Log, Welcome) are left out: they only work inside the Editor and the GUI has its own versions.
  A panel opens as a tab next to Settings; double-click or right-click its tab to float it in its own window,
  and use "Dock next to Settings" to put it back. Open panels and their docked/floating state come back on the
  next start. The GUI gives panels the current plan, selection, settings and Run, includes result listeners they
  add (so Live Results updates during runs), and loads the Keysight theme resources panels expect. A panel that
  fails to load is closed with a message instead of taking the GUI down. Tested with Operator Panel,
  Documentation and Live Results 2 on OpenTAP 9.35.1; without "WPF Controls" the menu just says no panels are
  installed. `Keysight.OpenTap.Wpf.dll` is referenced from the OpenTAP installation, not shipped
  (`WpfControlsPath` in the project file).
- **Breakpoints**: F9, Run → Toggle Breakpoint or right-click a step. A red dot next to the step number marks
  it. The run pauses just before the step starts (row shown in orange, status bar says where); F5 / ▶ Continue
  resumes, Abort stops. Uses OpenTAP's `TestPlan.BreakOffered`. Breakpoints are kept for the session (by step
  ID), not saved in the plan file.
- **File → Recent Test Plans**: the last 5 plans opened or saved.
- The Settings pane width and the test plan column order (drag column headers) are remembered (GUI Settings).
- **Jump to Source**: right-click a setting whose value comes from another step's output (Assign Output, or an
  input such as the If step's) to select that step and its output.
- **Log panel** (`LogPanel`): Error / Warning / Information / Debug check boxes, a Sources drop-down, a Filter box
  (only show matching lines), a Find box with ▲/▼ (highlights matches; finding turns Auto scroll off so the match
  stays in view) and an Auto scroll check box. Also used in the package manager.
- The results viewer's **Step Results** grid (`StepResultsGrid`). Select one or
more runs to list every step with its verdict, result values, Min/Max limits and Check Limits. The limits come
from each step's "Limits" settings as saved with the step run (the same data the TestRunViewerSqlite tool reads).
Failed steps are highlighted, values outside their limits are marked red when Check Limits is on, columns sort
(numbers numerically) and Ctrl+C copies the selected rows with headers.

- The results viewer's **Database** drop-down chooses where runs are read from: all result stores in Result
  Settings, one of them (for example PostgreSQL), or a `.TapResults` file picked with an Open dialog (read through
  a SQLite result store from the "SQLite and PostgreSQL" package; the last 5 files are listed). The choice is
  remembered (GUI Settings). Errors a store logs while reading (such as a failed PostgreSQL login) are shown in
  the status bar, since result stores report them in the log rather than throwing.
- The package manager highlights installed packages that have a newer release (not pre-release) available for
  this OS and architecture, like the "update available" note of `tap package list`, and shows the count above the
  list. The check runs in the background after the list loads (one query per installed package).

Differences from the TUI worth knowing:
- The results viewer also plots text columns whose values are all numbers (e.g. values captured by a
  "Run Program" regular expression, or stored as text by a result listener); the TUI skipped those.
- The package manager asks each repository through its own URL (the TUI's version list always asked
  packages.opentap.io).

Not ported: the TUI's color themes and key-map settings (Windows menus/shortcuts replace them) and the
"focus mode" easter egg.
