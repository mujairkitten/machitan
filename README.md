# Machitan

An alternative modern shell for Windows 11

[![Latest release](https://img.shields.io/github/v/release/mujairkitten/machitan)](https://github.com/mujairkitten/machitan/releases/latest)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![Windows](https://img.shields.io/badge/Windows-11-blue)](https://github.com/mujairkitten/machitan/releases/latest)
[![Ei, Ei, Mun!](https://img.shields.io/badge/Ei,_Ei,-Mun!-blue?labelColor=%23ee5b56&color=%230f8dce)](https://umamusu.wiki/Matikanetannhauser)

<!-- TODO: hero screenshot. Capture the desktop with the bar, the info line and the ring visible. Save it as docs/images/desktop.png and uncomment the line below. -->
<!-- ![Machitan desktop](docs/images/desktop.png) -->

> **Read this first**:
> - Machitan replaces Explorer's taskbar while it runs. The system's taskbar comes back when Machitan exits, and a small guardian process restores it if Machitan crashes or is killed.
> - The installers are not signed, so Windows SmartScreen will warn on first run.
> - This is a beta. Daily drivable but expect some missing things for now.

## Install

Download the latest release from the [releases page](https://github.com/mujairkitten/machitan/releases/latest). It currently runs on Windows 11 26H2, x86-64 (see explanation at ... section).

| Installer | Size | What it brings |
| --- | --- | --- |
| `Machitan-Setup-v.x.y-Full.exe` | ~47 MB | Full package, including .NET 10 runtime |
| `Machitan-Setup-v.x.y-Trimmed.exe` | ~22 MB | Without .NET 10 runtime. This runtime is mandatory to ensure the Machitan's running perfectly. <!-- Symboli Rudolf approved this pun --> If not installed, you will redirected to runtime's download website. |

Both install for all users in `C:\Program Files\Machitan`, ask for administrator rights once, add Machitan to the Start menu, close a running Machitan cleanly first, and can start it at the end.

To uninstall, use Windows Settings, Apps. Uninstalling also removes Machitan's Run at startup entry and the `Machitan\AdminKeys` task. Your settings in `%AppData%\Machitan` stay.

## What it does

### Bar

The bar (a.k.a. 'taskbar') is a see-through pill at the bottom of the screen. The Start cluster and the app dock are separate pills, or joined into one, or stretched full width. Pinned apps sit on the bar. A Show Desktop strip sits at the bar's end. Auto-hide is on by default.

Drag Start, an app bubble, the search icon or any app to rearrange the bar. Ctrl+click apps, or right-click and choose Group apps, to make a new bubble. Right-click the bar and choose Customize to pick, remove and change the bar's bubbles, Dynaland and the corner widgets.

The tray can sit on the bar as its own bubble (Settings, Taskbar, Tray on the bar). Its icons live in a box behind a ^ button, as on Windows' taskbar, and any of them can be dragged out onto the bar. Tray icons drag to a new order, on the bar and in Dynaland.

The accent colour is taken from the wallpaper.

### Start menu

Machitan has its own Start menu. Search covers apps with `apps:` and files with `files:`. Pinned and All apps are separate lists. Each app has a "..." menu with pin to Start or the taskbar, run as administrator, app settings, uninstall, and the app's own jump list tasks. A found file shows a preview with its action icons below the picture. Folder buttons, and one for Machitan's settings, sit in the order you drag them to in Settings. The power menu offers sleep, hibernate, shut down, restart, and update and shut down or restart while an update waits.

### Dynaland

Dynaland is a small pill at the top or bottom centre, opposite the bar by default. It comes in a notch or island shape. It grows only for live things: music in the colour of its cover, a volume change, camera or microphone use with green and orange dots.

Point at it to open it. The player comes first while music plays. The general page shows who uses the camera and microphone, as Windows' audio sessions and camera monitor report it, the apps' tray icons, received straight from the apps, with clicks, double-clicks and their menus, and a button for Windows' notification centre.

The strip kept at the free edge can have a colour cover, like a menu bar.

### Corner widgets

An info line and a status ring sit in the corners, side by side when they share one, beside the bar on its edge. Both stay clear of maximised windows.

The info line shows date, time and weather from Open-Meteo, for Windows' location or a place you choose. A click opens Windows' notification centre. The status ring shows the battery rim, Wi-Fi and volume dots, in colours that follow the wallpaper. A click opens Windows' quick settings.

### Shortcuts

A lone Win key opens Machitan's Start. Win+1 to Win+9 and Win+0 open the apps in the bar's order. Win+N opens the calendar. Win+A opens volume and brightness. Win+` opens the window switcher. Settings, Shortcuts shows each shortcut's state.

Shortcuts in apps running as administrator is off by default. Turning it on makes a Windows scheduled task, `Machitan\AdminKeys`, which starts `Machitan.exe --adminkeys` as administrator. Windows asks once. Turning it off removes the task.

### Hover previews

Hovering a running app on the bar shows live previews of its windows, or a list of them (Settings, Taskbar, Show previews as). Windows that carry an app ID of their own, like Firefox's private windows, get their own button, as on Windows' taskbar. Windows' own Win+X menu opens next to Machitan's Start button.

### Search on the bar

The bar's search icon opens Windows' own search. The search box lets you type right on the bar, with results next to it.

### Languages

English and Bahasa Indonesia supported for now (Settings, General, Language). See `TRANSLATING.md` for translating it to your language.

## Screenshots

<!-- TODO: capture these on the test PC and save them under docs/images/, then uncomment each line. Keep the files under 1 MB each. -->
<!-- ![Bar](docs/images/bar.png) -->
<!-- ![Start menu](docs/images/start-menu.png) -->
<!-- ![Dynaland playing music](docs/images/dynaland.png) -->
<!-- ![Corner widgets](docs/images/corner-widgets.png) -->
<!-- ![Settings](docs/images/settings.png) -->

## Tested on

| Machine | Windows | GPU | Result |
| --- | --- | --- | --- |
| Intel i7-13620H, 1920x1200 | Windows 11 26H2, build 26300.10066 | Intel UHD | Runs. Acrylic renders as solid dark, see Known limitations. |
| <!-- TODO: second machine --> | <!-- TODO --> | <!-- TODO --> | <!-- TODO --> |
| <!-- TODO: Windows 10 machine --> | <!-- TODO --> | <!-- TODO --> | <!-- TODO --> |

Add a row for each machine the release is tried on. One row per machine, with the exact Windows build.

## Known limitations

- Acrylic can render as solid dark on some PCs. The cause is in Windows or the graphics driver on those machines. Windows Terminal's acrylic is black on the same PC. The bar still works. `Machitan.exe --proof` opens a test window for this.
- The bar runs on the primary display. Per-monitor bars are not done yet.
- There is no notification list. Win+N opens the calendar, and the button in Dynaland opens Windows' notification centre.

## Build from source

1. Install Visual Studio 2026 with the **WinUI application development** workload and the .NET 10 SDK.
2. Open `Machitan.slnx`.
3. Pick `Debug` or `Release`, platform `x64`.
4. Build, then run (F5).

If a build complains about a symbol that no longer exists, delete `bin/`, `obj/` and `.vs/`, then rebuild.

## Build the installer

`build-installer.cmd` (double-click it) makes two installers in `publish\`, to run on any Windows 10 (2004 or later) or 11 PC, x64. It publishes a Release build per edition to `publish\Machitan-Full` and `publish\Machitan-Trimmed`, then compiles `installer\Machitan.iss` for each. The folder opens when it is done.

1. Once: install Inno Setup 7 (`winget install --id JRSoftware.InnoSetup.7 -e`, or from jrsoftware.org). Inno Setup 6.3 or later works too.
2. Run `build-installer.cmd`.

The version is `<Version>` in `Machitan.csproj` (Windows stamps it with four numbers, v.x.y.z; the installer's name and Windows' Apps list say v.x.y). Both installers bring the parts of the Windows App SDK Machitan uses (WinUI 3), not its AI parts.

## Files it keeps

All in `%AppData%\Machitan`:

| File | What it is |
| --- | --- |
| `settings.json` | Your settings. Edits are picked up while Machitan runs. |
| `pins.json` | Apps pinned to the bar (created on the first pin). |
| `start-pins.json` | Apps pinned to Start (first a copy of `pins.json`, which held both before). |
| `bar-layout.json` | The bar's bubbles and their order, the tray icons' order, and which tray icons sit on the bar outside the tray box. |
| `diagnostics.log` | What happened, for troubleshooting. It can contain app names and file paths. |

## Stack

- .NET 10 (`net10.0-windows10.0.19041.0`), C#, WinUI 3
- Windows App SDK 2.7.0 (its WinUI 3 part only, not the AI parts)
- System.Management (Windows' WMI, for the laptop screen's brightness)
- x64, unpackaged, self-contained
- WinUI 3 and native Win32 interop only. No WebView, no web tech.

## Status

Beta public test. See `CHANGELOG.md` for what each batch built and verified.

## License

GNU General Public License, version 3. See `LICENSE`. Others' work Machitan includes, and its licence, is listed in `THIRD-PARTY-NOTICES.md`.
