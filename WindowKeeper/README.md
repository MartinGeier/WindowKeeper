# WindowKeeper

A small Windows 11 tray app that remembers where your application windows are: position, size,
maximized state, monitor and virtual desktop. It keeps a separate saved layout for every monitor
configuration.

## How it works

- **Saving only happens when you ask.** In the tray menu, choose **Save window layout for these monitors**,
  or press **Ctrl+Alt+Shift+S** anywhere (**Ctrl+Alt+Shift+R** restores).
  WindowKeeper saves every open application window for the monitors connected right now, replacing what
  was saved before for that monitor setup.
- **One layout per monitor configuration.** A configuration is the set of connected physical monitors
  (identified by their hardware id), each with its resolution and scaling. The laptop screen alone, the
  laptop with the office monitor, and the home dock each get their own saved layout. How the monitors are
  arranged relative to each other doesn't matter, because positions are stored relative to each monitor.
- **Restoring happens automatically:**
  - when the monitor setup changes (dock/undock, monitor switched on or off, resolution or scaling change,
    resume from sleep, unlock, RDP). Restoring waits until the setup has been stable for 3 seconds.
  - when an application opens a window that has a saved place, e.g. after a reboot. The window gets its
    saved place 1.5 seconds after it appears.
  - when WindowKeeper starts (for example at login).
  - and at any time from **Restore saved layout now**.
- **Per-monitor fallback.** Say you saved layouts for "laptop + monitor A" and "laptop + monitor B", and you
  later connect a combination you never saved. A window can still go back to its saved place on any
  monitor that is connected again with the same resolution.
- **Virtual desktops.** Each window's desktop is saved, and the window is moved back to that desktop.
  Windows 11 keeps desktop ids across reboots. If a desktop was deleted, the desktop's position
  (1st, 2nd, ...) is used instead.
- **Survives reboots.** Data is in `%APPDATA%\WindowKeeper\layouts.json`. It is written atomically and a
  `.bak` copy is kept. Turn on **Start with Windows** in the menu.

Windows are recognized by process (exe), window class and title. If an app has several windows,
the one with the same title gets its own place first.

**Browser windows** (Edge, Chrome, Brave, Vivaldi, Opera, Firefox, ...) are recognized by their **open
tabs**, because their title only shows the active tab and changes all the time. WindowKeeper reads the tab
titles through UI Automation when you save. When restoring, it gives each browser window the saved place
whose tabs are most alike. So two browser windows on two desktops each go back to their own desktop,
also after a reboot when the browser restores its session. A browser window that just opened gets up to
10 seconds to fill its tab strip before it is placed. A window is never minimized or un-minimized by a
restore. A minimized window only gets its "restore to" position fixed.

## Keyboard shortcuts

The shortcuts are system-wide and work in every app. While WindowKeeper runs, no other app receives
them. Change them in `settings.json` in the data folder, then restart WindowKeeper:

```json
"SaveHotkey": "Ctrl+Alt+Shift+S",
"RestoreHotkey": "Ctrl+Alt+Shift+R"
```

Use `Ctrl`, `Alt`, `Shift`, `Win` plus a key name (`A`-`Z`, `0`-`9`, `F1`-`F24`, `Home`, ...). An empty
value turns the shortcut off. If another program has already registered a combination, WindowKeeper
shows a notification and writes it to the log.

Ctrl+Shift+S and Ctrl+Shift+R are not the default on purpose. Many apps use them ("Save As" in VS Code,
Visual Studio and Notepad++; "reload without cache" in all browsers). A global shortcut would take
them away from those apps.

## Build and run

Needs the .NET 8 SDK.

```bash
dotnet build -c Release
```

```bash
bin\Release\net8.0-windows\WindowKeeper.exe
```

To get a single exe to keep in a fixed place (e.g. for autostart):

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## Command line (for scripts and troubleshooting)

| Option | What it does |
| --- | --- |
| `--diag` | Lists monitors, layout key, virtual desktops and all tracked windows (with browser tabs) |
| `--save` | Saves the layout for the current monitors and exits |
| `--restore` | Restores the layout for the current monitors and exits |
| `--only <text>` | Only handle windows whose process or title contains the text (also in tray mode, for testing) |
| `--data <folder>` | Uses another data folder |

Output goes to stdout. Because this is a GUI exe, redirect it, for example
`Start-Process -Wait -NoNewWindow WindowKeeper.exe --diag -RedirectStandardOutput diag.txt`.
A log is written to `log.txt` in the data folder.

## Limitations

- **Moving windows between virtual desktops** uses undocumented Windows shell interfaces, because the
  official API can only move a program's own windows. They are implemented for Windows 11 24H2/25H2
  (build 26100+). At startup WindowKeeper checks them against the registry. If they don't match (for
  example after a future Windows update changes them), desktop moves are turned off and positions are
  still restored. Reading which desktop a window is on uses the official API.
- **Elevated (admin) windows** can only be moved if WindowKeeper itself runs as administrator.
- **UWP/Store apps** all run inside `ApplicationFrameHost.exe`, so they are recognized by title only.
- Apps that position their own windows *after* they appear (later than 1.5 s) may override the restore.
