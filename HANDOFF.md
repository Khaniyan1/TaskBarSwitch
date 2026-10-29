# TaskbarSwitch – handoff

Windows tray app that switches taskbar settings depending on whether an external
monitor is connected. .NET Framework 4.8 WinForms (ships with Windows 11, so no runtime
needed), single exe, per-user install to `%LOCALAPPDATA%\Programs\TaskbarSwitch`.

## Current state (v1.0.1)

- `Displays.cs` – detects built-in vs external screens via WMI `WmiMonitorConnectionParams`.
  Works on Jacob's laptop (internal = 0x80000000, external over USB-C = DisplayPort).
- `TaskbarProfile.cs` – reads/writes the taskbar registry values, restarts Explorer,
  sets auto-hide live with `SHAppBarMessage`, verifies 3 s and 11 s after.
  **On build 26200 (movable taskbar) the real position is `Explorer\Advanced\TaskbarLocation`**
  (0 left, 1 top, 2 right, 3 bottom). `StuckRects3` byte 12 alone gets overwritten by Explorer.
  v1.0.1 writes both. Not yet confirmed working on Jacob's machine.
- `Recovery.cs` – backups of only the 8 touched values (`backups\original.reg`, last 10
  `before-*.reg`), restore original, reset to Windows defaults, `/restore` and `/reset` switches.
- `Log.cs` – `%APPDATA%\TaskbarSwitch\log.txt`, rotation at 1 MB, global crash handlers in `Program.cs`.
- `Installer.cs` – self-install, Run key autostart, Start menu shortcut, Apps & features entry,
  `/uninstall` (keeps backups).
- `SettingsForm.cs` / `TrayApp.cs` – UI with one column per setup, tray menu.

Build: `dotnet build -c Release` → `bin\Release\net48\TaskbarSwitch.exe`.

## Next: auto-update from GitHub with beta and release channels

Decided so far:

- **Channels map to GitHub releases.** Stable = normal releases (`/releases/latest`).
  Beta = newest release including pre-releases. Tags: `v1.2.0` (stable), `v1.2.0-beta.1` (pre-release).
- **GitHub Actions** on tag push: build on `windows-latest`, attach `TaskbarSwitch.exe` and
  `TaskbarSwitch.exe.sha256` to the release, mark `-beta` tags as pre-release automatically.
- **Updater in the app:**
  - Settings: update channel (Stable / Beta), "Install updates automatically" vs "Just tell me".
  - Check at startup (after ~1 min) and every 24 h, plus "Check for updates now" in tray menu.
  - Download to `%LOCALAPPDATA%\TaskbarSwitch\update\`, verify SHA-256 against the release asset,
    refuse anything that doesn't match or isn't a newer version.
  - Swap: start the downloaded exe with `/update <installed path>`; it waits for the old process
    to exit (exit event already exists), copies itself over, relaunches, logs the result.
    Keep the previous exe as `TaskbarSwitch.prev.exe` and roll back if the new one doesn't
    start cleanly.
  - Never update in the middle of applying a taskbar change.
  - Log every check, download, verification and swap.
- **Repo must be public** so the app can read releases without a token. A private repo would
  mean shipping a GitHub token inside the exe, which isn't acceptable.

Open: GitHub username/repo name; whether to sign the exe later (SmartScreen warns on first install only;
updates downloaded by the app itself don't get the web mark, so they won't warn).
