# Changelog

## 0.9.3 (in progress)

### Added

- **Dynaland instead of Windows' flyouts.** Each has a switch in Settings > Dynaland, all on by default. While Dynaland is hidden for a full-screen app, Windows' own flyouts show as before.
  - **Volume and brightness.** Volume up, down and mute now change the volume through Machitan, and Windows' volume and brightness flyout is hidden, so only Dynaland shows the level. The volume keys also work this way in front of apps running as administrator when "Shortcuts in apps running as administrator" is on.
  - **New notifications.** Windows' popup is hidden and Dynaland opens a little for 5 seconds with the app, the title, the text and the notification's pictures (a person's photo beside the words, a big picture below them); point at it for the full list. The notification stays in Windows' list.
  - **Charger.** Plugging in or unplugging shows "Charging", "Plugged in" or "On battery" with the battery level.
  - **Copy and cut.** "Copied", "Files copied", "Files cut" or "Picture copied". Machitan never reads what is on the clipboard.

### Changed

- **The status ring on a desktop PC and on a network cable.** With no battery, the rim is drawn whole instead of showing only its faint track. On a network cable the ring shows a plug and the network's name, as Windows' Settings shows it ("Ethernet" only when Windows gives none). Wi-Fi and the volume dots are as before.
- **x64 installers are marked x64.** They are now `Machitan-Setup-<version>-x64-Full.exe` and `Machitan-Setup-<version>-x64-Trimmed.exe`, like the ARM64 ones. The in-app update finds the x64 installer under either name.

### Known limits

- Windows' notification popup is recognised by its title, "New notification", so far only on English Windows. In another language it still shows, beside Dynaland's (diagnostics.log names the title, so it can be added).
- A notification's buttons (reply, snooze) are in Windows' notification centre, not in Dynaland.
- Dynaland's brightness level needs a screen whose brightness Windows reports (a laptop's own screen). Without it, Windows' brightness flyout shows as before.
- If Windows shows its flyout for something Dynaland does not show (such as airplane mode), or a popup for a notification it keeps out of its list, Machitan shows Windows' own again, under a second late (popups: about two and a half seconds).

## 0.9.2 (2026-11-25)

### Added

- **File shelf in Dynaland.** Drag files or folders onto Dynaland to park them; it opens on its shelf while you drag. Click one to open it, drag it out into a folder or a chat (Windows' usual rules: moved on the same drive, copied to another; Ctrl copies, Shift moves), or take it off with its X. Ctrl or Shift + click picks several, to drag out or take off together; "Clear all" empties the shelf. Machitan keeps only where the files are (shelf.json, next to pins.json), so the shelf stays after a restart and copies nothing; a file moved or deleted since says "Not found". Up to 50 items. Also reached from the shelf button on Dynaland's camera and notifications page.
- **In-app update.** Settings > About > Updates checks Machitan's releases on GitHub: with its button, and at start and once a day (on by default). A newer version shows there and as "Update: x.y.z" in Start's top row. "Download and install" downloads the installer for your PC (x64 or ARM64, Full or Trimmed), checks it against the SHA-256 GitHub gives for it, and starts Setup, which closes Machitan first and can start it again at the end. Nothing is downloaded or installed without that click, and a check that could not run says so. "Also offer pre-releases" (on for now) includes every version before 1.0 and versions marked as pre-release on GitHub.
- **Calculator and actions in search.** Start's search and the bar's search box answer sums (type `2+2`; Enter or a click copies the answer) and list things to do: Windows Settings pages (Display, Sound, Bluetooth, Wi-Fi, Windows Update and more), Windows tools from the Win+X menu, Show desktop, Lock, the power menu's choices (with the same questions), Sign out (with a question), Exit Machitan, and Machitan's own Settings pages ("bar" finds Taskbar). `apps:` lists apps only.
- **A search box in Settings**, above its pages: it finds them by name.
- **Volume in Dynaland's player.** A mute button and a volume bar right below the music controls. Click or drag the bar, or use the mouse wheel over it. It is Windows' own volume, the same as in Win+A.
- **Action center in Win+A.** Switches for Wi-Fi, Bluetooth, airplane mode, mobile hotspot and live captions, as Windows has them on your PC, plus Project and Cast.
- **Split taskbar** (optional, off by default). Pinned apps on top and open apps below, as two rows in one bar or as two stacked bars (Settings > Taskbar).
- **Windows on Arm.** Machitan builds for ARM64 too, with its own installers (Full and Trimmed).

### Changed

- **Shortcuts in apps running as administrator** are set up by the installer (a ticked box), with no extra Windows question: on after installing. Turned off in Settings > Shortcuts, it stays off through later updates. Only a small helper runs as administrator; Machitan itself keeps normal rights.
- New apps show in Start and search a few seconds after they are installed (and go when removed), without waiting for Start to open.
- Settings > Privacy says what the shelf keeps and what GitHub sees when updates are checked.

### Fixed

- The bar's search icon closes Start before Windows' search opens.

### Known limits

- Win+A has no Night light, Energy saver or Do not disturb switch: Windows has no public way for other apps to switch them.
- Windows does not let File Explorer drop files on an app running as administrator. If Machitan was started as administrator (for example from an app running as administrator), the shelf takes no drops; started normally, it does.
- Shortcuts in apps running as administrator work for one copy of Machitan.exe at a time: the installed one, after Setup.
- An update installs into the folder Setup installed Machitan in, not over a copy run from elsewhere. Machitan installs an update itself only when the release has the installer for your PC under its usual name and GitHub gives its SHA-256; otherwise Settings > About opens the release page.

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
