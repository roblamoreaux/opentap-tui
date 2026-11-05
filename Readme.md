# OpenTAP TUI (Textual User Interface)

This repository also contains the **[OpenTAP GUI](#opentap-gui-windows)**, a Windows desktop port of the TUI written by Claude (Anthropic's AI assistant).

The OpenTAP TUI is a textual based user interface that can be used from a terminal. It gives you a graphical way to create OpenTAP test plans (`.TapPlan`) and install OpenTAP packages.

It supports running in almost every terminal including in Docker containers.

 

## Install

Either build it from source or install it from the OpenTAP package repository ([packages.opentap.io](https://packages.opentap.io)).

### Install from Repository

- Install: `tap package install TUI`
- Run: `tap tui`
- Run Package Manager: `tap tui-pm`
- Run Results Viewer: `tap tui-results`

### From source

- Build: `dotnet build`
- Run: `OpenTAP.TUI/bin/Debug/tap tui`
- Run Package Manager: `OpenTAP.TUI/bin/Debug/tap tui-pm`
- Run Results Viewer: `OpenTAP.TUI/bin/Debug/tap tui-results`

### Via Docker

- See https://hub.docker.com/r/opentapio/opentap/tags for available OpenTap Docker images, the following example uses `9.15-ubuntu18.04`.
- Run: `docker pull opentapio/opentap:9.18.3-bionic`
- Run: `docker run --name mytui -it opentapio/opentap:9.18.3-bionic`
- Run: `tap package install TUI`
- Run: `tap tui`

## Documentation

More documentation is available [here](https://stefanholst.github.io/opentap-tui/).

## Known Issues

### Running Linux builds in Windows terminals causes graphical glitches

E.g. when running in an SSH session, or a linux build on WSL. Launching the TUI with mono instead of dotnet, seems to work:

`mono OpenTAP.TUI/bin/Debug/tap.dll tui`

## OpenTAP GUI (Windows)

The `OpenTAP.GUI` folder contains a Windows Forms port of this TUI, made by **Claude** (Anthropic's AI
assistant). Like the TUI it is an OpenTAP plugin, so it runs inside `tap.exe` with every installed plugin,
instrument and setting available, but with a standard Windows interface: menu bar, toolbar, status bar,
dialogs, drag and drop and resizable panes.

- Run: `tap gui [plan.TapPlan]`
- Run Package Manager: `tap gui-pm` (also in the GUI's Tools menu)
- Run Results Viewer: `tap gui-results` (also in the GUI's Tools menu)
- Build: `dotnet build OpenTAP.GUI/OpenTap.Gui.csproj` (a Release build also creates `GUI.<version>.TapPackage`,
  installable with `tap package install`)
- Requires OpenTAP 9.33 or later on Windows.

Besides everything the TUI does (test plan editing and running, step and bench settings, profiles, user input
dialogs, package manager, results viewer with plots), the GUI adds:

- A test plan view with step numbers, alternating row shading, the running step highlighted and followed, and
  selectable, rearrangeable columns (Duration, Verdict, Type, Progress, Outputs, or any step setting).
- Breakpoints (F9) that pause a run before a step, with Continue.
- A log panel with level check boxes, source filter, text filter, find and auto scroll.
- Step results with their limits and verdicts in the results viewer.
- Recent test plans, Jump to Source for settings fed by another step's output, and remembered layout.
- Hosting of OpenTAP editor panel plugins (Operator Panel, Live Results, Documentation, ...) docked next to
  Settings or floating.

New users: start with the [User Manual & Tutorial](doc/gui/Manual.md). See [OpenTAP.GUI/README.md](OpenTAP.GUI/README.md)
for technical details, including how each TUI part maps to the GUI.

## Maintainers

- @StefanHolst - stefan.holst@keysight.com
- @frederikja163 - frederik.andreasen@keysight.com
- @rmadsen-ks - rolf\_madsen@keysight.com
- @alnlarsen - alexander.larsen@keysight.com
