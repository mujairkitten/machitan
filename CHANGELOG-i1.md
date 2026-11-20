# Changelog - line i1

## Batch 0 - Scaffold + backdrop proof

**Status: builds and runs. Acrylic NOT green on the test PC - see "Outcome".**

### Built

- Project scaffold: `Machitan.csproj` (.NET 10, Windows App SDK 2.6.5, x64, unpackaged, self-contained), `Machitan.slnx`, `app.manifest` (PerMonitorV2), `Properties/launchSettings.json` (unpackaged launch profile), `.gitignore`, `.gitattributes`, `LICENSE` (GPL v3), `README.md`.
- `Program.cs` with our own `Main` (the XAML-generated one is switched off).
- `App.xaml` + `App.xaml.cs`.
- `MainWindow`: proof window with the stock `DesktopAcrylicBackdrop`, plus live status lines (acrylic support, Windows transparency effects, energy saver, focus state, runtime versions).
- `Visuals/PersistentAcrylicBackdrop.cs`: acrylic that stays on when the window is not focused. The stock backdrop goes solid when its window loses focus, and the bar is never the focused window. A switch in the proof window flips between the two.
- `Interop/User32.cs`: `GetDpiForWindow` (window sizing in physical pixels).

### Verified

- Builds and runs on Sam's PC (after installing the .NET 10 SDK, which was missing from the machine).
- First look: both backdrops read as solid; the stock one changes slightly between focused and not focused. Not decisive, so fix 1 below.

### Fix 1

- `PersistentAcrylicBackdrop` gets a `TintStrength` (0 = blur only, 1 = solid) instead of the dense default dark tint.
- Proof window gets a tint slider, so blur can be shown with no tint on top.

### Fix 2

- Result of fix 1 on Sam's PC (Windows build 26300, transparency effects on, energy saver off): at tint 0 the window is pure black. The tint layer draws, but Windows hands the app nothing to blur.
- Proof window now has four tests, each through a different route: stock acrylic, Machitan acrylic, Mica, and a rounded acrylic box (`SystemBackdropElement`).
- New switch that asks Windows for the host backdrop directly (`DwmSetWindowAttribute`, attribute 17), in case the SDK is not asking.
- New file `Interop/Dwmapi.cs`.

### To check

1. Clean build: 0 errors, 0 warnings.
2. Window opens; blur of whatever is behind it is visible (switch off).
3. Click another window: does the blur stay or go solid (switch off)?
4. Turn the switch on, click another window: the blur should stay.

### Left out

- Nothing.

### Notes

- The System Composition Engine (the mode where 2.6.5 does not support system backdrops) is opt-in. Machitan does not opt in, so acrylic is expected to work.
- `Microsoft.Web.WebView2` comes in as a NuGet dependency of WinUI itself. Machitan does not use it.

### Outcome

- All acrylic routes render black on Sam's PC (Windows 26H2 build 26300.10066, Intel UHD on i7-13620H): stock backdrop, Machitan backdrop, forced DWM host backdrop, and `SystemBackdropElement`. Mica works.
- Same result on Windows App SDK 2.5.1, and Windows Terminal's own acrylic is black too. So the fault is in Windows or the graphics driver on this PC, not in Machitan or the SDK.
- Restart, graphics driver reset, HDR check (off, not supported), driver update: no change. No tweak tools installed.
- Decision: keep the acrylic design and SDK 2.6.5, move on. On this PC the bar shows as solid dark. The proof window stays in the build (`Machitan.exe --proof`) so this can be re-tested later.
- Not verified anywhere yet: that the acrylic code shows real blur on a healthy PC.

## Batch 1 - Shell core

**Status: GREEN (verified by Sam, 2026-11-17).**

### Built

- **Start-up order** (`Program.cs`): guardian mode first, then `--exit`, then `--proof`, then the shell: single-instance mutex (`Local\Machitan_SingleInstance`), guardian started, Explorer's taskbar hidden, app run, taskbar restored in a `finally` block.
- **Guardian** (`Core/Guardian.cs`): `Machitan.exe --guardian <pid>` waits for the shell process to end and restores Explorer's taskbar. Covers crash, kill and "Stop debugging".
- **Explorer's taskbar** (`Shell/ExplorerTaskbar.cs`): switched to auto-hide (frees its screen strip) and hidden; original state kept in `explorer-taskbar.state` so any process can restore it. Re-hidden 4 times a second if Explorer shows it again. Also restored on sign-out/shutdown.
- **Bar window** (`Bar/BarWindow.xaml` + `.cs`): borderless, always on top, never takes focus, capsule shape. Pill height = BarSize - 14 DIP, centred in the BarSize strip; 16 DIP side margin. Dock edge and alignment from settings.
- **AppBar reservation** (`Shell/AppBarHost.cs`): `SHAppBarMessage`, only when auto-hide is off. Hides the bar for full-screen apps.
- **Task list** (`Shell/TaskService.cs`, `WindowFilter.cs`, `TaskModels.cs`): full window enumeration, Alt+Tab rules, grouped by app (executable path; store apps resolved through their frame window), refreshed by the shell hook. Click a button: bring the app forward, or minimise it if it is in front.
- **Shell hook** via a subclass of the bar window (`Shell/WindowSubclass.cs`). If registration fails, the 2 s timer polls instead - and only then.
- **Self-heal**: unconditional 2 s timer restores the bar if minimised, hidden or moved, and re-asserts always-on-top.
- **Auto-hide**: unconditional 250 ms timer. Hides 0.8 s after the pointer leaves; returns when the pointer touches the docked edge. Instant for now, no slide.
- **Settings** (`Config/ShellSettings.cs`, `SettingsStore.cs`): spec defaults, clamps, atomic writes with `.bak`, enums written as text and read as text or number, unknown keys preserved, unreadable file copied aside and never wiped. The file is watched, so hand edits apply live.
- **Pin store** (`Shell/PinStore.cs`): `pins.json`; refuses to save for the rest of the run if the file could not be read.
- **Diagnostics log** (`Core/Log.cs`): `diagnostics.log`, 1 MB rotation, never throws. Unhandled errors are logged and the shell keeps running.
- **Exit**: right-click the bar > Exit Machitan, or `Machitan.exe --exit`.
- Interop: `User32`, `Shell32`, `Kernel32`, `Gdi32`, `Comctl32`, `Dwmapi`, `WindowInfo`, `NativeTypes`. No `SHGetFileInfo` anywhere.

### How I read two spec points (say so if wrong)

- "visual pill = BarSize - 14 DIP, 16 DIP edge margin": the reserved strip is BarSize thick, the pill is 14 DIP thinner and centred in it, and 16 DIP is the smallest gap between the pill and the left/right screen edges.
- With auto-hide on (the default), nothing is reserved, like Windows' own auto-hide taskbar.

### Verified (Sam's PC)

- Clean build: 0 errors, 0 warnings.
- Launch: Explorer's taskbar hidden, bar shows open apps; buttons follow windows opening and closing.
- Click brings an app forward; a second click minimises it.
- Auto-hide hides and returns at the bottom edge.
- Auto-hide off: AppBar registered, maximised windows stop above the bar.
- Win+D with auto-hide off: bar present afterwards.
- Right-click menu opens, closes on an outside click, and exits cleanly; Explorer's taskbar returns.
- "Stop debugging": the guardian restores the taskbar.
- Live settings edit applies while running.
- `diagnostics.log`: no warnings or errors; shell hook registered on every run.
- Not checked: second start (single-instance), because the running copy was under Visual Studio.

### Checklist used

1. Clean build: 0 errors, 0 warnings.
2. Launch: Explorer's taskbar gone, Machitan bar at bottom centre with one button per open app.
3. Click a button: app comes forward; click again: it minimises. Open and close apps: buttons follow at once.
4. Auto-hide: bar hides when the pointer leaves, returns at the bottom edge.
5. In `%AppData%\Machitan\settings.json` set `"AutoHideBar": false` and save: bar stays, maximised windows stop above it. Try `"DockAlignment": "Left"` and `"DockEdge": "Top"`.
6. Minimise repro with auto-hide off: Win+D - bar back within about 2 s.
7. Exit by right-click > Exit Machitan: Explorer's taskbar returns. Start again, then "Stop debugging" in Visual Studio: taskbar returns within a second or two (guardian).
8. Start Machitan twice: the second start does nothing.
9. `diagnostics.log`: no ERROR lines; one line says "Shell hook registered".

### Left out (on purpose, for later batches)

- Icons, running/focus indicators, segments, modes other than a single pill, search, Show Desktop: Batch 2.
- Slide animation for auto-hide: Batch 2.
- True see-through around the pill: the window is clipped to a capsule with a window region, so edges are not anti-aliased yet. Batch 2 starts with a transparency test.
- Pins have no UI yet (Batch 3). The store is there and logs what it loads.

## Batch 2 - Bar per the spec

**Status: GREEN (2026-11-17).** Left out on purpose: Compact mode (optional per the plan); pinning store apps from the bar (comes with the Start menu's app list, Batch 3).

Delivered in two parts, so each part gets its own build check.

### Part 1 - icons, indicators, see-through test

**Status: GREEN (verified by Sam, 2026-11-17).** Clean build 0/0; icons sharp and centred with no tile; indicators correct. Store apps such as Calculator showed a letter (fixed in part 2).

Built:

- **Icon buttons** (`Bar/BarWindow.xaml.cs`): bare icon at the exact centre of the button, padding zeroed, no tile behind it; background only on hover or keyboard focus. Icon size from `IconSize` (24).
- **Running indicator**: 3 DIP tall, rounded ends, placed from the centre (top = centre + half the icon + 4). 14 wide in the Windows accent colour for the focused app, 6 wide and translucent for running apps. Buttons are updated in place when the app list is unchanged.
- **Icon pipeline** (`Shell/IconService.cs`, `Interop/IconReader.cs`): executable icon at the needed pixel size first (sharpest), then the window's own icon, then its class icon. GDI only; no `SHGetFileInfo`, no new packages. Successes cached; a miss is retried at most every 5 s and never kept.
- **No icon available**: the button shows the app's first letter. Nothing is invented.
- **See-through test** (`Machitan.exe --seethrough`; `SeeThroughTestWindow`, `Visuals/TransparentBackdrop.cs`): decides how segments and Start menu boxes get real gaps and smooth edges.

Changes from the handoff's wording, on purpose:

- Icon order: executable icon first, because window icons are usually 32 px and look soft above 100% scale.
- Cache holds ready pixel data in memory, not PNG bytes. Same goal (decode once), no encoder round trip. PNG only matters for a disk cache, which does not exist.

Known gap:

- Store apps (Settings, Calculator, ...) often have no icon in their executable and none on their window, so they show a letter for now. Their real logos live in the app package; reading those is planned for part 2.

To check:

1. Clean build: 0 errors, 0 warnings.
2. Bar: icons instead of text, sharp, centred; no tile behind them; hover shows a background.
3. Indicator: wide accent line under the focused app, short faint line under the others, a small gap below the icon.
4. Which apps show a letter instead of an icon?
5. `Machitan.exe --seethrough` (with Machitan itself closed or running, either is fine): see the questions in chat.

### See-through test, round 1 (2026-11-17)

- Result: black around the shapes with every switch setting (transparent backdrop on/off, layered on/off). Shape edges look smooth against the black.
- Cause, as far as I can tell: nothing told Windows to honour the window's transparent pixels. A layered window with whole-window alpha 255 does not do that for WinUI's content.
- Round 2: new switch B, "Windows alpha" (DWM blur-behind with a region off the window, so nothing is blurred but transparent pixels show through); layered is now switch C and off by default; new switch D cuts the window to the shapes, 2 px wider than each, so gaps pass clicks through while edges stay smooth. The Windows 11 window outline and corner rounding are turned off.

### See-through test, round 2 (2026-11-17) - PASSED

- A (see-through backdrop) + B (Windows alpha) together: see-through around the shapes, smooth edges. Neither alone works; C (layered) makes no difference.
- D (window cut to the shapes, 2 px wider): clicks in the gaps reach what is behind, edges stay smooth. The 2 px rim is invisible only with A + B on; otherwise it shows as a black ring.
- The Windows 11 window outline did not go away through `DWMWA_BORDER_COLOR`, but D cuts it off.
- Recipe adopted for the bar: A + B + D. The test stays available as `--seethrough`.

### Part 2 - segments, Start, Show Desktop, pins, modes

**Status: GREEN (verified by Sam, 2026-11-17), after rounds 1-6 below.**

Built:

- **See-through bar window** with the proven recipe (`Bar/BarWindow.xaml.cs`): gaps between segments show the desktop and pass clicks through.
- **Modes** (`Bar/BarWindow.Layout.cs`): Segmented (Start cluster and App dock as separate pills, 8 DIP gap), Joined (one pill, thin divider), Classic (full width minus the 16 DIP margins, 16 DIP corners, dock placed per alignment). Compact is not built (optional per the plan) and shows as Segmented.
- **Segment fill**: acrylic per segment when on and supported (on Sam's PC it renders dark, the known Windows fault), else a translucent colour at the `Opacity` setting.
- **Start cluster**: Start button (a neutral four-tile glyph drawn in code) and Search (icon by default; 180 DIP box in Box mode; none in Hidden). Until Machitan's Start menu exists, Start opens Windows' Start menu (Ctrl+Esc) and Search opens Windows search (Win+S). The box is read-only for now; typing into it arrives with the Start menu in Batch 3.
- **Show Desktop strip**: 2 DIP line, 38% of the bar height, rounded, at the far right end on the segment's normal padding; button padding zeroed; 12 DIP hit area; no hover tile. Invisible at rest, shown while the bar or strip is hovered, while the desktop is shown, and always on touch devices. Translucent, brighter on hover, accent while the desktop is shown. Click minimises all app windows; the second click restores exactly those, maximised ones maximised, in their old order (`Shell/DesktopToggle.cs`).
- **Pins in the dock** (`Shell/DockModel.cs`, `Bar/BarWindow.Dock.cs`): pinned apps first, then other running apps. Pinned but not running: icon without indicator; click starts it. Right-click: Pin to bar / Unpin from bar, Close window(s).
- **Store-app logos** (`Shell/IconService.cs`): packaged apps get the logo from their package (as Windows' own taskbar does), loaded in the background; exe or window icon meanwhile.
- **Auto-hide slide**: the content slides out of the window with a Composition animation (180 ms), then the window hides; the reverse on reveal. The window is never resized to animate.
- New interop: `SendInput` (`Interop/KeyChord.cs`), `ShowWindowAsync`, `IsWindow`, `PostMessageW`, `GetSystemMetrics`, `GetPackageFullName`.

Left out, and why:

- Store apps cannot be pinned from the bar: they cannot be started from their exe path. They become pinnable from the Start menu's app list in Batch 3.
- Compact mode: optional per the plan; can come later.

To check:

1. Clean build: 0 errors, 0 warnings.
2. Bar: two pills with a see-through gap; clicks in the gap reach the desktop; edges smooth.
3. Start opens Windows' Start menu; Search opens Windows search.
4. Show Desktop: invisible at rest, appears when hovering the bar; click minimises, click again restores (maximised windows come back maximised).
5. Calculator and other store apps: real logo instead of a letter.
6. Pin an app (right-click > Pin to bar), close it: icon stays without indicator; click starts it. Unpin removes it.
7. Auto-hide: slides down and back up.
8. `settings.json`: `"TaskbarMode": "Joined"`, then `"Classic"`, then back to `"Segmented"`; `"SearchMode": "Box"` and `"Hidden"`.
9. `diagnostics.log`: no ERROR lines.

Results, round 1 (Sam, 2026-11-17): clean build 0/0 (after deleting stale `bin`/`obj`); see-through gap works; Start and Search open Windows' own; Show Desktop works; store-app logos show but small; pins work; Joined/Classic/Segmented switch live; auto-hide slide fine with few apps but "looks different" with more than 3 apps (details pending); search-mode change did not apply (no reload in the log for it).

Fixes, round 2:

- **Window outline**: Windows 11 draws a thin outline along the bar window's edge, which ran along the pills' edges. The window now has 4 DIP of empty space on every side, so its edge falls outside the region cut and the outline is cut away.
- **Store-app logos too small**: the general logo Windows hands out has a wide built-in margin. Now the app's "target size" logo file is used (as Windows' own taskbar does), unplated, in the variant for the bar's theme, read from the package's manifest and asset folder. The general logo stays as the fallback.
- **Settings log**: a reload now names every setting it changed ("SearchMode Icon -> Box"), says so when a save changed nothing, and warns about any value it does not understand.

Round 2 results (Sam, 2026-11-17): store-app logos full size; search modes switch live (log: SearchMode Icon -> Box -> Hidden -> Icon). The white lines along the pills were Visual Studio's in-app debugging overlay (gone when started without debugging). A faint grey rounded frame about 8 DIP outside the pills remained.

Fixes, round 3:

- **Frame styles stripped**: the bar is now a plain popup window (no caption, resize edge or border styles), with Windows 11's outline colour set to none and corner rounding off. WinUI keeps some frame styles even with border and title bar switched off, and Windows 11 outlines framed windows.
- **Window cut self-heal**: the first cut is logged ("Bar window cut to N segment(s)"); the 2 s timer re-applies it, with a log line, if anything removed it.

Round 3 results (Sam, 2026-11-17): grey frame gone; right-click in the gap reaches the desktop (cut works); everything else unchanged. Log: "Bar window cut to 2 segment(s)", no warnings. The 5.6 s start-up gap was a first launch after a build (the next start took 0.2 s).

Fix, round 4:

- **Bar hid during screen recording** (auto-hide off, so the AppBar is active): Windows' full-screen signal also fires for full-screen overlays such as a recorder's selection layer. The bar now checks the front window itself, four times a second: it steps aside only for a window that covers the whole screen and is not maximised, not the desktop, not a click-through, no-activate or tool window, not hidden by the system, and not Task View. Windows' signal only triggers an immediate check. Every change is logged with the app's file name and window class.

Round 4 results (Sam, 2026-11-17): auto-hide slide fine with few and many apps (the earlier oddity is gone). The bar still disappears on screen while recording with Snipping Tool, but the log shows no full-screen or self-heal line, so Machitan did not hide it.

Diagnostics, round 5:

- Each window that covers the screen and comes to the front without being treated as full-screen is logged once (app, window class, extended style, state).
- Four times a second, while the bar should be on screen: if it is not visible, minimised, or cloaked by the system, that is logged (with what is in front) and the bar is put back at once.

Round 5 results (Sam, 2026-11-17): the Snipping Tool recording shows the bar sliding out and back in about every 1.8 s (visible ~1.3 s, gone ~0.5 s); the slide frames prove it is Machitan's auto-hide animation (auto-hide was on). The log shows Snipping Tool's overlay (SnippingTool.exe, class XamlWindow, extended style 0x00200188: tool window, topmost) correctly not treated as full-screen, and no visibility problem.

Diagnostics, round 6: every auto-hide slide out and in is logged with the pointer position, the bar rectangle and the screen bottom, to find what makes it cycle.

Round 6 results (Sam, 2026-11-17): Test A (auto-hide on, recording): the log shows every slide-out with the pointer away from the bar and every slide-in with the pointer on the bottom row (y 1199 of 1200); the recording only looked automatic because Snipping Tool does not record the pointer. Auto-hide works as designed. Test B (auto-hide off, recording): the bar stays put; Snipping Tool's overlay is logged and ignored. No further fixes. Batch 2 closed.

## Batch 3 - Start menu + Settings window

Delivered in two parts, like Batch 2.

### Part 1 - Start menu

**Status: GREEN (verified by Sam, 2026-11-17).** Clean build 0/0 (after one fix: an ambiguous `DispatcherQueueTimer`; Visual Studio had also saved its open copy over the first fix). All six checks passed. Log: "App list: 155 apps (130 shortcuts, 25 store apps)" (11 s on the first launch after a build, under 1 s after); second start ignored ("already running"), so single-instance is now verified; no warnings or errors.

Built:

- **Start menu window** (`Start/StartMenuWindow.xaml` + `.cs`): separate floating boxes on a see-through window (the gaps show the wallpaper and pass clicks through), 648 x 400 DIP. Left: search capsule over the Pinned grid. Right: All apps, as tall as the left column; lists scroll inside their boxes. Below: slim user strip. No Recently used, recent items or widgets. Opens above the bar, lined up with its left edge (below it for a top bar). Closes on Esc, on a click elsewhere, and after starting something. The bar stays visible while it is open.
- **Search** focused on open; filters Pinned and All apps by name. Scopes: `apps:` (default) and `files:`; no `web:` scope, deliberately. Enter starts the first result.
- **files:** searches file names in Desktop, Documents, Downloads, Pictures, Music and Videos, 5 levels deep, up to 40 results, 3 s limit, on a worker thread (`Shell/FileSearch.cs`). No index, no web.
- **All apps** (`Shell/AppCatalog.cs`): Start menu shortcuts (all users and current user) plus store apps that show in Windows' own app list, read on a worker thread, kept for a minute. Shortcut icons from the shortcut's icon location, else its target (`Interop/ShellLink.cs`, `IShellLinkW.GetPath` called with `IntPtr.Zero`); store logos from the package's target-size files (`Shell/PackageLogo.cs`).
- **Pin buttons** on All apps rows and **unpin buttons** on Pinned tiles: hover only (and while keyboard-focused), always visible on touch devices.
- **Pins**: one list for the bar's dock and Start's Pinned box. Store apps can now be pinned (by app ID, from All apps); shortcuts are pinned by the exe they start (so they line up with the running app on the bar) and started through the shortcut (keeping its arguments). `pins.json` gains optional `AppId` and `Launch` fields; older files still load.
- **User strip**: account picture from Windows (initials when none is set), display name, power button. Power: Restart / Shut down, each with an inline "now?" confirmation before anything happens (`shutdown.exe`).
- **Bar**: Start opens and closes Machitan's Start; Search (icon or box) opens it with the search box focused. Windows' own Start and search are now only the fallback if the Start menu cannot be created.
- **Explorer's taskbar** is hidden only once Machitan's bar is on screen (still restored on exit, on sign-out, and by the guardian).
- Shared helpers: `Visuals/SeeThrough.cs` (the see-through recipe), `Visuals/WindowCutter.cs`, `Visuals/SurfaceFill.cs`, `Shell/Launcher.cs`, `Shell/UserInfo.cs`.
- Radii: 16 boxes, 10 rows and tiles, 8 small buttons, capsule search box.

Choice made (say if you want it different):

- One pin list for the bar and Start (Windows keeps two). Pinning in Start also adds the app to the bar.

To check:

1. Clean build: 0 errors, 0 warnings.
2. Start button: menu opens above the bar; boxes with see-through gaps; search box focused; Esc and an outside click close it.
3. All apps: your apps with real icons (shortcuts and store apps); scrolls inside its box.
4. Search: typing filters both lists; `files:` plus a name finds your files; Enter starts the first result.
5. Pin from All apps (hover a row, pin button), including a store app such as Calculator: it appears in Pinned and on the bar. Unpin from the tile's hover button.
6. User strip: your picture (or initials) and name. Power: Restart or Shut down asks "now?" first; Cancel and Esc back out. (No need to confirm for real.)
7. Search icon on the bar, and Box mode, open Start with the search box focused.
8. `diagnostics.log`: an "App list: N apps" line, no ERROR lines.

### Part 2 - Settings window

**Status: GREEN (Sam, 2026-11-17).** All six checks pass, after the fixes in rounds 1-4 below (a crash from acrylic fills, Start's position per alignment, the Start button toggle).

Built:

- **Settings window** (`SettingsUi/SettingsWindow.xaml` + `.cs`), opened from the bar's right-click menu ("Settings"); one window at a time. Mica backdrop (works on this PC). Every change applies at once; sliders apply 250 ms after the last move.
- **Sections**: Bar (mode, position, alignment, bar size, icon size, auto-hide, search: icon/box/hidden), Look (theme, acrylic, opacity), Corner widgets (info line on/off; date, time, weather; corner; status ring on/off; corner), Dynaland (on/off), Other options from the plan (system stats, focused app name, recent items: shown off and disabled), Privacy, System status, About.
- **Honest labels**: corner widgets and Dynaland are marked "not built yet"; their choices are saved and apply when they arrive. The three unbuilt options cannot be switched on.
- **Privacy notes**: weather sends the approximate location to Open-Meteo (once weather exists); the diagnostics log can contain app names, file paths and window titles, and stays on the PC. A button opens Machitan's folder.
- **System status** line: says plainly that the Windows-key hook and Win+N / Win+A / Win+Tab are not Machitan's yet (Batch 4).
- **Accessible names** on every control; section titles are marked as headings.
- **Settings store**: new keys `InfoLineEnabled`, `InfoLineShowDate`, `InfoLineShowTime`, `InfoLineShowWeather`, `InfoLineCorner`, `DuoRingEnabled`, `DuoRingCorner` (defaults per the spec). Changes from the Settings window apply, save and notify at once, and are logged by name; Machitan does not re-read its own write. Hand edits to settings.json still apply live, and the open Settings window follows them.
- Tidy: the desktop no longer appears in the log as a "window covering the screen"; the Explorer-hidden log line no longer reads like a failure when the taskbar was already out of sight.

To check:

1. Clean build: 0 errors, 0 warnings.
2. Right-click the bar > Settings: the window opens; a second Settings click brings the same window forward.
3. Change mode, position, alignment, search, theme: the bar (and Start) follow at once. Drag bar size, icon size, opacity: applied after you let go.
4. Toggle auto-hide: the bar follows.
5. Corner widgets and Dynaland sections say "not built yet"; the three plan options are off and greyed.
6. Edit settings.json by hand while Settings is open: the controls follow.
7. "Open Machitan's folder" opens %AppData%\Machitan.
8. `diagnostics.log`: each change logged by name, no ERROR lines.

Part 2 round 1 (Sam, 2026-11-17): the process ended twice with code 0xc0000409 (a fail-fast that bypasses Machitan's handlers), each time about 12 s after the last logged change (mode Joined -> Classic -> Compact -> Segmented, position Top -> Bottom; all applied fine). Cause not known yet.

Diagnostics: every exception is now logged at the moment it is thrown (first-chance), with the call stack, capped at 150 per run (`Core/ExceptionTrail.cs`), so a crash that skips the handlers still leaves its trail.

Event Viewer (both runs, Release and Debug): faulting module `Microsoft.UI.Input.dll` 12.6.5.0 (Windows App SDK input layer), code 0xc0000409, same offset 0xacece. So the stop happens inside the Windows App SDK, not in Machitan's code; what Machitan did to trigger it is not known yet.

More diagnostics: the log now notes when the Settings window opens and closes, when Start opens and closes, and when the bar menu opens, so the last step before a crash shows. `Properties/launchSettings.json` turns on native ("mixed-mode") debugging, so Visual Studio shows the Windows App SDK's own failure message and call stack when it stops.

Part 2 round 2 (Sam, 2026-11-17, native debugging on): Visual Studio showed the cause. `Microsoft.UI.Input.dll`, `ApiCommon.h(149)`: FailFast 8001010E "The application called an interface that was marshalled for a different thread", right after "The caller must call Close / Dispose from the owning thread", on a thread other than the UI thread. The log shows the Settings window was still open; the stop came about 19 s after the last change, with nothing being done.

Cause: every settings change rebuilds the bar (and refilled Start's boxes), and each acrylic fill is a `SystemBackdropElement` with Machitan's own `PersistentAcrylicBackdrop`. The old fills were simply dropped. The objects their backdrops had received (the link that attaches a backdrop to one element, `ContentExternalBackdropLink`, which lives in Microsoft.UI.Input.dll) were then released by .NET's clean-up (finalizer) thread at the next garbage collection. Those objects may only be released on the UI thread, so the Windows App SDK stopped the app. Acrylic is on in this setup, so every change left a few behind.

Fix:
- `Visuals/BackdropKeeper.cs` (new): every target, controller, configuration and brush a backdrop touches stays referenced until Machitan exits, so .NET never releases one on the wrong thread (.NET does not run that clean-up at exit). A few small objects per replaced fill.
- `SurfaceFill.Release`: an acrylic fill is detached on the UI thread before it is dropped, so its controller is closed there. The bar now uses `SurfaceFill` too (same look as before), and detaches its old fills before every rebuild.
- Start replaces its box fills only when their look changes (acrylic, opacity, theme), not on every settings change.
- Both backdrops override `OnDefaultSystemBackdropConfigurationChanged` with nothing: they keep their own settings, and the base version raised "The parameter is incorrect: target" on every window activation (seen 4 times in the log, harmless but noisy).

Part 2 round 3 (Sam, 2026-11-17): the crash is gone (63 changes, no errors, nothing thrown). Reported: Start was not centred when the bar is centred (Left and Right looked right). Cause: Start always lined its left edge up with the bar's left edge; in Right mode the screen edge hid that. Fix: Start now lines up with the bar the way the bar is aligned (left edges, centres, or right edges); in Classic mode it stays at the left, under the Start button.

Part 2 round 4 (Sam, 2026-11-17): Start position per alignment confirmed; all Settings checks pass. Reported: clicking Start while the menu is open reopened it instead of closing it. Cause: pressing the button moves focus to the bar, so the menu closes itself on focus loss; the click then arrives, finds it closed, and opens it again. Fix: a Start-button click within half a second of a focus-loss close counts as that close, so the menu stays shut.

## Batch 4 - Hotkeys, flyouts, hover thumbnails

Split in three parts so each can be checked on its own: 1 keyboard (hook, shortcuts that need no new window, real status line); 2 flyouts (Win+N calendar, Win+A quick settings, Win+Tab and Win+` switchers, Esc closes them); 3 hover thumbnails.

### Part 1 - Windows key hook and shortcuts

**Status: GREEN (Sam, 2026-11-17)**, after round 1 below (Win+1..9 caught in the hook).

Built:

- **Keyboard thread** (`Shell/KeyboardService.cs`): the low-level keyboard hook and all shortcuts live on a thread of their own, so a busy UI thread never makes typing lag (Windows drops a hook that answers slowly).
- **Lone Win key** opens and closes Machitan's Start. Only a lone press is taken: every Win combination passes through to Windows. Its release is held back and sent again after an unassigned key (0xE8), so Windows sees a combination and keeps its own Start shut, and no key is left looking held down. Machitan's own key presses are marked and pass straight through. If Machitan's Start is missing, a lone Win key is left to Windows.
- **Shortcuts** (`Shell/HotkeyActions.cs`), registered with Windows one by one:
  - Win+1..9: the app in that place on the dock. Not running: start it. Running, not in front: bring it forward. In front with one window: minimise. In front with several: step to the next window.
  - Win+Shift+1..9: start another copy.
  - Win+D: Show desktop (same as the bar's strip). Win+E: File Explorer. Win+L: lock. Win+R: Windows' Run box.
  - Alt+Esc: next open window in the bar's order (minimised windows skipped).
  - Shortcuts Windows owns itself come back as error 1409: logged and left to Windows, as planned (not a failure).
- **Bringing windows forward** (`Shell/WindowActivator.cs`): tries the plain way, then borrows the front app's input state for a moment, then a quick Alt tap. Used by Start (opened by the Win key, Windows may not hand over the keyboard by itself), the shortcuts and the dock.
- **System status** in Settings now reports the real state: hook active or not (with the error), which shortcuts Machitan handles, which Windows owns, which failed.

How I read one spec point (say so if wrong): Alt+Esc steps to the next window, like Windows' own, but in the bar's order and skipping minimised windows, so it never pops minimised apps back up.

To check:

1. Clean build: 0 errors, 0 warnings.
2. Press and release Win alone: Machitan's Start opens (Windows' own does not). Again: it closes.
3. Win combinations still work: Win+V, Win+Shift+S, Win+I, Win+arrow keys.
4. Settings > System status lists what Machitan handles and what Windows owns. Tell me what it says.
5. The shortcuts Machitan handles do what is listed above.
6. Typing in any app feels normal; no key sticks after using Win.

#### Part 1 round 1 (Sam, 2026-11-17)

Works: lone Win opens and closes Machitan's Start, Win combinations, typing, Win+D/E/L/R, Alt+Esc, the status line. Not working: Win+1..9.

Log: Windows owns Win+1..9, Win+Shift+1..9, Win+D, Win+E, Win+L and Win+R (error 1409); Machitan got only Alt+Esc. Win+D/E/L/R are fine left to Windows (same result). Win+1..9 were not: Windows' version acts on Explorer's hidden taskbar and its own app list, not on Machitan's dock.

Fix (a deliberate, narrow step past "the hook takes only a lone Win key"; say if you would rather not): shortcuts can be marked "catch when Windows owns it". Only Win+1..9 and Win+Shift+1..9 are, and only when Windows refused them. The hook then holds that number key back from Windows (press, repeats, release), runs the dock action, and masks the Win release so Windows' Start stays shut. Ctrl or Alt with Win+number still go to Windows. Settings > System status lists these as "Owned by Windows, so Machitan catches them in its keyboard hook".

### Part 2 - Flyouts and the switcher

**Status: GREEN (Sam, 2026-11-17)**, after rounds 1-6 below. The log confirms the day names: 7 labels found, "fit, font 12".

Built:

- **Flyout base** (`Flyouts/FlyoutWindow.cs`): a see-through window like Start, with rounded boxes and the bar's fill (acrylic or translucent colour), theme from Settings. Closes on Esc and on a click anywhere else; not in Alt+Tab. One pop-up at a time: opening one closes Start and the others.
- **Win+N - calendar** (`Flyouts/CalendarFlyout.cs`): the time and today's date in your own format, a "Today" button, and Windows' own month calendar (`CalendarView`). No notification list: Machitan keeps no notification history and shows none. Opens at the bar's right end.
- **Win+A - quick settings** (`Flyouts/QuickSettingsFlyout.cs`): volume of Windows' default speakers through Core Audio (`Shell/VolumeService.cs`, `Interop/CoreAudio.cs`), with mute and the device's name; follows changes made elsewhere while open. Brightness over DDC/CI (`Shell/BrightnessService.cs`, `Interop/Dxva2.cs`), one slider per screen that answers, run in the background (monitors answer slowly). Each control appears only when Windows can really do it. Opens at the bar's right end.
- **Win+Tab and Win+` - switcher** (`Flyouts/SwitcherWindow.cs`): every open window, most recently used first, with icon, title and app (and "minimised"). As tall as needed up to 70% of the screen, then it scrolls. Starts on the previous window, so the shortcut then Enter flips between two. The shortcut again, Tab, arrows move; Enter or a click switches; Esc closes.
- **Shortcuts**: Win+N and Win+A are caught in the keyboard hook when Windows keeps them (like Win+1..9). Win+Tab is left to Windows' Task View when Windows keeps it, as the plan allows. Win+` is caught too if Windows ever keeps it.
- The System status line no longer says these arrive later.

How I read three spec points (say so if wrong):

1. Win+` opens the same switcher as Win+Tab: it is the switcher's own key, for when Win+Tab belongs to Windows (the plan's test list: "Win+Tab fall-through noted, Win+` switcher").
2. The calendar and quick settings open at the bar's right end (like Windows' own, which sit at the taskbar's right); the switcher opens in the middle of the screen.
3. Brightness is DDC/CI only, as planned. A laptop's own screen usually does not answer DDC/CI, so on this PC brightness is likely hidden unless an external monitor is plugged in. The laptop screen would need WMI (a new package, System.Management, or hand-written WMI calls): not built; say if you want it.

To check:

1. Clean build: 0 errors, 0 warnings.
2. Win+N: the calendar opens with the right time and date; Today works; Esc or a click elsewhere closes it; Win+N again closes it.
3. Win+A: the volume slider moves Windows' volume (watch Windows' own volume level), mute works, and volume keys move the slider while it is open. Brightness: shown only for a screen that supports it.
4. Win+`: the switcher lists your windows, newest first; Win+` again, Tab and arrows move; Enter or a click switches; Esc closes. Many windows: the list scrolls, nothing is cut off.
5. Win+Tab: tell me whether Windows' Task View or Machitan's switcher opens.
6. Settings > System status: tell me what it says now.

#### Part 2 round 1 (Sam, 2026-11-17)

Build: CS4004 "Cannot await in an unsafe context" twice in `Shell/BrightnessService.cs`: the whole class was marked unsafe. Fix: only the two methods that use pointers (`Probe`, `MonitorCallback`) are unsafe now.

#### Part 2 round 2 (Sam, 2026-11-17)

Works: Win+N, Win+A, Win+`, Win+Tab (Windows' Task View, as planned), status line. Sam asked for five changes:

1. **Brightness for the laptop's own screen.** Through Windows' WMI, using Microsoft's **System.Management** package (10.0.14), agreed with Sam (the plan's "no new packages without asking"). `Shell/BrightnessService.cs` now finds the built-in screen through WMI and external monitors over DDC/CI, both in the background. WMI is asked once per run: if Windows says it has no such control, it is not asked again. While quick settings is open, the slider follows the brightness keys (read once a second). With several screens, rows are labelled "built-in screen" / "display N".
2. **Notifications on Win+N: left out**, Sam's choice. Windows lets only apps with package identity read notifications (Machitan is unpackaged), and no app can switch Do Not Disturb; the plan also says "No notification history".
3. **Collapse button on the calendar** (there is no notification section). The arrow folds the month calendar away, leaving the time and date; remembered in settings (`CalendarCollapsed`, set from the flyout, logged as "changed in the calendar").
4. **Optional seconds on the calendar's clock**, off by default: Settings > Calendar (Win+N) > Show seconds (`CalendarShowSeconds`). The time uses your own long time format then.
5. **Calendar and quick settings line up with the bar like Start** (left edges, centres or right edges; Classic: left). One shared rule now places Start and both flyouts (`Shell/PopupPlacement.cs`), so they cannot drift apart again. The switcher stays in the middle of the screen.

Also: the bar no longer rebuilds itself when a setting that is not the bar's changes (the calendar fold, corner widgets); it compares its own settings first.

#### Part 2 round 3 (Sam, 2026-11-17)

Round 2 works (brightness, fold, seconds, alignment). Two changes asked:

1. **Calendar day names**: three letters (Mon, Tue, Wed...) on one line. They wrapped ("M" over "o") and the bottom row was cut in half. Now: `DayOfWeekFormat = {dayofweek.abbreviated(3)}`, the calendar is 336 DIP wide (48 a column), its day-name text blocks are kept on one line, and it picks its own height (exactly six weeks); the flyout sizes itself to that after the first layout.
2. **Win+Tab opens Machitan's switcher** too, caught in the keyboard hook like Win+` (Windows' Task View no longer opens with Win+Tab while Machitan runs). A step past the plan's "Win+Tab fall-through tolerated", at Sam's request.

#### Part 2 round 4 (Sam, 2026-11-17)

Win+Tab works. Day names still cut ("M...", "W..."): the earlier fix looked for the labels by a template name that evidently does not match, and the labels leave too little room for wide names. Now, after a layout, the calendar's day-name labels are found by their text (formatted the same way, `{dayofweek.abbreviated(3)}`), kept on one line with no margin or trimming, and if a name still does not fit, the labels' font goes one step smaller (down to 10) until all fit. The log records what was found and the final size ("Calendar: ... day-name labels found ...", "Calendar: day names fit, font N.").

#### Part 2 round 5 (Sam, 2026-11-17)

Day names still cut. The log had no "day-name labels found" line: the labels were never found by text. Likely cause: the names were formatted in Windows' display language, while the calendar shows English. Now the day-name row is found by its shape (a panel holding exactly seven text labels), with a text match in the calendar's own language as a fallback; if neither finds it, the calendar's parts are written to the log once, so the next fix can be exact.

#### Part 2 round 6 (Sam, 2026-11-17)

Round 5 never reached the build at first: Visual Studio had `CalendarFlyout.cs` open and saved its old copy over it (the round-3 version ran again). Re-written; Sam closed the tab. Then: all seven names show in full. Log: 7 labels found (`WeekDay1`.. in Grid `WeekDayNames`, 45 DIP columns, template font 12). But the fit check compared each name with the label's own width (a centred label is only as wide as its text), so it shrank the font to 10 for nothing. Fixed: the room is the label's layout slot (its column); the font now stays at 12 unless a name really does not fit.

### Part 3 - Window previews on hover

**Status: GREEN (Sam, 2026-11-18).** Previews, Peek and the staged exit all work; the log of the first exits after the change shows each window closing in turn and nothing failing. Batch 4 is done.

Built:

- **Previews** (`Flyouts/PreviewWindow.cs`): hover a running app on the dock and its windows show above the button as live pictures, drawn by Windows through the DWM thumbnail API (`DwmRegisterThumbnail`, `DwmUpdateThumbnailProperties`, `DwmQueryThumbnailSourceSize`; `Interop/Dwmapi.cs`). Each card: app icon and name, the window's title, a close button (sends the window a normal close request, so it can still ask to save), and the picture in the window's own shape. A click on a card brings that window back (restored if minimised). Pinned apps that are not running get no preview.
- **Timing like Windows'**: opens about 0.4 s after the pointer rests on a button; moving to another running app switches at once; stays while the pointer is over it; goes about 0.3 s after the pointer leaves both. A click on the dock, its right-click menu, or the bar sliding away closes it at once.
- **Never takes the keyboard**: the preview window is shown without activating and does not become the active window (`FlyoutWindow` gained a no-focus mode). The bar stays up while a preview shows, even with auto-hide on.
- **Many windows**: cards are 232 DIP wide, narrower (down to 160) when needed to fit the screen; past that, a "+N more windows" card (the real count). Cards are made once (8 plus the "more" card) and reused; their fills and Windows' pictures are released when the preview closes.
- Cards keep a steady order (by window handle), so they do not swap places as windows take turns in front; titles update while open; a closed window's card goes.

To check:

1. Clean build: 0 errors, 0 warnings.
2. Hover a running app: after a moment its window shows above the button, live (a playing video moves). App name, title and close button are there.
3. Click the picture: that window comes to the front (also when it was minimised).
4. Close button: the window closes (an app with unsaved work may ask first); its card goes.
5. An app with several windows: one card each. Move between apps: the preview follows at once. Leave: it goes.
6. Typing in the app in front is never interrupted by a preview. With auto-hide on, the bar stays while a preview shows.

#### Part 3 round 1 (Sam, 2026-11-17)

All of part 3 works. Sam asked for Peek, like Windows: hovering a preview brings its window to the front until the pointer leaves. Built in `Flyouts/PreviewWindow.cs`: resting on a card for about 0.3 s brings that window to the front of the normal windows without activating it (a minimised one is shown without activating); moving to another card switches at once (a 120 ms grace covers the gap between cards); leaving puts it back exactly: minimised again, or right behind the window that was in front of it (`GW_HWNDPREV`; nothing to undo when that was an always-on-top window). A click keeps it in front for real; closing a peeked window from its card puts nothing back. Windows' own Peek also turns the other windows see-through; that needs an undocumented Windows call (`DwmpActivateLivePreview`), so it is not done. New: `Interop/WindowOrder.cs` (GW_HWNDPREV, HWND_TOP, SWP_NOOWNERZORDER, WS_EX_TOPMOST).

#### Part 3 round 2 (Sam, 2026-11-17)

"Peek doesn't work at all": Visual Studio had saved its open copy of `PreviewWindow.cs` over the round-1 version (the PC's file had no Peek code; 21,449 bytes against 26,743). Re-written. Also made sturdier: Windows does not let a background program move another app's window in front of the active one, so for the moment of a peek the window is made always-on-top (`HWND_TOPMOST`), the bar and the preview are raised above it again, and the always-on-top is always undone when the peek ends (also after a click, which then leaves the window at the front of the normal windows). Windows whose app is not answering are not peeked (`IsHungAppWindow`). The first peek of a run is logged ("Peek: a window was brought forward...") or, if Windows refuses, the error. `BarWindow.Handle` / `ShellHost.BarHandle` added.

Build after round 2: CS1061 "BarWindow has no Handle": Visual Studio again saved an old `BarWindow.xaml.cs` over the new one, a second after it was written (the file sent had `Handle`; the PC's copy did not). Re-written with all tabs closed. CS8602 at `PreviewWindow.cs` line 121 (`_moreBox` right after `MakeBox`): now set through a local, so the compiler sees it is never null.

Next build: CS0117 `HWND_NOTOPMOST` / `IsHungAppWindow`: `Interop/WindowOrder.cs` had also been saved back to its round-1 copy, the same second it was written. CS8602 (now line 128) was not about `_moreBox` at all: the pointer handlers that use `_hideTimer` were set up before the timer was created. Moved below the timers. Then every source file on the PC was compared with Claude's copy before writing.

**Correction (2026-11-17):** Sam had only the `.csproj` open in Visual Studio, so the "Visual Studio saved its old copy over the new one" explanation above (Batch 3 CS0104 repeat, Batch 4 part 2 round 4-5, part 3 rounds 1-2) was wrong. In every case the file had just been re-sent from the same staging path moments after it was updated, and the PC received the previous copy of that path. Re-sent from a fresh path, it arrived intact (verified byte for byte). From now on each delivery is sent from a fresh staging folder and read back from the PC before it is called ready. Nothing needs changing in Visual Studio.

#### Part 3 round 3 (Sam, 2026-11-17)

Peek works (log: "Peek: a window was brought forward for the first time this run (GitHub Desktop)"). One exit out of several ended in 0xC000027B in Microsoft.UI.Xaml.dll (E_UNEXPECTED): the log stopped right after "Shutdown requested.", so it failed while Machitan closed its windows, all inside the click on "Exit Machitan" while that menu was still closing. Shutdown now works in steps (`ShellHost.Shutdown`): right away the keyboard hook stops and every pop-up is hidden; then, once the click has finished, the windows close one at a time, each in a dispatcher turn of its own (low priority); last the bar closes, Explorer's taskbar is restored and the app ends. While this runs, Win+N / Win+A / Win+Tab, dock hover, Settings and the Start button do nothing, so nothing reopens a window that is closing. Each step is logged ("Shutdown: closing ..."), so if it ever fails again the log shows which window.

## Batch 5 - Corner widgets and Dynaland

Split in two parts: 1 the two corner widgets (info line and status ring); 2 Dynaland.

### Part 1 - Corner widgets

**Status: GREEN (Sam, 2026-11-18).** After rounds 1 and 2 below, everything works; the log shows the ring following seven wallpaper changes in a row, each within about a second.

Built:

- **Shared base** (`Widgets/CornerWidgetWindow.cs`): a see-through, always-on-top window in a corner of the bar's screen, 10 DIP from the edges of the work area. It never takes focus (a click leaves the keyboard where it was) and never shows in Alt+Tab or on the bar. It only reads the work area, so the bar's screen reservation is untouched. Every 2 seconds it puts itself back in place and back on top; it also re-places after screen, scaling and work-area changes. It steps aside while a full-screen app is in front (the bar now tells the shell, `ShellHost.NoteFullScreen`). The window has a fixed size and is cut to the widget's shape, so the rest of it lets clicks through. Two widgets in the same corner sit one under the other (one above the other in a bottom corner). Movable to any corner and switched on and off from Settings, as before.
- **Info line** (`Widgets/InfoLineWindow.cs`): one capsule (36 DIP, fully round, the bar's own fill) with the date, the time and the weather, each switchable; it hides when nothing is left to show. Date and time follow Windows' language ("Tue, Nov 17", short time). The weather is a one-colour symbol and the temperature, rounded (for example 27 degrees); the tooltip has the full date, the description and when it was read. Hover lightens it. A click sends Win+N to Windows: the system's own notification centre opens (marked keys pass Machitan's hook, so Machitan's calendar does not open).
- **Weather** (`Shell/WeatherService.cs`): Windows' location (`Geolocator`, asked at most once an hour), rounded to 2 decimals (about 1 km), then Open-Meteo's current weather (`temperature_2m`, `weather_code`, `is_day`) every 15 minutes, 5 minutes after a failure. Nothing is shown without a reading less than 2 hours old. If Windows does not let desktop apps use the location, the log says so once and the weather stays hidden. Runs only while the info line and its weather are on.
- **Status ring** (`Widgets/DuoRingWindow.cs`), per the spec: a rounded rectangle 36 DIP tall with 10 DIP corners and no background at all. Its 3 DIP rim is the battery: up the left, across the top, down the right, then in along the bottom from both corners, stopping 7 DIP short of the four volume dots in the middle. The fill follows the same path for the battery's share of its length: white (dark look; near-black on the light look), red #FF3B30 under 20 %, green #34C759 while charging, over a 22 % track. No % text. In the middle, exactly centred: the Wi-Fi arcs and dot (lit by signal, 3 arcs) and the network's name; "Connecting..."; "No signal" with the arcs unlit; the plane and "Plane mode" when the Wi-Fi radio is off; a cable icon and "Ethernet" when only a cable connects. A bolt beside the label while charging. The volume dots light by level (none when muted; hidden when there is no output device). The width follows the label, animated (about 0.2 s). Tooltip: battery, network and volume in words. A click sends Win+A to Windows: its own quick settings.
- **Readings**: battery from `GetSystemPowerStatus` every 5 s (`Interop/Power.cs`; no battery: the track only); volume through Core Audio twice a second (the default device is looked up again every 5 s); network on its own thread (`Shell/NetworkMonitor.cs`) every 2 s, every second while connecting, and at once on Windows' network-change event. The Wi-Fi adapter's state and radio come from Native Wifi (`Interop/Wlan.cs`); the name and signal bars from Windows' connection list. Machitan deliberately does not ask Native Wifi for the current connection: since Windows 11 24H2 that needs location permission and shows as location use in the tray each time.
- **Settings**: the corner widgets section is live (no more "not built yet"). Privacy notes for the weather (what is sent, how often, rounded) and the status ring (read on this PC, no location needed).
- **Shutdown**: both widgets hide with the pop-ups and close one per turn like the other windows ("Shutdown: closing the info line / the status ring").

To check:

1. Clean build: 0 errors, 0 warnings.
2. Top-left: the info line with date, time and (after a few seconds) the weather. Windows may ask once whether Machitan may use your location. Settings: turning date, time or weather off shrinks the line; all three off hides it.
3. Click the info line: Windows' own notification centre opens (not Machitan's calendar).
4. Top-right: the status ring. Battery rim matches the battery level; plug in the charger: the rim turns green and a bolt appears. Wi-Fi name in the middle. Change the volume: the dots follow; mute: all dim.
5. Turn Wi-Fi off in Windows: "Plane mode" with the plane. On again: "Connecting..." then the name; the ring's width slides to fit.
6. Click the ring: Windows' own quick settings open.
7. Settings: move each widget to another corner; put both in one corner (they stack). A full-screen video or game: both step aside, and come back after.
8. Exit: both close; the log shows "Shutdown: closing the info line" and "the status ring".

#### Part 1 round 1 (Sam, 2026-11-18)

Sam asked for five changes before testing further:

1. **Top strip** (`Widgets/TopStrip.cs`, setting "Keep the top free", on by default): Machitan keeps a strip at the top of the bar's screen through an AppBar of its own (owned by a small hidden window; the bar's AppBar is untouched), so maximised windows start below the top widgets: 10 + 36 + 10 DIP for one row, 100 DIP when both widgets share a top corner, none when no widget is in a top corner. Top-corner widgets sit inside the strip. It stays while a full-screen app hides the widgets (so windows do not jump with every video), is given back first thing at exit, and is not kept while the bar itself is docked at the top. When another top app bar changes, it only moves if Windows would now put it elsewhere, so two app bars never keep answering each other.
2. **Manual weather place** (Settings > Corner widgets: "Weather location" and "Weather place"): type a city and press Find (or Enter); the first match from Open-Meteo's place search (name, region, country) becomes the place and the weather switches to it at once. Typed coordinates ("-5.14, 119.42") are used without any search. Windows' location is then not asked at all. Settings show what is in use; with "A place I choose" but no place yet, the weather stays hidden and the log says why. The tooltip names the chosen place. New settings: `WeatherUseManualPlace`, `WeatherPlaceName`, `WeatherLatitude`, `WeatherLongitude` (both null when none; half a place counts as none).
3. **Ring follows the wallpaper** (`Visuals/WallpaperSampler.cs`): Machitan reads Windows' own wallpaper record for the ring's screen (`IDesktopWallpaper`: picture, fit, background colour; `Interop/DesktopWallpaper.cs`), decodes a small copy (at most 640 px), places it the way Windows shows it (fill, fit, stretch, centre, tile; span judged as fill) and samples the area behind the ring (48 x 8 points). The ink is white or near-black, whichever contrasts more on average. When more than 15 % of that area has under 3:1 contrast with the ink (a busy or mixed wallpaper), a soft backing in the opposite tone appears behind the ring (black at 35 % under white ink, white at 55 % under dark ink); otherwise there is still no background at all. Read again when Windows reports a new wallpaper, when the ring moves or gets much wider or narrower, and every 30 s if Windows' record changed (slideshows). The choice is logged when it changes ("Status ring: the wallpaper behind it is ..."). If the wallpaper cannot be read, the ring keeps the theme's colours.
4. **Volume dots**, Sam's rule, on the percentage Windows shows: 0 % none, 1-25 % one, 26-50 % two, 51-75 % three, 76-100 % four. Muted shows none.
5. **Seconds on the info line** (`InfoLineShowSeconds`, off by default): Windows' long time format when on.

#### Part 1 round 2 (Sam, 2026-11-18)

Everything works except one thing: after a wallpaper change the ring kept its old colours until Machitan was started again. The log showed why: in the run where the wallpaper was changed, the ring judged the wallpaper once at start and never again (the next start read the new, darker picture at once: luminance 0.66 before, 0.10 after). So neither Windows' "wallpaper changed" notice (WM_SETTINGCHANGE) reached the ring, nor did the 30-second check see a difference: it compared only what Windows' wallpaper record (IDesktopWallpaper) reported, which apparently kept giving the old answer inside a program that was already running.

Fix:
- **Noticed from what always changes** (`Visuals/WallpaperWatcher.cs`, new): a background thread is woken by Windows the moment it writes the wallpaper settings in the registry (`HKCU\Control Panel\Desktop`, and `...\Explorer\Wallpapers` for slideshows and per-screen pictures; `RegNotifyChangeKeyValue`, `Interop/Advapi32.cs`), and the file Windows actually shows (`TranscodedWallpaper` and its siblings in the Themes folder) is watched for writes. Notices are bundled into one check about a second after the last, plus one more 4 seconds later (Windows can finish writing the picture after the registry). Windows' notice and the 30-second check stay as extra triggers.
- **Read from the registry too** (`WallpaperSampler.ReadSettings`): when every screen shows the same picture (always so with one screen), the picture's path, its fit (`WallpaperStyle`, `TileWallpaper`) and the background colour (`Control Panel\Colors\Background`) are read from the registry, which is current at once; Windows' wallpaper record still gives the screens, and the per-screen pictures when they differ. The comparison also includes the write time and size of both the picture and the file Windows shows.
- The log now names the file and where its path came from ("... a picture (fill, img19.jpg) from the registry ..."), and says when a change was seen and why ("Status ring: the wallpaper changed (Windows' wallpaper settings changed); reading it again.").

### Part 2 - Dynaland

**Status: GREEN (Sam, 2026-11-18)**, reworked by the additional prompt below; see there.

Built (`Widgets/DynalandWindow.cs`, `Shell/MediaService.cs`, `Visuals/TrackAccent.cs`):

- **Look, Notch Nook style**: a black pill hanging from the top edge of the bar's screen, centred, with square top corners and round bottom corners (12 DIP collapsed, 22 DIP open) and a faint 1 px edge so it shows on dark wallpapers. Always dark, whatever the theme. When the bar is docked at the top it hangs below the bar instead.
- **Collapsed (32 DIP tall)**: the clock when nothing is playing; while something plays, its cover (20 DIP), its title and a note in the cover's colour (a pause sign while paused). The pill fits its content (96 to 270 DIP).
- **Open on hover** (after 0.14 s; closes 0.38 s after the pointer leaves; a click on the collapsed pill opens it at once): 400 x 152 DIP with the clock and the playing app's name on top, the cover (64 DIP), title and artist, previous / play-pause / next (each enabled only when the app allows it), and the position with elapsed and total time when the app reports a timeline (moved on between the app's reports while playing). Nothing playing: a large clock, the date, and "Nothing is playing".
- **Data**: Windows' media controls (`GlobalSystemMediaTransportControlsSessionManager`): the current session, its title, artist, cover, state, timeline and allowed controls; events from Windows are handled on the UI thread; play/pause, next and previous are passed to the app. A session without a title shows nothing. The app's name comes from Machitan's app list (by app ID or exe), else from Windows' ID.
- **Accent from the cover**: the cover is decoded small (40 x 40) and the strongest hue is taken (pixels weighted by colour and brightness; greys and near-blacks ignored), then made light enough to read on black. Used for the progress bar, the play/pause sign and the collapsed note. A cover without real colour gives a neutral light grey, not an invented colour. The first accent per run is logged.
- **Animation, per the spec**: the window has one fixed size and never resizes. The pill is a Composition shape (`CompositionRoundedRectangleGeometry` in a `ShapeVisual`) whose size, position and corner radius animate (0.3 s, slight overshoot); the two contents cross-fade through their Composition visuals. All Composition objects are held for the window's life.
- **Click-through when collapsed**: the window is cut to the pill. While collapsed only the small pill takes the pointer; everything else in the window lets clicks through. When opening, the window takes the large outline first; when closing, it keeps it until the pill has shrunk, so the animation is never cut off.
- Never takes focus; steps aside for full-screen apps; kept in place and on top every 2 seconds; re-placed after screen changes.
- **Top strip**: while Dynaland is on, the strip at the top keeps at least the collapsed pill plus a margin (40 DIP), so maximised windows also start below it when no corner widget is at the top. The open view still floats over windows (hover only).
- **Settings**: the Dynaland switch is live (no more "not built yet").
- **Shutdown**: Dynaland hides with the other pop-ups and closes in its own turn ("Shutdown: closing Dynaland").
- Left for Batch 6: the Tray section (spike-gated).

To check:

1. Clean build: 0 errors, 0 warnings.
2. Nothing playing: a small black pill at the top centre with the clock. Hover: it opens to the large clock, the date and "Nothing is playing"; move away: it closes.
3. Play music (Spotify, a browser tab, Media Player): the pill shows the cover and the title, and the note takes the cover's colour. Hover: cover, title, artist, app name, the position moving, and the controls; previous, play/pause and next work.
4. Pause: the note becomes a pause sign; the play/pause button shows play.
5. Clicks next to the collapsed pill reach the window below (for example a browser tab under it).
6. A full-screen video: Dynaland steps aside and comes back after.
7. Settings: turn Dynaland off and on.
8. Exit: "Shutdown: closing Dynaland" in the log.

## Ring + Dynaland (additional prompt)

Sam, 2026-11-18. An additional prompt on top of the existing build (not a restart): it changes only the corner duo ring and Dynaland, plus three settings fields. The bar, the Start menu and the Settings window UI are not touched (one line in the Settings window follows the renamed ring setting). The corner info line stays as built in Batch 5.

**Status: GREEN (Sam, 2026-11-18)**, after rounds 1-7 below (r14-r21). The log of the last run is clean: no errors or warnings; Dynaland and the strip followed the bar between the edges, and the bar minimised Settings on a second click. Batch 5 is done.

### Duo ring

- **A capsule now** (supersedes "10 px corners"): 36 DIP tall, fully round (18 DIP), like the info line. The rim still runs up the left, across the top, down the right and in along the bottom from both corners to the volume-dot gap; with the full radius each end of the rim is a half circle. Window cut and the optional backing use the same 18 DIP radius.
- **Wi-Fi icon 19 DIP** (was 16): three arcs and a dot, lit by signal.
- Everything else as built: real battery, network and volume; states "Connecting...", "No signal", "Plane mode"; click sends Win+A to Windows; movable to any corner; stacks under the info line when they share a corner (no overlap); see-through window (no acrylic).
- **Kept from Sam's earlier request** (round 1 of Batch 5 part 1, confirmed by Sam in round 1 below): ink follows the wallpaper, and a soft backing appears only where the wallpaper behind the ring is too busy to read. On a calm wallpaper there is no background, as the prompt says.

### Dynaland

- **Idle is a small empty pill** (110 x 32 DIP) and does nothing: no clock (the info line owns the time), no media, no tray strip; hovering an idle pill does not open it.
- **It grows only for live things:**
  - **Music** (Windows' media controls): the pill widens to the cover, title and a note in the cover's colour; hovering opens the full view (app name, cover, title, artist, previous / play-pause / next, position). When a new track starts playing, the full view also shows by itself for 4 seconds. The clock was removed from this view too.
  - **Volume change**: the pill shows the level (icon, bar, percentage, or "Muted") for 1.8 s. Read from Core Audio four times a second; the first reading after showing only sets the start, so starting Machitan does not flash it.
  - **Camera / microphone use**: when a use starts, an "In use now" view opens for 4 seconds, naming the device and the apps (also on hover while in use).
- **Privacy dots**: a green camera dot and an orange mic dot on the pill in every state (idle, music, volume, open), only while the device is in use. **Detection** (`Shell/PrivacyMonitor.cs`): Windows records each app's camera and microphone use in the registry under `HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam` and `\microphone` (Store apps by package family name, desktop apps under `NonPackaged` by exe path), with `LastUsedTimeStart` and `LastUsedTimeStop`; while an app uses the device its stop time is 0. These are the records Windows' own privacy indicator uses. Machitan only reads them: it never opens the camera or the microphone. A desktop app counts only while its exe is running, so a crashed app cannot leave a stale dot. A background thread is woken by Windows when the records change (`RegNotifyChangeKeyValue` on both keys and their subkeys) and also checks every 3 seconds. App names come from Machitan's app list (Store apps) or the exe's description (desktop apps). Each start of use is logged ("Privacy: Camera: ... Microphone: ...").
- **Not for notifications**: Machitan is unpackaged and cannot see a notification arrive; nothing expands for one. Notifications wait for the packaging conversation (Sam's call).
- **Click opens the Dynaland centre** (changed to hover in round 1 below) (`Flyouts/DynalandCentre.cs`), under the pill: (1) "In use now", from the same records as the dots, each device with its dot colour and the apps using it, or "Nothing is using the camera or the microphone."; (2) Notifications: one button, "Open Windows' notification centre", which sends Win+N to Windows. There is no notification list. Closes on Esc, on a click elsewhere, and on a second click on Dynaland; it closes the other Machitan pop-ups when it opens and is closed with them. While it is open the pill stays collapsed. A click on Dynaland's media buttons does not open it.
- Unchanged: Notch Nook look, fixed-size see-through window, pill animated inside through Composition (never `AppWindow.Resize`), window cut to the pill so everything around it lets clicks through, cover-art accent, full-screen step-aside, top strip of at least 40 DIP while Dynaland is on.

### Settings (model and settings file only)

- `ShowDuoRing` (default true): the ring's switch, renamed from `DuoRingEnabled`. An existing file's `DuoRingEnabled` value is read, so the saved choice carries over; the old key is dropped on the next save. The Settings window's ring switch now uses the new name.
- `DuoRingCorner` (default "TopRight"): unchanged.
- `ShowPrivacyDots` (default true): Dynaland's camera and mic dots and its "In use now" view on the pill. The centre's "In use now" row stays either way. No Settings-window row (that is Batch 3's job, per the prompt).
- `EnableDynaland`: unchanged. Enums are still written as names.

### Left out, and why

- **The tray grid in the Dynaland centre**: its spike has not been run. The verified lead, FlaUI.UIA3, means two new packages (`FlaUI.Core`, `FlaUI.UIA3`), and the rule is to ask first; Sam has been asked. The other lead, `TB_GETBUTTON`, reads the old Win32 toolbar of the notification area, which Windows 11's new taskbar no longer uses; it was not tried. No placeholder or invented tray icons are shown: the centre holds the privacy row and the notification-centre button only.
- No notification list, no toast log, no timer feature, no notification-triggered expansion (per the prompt).

### To check (PASS / FAIL / NOT TESTED)

1. Clean build, 0 errors, 0 warnings; launch; diagnostics log clean.
2. Ring: a capsule (fully round ends), no background on a calm wallpaper; rim follows the battery; Wi-Fi icon larger, name centred; volume dots; width follows the name; click opens Windows' quick settings.
3. Dynaland idle: a small empty black pill; no clock; hover does nothing.
4. Dots: open the Camera app: a green dot appears (and "In use now" opens for 4 s); close it: the dot goes. A call or Voice Recorder: the orange dot.
5. Music: the pill widens; a new track opens the full view for 4 s; hover opens it; controls work; accent follows the cover.
6. Volume: change it: the level shows briefly.
7. Click Dynaland: the centre opens under it (privacy row + notification-centre button); Esc, a click elsewhere, and a second click on Dynaland close it; the button opens Windows' notification centre.
8. Leave running a few minutes: pill steady; no errors in the log.

### Round 1 (Sam, 2026-11-18)

r14 built and ran: the centre opened under the idle pill (Sam's screenshot). Sam's changes:

- **The centre is a hover flyout now, not a click.** Pointing at Dynaland (idle or not) for 0.14 s shows the centre under the pill; with music playing the pill also opens to the player and the centre sits under the player. It stays while the pointer is on the pill, on the centre, or crossing the gap between them, and goes 0.38 s after the pointer leaves all three (checked every 0.12 s from the cursor position, since the centre is a separate window). It never takes the keyboard (hovering must not pull focus from the app being typed in), so Esc and click-away no longer apply. A click on the pill shows the same at once. Hovering no longer opens the pill's own "In use now" view: the centre shows that; the 4-second "In use now" view when a use starts stays. The centre moves with the pill when the pill opens or closes under it.
- **The soft backing behind the ring stays** (Sam): only where the wallpaper behind the ring is too busy to read, none on a calm one.

### Round 2 (Sam, 2026-11-18)

Round 1 built and ran (Sam's screenshot: the centre under the idle pill on hover). Sam's changes: the section belongs inside Dynaland itself, not in a separate flyout; and with music playing, the music comes first, with an arrow to the general section.

- **The general page lives inside Dynaland.** Hovering opens Dynaland itself (400 x 152 DIP, like the player):
  - **With music playing**: on the player first (as before), with a small arrow (chevron right) at the top right, next to the privacy dots, to go to the general page.
  - **Without music**: straight on the general page.
  - **General page**: "In use now" (each device with its dot colour and the apps using it, or "Nothing is using the camera or the microphone."), then "Notifications" with the "Open Windows' notification centre" button (closes Dynaland, sends Win+N to Windows). While music plays, a small arrow (chevron left) at the top right goes back to the player.
  - Each new hover starts on the player again. The pages cross-fade inside the same pill (same size, so the pill does not move). Moving away closes Dynaland (0.38 s, checked from the cursor position as well).
- **The separate flyout is gone**: `Flyouts/DynalandCentre.cs` is no longer used by anything (removed from `ShellHost` and Dynaland). Sam deletes the file; it still compiles if it stays, so the build does not depend on that.
- Unchanged: the 4-second "In use now" view when a use starts, the 4-second player view on a new track, the volume display, the dots, the soft backing behind the ring.

### Round 3 (Sam, 2026-11-18)

Round 2 built and ran (Sam's screenshot of the player, app name "WACUP (64-bit)" centred). Sam's seven changes:

1. **Music text scrolls.** The small pill now shows "Title - Artist" (just the title when there is no artist), and the open player's title and artist each scroll sideways when they do not fit, then loop without a jump (a 1.5 s wait first, about 32 DIP a second; the text is drawn twice with a 40 DIP gap). Text that fits stands still. The sliding runs in Composition, so it costs no layout work (`Visuals/Marquee.cs`).
2. **Machitan's Settings minimises from the bar like other apps.** Clicking its bar icon while it is in front now minimises it. Cause: the bar and Settings share a thread, so a click on the bar moved the focus to the bar's window and Windows reported the bar, not Settings, as in front. The bar now remembers the last active app window (from Windows' shell messages) and uses it whenever the window in front is one of Machitan's own bar or pop-up windows; the desktop or another non-app window clears it. Any window of a group in front counts, not only its first (`Shell/TaskService.cs`).
3. **Dynaland shape: Notch or Island** (Settings > Dynaland > Shape; new setting `DynalandStyle`, default "Notch"). Notch is as before. Island (iPhone-inspired) floats 8 DIP below the top edge, round all around: a full capsule when small, 30 DIP corners open (28 for the "In use now" view), and the open pages sit a little lower so their top row clears the round corners. Changing it applies at once. The top strip keeps 8 DIP more for the island.
4. **The app name in the player is left-aligned** (it was centred: a capped width with the default stretch).
5. **Start search puts the best fit first**: the whole name, then names that start with what was typed, then names with a word that starts with it, then names that contain it; equal fits stay A to Z (pins in pin order). "WACUP" now lists WACUP before Uninstall WACUP, and Enter opens the first one.
6. **The bar's search icon opens Windows' own search** (Win+S). The search box (Box mode) still opens Machitan's Start with its search box ready.
7. **Top strip cover** (Settings > Corner widgets; new settings `TopStripCover` off by default, `TopStripCoverColour` "Dark" / "Light" / "Accent", `TopStripCoverOpacity` 10-100%, default 60%). One flat colour over the strip at the top, like a menu bar's background. It shows only while the strip is kept (a top-corner widget or Dynaland is on, "Keep the top free" is on, and the bar is not docked at the top), steps aside for full-screen apps, never takes focus, and the widgets and Dynaland stay above it. See-through window, no acrylic (`Widgets/TopStripCoverWindow.cs`).

Also: Settings > Dynaland now has a **Camera and mic dots** switch (`ShowPrivacyDots` had no row yet), and the Dynaland description no longer mentions a clock.

**To check:**

1. Clean build; launch; log clean.
2. A long title: the small pill scrolls "Title - Artist"; hover: the long title and artist scroll; short ones stand still.
3. Open Settings, click its bar icon: it minimises; click again: it comes back. Other apps behave as before.
4. Settings > Dynaland > Shape > Island: the pill floats below the edge, round all around, in every view; back to Notch.
5. The player's app name sits on the left.
6. Start: type "WACUP": WACUP first, Uninstall WACUP after; Enter opens WACUP.
7. The bar's search icon opens Windows' search.
8. Settings > Corner widgets > Cover the top strip: the strip fills with the colour; colour and opacity change it live; a full-screen video hides it; off removes it.

### Round 4 (Sam, 2026-11-18)

Round 3 built and ran (Sam's screenshots: the island pill, the cover over the strip, the ring with dark ink on a dark cover). Sam's four changes; Sam chose outward curves for the notch, and the widgets 8 DIP down whatever the shape:

1. **The ring's colours follow the cover too.** When the top strip cover is on, the ring judges what is behind it as the screen shows it: each wallpaper sample is blended with the cover's colour and opacity before the ink (dark or white) and the soft backing are chosen. It judges again whenever the cover appears, goes, or changes colour or opacity. If the wallpaper cannot be read, a cover at 50% or more decides the ink alone. The log line says when the cover was taken into account.
2. **Settings minimising from the bar, second try.** The r17 fix relied on Windows' shell messages to remember the app in front, and that was not enough. Now Machitan follows every change of the window in front with Windows' foreground event (`SetWinEventHook`, `EVENT_SYSTEM_FOREGROUND`, no new package). When the bar itself has taken the focus (a click on it while Settings is in front: they share a thread), the window in front just before the bar counts, unless it was minimised since. Each click on a running app on the bar now writes one line to the log ("Bar: Settings minimised (it was in front)." or "... brought to the front (in front was ...)"), so a further miss can be traced.
3. **Info line and ring are smaller and line up with Dynaland**: 32 DIP tall (was 36), the same as Dynaland's small pill, and 8 DIP from the screen's edges (was 10), the island's gap, so with the Island shape all three sit on one line. Text one point smaller (info line 12, ring label 12), side padding 14. The top strip, and so the cover, shrinks from 56 to 48 DIP (8 + 32 + 8); two widgets in one corner need 88.
4. **The notch's top corners curve outward** into the screen's top edge, like a laptop's notch: a 10 DIP curve on each side, drawn as two small shapes that follow the pill's edges through every animation (an expression on the pill's shape), with the pill's faint outline running along the curves. The window is 20 DIP wider for them, and its cut includes a band along the top edge. If Windows refuses the shapes, the notch keeps square top corners and the log says so. Island is unchanged.

**To check:**

1. Clean build; launch; log clean.
2. Cover on, dark and nearly solid: the ring turns to white ink; light cover: dark ink; the log line mentions the cover. Change the opacity: the ring follows.
3. Settings in front, click its bar icon: it minimises; click again: it comes back. If not, send the log (the "Bar:" lines).
4. Island: info line, Dynaland and ring on one line, the same height. Notch: the widgets stay 8 DIP down.
5. Notch: the top corners curve out into the edge, idle, with music, open and in the "In use now" view; clicks just beside the curves still reach what is behind.

### Round 5 (Sam, 2026-11-18)

Round 4 built with two warnings (Sam's screenshot of the error list). Sam's three changes:

1. **Both warnings fixed.** CS0108: the ring's private arc helper was called `Corner`, the same name as the widgets' `Corner` property; it is `CornerArc` now. CS8602 in `Visuals/WallpaperWatcher.cs`: the settle timer's handler used the second timer before it was made; that timer is made first now.
2. **Bar buttons' hover box follows the bar's curve.** Every bar button (Start, search icon, dock apps) had a fixed 10 DIP corner. Now the corner is concentric with the pill the button sits in: the pill's radius minus the gap between the pill's edge and the button. In the capsule pills (Segmented, Joined) that makes the hover box nearly round at the ends, the same curve as the bar; in Classic (16 DIP corners) gently rounded. It follows the bar size.
3. **Typing in the bar's search box** (Box mode). A click in the box now lets you type right there: the bar takes the keyboard for that (the one time it does; it never does on its own), and the results show in a flyout next to the bar, its left edge lined up with the box (`Flyouts/SearchResultsFlyout.cs`). The search is Start's: app names, best fit first (the ranking is shared now, `Start/SearchRank.cs`), up to 8, with their icons; `files:` searches your own folders (up to 8, needs two letters); `apps:` apps only. The first result is selected; Up and Down move, Enter opens it, a click opens any. Esc clears the box and gives the keyboard back to the window that had it; a click anywhere else, or opening a result, ends the search too. The flyout never takes the keyboard. Honest notes: "No app matches.", "Reading your apps...", "Searching your files...", "No file matches in your folders.". The bar does not auto-hide while you type. If Windows refuses to give the bar the keyboard, the box opens Start's search as before (logged). The search icon still opens Windows' own search.

**To check:**

1. Clean build, no warnings.
2. Hover a bar button: the hover box's corners follow the bar's curve (Segmented and Classic).
3. Box mode: click the box, type "wac": WACUP is first in the flyout next to the bar; Down / Up move; Enter opens it; Esc clears and returns to the app you were in; a click elsewhere closes it. Try "files:" and a file name.

### Round 6 (Sam, 2026-11-18)

Round 5 built and ran. Sam tried the bar and the widgets in different places (five screenshots: bar top centred, top left, bottom centred, bottom left, top centred with the widgets at the top). What was wrong, and the fixes:

1. **Bar at the top stayed about 48 px down** (widgets in the top corners). The top strip Machitan keeps for the top widgets was still reserved when the bar moved to the top, so Windows placed the bar below it; the strip was then released (it is never kept while the bar is at the top) but nothing told the bar, so it stayed there. Now, whenever the strip comes, goes or changes, the bar is placed again.
2. **Nothing followed the bar when it moved.** The widgets and Dynaland are now placed again whenever the bar's position or size changes (alignment, edge, size, apps added or closed).
3. **Widgets on the bar's edge** (bar at the bottom and widgets in the bottom corners, or both at the top): when the corner is clear of the bar (the widest widget in that corner plus an 8 DIP gap does not reach the bar), the widgets sit in the bar's band, centred on the bar, instead of floating above it. When the bar reaches the corner (aligned to that side, or Classic), they sit just past the bar, 8 DIP from it (it was 8 DIP from the work area, which left an uneven 15 px gap).
4. **Widgets line up with the bar at the sides**: 16 DIP from the left or right side (the bar's own margin; was 8). Still 8 DIP from the top or bottom edge.
5. **Dynaland with the bar at the top**: if the bar leaves the top centre free (aligned left or right), Dynaland stays at the screen's top edge, between, in its own shape (it was hanging below the bar's band, in mid-air). If the bar covers the centre, Dynaland floats 8 DIP below the bar with the island's look whatever the shape setting (a notch cannot hang from the bar). The log says which.

**To check:**

1. Bar at the top, centred, widgets in the top corners: the bar is at the very top; the widgets sit beside it in its band, centred on it; Dynaland floats just below the bar as an island.
2. Bar at the top, aligned left: Dynaland at the top edge in the middle (notch or island as chosen); a top-left widget sits below the bar, 8 DIP from it.
3. Bar at the bottom, centred, widgets in the bottom corners: the widgets sit beside the bar, centred on it.
4. Bar at the bottom, aligned left, both widgets bottom left: they stack above the bar, 8 DIP apart, left edges in line with the bar.
5. Switch edges and alignments back and forth: everything follows without a restart.

### Round 7 (Sam, 2026-11-18)

Round 6 built and ran. Sam confirmed: **Settings now minimises from its bar icon** (r18) and **typing in the bar's search box works** (r19). Sam's two changes:

1. **Dynaland's position** (Settings > Dynaland > Position; new setting `DynalandPosition`: "Auto" (default), "Top", "Bottom"). Auto puts Dynaland on the edge without the bar: the top while the bar is at the bottom, the bottom while the bar is at the top. At the bottom, everything is mirrored: the notch hangs up from the bottom edge with its curves at the bottom corners, the island floats 8 DIP above the edge, the pill grows upward when it opens, and the open pages keep their rows clear of the rounded side. On the bar's own edge (chosen by hand), the r20 rule applies at either edge: beside a bar aligned to a side Dynaland stays at the edge; when the bar covers the centre it floats just past the bar as an island.
2. **Widgets stand side by side instead of stacking up.** Two widgets in one corner now form a row, 8 DIP apart: the info line nearest the corner, then the ring. On the bar's edge the row sits in the bar's band, centred on the bar: in the corner when it is clear of the bar, or **right after the bar** when the bar is aligned to that side, so bar, info line and ring make one line (it stops before Dynaland when Dynaland sits in the same band). With no room beside the bar (Classic, or a bar too wide), the row sits just past the bar. Each widget now tells the shell its real width (the info line's capsule follows its text, the ring its Wi-Fi name), and the row moves when a width changes.
3. **The strip moves to the free edge.** Machitan's reserved strip (Settings: "Keep the free edge clear", same setting as before) is now kept at the edge without the bar, for the widgets in that edge's corners and Dynaland when it is there: at the top while the bar is at the bottom, at the bottom while the bar is at the top. One row now (8 + 32 + 8 DIP), since widgets no longer stack. The cover ("Cover the strip") follows the strip.

**To check:**

1. Bar at the bottom, Position Auto: Dynaland at the top (as before). Bar to the top: Dynaland moves to the bottom, mirrored (notch from the bottom edge with curves at the bottom; island 8 DIP above it); hover: it opens upward.
2. Position Top / Bottom by hand on the bar's edge: beside a left- or right-aligned bar it stays at the edge; with a centred bar it floats just past the bar.
3. Bar at the bottom aligned left, both widgets bottom left: bar, info line, ring in one line, centred on the bar.
4. Both widgets in a top corner: side by side; the top strip is one row.
5. Bar at the top, widgets in the bottom corners: a strip at the bottom keeps maximised windows clear of them; the cover (if on) is at the bottom.
6. Turn seconds on: the ring beside the info line does not jitter.

**Round 7 result:** GREEN (Sam, 2026-11-18): "everything works properly". Batch 5 is done.

## Batch 6 - Dynaland tray grid (FlaUI)

Sam, 2026-11-18: "continue to Batch 6 and use FlaUI" (the two packages agreed).

### Part 1 - Spike and first grid

**Status: built and ran (r23); the spike answered no** (see "Spike result" below). The spike's question: can Machitan read Windows' tray icons and click them while Explorer's taskbar is hidden? Claude cannot run anything on this PC, so the spike is built into Machitan and answers in the log on the first start; the same code shows the grid, so one run shows whether a real tray icon appears and responds.

- **New packages: `FlaUI.Core` and `FlaUI.UIA3` 5.0.0** (the latest release; it targets .NET 8 and runs on .NET 10). They bring Windows' UI Automation wrapper (`Interop.UIAutomationClient`) and a few small runtime packages, and they reference Windows Forms, so the self-contained build is somewhat larger. Nothing else uses Windows Forms.
- **How the icons are read** (`Shell/TrayIconService.cs`): Windows 11 draws the tray in XAML; each app icon is a button with the automation id `NotifyItemIcon` whose name is its tooltip. Machitan asks UI Automation for those buttons under Explorer's taskbar window (`Shell_TrayWnd`); if the hidden window shows nothing, each of its child windows (the XAML island) is asked directly. All UI Automation work runs on a background thread, one call at a time, with 2-second timeouts so a busy Explorer cannot hold anything up. Icons in Windows' "hidden icons" menu are not included: Windows makes them visible to UI Automation only while that menu is open (Raymond Chen, The Old New Thing, 2025-09-29). To get one into the grid, turn it on in Windows' Settings > Personalization > Taskbar > Other system tray icons.
- **Pictures**: UI Automation gives names, not images. The picture is Windows' own: its tray records (`HKCU\Control Panel\NotifyIconSettings`, one per icon, with `InitialTooltip`, `ExecutablePath` and `IconSnapshot`, a PNG) matched to the button by the same tooltip, then a tooltip starting with the other, then the program's name in the tooltip. No sure match, no picture: the tile shows the tooltip's first letter. Machitan only reads these records.
- **Clicks**: a click on a tile closes Dynaland and asks the icon's button to act (UI Automation Invoke), as a left click on Windows' tray would; a right-click asks the button for its menu (UI Automation ShowContextMenu). Before either, the app that owns the icon is allowed to come to the front (`AllowSetForegroundWindow`), so its window or menu is not stuck behind.
- **The grid** sits on Dynaland's general page, between "In use now" and "Notifications": a "Tray" title and tiles (32 DIP, the picture at 20 DIP, the tooltip on hover), up to ten in a row and two rows; with more, the last tile says "+N". The general page grows by the tray's rows (it stays 152 DIP with no tray icons); the player keeps its size. With no icons the section is not there at all.
- **When**: once 4 seconds after start (so the log has the spike's answer without hovering), and while the general page is open, every 5 seconds.
- **The spike's report** (first reading, in the log, "Tray (first reading): ..."): whether the taskbar window was found and hidden, its child windows, how many icons UI Automation sees and by which route, per icon its name, class, whether it can be clicked (Invoke), where Windows says it is, and which record gave its picture; how many tray records Windows keeps; and, when nothing is found, what UI Automation does see under the taskbar (automation ids and classes) and any refusal, for the next step. Later readings log a line only when the icons change. The first click and the first right-click are logged too.

**To check:**

1. Clean build (the two packages restore), no warnings; launch.
2. The log's "Tray (first reading)" lines: send them to Claude either way.
3. Open Dynaland's general page: a "Tray" row with your tray icons, Windows' own pictures where matched.
4. Click one: its app opens (or does what a click on Windows' tray does). Right-click one: its menu appears.
5. If nothing appears: the log says why; the grid stays out (as the plan says) until a way is found.

### Spike result (Sam, 2026-11-18)

r23 built (both packages restored, no warnings: checked in the build's own restore records) and ran. The log's first reading, 12 s after start: Explorer's taskbar window found, **hidden**; its child windows Windows.UI.Composition.DesktopWindowContentBridge, Windows.UI.Core.CoreWindow, Start, TrayDummySearchControl, TrayNotifyWnd, ReBarWindow32. **UI Automation sees nothing under it: 0 elements**, not only 0 tray icons, and no refusal either. Windows' tray records are readable: 44, all with a picture, 3 shown in the taskbar. So while Machitan hides Explorer's taskbar the usual way (SW_HIDE), Windows exposes nothing of it, and the grid stayed out, as planned.

Two ways on were put to Sam: (A) keep Explorer's taskbar shown but invisible, so UI Automation can see it (FlaUI stays); (B) a window of Machitan's own named like the taskbar that receives the tray messages apps send (no FlaUI). Sam chose A.

### Part 2 - Explorer's taskbar veiled instead of hidden

**Status: built and ran (r24); the veil works, the tray did not** (see "Part 2 result" below).

- **The main taskbar is veiled, not hidden** (`Shell/ExplorerTaskbar.cs`): it is still switched to auto-hide (so its strip of the screen is free, as before), but instead of hiding the window, Machitan gives it an empty window region (`SetWindowRgn`) and keeps it shown. Windows draws nothing of a window outside its region and sends it no clicks, so it is as invisible as before, but it counts as shown, so Windows lets UI Automation see its contents: the tray icons. The region is put on first, then the window shown if it was hidden, so it never appears for a moment.
- The quarter-second check (which used to hide the taskbar again whenever Explorer showed it) now puts the empty region back if Explorer made a new taskbar window (after an Explorer restart) and shows it again if something hid it.
- **If Windows refuses the region**, the log says so once and the main taskbar is hidden the old way for the rest of the run (then the tray grid stays empty, as in r23).
- Taskbars on other monitors (no tray there) are hidden as before.
- **Restoring** (clean exit, sign-out, and the guardian after a crash, all through one place) now also removes the region before showing the taskbar again, so a crash never leaves an invisible taskbar behind.
- The tray reading (Part 1) is unchanged; with the taskbar shown it should now find the icons.

**To check:**

1. Clean build; launch. Explorer's taskbar must stay invisible: nothing at the bottom edge, no flicker; pointing at the very bottom edge must not bring it up; Machitan's bar works as before (clicks, auto-hide if on).
2. The log: "Explorer's taskbar: kept shown but invisible ..." and the "Tray (first reading)" lines (visible now, how many icons): send them either way.
3. Dynaland's general page: the Tray row with your icons; click one, right-click one.
4. Exit Machitan: Explorer's taskbar comes back normally (visible, clickable).

### Part 2 result (Sam, 2026-11-18)

Explorer's taskbar stayed invisible with the empty region (Sam: "taskbar hiding works"). The tray stayed empty: the log's first reading found the taskbar window visible, but UI Automation saw only 2 elements under it (MSTaskSwWClass, TrayNotifyWnd), nothing of its XAML content, so 0 tray icons. And even when it works, UI Automation can only see icons that are on screen: Raymond Chen (The Old New Thing, 2025-09-29) explains that the icons in Windows' "Show hidden icons" menu exist for UI Automation only while that menu is open. Here that would be 3 of 44 icons. So UI Automation is a dead end for this grid; Sam chose option B.

### Part 3 - the tray received straight from the apps (option B)

**Status: built and ran (r25); works, two problems** (see "Part 3 result" below).

- **Machitan receives the tray icons itself** (`Shell/TrayIconService.cs`, rewritten; same file name, so nothing to delete). Apps add, change and remove their icons with Windows' `Shell_NotifyIcon`, which looks for the taskbar's window by its class name ("Shell_TrayWnd") and sends it the icon. Machitan now has a hidden window with that class name, on its own thread, kept in front of Explorer's, so these messages reach Machitan first. This is how Zebar's systray-util does it.
- **Explorer keeps working as before**: every message that reaches Machitan's window is passed on to Explorer's own taskbar window, unchanged, and Explorer's answer goes back to the app (also the app bar messages, Machitan's own bar and strip included). Explorer's tray is complete when Machitan stops. If Machitan crashes, its window goes with it and apps reach Explorer directly again.
- **All icons**, also those in Windows' "Show hidden icons" menu. Icons an app hides itself stay out. At start (and again 2 s after Explorer restarts) Machitan tells all apps "the taskbar was created", as Explorer does when it starts, so they send their icons again. An app that ignores that message appears once it next updates its icon fully.
- **Live pictures and tooltips**: each picture is copied the moment the app hands it over (apps often throw their icon away right after), shown at Windows' own tray size (16). Changes show within a fraction of a second; a changed picture updates only its own tile.
- **Clicks as on Windows' own tray**: left click, double click (Dynaland waits Windows' double-click time after a left click, then closes), right click (Dynaland closes first; the app's menu opens at the pointer). The app is allowed to come to the front, so its menu closes when you click elsewhere. When an app asks where its icon is (to place its own pop-up), the answer is the pointer.
- Icons whose app ended without removing them are dropped within 2 s.
- **Explorer's taskbar is hidden the plain way again** (`Shell/ExplorerTaskbar.cs`): the empty-region trick was only for UI Automation. Explorer's taskbar window is now told apart from Machitan's own by its process. Restoring still removes any empty region an r24 run may have left.
- **FlaUI.Core and FlaUI.UIA3 removed** from `Machitan.csproj` (and Windows Forms with them). No new packages.
- New: `Interop/TrayInterop.cs` (the Windows calls for the window), `IconReader.FromIconHandle`.
- The log: "Tray: asked all apps to send their tray icons again", then about 6 s after start "Tray (first look): ..." with the count and the icons' names.

**To check:**

1. Clean build (VS drops the two packages by itself); 0 warnings.
2. Launch. Explorer's taskbar stays hidden; Machitan's bar, strip and auto-hide work as before.
3. Dynaland's general page: the Tray rows with your apps' icons, the right pictures and tooltips (hidden ones too).
4. Left click, double click and right click a few icons: each does what it does on Windows' own tray; menus open at the pointer and close when you click elsewhere.
5. Start an app with a tray icon, then quit it: its tile comes and goes.
6. Exit Machitan: Explorer's taskbar comes back with all its tray icons.
7. Send the "Tray (first look)" log lines either way.

### Part 3 result (Sam, 2026-11-18)

The tray works: the log's first look had 7 icons (Tray 2048, Headphones: 48%, Battery status, one without a tooltip, ZapFast, UniGetUI, Steam), Machitan's window stayed in front of Explorer's on its own, Explorer's taskbar window found. Two problems (Sam: "some still hidden while some intended to not showed up (volume and battery) shows"):

- **Volume and battery showed.** These are Windows' own old icons: Explorer still makes them, with no way to click them (the log: "Headphones: 48%" takes no clicks), and Windows 11 shows them in its quick settings button, not in the tray.
- **Some apps' icons stayed missing.** Windows sends a broadcast only to top-level windows; an app whose tray icon lives on a message-only window never hears "the taskbar was created", so it sends nothing until it next changes its icon fully. (Zebar has the same gap on Windows 11; its other way, reading Explorer's old icon list, exists only on Windows 10.)

### Part 4 - Windows' own icons left out; message-only windows asked too

**Status: built and ran (r26); volume left out, battery still shown, Windows Security missing** (see "Part 4 result" below).

- **Windows' own icons are left out** (`Shell/TrayIconService.cs`): an icon made by Explorer's own process that takes no clicks (volume, battery and the like) does not show in Dynaland. Explorer's icons that do take clicks (for example "Safely remove hardware") still show, as on Windows' own tray. An icon whose app later gives it a way to click shows from then on.
- **Apps on message-only windows are asked too**: after the broadcast, the "taskbar was created" message also goes to each message-only window directly (they ignore it unless they are waiting for it), so those apps send their icons as well. The same happens 2 s after Explorer restarts.
- **The log tells more**: the first look now lists each shown icon with its program (and "takes no clicks" where so), the left-out Windows icons, and the running programs Windows has tray records for that sent no icon (yet); after that, each icon that arrives is noted ("Tray: icon added: ...").

**To check:**

1. Build (0 warnings), launch.
2. Dynaland's general page: no volume or battery tile; the icons that were missing now there.
3. Clicks as before.
4. Send the "Tray (first look)" lines (and any "Tray: icon added" lines), especially if an icon is still missing: they name the programs that sent nothing.

### Part 4 result (Sam, 2026-11-18)

The log: 358 message-only windows asked; 5 icons shown (Explorer's battery icon without a tooltip, Claude, ZapFast, Task Manager, Steam), 12 hidden by their apps. This time Explorer's battery icon came WITH a way to click it, so the "no clicks" rule missed it. Running programs with a Windows tray record that sent nothing: AnyDesk, Firefox, Office Click-to-Run, Windows Security (SecurityHealthSystray.exe), and Machitan itself (an old record). Windows Security is known not to send its icon again when the taskbar restarts (users report it stays gone even after restarting Explorer; running SecurityHealthSystray.exe again brings it back). Sam also asked: keep Dynaland open after right-clicking a tray icon.

### Part 5 - battery left out by ID; Dynaland stays open for a tray menu

**Status: GREEN (Sam, 2026-11-18: "everything works fine"). Batch 6 GREEN.**

- **Windows' volume, network and battery icons are left out by the IDs Windows gives them** (`Shell/TrayIconService.cs`), whether or not they can be clicked; Explorer's other icons with no way to click are still left out too.
- **Right-click on a tray tile keeps Dynaland open** (`Widgets/DynalandWindow.cs`): the app's menu opens at the pointer and Dynaland stays while it is up (it waits 1.5 s for the menu to appear, then as long as one of Windows' standard menus is on screen) and does not put itself on top of the menu meanwhile. When the menu closes, Dynaland closes as usual if the pointer is away. Left click unchanged.
- **The log, to find out about Windows Security**: for 10 s after Machitan asks the apps, every tray message is listed in one block ("Tray: what the apps sent in the 10 s after Machitan asked ..."): add / change / remove, the program, the icon's ID, and what Machitan did with it (including changes it skipped). The first look also lists Explorer's own icons with their IDs. Machitan no longer lists itself among the programs that sent nothing.

**To check:**

1. Build (0 warnings), launch.
2. No battery tile (nor volume or network).
3. Right-click a tray tile: Dynaland stays while the menu is open; picking an item or clicking elsewhere closes the menu; then Dynaland behaves as usual.
4. Send the two log blocks ("Tray (first look)" and "Tray: what the apps sent ...").

### Part 5 result - Batch 6 GREEN (Sam, 2026-11-18)

Sam: "everything works fine." The log (r27) confirms:

- Windows' own icons by ID: battery {7820AE75-23E3-4229-82C1-E41CB67D5B9C} ("Battery status: ..."), left out; volume {7820AE73-23E3-4229-82C1-E41CB67D5B9C} ("Speakers: Muted"). Explorer also removed two icons it had not re-added ({6DA68F06-00F1-4E6E-8158-7F1FFD4F9DB9}, {7820AE82-23E3-4229-82C1-E41CB67D5B9C}).
- Windows Security (SecurityHealthSystray.exe, ID {BA82E2DC-F405-47AD-B032-CF0FAA0E3933}) does not add its icon again when asked; it only sends status changes without the message for clicks (flags 0xAE), which Machitan skips, as it could not pass clicks on. Its icon arrived in full later ("Tray: icon added: Windows Security - No actions needed.", 22:03) and showed. So an app like this appears in Dynaland once it next sends its icon in full.
- Right-click menus keep Dynaland open; clicks, double clicks and pictures work; Machitan's window never had to step in front of Explorer's.

## Batch 7 - Custom mode, radii, wallpaper accent

Plan (handoff): custom mode (toolbar, right-click menu per segment, 2x2 variant picker with carousel, x badge removal), dragging on the bar at any time (segments to any position incl. the far edges; icons between segments) with persistence, one radii scale (16/10/8) defined once, and the wallpaper-adaptive accent. Built in parts. Sam also asked (2026-11-18): the Show Desktop strip makes the dock pill slightly asymmetrical; the dock should be completely hidden when there is no app on the bar.

### Part 1 - Show Desktop balanced, empty dock hidden, radii scale, wallpaper accent

**Status: delivered, waiting for build and run report.**

- **The dock pill is symmetrical again** (`Bar/BarWindow.Layout.cs`): the Show Desktop strip (2 DIP line in a 12 DIP hit area, at the far right end on the normal padding, as the spec says) is balanced by an empty space of the same width at the pill's left end, so the icons sit in the middle with equal room on both sides. Same in Joined mode (one pill). Classic (full width) unchanged.
- **No app, no dock**: with no app on the bar (none pinned, none running), the dock and Show Desktop go: the dock's whole pill in Segmented (only the Start pill is left), the divider and the strip in Joined and Classic. They come back with the first app. (A minimised app still counts as an app.)
- **One radii scale, defined once** (`Visuals/Radii.cs`): Large 16 (Start boxes, flyouts, Classic bar, preview cards), Medium 10 (Settings cards and the like), Small 8 (small buttons and inner rows); capsules stay fully rounded; icon tiles a quarter of their size. The Start menu, flyouts, preview cards, Settings and Dynaland's tiles now take their radii from it. Changed values: the window preview card 12 -> 16 (a large surface), its close button and picture 6 -> 8 (concentric with the card's 8 DIP padding). Kept on purpose: the bar's hover boxes (concentric with the pill, Sam's earlier choice), the capsule ring and info line, Dynaland's notch/island shapes.
- **Wallpaper accent** (`Visuals/AccentService.cs`, new): Machitan's accent colour now follows the wallpaper: the picture Windows shows on the main screen is read natively (the same reader as the status ring) and its strongest colour taken, lightened to read on dark surfaces (the same pick as Dynaland's track colour). A plain wallpaper colour counts too. When the wallpaper has no real colour (black and white, grey) or cannot be read, it is Windows' accent colour. It follows wallpaper changes and Windows' accent changes. Used for: the focused app's line on the bar, Show Desktop while the desktop is shown, the strip cover's "Accent colour", and the Start menu's power confirm button (with black or white text, whichever reads better). The log says which colour and from where ("Accent colour: #...").
- **New setting** (Settings > Look > Accent colour): "From the wallpaper" (default, as the spec says) or "Windows' accent colour".

**To check:**

1. Build (0 warnings), launch.
2. The dock pill: equal room left of the first icon and right of the last (with Show Desktop's line in the right end).
3. Close or unpin every app: the dock pill goes completely, Start's pill stays; open an app: it comes back. In Joined mode the divider and strip go.
4. The focused app's line (and Show Desktop while the desktop is shown) in the wallpaper's colour; change the wallpaper: it follows within a few seconds. Settings > Look > Accent colour > Windows' accent colour: back to Windows' colour.
5. Corners: Start menu, flyouts, previews look right (the preview card a bit rounder).
6. Send the "Accent colour:" log line.

### Part 2 - Sam's list: search box, account picture, End task, Settings by category

Sam (2026-11-18): match the bar's search box to Start's; add size options for the info line, Dynaland and the ring; make Start's profile picture open Windows' Settings; trim Machitan's Settings wording with the antislop skills and sort the options into categories like Seelen UI; add End task to the bar's app menu. The size options come in the next part (they change how the widgets and the edge strip are placed). Part 1's report is still to come.

**Status: delivered, waiting for build and run report.**

- **The bar's search box is built like Start's** (`Bar/BarWindow.Search.cs`): a capsule with a light fill and a bare text field inside (no border, no fill of its own, no focus underline), the same inner margin; "Search apps" as its hint (Start's longer hint does not fit 180 DIP). A press anywhere in the capsule starts typing.
- **Start's picture and name open Windows' Settings** at Accounts > Your info (`ms-settings:yourinfo`); Start closes first (`Start/StartMenuWindow.xaml`, `.xaml.cs`). Tooltip "Account settings".
- **End task** in the bar's right-click menu for a running app (`Bar/BarWindow.Dock.cs`, `Shell/TaskService.cs`, `Interop/Kernel32.cs`): ends the app's processes at once, as Task Manager's End task. The processes: those owning the app's windows; for a store app, the one behind the window's content (its frame belongs to ApplicationFrameHost, which hosts every store app); for any packaged app, every process of its package. Never Explorer, ApplicationFrameHost or Machitan. An app running as administrator cannot be ended from here; the log says so ("End task: ...").
- **Settings by category** (`SettingsUi/SettingsWindow.xaml`, `.xaml.cs`): a sidebar like Seelen UI's, one page per category: General (theme, accent, acrylic, opacity), Taskbar (layout, size, search), Corner widgets (info line, weather place, status ring), Dynaland, Edge strip (keep the edge clear, cover), Shortcuts (hotkey status, Win+N calendar), Privacy, About (version, licence, and the planned options, off and labelled). The window is wider (980 DIP) for the sidebar. Every description was rewritten shorter with the antislop and antislop-copywriting rules (mode: during): no filler, no repeated explanations, plain verbs, the facts kept (privacy text unchanged in substance).

**To check:**

1. Build (0 warnings), launch.
2. Search box mode: the bar's box looks like Start's (no border); click it and type: results as before.
3. Start: click the picture or name: Start closes, Windows' Settings opens at Your info.
4. Right-click a running app on the bar: End task ends it (try a normal app; a store app such as Calculator ends without other store apps closing).
5. Settings: the sidebar pages; every option still works and is where you would look for it; the icons beside the categories make sense (they are Segoe Fluent glyphs: tell me if one shows something odd).

#### antislop Delivery Gate (during mode), Part 2 UI

Design read: a settings window for a desktop shell, for one power user who tweaks often, in Windows 11's own Fluent language with Seelen UI's category sidebar (Sam's direction), dial ENERGY 1 / RHYTHM 1 / MOTION 1. Desktop WinUI, not a web page: web-only items are marked N/A with the reason.

Block 1, Hard Gate (all must be no):
- R-02 PASS: no em dash or " -- " in any changed file (checked: every changed file is pure ASCII).
- R-03 N/A: a desktop window, no mobile layout; the content column keeps a 760 DIP max width and wraps text.
- R-17 PASS: no statistics shown; the only numbers are real settings values and the real Windows build.
- R-18 PASS: no testimonials.
- R-23 PASS: no new assets; the account picture is Windows' own (PersonPicture); the sidebar title is the product name as text.
- R-24 PASS: every sidebar item opens a page that exists.
- R-25 PASS: WinUI theme text colours; descriptions at 70% opacity on the card colours used since Batch 3 (white on near-black in dark, black on near-white in light), above 4.5:1.
- R-26 PASS: no dead controls; the planned options are disabled and labelled "Not built yet" (About > Planned).
- R-27 PASS: hotkey status shows its real state or "not started"; the weather place row has its looking-up, failed and in-use states; End task logs refusals.
- R-28 N/A: no FAQ.
- R-32 PASS: NavigationView, buttons, toggles, combo boxes and sliders are keyboard controls with system focus visuals; the account button and search capsule take Tab and Enter.
- R-33 PASS: written in source; no patch scripts in the project.
- R-34 PASS: the page is rebuilt on a theme change; card colours have a dark and a light set.
- R-35 NOT RUN: Claude cannot build or run on this PC; verified by code inspection and a C# parser check of every file. Sam's build and click-through are the run check.
- R-36 PASS: no claims beyond what the code does (privacy text describes the real network calls).
- R-37 PASS: direction from Sam (Seelen UI categories) on top of the existing Fluent look; dials set above.
- R-38 PASS: no fabricated content.
- R-39 PASS: every control has an accessible name (sidebar items by their text, the account button "Account settings", the search box "Search apps, or type files: to search files"); hover, pressed and disabled states are WinUI's own.
- R-40 N/A: no web media or scripts.

Block 2, Purpose-Gate:
- R-01 PASS: no gradients or glows added.
- R-04 PASS: sidebar icons chosen for their category: gear (General), app grid (Taskbar), clock (Corner widgets: the info line's time), audio (Dynaland: music first), monitor (Edge strip: a screen edge), keyboard (Shortcuts), lock (Privacy), info (About).
- R-06 PASS: Segoe UI Variable, Windows' own type, as everywhere in Machitan.
- R-07, R-08, R-09, R-13, R-14, R-19, R-22 PASS: no background patterns, decorative arrows, badges, glows, card grids, animations beyond WinUI's own, or illustrations added.
- R-10 PASS: Mica on the window only (as before).
- R-12 PASS: no shadows added.

Block 3, Liveliness:
- Dials declared (ENERGY 1 / RHYTHM 1 / MOTION 1) and kept: uniform card pages, by choice, as Windows' Settings.
- Focal point per page: the page title. Whitespace: section headers and card spacing separate groups.
- One accent: the sidebar's selection marker (Windows' accent).
- Identity motif: the rounded cards with hairline separators, shared with the flyouts.
- Design read declared above.

Block 4, Craftsmanship and Quality Locks:
- C-1 PASS: every page and grouping follows where a user looks for the option (Sam's Seelen UI reference).
- C-2 PASS: every control works (same row helpers as before, same settings keys).
- C-3 PASS: one page per real category; no filler pages.
- C-4 PASS: both themes, keyboard; the window keeps its size per DPI.
- C-5 PASS: no claims.
- R-05, R-11 (radii from Radii.cs), R-15 ("Open Machitan's folder", "Find", "End task" name their action), R-16, R-20, R-21 (theme setting kept), R-29, R-30 (not a clone; Windows' own control set), R-31 (reasons above) PASS.

### Part 3 - size options for the info line, the status ring and Dynaland

Sam (2026-11-18): "add size customization for info line, dynaland, and ring"; the plan (each widget keeps its design and gets one size factor) approved.

**Status: delivered, waiting for build and run report.**

- **Three new settings**, 75% to 150% in steps of 5, default 100%: Info line size and Status ring size (Settings > Corner widgets, under each), Dynaland size (Settings > Dynaland). Saved as `InfoLineSize`, `DuoRingSize`, `DynalandSize` (percent).
- **How it works**: each widget is laid out at its standard size and scaled as a whole (text, icons, ring, dots, pill, pages, tray tiles), so its proportions never change. Its window, its place in the corner row, the strip at the free edge, and the cut-out that lets clicks through all follow the size (`Widgets/CornerWidgetWindow.cs`, `Widgets/DynalandWindow.cs`, `Visuals/WindowCutter.cs`: corner radii scale too). Margins to the screen edge, the bar and between widgets stay as they are.
- **Mixed sizes**: two widgets of different sizes in one corner stay side by side, each against the screen edge (or centred on the bar when they sit in its band). The strip at the free edge is as tall as the tallest widget there (8 + its height + 8 DIP), and at least what Dynaland needs at its size (`Shell/ShellHost.cs`).
- The status ring reads the wallpaper under its real (scaled) place on screen (`Widgets/DuoRingWindow.cs`). Dynaland's pointer checks (when it opens and closes) use its scaled pill.

**To check:**

1. Build (0 warnings), launch.
2. Settings > Corner widgets: move each Size slider: the info line and the ring grow and shrink in place, sharp, still side by side; maximised windows keep clear of them (the strip grows with the tallest one).
3. Settings > Dynaland > Size: the pill and its pages grow and shrink; hovering opens and closes it as before at every size; the tray tiles still click.
4. Text at 150%: sharp or soft? (WinUI usually redraws text at the new size; tell me if it looks blurry.)

#### antislop Delivery Gate (during mode), Part 3 UI

Design read: three size sliders on existing Settings pages, for one power user, Windows 11 Fluent controls; dial ENERGY 1 / RHYTHM 1 / MOTION 1 (no new motion). Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS (no em dash; ASCII-only files). R-03 N/A (desktop). R-17, R-18, R-28, R-36, R-38 PASS (no numbers beyond the real setting values, no claims). R-23 PASS (no new assets). R-24 PASS (no new navigation). R-25 PASS (WinUI slider and text colours). R-26 PASS (each slider changes a real setting and the widget follows). R-27 PASS (settings outside 75..150 are clamped on load). R-32 PASS (sliders take arrow keys; accessible names are their labels). R-33 PASS (source only). R-34 PASS (both themes use the same controls). R-35 NOT RUN (Claude cannot run on this PC; verified by code inspection and a C# parser check; Sam's build is the run check). R-37 PASS (Sam's direction and approved plan). R-39 PASS (labels and value text on every slider). R-40 N/A.
- Block 2: R-01, R-04, R-06, R-07, R-08, R-09, R-10, R-12, R-13, R-14, R-19, R-22 PASS (nothing decorative added).
- Block 3: dials declared and kept; focal point per page unchanged (the page title); one accent (the slider's Windows accent); identity motif unchanged (the cards).
- Block 4: C-1 to C-5 PASS (each slider has one job, scales the real widget, no filler); R-11 PASS (radii scale with the widget, so its shape stays the same); R-15, R-16 PASS (labels say "Size" and what it scales); R-31 PASS (reason: one factor per widget keeps its design intact at every size).

### Part 4 - Dynaland: width setting, working seek bar, titles pause with the music

Sam (2026-11-18): "yes" (the custom-mode variant lists are approved too); "add dynaland width customizer, and make the dynaland's music slider functional. and also, if the music's paused, make the scrolling text paused too." Dragging on the bar comes next.

**Status: delivered, waiting for build and run report.**

- **Width** (Settings > Dynaland > Width, 360 to 560 DIP in steps of 10, default 400): the open player and general page are this wide (at 100% size; the Size setting scales it too). The window, the cut-out, the pill's centre and the tray rows (as many tiles per row as fit) follow, live (`Widgets/DynalandWindow.cs`: the stage width is a setting now, not a constant). Saved as `DynalandWidth`.
- **The player's timeline is a seek bar**: point at it and a thumb shows; press or drag to pick a place (the times follow the pointer), and the playing app jumps there on release (`MediaService.SeekAsync`, Windows' media controls). Only when the app allows seeking (`MediaSnapshot.CanSeek`, from the app's own controls); otherwise the bar stays a display, without a thumb. Dynaland stays open while dragging, even past its edge. Right after a seek the bar holds the chosen place for 1.5 s, until the app reports its new position.
- **Paused music, paused titles**: the scrolling title, artist and the small pill's text stop where they are while the music is paused, and go on from there when it plays (`Visuals/Marquee.cs`: `Paused`).

**To check:**

1. Build (0 warnings), launch.
2. Settings > Dynaland > Width: the player and general page get wider or narrower live; centred; tray rows refill.
3. Play music; open the player; point at the timeline: a thumb; drag it: the times follow; let go: the music jumps there. Try an app that cannot seek (some browsers' radio streams): no thumb, nothing happens.
4. Pause: the scrolling title and artist stop; play: they go on.

#### antislop Delivery Gate (during mode), Part 4 UI

Design read: Dynaland's black player page (existing design) gains a seek thumb, and Settings gains one slider; dial ENERGY 1 / RHYTHM 1 / MOTION 1 (the only motion added is the thumb following the pointer). Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS (ASCII only). R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (times shown are the app's own). R-23, R-24 PASS. R-25 PASS (white thumb and the accent fill on black). R-26 PASS (the seek bar seeks; where the app cannot seek it shows no thumb and takes no press, so it does not pretend). R-27 PASS (no timeline: hidden; cannot seek: display only; refused seek: logged, the bar returns to the app's position). R-32 N/A for Dynaland (a hover window that never takes the keyboard, as before; the Settings slider takes arrow keys). R-33, R-34 PASS. R-35 NOT RUN (Claude cannot run on this PC; parser check and code inspection; Sam's build is the run check). R-37 PASS (Sam's direction). R-39 PASS (the seek area is named "Playback position"; hover shows the thumb). R-40 N/A.
- Block 2: no gradients, glows, badges, decoration added (PASS).
- Block 3: dials kept; focal point on the player page unchanged (the cover and title); one accent (the track colour on the bar); identity motif unchanged.
- Block 4: C-1 to C-5 PASS; R-11 PASS (the thumb is a circle, as Windows' own sliders); R-31 PASS (thumb only on hover: the bar stays as calm as before until it is used).

### Part 5 - dragging on the bar

Sam (2026-11-18): "continue" (after Part 4). Spec: dragging works on the bar at any time; a whole segment drags to any position, far edges included (an edge drop aligns the bar group, except Classic); icons drag between segments; while dragging, the element follows the cursor, the segment under it is outlined, an accent marker shows the drop slot; results are saved.

**Status: delivered, waiting for build and run report.**

What can be dragged (press, then move a few pixels; a plain click works as before):
- **Start** moves its whole group. Drop it beside the apps group: the order changes (Start first or apps first). Drop it near the left or right screen edge (the outer sixth): the bar lines up with that edge, Start on that side. Drop it in open space away from the edges: the bar is centred. In Joined the same, inside the one pill. In Classic (full width) Start goes to the left end or the right end (before Show Desktop); there is no edge drop.
- **The apps group** (Segmented, where it is its own pill) drags by its empty space (the pill's ends, the gaps): same rules as Start.
- **The search icon** moves to one of three places: with Start, before the apps, or after the apps (the nearest place in reach). The search box is a text field, so it does not drag; its place follows the same setting.
- **An app** moves among the pinned apps (this is the one pin order, so Start's Pinned box follows). A running app that is not pinned is pinned where it is dropped. A store app that is running but not pinned cannot be pinned from the bar (as before), so it does not drag.

While dragging: a copy of the piece follows the pointer along the bar (it can lift a little), the piece itself is faded in its place, the group it would go into is outlined (white on dark, black on light), and a short line in Machitan's accent marks the drop place (for an edge or centre drop: where the bar will line up). Esc, or letting go well above the bar (the copy fades when there is nowhere to go), puts it back. The bar does not change under a drag: apps that open or close meanwhile show up when it ends.

Saved: the order (`SegmentOrder`: StartFirst / AppsFirst) and the search place (`SearchPlace`: WithStart / BeforeApps / AfterApps) in settings.json, the alignment in `DockAlignment` (as before), the pin order in pins.json. Settings > Taskbar has the same choices (Layout: Order; Search: Place), so all of it can be set without a mouse.

Also fixed on the way: with no app on the bar, the hidden apps pill stopped the bar's window cut from updating (it waited for the pill to have a size), so the cut could stay at an old shape. Hidden pills are now left out of the cut.

Files: `Bar/BarWindow.Drag.cs` (new: presses, the drag, drop places, saving), `Bar/BarDragLayer.cs` (new: the see-through window that draws the copy, outline and line; one band along the bar, the whole screen wide, shown only during a drag), `Bar/BarWindow.Layout.cs` (order, search place; Show Desktop always in the right-hand pill; pills take presses on their empty space), `Bar/BarWindow.Dock.cs` (apps drag; no previews during a drag), `Bar/BarWindow.xaml.cs` (dock updates and placement wait for a drag; region fix; auto-hide stays up during a drag), `Shell/PinStore.cs` (`PinAt`, `Move`), `Shell/ShellHost.cs` (the drag layer closes on shutdown; a drag is cancelled first), `Config/ShellSettings.cs`, `Config/SettingsStore.cs`, `SettingsUi/SettingsWindow.xaml.cs`, `Interop/User32.cs` (mouse button constants), README.

Left for later (decide with Sam): app icons into the Start group (the spec's example moves an icon from the left side into the dock; here the search icon does that, and the Start group holds only Start and search); reordering running apps that are not pinned (they follow the open order, as before).

**To check:**

1. Build (0 warnings), launch.
2. Segmented, bar centred: drag Start to the right of the apps pill: Start goes after the apps (Show Desktop moves to Start's pill, at the far right). Drag it back.
3. Drag Start to the far left of the screen: the bar lines up at the left. Drag it to the middle of the screen: centred again. Same at the far right.
4. Drag the apps pill by its empty end: same as Start.
5. Drag the search icon to the end of the apps: it sits there; back next to Start.
6. Drag a pinned app between two others: the order changes, also in Start's Pinned box. Drag a running app that is not pinned in among the pinned ones: it is pinned there.
7. While dragging: copy follows, the group under it is outlined, an accent line marks the place. Esc puts it back. Letting go far above the bar puts it back.
8. Joined and Classic: drag Start to the other side.
9. Restart Machitan: everything is where you left it.

#### antislop Delivery Gate (during mode), Part 5 UI

Design read: the bar's existing look (see-through pills, icon-only buttons, Machitan's accent) gains three drag cues: a lifted copy, an outline, an accent line. Dial ENERGY 1 / RHYTHM 1 / MOTION 1 (the only motion is the copy following the pointer; no animations added). Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS (ASCII only). R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (no data shown). R-23, R-24 PASS (no assets). R-25 PASS (outline 200/255 white on dark, 170/255 black on light; the accent line over the bar as the focused-app line already is). R-26 PASS (every cue matches a real drop: the line is where the piece goes; no line, faded copy = it goes back). R-27 PASS (nowhere to go: put back; no layer window: the drag still works, without cues; picture failed: the copy shows without its picture; capture refused: the mouse button decides). R-32 PASS (every drag result has a keyboard path: Settings > Taskbar > Order, Alignment, Search > Place; the pin order: not yet by keyboard, noted). R-33, R-34 PASS. R-35 NOT RUN (Claude cannot run on this PC; parser check and code inspection; Sam's build is the run check). R-37 PASS (Sam's spec). R-39 PASS (drag sources keep their names; no new controls without names). R-40 N/A.
- Block 2: no gradients, glows, badges or decoration added (PASS).
- Block 3: dials kept; focal point while dragging = the accent line; one accent; identity motif unchanged.
- Block 4: C-1 to C-5 PASS; R-11 PASS (outline follows each pill's own corners); R-31 PASS (cues exist only during a drag).

### Part 6 - Sam's report on Parts 4 and 5; app groups (new bubbles)

Sam (2026-11-19): "1. the width slider does nothing. 2. only windows media player has slider, WACUP isn't. fix this. 3. make unpinned apps draggable as well. 4. as you can see in the 'Segmented' mode, there's 2 bubbles (start+search) and opened apps. make an ability to make a new bubble (app groups), simply by pressing ctrl + clicking the apps that want to move to new bubble, and then right click and choose 'group apps'. those bubbles can be dragged everywhere, just like existing bubbles."

**Status: delivered, waiting for build and run report.**

1. **Dynaland width.** The log showed every slider move arriving and being applied, but the width only shows on the open player and general page, and Dynaland stays closed while Settings is in front, so nothing visible changed. Now: moving the slider opens Dynaland for 4 s to show the new width (the player while music plays, else the general page), and the closed music pill may grow with it too (270 DIP at the standard 400, so 243 to 378; its scrolling text 160 DIP, so 144 to 224). Each change is logged with the width (`Dynaland: width ... DIP`). (`Widgets/DynalandWindow.cs`, `Visuals/Marquee.cs`: `SetMaxWidth`.)
2. **Seek bar for WACUP.** WACUP tells Windows' media controls it does not take seeking, so the bar had no thumb. WACUP still answers Winamp's message interface on its main window (class "Winamp v1.x", seen in the log). Now, for a media session whose app owns such a window (matched by its program's name; also Winamp, AIMP): its position and track length are read from there (fresh after every change), and seeking goes there when the app does not take it through Windows' controls. Windows Media Player and others are unchanged. (`Interop/WinampInterop.cs` new: messages 105 IPC_GETOUTPUTTIME, 106 IPC_JUMPTOTIME, 150 ms limit, numbers only; `Shell/MediaService.cs`.)
3. **Any app drags**, pinned or not, running store apps too. Dropping no longer pins (pin from the right-click menu): an app keeps its pinned or unpinned state and just takes the new place. The bar's order is its own now (bar-layout.json); Start's Pinned box keeps the pin order.
4. **App groups (new bubbles).** Ctrl+click apps on the bar to pick them (an accent ring marks each; Ctrl+click again to drop one; a plain click on an app, or on Start, clears the picks). Right-click one of them: **Group N apps** puts the picked apps (with the one right-clicked) into a new bubble, just right of the bubble the right-clicked app is in. With nothing picked, the menu offers **Move to a new group** for that app alone. In a group's app menu: **Ungroup** (its apps go back to the end of the apps bubble). Every bubble drags by its empty space in Segmented (like the apps bubble), between the others or to a screen edge; apps drag between bubbles. A bubble with no app showing (all closed, none pinned) is hidden and comes back with its apps. Joined shows the bubbles in one pill with dividers; Classic puts the app bubbles side by side (dividers between) and Start at the left end, or at the right end when it is not first.

How it is kept: `bar-layout.json` (new, `Shell/BarLayoutStore.cs`): `Order` (bubble IDs from the left: `start`, `apps`, `group-1`...) and `Bubbles` (each app bubble's apps, by pin path or running app's path, in order). An app with no place yet sits in the apps bubble after the placed ones (pins in pin order, then running apps in opening order). An app is in one bubble only. Same safety rule as pins: an unreadable file is left untouched and not overwritten that run. The `SegmentOrder` setting from Part 5 is gone (the order is in this file now; its Settings row too); a stale `SegmentOrder` line is removed from settings.json on the next save.

Files: `Shell/BarLayoutStore.cs` (new), `Core/AppPaths.cs`, `Shell/ShellHost.cs` (`BarLayout`, loaded with the pins), `Bar/BarWindow.Layout.cs` (bubbles per mode), `Bar/BarWindow.Dock.cs` (bubbles filled in place, Ctrl+click picks, menu items), `Bar/BarWindow.Drag.cs` (any number of bubbles; apps between bubbles), `Bar/BarWindow.xaml.cs` (layout changes rebuild the bar), `Config/ShellSettings.cs`, `Config/SettingsStore.cs`, `SettingsUi/SettingsWindow.xaml.cs`, `Interop/WinampInterop.cs` (new), `Shell/MediaService.cs`, `Widgets/DynalandWindow.cs`, `Visuals/Marquee.cs`, README.

**To check:**

1. Build (0 warnings), launch.
2. Settings > Dynaland > Width: Dynaland opens and grows or shrinks as you move it, then closes after 4 s.
3. Play in WACUP, open the player: a thumb on the timeline; drag it: WACUP jumps there. Times follow WACUP.
4. Drag an unpinned running app to another place: it moves and stays unpinned.
5. Ctrl+click two apps (rings show), right-click one: Group 2 apps: a new bubble right of the apps. Drag it by its empty end to the left of Start, then to a screen edge.
6. Drag an app from the apps bubble into the group and back.
7. Right-click an app in the group: Ungroup: the apps go back.
8. Close every app of a group (unpinned): the bubble goes; open one: it comes back in its place.
9. Restart Machitan: bubbles and places stay.

#### antislop Delivery Gate (during mode), Part 6 UI

Design read: the existing bar look; new: more pills of the same kind, a selection ring (accent, 2 DIP, faint accent tint), three menu items; Dynaland opens briefly while its width changes. Dial ENERGY 1 / RHYTHM 1 / MOTION 1 (the width preview uses Dynaland's existing open and close motion; nothing new). Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS (ASCII only). R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (timeline numbers are WACUP's own answers). R-23, R-24 PASS. R-25 PASS (accent ring over the icon's own background; screen reader name adds "selected"). R-26 PASS (the seek thumb shows only where seeking reaches the player: Windows' controls or Winamp's messages; the width preview shows the real Dynaland). R-27 PASS (player does not answer: no Winamp timeline, the bar falls back to Windows' report; jump refused: logged, the bar returns to the player's position; layout file unreadable: standard order, not overwritten). R-32 PARTIAL: picking with Ctrl+click and grouping are mouse actions on the bar, which never takes the keyboard (as before); the bubbles' order no longer has a Settings row; a keyboard path belongs with custom mode (Reset there). R-33, R-34 PASS. R-35 NOT RUN (Claude cannot run on this PC; parser check and code inspection; Sam's build is the run check). R-37 PASS (Sam's direction). R-39 PASS (menu items name their action and count). R-40 N/A.
- Block 2: no gradients, glows, badges or decoration added (PASS).
- Block 3: dials kept; one accent; identity motif unchanged.
- Block 4: C-1 to C-5 PASS; R-11 PASS (ring follows the button's own corners); R-31 PASS (rings only while picking).

### Part 6b - Dynaland's closed pill follows the width too

Sam (2026-11-19): "oh i see... you only tweaked the hovered pill's width. tweak the idle state's width too as well."

**Status: delivered, waiting for build and run report.**

- The closed pill with nothing playing now follows the Width setting: 110 DIP at the standard 400, so 99 to 154 (as the music pill already did: 243 to 378). It changes live as the slider moves; the open pages show their width on the next hover. The 4 s opening while the slider moves (Part 6) is gone, so the closed pill is what you see change. (`Widgets/DynalandWindow.cs`: `_idleWidth`; `SettingsUi/SettingsWindow.xaml.cs`: the row's description.)

**To check:** Settings > Dynaland > Width with nothing playing: the closed pill gets wider or narrower as you move it. With music: the music pill too. Hover: the open player at the new width.

Gate (during mode): no new visuals; R-26 PASS (the closed pill shows the real setting); R-35 NOT RUN (Claude cannot run on this PC); everything else as Part 6.

### Part 7 - custom mode

Sam (2026-11-19): "do this too with Batch 7." Spec: Customize opens an edit mode with a top-right toolbar (back, auto-arrange, reset, add, checkmark); right-click a segment for a black menu (Settings / Remove; the tray also gets Customize); Settings opens a central picker, a 2x2 variant grid with '< 1 of N >' carousel controls, the same picker serving widgets; the selected element shows a white x badge at its top-right, clicking it removes the element; removals and variant choices are saved. Variant lists approved by Sam on 2026-11-18.

**Status: delivered, waiting for build and run report.**

How it works:
- **Customize**: right-click the bar > Customize, or Settings > Taskbar > Customize (which also opens the picker, for the keyboard).
- While on, a click picks instead of using: a bar bubble (Start, the apps, a group), Dynaland (it does not open) or a corner widget. The picked one gets a **white x badge** at its top-right; clicking it removes it. Dragging on the bar works as always. Auto-hide waits.
- **Right-click** any of them: a **black menu**: Settings (the picker on that element), Remove (for a group: ungroup), Done customizing.
- **Toolbar** (black capsule, top right of the bar's screen): **Back** (everything custom mode changed goes back to how it was on Customize: the settings below and bar-layout.json), **Auto-arrange** (Start first, then the apps, then the groups; the bar centred), **Reset** (the standard bar: no groups, standard order, centred, every element on, first variants; Back still undoes it), **Add** (the picker on the first element that is removed or off; dimmed when nothing is), **Done** (keep everything).
- **Picker** (middle of the screen): the element's variants as a 2x2 grid, the one in use outlined in the accent; '< N of 4 >' goes through Start, Apps, Dynaland, Corner widgets. A click on a variant applies it at once (and puts a removed element back). The previews are real: the bar's Start mark, the apps on the bar now with their own icons and names, the track playing now. It takes the keyboard: Tab and Enter, PageUp and PageDown for the carousel, Esc closes; Done leaves custom mode.

Variants and what removing does:

| Element | Variants | Remove |
|---|---|---|
| Start | Start + search icon / Start + search box / Start only (SearchMode) | `ShowStartOnBar` off |
| Apps | Icons only / Icons with labels (`DockLabels`, new) | `ShowAppsOnBar` off (groups stay); a group: ungroup |
| Dynaland | Music / Off (`EnableDynaland`) | Off |
| Corner widgets | Info line + Duo ring / Info line only / Duo ring only / None | the picked one off (`InfoLineEnabled`, `ShowDuoRing`) |

The bar keeps at least one bubble: Remove is dimmed on the last one (menu and badge say why). **Icons with labels**: each app's name (up to 120 DIP, trimmed) beside its icon; also Settings > Taskbar > App names.

**Not in this part: the Tray element** ("Hidden / Icons on the bar", and the tray's Customize item). The tray lives in Dynaland's general page; putting its icons on the bar means sharing the tray service between Dynaland and the bar, which touches the Batch 6 tray (green). That is the next part, so it is not offered in the picker yet (no option that does nothing).

Files: `Custom/CustomMode.cs` (new: state, Back snapshot, toolbar actions, remove, variants), `Custom/CustomMenus.cs` (new: the black menu), `Custom/CustomToolbarWindow.cs`, `Custom/CustomBadgeWindow.cs`, `Custom/VariantPickerWindow.cs` (new windows), `Shell/ShellHost.cs` (`Custom`, element positions, shutdown), `Shell/BarLayoutStore.cs` (`Snapshot`, `Restore`, `StandardOrder`, `ResetAll`), `Config/ShellSettings.cs` and `Config/SettingsStore.cs` (`DockLabels`, `ShowStartOnBar`, `ShowAppsOnBar`), `Bar/BarWindow.Layout.cs`, `Bar/BarWindow.Dock.cs`, `Bar/BarWindow.Drag.cs` (Start may be off the bar), `Bar/BarWindow.Search.cs`, `Bar/BarWindow.xaml.cs` (the bar's menu is built in code now: Customize / Settings / Exit), `Widgets/CornerWidgetWindow.cs`, `Widgets/InfoLineWindow.cs`, `Widgets/DuoRingWindow.cs`, `Widgets/DynalandWindow.cs`, `Interop/Monitors.cs` (`TryGetMonitorRectAt`), `SettingsUi/SettingsWindow.xaml.cs`, README.

**To check:**

1. Build (0 warnings), launch.
2. Right-click the bar > Customize: the toolbar at the top right. Click Start: the white x on its top-right. Click an app: its bubble is picked (the app does not open). Click Dynaland and the info line: each picked; Dynaland does not open.
3. Right-click a bubble: black menu; Settings: the picker; click "Icons with labels": names appear on the bar. '<' and '>' go through the four elements.
4. Click the x on the Duo ring: it goes. Add: the picker on Corner widgets; choose "Info line + Duo ring": back.
5. Remove Start, then try to remove the apps: dimmed (last bubble).
6. Reset: the standard bar. Back: everything as before Customize. Customize again, change something, Done: it stays after a restart.
7. Settings > Taskbar > Customize: the picker takes the keyboard (Tab, Enter, PageDown, Esc).

#### antislop Delivery Gate (during mode), Part 7 UI

Design read: custom mode follows the spec's concept: black surfaces (toolbar capsule, menu, picker card) with white glyphs and text, Machitan's accent only on the variant in use, a white x badge; radii from the scale (capsule toolbar, 16 picker card, 10 variant cards, 8 small buttons). Dial ENERGY 1 / RHYTHM 2 (a 2x2 grid of equal cards: the spec's) / MOTION 0 (nothing animates). Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS (ASCII only). R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (previews show real icons, names and the playing title; descriptions are plain facts). R-23, R-24 PASS (glyphs from Segoe Fluent Icons; the Start mark is the bar's own drawing). R-25 PASS (white on black; accent outline on the card in use plus "in use" in its name for screen readers). R-26 PASS (every control does what it says; Add is dimmed with a reason when nothing is removed; Remove is dimmed with a reason on the last bubble; the Tray element is left out until it works). R-27 PASS (window failures are logged and custom mode leaves cleanly; Back restores from a snapshot). R-32 PASS (Settings > Taskbar > Customize opens the keyboard-ready picker with Done; toolbar and badge are mouse extras over the same actions, except Back/Reset/Auto-arrange, which are mouse-only: noted). R-33, R-34 PASS. R-35 NOT RUN (Claude cannot run on this PC; parser check and code inspection; Sam's build is the run check). R-37 PASS (spec and Sam's approved variants). R-39 PASS (every button named, with a tooltip). R-40 N/A.
- Block 2: no gradients, glows, badges beyond the spec's x (PASS).
- Block 3: dials as stated; focal point = the picker's card in use; one accent.
- Block 4: C-1 to C-5 PASS; R-11 PASS (radii scale); R-31 PASS (custom-mode chrome only while custom mode is on).

### Part 8 - the tray on the bar; tray icons drag

Sam (2026-11-19): "add option to move tray to the bar, as another little bubble. make them draggable, both in bar and dynaland."

**Status: delivered, waiting for build and run report.**

- **Tray on the bar** (Settings > Taskbar > Tray on the bar; custom mode: the Tray element, "Hidden" / "Icons on the bar", the approved variants): the tray icons move into a bubble of their own on the bar, and Dynaland's general page drops its tray section. Clicks work as in Dynaland: a click and a double click (within Windows' double-click time) go to the app, a right-click opens the app's menu. The bubble shows while there are tray icons; it starts at the bar's right end (after the apps) and drags like any bubble (by its empty space in Segmented), and custom mode picks and removes it (Remove = back to Dynaland). In Joined it is a part of the one pill; in Classic it sits at the right end, before Show Desktop.
- **Tray icons drag**, on the bar (within the tray bubble: the copy, the outline and the accent line, as for apps) and in Dynaland's general page (the tile follows the pointer, an accent line marks the place; Dynaland stays open during the drag). One order for both, kept in bar-layout.json (`Tray`), by a name that lasts between runs: the icon's GUID, or its program's file name and the icon's number (`TrayIcon.Stable`). New icons go after the placed ones.
- The tray receiver now belongs to the shell (it served only Dynaland): it runs while Dynaland or the tray bubble is on, and stops last at shutdown, so apps reach Explorer's tray again.
- Not added: the spec's separate "Customize" item on the tray's menu. Settings (the picker's Tray page) and dragging cover choosing where the tray is and its order; there is nothing else to customize that the app tray would honour.

Files: `Shell/TrayIconService.cs` (`TrayIcon.Stable`; nothing else changed in the receiver), `Shell/ShellHost.cs` (`Tray`, started when wanted, disposed in FinishShutdown), `Shell/BarLayoutStore.cs` (`tray` bubble in the order, `Tray` list, `ArrangeTray`, `PlaceTrayIcon`), `Config/ShellSettings.cs`, `Config/SettingsStore.cs` (`TrayOnBar`), `Bar/BarWindow.Layout.cs` (the tray bubble), `Bar/BarWindow.Drag.cs` (tray icon drags), `Bar/BarWindow.Dock.cs`, `Bar/BarWindow.xaml.cs`, `Widgets/DynalandWindow.cs` (the shell's receiver, the kept order, tile dragging), `Custom/CustomMode.cs`, `Custom/VariantPickerWindow.cs` (Tray page, its preview shows the real tray pictures), `SettingsUi/SettingsWindow.xaml.cs`, README.

**To check:**

1. Build (0 warnings), launch.
2. Settings > Taskbar > Tray on the bar: a tray bubble at the bar's right end; Dynaland's general page has no tray now. Click, double-click and right-click an icon: as in Dynaland.
3. Drag a tray icon within the bubble: it moves; the order stays after a restart.
4. Drag the tray bubble by its empty end to the left of Start.
5. Turn Tray on the bar off: the tray is back in Dynaland, in the same order; drag a tile there: it moves (Dynaland stays open while dragging).
6. Custom mode: pick the tray bubble, Remove: back to Dynaland; the picker's Tray page shows the real icons.

#### antislop Delivery Gate (during mode), Part 8 UI

Design read: the tray bubble is a pill like the others (same fill, radius and icon buttons; tray pictures at their own 16 px); Dynaland's drag uses its accent line on black. Dial ENERGY 1 / RHYTHM 1 / MOTION 1 (only the dragged piece follows the pointer). Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS. R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (real tray pictures and tooltips only). R-23, R-24 PASS. R-25 PASS. R-26 PASS (every tile clicks through to its app; the accent line is where the icon lands). R-27 PASS (no icons: no bubble; a picture that cannot be read: the name's first letter; a drop with nowhere to go: put back). R-32 PARTIAL (where the tray is: Settings toggle and the keyboard picker; the icons' order: mouse only, noted). R-33, R-34 PASS. R-35 NOT RUN (Claude cannot run on this PC; parser check and code inspection; Sam's build is the run check). R-37 PASS. R-39 PASS (tiles named by their tooltips). R-40 N/A.
- Block 2: nothing decorative added (PASS). Block 3: dials kept; one accent. Block 4: C-1 to C-5 PASS; R-11 PASS; R-31 PASS.

### Part 8b - Win+1..9 follow the dragged order

Sam (2026-11-19): "also, the Win+1...9 isn't matches anymore when taskbar icons are dragged."

**Status: delivered with Part 8, waiting for build and run report.**

- Win+1..9 (and Win+Shift+1..9, Alt+Esc's window order, and the window switcher's list) still counted the apps in the old order: pins first, then running apps. They now count them as the bar shows them, from the left: the shown bubbles in their order (groups included), each bubble's apps in their dragged order (`ShellHost.BarOrderedApps()`, built fresh from the pins and running apps each time; `BarWindow.ShownBubbleIds`). A bubble that is not shown (removed in custom mode) is not counted. Files: `Shell/ShellHost.cs`, `Shell/HotkeyActions.cs`, `Flyouts/SwitcherWindow.cs`, `Bar/BarWindow.Layout.cs`.

**To check:** drag an app to the first place on the bar: Win+1 opens it. Make a group right of the apps: Win+(number of apps + 1) opens the group's first app.

### Part 9 - Win+0, mic and camera dots, Start's menus, search actions, power menu, run at startup

Sam (2026-11-19): Win+0 "just as windows does", plus: "1. the mic and camera dots never worked 2. add option to customize what inside power button, including hibernate, sleep (if PC activates one or both of them), and 'update and shutdown/restart'. 3. also add option to customize what button could be placed before the power button, just like in windows' settings. 4. change the current 'pin' button on start menu list to three dots, that when opened would show something like in the 2nd image. 5. when hovering a search result, add textless horizontal strip of icons [...] for files, [...] the horizontal strip would been directly below the image preview. 6. add option in the settings to run at startup. 7. remove excess subtitle texts in the machitan's settings."

**Status: built and run (Sam, 2026-11-20); his follow-ups are Part 10.** No new packages.

- **Win+0** (and Win+Shift+0) is the 10th app, as in Windows (`Shell/HotkeyActions.cs`).
- **Mic and camera dots.** Cause: they read only Windows' records under `CapabilityAccessManager\ConsentStore` (LastUsedTimeStop 0 while in use). The log shows Windows Camera using the mic on 2026-11-18 23:21 (Windows' own tray icon said so) while Machitan logged nothing: this Windows build no longer writes those records. Now three sources, joined: the microphone from Core Audio's recording sessions on every active capture device (the ones the volume mixer lists, checked every 1.5 s), the camera from Media Foundation's sensor activity monitor (`MFCreateSensorActivityMonitor`, Windows calls back when a camera's users change), and the old records where Windows still writes them (a store app from them counts only while one of its processes runs). Machitan still never opens either device. New `Interop/DeviceActivity.cs` (raw COM by vtable slot; the camera callback is a small hand-made COM object); `Shell/PrivacyMonitor.cs`. The log says which sources work at start ("Privacy: camera use comes from ...").
- **Power menu** (Settings > Start > Power menu): Sleep, Hibernate, Shut down, Restart, Update and shut down, Update and restart. Restart and Shut down stay on; the new ones are off. Sleep shows only when the PC can sleep (S1-S3 or Modern Standby; on Modern Standby Windows' sleep is the screen turning off, so Machitan does that), Hibernate only when hibernation is on (the hibernation file exists), the update entries only while Windows has an update waiting (`HKLM\SOFTWARE\Microsoft\WindowsUpdate\Orchestrator` ShutdownFlyoutOptions, which Windows' own power menu reads) and run `InitiateShutdown` with SHUTDOWN_INSTALL_UPDATES. In Settings, Sleep and Hibernate are greyed with the reason when the PC does not offer them. The power button now opens a menu; shutting down, restarting and the update entries still ask once in the strip. `Shell/PowerActions.cs`, `Interop/Power.cs`.
- **Folder buttons** left of the power button (Settings > Start > Next to the power button): Settings, File Explorer, Documents, Downloads, Music, Pictures, Videos, Network, Personal folder, as in Windows' Start > Folders. All off. The folders open in File Explorer by their shell names.
- **"..." on Start's app rows** (replaces the pin button; also a right-click on a row or a pinned tile): Pin to Start / Unpin from Start, Run as administrator (desktop apps and packaged desktop apps, from the package manifest), Pin to taskbar / Unpin from taskbar, App settings (store apps: Settings > Apps > the app's page), Uninstall (store apps: one question in place of the row, then Windows removes the package for this user; Windows' system apps: not offered; desktop apps: Settings > Installed apps, as Windows' Start does), then **Tasks**: the app's own jump list tasks, read from Windows' file for the app (`%AppData%\Microsoft\Windows\Recent\CustomDestinations`, named by Windows' app ID hash; checked against two known hashes), with their icons; a task runs like its shortcut. `Start/ResultActions.cs`, `Shell/AppActions.cs`, `Shell/JumpListTasks.cs`.
- **Pin to Start and Pin to taskbar are now two lists**: `start-pins.json` (Start's Pinned) and `pins.json` (the bar). Start's list starts as a copy of `pins.json`, which held both before, so nothing moves. The bar's own menu now says "Pin to taskbar" / "Unpin from taskbar".
- **Search action strip**: pointing at an app result (Start while searching, and the bar's search box) shows icon buttons without text, each named by its tooltip: Run as administrator, Open file location, Pin to Start, Pin to taskbar, Uninstall (only those that apply). Files get a **preview**: in Start it takes Pinned's place, beside the bar's search box it is a second box. It shows the name, date and type, Windows' thumbnail, then the strip right below the picture (Open, Share, Copy, Copy as path, Properties, Open file location), then where it is, its size in pixels (pictures and videos) and its file size. The previewed file is the first result, or the one pointed at (or chosen with Up and Down by the bar's box). Share uses Windows' share window; Copy puts the file itself on the clipboard; Copy as path gives the path in quotes, as Windows does. `Start/FilePreview.cs`, `Shell/FileActions.cs`, `Flyouts/SearchResultsFlyout.cs`, `Start/StartMenuWindow.xaml(.cs)`.
- **Run at startup** (Settings > General, off): Windows' own startup list (`HKCU\...\Run`, value Machitan). The switch reads Windows each time, so it is off when the entry is gone, points at another copy, or Task Manager turned it off; switching on also clears Task Manager's "off". `Shell/StartupRegistration.cs`.
- **Settings text**: descriptions cut to the ones that carry something the label does not (Compact not built yet, what Icon and Box search do, which dot is which, a needed option, why Sleep or Hibernate is greyed, when the update entries show). New Start page.
- Start no longer closes when one of its own menus takes the focus (it closes after the menu if the click went elsewhere).

**To check:**
1. Use the mic (Voice Recorder or a call): the orange dot and the app's name in Dynaland. Use the camera (Windows Camera): the green dot.
2. Settings > Start: turn on Sleep and the folder buttons; open Start: the folders sit left of the power button, the power menu lists Sleep.
3. Start > ... on an app: the menu as in Windows; for Claude, Tasks lists New Chat and New Code Session.
4. Type in Start's search, point at an app: the icon strip. Type `files:` and a name: the preview replaces Pinned, its icons sit right under the picture.
5. Settings > General > Run at startup on: Task Manager > Startup apps lists Machitan.
6. Win+0 opens the 10th app on the bar.

#### antislop Delivery Gate (during mode), Part 9 UI

Design read: everything reuses Machitan's parts: Start's rows and boxes, Windows' menus, 28 DIP icon buttons with tooltips (like the dock's), the accent button for the one destructive question. The preview is a card in an existing box (Start) or one more box of the same fill and radius (bar search). Dial ENERGY 1 / RHYTHM 1 / MOTION 0 (nothing animates; strips appear on hover). Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS (no em dashes). R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (every entry comes from Windows: capabilities, the update flag, the package, the manifest, the jump list file, the thumbnail and file properties; nothing is shown that is not there). R-23, R-24 PASS (no new pictures; icons are the app's own or Segoe Fluent glyphs). R-25 PASS. R-26 PASS (every button acts; entries that would not work are left out, and Sleep/Hibernate are greyed with the reason). R-27 PASS (no thumbnail: the box stays empty; no tasks: no Tasks heading; a failed uninstall says so; nothing chosen: the power menu says where to choose). R-32 PASS for menus, power and settings (keyboard reachable); PARTIAL for the hover strips (the "..." menu holds the same app actions by keyboard; file actions are mouse only, noted). R-33, R-34 PASS. R-35 NOT RUN (Claude cannot run on this PC; parser check and code inspection; Sam's build is the run check). R-37 PASS. R-39 PASS (every icon button has a name and a tooltip). R-40 N/A.
- Block 2: nothing decorative added (PASS). Block 3: dials kept; one accent, used once (the uninstall question). Block 4: C-1 to C-5 PASS; R-11 PASS; R-31 PASS.

#### Part 9 fix 1 (build report, 2026-11-19 18:30)

- CS0234 x6 in `FileActions.cs` and `JumpListTasks.cs`: the vtable helper class was called `Com`, and outside `Machitan.Interop` the name found a namespace `Com` from the referenced Windows assemblies first. Renamed to `ComCall` (`Interop/DeviceActivity.cs` and both callers).
- CS8602 in `DynalandWindow.cs` (the seek bar's press handler, from Part 6): the handler is made before `_media` is set further down the constructor, so the compiler saw a possible null. It now checks `_media?.Current.CanSeek != true` (it always runs later, when `_media` is set).

#### Part 9 result (Sam, 2026-11-20)

Built after fix 1. The mic and camera dots work (the log: "Privacy: Camera: Camera, Microphone: Camera"). Sam's next list is Part 10.

### Part 10 - strip fit, Alt key, admin windows, Win+X, search actions, list previews, folder order, Firefox private windows, logo

Sam (2026-11-20, 7 screenshots): "1. as you can see in the 1st image, the reserved area bugged. 2. [...] when i switch windows by clicking the icon on the taskbar, somehow alt button is 'pressed'. 3. i've changed my mind, remove the action buttons in the app search's result except the 3 dots. 4. the action buttons in the file search's result should be centered. add 'run as admin' if it's .exe or anything that can run as admin. 5. opening apps such as regedit, event viewer, etc breaks windows shortcuts, and even shows original start menu when windows button is pressed. 6. add list view as option other than thumbnail view in taskbar. 7. fix the win+x flyout, it's system's own, you don't need to make it your own, just direct it to the right position. 8. make the section in the 7th image is machitan's settings has customizable position, plus has button for machitan's settings (use bolt and screw icon). 9. more calendar's 'show second' option to the taskbar tab. 10. count firefox's normal and incognito windows as separate, just like in the original windows. 11. [...] `machitan.ico`, `MachitanLogo.xaml`, and `MachitanLogo.xaml.cs` [...]"

**Status: built and run (Sam, 2026-11-20); three items needed more work, in Part 11.** No new packages.

1. **Maximised window under the top strip.** The log shows the top strip reserved at 01:18:40, a second after the bar's own; Claude's window was already maximised and Windows did not fit it again. Now, 0.8 s after each strip reservation, every maximised window on that screen that still reaches into a kept strip is moved and sized to the work area (it stays maximised). Full-screen windows (exactly the whole screen) are left alone. The log says when it fits one. `Shell/MaximizedFit.cs` (new), `Widgets/TopStrip.cs` (an Applied event), `Shell/ShellHost.cs`.
2. **Alt "pressed" after a click on a taskbar icon.** Cause: the last way to bring a window to the front tapped Alt (Windows lets a program that just sent a key press do that), and apps show their access keys when Alt is let go. The Alt tap is gone. In its place: an empty mouse input (no key, no movement), which gives the same permission and shows nothing. Order now: the plain way, then the empty input and the plain way again, then borrowing the front app's input state. `Shell/WindowActivator.cs`, `Interop/KeyChord.cs`, `Interop/User32.cs`.
3. **App results keep only "...".** The hover icons are gone from Start's results and from the bar's search box. The bar's results now have the same "..." button (and right-click) as Start, with the same menu; the bar's search stays open while that menu is open and goes on after it. `Start/StartMenuWindow.xaml.cs`, `Flyouts/SearchResultsFlyout.cs`, `Bar/BarWindow.Search.cs`, `Start/ResultActions.cs`.
4. **File actions centred, with Run as administrator** after Open, when Windows itself offers it for that kind of file: its file type has a runas verb in the registry (programs, batch files, consoles such as .msc, and others Windows marks so); for a shortcut, its target. `Shell/FileActions.cs`, `Start/ResultActions.cs`, `Start/FilePreview.cs`.
5. **Admin windows (regedit, Event Viewer).** Cause: while a window running as administrator is in front, Windows keeps every key from programs that are not, Machitan's keyboard hook included. So the Win key went to Windows (its Start opened), and key releases never reached the hook, which then thought Win was still held and broke the shortcuts after. Now:
   - Windows' Start coming to the front without Machitan asking for it (its Ctrl+Esc fallback) is closed with Esc, and Machitan's Start opens.
   - The hook checks its state against the keyboard: a Win key it still thinks is held but is up is let go; a held-back key whose release never came starts over on its next press.
   - Settings > Shortcuts says it: while an admin window is in front, the shortcuts Machitan takes through its hook do not reach it, and Windows' own act.
   `Shell/SystemUiWatcher.cs` (new), `Shell/KeyboardService.cs`, `Interop/KeyChord.cs`.
6. **List previews.** Settings > Taskbar > Show previews as: Thumbnails (default) or List. List is one box with a row per window: the app's icon, the window's title, a close button; up to 12, then "+N more windows". Pointing peeks, a click switches, as with thumbnails. `Flyouts/PreviewWindow.cs`, setting `PreviewStyle`.
7. **Win+X menu.** Still Windows' own menu. When it opens, Machitan moves it next to its Start button: above a bottom bar, below a top bar, its left edge at the button's, kept on screen. It is found as explorer's top-level popup menu that opens within 2 s of Win+X; when the hook could not see Win+X (an admin window in front), as explorer's menu near the screen's left corner, away from the pointer. Its submenu is left alone. `Shell/SystemUiWatcher.cs`.
8. **Next to the power button: order and a Machitan button.** Each row in Settings > Start has Move up and Move down (top to bottom here is left to right on Start). New row "Machitan settings" (off): a button that opens Machitan's Settings, with the nut and bolt symbol (U+1F529 from Segoe UI Emoji, drawn in one colour like the other icons). Settings `FolderOrder` and `FolderMachitan`. `Start/StartFolders.cs` (new), `Start/StartMenuWindow.xaml.cs`, `SettingsUi/SettingsWindow.xaml.cs`, `Config/ShellSettings.cs`, `Config/SettingsStore.cs`.
9. **Seconds** moved to Settings > Taskbar > Calendar (Win+N).
10. **Firefox's private windows get their own button.** Windows' taskbar groups windows by app ID, and a window can carry its own (System.AppUserModel.ID in the window's property store). Machitan now reads it. A window whose ID is not the program's usual one gets its own button: usual means the ID Start's shortcuts to that program carry; when none carries one, an ID with a ';' is the extra one (Firefox adds ";PrivateBrowsingAUMID"). The button shows the name and icon the window asked Windows for, else its window icon. A pin of Firefox stands for its normal windows only. The same rule splits other apps the way Windows does (for example a browser's installed web apps). Not done: starting a new window from the private button opens a normal Firefox window. `Interop/WindowAppId.cs` (new), `Shell/TaskService.cs`, `Shell/TaskModels.cs`, `Shell/DockModel.cs`, `Shell/AppCatalog.cs`, `Shell/IconService.cs`, `Shell/ShellHost.cs`.
11. **Logo and icon.**
   - The bar's Start button shows `MachitanLogo` (36 x 24 at the default icon size of 24; it follows Icon size, and the button widens to fit). The four-square glyph is gone.
   - Settings has its own title bar: the logo at 24 x 16 and "Machitan Settings". Windows keeps the window's buttons, coloured for Machitan's theme.
   - `machitan.ico` is Machitan.exe's icon (`ApplicationIcon`), is copied next to the exe, and is the Settings window's taskbar and Alt+Tab icon (`Core/AppIcon.cs`, new).
   - One fix in `MachitanLogo.xaml`: its hover and press states were set on the UserControl itself, but VisualStateManager looks for them on the control's first element, so they never ran. They now sit on the Viewbox; nothing else changed. The two files stay in the project root (namespace `Machitan.Controls`).

**To check:**
1. Start Machitan with a maximised window already open: its top edge ends at the top strip.
2. Click an app on the bar to switch to Notepad: no underlined access keys.
3. Search an app: only "..." on hover, in Start and in the bar's box. Search a file (`files:`): the icons sit centred under the picture; for an .exe, Run as administrator is there.
4. Open regedit, keep it in front, press Win: Machitan's Start opens (Windows' may flash first). Close regedit: Win, Win+1..9 and Win+N work.
5. Settings > Taskbar > Show previews as > List, then hover an app with two windows.
6. Win+X: the menu opens next to Machitan's Start button.
7. Settings > Start: move a button up, turn on Machitan settings; open Start: the order matches, the nut and bolt opens Settings.
8. Firefox: open a private window: it gets its own button next to the normal one.
9. The bar's Start button and Settings' title bar show the logo; the logo lightens on hover.

#### antislop Delivery Gate (during mode), Part 10 UI

Design read: the parts reuse Machitan's own: the list previews use the preview box, row height and hover wash of Start's rows; the move buttons are the Settings page's 32 DIP icon buttons; the logo is Sam's file at the sizes he gave; the title bar follows Windows 11's 32 DIP caption height. Dial ENERGY 1 / RHYTHM 1 / MOTION 0 (only the logo's own hover change moves). Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS (no em dashes in Machitan's files; Sam's two logo files keep his own text). R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (names, icons and app IDs come from the windows and Windows; Run as administrator shows only where Windows has the verb). R-23 PASS (the logo and the icon are Sam's; the nut and bolt is a system font symbol he asked for). R-24, R-25 PASS. R-26 PASS (every new button acts; the move buttons at the ends are disabled). R-27 PASS (no window app ID: grouping as before; no icon asked for: the window's own; no logo file: Windows' default icon, logged). R-32 PASS (move buttons and toggles by keyboard, focus stays on the moved row; the bar's "..." opens by right-click or the button; list rows are pointer-only like the thumbnails, noted). R-33, R-34 PASS. R-35 NOT RUN (Claude cannot run on this PC; parser check and code inspection; Sam's build is the run check). R-37 PASS. R-39 PASS (each new button has a name and a tooltip). R-40 N/A.
- Block 2: nothing decorative added (PASS). Block 3: dials kept; no new colours besides the logo's own. Block 4: C-1 to C-5 PASS; R-11 PASS; R-31 PASS.

#### Part 10 result (Sam, 2026-11-20)

Built and run. Everything else works; still wrong: the top strip safeguard, shortcuts in apps running as administrator, and the Win+X menu's place. Sam's next list is Part 11.

### Part 11 - top strip again, admin apps, tray box, Win+X above Start, Window Wrench, folder drag, Windows App SDK 2.7.0, Indonesian

Sam (2026-11-20, 2 screenshots): "1. top strip safeguard still don't work 2. admin-elevated apps shouldn't take away machitan's keyboard shortcuts 3. group all tray icons inside a collapsible box when 'taskbar' tray position is selected, just like in the 1st image. those tray icons can be put outside the designated box. 4. win+x flyout should've been above the start button, not after it. 5. machitan's settings icon in the start menu should've been Window Wrench. 6. when i opened the start menu and right-clicked the taskbar, close the start menu rather than letting it open. 7. in the 'Next to the power button' in the machitan's settings, give the options the icon and make their position dragable. 8. use winapp sdk 2.7.0. 9. add indonesian support. also, make this project easy to translatable by giving translation PR guides. 10. remove 'signal x of 3'. 11. bump the winapp sdk version to 2.7.0." After this: notifications.

**Status: built and run (Sam, 2026-11-20); all fine except items 1 and 4, in Part 12. A first build stopped on CS0133 (a translated text read into a `const`), fixed in r43.** One package version changed (Windows App SDK 2.6.5 to 2.7.0, Sam's request); no new packages.

1. **Top strip.** The log shows no window was fitted. Cause: when Machitan starts, Explorer's taskbar goes to auto-hide and Windows grows maximised windows to the whole screen; the check then took them for full-screen apps (exactly the screen's size) and left them. Now a maximised window is left alone only when it has no title bar and no sizing border (what full-screen apps look like). It also looks twice (0.8 s and 2.8 s after the strip is taken), as Windows may still re-fit windows itself in between, and logs what it checked when it fits nothing. `Shell/MaximizedFit.cs`, `Shell/ShellHost.cs`, `Interop/User32.cs`.
2. **Shortcuts in apps running as administrator** (Settings > Shortcuts, off). Windows keeps an admin app's keys from every program with fewer rights; only a program with administrator rights can see them. Turned on, Machitan makes a Windows scheduled task, `Machitan\AdminKeys`, that runs `Machitan.exe --adminkeys` as administrator (Windows asks once, then never at start). That helper has no window: only Machitan's own keyboard hook (the same code), acting only while an app with more rights than Machitan is in front. A lone Win and Win+0..9, Win+Shift+0..9, Win+N, Win+A, Win+Tab are held back from Windows there and handed to Machitan, which does what it always does. Before each, the helper lets Machitan bring its window to the front (Windows allows that only to a program with the admin app's rights), and Machitan asks it for that too when Windows refuses. The helper ends with Machitan. Turning the option off ends it and removes the task (Windows asks again). The task names this copy of Machitan.exe; another copy needs the option turned on again (the log says so). `Shell/AdminKeys.cs`, `Shell/AdminKeysHelper.cs` (new), `Interop/TokenInfo.cs` (new), `Shell/KeyboardService.cs` (its two outlets, lone Win and caught shortcut, are now set by whoever starts the hook), `Shell/WindowActivator.cs`, `Program.cs`, settings `AdminAppShortcuts`, `AdminAppShortcutsFor`.
3. **Tray box.** With the tray on the bar, its icons now sit in a box behind a ^ button (v for a top bar), as on Windows' taskbar; a new icon starts there. The box opens above the button: up to five icons a row, a click goes to the app as before. Dragging an icon out of the box closes it and the bar's drag takes over: let go among the bar's tray icons and it stays on the bar. Dragging a bar tray icon onto the ^ button puts it back in the box. Which icons are out is kept in `bar-layout.json` (`TrayOut`). Dynaland's tray is unchanged. `Flyouts/TrayBoxWindow.cs` (new), `Bar/BarWindow.Layout.cs`, `Bar/BarWindow.Drag.cs`, `Shell/BarLayoutStore.cs`, `Shell/ShellHost.cs`.
4. **Win+X menu above the Start button.** The log has no line from the move, so it never happened, and nothing said why. Now the menu's left edge lines up with the Start button and its bottom sits 8 DIP above it (below a top bar); Machitan looks again for half a second after it opens and moves it back if Windows placed it again; the log says where Windows put it, where it went, and why an explorer menu was left alone (first three). `Shell/SystemUiWatcher.cs`.
5. **Window Wrench** for Machitan's settings button: Segoe Fluent Icons has no such glyph, so it is the vector path of "Window Wrench" (16 px, regular) from Microsoft's Fluent UI System Icons, drawn in the text colour. MIT licence, noted in the new `THIRD-PARTY-NOTICES.md`. `Start/StartFolders.cs`.
6. **A right-click on the bar closes Start**, as on Windows' taskbar. `Bar/BarWindow.xaml.cs`.
7. **Next to the power button** rows show their icon and have a drag handle: drag a row to its new place (the others make room); Alt+Up and Alt+Down on the handle move it with the keyboard. The up/down buttons are gone. `SettingsUi/SettingsWindow.xaml.cs`.
8. **Windows App SDK 2.7.0** (released 2026-11-17). `Machitan.csproj`, README.
9. **Bahasa Indonesia, and translations.**
   - All text people see moved out of the code into `Translations/Strings.resx` (English, 361 entries) with `Translations/Strings.id.resx` (Indonesian) beside it; `Translations/Strings.Designer.cs` is the usual Visual Studio accessor (`Strings.Name`), and text with values in it goes through `Fmt.F`. Log messages stay English.
   - Settings > General > Language: Windows' language (default), English, Bahasa Indonesia; it applies when Machitan starts. Dates keep Windows' regional format.
   - Windows' own Indonesian words where Windows has them (Mulai, Bilah tugas, Sematkan ke bilah tugas, Jalankan sebagai administrator).
   - `TRANSLATING.md`: how to add or improve a language, the rules (keep `{0}`, use Windows' words, no new claims), how developers add text, and what a translation pull request should hold.
   - `Core/Languages.cs`, `Core/Fmt.cs` (new); every UI file.
10. **"(signal x of 3)"** removed from the status ring's Wi-Fi tooltip. `Widgets/DuoRingWindow.cs`.

**To check:**
1. Maximise a window, restart Machitan: its top ends at the strip; the log has a "Strip:" line about it.
2. Settings > Shortcuts > Shortcuts in apps running as administrator: on (Windows asks once). With regedit in front: Win opens Machitan's Start, Win+1 and Win+N work. Turn it off: Windows asks again; the task is gone from Task Scheduler.
3. Settings > Taskbar > Tray on the bar: the ^ button opens the box; drag an icon onto the bar, then back onto ^.
4. Win+X: the menu sits above the Start button.
5. Start's button for Machitan's settings shows the Window Wrench.
6. Open Start, right-click the bar: Start closes.
7. Settings > Start: drag a row by its handle; Start's buttons follow.
8. Settings > General > Language > Bahasa Indonesia, restart Machitan: Settings, Start, the bar's menus, Dynaland and the flyouts are in Indonesian.

#### antislop Delivery Gate (during mode), Part 11 UI

Design read: everything reuses Machitan's parts. The tray box is one flyout box of the bar's fill, 40 DIP cells (the size of Start's and the switcher's hit areas); the ^ button is a bar button; drags use the bar's own drag layer and marker. The Settings rows keep the card's lines and only fill a row while it is dragged. The Window Wrench is Microsoft's own icon in the text colour like the Segoe glyphs beside it. Dial ENERGY 1 / RHYTHM 1 / MOTION 1 (rows make room while dragging; nothing else moves). Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS (no em dashes in Machitan's files; the Indonesian text was checked for them too). R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (the box shows only icons apps sent; every translated text says what the English says, no new claims). R-23, R-24 PASS (the one new picture is the icon Sam named, from Microsoft's own set, with its licence). R-25 PASS. R-26 PASS (the ^ button shows only while the box holds icons; every new control acts). R-27 PASS (empty box: no ^ button; no Start button on the bar: the Win+X menu stays where Windows put it and the log says why; the helper's task refused or declined: the switch goes back off; a language Windows does not know: English). R-32 PASS for the Settings rows (Alt+Up/Down), the box's icons (Tab, Enter) and the switch; dragging between box and bar is pointer-only (the box keeps working by keyboard), noted. R-33, R-34 PASS. R-35 NOT RUN (Claude cannot run on this PC; parser check, string-table check (every `Strings.` name exists, every `{0}` kept in both languages) and code inspection; Sam's build is the run check). R-37 PASS. R-39 PASS (handles, ^ button and box icons have names and tooltips, in both languages). R-40 N/A.
- Block 2: nothing decorative added (PASS). Block 3: dials kept; no new colours. Block 4: C-1 to C-5 PASS; R-11 PASS; R-31 PASS.

### Part 12 - the top strip and Win+X, third look

Sam (2026-11-20 14:44): "top strip still unfixed, somehow. Win+X too, still overlaps with the taskbar. everything else, including in other parts, works. also, what's left in the batch 7?"

**Status: built and run (Sam, 2026-11-20 15:06); neither worked, both redone in Part 13. Its logs found the causes: the work area kept the strip, and Windows' Win+X menu is a XAML popup Windows keeps placing.** No new packages.

1. **Top strip.** The log of the 14:09 run has no line from the fit at all. It writes one only when it finds a maximised window, so it found none. Claude's window fills the screen without being maximised in Windows' sense: an Electron app with a see-through window sizes itself to the work area when maximised, and keeps that size when the work area changes. Parts 10 and 11 looked at maximised windows only. Now:
   - Ordinary windows that look maximised (as wide as the free area, at least 60% of its height, reaching into the strip) get their top edge (bottom edge for a strip at the bottom) moved out of the strip. Maximised windows are fitted as before and stay maximised.
   - Left alone: full-screen windows (covering the whole screen, the bar's own rule), tool windows, always-on-top windows, click-through overlays, dialogs of another window, ordinary windows that do not fill the screen, and a window that moves back 3 times within 10 s.
   - Checked when the strip is taken (0.8 s and 2.8 s after), and from then on whenever a window appears, moves or changes size (gathered every 150 ms), so a window maximised later is checked too.
   - The free area is the work area, made smaller by the strip itself if Windows did not take the strip off the work area.
   - The log always says what a check found: "Strip check (...)" with the windows fitted, the ones left alone and why, the free area, the work area and the strip. Later fits are logged once per program.
   `Shell/MaximizedFit.cs`, `Shell/WindowChangeWatch.cs` (new), `Shell/ShellHost.cs`.
2. **Win+X.** The log of the 14:09 run again has no Win+X line, not even a skipped one, so no classic popup menu ("#32768") of explorer opened: on this Windows build the menu is another kind of window. Now:
   - The keyboard hook tells the watcher the moment Win+X goes past to Windows, after noting which windows are on screen.
   - For 1.5 s Machitan then looks every 30 ms for a new window of Windows' shell (explorer.exe, ShellHost.exe, ShellExperienceHost.exe, StartMenuExperienceHost.exe) of menu size, and moves the first one above the Start button, keeping it there for half a second. The classic menu is still caught as before.
   - When the hook could not see Win+X (an admin app in front), a classic menu of Explorer's own hidden taskbar counts too.
   - For the first three Win+X a run, the log says that the hook saw it, which new shell window came, where, and whether it moved; or that none came, and which menu events came instead.
   `Shell/SystemUiWatcher.cs`, `Shell/KeyboardService.cs`.

**Batch 7, what is left:** every item of the handoff's Batch 7 plan is built and, by Sam's report, works. Left: these two fixes, and one known gap from Part 10 (a new window from the button of Firefox's private windows opens a normal window).

**To check:**
1. Maximise Claude (or any app), restart Machitan: its top ends at the strip. Maximise another window after Machitan started: the same. The log has "Strip check" lines.
2. Win+X: the menu sits above the Start button. If it does not, the log's "Win+X" lines say what came.

#### antislop Delivery Gate (during mode), Part 12 UI

Design read: no new visuals; two behaviours on other apps' windows (a screen-filling window moved out of the strip; Windows' Win+X menu moved above Start). Dials unchanged. Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS (no em dashes). R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (nothing new is shown; log lines say only what Windows reported). R-23, R-24, R-25 N/A (no pictures). R-26 PASS (no new control). R-27 PASS (no Start button on the bar: the menu stays where Windows put it and the log says why; a window that moves back: left alone after 3 tries, logged; a window of an app running as administrator: Windows refuses the move, logged). R-32, R-33, R-34 N/A (no new control). R-35 NOT RUN (Claude cannot run on this PC; parser check and code inspection; Sam's build is the run check). R-37 PASS. R-39, R-40 N/A.
- Block 2: nothing decorative added (PASS). Block 3: dials kept. Block 4: C-1 to C-5 PASS (C-4: full-screen apps, tool windows and overlays are never moved).

### Part 13 - the strip taken off the work area; Machitan's own Win+X menu

Sam (2026-11-20 15:06, 2 screenshots of Windows' Win+X menu and its "Shut down or sign out" submenu): "claude still bugs. other apps though, works, but with additional white/black strip on the top of the window, and big gap between the taskbar and the maximised window. for win+x, maybe we should made our own, since the system's own win+x still fails (and even has funny glitch animation this time.)"

**Status: built and run (Sam, 2026-11-20 15:27): Win+X works; the strip is still not taken off the work area, and the bar's area safeguard still has problems. Sam's test at 15:35 found the cause (Part 14).** No new packages.

1. **Top strip: the real cause.** Part 12's log line says it: work area 0,0 to 1920,1104 while the strip 0,0 to 1920,48 was kept. Windows granted the strip but never took it off the work area, so every maximised window still filled the top. Claude moved straight back each time it was fitted (left alone after 3 tries), and the windows Machitan moved by hand showed the band apps keep under a maximised window's top edge (the white or black strip) and a gap above the bar, because the app and Windows no longer agreed on where "maximised" is. The strip's app bar was owned by a hidden window of no size; the bar's own app bar, which always worked, is a real window in its place. Now:
   - The strip's window sits exactly on the strip: shown, fully see-through (layered, alpha 0), click-through, behind every other window, never active, never on the bar or in Alt+Tab. After each placement Windows is told the window moved (ABM_WINDOWPOSCHANGED). Given back: the window hides.
   - Maximised windows are no longer moved by hand while Windows has not taken the strip off the work area; once it has, Windows fits them itself, and Machitan only fits one Windows missed.
   - The "Strip check" log line now says whether Windows took the strip off the work area.
   `Widgets/TopStrip.cs`, `Shell/MaximizedFit.cs`.
2. **Machitan's own Win+X menu.** Part 12's log shows Windows' menu is a XAML popup of Explorer ("Xaml_WindowedPopupClass") that Windows placed again after every move: the jump Sam saw. Win+X is now caught by the keyboard hook like Win+N (and by the admin-keys helper when that is on) and opens Machitan's menu above its Start button (below it for a top bar), its left edge at the button's:
   - Windows' entries in Windows' order and groups (Sam's screenshots): Installed apps, Mobility Center, Power Options, Event Viewer, System, Device Manager, Network Connections, Disk Management, Computer Management, Terminal, Terminal (Admin) | Task Manager, Settings, File Explorer, Search, Run | Shut down or sign out >, Desktop. Submenu: Sign out, Sleep, Hibernate, Update and shut down, Shut down, Update and restart, Restart.
   - Shown only where Windows shows them: Mobility Center on a PC with a battery; Terminal when Windows Terminal is installed, else Windows PowerShell (Admin); Sleep and Hibernate when the PC can and Windows' "Show in power menu" choices have them (policy first, then Control Panel's setting; Windows' defaults: Sleep shown, Hibernate not); the update entries while an update waits.
   - What each does: Settings pages for Installed apps, Power Options, System and Network Connections (as Windows 11's menu); the consoles for Event Viewer, Device Manager, Disk Management and Computer Management; Terminal or PowerShell in the user's folder (Admin asks Windows first; "No" is not an error); Search opens Machitan's Start with its search box; Run is Windows' Run box (as Win+R); Desktop is Show desktop (as Win+D). Everything acts at once, as in Windows' menu.
   - Keys: the underlined letters (Windows' own, from the screenshots: P, B, O, V, Y, M, W, K, G, I, A, T, N, E, S, R, U, D; submenu I, H, U, R, and S for Sleep), Up and Down, Enter, Right and Left for the submenu, Esc. Pointing at "Shut down or sign out" opens its submenu beside the menu (on the side with room), kept above the bar.
   - The labels mark their key with "&" in the string table; Indonesian has its own letters (TRANSLATING.md explains the rule).
   - Windows' Win+X menu is no longer moved (that code is gone from `Shell/SystemUiWatcher.cs`, which keeps replacing Windows' Start).
   `Flyouts/WinXMenuWindow.cs` (new), `Shell/HotkeyActions.cs`, `Shell/PowerActions.cs`, `Shell/ShellHost.cs`, `Shell/SystemUiWatcher.cs`, `Shell/KeyboardService.cs`, `Translations/*`, `TRANSLATING.md`.

**To check:**
1. Maximise Claude, restart Machitan: Claude ends at the strip, no band at its top, no extra gap above the bar. The log's "Strip check" line says "Windows took the strip off the work area".
2. Win+X: Machitan's menu above the Start button. Try U then U (shut down) only if you mean it; try Win+X then T (Task Manager), and Right on "Shut down or sign out".

#### antislop Delivery Gate (during mode), Part 13 UI

Design read: the Win+X menu is a Machitan flyout (the bar's fill, Radii.Large box, Radii.Small rows) holding Windows' own entries in Windows' order, so it reads like Windows' menu in Machitan's look. Rows 32 DIP (the hit size of Machitan's other rows), 1 px dividers where Windows has them, the submenu chevron from Segoe Fluent Icons. Dial ENERGY 1 / RHYTHM 1 / MOTION 0 (nothing animates; the submenu appears after 300 ms of pointing, as menus do). Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS (no em dashes). R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (only Windows' own entries, each shown only when this PC has it; nothing invented). R-23, R-24, R-25 N/A (no pictures). R-26 PASS (every entry acts; none shown that cannot). R-27 PASS (no Windows Terminal: PowerShell entries; no battery: no Mobility Center; no update: no update entries; a "No" to Windows' admin question: logged, nothing else; no Start button on the bar: the menu opens by the bar). R-32 PASS (Up, Down, Enter, Right, Left, Esc, and every entry's letter). R-33, R-34 PASS. R-35 NOT RUN (Claude cannot run on this PC; parser check, string-table check (389 entries in both languages, keys unique per menu) and code inspection; Sam's build is the run check). R-37 PASS. R-39 PASS (the menu and the submenu have names; every entry has its name and its key for screen readers). R-40 N/A.
- Block 2: nothing decorative added (PASS). Block 3: dials kept; no new colours (dividers are the text colour at low opacity). Block 4: C-1 to C-5 PASS (C-4: the strip's window can never be seen or clicked; maximised windows are left to Windows).

### Part 14 - Machitan's own AppBar messages straight to Explorer (the top strip and the bar's area)

Sam (2026-11-20, after Part 13): Win+X works; the top strip and the bar's area safeguard still do not. 15:35: with Dynaland off and "Tray on the bar" off (so Machitan's tray receiver never starts), maximised windows stop below the strip as they should. 15:46: "the problem 2 is related to problem 1, so go ahead."

**Status: built and run (Sam, 2026-11-20 16:02 and 19:58; CS0136 fixed in r47): the check stopped it as designed. On Windows build 26300 the message is 64 bytes, not 56, so Machitan's messages kept going the usual way and the strip was still not taken off the work area. Replaced by Part 15.** No new packages.

1. **Machitan's own AppBar messages go straight to Explorer.** Windows' `SHAppBarMessage` hands every AppBar message to the first window called `Shell_TrayWnd`. While the tray receiver runs, that is Machitan's own window (it gets the apps' tray icons first), which passed each message on to Explorer's. The bar registers before that window starts, so its first reservation reached Explorer itself and worked; the strip registers after, and its messages took the detour: Explorer answered them, yet never took the strip off the work area. Every later reservation of the bar (a new height, the other edge, after another app bar changed) took the same detour, which fits Sam's word that problem 2 is related. Now:
   - `Shell/AppBarSender.cs` (new) sends Machitan's own AppBar messages (the strip's, the bar's, Explorer's taskbar state) straight to Explorer's taskbar window, from the caller's own thread, built the way shell32 builds them: WM_COPYDATA (dwData 0) of 0x38 bytes, an APPBARDATA with fixed-size fields (0x28 bytes, 32-bit window, 64-bit lParam), the message, a shared copy for the answer of ABM_QUERYPOS, ABM_SETPOS and ABM_GETTASKBARPOS, and Machitan's process ID. The layout follows Geoff Chappell's notes on shell32 and ReactOS (both 0x38 bytes).
   - Two checks first, once a run, each in the log. Check 1: an ABM_GETTASKBARPOS with marked values goes the usual way; Machitan's tray window sees what shell32 built on this PC, and every field must sit where Machitan puts it. Check 2: one built by Machitan goes straight to Explorer, which must answer and write its taskbar's rect back. If either fails, Machitan's messages keep going the usual way (as in Part 13) and the log says why, with the bytes Windows built.
   - Used only while Machitan's tray window is the first `Shell_TrayWnd`; otherwise (tray receiver off, the guardian after a crash) `SHAppBarMessage` reaches Explorer itself and is used as before.
   - A message that gets no answer within 4 s is not sent again (that could register a bar twice); the log says so.
   `Shell/AppBarSender.cs` (new), `Interop/Shlwapi.cs` (new: SHAllocShared, SHLockShared, SHUnlockShared, SHFreeShared), `Interop/Shell32.cs` (ABM_GETTASKBARPOS), `Widgets/TopStrip.cs`, `Shell/AppBarHost.cs`, `Shell/ExplorerTaskbar.cs`.
2. **The log shows the route.** "AppBar check 1 of 2" and "AppBar check 2 of 2" (passed, or what was wrong); "AppBar: straight to Explorer: ABM_SETPOS for window ..., asked ..., got ...; Explorer answered ..." (the first 16 a run); and "Tray: Machitan's own ... came through Machitan's tray window" for any that still take the detour (the check's own message, or one sent before the check; the first 10 a run). `Shell/TrayIconService.cs`.
3. **If it still fails.** The test at 15:35 turned the whole tray receiver off, so it shows the receiver is involved, not yet that the detour is the part that breaks it. If the checks pass, the messages go straight, and "Strip check" still says "Windows did NOT take the strip off the work area", then the tray window being there is enough, and the next step is to take the strip (and redo the bar's reservation) while Machitan's tray window steps behind Explorer's for that moment.

**To check:**
1. Dynaland on (or Settings > Taskbar > Tray on the bar on), Settings > Edge strip > Keep the edge clear on. Maximise Claude (or any app), restart Machitan: the window's top ends at the strip, no white or black band at its top, its bottom ends at the bar's space with no extra gap.
2. The log: "AppBar check 1 of 2" and "AppBar check 2 of 2" say they passed; "AppBar: straight to Explorer: ABM_SETPOS" lines follow; the "Strip check" line says "Windows took the strip off the work area". If not, send the log: those lines say which part failed.
3. The bar's area: Settings > Taskbar > Bar height: change it, then back; Position: Top, then Bottom. Each time, a maximised window ends at the bar's space and at the strip.
4. Tray icons still come in (Dynaland's general page, or the bar's tray box), and clicks on them work.
5. Exit Machitan: Explorer's taskbar comes back as before, and maximised windows use the whole screen above it.

#### antislop Delivery Gate (during mode), Part 14 UI

Design read: no new visuals; Machitan's own reservations (the bar's space and the strip) reach Windows another way, so other apps' maximised windows end where Machitan's widgets and bar begin. Dials unchanged. Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS (no em dashes). R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (nothing new is shown; log lines say only what Windows answered and what Machitan sent). R-23, R-24, R-25 N/A (no pictures). R-26 PASS (no new control). R-27 PASS (a check that fails or cannot run: the usual way, logged with the reason and the bytes Windows built; Explorer's window gone: the usual way, logged; no answer within 4 s: not sent twice, logged; shlwapi's calls missing: the usual way, logged). R-32, R-33, R-34 N/A (no new control). R-35 NOT RUN (Claude cannot run on this PC; bracket and text checks and code inspection, no compiler in this workspace; Sam's build is the run check). R-37 PASS. R-39, R-40 N/A.
- Block 2: nothing decorative added (PASS). Block 3: dials kept. Block 4: C-1 to C-5 PASS (C-4: other apps' AppBar messages are passed on as before; only Machitan's own take the new way).

### Part 15 - AppBar messages passed on so Explorer can answer them; tray icons sent while Explorer's window was in front

Sam (2026-11-20 18:29): "some apps like traydio (and most apps in the Traytober 2026) didn't showed up in the tray." 19:59, after restarting Machitan and then Traydio: "the icon's still doesn't showed up." He also copied `shell32.dll` from his PC (`%AppData%\Machitan\shell32.dll`) so its code could be read.

**Status: built and run (Sam, 2026-11-20, r48): "strip and bar's area fixed. traydio still doesn't fixed." Item 1 works: the log shows "AppBar: passed on ABM_SETPOS ... Explorer answered 1" and the work area starting below the strip. Item 3 did not find Traydio: in the 20:17 run Traydio started at about 20:18:31, no other `Shell_TrayWnd` window was in front near then (the only two moments were at 20:17:55, at Machitan's own start), and nothing from Trdo.exe reached Machitan. See Part 16.** No new packages.

What the logs and shell32.dll (build 26300) showed:
- Part 14's check 1 saw the message Windows builds: 64 bytes. The field at 0x30 held a value like `0x000001FE2183FAA0`, not a handle Explorer could use.
- `SHAppBarMessage` (read from shell32.dll): finds the first `Shell_TrayWnd` window, takes the ID of the process that owns it (`GetWindowThreadProcessId`), and for ABM_QUERYPOS, ABM_SETPOS and ABM_GETTASKBARPOS makes the answer block for that process (`SHAllocShared(NULL, 0x28, that process)`), puts the block at 0x30 and that process's ID at 0x38, sends WM_COPYDATA (dwData 0, 0x40 bytes) with `SendMessageW`, then reads the answer back from the block. While Machitan's tray receiver runs, that process is Machitan, and Explorer cannot open a block made for Machitan: it answered without acting. That is why the strip and the bar's later reservations never changed the work area. The run at 16:05:47 shows it: the strip grew to 72 px through Machitan's window and the work area stayed at 48 px.
- `Shell_NotifyIconW` (read from shell32.dll): `FindWindowW("Shell_TrayWnd")` each time, then `SendMessageTimeoutW` (WM_COPYDATA, dwData 1, 0x5CC bytes, flags 0xB, 7 s). Nothing else; every app's tray icon goes to whichever `Shell_TrayWnd` window is first at that moment.
- Traydio (Trdo) started at 18:20:44 and again at 19:58:31 while Machitan's tray receiver ran; nothing from it reached Machitan (Machitan logs every icon added after the first look). It is a full-trust packaged WinUI 3 app; its tray icon is WinUIEx's TrayIcon (a hidden WinUI window, `Shell_NotifyIcon`, re-added on "TaskbarCreated"). So its add went to another `Shell_TrayWnd` window: most likely Explorer's, which comes in front of Machitan's now and then (the 09:44 run had to step back in front 3 times in its first 6 s; later moments were never logged). An app that adds its icon as it starts can hit that moment. Not proven yet: the new log lines below say.

1. **AppBar messages passed on with a block Explorer can open.** `Shell/AppBarRelay.cs` (new), on the tray thread: for a 64-byte ABM_QUERYPOS, ABM_SETPOS or ABM_GETTASKBARPOS whose block was made for Machitan, it copies the block into a new one made for Explorer's process, passes the message on with that block and Explorer's process ID, and copies Explorer's answer back into the caller's block, as shell32 does itself when Explorer's window is first. This covers the strip, the bar's reservations and other apps' AppBar questions (for example apps asking where the taskbar is). Anything else is passed on unchanged, as before. If shlwapi's calls are missing, the message goes on unchanged and the log says so. Log: "AppBar: passed on ABM_SETPOS for window ... (Machitan) with an answer block for Explorer; Explorer answered 1, rect ..." (the first 12 a run). `Shell/TrayIconService.cs`, `Interop/Shlwapi.cs` (comment).
2. **Part 14's direct sender removed.** The strip, the bar and Explorer's taskbar state use `SHAppBarMessage` again; `Shell/AppBarSender.cs` is now an empty file (comment only) and can be deleted. `Widgets/TopStrip.cs`, `Shell/AppBarHost.cs`, `Shell/ExplorerTaskbar.cs`, `Shell/AppBarSender.cs`.
3. **Tray icons sent while another `Shell_TrayWnd` window was in front.** Each time Machitan's quarter-second check finds another window in front (after Machitan first asked the apps), the log says which program's window it was. 1.5 s later every window (shown, hidden or message-only) of an app started in the minute before is told "the taskbar was created", so it sends its icon again, this time to Machitan; each window is asked once a run, and apps running longer are left alone. If it happens while the apps are still answering Machitan's start-up question, all apps are asked once more instead. What comes back goes to the log in one block ("Tray: what the apps sent ..."). An answer block still open is now written before a new one starts. `Shell/TrayIconService.cs`.

**To check:**
1. Dynaland on (or Settings > Taskbar > Tray on the bar on), Settings > Edge strip > Keep the edge clear on. Restart Machitan, maximise a window: its top ends at the strip and its bottom at the bar's space. The log: "AppBar: passed on ABM_SETPOS for window ... (Machitan) ..." and "Strip check ... Windows took the strip off the work area".
2. Settings > Taskbar > Bar height: change it, then back; Position: Top, then Bottom. Each time a maximised window fits between the strip and the bar.
3. With Machitan running, exit Traydio (its tray menu, or Task Manager) and start it again: its icon shows within about 2 s. The log shows "another "Shell_TrayWnd" window (explorer.exe) was in front ...", then "asked ... window(s) ... Trdo.exe" and "add from Trdo.exe". Same for the other Traytober apps. If the icon still does not show and no "another ... window" line comes near Traydio's start, the cause is something else: send the log.
4. Tray icons already there still work (left and right click); exit Machitan: Explorer's taskbar comes back as before.
5. Delete `Shell/AppBarSender.cs` (now empty) and `%AppData%\Machitan\shell32.dll`.

#### antislop Delivery Gate (during mode), Part 15 UI

Design read: no new visuals; Windows now applies Machitan's reservations (the strip and the bar's space), and tray icons that reached only Explorer are asked for again. Dials unchanged. Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS (no em dashes). R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (nothing new is shown; log lines say what Windows sent and answered). R-23, R-24, R-25 N/A (no pictures). R-26 PASS (no new control). R-27 PASS (block not openable, no block for Explorer, shlwapi missing: passed on unchanged and logged; no answer within 4 s: logged, not sent twice; a process whose start time cannot be read: left alone). R-32, R-33, R-34 N/A (no new control). R-35 NOT RUN (Claude cannot run on this PC; bracket and text checks and code inspection, no compiler in this workspace; Sam's build is the run check). R-37 PASS. R-39, R-40 N/A.
- Block 2: nothing decorative added (PASS). Block 3: dials kept. Block 4: C-1 to C-5 PASS (C-4: tray messages are passed on to Explorer as before; AppBar messages carry the same data, in a block Explorer can open).

### Part 16 - tray box stays for a right-click menu, peek at the desktop, Windows' own peek for previews, the Start button's own pictures

Sam (2026-11-20, after r48): "strip and bar's area fixed. traydio still doesn't fixed. also, make the taskbar tray box keep visible after i right-clicked a tray icon there. plus: * if i hover over the show desktop strip, show the empty desktop (this is called 'aero peek') * find out how the existing peek from taskbar's thumbnail could be made better (closer to real windows' taskbar's thumbnail peek) * add option to customize start menu (3 states: normal, hovered, pressed)"

Read as: the Start *button* (the bar's Start bubble) with the user's own pictures for normal, hovered and pressed.

**Status: built and run (Sam, 2026-11-20 21:09, r49).** The log shows Windows' own peek working for both uses ("kind 3" for a window from the previews, "kind 1" for the desktop) and Sam's three Start pictures chosen; no problem reported with items 1 to 3. Sam: "by 'pressed' state, i mean, keep it that way when start menu opens" (Part 16b). Traydio: still no icon; see Part 16b. No new packages. New options are off by default.

1. **The tray box stays while a right-clicked icon's menu is open.** Before, the app's menu took the focus, the box lost it and closed under the menu. Now a right-click keeps the box open while the icon's app (its menu is one of its windows) or Machitan is in front. It closes when another app comes to the front, when a mouse button is pressed outside the box, the ^ button and the app's windows, or when a button pressed in the app's menu (a choice) is let go, as Windows' own box does once a menu item is picked. A click back in the box gives the usual rules back. `Flyouts/TrayBoxWindow.cs`, `Flyouts/FlyoutWindow.cs` (a flyout can ask to stay when it loses the focus), `Shell/TrayIconService.cs` (`OwnerOf`), `Interop/User32.cs` (`WindowFromPoint`, `GA_ROOT`).
2. **Peek at the desktop** (Settings > Taskbar > Layout > Peek at the desktop, off by default, as in Windows 11). Resting the pointer on Show desktop for half a second turns every window see-through so the desktop shows; moving away or clicking ends it. Nothing is minimised or moved. Windows 7 and 10 did this with Windows' own peek; Machitan uses the same call (item 3). The bar, Dynaland, the corner widgets and open flyouts stay solid. Not while the desktop is already shown, nor in custom mode. `Bar/BarWindow.Layout.cs`, `Bar/BarWindow.xaml.cs` (ended when Machitan exits), `Config/ShellSettings.cs`, `Config/SettingsStore.cs` ("PeekAtDesktop"), `SettingsUi/SettingsWindow.xaml.cs`, `Translations/*`.
3. **Thumbnail peek, closer to Windows' taskbar.** What Windows does that Machitan did not:
   - Windows does not move the window. DWM draws it in place and turns every other window see-through. Machitan brought the window to the front (made always-on-top for that moment, since Windows does not let a background program move another app's window in front of the active one) and put it back afterwards.
   - A minimised window shows as it last looked; Machitan restored it and minimised it again, which plays both animations.
   - Windows fades the peek in and out (DWM's own animation).
   - Nothing changes in z-order or always-on-top, so nothing can be left wrong when the peek ends.
   - Windows honours an app's wishes: no peek for a window that asked for none (DWMWA_DISALLOW_PEEK), a window that asked to stay solid stays solid (DWMWA_EXCLUDED_FROM_PEEK), and an app's own peek picture (DwmSetIconicLivePreviewBitmap) is shown.
   Now the previews use Windows' own peek: `Shell/LivePeek.cs` (new) calls dwmapi.dll's undocumented ordinal 113 (`DwmpActivateLivePreview`, the call Explorer's taskbar and third-party taskbars use), with the preview kept on top and Machitan's visible tool windows marked DWMWA_EXCLUDED_FROM_PEEK. Its fourth value is the kind of peek; third-party taskbars use 1 for the desktop and 3 for a window. Neither is documented, so if Windows refuses one kind the other is tried once, and the log says which worked ("Peek: Windows' own peek works (first this run: ..., kind ...)"). If Windows refuses both (Windows' "Enable Peek" off in Performance Options, or the call gone from a later Windows), the previews peek the old way and the log says so once. A window whose app asked for no peek is not peeked at. `Shell/LivePeek.cs` (new), `Flyouts/PreviewWindow.cs`, `Interop/Dwmapi.cs` (DWMWA_DISALLOW_PEEK, DWMWA_EXCLUDED_FROM_PEEK, DwmpActivateLivePreview).
4. **The Start button's own pictures** (Settings > Start > Start button, off by default). "My own pictures" on, and three pictures: Normal, Hovered, Pressed (.png, .jpg, .jpeg, .bmp, .gif, .ico), each with a small preview, Choose picture (Windows' file picker) and Remove. The pictures take the logo's place and size, fitted with their shape kept. A state without its own picture shows the one before it (pressed: the hovered one, else normal) with the bar's usual hover or press tile behind it; a state with its own picture shows no tile (the picture is the feedback). No normal picture, or its file gone: Machitan's logo stays, and Settings says "Not found: ..." for a missing file. Choosing the normal picture turns "My own pictures" on. A picture changed under the same file name is read again when the bar is rebuilt. `Bar/StartButtonPictures.cs` (new), `Bar/BarWindow.Layout.cs`, `Bar/BarWindow.xaml.cs` (the bar is rebuilt when these change), `Config/ShellSettings.cs` (full paths only; others dropped), `Config/SettingsStore.cs` ("CustomStartButton", "StartButtonPicture", "StartButtonPictureHover", "StartButtonPicturePressed"), `SettingsUi/SettingsWindow.xaml.cs`, `Translations/*` (16 new strings, English and Indonesian).
5. **Traydio: what reaches Machitan.** r48's log (Part 15 status) rules out another `Shell_TrayWnd` window being in front: Traydio started while Machitan's window was first, and Machitan logged nothing from Trdo.exe. Two causes are left: Traydio did not send its icon at all at that moment (for example it was still running from before, only its window closed, and it did not answer "the taskbar was created"), or it sent something Machitan did not understand and dropped without a word. New log line, after the first look: the first message from each program that had sent nothing before ("Tray: first message from Trdo.exe after the first look: tray add (1484 bytes)." or "... a tray message without shell32's mark ..."), whatever it is and whether or not Machitan understands it (40 lines a run at most). `Shell/TrayIconService.cs`.

**To check:**
1. Tray box: open it (^), right-click an icon: the box stays under the app's menu. Pick a menu item: the box closes. Right-click again, press Esc, click on the desktop: the box closes. Right-click, then click another icon in the box: works as before.
2. Settings > Taskbar > Peek at the desktop on. Rest the pointer on Show desktop (the thin line at the bar's end) for half a second: every window turns see-through; move away: they come back. Click it while peeking: the desktop is shown as before. The log says "Peek: Windows' own peek works (first this run: the desktop, kind 1)" (or kind 3).
3. Previews: rest the pointer on a window's preview: that window shows in place, the rest see-through, with a short fade; a minimised window shows without restoring. Move to the next preview; move away. The log names the kind that worked for "a window from the previews".
4. Settings > Start > Start button: Choose picture for Normal (this turns "My own pictures" on): the bar's Start shows it. Add Hovered and Pressed: they show while pointing and while pressing. Remove Normal: the logo comes back. Rename the picture file: Settings says "Not found", the logo shows.
5. Traydio: open Task Manager, end Traydio (Trdo.exe), start it again, wait 10 s, send the log. Also say how Traydio was closed before (its window's X, or its tray menu's Exit).
6. Delete `%AppData%\Machitan\shell32.dll` if not done yet (`Shell/AppBarSender.cs` is already gone).

#### antislop Delivery Gate (during mode), Part 16 UI

Design read: three small additions to existing surfaces, in their existing look. Settings gets one toggle (Taskbar > Layout) and one card (Start > Start button) built from the same rows as the rest of Settings (label, grey note, control on the right; Choose picture and Remove as Radii.Small buttons, a 36 x 24 preview). The bar's Start bubble keeps its size and place; only what is drawn in it changes. Peeks are Windows' own (DWM's fade), so they look like Windows' taskbar. Dials unchanged (ENERGY 1 / RHYTHM 1 / MOTION 0 for Machitan's own parts; the peek's fade is Windows'). Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS (no em dashes). R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (only the user's own pictures and Windows' own peek are shown; Settings states what is chosen or what shows instead, nothing invented). R-23 PASS (pictures fitted with their shape kept, never stretched). R-24 PASS (a picture that cannot be read: logged, the logo or the normal picture shows). R-25 PASS (the 36 x 24 preview has no text and is hidden from screen readers; the row names the picture). R-26 PASS (every new control acts: toggles save at once, Choose picture opens the picker, Remove is greyed while nothing is chosen). R-27 PASS (picker cancelled: nothing changes; picker fails: "The picture could not be chosen. Try again." and logged; file gone: "Not found: ..." and the logo; Windows refuses its peek: the previews peek the old way, the desktop peek does nothing, logged once; desktop window not found: logged). R-32 PASS (every new control is a standard toggle or button, reached with Tab). R-33, R-34 PASS. R-35 NOT RUN (Claude cannot run on this PC; bracket and text checks, string-table check (405 entries, same keys in both languages, sorted) and code inspection, no compiler in this workspace; Sam's build is the run check). R-37 PASS. R-39 PASS (Choose picture and Remove carry the picture's name for screen readers: "Choose picture: Normal"). R-40 N/A.
- Block 2: nothing decorative added (PASS). Block 3: dials kept; no new colours. Block 4: C-1 to C-5 PASS (C-4: Windows' own peek moves no window, so ending it puts nothing back; it ends when the pointer leaves, on a click, and when Machitan exits; the tray box keeps closing on outside clicks).

### Part 16b - the Start button stays pressed while Start is open; what Traydio sends

Sam (2026-11-20 21:09, after r49): "1. done. you can read the log in the directory. im always closing it by using task manager because this issue still unfixed yet. 2. by 'pressed' state, i mean, keep it that way when start menu opens."

**Status: item 1 delivered (r50), waiting for build and run report. Item 2: Sam said "go" (21:41) to watching Traydio; the cause was found, see Part 17.** No new packages.

1. **Pressed while Start is open.** The Start button's own pressed picture now shows for as long as Machitan's Start menu is open, not only while the button is held down; it goes back to the hovered or normal picture when Start closes (by the button, Esc, the Win key or a click elsewhere). Without a pressed picture, the hovered one shows while Start is open (the same order as before: pressed, else hovered, else normal). Start tells the bar when it opens and closes (`StartMenuWindow.OpenChanged`); a bar rebuilt while Start is open starts pressed. The note under "My own pictures" says so. `Start/StartMenuWindow.xaml.cs`, `Bar/StartButtonPictures.cs`, `Bar/BarWindow.Layout.cs`, `Bar/BarWindow.xaml.cs`, `Translations/*` (SetOwnPicturesNote).
2. **Traydio: what the r49 log shows.** Traydio (Trdo.exe) was started fresh at about 21:00:20, while Machitan had been running since 20:59:21 with its tray window first. No "Tray: first message from Trdo.exe" line came: Trdo.exe sent nothing at all to Machitan's window, not a tray message Machitan dropped. At 21:00:52 Machitan sent "the taskbar was created" straight to Trdo.exe's windows ("asked 18 window(s) ... explorer.exe, Trdo.exe"); nothing came back in the next 10 s either. Traydio's own manifest asks for full trust (runFullTrust), so it runs with the same rights as Machitan, and Machitan's window takes WM_COPYDATA from lower rights too; rights are not the cause. Traydio's tray icon is most likely WinUIEx's `TrayIcon` (WinUIEx is one of its packages; its source was not readable this time): a hidden WinUI window, `Shell_NotifyIcon` NIM_ADD then NIM_SETVERSION, return values not checked, re-added on "TaskbarCreated" while it should be visible. Sam always ends Traydio in Task Manager, so it never removed its icon itself. Next step: watch Traydio's own calls (`Shell_NotifyIconW`, `FindWindowW`, `SendMessageTimeoutW`) to see where its icon goes and what Windows answers; that needs Sam's go-ahead (a debugging tool attached to Traydio for a minute). No code change for this in Part 16b.

**To check:**
1. Settings > Start > Start button with a pressed picture: click Start. The pressed picture stays while Start is open; closing Start (click Start again, Esc, Win, a click elsewhere) brings back the hovered or normal picture.
2. Without a pressed picture: the hovered picture shows while Start is open.

#### antislop Delivery Gate (during mode), Part 16b UI

Design read: the Start button's pressed picture now also marks "Start is open", as a pressed toggle does; nothing else changes on screen. Dials unchanged. Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS (no em dashes). R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (only the user's own pictures). R-23, R-24, R-25 PASS (as Part 16). R-26 PASS (no new control). R-27 PASS (no pressed picture: the hovered one, else the normal one; no pictures: the logo, unchanged; a failing listener: logged, Start still opens and closes). R-32, R-33, R-34 N/A (no new control). R-35 NOT RUN (Claude cannot run on this PC; bracket and text checks, string-table check and code inspection, no compiler in this workspace; Sam's build is the run check). R-37 PASS. R-39, R-40 N/A.
- Block 2: nothing decorative added (PASS). Block 3: dials kept. Block 4: C-1 to C-5 PASS.

### Part 17 - Traydio: Machitan's tray window names Explorer's taskbar button list

Sam (2026-11-20 21:41): "go" (to watching Traydio's tray calls with a debugging tool for a minute).

**Status: delivered, waiting for build and run report.** No new packages.

What was found (Frida attached to Trdo.exe 2.1.6.0 on Sam's PC; read only, apart from sending "TaskbarCreated" to Traydio's own windows):
- Inside Traydio, `FindWindowW("Shell_TrayWnd")` returns Machitan's window, as it should. Explorer's is second.
- Sent "TaskbarCreated", Traydio called `Shell_NotifyIconW` not once in 45 s. Its UI thread was idle in its message loop, not stuck.
- Traydio's own log (`%LocalAppData%\Packages\40087JoeFinApps.Trdo_vek3qaanxbakt\LocalState\logs\traydio.log`), every start since 2026-11-20 18:20: "Unhandled XAML exception :: System.NotImplementedException: Not implemented. SetShownInSwitchers failed." at `AppWindow.set_IsShownInSwitchers` in `WinUIEx.TrayIcon..ctor`, from `Trdo.App.InitializeTrayIcon`. WinUIEx's tray icon makes a hidden WinUI window and hides it from Alt+Tab before it adds the icon (read from WinUIEx.dll's metadata); that throw stopped it, so no icon was ever added, and the music played on without one.
- Others report the same exception from the Windows App SDK while no taskbar window exists, for example at sign-in before Explorer made its taskbar.
- Windows' taskbar list (ITaskbarList, explorerframe.dll on build 26300, read in Traydio's memory): to find the taskbar's button list it calls `FindWindowW("Shell_TrayWnd")` and then `GetPropW(that window, "TaskbandHWND")`, and posts its messages (0x440, 0x441) to the window it gets. Explorer's taskbar window has that property (pointing at its `MSTaskSwWClass` button list); Machitan's had none, so every taskbar-list call failed while Machitan's window was first. That is why the Windows App SDK refused to hide Traydio's window, and why most Traytober apps (WinUI 3 tray apps on the same libraries) never showed an icon.

1. **The property.** Machitan's tray window now carries Explorer's "TaskbandHWND" property with Explorer's value, set before the window is first made the front-most one and checked every quarter second (so a restarted Explorer is followed; the property goes when Explorer's does, and when Machitan's window closes). Windows' taskbar list then reaches Explorer's own button list, as it does without Machitan. Log: "Tray: Machitan's tray window names Explorer's taskbar button list (0x...)" (the first 10 changes a run). Other properties of Explorer's window (its DPI, monitor, auto-hide state, drag-and-drop target) are not copied. Messages apps send the taskbar window (WM_USER and up) were already passed on to Explorer's. `Shell/TrayIconService.cs`, `Interop/User32.cs` (GetPropW, SetPropW, RemovePropW).
2. **Apps already running stay broken.** An app whose tray icon failed this way does not try again by itself: end it (Task Manager) and start it again once.

**To check:**
1. Build, start Machitan. The log says "Tray: Machitan's tray window names Explorer's taskbar button list (0x...)".
2. End Traydio in Task Manager, start it again: its icon shows (Dynaland's general page, or the bar's tray box), and "Tray: icon added: ... (Trdo.exe)" is in Machitan's log. Traydio's own log no longer says "SetShownInSwitchers failed".
3. The other Traytober apps: start them; their icons show.
4. Tray icons already there still work; Alt+Tab looks as before; exit Machitan: Explorer's taskbar comes back as before.

#### antislop Delivery Gate (during mode), Part 17 UI

Design read: no new visuals; apps whose tray icon never came now add it, and it shows where tray icons already show. Dials unchanged. Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS (no em dashes). R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (nothing new is shown; log lines say what Machitan copied). R-23, R-24, R-25 N/A (no pictures). R-26 PASS (no new control). R-27 PASS (Explorer's taskbar gone: the property is taken off, logged; it cannot be set: logged, tried again a quarter second later, 10 lines a run at most). R-32, R-33, R-34 N/A (no new control). R-35 NOT RUN (Claude cannot run on this PC; bracket and text checks and code inspection, no compiler in this workspace; Sam's build is the run check). R-37 PASS. R-39, R-40 N/A.
- Block 2: nothing decorative added (PASS). Block 3: dials kept. Block 4: C-1 to C-5 PASS (C-4: the property holds Explorer's own value and goes with Machitan's window; taskbar-list calls go where they go without Machitan).

### Part 18 - animations for Start, the tray box and the taskbar, and a switch to turn them off

Sam (2026-11-20 22:04): "now, the animations (start menu, taskbar, tray box) and option to turn off them."

Read as: motion like Windows 11's own for Start opening and closing, the tray box opening and closing, and the taskbar (sliding in and out when it hides, a button squishing when pressed, a new app growing into its place, the line under an app stretching), and one setting that turns all of it off.

**Status: delivered (r52), waiting for build and run report.** No new packages. The new option is off by default (animations on).

1. **Start** rises 40 DIP from the bar's side and fades in (250 ms, Windows' slow-down curve), and sinks 24 DIP and fades out as it closes (150 ms, speeding up). Opened again while it is still closing, it turns back from where it is, with no jump. Nothing in it takes a click while it closes. The window's cut (its rounded boxes, Part 11) is lifted while the boxes move, else their edges would be clipped, and comes back when they stop; the cut follows the boxes' resting place. `Start/StartMenuWindow.xaml.cs`, `Visuals/WindowCutter.cs` (Suspend, Resume).
2. **The tray box** (^) rises 16 DIP and fades in (200 ms) and fades back 8 DIP towards the bar (120 ms), the same way. Any flyout can now name what moves (`MotionTarget`); only the tray box does for now, the other flyouts open as before. `Flyouts/FlyoutWindow.cs`, `Flyouts/TrayBoxWindow.cs`.
3. **The taskbar.**
   - Pressed, a bar button's icon shrinks a little (to 86 %, 90 ms) and springs back when let go (260 ms, a small overshoot), as Windows 11's taskbar icons do. Apps, Start, search, the ^ button and tray icons on the bar. Only the icon moves, not an app's name, line or selection ring. A press that ends any other way (dragged off, a drag starts) springs back too. Left button, touch and pen only.
   - A new app on the bar (just started, or just pinned) grows from half size and fades in (300 ms). Apps already there do not, also when they move to another place or group; a full rebuild (a setting changed) shows everything at once.
   - The line under an app stretches from its centre when it appears (the app started) or changes width (the app comes to the front or goes behind), 250 ms.
   - Auto-hide's slide (already there) follows the new setting: with animations off the bar simply appears and goes.
   `Bar/BarWindow.Layout.cs` (press), `Bar/BarWindow.Dock.cs` (new apps, the line), `Bar/BarWindow.xaml.cs` (slide).
4. **Turn off animations** (Settings > General, off by default): everything above appears and goes at once. Windows' own Animation effects (Settings > Accessibility > Visual effects) being off does the same, and Machitan checks it each time, so a change applies at once. If Windows' setting cannot be read, Machitan's own setting decides alone (logged once). `Visuals/Motion.cs` (new: every animation, run by Windows' compositor on the element's own visual, so layout, clicks and the keyboard are never held up), `Config/ShellSettings.cs`, `Config/SettingsStore.cs` ("TurnOffAnimations"), `SettingsUi/SettingsWindow.xaml.cs`, `Translations/*` (2 new strings, English and Indonesian).

**To check:**
1. Click Start: it rises and fades in; click it again (or Esc, Win, a click elsewhere): it sinks and fades. Click Start twice quickly: it turns back without a jump. Its corners and the gaps between its boxes look as before once it stops; clicks in the gaps reach the desktop again. With acrylic on, the boxes' acrylic should move and fade with them (the bar's slide already moves acrylic; fading it is new). If the acrylic pops in or out instead of fading, say so.
2. Open the tray box (^) and close it: the same, smaller. Right-click an icon in it: the box stays as before (Part 16).
3. Press and hold an app on the bar: its icon shrinks; let go: it springs back. Press and drag off: it springs back.
4. Start an app that is not pinned: its button grows in. Bring another app to the front: the lines stretch and shrink. Start a pinned app: its line grows from the middle.
5. With auto-hide on, the bar still slides in and out.
6. Settings > General > Turn off animations on: all of the above happen at once, with no motion. Off again: the motion comes back. Also try Windows' Settings > Accessibility > Visual effects > Animation effects off, with Machitan's switch off: no motion either.

#### antislop Delivery Gate (during mode), Part 18 UI

Design read: Machitan's own motion follows Windows 11's: short (90 to 300 ms), things arriving slow down at the end (Windows' decelerate curve, 0.1,0.9,0.2,1), things leaving speed up (0.7,0,1,0.5), a press springs back with a small overshoot. Movement is small (8 to 40 DIP, scale 0.5 to 1 for a new app, 0.86 for a press) and always towards or away from the bar, so it says where things come from. Nothing loops, nothing waits on an animation, and nothing animates that the user did not cause. MOTION dial raised from 0 to 1 for Machitan's own parts (ENERGY 1 / RHYTHM 1 / MOTION 1). Reduced motion: Windows' Animation effects off turns every animation off, as does Machitan's own switch. Settings gets one toggle in the General card, built like the rows around it. Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS (no em dashes). R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (nothing invented; only real items move). R-23, R-24, R-25 N/A (no new pictures). R-26 PASS (the new toggle acts at once and is saved). R-27 PASS (an animation that cannot start: the element is put at rest and shown, logged; Windows' setting unreadable: logged once, Machitan's switch decides; the window cut could not be lifted: logged, the box may be clipped at its edges while it moves). R-32 PASS (a standard toggle, reached with Tab). R-33, R-34 PASS. R-35 NOT RUN (Claude cannot run on this PC; bracket and text checks, string-table check (407 entries, same keys in both languages, sorted) and code inspection, no compiler in this workspace; Sam's build is the run check). R-37 PASS (Windows' reduced-motion setting honoured). R-39 PASS (the toggle carries its name for screen readers). R-40 N/A.
- Block 2: nothing decorative added; every motion shows a change the user caused (PASS). Block 3: MOTION 0 to 1 as above; no new colours. Block 4: C-1 to C-5 PASS (C-4: a menu hidden after its closing motion is hidden only if it was not opened again meanwhile; the window cut always comes back when the motion ends or is replaced; Machitan's exit is unchanged).

### Part 19 - Exit Machitan, always in Start's power menu

Sam (2026-11-20 22:48): "add 'Exit Machitan' as mandatory option in the Power button."

**Status: delivered (r53), waiting for build and run report.** No new packages. Nothing to turn on: the entry is always there.

1. **Start's power menu ends with Exit Machitan**, below a line, after whatever power entries are chosen in Settings > Start (also when none are: then "Nothing chosen in Settings > Start", the line, Exit Machitan). It cannot be turned off. It asks nothing, like Exit on the bar's own menu, and does the same thing (Machitan closes cleanly and Explorer's taskbar comes back). Log: "Start: Exit Machitan chosen from the power menu." then the usual shutdown lines. `Start/StartMenuWindow.xaml.cs`.
2. **Settings > Start > Power menu** lists Exit Machitan last, with its toggle on and greyed, and the note "Always in the menu, so Machitan can always be closed from Start." `SettingsUi/SettingsWindow.xaml.cs` (`AlwaysOnRow`), `Translations/*` (1 new string; "Exit Machitan" is the bar menu's existing text).
3. **Wording fix for Part 18's note** under Turn off animations, to use the names the rest of Settings uses: "Start, the hidden icons (^) and the bar appear and go at once ..." (Indonesian: "Mulai, ikon tersembunyi (^), dan bilah ..."). `Translations/*`.

**To check:**
1. Open Start, click the power button: the menu ends with a line and Exit Machitan. Click it: Machitan closes as with the bar menu's Exit, and Explorer's taskbar comes back.
2. Settings > Start > Power menu: turn every entry off. The power menu shows "Nothing chosen in Settings > Start", the line, Exit Machitan.
3. Settings > Start > Power menu: Exit Machitan is the last row, its toggle on and greyed.

#### antislop Delivery Gate (during mode), Part 19 UI

Design read: one more entry in an existing menu, after a separator as on the bar's menu, so closing Machitan sits apart from the PC's power entries; one more row in an existing Settings card, built from the same toggle row, shown on and greyed because it cannot change. Dials unchanged (ENERGY 1 / RHYTHM 1 / MOTION 1). Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS (no em dashes). R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (a real action; nothing invented). R-23, R-24, R-25 N/A (no pictures). R-26 PASS (the entry acts; the greyed toggle says why it cannot change). R-27 PASS (the exit path is the bar menu's own, which logs each step and survives a closing menu). R-32 PASS (a standard menu item, reached with the arrow keys; the greyed toggle is skipped by Tab as Windows does). R-33, R-34 PASS. R-35 NOT RUN (Claude cannot run on this PC; bracket and text checks, string-table check (408 entries, same keys in both languages, sorted) and code inspection, no compiler in this workspace; Sam's build is the run check). R-37 PASS. R-39 PASS (the greyed toggle carries its name and why: "Exit Machitan (Always in the menu, ...)"). R-40 N/A.
- Block 2: nothing decorative added (PASS). Block 3: dials kept. Block 4: C-1 to C-5 PASS (C-4: one exit path for both menus; nothing new to undo).

### Part 20 - an installer

Sam (2026-11-20 22:54): "well, the test is clean, but idk how to make an installable .exe."

Read as: r49 to r53 are built and tested; now a Setup .exe to install Machitan with.

**Status: delivered (r54), waiting for Sam to make and try the installer.** No new NuGet packages and no code change. One new tool on Sam's PC: Inno Setup (free, used for installers by many apps; `winget install --id JRSoftware.InnoSetup.7 -e`).

1. **`build-installer.cmd`** (new, in the project folder): publishes a Release build (x64, self-contained, as the project is set up) to `publish\Machitan`, emptied first, then compiles the installer. It finds Inno Setup 7 or 6 on the PATH or in its usual folders and says how to install it if it is missing. Output: `publish\Machitan-Setup-<version>.exe` (`publish\` is already in `.gitignore`). The folder opens at the end; the window stays open on an error.
2. **`installer\Machitan.iss`** (new), the installer itself (Inno Setup's standard wizard, English):
   - The version is Machitan.exe's own (`<Version>` in `Machitan.csproj`, now 0.1.0); the same AppId on every version, so a newer installer updates an installed Machitan in place.
   - Installs for all users in `C:\Program Files\Machitan`, with Windows' administrator prompt once. Not the user's own folders: "Shortcuts in apps running as administrator" makes a scheduled task that runs this Machitan.exe as administrator, and only a folder that needs administrator rights to change keeps that safe.
   - The GPL licence page; Start menu entry; desktop shortcut only if ticked; LICENSE and THIRD-PARTY-NOTICES.md go with the files.
   - A running Machitan is asked to exit cleanly first, through the same signal as `Machitan.exe --exit` (so Explorer's taskbar comes back), up to 20 s; the Ready page says so. If it does not close, Setup stops and says to close it from the bar's menu. Windows' Restart Manager covers anything left using the files; nothing is restarted behind the user's back.
   - At the end, "Launch Machitan" (ticked) starts it with the user's normal rights, never as administrator.
   - Uninstall (Windows' Settings > Apps): Machitan is asked to exit the same way; its files and shortcuts go; its "Run at startup" entry goes if it names this copy; the `Machitan\AdminKeys` scheduled task goes. `%AppData%\Machitan` (settings, pins, layout, log) stays for a later install.
   - Windows 10 2004 (build 19041) or later, x64 (and Windows on Arm, which runs x64 apps), as the project targets. Setup writes its own log to `%TEMP%` (Setup Log ....txt).
   - Not signed (that needs a paid code-signing certificate): on another PC, Windows SmartScreen may warn first (More info > Run anyway).
3. **`README.md`**: a new "Installer" section.

**To check:**
1. Install Inno Setup 7 once: `winget install --id JRSoftware.InnoSetup.7 -e` (or from jrsoftware.org).
2. Close Visual Studio's running Machitan if any (not needed, but simpler). Double-click `build-installer.cmd`. It ends with "Done." and opens the `publish` folder with `Machitan-Setup-0.1.0.0.exe`. If it stops with an error, send the window's text.
3. Run the installer with Machitan running: it closes Machitan (Explorer's taskbar shows), installs, and starts it again at the end. Machitan's log shows the start from `C:\Program Files\Machitan`.
4. Start menu (Machitan's and Windows'): "Machitan" is there.
5. Settings > General > Run at startup on, sign out and in: the installed Machitan starts.
6. Windows' Settings > Apps > Installed apps > Machitan > Uninstall: Machitan closes, the taskbar is Explorer's, `C:\Program Files\Machitan` is gone, `%AppData%\Machitan` is still there.

#### antislop Delivery Gate (during mode), Part 20 UI

Design read: no change to Machitan's own screens. The installer is Inno Setup's standard modern wizard (Windows' look, its own dark mode), with Machitan's icon; its one added sentence (the Ready page) says that a running Machitan is closed first. Dials unchanged. Desktop: web-only items N/A.
- Block 1: R-02 PASS (no em dashes). R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (nothing invented; the version is read from Machitan.exe). R-23, R-24, R-25 N/A. R-26 PASS (every wizard control is Inno Setup's own and acts). R-27 PASS (Inno Setup missing: the script says how to get it; build or compile failing: it stops, says which step and keeps the window open; Machitan not closing: Setup stops with what to do; schtasks finding no task on uninstall: nothing happens). R-32 PASS (Inno Setup's wizard works with the keyboard). R-33, R-34 N/A. R-35 NOT RUN (Claude cannot run on this PC or compile with Inno Setup here; the parts that could be wrong (reading the version from Machitan.exe, the Run flags, the mutex check, the [Setup] directives) were checked against Inno Setup's help; Sam's run of build-installer.cmd is the check). R-37 PASS. R-39 PASS. R-40 N/A.
- Block 2: nothing decorative added (PASS). Block 3: dials kept. Block 4: C-1 to C-5 PASS (C-4: install and uninstall close Machitan the clean way, so Explorer's taskbar is always put back; user data is kept).

### Part 20b - the installer says 0.9.0, not 0.9.0.0

Sam (2026-11-20 23:06): "i've changed the version to 0.9.0, but why it reports as 0.9.0.0?" Then (23:07): "go ahead." (to showing 0.9.0).

Why: a Windows program's version always has four numbers (major.minor.build.revision), so .NET stamps `<Version>0.9.0</Version>` on Machitan.exe as 0.9.0.0, and the installer read it from there. Same version either way.

**Status: delivered (r55), waiting for Sam to make the installer again.** No code change.

1. `installer\Machitan.iss`: a last ".0" is left out of the installer's file name (`Machitan-Setup-0.9.0.exe`), Setup's title and Windows' Apps list (0.9.0). Only a last ".0": 0.9.0.3 stays 0.9.0.3. Setup's own file version keeps all four numbers (Windows needs them); its product version says 0.9.0. Windows' file details of Machitan.exe and Machitan's log line ("Version 0.9.0.0") still show four numbers.
2. `README.md`: says so.

**To check:** run `build-installer.cmd`: the `publish` folder has `Machitan-Setup-0.9.0.exe`. Install it: Windows' Settings > Apps > Installed apps shows Machitan 0.9.0.

No UI of Machitan's changed (no Delivery Gate block).

### Part 21 - test windows and debug files out; .gitignore updated

Sam (2026-11-20 23:11): "btw, can you delete excess files and debug-related files, as well as updating .gitignore?"

Read as: the early test windows and Visual Studio's debug profile go; nothing Machitan uses changes.

**Status: delivered (r56); Sam deletes the files listed below (Claude can write files on Sam's PC but not delete them), then builds.** No new packages. Nothing changes for the user.

1. **Test windows out of the code.** `--proof` (Batch 0's acrylic proof window, `MainWindow`) and `--seethrough` (Batch 2's see-through test window, `SeeThroughTestWindow`) were only for those early checks; their findings live on in `Visuals/SeeThrough.cs`, `Visuals/TransparentBackdrop.cs` and `Visuals/WindowCutter.cs`. `Program.cs` no longer knows the two switches (`EntryPoint.Mode` gone), `App.xaml.cs` starts only the shell. Comments that named the test now name Batch 2's test (`Bar/BarWindow.xaml.cs`, `Visuals/SeeThrough.cs`, `Visuals/TransparentBackdrop.cs`). `README.md`: the two rows are gone from "Running".
2. **Files for Sam to delete** (GitHub Desktop then shows them as deleted):
   - `MainWindow.xaml`, `MainWindow.xaml.cs`
   - `SeeThroughTestWindow.xaml`, `SeeThroughTestWindow.xaml.cs`
   - `Core\StartupMode.cs`
   - `Properties\launchSettings.json` (Visual Studio's F5 profile with native debugging on, from an earlier crash hunt; F5 works without it, and it is now ignored, so a profile Visual Studio makes later stays on that PC), then the empty `Properties` folder.
   The build works before and after the deletion: nothing uses these files any more.
3. **`.gitignore`**: also ignores Visual Studio's own debug profile (`Properties/launchSettings.json`, `*.pubxml.user`), Rider's `*.DotSettings.user`, Windows trace files (`*.etl`), code-signing keys (`*.pfx`, `*.p12`, `*.snk`: never to be committed), and editor or merge leftovers (`*.bak`, `*.tmp`, `*.orig`). It says that `publish/` also holds the installer.
4. **`.gitattributes`**: `.cmd`, `.iss` and `.resx` files keep Windows line endings (a batch file with Unix line endings can break, for example `build-installer.cmd`'s `call :find`).

Kept on purpose: `.github/copilot-instructions.md` (Sam's note for Copilot about matching a visual reference), and `bin/`, `obj/`, `.vs/` (build output, already ignored; Visual Studio makes them again). Outside the project: `%AppData%\Machitan\shell32.dll`, if still there, is the copy Sam made for Part 15 (to read Windows' code) and can go.

**To check:**
1. Delete the files in item 2. Build: 0 errors, 0 warnings. Run (F5): Machitan starts as before.
2. GitHub Desktop: the deleted files show as deleted; no `bin`, `obj`, `.vs` or `publish` files show.

No UI of Machitan's changed (no Delivery Gate block).

### Part 22 - the Windows App SDK's AI parts out; two installers (Full and Trimmed)

Sam (2026-11-20 23:29): "how did the installer has 63.2MB???" Then (23:44): "make two installers, both has AI parts trimmed: * Full: including every needed files. * Trimmed: just like Full, minus .NET 10 runtime."

What made it big (Sam's `publish\Machitan`: 523 files, 245.5 MB, packed into 63.2 MB): the .NET 10 runtime (about 85 MB), WinUI 3 and the rest of the Windows App SDK (about 75 MB), the C# bridge to Windows' APIs (`Microsoft.Windows.SDK.NET.dll`, 25 MB), and the Windows App SDK's AI parts, which Machitan never uses (about 62 MB: `onnxruntime.dll` 24 MB, `DirectML.dll` 19 MB, AI imaging, text and video, semantic search, Widgets, Workloads). The AI parts came with the all-in-one `Microsoft.WindowsAppSDK` package.

**Status: built; installers made (Sam, 2026-11-21 00:00): `Machitan-Setup-0.9.0-Full.exe` 45.1 MB (was 63.2 MB with the AI parts), `Machitan-Setup-0.9.0-Trimmed.exe` 20.8 MB. Sam (00:03) installed and runs the Trimmed edition; he takes Full as working too (the same app files, with .NET).** The package change was agreed by Sam (23:44). No code change.

1. **`Machitan.csproj`: only the Windows App SDK parts Machitan uses.** `Microsoft.WindowsAppSDK` 2.7.0 (all-in-one: AI, Base, DWrite, Foundation, InteractiveExperiences, ML, Runtime, Search, Widgets, WinUI) is replaced by `Microsoft.WindowsAppSDK.WinUI` 2.7.0 and `Microsoft.WindowsAppSDK.Runtime` 2.7.0. These were already in the project (the all-in-one package pulled them in); WinUI brings Foundation, InteractiveExperiences, Base and WebView2 with it, as before. Gone: AI, ML, Search, Widgets and DWrite (DWriteCore). Checked first: Machitan's code uses only `Microsoft.UI.*` (WinUI, composition, windowing, input, dispatching), nothing from `Microsoft.Windows.*` or `Microsoft.Graphics.*`; WinUI's own DLLs (Microsoft.ui.xaml.dll, WinUIEdit.dll, Microsoft.UI.Xaml.Controls.dll) name Windows' own `DWrite.dll`, not DWriteCore, and no AI file. Visual Studio restores the packages at the next build.
2. **Two installers.** `build-installer.cmd` publishes twice and compiles `installer\Machitan.iss` twice (`/DEdition=Full`, `/DEdition=Trimmed`):
   - **Full** (`publish\Machitan-Full`, `Machitan-Setup-<version>-Full.exe`): self-contained, as before.
   - **Trimmed** (`publish\Machitan-Trimmed`, `Machitan-Setup-<version>-Trimmed.exe`): the same without the .NET 10 runtime (published with `--self-contained false`); the Windows App SDK still goes with it (`WindowsAppSDKSelfContained` is unchanged). Machitan needs only the `Microsoft.NETCore.App` framework (read from the Full build's `Machitan.runtimeconfig.json`), which both the ".NET Runtime 10" and the ".NET Desktop Runtime 10" bring. Before anything else, Setup looks for a .NET 10 runtime where .NET's installer records x64 .NET (`InstallLocation` under `HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64`, both registry views) and in `C:\Program Files\dotnet`. Missing: Setup says so, offers to open https://dotnet.microsoft.com/download/dotnet/10.0, and stops without changing anything.
   - Both editions are the same app to Windows (one AppId, one entry in Settings > Apps); either one updates or replaces the other.
   - **The old files go first.** Before copying, Setup removes everything in the install folder except its own uninstaller, but only in a folder that holds `Machitan.exe` and `Machitan.dll`. Without this, the Full edition's .NET files would make a Trimmed install's `Machitan.exe` look for .NET in its own folder (and fail), and an older install's 62 MB of AI files would stay.
   - The publish folder is emptied of older results first (including the old `publish\Machitan` and older Setup files). The old `publish\Machitan` folder name is not used any more.
3. **`README.md`**: the two installers.

**To check:**
1. Build in Visual Studio (it restores the new packages): 0 errors, 0 warnings. Run (F5): Machitan works as before (bar, Start, flyouts, tray, Dynaland, Settings).
2. Run `build-installer.cmd`: it ends with "Done." and lists `Machitan-Setup-0.9.0-Full.exe` and `Machitan-Setup-0.9.0-Trimmed.exe`. Expected: Full clearly smaller than 63.2 MB; Trimmed smaller again. Please say both sizes.
3. Install Full over the installed 0.9.0: Machitan starts; `C:\Program Files\Machitan` has no `onnxruntime.dll` or `DirectML.dll`.
4. Install Trimmed over it: Machitan starts (this PC has .NET 10 from Visual Studio); `C:\Program Files\Machitan` has no `coreclr.dll`. Then install Full again: it starts.
5. If a PC without .NET 10 is at hand: Trimmed says .NET 10 is missing and stops.

#### antislop Delivery Gate (during mode), Part 22 UI

Design read: no change to Machitan's own screens. The Trimmed installer adds one message box, in Inno Setup's standard look, for a missing .NET 10 runtime: what is missing, what to do (the download page, then Setup again) and the other way (the Full edition). Dials unchanged. Desktop: web-only items N/A.
- Block 1: R-02 PASS (no em dashes). R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (nothing invented; .NET is looked for where .NET records itself). R-23, R-24, R-25 N/A. R-26 PASS (Yes opens the page, No closes; both stop Setup before any change). R-27 PASS (.NET missing: said, with the way out; an old file that cannot be removed: logged, Setup's usual file-in-use handling follows; build or compile failing: the script stops and says which step). R-32 PASS (a standard message box, keyboard-usable). R-33, R-34 N/A. R-35 NOT RUN (Claude cannot run on this PC or compile here; the package graph was read from Sam's `obj\project.assets.json`, the code and WinUI's DLLs were searched, and Inno Setup's help was checked for the `/D` option; Sam's build and installs are the check). R-37 PASS. R-39 PASS. R-40 N/A.
- Block 2: nothing decorative added (PASS). Block 3: dials kept. Block 4: C-1 to C-5 PASS (C-4: an edition switch removes the other edition's files, so nothing mixes; user data stays).
