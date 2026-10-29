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

## Auto-update (written, not yet compiled or tested)

Repo: https://github.com/Khaniyan1/TaskBarSwitch (public, GPL-3.0; public so the app can read
releases without a token). No .NET SDK on Jacob's PC yet, so nothing has been compiled locally.

**Branches and releases**

- `main` = stable, what the public gets. `beta` = day-to-day development.
- Tag `v1.2.0-beta.1` on `beta` -> pre-release -> only Beta-channel installs get it.
- Merge `beta` into `main`, tag `v1.2.0` there -> normal release -> everyone gets it.
  `release.yml` refuses a stable tag that isn't on `main`.
- The version comes from the tag (`-p:Version=`); `<Version>` in the csproj is only for local builds.
- `build.yml` builds every push/PR to `main` and `beta` and uploads the exe as a workflow artifact.

**Updater** (`Updater.cs`, hooks in `TrayApp.cs`, `SettingsForm.cs`, `Program.cs`)

- Settings: channel Stable / Beta, "Install updates automatically" (off = balloon + tray item).
  Stored as `UpdateChannel` / `AutoUpdate` in settings.ini.
- Stable reads `/releases/latest` (never a pre-release). Beta takes the newest of all releases,
  so it also gets stable releases. Switching Beta -> Stable never downgrades; you stay on the beta
  until a newer stable release exists.
- Check 1 min after start, then every 24 h (timer looks hourly), plus "Check for updates" in the tray.
  Only the installed copy updates itself.
- Download to `%LOCALAPPDATA%\TaskbarSwitch\update\`. Must match the `.sha256` asset (and GitHub's
  asset digest when present), and the exe's own ProductVersion must equal the tag and be newer.
- The update takes the same `applying` slot as a taskbar change, so the two never overlap.
- Swap: `new.exe /update "<installed exe>" <old pid> <old version>`. It signals the old copy to exit,
  saves `TaskbarSwitch.prev.exe` next to the installed exe, copies itself over, starts it with `/updated`.
  The new version sets `Local\TaskbarSwitch.Started` 5 s after starting. No signal within 60 s, or the
  process dies -> roll back to prev.exe and add the version to `update\bad-versions.txt` so it isn't retried.
- If the updater never takes over, the old copy keeps running after 60 s.

Open: whether to sign the exe later (SmartScreen warns on first install only;
updates downloaded by the app itself don't get the web mark, so they won't warn).
