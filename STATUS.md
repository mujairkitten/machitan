# Machitan status

The resume note. Read it first, then `CHANGELOG.md` (what each version changed) and `README.md`. Sam (the owner) builds and runs; Claude writes the code. Updated 2026-11-25: v0.9.2's changelog written; its checks wait for Sam (see "0.9.2 checklist").

## Numbering

Work is numbered by version: this is v0.9.2. The old batch, part and line numbers are retired; do not use them in new work. `CHANGELOG-i1.md` stays as it is: the detailed working history up to v0.9.1 and the first v0.9.2 changes (its "Part" numbers are history). This file replaces `HANDOFF-i1.md`.

## Versions

| Version | Date | State | What |
|---|---|---|---|
| 0.9.0 | 2026-11-21 | Released (GitHub tag `v0.9.0`) | First public beta: the bar (bubbles, joined or full width, pins, Show Desktop with peek, auto-hide, previews, drag and drop, groups, custom mode, tray with its ^ box, own Start button pictures, wallpaper accent, animations), Machitan's Start (search, pins, All apps, app menus, file previews, folders, power menu), the keyboard (Win, Win+1..9, Win+N, Win+A, Win+`, Win+X, optionally while an admin app runs), the info line and status ring, Dynaland (music, volume, camera and mic dots, tray icons), Settings, English and Indonesian, run at startup, two installers (Full, Trimmed). |
| 0.9.1 | 2026-11-23 | Released (commit "v0.9.1"; Sam made its installers) | Windows' notifications in Dynaland (with their pictures), Start's new layout (greeting, pinned categories, Most used, letters, drag and drop), Lock in the power menu, ring and Dynaland options (low-battery level, transparency, trapezoid notch), Win+N and Win+A by their widgets, bar fixes. |
| 0.9.2 | 2026-11-25 | Changelog written (Sam, 2026-11-25 12:18: "write me 0.9.2 changelog, the timestamp, today. i will check this when 0.9.3 is ready to ship."); not all of it has run yet, see "0.9.2 checklist" | File shelf in Dynaland, in-app update (with pre-releases), calculator and actions in search, Settings' search box, volume in Dynaland's player, action center in Win+A, split taskbar, Windows on Arm, admin shortcuts set up by the installer, new apps found at once, search icon fix. |
| 0.9.3 | - | Next | The rest of the Most wanted list (5: Explorer's flyouts, spike first; 6: multi-monitor), and the 0.9.2 checks before it ships. `Machitan.csproj` still says 0.9.2. |

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

### 0.9.2 checklist (Sam checks it before 0.9.3 ships)

What Sam has run so far, and what not. Each item's own "To check" below (or in `CHANGELOG-i1.md` for the first five) says how.

Checked by Sam:
- Volume in Dynaland's player (1).
- Calculator and actions in search, Machitan's pages in search, Settings' search box (2, 2b).

Go-ahead given, worth a full pass:
- File shelf (3): drop, restart, open, drag out (copy and move), X, "Not found", Settings > Privacy row.
- Shelf, several at once (3b): Ctrl and Shift + click, drag of picked ones, select all, take off.

Not built or run yet (as far as Claude knows):
- Shelf "Clear all" (3c).
- Installer's admin-shortcuts box (3b): ticked; on after installing; stays off after being turned off; shelf drops still work.
- In-app update (4, 4b, 4c): check (newest, failed without network), an offer (with a lower `<Version>` for a moment), "(pre-release)", the switch off gives "No release to offer", Start's "Update: x.y.z", download, SHA-256, Setup closing and restarting Machitan.
- Action center in Win+A, split taskbar (both shapes), new apps found at once, the search icon closing Start (the first five, `CHANGELOG-i1.md`).
- ARM64: the four installers from build-installer.cmd; a run on an Arm PC (none tried).

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
2. Search actions (from Abra): a calculator (the result is copied on Enter or click) and actions for things Machitan already does (Settings pages, power actions); ranked by `Start/SearchRank.cs`; no `web:` scope. **Green (Sam, 2026-11-25 09:34: "everything works").** Then Sam asked "how about machitan settings?": Machitan's Settings pages in search, **green (Sam, 2026-11-25 10:11: "next.").**
3. File shelf in Dynaland: park files by dropping them on the pill, drag them out, click to open, remove; paths stored (not copies) in a new file next to `pins.json`, kept across restarts; an honest empty state. **Go-ahead (Sam, 2026-11-25 11:30: "add 'clear all' button while continue to the next step.").** "Clear all" delivered with it; waiting for its build.
4. In-app update: Settings > About "Check for updates" and a check at start (setting, on by default unless there is a reason not to), from github.com/mujairkitten/machitan's releases; download and install only after the user says yes, closing Machitan cleanly first and offering to start it again; an honest "the check failed" when it could not run. **Delivered 2026-11-25, waiting for build.**
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

**Status: built with one error (Sam, 2026-11-25 09:54: CS0117, `AutoSuggestionBoxTextChangeReason` has no `UserInputChange`; the value is `UserInput`); fixed. Then built and ran (09:59): Sam typed `bar` in Start's search and got "No app matches." (Start matched actions only at a word's start, while the Settings box matches anywhere). Changed: Machitan's pages now match anywhere in their names in Start and the bar's search too (`IsMachitanPage`, up to SearchRank's "inside"; the other actions keep "a word's start"), and the empty note says "Nothing matches." when actions were searched ("No app matches." stays for `apps:`). Then green (Sam, 10:11: "next.").** No new packages or settings; 3 new strings (`SetSearchSettings`, `SetSearchNoPage`, `SearchNothingMatches`).

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

#### 3. File shelf in Dynaland (2026-11-25)

Sam's go-ahead: "next." (10:11).

**Status: delivered, waiting for build and run report.** No new packages or settings; 13 new strings (11 `DynShelf*`/`DynToShelf`/`DynBackToGeneral`, `SetShelf`, `SetPrivacyShelf`).

- `Shell/ShelfStore.cs` (new): the shelf, newest first, at most 50 paths, in `%AppData%\Machitan\shelf.json` (`AppPaths.ShelfFile`): `{"Shelf": [{"Path": ...}]}`. The pins' rules: written with `AtomicFile`; an unreadable file is never overwritten (changes are not saved that run, the shelf says so, and a copy is kept as `shelf.unreadable-<time>.json`). `Add` (the first dropped on top; one already there moves to the top; past 50 turned away and counted), `Remove`. Loaded at start with the pins (`ShellHost.Shelf`).
- `Widgets/DynalandWindow.cs`: a fourth open page, the shelf (`PillShape.Shelf`), as wide as the others and as tall as its rows (up to 300 DIP of rows, then they scroll; never smaller than the other pages). Reached from a shelf button on the general page (its back arrow returns there), and by dragging files or folders over the pill: it opens on the shelf with "Drop to add to the shelf" while they are over it, and closes 150 ms after the drag leaves without a drop. The drop takes the dragged items' paths (Windows' storage items; nothing copied, nothing moved: Copy, else Link, is what the drop tells the app); items without a path on this PC (inside an archive) and those past a full shelf are said in a note. After a drop the shelf stays open as when hovered. Each row: Windows' own icon or thumbnail (`GetThumbnailAsync`, never SHGetFileInfo), the name, the folder; a click opens it (`FileActions.Open`, then Dynaland closes); a drag takes it out with Copy or Move allowed, so the target follows Windows' rules (same drive moves, another copies; Ctrl, Shift); after a drag out the path is looked at for 10 s, and a file that moved away leaves the shelf; the X takes it off. Which items still exist is looked at off the UI thread each time the shelf opens (a gone network drive cannot stall Dynaland); one gone says "Not found: moved or deleted" and can only be taken off. Empty: "Drop files or folders on Dynaland to keep them here. Only where they are is remembered: nothing is copied." No drops in custom mode.
- `Shell/ShellHost.cs`, `Core/AppPaths.cs`: the store and its file.
- `SettingsUi/SettingsWindow.xaml.cs`: Settings > Privacy has a "File shelf" row saying what is kept and where.

Note: Windows does not let File Explorer drop files on an app that runs as administrator. If Visual Studio runs as administrator, Machitan started from it is too, and drops will not arrive; start Machitan normally to try the shelf.

To check:
1. Build: 0 errors, 0 warnings.
2. Drag a file from File Explorer over Dynaland: it opens on the shelf with "Drop to add to the shelf"; drag away without letting go: it closes. Let go over it: the file is on the shelf, with its icon, name and folder.
3. Quit and start Machitan again: the file is still on the shelf (Dynaland, camera and notifications page, the shelf button).
4. Click it: it opens, Dynaland closes. Drag it out to a folder on another drive (or with Ctrl): copied, still on the shelf. Drag it to a folder on the same drive: moved, and it leaves the shelf. Drag it into a chat app: the app gets the file.
5. The X: off the shelf, the file untouched. Rename or delete a parked file in File Explorer, open the shelf: "Not found: moved or deleted".
6. Empty shelf: the text about dropping files. Several files at once: all parked, the first on top. A folder: parked, opens in File Explorer.
7. Settings > Privacy: the "File shelf" row.

antislop Delivery Gate (during mode), UI: Design read: the shelf is a page like the general page (same title row, back arrow, row look of the notification tray, dim second lines), not a new surface; the drop hint is a quiet outlined box with one glyph and one line, shown only while files are over the pill. Real data only: names, folders and icons come from Windows for real files; gone files say so; nothing is invented or kept beyond the paths. Dials unchanged (the pill's existing open and close motion). Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS. R-03 N/A. R-17, R-18, R-28, R-36, R-38 PASS (nothing invented; the empty state says how to fill it). R-23, R-24, R-25 N/A. R-26 PASS (each control does what it says; taking off never touches the file). R-27 PASS (unreadable file, failed drag out, failed icon, failed drop: logged, never fatal; a full shelf and pathless items are said). R-32 PARTIAL (Dynaland never takes the keyboard, as before; the X buttons and the shelf button are buttons; rows are drag sources, opened by a click). R-33, R-34 PASS (names and folders trim with "..."). R-35 NOT RUN (Claude cannot run on this PC; bracket and text checks, name checks against the project, code inspection; Sam's build and run is the check). R-37 N/A. R-39 PASS (rows named by file and folder; buttons named). R-40 N/A.
- Block 2: nothing decorative added (PASS). Block 3: dials kept. Block 4: C-1 to C-5 PASS (C-4: existence checks run off the UI thread; icons are cached while parked).

#### 4. In-app update (2026-11-25)

Sam's go-ahead (11:30, with "Clear all").

**Status: delivered, waiting for build and run report.** No new packages; one new setting (`CheckForUpdates`, on by default as the handoff asks: it is only a question to GitHub, nothing is fetched or installed without a click); 40 new strings.

- `Shell/UpdateService.cs` (new), `ShellHost.Updates`:
  - The check: `GET api.github.com/repos/mujairkitten/machitan/releases/latest` (GitHub leaves out drafts and pre-releases), version from the tag (the first "1.2.3" in it), compared with the assembly's (the csproj's `<Version>`), both as four numbers. 30 s after start, then every 24 h, while the setting is on (read at each tick), and at About's button. Failures are named, never "newest": no connection or no answer in 30 s, GitHub turning it away (403/429: 60 checks an hour per network), no release, an answer that cannot be read.
  - The installer for this PC: `Machitan-Setup-<version>` + `-Full`, `-Trimmed`, `-ARM64-Full` or `-ARM64-Trimmed` + `.exe` (build-installer.cmd's names), as this copy is (ARM64 when the process is; Full when coreclr.dll is beside Machitan.exe). Only from `github.com/mujairkitten/machitan/releases/download/`. Its `digest` (the SHA-256 GitHub computes for every uploaded release file) is required: without one, or without an installer for this PC, About offers only the release page.
  - "Download and install" (the yes): into `%TEMP%\Machitan\Update` (emptied first, and at every start), off the UI thread, SHA-256 worked out as it comes, progress in About, Cancel; 60 s without data counts as no connection; a different SHA-256 deletes it. Checked once more just before Setup starts. Setup starts with its own pages (no silent install, no switches): Windows asks for administrator rights; Setup's Ready page says it closes Machitan, which it does cleanly (`--exit` signal), and its last page offers to start Machitan again. "Windows' question was answered no" and other failures are said, with "Try again"; "Start Setup again" while Setup was started.
  - Setup's folder is read from Windows' installed apps (Setup's AppId); About says so when this copy is not that one (a build from Visual Studio): the update goes to Setup's folder.
- `SettingsUi/SettingsWindow.xaml.cs`: About > Updates: the update row (state in words, This is Machitan x (x64, Full), when checked, a progress bar, the buttons for now), the setting's switch, and the note above; Privacy > Updates: what GitHub sees.
- `Start/StartMenuWindow.xaml.cs`: "Update: x.y.z" (accent, small) first in the top row's folder buttons while a newer Machitan is known; it opens Settings > About.
- `Config/ShellSettings.cs`, `Config/SettingsStore.cs`: `CheckForUpdates`, saved and read (missing in an older file: on).

For releases from now on: a GitHub release (not a pre-release, not a draft), tag like `v0.9.2`, with the four installers from build-installer.cmd under their own names. GitHub adds the SHA-256s itself.

Note: the releases page could not be read from here (GitHub rate-limited it), so the names of v0.9.1's files are not checked; v0.9.1 had x64 installers only.

To check:
1. Build: 0 errors, 0 warnings.
2. Settings > About > Updates: "Check for updates": "Machitan 0.9.2 is the newest version" (v0.9.1 is the newest release), "Checked at ...". With the network off: "The check failed: GitHub could not be reached...".
3. To see an offer before 0.9.2 is released: set `<Version>0.9.0</Version>` in Machitan.csproj for a moment and build: About offers 0.9.1 (or says its release has no installer for this PC / no SHA-256), and Start's top row shows "Update: 0.9.1". "What's new" opens the release. Only click "Download and install" if installing 0.9.1 is fine; then: progress, Setup opens, it closes Machitan, installs, and can start it again. Set the version back after.
4. Settings > Privacy: the Updates row. The switch off: no check at start (diagnostics.log has no "Update:" line after 30 s).

antislop Delivery Gate (during mode), UI: About's update row is one Settings row like the others (title, dim second line, buttons on the right; the accent button is the one to press next); Start's button is a small accent pill, shown only while there is something to install. Real data only: versions, names, sizes and SHA-256s come from GitHub and the app itself; a check that did not run never says "newest". Dials unchanged. Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS. R-17, R-18, R-28, R-36, R-38 PASS. R-26 PASS (nothing installs without "Download and install"; Setup's own pages and Windows' question follow). R-27 PASS (every failure named and logged; never fatal). R-32 PASS (Settings rows and buttons take Tab; Start's button is a button). R-33, R-34 PASS (texts wrap). R-35 NOT RUN (Claude cannot run on this PC; text and name checks, the name match and version compare checked with a copy in Python; Sam's build and a release are the check). R-37 N/A. R-39 PASS (the row is named by its state; buttons named). R-40 N/A.
- Block 2: PASS. Block 3: dials kept. Block 4: C-1 to C-5 PASS (the download and the SHA-256 run off the UI thread).

#### 4b. Pre-releases in the update (2026-11-25)

Sam (11:52): "wait, how about pre-release auto-update then?" Before this, pre-releases were never offered (GitHub's "latest" leaves them out). Asked; Sam: **an opt-in switch, "on for now until i say ready for 1.0"**; numbering: "current number progression adds +0.0.1, but that's now for beta test releases. i will decide later after 1.0."

Sam (12:09): "anything below 1.0 are naturally pre-release." So a pre-release is every version before 1.0, one with a label, and one GitHub marks as such (`UpdateService.IsPreRelease`); the check always reads the release list and leaves pre-releases out while the switch is off (before, a 0.9.x release not marked on GitHub would have counted as a normal one). The switch's note says so.

**Status: delivered, waiting for build and run report.** Two new settings; 5 new strings.

- `Config/ShellSettings.cs`, `Config/SettingsStore.cs`: `OfferPreReleases` and `OfferPreReleasesChosen` (set when the switch is used), and `OfferPreReleasesNow`: the choice, or `ShellSettings.PreReleasesByDefault` (true now) while there is none. **At 1.0: set `PreReleasesByDefault` to false**; everyone who never used the switch then gets only normal releases.
- `Shell/UpdateService.cs`: the check reads GitHub's 20 latest releases (`/releases?per_page=20`) and takes the highest version (drafts skipped; pre-releases only with the switch on); off with only pre-releases on GitHub (all of 0.x): "No release to offer" (the check worked; not called a failure). Versions: four numbers first (the +0.0.1 steps), then a label if a tag or the csproj's `<Version>` ever has one ("0.9.3-beta.1" comes before "0.9.3"; "beta.2" before "beta.10"); this copy's label comes from its informational version. A pre-release found says "(pre-release)" in About.
- `SettingsUi/SettingsWindow.xaml.cs`: About > Updates: "Also offer pre-releases" (Test versions, marked as pre-release on GitHub).
- `Shell/ShellHost.cs`: passes the setting to the check.

To check:
1. Build: 0 errors, 0 warnings.
2. About > Updates shows "Also offer pre-releases", on. "Check for updates": the result names a pre-release as "(pre-release)" if the newest release is one.
3. Turn it off and check again: "No release to offer" (every release so far is before 1.0). Turn it back on.

#### 3c. Shelf: Clear all (2026-11-25)

Sam (11:30): "add 'clear all' button while continue to the next step." **Status: delivered, waiting for build** (the go-ahead covers 3 and 3b; 4 started at once).

- `Widgets/DynalandWindow.cs`: "Clear all" in the shelf's title row, as the notifications' one (a dim text button, the same string), while the shelf has something; it takes everything off at once (`RemoveMany`), the files untouched; named "Take everything off the shelf (the files stay where they are)" for screen readers. 1 new string (`DynShelfClearAll`).

To check: build; park two files, "Clear all": the shelf is empty (its empty text), the files are where they were.

#### 3b. Shelf: several at once; admin shortcuts on by default (2026-11-25)

Sam (10:52), on Settings > Shortcuts > "Shortcuts in apps running as administrator": "is it possible to turn on this permanently, by default, without breaking shelf functionality? also, add ability to drop multiple files from the shelf." Answer: it already stays on once turned on (saved, and its task needs no new prompt); the shelf is not affected (only the helper runs as administrator; drops reach Machitan itself, which keeps normal rights). Asked how to make it the default; Sam chose **the installer sets it up**.

**Status: delivered, waiting for build and run report.** No new packages; one new setting (`AdminAppShortcutsChosen`, false in a new file); 5 new strings.

- `Widgets/DynalandWindow.cs`, `Shell/ShelfStore.cs`: picking several rows, as in File Explorer (the keys read with GetAsyncKeyState, since Dynaland never takes the keyboard): Ctrl + click picks or lets go, Shift + click picks from the last picked to this one (Ctrl + Shift adds), a plain click still opens. Picked rows are brighter, the title row says "3 selected" and shows a button that takes them off the shelf (`RemoveMany`, one save) and one that selects all or none (from two rows on). A drag of a picked row takes all the picked ones that are still there, in the shelf's order (one that cannot be had is left out and logged); a drag of another row takes only it. After the drop, each one that moved away leaves the shelf. Gone rows can be picked (to take them off) but are never dragged. The picks are forgotten when Dynaland closes.
- `installer/Machitan.iss`: a ticked task, "Machitan's shortcuts also in apps running as administrator", that runs `Machitan.exe --setup-adminkeys` as administrator (Setup is) before the last page; Machitan itself is still started with normal rights.
- `Program.cs`, `Shell/AdminKeys.cs` (`SetUpForInstaller`, `RegisterTaskAsync(askWindows)`): makes the task for the installed copy with no prompt and turns the option on in that user's settings.json; does nothing when the option was turned off in Settings (`AdminAppShortcutsChosen`, set by Settings and by the installer) or when settings.json exists but cannot be read (`SettingsStore.ReadFailed`: never written over).
- `Config/ShellSettings.cs`, `Config/SettingsStore.cs`, `Shell/ShellHost.cs`: the new setting, saved and read; Settings > Shortcuts sets it when the option is turned on or off.
- Known: the task names one Machitan.exe. Installing moves the option to the installed copy; a copy run from Visual Studio then says (in diagnostics.log) that it was turned on for another copy, and needs it turned on again in Settings (which moves it back). Over-the-shoulder installs (a standard user typing an administrator's password) set it up for the administrator's account, not the user's: Windows does not let a standard user's helper see an administrator app's keys anyway.

To check:
1. Build: 0 errors, 0 warnings.
2. Shelf: park three files. Ctrl + click two: they are brighter, "2 selected". Drag one of them to a folder on another drive: both copied. Shift + click from the first to the third: all three. The take-off button: all off, the files untouched. The select-all button: all, again: none.
3. Plain click still opens a file. Close and open Dynaland: nothing picked.
4. Installer (build-installer.cmd): the "Select Additional Tasks" page has the box ticked. After installing: Settings > Shortcuts shows the option on, Windows asked nothing more than Setup's own question, Task Scheduler has Machitan\AdminKeys, and the shortcuts work in an app running as administrator (regedit). Dropping files on the shelf still works.
5. Turn the option off in Settings (Windows asks to remove the task), install again: it stays off.

antislop Delivery Gate (during mode), UI: the picking follows File Explorer's keys; the two new buttons are the title row's small round buttons (the back arrow's look); the count is a dim line next to the title. Nothing decorative. Real data only. Desktop WinUI: web-only items N/A.
- Block 1: R-02 PASS. R-17, R-18, R-28, R-36, R-38 PASS. R-26 PASS (taking off never touches files; the select button's name says what it will do). R-27 PASS (an item that cannot be had is left out of a drag and logged; the installer step logs and changes nothing on any failure; an unreadable settings.json is never written over). R-32 PARTIAL (as 3: Dynaland never takes the keyboard; the buttons are buttons). R-33, R-34 PASS. R-35 NOT RUN (Claude cannot run on this PC; text and name checks, code inspection). R-39 PASS (picked rows carry "Selected"; buttons named). Others N/A.
- Block 2: PASS. Block 3: dials kept. Block 4: C-1 to C-5 PASS.

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
