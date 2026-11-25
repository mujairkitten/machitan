# Machitan status

The resume note. Read it first, then `CHANGELOG.md` (what each version changed) and `README.md`. Sam (the owner) builds and runs; Claude writes the code. Updated 2026-11-25, during v0.9.2.

## Numbering

Work is numbered by version: this is v0.9.2. The old batch, part and line numbers are retired; do not use them in new work. `CHANGELOG-i1.md` stays as it is: the detailed working history up to v0.9.1 and the first v0.9.2 changes (its "Part" numbers are history). This file replaces `HANDOFF-i1.md`.

## Versions

| Version | Date | State | What |
|---|---|---|---|
| 0.9.0 | 2026-11-21 | Released (GitHub tag `v0.9.0`) | First public beta: the bar (bubbles, joined or full width, pins, Show Desktop with peek, auto-hide, previews, drag and drop, groups, custom mode, tray with its ^ box, own Start button pictures, wallpaper accent, animations), Machitan's Start (search, pins, All apps, app menus, file previews, folders, power menu), the keyboard (Win, Win+1..9, Win+N, Win+A, Win+`, Win+X, optionally while an admin app runs), the info line and status ring, Dynaland (music, volume, camera and mic dots, tray icons), Settings, English and Indonesian, run at startup, two installers (Full, Trimmed). |
| 0.9.1 | 2026-11-23 | Released (commit "v0.9.1"; Sam made its installers) | Windows' notifications in Dynaland (with their pictures), Start's new layout (greeting, pinned categories, Most used, letters, drag and drop), Lock in the power menu, ring and Dynaland options (low-battery level, transparency, trapezoid notch), Win+N and Win+A by their widgets, bar fixes. |
| 0.9.2 | - | In progress | See "v0.9.2" below. |

## The project

Machitan is an alternative shell for Windows 11: its own taskbar ("the bar", an AppBar at the bottom by default), Start menu, flyouts (Win+N calendar, Win+A quick settings, Win+Tab switcher, Win+X menu), corner widgets (info line, status ring), Dynaland (a notch-style pill at the top), tray icons received straight from apps (Machitan has its own `Shell_TrayWnd` window), custom mode, Settings window, English and Bahasa Indonesia.

- Folder: `C:\mujairkitten\projects\machitan` (Sam's PC). Sam opens `Machitan.csproj` in Visual Studio 2026 and uses GitHub Desktop. Public on GitHub: github.com/mujairkitten/machitan.
- Stack: .NET 10 (`net10.0-windows10.0.19041.0`), WinUI 3, Windows App SDK 2.7.0 (only `Microsoft.WindowsAppSDK.WinUI` and `.Runtime`, not the all-in-one package; do not downgrade), System.Management 10.0.14, x64 and ARM64 (ARM64 since v0.9.2, not built yet), unpackaged, self-contained (the Trimmed installers are the same without .NET).
- Version: `<Version>` in `Machitan.csproj` (0.9.2 now) is the installed version's source of truth; the installers take it from Machitan.exe.
- PC: Windows 11 26H2 build 26300.10066, 1920 x 1200 at 150%, laptop (battery).
- Machitan's data and log: `C:\Users\Sammy\AppData\Roaming\Machitan` (`settings.json`, `bar-layout.json`, `pins.json`, `start-pins.json`, `app-usage.json`, `diagnostics.log`). The log is the main evidence: ask Sam for it.

## Hard limits (Sam's, not negotiable)

- WinUI 3 + native Win32 interop only. No WebView/WebView2, no Rust, no web tech.
- No fake content anywhere: every number, icon and list item comes from real data or is honestly empty.
- No notification history (Machitan shows Windows' own current list and keeps no copy).
- No git commands: Sam uses GitHub Desktop. Git's state can be read from the `.git` files (`logs/HEAD`, objects) without running git.
- No new NuGet packages unless a part truly needs one; say so first.
- Never call `SHGetFileInfo`. `IShellLinkW.GetPath` gets `IntPtr.Zero` for its find-data argument.
- `MoveWindow` never restores a minimised window (`SW_RESTORE` first). `RegisterHotKey` error 1409 means Windows owns the chord: log it and skip.
- No `DesktopAcrylicBackdrop` for the status ring or Dynaland.
- New optional options are off by default (unless Sam agrees otherwise). New settings go in the existing settings model with fresh-install defaults; never wipe an existing settings.json.
- Failures go in diagnostics.log, never fatal.
- 0 compiler warnings.
- Talk to Sam in easy, short words; technical terms only when needed.
- Complete files with exact paths, no placeholders, no truncation. Fix what Sam reports before moving on.
- If a part fails and cannot be fixed in a reasonable attempt, leave it out, say so plainly, and move on.
- Stale bin/obj: Sam deletes bin/, obj/ (and .vs/) and rebuilds; don't "fix" a CS1061 about a deleted symbol by adding deleted code back.

## Code rules that keep the build clean

- All text people see is in `Translations/Strings.resx` (English) and `Translations/Strings.id.resx` (Indonesian), used as `Strings.Name`; values through `Fmt.F(Strings.Name, value)`. `Strings.X` are properties, not constants (no `const`, `case`, default parameters). Log messages stay English. VS regenerates `Strings.Designer.cs`; if you add strings by hand, add the Designer properties too (sorted case-insensitively; resx entries sorted ordinally). See `TRANSLATING.md`. Win+X labels mark their key with `&`.
- Files: ASCII, CRLF. `.resx` and `Strings.Designer.cs` are UTF-8 with BOM. XAML keeps its BOM state. Sam's `MachitanLogo.xaml(.cs)` are his (UTF-8, LF): touch minimally.
- Use `LibraryImport`, not `DllImport`. No `await` inside `unsafe` classes or methods (CS4004). Don't name a type `Com` or a member `Windows`. Raw COM goes through `ComCall` vtable slots; known COM interfaces may use `[ComImport]`.
- Watch WinUI name clashes: `Microsoft.UI.Xaml.Automation.ToggleState`, `Microsoft.UI.Xaml.Controls.Primitives` types, `System.IO.Path` vs `Microsoft.UI.Xaml.Shapes.Path` (use aliases).
- antislop ("during" mode): each UI part ends with an antislop Delivery Gate block in this file's v0.9.2 log; R-35 is always "NOT RUN" (Claude cannot run the app).

## How the work runs

- Claude writes complete files into Sam's folder through the device bridge: first it re-reads each file and checks its modification time (Sam may have changed it; merge, never overwrite his change), then writes, reads back and compares hashes. Claude keeps a copy of every delivered file.
- After each part: report (what was built, what to check), then wait for Sam's go-ahead. Sam builds, runs, reports build errors verbatim and what he sees.
- When a part is green: a short `CHANGELOG.md` entry under its version (what was built, what was checked, what was left out and why) and this file updated.
- Claude cannot delete files on Sam's PC: list them for him.
- 2026-11-23: a Discard all changes in GitHub Desktop took two days of uncommitted work (put back from Claude's copies). Remind Sam to commit after each tested part.

## v0.9.2

### Done first (2026-11-24; builds since 2026-11-25, not run yet)

Sam (2026-11-23 23:08) asked for five things; asked back, he chose quick switches for the action center, both split shapes, and a pinned open app in both rows.

1. The bar's search icon closes Machitan's Start, then opens Windows' search (`Bar/BarWindow.Layout.cs`).
2. Action center in Win+A (`Shell/QuickToggles.cs`, `Interop/RadioManager.cs`, `Flyouts/QuickSettingsFlyout.cs`): Wi-Fi and Bluetooth (`Windows.Devices.Radios`), airplane mode (Windows' undocumented `IRadioManager`; its first reading is logged next to Windows' registry record so its meaning can be checked), mobile hotspot (`NetworkOperatorTetheringManager`, only while Windows says the connection can be shared), live captions (LiveCaptions.exe), Project and Cast (Win+P, Win+K). Tiles show Windows' state; after a click they wait, greyed, until Windows reports the new state (up to 6 s); one-second follow while open. Left out: night light, energy saver, do not disturb (no public way to switch them).
3. ARM64 (`Machitan.csproj`, `Machitan.slnx`, `build-installer.cmd`, `installer/Machitan.iss`): platforms x64;ARM64, the runtime follows the platform, four installers (x64 names unchanged, plus `-ARM64-Full` and `-ARM64-Trimmed`, `/DArch=ARM64`). No x64-only code found. Not run on an Arm PC.
4. New apps found at once (`Shell/AppWatcher.cs`, `Shell/AppCatalog.cs` `RefreshNow`, `Shell/ShellHost.cs`): the Start menu's Programs folders and Windows' package catalog; the app list is built again 2 s after the last change.
5. Split taskbar (Settings > Taskbar > Pinned and open apps: Together (default), Two rows in one bar, Two stacked bars; `TaskbarSplit`; `Bar/BarWindow.*`): pinned apps on top, open apps below; `AppBubble.PinnedPanel`, `_builtTopIds`/`_shownIds`, `SegmentHeightDip`/`BarHeightDip`; drags stay within the row's kind (`BubbleSnap.Full`).

To check (with the first v0.9.2 build): see `CHANGELOG-i1.md`, the last entry, "To check".

### Most wanted (Sam's inspiration ranking, 2026-11-24)

From five Indonesian-made shell and island apps (Abra, Nusa, Layzee, Rav, ReyLand). Built smallest first, each reported and checked before the next:

1. Volume on Dynaland's music page (from Nusa): **green (Sam's go-ahead, 2026-11-25 07:13).**
2. Search actions (from Abra): a calculator (the result is copied on Enter or click) and actions for things Machitan already does (Settings pages, power actions); ranked by `Start/SearchRank.cs`; no `web:` scope. **Green (Sam, 2026-11-25 09:34: "everything works").** Then Sam asked "how about machitan settings?": Machitan's Settings pages in search, delivered 2026-11-25, waiting for build.
3. File shelf in Dynaland: park files by dropping them on the pill, drag them out, click to open, remove; paths stored (not copies) in a new file next to `pins.json`, kept across restarts; an honest empty state.
4. In-app update: Settings > About "Check for updates" and a check at start (setting, on by default unless there is a reason not to), from github.com/mujairkitten/machitan's releases; download and install only after the user says yes, closing Machitan cleanly first and offering to start it again; an honest "the check failed" when it could not run.
5. Explorer's own flyouts replaced by Dynaland's (from Rav): spike first (volume and brightness keys handled in Machitan's keyboard hook; battery, charging and copy/paste may not be possible from an unpackaged app). Only what the spike proves ships; the rest is left out and said so.
6. Multi-monitor (from Abra): the bar, Dynaland and the corner widgets on a chosen screen, mixed scaling, everything back to the main screen when a screen goes. The widget side may come first if per-monitor bars do not fit in v0.9.2; say so plainly.

#### 1. Volume on Dynaland's music page (2026-11-25)

**Status: green.** Built (Sam, 2026-11-25 00:57: one error, a local named `volumeBar` twice in the constructor; renamed `playerVolumeBar`). Sam (01:03): "remove the volume number, move to directly below the media controls, and match the overall width of volume icon + slider to the width of the media controls." Done; Sam's go-ahead (07:13). No new packages, settings or strings.

- `Widgets/DynalandWindow.cs`: the player gets a volume line right below the music controls (previous, play, next), as wide as they are (the controls and the line share a column; the line stretches to the controls' width): the mute button (24 DIP; its icon shows the level, or muted) and a bar like the position bar (a click or drag sets the level at once, the thumb shows on hover; the mouse wheel moves it 2% a notch). No number on screen (screen readers get the level as the bar's status). It is Windows' default speakers through the same `VolumeService` Dynaland already reads four times a second, so it matches Windows on open and follows changes from anywhere (keyboard keys, Win+A, Windows' own). Like Windows' own slider, moving it while muted turns mute off. Changes made here do not also pop the volume level on the closed pill. Without speakers or headphones the line is hidden. The player keeps its height (152 DIP): the controls and the line fit beside the cover.
- The player shows while an app has a media session, playing or paused; with no media session there is no player (volume stays in Win+A).

To check:
1. Build: 0 errors, 0 warnings.
2. Play music, hover Dynaland: the volume line right below the controls, as wide as them, no number; it matches Windows' volume.
3. Drag the bar: the sound changes while dragging; click a spot: it jumps there. Mouse wheel over the line: up and down 2% a notch.
4. The mute button: mutes (the muted icon, the bar empties); again: unmutes. Mute, then drag: mute turns off.
5. Pause the music: the line still works. Change the volume with the keyboard keys while the player is open: the line follows.
6. After closing the player, the pill does not flash the volume level for changes made in the player.

antislop Delivery Gate (during mode), UI: Design read: the volume line sits under the controls at their exact width, built like the position bar (same 4 DIP bar in the cover's colour, same white thumb on hover) with a small round button like the controls, so it reads as part of them; no number, as Sam asked. Real data only (Windows' own volume). Dials unchanged (no new motion). Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS (no em dashes). R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (nothing invented). R-23, R-24, R-25 N/A. R-26 PASS (the button mutes, the bar sets the level). R-27 PASS (no speakers: the line is hidden; Windows refusing a change is logged by VolumeService). R-32 PARTIAL (Dynaland opens on hover and takes no keyboard focus, like its position bar; the keyboard way is Win+A's slider). R-33, R-34 N/A (no text). R-35 NOT RUN (Claude cannot run on this PC; bracket and text checks and code inspection, no compiler in this workspace; Sam's build is the run check). R-37 N/A (no new motion). R-39 PASS (the button and the bar are named: Mute, Volume). R-40 N/A.
- Block 2: nothing decorative added (PASS). Block 3: dials kept. Block 4: C-1 to C-5 PASS (C-4: the line is read by the existing 250 ms poll, which runs only while Dynaland is on screen).

#### 2. Search actions (2026-11-25)

**Status: green (Sam, 2026-11-25 09:34: "everything works").** No new packages or settings; 17 new strings.

- `Start/Calculator.cs` (new): an expression typed in search (numbers with a decimal point, or a decimal comma where Windows' number format uses one; + - * / and the multiplication and division signs; ^; % after a number for percent, between numbers for the remainder; brackets) gives its answer, in Windows' number format, up to ten decimals. Only a whole expression with at least one operation counts: a plain number or a name is left alone, nothing is guessed; division by zero gives no answer.
- `Start/SearchActions.cs` (new): the actions, matched with `SearchRank` (whole name, its start, or a word's start, from two letters; also a few English words, such as "volume" for Sound) and left out when an app has the same name (the app is listed): Machitan settings; Windows Settings pages (Settings, Display, Sound, Bluetooth & devices, Wi-Fi, Network connections, Personalization, Installed apps, Default apps, Notifications, Power options, Storage, Date & time, Windows Update, System); Windows tools from the Win+X menu (Task Manager, Device Manager, Event Viewer, Disk Management, Computer Management, Run); Show desktop; Lock; Start's power menu choices as the PC offers them now, with their questions; Sign out (asks); Exit Machitan. The calculator's answer goes on top, then apps and actions by fit (apps first on equal fits). `apps:` lists apps only; `files:` is unchanged. No web scope.
- `Start/StartMenuWindow.xaml.cs`: the actions among All apps' results (two-line rows: icon, name, kind); Enter does the first result; an action that asks uses the strip at the top, as the power menu does.
- `Flyouts/SearchResultsFlyout.cs`: the same in the bar's search box results (up to 8 rows); an action that asks asks in its row (its own button, or Cancel; Enter again is its button, Esc closes). `Start/ResultActions.cs`: that question row (`Question`).
- Copying uses Windows' clipboard (as "Copy as path" does).

To check:
1. Build: 0 errors, 0 warnings.
2. Start: type `2+2`: "4" on top ("Calculator: Enter or a click copies it"); Enter: Start closes, paste gives 4. Also `(3*4)/2` (6), `2^10` (1024), `50%` (0,5 or 0.5, by Windows' number format), `10 % 3` (1). `2026` alone: no calculator row.
3. Start: type `sound`, `display`, `update`, `task`, `lock`, `machitan`: the actions show with their kind; Enter or a click does them. `restart` or `shut down`: the strip at the top asks first.
4. The bar's search box: the same; `shut down`: the row asks; Cancel puts it back.
5. `apps:sound`: apps only. `files:report`: files as before.

antislop Delivery Gate (during mode), UI: Design read: actions use the file rows' look (icon, name, a dim second line saying what kind it is), so they sit with the apps as results, not as a new panel; the answer's number is semibold. Questions reuse the power menu's strip (Start) and the uninstall question's row (bar). Real data only: the power choices are what the PC offers now; the answer is computed, never guessed. Dials unchanged (no new motion). Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS. R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (nothing invented). R-23, R-24, R-25 N/A. R-26 PASS (each row does what it says; the irreversible ones ask). R-27 PASS (a failed start or copy is logged; a bad expression shows no answer). R-32 PASS (Enter does the first result; rows are buttons; the question's buttons take Tab). R-33, R-34 PASS (names trim with "..."; the question has a tooltip with its whole text). R-35 NOT RUN (Claude cannot run on this PC; bracket and text checks, the calculator's logic checked with a copy in Python, code inspection; Sam's build is the run check). R-37 N/A. R-39 PASS (rows named by their action). R-40 N/A.
- Block 2: nothing decorative added (PASS). Block 3: dials kept. Block 4: C-1 to C-5 PASS (C-4: the actions list is built per keystroke from fixed entries and the power capabilities, which are cheap reads).

#### 2b. Machitan's settings pages in search (2026-11-25)

Sam (09:34): "how about machitan settings?" Asked where and how much; Sam chose **both** (Start and the bar's search, and a box in Settings itself) and **pages only** (no single options).

**Status: built with one error (Sam, 2026-11-25 09:54: CS0117, `AutoSuggestionBoxTextChangeReason` has no `UserInputChange`; the value is `UserInput`); fixed. Then built and ran (09:59): Sam typed `bar` in Start's search and got "No app matches." (Start matched actions only at a word's start, while the Settings box matches anywhere). Changed: Machitan's pages now match anywhere in their names in Start and the bar's search too (`IsMachitanPage`, up to SearchRank's "inside"; the other actions keep "a word's start"), and the empty note says "Nothing matches." when actions were searched ("No app matches." stays for `apps:`). Waiting for build and run report.** No new packages or settings; 3 new strings (`SetSearchSettings`, `SetSearchNoPage`, `SearchNothingMatches`).

- `SettingsUi/SettingsPages.cs` (new): the 9 pages (key, name, icon) in the sidebar's order, one list for the sidebar, the Settings search box and Start's and the bar's search; `Find` ranks page names with `SearchRank` (anywhere in the name).
- `SettingsUi/SettingsWindow.xaml.cs`: the sidebar comes from that list (the page constants and the `Pages` array moved there); a search box at the top of the sidebar (NavigationView's own AutoSuggestBox: "Search settings"; it lists the pages that fit, or "No page matches"; a click or Enter opens the page, from the top, and empties the box); `GoToPage(key)` opens a page from outside the sidebar.
- `Shell/ShellHost.cs`: `OpenSettings(string page)` opens Settings at a page (`OpenSettings()` unchanged).
- `Start/SearchActions.cs`: the 9 pages, right after "Machitan settings" (second line "Machitan settings"; from two letters, anywhere in the name, as the Settings box); they are never hidden for an app with the same name (`IsMachitanPage`).
- `Start/StartMenuWindow.xaml.cs`, `Flyouts/SearchResultsFlyout.cs`: "Nothing matches." when nothing fits and actions were searched.

To check:
1. Build: 0 errors, 0 warnings.
2. Start: type `taskbar`, then `bar`: "Taskbar, Machitan settings"; Enter: Settings opens on Taskbar, at the top. Also `dyna` (Dynaland), `widgets` (Corner widgets), `edge` (Edge strip). `zzz`: "Nothing matches."; `apps:zzz`: "No app matches."
3. With Settings already open on another page: Start, type `privacy`, Enter: Settings comes forward on Privacy.
4. The bar's search box: the same.
5. Settings: the search box above the pages; type `bar`: Taskbar listed; click it: the Taskbar page, the box empties. Type `zz`: "No page matches", which opens nothing. Type `sh`, Enter: Shortcuts.
6. The sidebar still works as before (pages, their icons, the selected one).

antislop Delivery Gate (during mode), UI: Design read: the Settings box is NavigationView's own search box (the place Windows Settings has it), not a new panel; Start's and the bar's rows reuse the action rows. Real data only: the list is the sidebar's own pages and names. Dials unchanged. Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS. R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (nothing invented). R-23, R-24, R-25 N/A. R-26 PASS (each result opens the page it names). R-27 PASS (an unknown page key is logged and ignored; nothing that fits says so). R-32 PASS (the box takes Tab, arrows and Enter as every AutoSuggestBox does). R-33, R-34 PASS (page names are short; rows trim with "..."). R-35 NOT RUN (Claude cannot run on this PC; bracket and text checks, code inspection; Sam's build is the run check). R-37 N/A. R-39 PASS (the box is named "Search settings"). R-40 N/A.
- Block 2: nothing decorative added (PASS). Block 3: dials kept. Block 4: C-1 to C-5 PASS.

## Before v0.9.2 (history, short)

The details are in `CHANGELOG-i1.md`, by date.

- 2026-11-20: the bar's and the top strip's AppBar messages are passed on to Explorer by `Shell/AppBarRelay.cs` (problems 1 and 2 below); tray catch-up for icons sent while another `Shell_TrayWnd` was in front; the tray box stays open under a right-clicked icon's menu; Peek at the desktop (off by default); previews use Windows' own peek (`Shell/LivePeek.cs`, dwmapi ordinal 113); own Start button pictures (off by default, `Bar/StartButtonPictures.cs`); Machitan's tray window carries Explorer's "TaskbandHWND" property (problem 3); animations (`Visuals/Motion.cs`; Settings > General > Turn off animations, and Windows' Animation effects, stop them all); Exit Machitan always last in Start's power menu; the installer (`build-installer.cmd`, `installer/Machitan.iss`, Inno Setup 7 or 6.3+; all-users install in Program Files; install and uninstall close a running Machitan through its exit signal `Local\Machitan_ExitRequest`; unsigned); the test windows removed; the Windows App SDK's AI parts out; Full and Trimmed installers (one AppId).
- 2026-11-21: v0.9.0 tagged. The notification tray: a probe found that reading Windows' list works without app identity (live updates do not: polled, 1 s while Dynaland's general page is open, 4 s otherwise); `Shell/NotificationService.cs` (read, dismiss, clear one by one); pictures from Windows' notification database `wpndatabase.db` (`Shell/NotificationPictures.cs`, `Interop/WinSqlite.cs`, undocumented; Sam agreed).
- 2026-11-23: bar fixes (Show Desktop's line only near it, Start closes when an app on the bar is clicked, the tray ^ closes its box and turns), Lock first in the power menu, ring options (low-battery level, transparency), Dynaland options (transparency up to 50%, trapezoid notch as one XAML path), Win+N and Win+A open by their widgets; Start's new layout (660 x 480 DIP; categories in start-pins.json; Most used in app-usage.json, off by default; letters; drag and drop on a 16 ms mouse timer); v0.9.1 committed and released.
- Skills: Sam saved three skills from Microsoft's WinUI agent plugin (winui-design, winui-code-review, winui-dev-workflow). Their `winapp` commands need WinApp CLI on a Windows PC; they cannot run in Claude's workspace.
- Known small gap: a new window opened from the button of Firefox's private windows opens a normal window.

## Solved problem 1: the top strip (Settings > Edge strip > "Keep the free edge clear")

**Solved 2026-11-20** (Sam). Kept for the history: if it comes back, start from "Cause found" below.

Goal: maximised windows must stop below the corner widgets and Dynaland at the top. Machitan registers a second AppBar for that, 48 px tall (`Widgets/TopStrip.cs`), separate from the bar's AppBar (`Shell/AppBarHost.cs`).

Evidence (`diagnostics.log`, run 2026-11-20 15:27):

```
15:27:55.687 AppBar registered.                       <- the bar's AppBar (bottom)
15:27:56.405 Explorer's taskbar set to auto-hide and hidden
15:27:56.586 Strip: 48 px kept free at the top ...
15:27:57.389 Strip check (0.8 s after the strip was taken): ... free area 0,48 to 1920,1104
             (work area 0,0 to 1920,1104, strip 0,0 to 1920,48; Windows did NOT take the strip off the work area).
```

So Windows accepted the strip (ABM_NEW worked, ABM_SETPOS returned 0,0 to 1920,48) but the work area still started at y = 0. The bar's AppBar did work: the work area ended at 1104 (96 px for the bar).

Tried first, did not help: moving maximised windows into the free area by hand after the strip was taken; also screen-filling windows that are not maximised, checked whenever any window moves (`Shell/WindowChangeWatch.cs`, `Shell/MaximizedFit.cs`): Claude's window moved straight back each time and other apps showed a white or black band at their top and a gap above the bar, because the app and Windows disagree on where "maximised" is. Hand-moving maximised windows is a dead end while the work area is wrong, so `MaximizedFit` leaves maximised windows alone unless Windows took the strip. Making the strip's AppBar window a real window on the strip (layered alpha 0, click-through, HWND_BOTTOM, tool window) plus ABM_WINDOWPOSCHANGED changed nothing either.

**Cause confirmed (Sam, 2026-11-20 15:35): Machitan's own tray receiver.** With Dynaland off and "Tray on the bar" off (so the tray receiver never starts), maximised windows stop below the strip as they should.

Why: `SHAppBarMessage` finds the taskbar window by its class name `Shell_TrayWnd`. Machitan has its own `Shell_TrayWnd` window (it receives tray icons straight from apps, `Shell/TrayIconService.cs`, on its own thread, kept in front of Explorer's so apps find it first). It passes every other WM_COPYDATA, AppBar messages included, on to Explorer's window. The bar registers its AppBar before that window starts, so its messages reach Explorer directly; the strip registered after.

**Cause found (from shell32.dll on build 26300):** `SHAppBarMessage` makes the answer block (`SHAllocShared`) for the process owning the first `Shell_TrayWnd` window and writes that process's ID at 0x38 of the 0x40-byte message (block at 0x30). With Machitan's window first, Explorer cannot open the block and answers without acting. `Shell/AppBarRelay.cs` (tray thread) copies the block into one made for Explorer's process, passes the message on, and copies the answer back. In the log: "AppBar: passed on ABM_SETPOS ... (Machitan)" and "Strip check ... Windows took the strip off the work area".

An earlier try sent hand-built AppBar messages (0x38 bytes, layout from Geoff Chappell's notes on shell32 and ReactOS) straight to Explorer (`Shell/AppBarSender.cs`); its own check found the message is 64 bytes on build 26300, so it never sent. It was emptied and Sam deleted it.

Keep the bar's own AppBar working: any later ABM_SETPOS of the bar (moving to the top, size changes, a split bar's taller strip) also goes through the relay, so test that too.

Last resort if it ever breaks again: the work area can be set with `SystemParametersInfo(SPI_SETWORKAREA)`, but Explorer resets it when it gets WM_SETTINGCHANGE.

## Solved problem 2: the taskbar area safeguard

**Solved 2026-11-20** (Sam), by the same relay: the bar's later reservations went through Machitan's tray window too and hit the same cause. Facts: BarSize 64 DIP = 96 px reserved at the bottom; the visible pill is BarSize - 14 DIP, centred in that space (a split bar reserves more: its strip grows by its extra height).

## Open problem 3: tray icons of Traydio and the Traytober apps never reach Machitan

**Cause found and fixed 2026-11-20, never confirmed by Sam.** Windows' taskbar list (ITaskbarList, explorerframe.dll) finds the taskbar's button list with `GetPropW(FindWindowW("Shell_TrayWnd"), "TaskbandHWND")`. Machitan's window had no such property, so the Windows App SDK's `AppWindow.IsShownInSwitchers` threw "Not implemented. SetShownInSwitchers failed." in WinUIEx's `TrayIcon` constructor (Traydio's own log), before the icon was added. Machitan now copies Explorer's value onto its window (`TrayIconService.MirrorTaskband`). If another app breaks while Machitan's window is first, look at what it reads from the first `Shell_TrayWnd` window: Explorer's carries more properties (TaskbarMonitor, TaskbarDPI_*, IsAutoHideEnabled, TaskbarVisible, OleDropTargetInterface...), listed in `CHANGELOG-i1.md` (2026-11-20).

How it was found: Traydio (Trdo, full-trust packaged WinUI 3, WinUIEx TrayIcon) started twice while the tray receiver ran and nothing from it reached Machitan. `Shell_NotifyIconW` on build 26300 is plain: `FindWindowW("Shell_TrayWnd")`, then `SendMessageTimeoutW`. A first guess (Explorer's window in front for a moment as the app started) was wrong: Machitan logs each such moment ("another "Shell_TrayWnd" window (...) was in front") and asks apps started in the last minute again 1.5 s later, and none showed. A log line for the first message from each new program ("Tray: first message from <program> after the first look") showed nothing at all from Trdo.exe, started fresh and asked directly. Rights are not the cause (runFullTrust). If it is still broken after the property fix: with Sam's go-ahead, attach a debugging tool (Frida is installed on Sam's PC and reachable through the rea tools) to Trdo.exe and log `Shell_NotifyIconW`, `FindWindowW("Shell_TrayWnd")` (which window it returns) and `SendMessageTimeoutW` (WM_COPYDATA, its result and last error). Without the go-ahead: start Traydio while Machitan runs, exit Machitan, and see whether Windows' own tray shows it.

Key files for these problems: `Widgets/TopStrip.cs`, `Shell/AppBarHost.cs`, `Shell/AppBarRelay.cs`, `Shell/MaximizedFit.cs`, `Shell/WindowChangeWatch.cs`, `Shell/ShellHost.cs` (`KeepTopStrip`, `UpdateTopStrip`, `FitMaximizedSoon`, `StartTrayWhenWanted`), `Shell/TrayIconService.cs` (`OnCopyData`, `ForwardCopyData`, `ExplorerTray`, `MirrorTaskband`), `Shell/ExplorerTaskbar.cs`.
