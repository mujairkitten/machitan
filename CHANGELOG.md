# Changelog

## 0.9.3 (2026-11-27)

### Added

- **Dynaland instead of Windows' flyouts.** Each has a switch in Settings > Dynaland, all on by default. While Dynaland is hidden for a full-screen app, Windows' own flyouts show as before.
  - **Volume and brightness.** Volume up, down and mute now change the volume through Machitan, and Windows' volume and brightness flyout is hidden, so only Dynaland shows the level. The volume keys also work this way in front of apps running as administrator when "Shortcuts in apps running as administrator" is on.
  - **New notifications.** Windows' popup is hidden and Dynaland opens a little for 5 seconds with the app, the title, the text and the notification's pictures (a person's photo beside the words, a big picture below them); point at it for the full list. The notification stays in Windows' list.
  - **Charger.** Plugging in or unplugging shows "Charging", "Plugged in" or "On battery" with the battery level.
  - **Copy and cut.** "Copied", "Files copied", "Files cut" or "Picture copied". Machitan never reads what is on the clipboard.
- **A widget sidebar** (Settings, Widgets; off by default). It holds the widgets you add, at the right or left edge of the screen. It slides in when you rest the pointer at that edge, or stays docked so maximised windows stop beside it. Drag its inner edge to make it wider or narrower; from 560 DIP wide, its widgets sit two side by side. Docked, it runs the full height of the screen, or leaves room for the indicators in its corners (Settings, Widgets, Height when docked). Widgets can also go **on the desktop**, anywhere you drag them: they stay behind your windows and show when the desktop does, also after Win+D. Widgets can be **resized**: on the desktop by any edge or corner, in the sidebar by a card's bottom edge; a double-click there goes back to the standard size. Widgets can be **dragged** by their name row: onto another sidebar widget to swap the two, out of the sidebar onto the desktop, or from the desktop into the sidebar (a sliding sidebar slides in when you hold the widget at the screen's edge). The widgets: **Clock and calendar** (the time, the date and a month calendar), **Clock** (the time and date only), **Weather** (the info line's Open-Meteo reading, with today's high and low), **Now playing** (cover, title (a long one scrolls), artist, the lyrics when Synced lyrics is on, a bar you can drag or click to jump, and previous, play or pause, next) and **Note** (a text note that saves as you type). No widget is there until you add one. Each can be **customized** (its menu, Customize): the clocks digital or analog, time and date or either alone, left, centre or right, small to large, with seconds, 12- or 24-hour, a long or short date; the weather full or compact, in Celsius or Fahrenheit, with or without the place and the day's high and low; Now playing with or without its cover, lyrics, bar and buttons, left or centred; the note's text size and alignment.
- **Synced lyrics in Dynaland** (Settings, Dynaland; off by default). While a song plays, the line being sung shows in the pill, and in the player in large type across its width with the next line below, from LRCLIB (lrclib.net); the Now playing widget shows them too; the pill widens to fit the line, as far as the open Dynaland is wide. A line still too long gets a little smaller, and if even that is too long, it slides once to show its end before the next line starts. For each song, its title, artist and length are sent there. When a search by title and artist finds nothing, LRCLIB is searched by the title alone and, with WACUP and other Winamp-style players, by the song's file name; then a .lrc file next to the song is used.
- **Wi-Fi networks in quick settings.** The Wi-Fi tile has a chevron, as in Windows 11, that opens the networks nearby: connect (Windows' saved key is used, otherwise the key is asked for; work or school networks with a user name and password, and hidden networks too), disconnect, turn Wi-Fi on or off, look again, or open Windows' Wi-Fi settings. Networks you connect to are kept in Windows' own list of known networks, as with Windows' quick settings; Machitan keeps nothing.

### Changed

- **Quick settings (Win+A) closes** when you click the bar, Dynaland or an indicator, as it does when you click another app.
- **Links and notes on Dynaland's shelf.** Drag a link or some text onto Dynaland, or use the shelf's new Paste button (files, a link or text from the clipboard). Click a link to open it in your browser, click a note to copy it, or drag either one out. Text that is one web address alone becomes a link; other text becomes a note (up to 20,000 characters). Settings > Privacy now also says what the shelf keeps of these, what synced lyrics send to LRCLIB, and what widgets keep.
- The sidebar's menus have a solid background (they showed the widgets through them).
- **Win+D is Machitan's Show desktop,** the same as the bar's desktop strip: it minimises the apps' windows and brings the same ones back with the next press. Widgets on the desktop stay in view; Windows' own Show desktop covered them.
- **The trapezoid notch has rounder corners,** as soft as the standard notch's.
- **"Corner widgets" are now "Indicators"** (the info line and the status ring), since "widgets" now means the sidebar's.
- **Quick settings (Win+A) in Windows 11's own look.** Tiles four to a row with the icon and name inside, filled with the accent while on; Wi-Fi is a wide tile with the network's name and "Connected". The volume and brightness sliders follow, then a band with the battery and its level (it opens Windows' battery settings), a pencil and the cogwheel (Windows' Settings). The pencil opens the edit mode: take a tile off with its pin, bring it back with Add, drag a tile onto another to move it, and drag the volume or brightness slider onto the other to swap them; Done (or Escape) ends it. The orders and the tiles taken off are kept.
- **The status ring on a desktop PC and on a network cable.** With no battery, the rim is drawn whole instead of showing only its faint track. On a network cable the ring shows a plug and the network's name, as Windows' Settings shows it ("Ethernet" only when Windows gives none). Wi-Fi and the volume dots are as before.
- **x64 installers are marked x64.** They are now `Machitan-Setup-<version>-x64-Full.exe` and `Machitan-Setup-<version>-x64-Trimmed.exe`, like the ARM64 ones. The in-app update finds the x64 installer under either name.

### Fixed

- The four Settings > Dynaland switches added in this version were not saved: turning one off lasted only until Machitan restarted.

### Known limits

- Quick settings has no Energy saver, Night light or Nearby sharing tile yet: Windows offers apps no supported way to switch them. They are being tested.
- On Windows 11 24H2 and later, the Wi-Fi list shows the networks nearby only with "Let desktop apps access your location" on (Settings > Privacy & security > Location): Windows shows them only to apps that may use the location. Without it the list shows the networks Windows has saved, to connect to one when it is in reach, and a button to that setting. While the list looks for networks, Windows may show its location icon.

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
