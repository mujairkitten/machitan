# Machitan

An alternative modern shell for Windows 11: a segmented pill bar, a floating Start menu, corner widgets and Dynaland.

**Status: early build (line i1, Batch 4).** Explorer's taskbar is replaced by a see-through Machitan bar (Start cluster and app dock as separate pills, or joined, or full width; pinned apps; Show Desktop; auto-hide) and Machitan's own Start menu (search with `apps:` and `files:`, Pinned, All apps, power). A Settings window (right-click the bar > Settings) covers every option. A lone Win key opens Machitan's Start; Win+1..9, Win+N (calendar), Win+A (volume and brightness) and Win+` (window switcher) are Machitan's (Settings > System status shows each shortcut's state). See `CHANGELOG-i1.md`.

## Stack

- .NET 10 (`net10.0-windows10.0.19041.0`), C#, WinUI 3
- Windows App SDK 2.6.5
- System.Management (Windows' WMI, for the laptop screen's brightness)
- x64, unpackaged, self-contained
- WinUI 3 and native Win32 interop only. No WebView, no web tech.

## Build

1. Install Visual Studio 2026 with the **WinUI application development** workload and the .NET 10 SDK.
2. Open `Machitan.slnx`.
3. Pick `Debug` or `Release`, platform `x64`.
4. Build, then run (F5).

If a build complains about a symbol that no longer exists, delete `bin/`, `obj/` and `.vs/`, then rebuild.

## Running

| Start it as | What happens |
|---|---|
| `Machitan.exe` | Runs the shell. Explorer's taskbar is hidden until Machitan exits. |
| `Machitan.exe --exit` | Asks the running shell to close cleanly. |
| `Machitan.exe --proof` | Opens only the backdrop proof window. Explorer is left alone. |
| `Machitan.exe --seethrough` | Opens only the see-through test window. Explorer is left alone. |

You can also right-click the bar and choose **Exit Machitan**.

If Machitan crashes or is killed, a small guardian process puts Explorer's taskbar back.

## Files it keeps

All in `%AppData%\Machitan`:

- `settings.json` - your settings. Edits are picked up while Machitan runs.
- `pins.json` - pinned apps (created on the first pin).
- `diagnostics.log` - what happened, for troubleshooting. It can contain app names and file paths.

## Principles

- No fake content. Every number, icon and list item comes from real data, or the space stays empty.
- Failures are logged, never fatal.
- Explorer's taskbar is always restored when Machitan exits.

## License

GNU General Public License, version 3. See `LICENSE`.
