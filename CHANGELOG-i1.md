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

**Status: delivered, waiting for build and run report.**

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
