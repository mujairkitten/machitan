# Handoff: Machitan line i1, after Batch 7 Part 28b (2026-11-23)

You are taking over Machitan from another Claude session. Read this whole note first, then `CHANGELOG-i1.md` (Parts 10 to 26 of Batch 7 are the recent history) and `README.md`. Sam (the owner) builds and runs; you write code.

## The project

Machitan is an alternative shell for Windows 11: its own taskbar ("the bar", an AppBar at the bottom by default), Start menu, flyouts (Win+N calendar, Win+A quick settings, Win+Tab switcher, Win+X menu), corner widgets (info line, status ring), Dynaland (a notch-style media pill at the top), tray icons received straight from apps (Machitan has its own `Shell_TrayWnd` window), custom mode, Settings window, English and Bahasa Indonesia.

- Folder: `C:\mujairkitten\projects\machitan` (Sam's PC). Sam opens `Machitan.csproj` in Visual Studio 2026 and uses GitHub Desktop.
- Stack: .NET 10 (`net10.0-windows10.0.19041.0`), WinUI 3, Windows App SDK 2.7.0 (only `Microsoft.WindowsAppSDK.WinUI` and `.Runtime`, not the all-in-one package), System.Management 10.0.14, x64, unpackaged, self-contained (the Trimmed installer is the same without .NET).
- PC: Windows 11 26H2 build 26300.10066, 1920 x 1200 at 150%, laptop (battery).
- Machitan's data and log: `C:\Users\Sammy\AppData\Roaming\Machitan` (`settings.json`, `bar-layout.json`, `diagnostics.log`). The log is the main evidence: ask Sam for it, or read it yourself if you can reach his files.

## Hard limits (Sam's, not negotiable)

- WinUI 3 + native Win32 interop only. No WebView/WebView2, no Rust, no web tech.
- No fake content anywhere (no invented data, placeholders, fake icons or entries).
- No notification history.
- No git commands: Sam uses GitHub Desktop.
- No new NuGet packages unless a batch truly needs one; say so first.
- Never call `SHGetFileInfo`. `IShellLinkW.GetPath` gets `IntPtr.Zero` for its find-data argument.
- No `DesktopAcrylicBackdrop` for the status ring or Dynaland.
- New optional options are off by default.
- 0 compiler warnings.
- Talk to Sam in easy, short words; technical terms only when needed.
- Complete files, no placeholders; fix what Sam reports before moving on.

## Code rules that keep the build clean

- All text people see is in `Translations/Strings.resx` (English) and `Translations/Strings.id.resx` (Indonesian), used as `Strings.Name`; values through `Fmt.F(Strings.Name, value)`. `Strings.X` are properties, not constants (no `const`, `case`, default parameters). Log messages stay English. VS regenerates `Strings.Designer.cs`; if you add strings by hand, add the Designer properties too (sorted case-insensitively; resx entries sorted ordinally). See `TRANSLATING.md`. Win+X labels mark their key with `&`.
- Files: ASCII, CRLF. `.resx` are UTF-8 with BOM. XAML keeps its BOM state. Sam's `MachitanLogo.xaml(.cs)` are his (UTF-8, LF): touch minimally.
- Use `LibraryImport`, not `DllImport`. No `await` inside `unsafe` classes or methods (CS4004). Don't name a type `Com` or a member `Windows`. Raw COM goes through `ComCall` vtable slots.
- antislop ("during" mode): each UI part in `CHANGELOG-i1.md` ends with an antislop Delivery Gate block; R-35 is always "NOT RUN" (the AI cannot run the app).

## State

- Batches 0 to 6: done (green). Batch 7 (custom mode, dragging, radii, accent, then Sam's follow-up lists in Parts 8 to 15) is the last batch of the original plan. Sam (2026-11-20): everything works except open problem 3 below (problems 1 and 2 were solved in Part 15). Win+X is fixed (Part 13: Machitan's own menu, `Flyouts/WinXMenuWindow.cs`).
- Part 14 (r46/r47): a direct sender for Machitan's own AppBar messages; its check found the message is 64 bytes on build 26300, so it never sent. Replaced by Part 15.
- Part 15 (r48, 2026-11-20): `Shell/AppBarRelay.cs` passes AppBar messages on with an answer block Explorer can open (the real cause of problems 1 and 2, read from shell32.dll); tray catch-up for icons sent while another `Shell_TrayWnd` window was in front (problem 3). Sam: "strip and bar's area fixed. traydio still doesn't fixed." `Shell/AppBarSender.cs` was emptied and Sam deleted it.
- Part 16 (r49, 2026-11-20): the tray box stays open under a right-clicked icon's menu; Peek at the desktop (Settings > Taskbar, off by default); the previews use Windows' own peek (`Shell/LivePeek.cs`, dwmapi ordinal 113, the old way as fallback); the Start button's own pictures for normal, hovered and pressed (Settings > Start > Start button, off by default, `Bar/StartButtonPictures.cs`); a log line for the first message from each new program (problem 3). Built and run: Windows' own peek works on Sam's PC (kind 3 for a window, kind 1 for the desktop; the values third-party taskbars use, not documented).
- Part 16b (r50, 2026-11-20): the Start button's own pressed picture stays while Start is open (`StartMenuWindow.OpenChanged`). Delivered, waiting for Sam's build and run report.
- Part 17 (r51, 2026-11-20): cause of problem 3 found and fixed: Machitan's tray window now carries Explorer's "TaskbandHWND" property (see problem 3). Delivered, waiting for Sam's build and run report.
- Part 18 (r52, 2026-11-20): animations. `Visuals/Motion.cs` (new) runs every animation on the element's own composition visual: Start and the tray box rise and fade in from the bar's side and fade back out (a flyout opts in with `MotionTarget`; `WindowCutter.Suspend`/`Resume` lift the window's cut while things move), bar buttons squish when pressed, new apps grow in, the line under an app stretches, and auto-hide's slide. Settings > General > Turn off animations ("TurnOffAnimations", off by default) and Windows' Animation effects off both stop all of it (`Motion.IsOn`). Delivered, waiting for Sam's build and run report.
- Part 19 (r53, 2026-11-20): Start's power menu always ends with Exit Machitan (cannot be turned off; Settings shows it as a greyed, on toggle with `AlwaysOnRow`). Same exit path as the bar menu (`ShellHost.Shutdown`).
- Sam (2026-11-20 22:54): "the test is clean" (r49 to r53 built and tested).
- Part 20 (r54, 2026-11-20): an installer. `build-installer.cmd` publishes Release x64 self-contained to `publish\Machitan` and compiles `installer\Machitan.iss` (Inno Setup 7 or 6.3+, a tool on Sam's PC, not a NuGet package) into `publish\Machitan-Setup-<version>.exe`. All-users install in Program Files (the AdminKeys task runs the exe as administrator, so its folder must be admin-only). Install and uninstall close a running Machitan through its exit signal (`Local\Machitan_ExitRequest`) and wait on its mutex. Uninstall removes the Run entry (if it names that copy) and the `Machitan\AdminKeys` task; `%AppData%\Machitan` stays. Unsigned. Delivered, waiting for Sam to make and try it. When the version should change, bump `<Version>` in `Machitan.csproj` (Sam set 0.9.0 on 2026-11-20). Part 20b (r55): the installer's name and the Apps list drop a last ".0" (0.9.0, not 0.9.0.0).
- Part 21 (r56, 2026-11-20): the `--proof` and `--seethrough` test windows are out of the code (`MainWindow`, `SeeThroughTestWindow`, `Core/StartupMode.cs` for Sam to delete, with `Properties/launchSettings.json`); `.gitignore` and `.gitattributes` updated. Claude cannot delete files on Sam's PC (no delete tool): list them for him. Sam deleted them (the `Properties` folder is gone).
- Part 22 (r57, 2026-11-20): `Microsoft.WindowsAppSDK` (all-in-one, with ~62 MB of AI, ML, Search, Widgets) replaced by `Microsoft.WindowsAppSDK.WinUI` + `Microsoft.WindowsAppSDK.Runtime` 2.7.0 (agreed by Sam). Two installers: Full (self-contained) and Trimmed (`--self-contained false`, needs the .NET 10 Runtime x64; Setup checks and points to the download page). One AppId for both; Setup removes the old install's files first (else a Full install's hostfxr.dll would make a Trimmed Machitan.exe look for .NET in its own folder). Output folders: `publish\Machitan-Full`, `publish\Machitan-Trimmed`. Built (Sam, 2026-11-21): Full 45.1 MB (was 63.2 MB), Trimmed 20.8 MB; Sam runs the installed Trimmed edition.
- Part 23 (r60, 2026-11-21): the notification tray, step 1. Sam chose: probe before any packaging; the tray shows Windows' own current list (Machitan stores nothing; dismissing in Dynaland dismisses in Windows). `Shell/NotificationProbe.cs` (log only, no notification content in the log) checks identity, access, one read (with which parts are readable), the live-update event, and 2 minutes of counting. Result (01:52): no identity, access Allowed, the list and every part of it readable, live event not allowed (0x80070490), polling works. No identity package needed.
- Part 24 (r61, 2026-11-21): the notification tray. `Shell/NotificationService.cs` reads Windows' list (1 s while Dynaland's general page is open, 4 s otherwise, none while Dynaland is off screen), dismiss/clear in Windows, app pictures cached per app in memory. `Widgets/DynalandWindow.cs`: "Notifications" section with "Clear all", rows (picture, app, time, title, text, X), three visible then scroll, click opens the app via Start's app list (else Windows' centre). No history, no content in the log. Sam deletes `Shell/NotificationProbe.cs`. Built and run 02:07: works; "Clear all" failed.
- Part 24b (r62): "Clear all" dismisses one by one (Windows' `ClearNotifications` needs identity, 0x80070490). Images: Windows' reader gives text only; the pictures are only in Windows' notification database (`wpndatabase.db`, SQLite, `Notification.Payload` XML). Asked Sam whether to read it (undocumented; `winsqlite3.dll`, no new package). Sam: go ahead.
- Released: tag `v0.9.0` on GitHub (github.com/mujairkitten/machitan, public), 2026-11-21 00:09, up to Part 22 (pre-notification). `Machitan.csproj` now 0.9.1 (Sam).
- Part 25 (r63): `CHANGELOG.md` (short, per release; keep it up to date with each user-visible change, under 0.9.1 until Sam releases it). Notification pictures from `wpndatabase.db` (`Shell/NotificationPictures.cs`, `Interop/WinSqlite.cs`): matched by id (checked by title) else by title within 10 minutes; hero, appLogoOverride (circle), inline, attribution; file, ms-appx, ms-appdata, http(s). Rows laid out like Windows' panel; the app's icon from Start's catalog (the "dots" were the plated logo). Sam (2026-11-23): everything works.
- Part 26 (r64, 2026-11-23): Show Desktop's line only within 96 DIP of it; Start closes after an app on the bar is switched to; flyouts keep a "closed by this same click" guard (`FlyoutWindow.TakeClosingClick`, time and place) so the tray ^ closes an open box; the ^ turns 180 degrees while the box is open (`TrayBoxWindow.OpenChanged`, `Motion.Turn`); Lock always first in Start's power menu; status ring: low-battery level (5 to 50, 20) and background Auto/Manual with transparency; Dynaland: transparency 0 to 50 and notch shape Default/Trapezoid; calendar and quick settings open by the info line and the ring (`FlyoutWindow.WidgetAnchor`, `PopupPlacement.NextToWidget`); the info line's click opens Machitan's calendar, the ring's click is unchanged. Built 18:59: trapezoid looked wrong and see-through showed darker overlaps (`LayerVisual` did not flatten).
- Part 26b (r65): for the trapezoid and any transparency, the notch is one XAML path (`DrawSilhouette`, `UpdateSilhouetteMode`), redrawn per frame with Dynaland's curve while the pill moves; trapezoid only for the closed pill (slant 22, curves 8, corners 8 DIP), open views rectangular; opaque default notch and the island stay the composition pill. No report yet.
- Part 27 (r66): Start's new layout (top user box with greeting, search over pinned categories, All apps on the right; 660 x 480 DIP); pinned categories in start-pins.json (`PinGroup`, `PinStore` grouped: rename, add, move, remove; pins' Move to); Most used (`Shell/AppUsage.cs`, app-usage.json, Settings `StartMostUsed` off by default, `StartMostUsedCollapsed`; counts apps opened from Start only; off deletes the file); All apps letters with the letters' grid; power menu icons (`PowerChoice.Glyph`). Built 21:21: pinned names cut off.
- Part 28 (r67): pinned tiles 88 DIP tall (two-line names fit); drag and drop in Start (pinned apps to any place in any category; All apps rows pinned where dropped and kept in All apps; `PinStore.PlaceInGroup`; mouse and pen, Escape cancels, accent line marks the place); bar ^ and tray icons get a corner radius for their own width (the hover was an oval). Built 21:33: all fine except both drags (Windows refused `Root.CapturePointer`).
- Part 28b (r68): Start's drags follow the mouse on a 16 ms timer like the bar's (cursor, button state, Escape), after the button releases the pointer; mouse only. Waiting for Sam's build.
- Skills: Sam saved three skills from Microsoft's WinUI agent plugin (winui-design, winui-code-review, winui-dev-workflow). Their `winapp` commands need WinApp CLI on a Windows PC; they cannot run in Claude's workspace.
- Known small gap: a new window opened from the button of Firefox's private windows opens a normal window.
- Notification tray (Parts 23 to 25): done. Sam's answers (2026-11-21): probe first (done: reading works without identity); show Windows' own current list, store nothing ("No notification history" stands: Machitan keeps no list, log or copy of notifications beyond Windows' list as last read, in memory).
- Now: Part 28b, waiting for the build (and a word on Part 26b).

## Solved problem 1: the top strip (Settings > Edge strip > "Keep the free edge clear")

**Solved by Part 15 (Sam, 2026-11-20, r48).** Kept for the history: if it comes back, start from "Cause found" below.

Goal: maximised windows must stop below the corner widgets and Dynaland at the top. Machitan registers a second AppBar for that, 48 px tall (`Widgets/TopStrip.cs`), separate from the bar's AppBar (`Shell/AppBarHost.cs`).

Evidence (`diagnostics.log`, run 2026-11-20 15:27, Part 13 code):

```
15:27:55.687 AppBar registered.                       <- the bar's AppBar (bottom)
15:27:56.405 Explorer's taskbar set to auto-hide and hidden
15:27:56.586 Strip: 48 px kept free at the top ...
15:27:57.389 Strip check (0.8 s after the strip was taken): ... free area 0,48 to 1920,1104
             (work area 0,0 to 1920,1104, strip 0,0 to 1920,48; Windows did NOT take the strip off the work area).
```

So Windows accepts the strip (ABM_NEW works, ABM_SETPOS returns 0,0 to 1920,48) but the work area still starts at y = 0. The bar's AppBar does work: the work area ends at 1104 (96 px for the bar).

Already tried, did not help:
- Part 10 and 11: moving maximised windows into the free area by hand after the strip was taken.
- Part 12: also screen-filling windows that are not maximised, checked again whenever any window moves (`Shell/WindowChangeWatch.cs`, `Shell/MaximizedFit.cs`). Result: Claude's window moved straight back each time; other apps showed a white or black band at their top and a gap above the bar, because the app and Windows disagree on where "maximised" is. So hand-moving maximised windows is a dead end while the work area is wrong. Part 13 stopped it: `MaximizedFit` now leaves maximised windows alone unless Windows took the strip.
- Part 13: the strip's AppBar window was a hidden 0x0 window; now it is a real window on the strip (layered alpha 0, click-through, HWND_BOTTOM, tool window) plus ABM_WINDOWPOSCHANGED. Log above: no change.

**Cause confirmed (Sam, 2026-11-20 15:35): Machitan's own tray receiver.** With Dynaland off and "Tray on the bar" off (so the tray receiver never starts), maximised windows stop below the strip as they should.

Why: `SHAppBarMessage` finds the taskbar window by its class name `Shell_TrayWnd`. Machitan has its own `Shell_TrayWnd` window (it receives tray icons straight from apps, `Shell/TrayIconService.cs`, on its own thread, kept in front of Explorer's so apps find it first). It passes every other WM_COPYDATA, AppBar messages included, on to Explorer's window with `SendMessageTimeout` (`OnCopyData` -> `ForwardCopyData`). The bar registers its AppBar BEFORE that window starts, so its messages reach Explorer directly and work. The strip registers AFTER, its messages go through the forward, and Explorer accepts them (ABM_NEW and ABM_SETPOS answer normally) but never takes the strip off the work area. Something in the forward breaks it: possibly Explorer checks the sender of WM_COPYDATA, or replies or notices go to the wrong place.

What to do (pick one, test with the "Strip check" log line, which says whether Windows took the strip off the work area):
1. Send the strip's AppBar messages to Explorer's own `Shell_TrayWnd` directly, not through `SHAppBarMessage` (which finds Machitan's window first). Find Explorer's window by process as `TrayIconService.ExplorerTray()` does, and build the AppBar WM_COPYDATA yourself (the 64-bit layout: APPBARDATA with 64-bit fields, the message, a shared-memory handle for the rect of ABM_QUERYPOS / ABM_SETPOS, and the source process id). Check the layout against the Windows SDK and a working reference before relying on it.
2. Or keep `SHAppBarMessage` and make it reach Explorer: register the strip (and redo ABM_SETPOS later) only while Machitan's tray window is not in front, or stop and restart the tray window around it. Simpler but fragile: every later ABM_SETPOS (the strip moving, ABN_POSCHANGED) has the same problem.
3. Or find what the forward loses and fix `ForwardCopyData` for AppBar messages (dwData 0). Compare what Explorer gets directly and through the forward.

**Cause found (Part 15, from shell32.dll on build 26300):** `SHAppBarMessage` makes the answer block (`SHAllocShared`) for the process owning the first `Shell_TrayWnd` window and writes that process's ID at 0x38 of the 0x40-byte message (block at 0x30). With Machitan's window first, Explorer cannot open the block and answers without acting. Part 15's `AppBarRelay` (tray thread) copies the block into one made for Explorer's process, passes the message on, and copies the answer back. Check the next log for "AppBar: passed on ABM_SETPOS ... (Machitan)" and "Strip check ... Windows took the strip off the work area".

**Earlier, Part 14 (option 1, superseded):** `Shell/AppBarSender.cs` builds the 0x38-byte message (layout from Geoff Chappell's notes on shell32 and ReactOS) and sends it to Explorer's window from the caller's thread, for the strip (`TopStrip`), the bar (`AppBarHost`) and Explorer's taskbar state (`ExplorerTaskbar`), only while Machitan's tray window is the first `Shell_TrayWnd`. It first checks the layout against what shell32 really sends (seen by Machitan's tray window, "AppBar check 1 of 2") and that Explorer answers a message built by Machitan ("AppBar check 2 of 2"); on failure it keeps the usual way and logs the bytes. Read the next log for: the two check lines, "AppBar: straight to Explorer" lines, and "Strip check". If the checks pass and the strip is still not taken off the work area, the detour was not the cause but the tray window's presence is: go to option 2 (take the strip and redo the bar's reservation while Machitan's tray window steps behind Explorer's).

Keep the bar's own AppBar working: it registers before the tray window today, but any later ABM_SETPOS of the bar (moving to the top, size changes) also goes through the forward, so test that too.

If none of that works: Explorer's own behaviour with its taskbar on auto-hide; the work area can be set with `SystemParametersInfo(SPI_SETWORKAREA)`, but Explorer on Windows 10 and later resets it when it gets WM_SETTINGCHANGE, so that is a last resort.

## Solved problem 2: the taskbar area safeguard

**Solved by Part 15 (Sam, 2026-11-20, r48).**

Sam (2026-11-20 15:30) says the bar's area safeguard still has problems too, without details yet; at 15:46 he said it is related to problem 1. The bar's later reservations went through Machitan's tray window too and hit the same cause; Part 15's relay covers them. Earlier (15:06) he saw "a big gap between the taskbar and the maximised window" while Machitan was moving maximised windows by hand (Part 12); Part 13 stopped that. Ask Sam what he sees now (a screenshot helps) before changing anything. Facts: BarSize 64 DIP = 96 px reserved at the bottom; the visible pill is BarSize - 14 DIP, centred in that space (handoff spec, Batch 1).

## Open problem 3: tray icons of Traydio and the Traytober apps never reach Machitan

**Cause found and fixed in Part 17 (r51), waiting for Sam's test.** Windows' taskbar list (ITaskbarList, explorerframe.dll) finds the taskbar's button list with `GetPropW(FindWindowW("Shell_TrayWnd"), "TaskbandHWND")`. Machitan's window had no such property, so the Windows App SDK's `AppWindow.IsShownInSwitchers` threw "Not implemented. SetShownInSwitchers failed." in WinUIEx's `TrayIcon` constructor (Traydio's own log), before the icon was added. Machitan now copies Explorer's value onto its window (`TrayIconService.MirrorTaskband`). If another app breaks while Machitan's window is first, look at what it reads from the first `Shell_TrayWnd` window: Explorer's carries more properties (TaskbarMonitor, TaskbarDPI_*, IsAutoHideEnabled, TaskbarVisible, OleDropTargetInterface...), listed in Part 17's notes.

Sam (2026-11-20 18:29 and 19:59). Traydio (Trdo, full-trust packaged WinUI 3, WinUIEx TrayIcon) started twice while the tray receiver ran; nothing from it reached Machitan. `Shell_NotifyIconW` on build 26300 is plain: `FindWindowW("Shell_TrayWnd")`, then `SendMessageTimeoutW`. Best guess: Explorer's window was in front for a moment as the app started. Part 15 logs each such moment ("another "Shell_TrayWnd" window (...) was in front") and asks apps started in the last minute again 1.5 s later. If the next log shows no such line near Traydio's start, the guess is wrong: look for another route (the app's own process, a second Shell_TrayWnd, etc.).

**r48 result: the guess is wrong.** Traydio started at about 20:18:31 while Machitan's window was first (the only "another window in front" lines were at 20:17:55, Machitan's own start) and nothing from Trdo.exe reached Machitan. Part 16 adds "Tray: first message from <program> after the first look: ..." for every program that had sent nothing before (also messages Machitan cannot read). Asked Sam: end Trdo.exe in Task Manager, start it again, wait 10 s, send the log, and say how Traydio was closed before. Then:
- A line from Trdo.exe that is not "tray add": read what it sends (for example a change or version for an icon Machitan never saw added, or a message without shell32's mark).
- No line from Trdo.exe at all: it sends nothing to Machitan's window. **This is what the r49 log shows (Part 16b):** started fresh at about 21:00:20, then asked directly at 21:00:52; nothing from Trdo.exe either time. Traydio is full trust (runFullTrust), so rights are not the cause. Check whether the app adds its icon only at some moment (WinUIEx's TrayIcon re-adds on "TaskbarCreated" only while it is visible), whether its icon window gets Machitan's broadcast (packaged apps, hidden WinUI windows), and whether Explorer's own tray shows it (Explorer's window gets nothing from it either if not). Next: with Sam's go-ahead, attach a debugging tool (Frida is installed on Sam's PC and reachable through the rea tools) to Trdo.exe and log `Shell_NotifyIconW`, `FindWindowW("Shell_TrayWnd")` (which window it returns) and `SendMessageTimeoutW` (WM_COPYDATA, its result and last error), then send "TaskbarCreated" to Trdo.exe's own windows from inside it so it adds its icon again. Without the go-ahead: start Traydio while Machitan runs, exit Machitan, and see whether Windows' own tray shows Traydio (then Explorer got it straight).

## How the previous session worked

- One delivery = one numbered folder of complete files (r68 was the last), written into Sam's folder, then read back and compared byte for byte. Sam builds, runs, reports with screenshots and the log.
- Every part has a CHANGELOG entry: Sam's words, a status line, what changed per item with file names, "To check" steps, and the Delivery Gate for UI work.
- Key files for this problem: `Widgets/TopStrip.cs`, `Shell/AppBarHost.cs`, `Shell/MaximizedFit.cs`, `Shell/WindowChangeWatch.cs`, `Shell/ShellHost.cs` (`KeepTopStrip`, `UpdateTopStrip`, `FitMaximizedSoon`, `StartTrayWhenWanted`), `Shell/TrayIconService.cs` (`OnCopyData`, `ForwardCopyData`, `ExplorerTray`), `Shell/ExplorerTaskbar.cs`.
