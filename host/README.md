# Dynamic Island Windows host

C# / .NET Framework + WebView2 host that places the wallpaper page under the Windows desktop icon layer.

## Build and run

```powershell
powershell -ExecutionPolicy Bypass -File host\build.ps1
host\bin\DynamicIsland.exe
```

The build uses the C# compiler that ships with Windows and downloads a pinned WebView2 SDK from NuGet the first time. Dependencies go to `host/lib/`, output to `host/bin/` (neither is committed).

## Command line

| Argument | Behavior |
| --- | --- |
| (none) | Start the wallpaper and open settings; if already running, open settings |
| `--autostart` | Start in the background without opening settings |
| `--settings` | Open settings |
| `--menu` | Show the tray menu |
| `--exit` | Quit and restore the system wallpaper |
| `--exit-installed` | Installer use: quit only the instance running from this exe path |

## Behavior and data

- Themes come from `prototype/themes.json`; only `ready` themes load. This fork ships one theme, `custom`.
- By default the current Windows wallpaper (SPI_GETDESKWALLPAPER, falling back to `%APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper`, or the solid background colour) is copied to `%LOCALAPPDATA%\DynamicIsland\wallpaper\` and served as `https://wall.local/<name>`; it is re-checked every 3 s. "Use a different image..." (`pickImage`) sets `source` to `file`; `useWindowsWallpaper` switches back.
- Desktop icon positions are polled every 400 ms; the page is notified only on change.
- Paused while a foreground window is maximized or full screen; can also be paused from the tray.
- Windows 11 24H2+ uses a layered Progman child window; older systems use WorkerW.
- Primary monitor only.
- Settings: `%LOCALAPPDATA%\DynamicIsland\settings.json`; log: `host.log` in the same folder.
- No accounts, no network update checks.
- Sharing uses only icon positions; the page draws sample icons and colored name blocks instead of capturing your desktop.

For install testing, set `DYNAMICISLAND_PROFILE_DIR` to an absolute path to isolate settings, WebView2 cache and the single-instance events.

## Page protocol

The settings page sends `hello`, `set`, `reset`, `theme`, `pickImage`, `autostart` and share messages; the host replies with `init`, `imagePicked`, icon rectangles and share results.

A wallpaper page sends `ready`; the host then sends `settings`, `icons` and `pause` messages, followed by parameter changes, icon changes and pause state.