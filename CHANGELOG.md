# Changelog

What changed in each Machitan release, in short. The full working notes are in `CHANGELOG-i1.md`.

## 0.9.1 (not released yet)

### Added

- **Notifications in Dynaland.** Dynaland's general page lists Windows' own notifications, newest first, laid out like Windows' notification panel: the app's icon and name, the time, the title and the text, and the notification's pictures. Click one to open its app, dismiss one with its X, or clear them all (in Windows too). Machitan keeps no copy: a notification Windows no longer holds is gone from Dynaland too.

### Known limits

- Windows sends Machitan no live updates (Machitan has no app identity), so the list is read again every second while Dynaland is open, and every few seconds otherwise.
- A click opens the notification's app, not the chat or mail it is about: Windows gives that only to the app itself.
- The pictures come from Windows' own notification database, which Microsoft does not document. A Windows update could stop them; the text list keeps working.

## 0.9.0 (2026-11-21)

First public beta build.

### Taskbar

- A see-through (task)bar of separate bubbles (Start, the app dock, the tray), joined, or full width, at the bottom or the top of the screen.
- Pinned apps, Show desktop with a peek at the desktop, auto-hide, and live thumbnail (or list) of an app's windows when you hover at it.
- Drag anything on the bar to somewhere; group apps into a bubble of their own (control + click the app icons > right-click > Group apps) ; right-click > Customize to choose the bubbles, Dynaland and the corner widgets.
- The tray on the bar, with hidden icons in a box behind a ^ button, as on Windows' taskbar.
- Your own Start button pictures (normal, hovered, pressed).
- Accent colour generated from your wallpaper.
- Animations for Start, the tray box and the bar, with a switch to turn them off.

### Start

- Machitan's own Start menu: search for apps and files, Pinned section, All apps with a menu per app (pin, run as administrator, app settings, uninstall, the app's jump list tasks), file previews, folder buttons, and a power menu you can customize.

### Keyboard Shortcuts

- Windows key opens Machitan's Start. Win+1..9 and Win+0 open the bar's apps, Win+N the calendar, Win+A volume and brightness, Win+` the window switcher; optionally also while an app runs as administrator.

### Corner widgets and Dynaland

- An info line (date, time, weather) and a status ring (battery, Wi-Fi, volume), in any corner.
- Dynaland, a pill at the top or bottom centre: music in its cover's colour, reacts when volume changes and camera and microphone in use, and has the apps' tray icons (disabling this will enable bar's tray, vice versa).

### Misc:

- Settings for more control and customization.
- Available in English and Bahasa Indonesia.
- Run at startup (off by default). Explorer's taskbar always comes back when Machitan exits, even after a crash.
- Two installers: Full (with .NET 10) and Trimmed (needs the .NET 10 Runtime, x64).
