# Machitan

A new taskbar for Windows 11. It looks and feels like part of Windows, not an overlay.

Machitan is **early software**. It is already usable day to day, but some parts are still missing (see [Known limits](#known-limits)).

Built with **WinUI 3** on **.NET 8**. Free and open source under the **GPL-3.0** license.

## What it does

- **Four taskbar modes** (change them in Settings):
  - **Default**: three floating pills. Widgets on the left, Start and apps in the center, tray area on the right.
  - **Joined**: one floating pill.
  - **Classic**: one full-width bar, edge to edge.
  - **Compact**: like Joined, but smaller (0.8× height, smaller icons).
- **Smart sizing**: the bar grows as you open apps. When it gets crowded, icons shrink instead of spilling out.
- **Start menu**: your name and search at the top, a pinned grid, recently used apps, and an All apps list. Power options and Machitan settings sit at the bottom.
- **Running apps**: windows are grouped per app. The active app gets an accent underline. Click to open or minimize. Right-click for more options (pick a window, close all, pin or unpin).
- **Real weather**: shown only when real data is available (from Open-Meteo, at your device location). If there is no data, you see just the icon. You can turn it off in Settings.
- **Battery**: shown only on devices that have one.
- **Windows panels**: the clock opens the notification center, and the quick settings area opens quick settings. Task View works too.
- **Looks**: light or dark theme, acrylic blur with an opacity slider, or a solid color.
- **Safe by design**: while Machitan runs, the Explorer taskbar is hidden. A small helper process watches Machitan and brings the Explorer taskbar back if Machitan closes or crashes.

## Run it

You need Windows 11 and Visual Studio 2026 with the **.NET desktop development** workload.

1. Clone or download this repo.
2. Open `Machitan.slnx` (or `Machitan.csproj`) in Visual Studio.
3. Press **F5**.

No installer. No admin rights. No registry changes.

**If the Windows taskbar ever stays hidden:** press `Ctrl + Shift + Esc`, choose *Run new task*, and run `explorer.exe`.

## Settings

Open Settings from the bar's right-click menu or the Start menu footer.

| Setting | Choices |
|---|---|
| Theme | Light or Dark |
| Taskbar mode | Default, Joined, Classic, Compact |
| Acrylic effect | On or off |
| Transparency | Slider |
| Weather in the bar | On or off |
| Dock edge | Top or Bottom |
| Dock alignment | Left, Center, Right |
| Bar size, App icon size | Sliders |

## Where Machitan saves things

| What | Where |
|---|---|
| Settings | `%AppData%\Machitan\settings.json` |
| Pinned apps | `%AppData%\Machitan\pinned.json` |
| Recently used | `%AppData%\Machitan\recent.json` |
| App order on the bar | `%AppData%\Machitan\task-order.json` |
| Debug log | `%TEMP%\Machitan.log` |

## Known limits

- **No system tray icons yet.** Icons from other apps are not shown. This is the hardest part and is being studied first.
- **Primary screen only.** Machitan draws one bar. Other monitors keep the Windows taskbar.
- **Scaling.** Spacing can look slightly off at some display scaling levels (125%, 150%). This is being worked on.
- **No installer.** Run it from Visual Studio for now.

## Project tour

| File or folder | What it is |
|---|---|
| `Program.cs` | Starts the app. One copy at a time. Launches the helper that restores the Explorer taskbar. |
| `App.xaml` / `App.xaml.cs` | App startup and shared styles. |
| `MainWindow.xaml` / `.cs` | The bar: modes, clock, running apps, weather, battery. |
| `SegmentWindow.xaml` / `.cs` | The side pills used by Default mode. |
| `SettingsWindow.xaml` / `.cs` | The Settings window. |
| `StartMenu/` | Start menu window, installed app scan, pinned and recent lists. |
| `Models/` | Settings, saving and loading, task order, one task button. |
| `Native/AppBarController.cs` | Reserves screen space for the bar. |
| `Native/WindowChrome.cs` | Borderless windows with smooth rounded corners. |
| `Native/FluentBackdrop.cs` | Acrylic blur. |
| `Native/ExplorerTaskbarController.cs` | Hides and restores the Explorer taskbar. |
| `Native/GuardianMode.cs` | The helper that restores the Explorer taskbar if Machitan dies. |
| `Native/ShellEvents.cs` | Notices when Explorer restarts. |
| `Native/ShellPanels.cs` | Opens Windows panels (notifications, quick settings, Task View). |
| `Native/WeatherService.cs` | Real weather from Open-Meteo. |
| `Native/BatteryStatus.cs` | Battery level. |
| `Native/IconLoader.cs` | Loads app icons. |
| `Native/NativeMethods.cs` | Windows API declarations in one place. |
| `Native/DiagnosticsLog.cs` | Debug log. |

## Design notes

Machitan follows the Windows 11 Fluent look and takes ideas from concept designs by Addy Visuals and Avdan. It never shows made-up content: no fake meetings, no fake weather. If something is not available, it shows less.

## License

Machitan is licensed under the [GNU General Public License v3.0](LICENSE).
