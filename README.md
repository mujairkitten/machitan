# Machitan — Windows 11 style taskbar redesign

A custom Windows taskbar written in **WPF** (.NET 8). It runs **alongside**
Explorer — zero risk to your system, nothing is replaced.

Redesigned to match the Windows 11 shellbar look: floating acrylic pills,
rounded corners, Fluent sheen, and three taskbar modes.

## What it does

- **Three taskbar modes** (set in Settings):
  - **Dock**: one centered floating pill with Start + Search + tasks + tray
  - **Segmented**: three separate pills across the width (Start/Search left,
    tasks center, weather/clock/tray right)
  - **Classic**: one full-width bar spanning the screen
- **Start menu**: avatar + search header, **Pinned** grid on the left,
  **Recently used** and **All apps** on the right, theme-aware light/dark
  translucency. Click Start to toggle it.
- **Taskbar** (Windows 11 style): Start button and running apps as icons-only
  buttons, refreshed every 2 seconds. Active window gets an accent underline;
  drag to reorder. Click to bring a window forward (restores if minimized).
- **Weather pill**: temperature and condition on the right side of the bar.
- **Clock + date**: date and time in the right segment.
- **Quick settings & notification center**: Win+A and Win+N shortcuts from the
  tray buttons.
- **Explorer taskbar takeover with a safety net**: while Machitan runs, the
  real Windows taskbar is hidden; it comes back on exit. A hidden *guardian*
  copy watches the main process and restores the real taskbar even if Machitan
  is hard-killed.

## Run it

1. Unzip this folder anywhere on your Windows PC.
2. Open `Machitan.csproj` in Visual Studio 2026
   (you need the **.NET desktop development** workload).
3. Press **F5**.

That's it. No installer, no registry changes, no admin needed.

## Project tour (file by file)

| File | What it is |
|---|---|
| `Machitan.csproj` | Project file: .NET 8, WPF enabled. |
| `App.xaml` / `App.xaml.cs` | App entry point, single-instance mutex, shared Win11 styles. |
| `MainWindow.xaml` | The bar's layout: three segments (Start/Search, tasks, tray) arranged by taskbar mode. |
| `MainWindow.xaml.cs` | Bar behavior: three taskbar modes, clock, weather, task list, appbar registration, settings. |
| `Native/NativeMethods.cs` | All raw Win32 declarations (`DllImport`) in one place. |
| `Native/AppBar.cs` | Registers the window as an appbar (`SHAppBarMessage`). |
| `Native/FluentBackdrop.cs` | Fluent Design backdrop acrylic (DWM API + legacy fallback). |
| `Native/GuardianMode.cs` | Watchdog: restores Explorer taskbar if Machitan dies. |
| `Models/MachitanSettings.cs` | Settings model: theme, opacity, size, dock edge/alignment, taskbar mode. |
| `Models/MachitanSettingsStore.cs` | Saves/loads settings as JSON in `%AppData%\Machitan\settings.json`. |
| `Models/TaskWindowItem.cs` | One task button: window handle + title + icon + sizing. |
| `SettingsWindow.xaml` / `SettingsWindow.xaml.cs` | Settings UI: theme, taskbar mode, transparency, dock edge/alignment, sizing. |
| `StartMenu/StartMenuWindow.xaml` | Start menu look: search box, pinned grid, all-apps list. |
| `StartMenu/StartMenuWindow.xaml.cs` | Start menu behavior: toggle, search, pin/unpin, launching. |

## Customizing

Open Settings (gear button on the bar) to change:

- **Theme**: Light or Dark
- **Taskbar mode**: Dock, Segmented, or Classic
- **Transparency**: opacity of the bar background
- **Dock edge**: Top or Bottom of screen
- **Dock alignment**: Left, Center, or Right (Dock mode only)
- **Bar size** and **App icon size**: sliders

Pins are stored in `%AppData%\Machitan\pinned.json`.

## Known limitations

- Assumes 100% display scaling for the docked position. On 125%/150% the
  bar still shows, but the reserved strip can be slightly off.
- Weather is a placeholder reading (72°F Sunny) — a real provider can be
  wired into `UpdateWeather()`.
- No multi-monitor support yet; docks to the primary screen.
- No system tray icon hosting yet (deferred to a later phase).

## What's next

- Real weather provider integration.
- System tray icon hosting.
- Multi-monitor support.
