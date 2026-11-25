# Changelog

## 0.9.2 (in progress)

### Added

- **Volume in Dynaland's player:** a mute button and a volume bar right below the music controls, as wide as they are. Click or drag the bar, or use the mouse wheel over it. It is Windows' own volume, the same as in Win+A.
- **Search actions:** Start's search and the bar's search box also list a calculator (type `2+2`: the answer shows on top; Enter or a click copies it) and actions: Machitan's settings, Windows Settings pages (Display, Sound, Bluetooth, Wi-Fi, Windows Update and more), Windows tools from the Win+X menu, Show desktop, Lock, the power menu's choices (with the same questions), Sign out (with a question) and Exit Machitan. `apps:` lists apps only.
- **Machitan's settings pages in search:** Start's search and the bar's search box list Machitan's Settings pages by any part of their names ("bar" finds Taskbar; Start, Corner widgets, Dynaland and the rest); choosing one opens Settings at that page. Settings itself has a search box above its pages that finds them the same way. When nothing fits, search says "Nothing matches." ("No app matches." with `apps:`).
- **Action center in Win+A:** switches for Wi-Fi, Bluetooth, airplane mode, mobile hotspot and live captions, as Windows has them on your PC, plus Project and Cast.
- **Split taskbar** (optional, off by default): pinned apps on top and open apps below, as two rows in one bar or as two stacked bars (Settings > Taskbar).
- **Windows on Arm:** Machitan builds for ARM64 too, with its own installers (Full and Trimmed).

### Changed

- New apps show in Start and search a few seconds after they are installed (and go when removed), without waiting for Start to open.

### Fixed

- The bar's search icon closes Start before Windows' search opens.

### Checked so far

- Everything above the search actions builds (2026-11-25).
- Volume in Dynaland's player: checked by Sam after its second look (2026-11-25).
- Search actions: checked by Sam (2026-11-25).
- The rest has not been run yet.

### Left out

- Win+A has no Night light, Energy saver or Do not disturb switch: Windows has no public way for other apps to switch them.

## 0.9.1 (2026-11-23)

### Added

- **Notifications in Dynaland.** Dynaland's general page lists Windows' own notifications, newest first, laid out like Windows' notification panel: the app's icon and name, the time, the title and the text, and the notification's pictures. Click one to open its app, dismiss one with its X, or clear them all (in Windows too). Machitan keeps no copy: a notification Windows no longer holds is gone from Dynaland too.
- **Lock** in Start's power menu.
- **Status ring options:** the battery level where the ring turns red, and its background: Auto (as before) or Manual, with a transparency slider.
- **Dynaland options:** a transparency slider (up to 50%, so the text stays easy to read) and a trapezoid notch shape.
- **Start: pinned categories.** Name them, add, move and remove them; move apps between them with Move to.
- **Start: Most used** (optional, off by default): the apps you open most from Start, at the top of All apps; it folds away. Counted by Machitan on your PC; turning it off deletes the counts.
- **Start: letters in All apps.** Click a letter to jump to another, as in Windows' Start.
- **Start: drag and drop.** Drag pinned apps to any place in any category; drag an app from All apps into Pinned to pin it there.
- Icons in Start's power menu.

### Changed

- **Start's new layout:** your picture and a greeting at the top with the folders and power, search over the pinned apps on the left, All apps on the right.
- Win+N's calendar opens below the info line, and clicking the info line opens it (instead of Windows' notification centre). Win+A's volume and brightness open below the status ring. Without those widgets, both open by the bar as before.

### Fixed

- Show desktop's thin line shows only when the pointer is close to it, not anywhere on the bar.
- Clicking an app on the bar closes Start.
- The tray's ^ button closes the box when it is open, and turns over while the box is open.
- The hover background of the tray's ^ button and tray icons is rounded like the other bar buttons, not an oval.

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
