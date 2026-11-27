# Machitan - handoff note for the next AI (2026-11-26, after v093-28)

## The project
- Machitan: Sam's alternative Windows 11 shell (bar, Start, flyouts, Dynaland, indicators, widget sidebar, desktop widgets).
- WinUI 3, C#, .NET 10 (net10.0-windows10.0.19041.0), Windows App SDK 2.7.0, unpackaged, self-contained.
- Folder: C:\mujairkitten\projects\machitan. Version 0.9.3 in progress (CHANGELOG has a "0.9.3 (in progress)" section).
- Read STATUS.md first: one entry per delivery (v093-1 .. v093-28), newest just above "## Before v0.9.2 (history, short)".
- v093-28 (widget dragging, scrolling Now playing title) is CONFIRMED WORKING by Sam (STATUS still says "waiting"; mark it done).

## How Sam works
- You write complete files (no placeholders, no diffs). Sam builds in VS 2026, tests, reports, and commits in GitHub Desktop.
- Remind him to commit after each delivery. NO git commands.
- Use short, easy English with Sam. Report after each part; fix what he reports first.
- Log: %AppData%\Machitan\diagnostics.log (settings.json is next to it).

## Hard rules (keep all of them)
- WinUI 3 + native Win32 interop only. No WebView, no Rust, no web tech.
- No fake content: every number, icon and list item comes from real data or is honestly empty.
- No notification history.
- No new NuGet packages unless truly needed, and ask Sam first.
- Never call SHGetFileInfo. IShellLinkW.GetPath gets IntPtr.Zero.
- MoveWindow never restores a minimized window (SW_RESTORE first).
- RegisterHotKey error 1409: log and skip (Win+D is caught in the keyboard hook instead).
- No DesktopAcrylicBackdrop for the status ring or Dynaland.
- New optional features are OFF by default. Never wipe settings.json.
- Failures go to diagnostics.log and are never fatal. Keep 0 compiler warnings.
- The log never gets notification app/title/text, song titles, artists, files, lyrics, links or shelf text.
- Files are ASCII only. .cs: CRLF, no BOM. Write a degree sign as "\u00B0" in code, never in resx.
- Strings: Translations/Strings.resx, Strings.id.resx (Indonesian) and Strings.Designer.cs (keep each file's UTF-8 BOM).
- If something fails, leave it out and say so plainly. Spike uncertain things first.
- Stale bin/obj errors (CS1061 on deleted code): Sam deletes bin/obj; never re-add deleted code.
- You can't delete files on Sam's PC: list them for him.
- Don't work around denied permissions.

## WinUI lessons learned here
- Windowed popups are clamped to the screen's free area, and a docked sidebar is outside it. Menus inside the sidebar are constrained (ShouldConstrainToRootBounds = true) and need a solid presenter style (SidebarWindow.SolidStyle), or they are see-through.
- Flyout defaults to constrained; MenuFlyout does not.
- ToolTips inside the docked sidebar appear outside it, so don't use them there.
- ComboBox drop-downs are windowed popups, so use RadioButtons and ToggleSwitch in widget options.
- Customize panels open unconstrained beside the card (sidebar: LeftEdgeAlignedTop for a right sidebar; desktop: right or left by free room).

## Widgets (where things are)
- Shell/WidgetStore.cs: widgets.json. Items have Kind, Place (sidebar or desktop), X/Y, Width/Height, and Data (options).
  - Add, Remove, Move, Swap, SetPlace(id, desktop, x, y, before, atEnd), SetSize, SetPosition, SetData.
  - Saving is skipped while the file is read-only (IsReadOnlyBecauseOfBadFile).
- Widgets/WidgetViews.cs: WidgetView base class + NoteWidget.
  - Members: Build, SetShown, Flush, FocusFirst, SetFixedHeight, HasOptions, BuildOptions, ApplyOptions.
  - Option helpers: Option, OptionOn, Choice, Switch, AlignChoice, SizeChoice.
  - Create/NameOf/GlyphOf map kinds to views. WidgetKinds.Known lists the kinds for the Add menu.
- The kinds: ClockCalendar, Clock (ClockWidgets.cs), Weather (WeatherWidget.cs), NowPlaying (NowPlayingWidget.cs; title is a Visuals/Marquee), Note.
- Widgets/SidebarWindow.cs:
  - Cards are laid out in a MasonryPanel (2 columns from 560 DIP).
  - Card menu: Customize, Up, Down, To desktop, Remove.
  - A card's bottom edge resizes it.
  - Cards are dragged by their name row: onto another card to swap, past the inner edge to go to the desktop.
- Widgets/DesktopWidgetWindow.cs + DesktopWidgets.cs:
  - One window per desktop widget, kept behind other windows (HWND_BOTTOM).
  - Drag by the name row; resize by any edge or corner.
  - Dropping onto the sidebar works through the ShellHost forwarders SidebarDragOver, SidebarDropAt and SidebarDragEnded.
- Shared services on ShellHost: Weather (WeatherService), Lyrics (LyricsService, LRCLIB), WidgetItems (WidgetStore), Shelf.

## Next tasks (Sam asked for these; all approved)
1. Widgets batch 2, each optional and only there when the user adds it, with Customize options where sensible:
   - Focus timer / pomodoro.
   - System usage: CPU, memory and disk from real counters.
   - Photo gallery: from a folder Sam picks, Pictures by default.
   - RSS news: feeds the user adds, none built in.
   For each: add the kind to WidgetKinds.Known, WidgetView.Create/NameOf/GlyphOf, and strings (en + id).
   Update Settings > Privacy text for RSS (feeds are fetched from the feed URLs the user adds).
   Update README, CHANGELOG and STATUS.
2. Cleanup later:
   - Share the CalendarDayNames logic with CalendarFlyout.
   - Remove the "(test)" log lines (desktop widget test, WindowsFlyouts TestLine, Dynaland NoteUnpaired).
3. Spikes later: Energy saver, Night light, Nearby sharing; multi-monitor.
4. Open question for Sam: should Win+W open the sidebar?
5. Release chores for 0.9.3.
