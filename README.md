# Dynamic Island

An adaptive Windows wallpaper that uses **your own picture**. Desktop icons get a soft frosted-glass, shadow or glow patch that follows them as you move them around, and the picture can slowly pan and zoom.

Dynamic Island is a personal fork of [Deskland](https://github.com/BLEZZ0102/Deskland) (GPL-3.0). It was built by analysing Deskland with [REA](https://github.com/morluto/rea), then replacing the built-in Beach and Meadow themes with a single "My Wallpaper" theme.

## Requirements

- Windows 10 / 11, 64-bit
- Microsoft Edge WebView2 Runtime (already present on most Windows 10/11 PCs)

## Build and run

```powershell
powershell -ExecutionPolicy Bypass -File host\build.ps1
host\bin\DynamicIsland.exe
```

The settings window opens. Click **Choose image...**, pick a JPG / PNG / WebP / BMP / GIF / AVIF, and it becomes your wallpaper immediately. Quit from the tray icon (or `host\bin\DynamicIsland.exe --exit`) to restore your normal Windows wallpaper.

## Settings

| Group | Setting | What it does |
| --- | --- | --- |
| Your image | Wallpaper image | The picture. A copy is kept in `%LOCALAPPDATA%\DynamicIsland\wallpaper\`, so you can move or delete the original. |
| Image | Fit | Fill, Fit, Stretch or Center |
| Image | Dim | Darkens the picture so icons stay readable |
| Around icons | Effect | None, Frosted, Shadow or Glow |
| Around icons | Spread / Strength | Size and intensity of the effect |
| Motion | Slow pan & zoom | Off, Slow or Faster Ken Burns motion (paused automatically behind full-screen windows) |

Turn on **Start with Windows** in settings to launch it at sign-in.

## Preview in a browser

```powershell
node prototype\serve.mjs
```

Open http://localhost:5178/ for the settings UI with mock data, or http://localhost:5178/custom.html?img=<url> to see the wallpaper page alone.

## Layout

- `host/` - Windows host (C#, WebView2). See [host/README.md](host/README.md).
- `prototype/custom.html` - the wallpaper page.
- `prototype/settings.html`, `themes.json`, `i18n/en.json` - settings UI.
- `prototype/share.js` - share image / share video.
- `tools/` - optional NSIS installer build.

## License

GPL-3.0, see [LICENSE](LICENSE) and [NOTICE](NOTICE). Not affiliated with or endorsed by the Deskland authors; see [TRADEMARKS.md](TRADEMARKS.md). "Dynamic Island" is an Apple feature name, so rename the app before distributing it publicly.