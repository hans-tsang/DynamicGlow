# Dynamic Glow

[![build](https://github.com/hans-tsang/DynamicGlow/actions/workflows/build.yml/badge.svg)](https://github.com/hans-tsang/DynamicGlow/actions/workflows/build.yml) [![release](https://img.shields.io/github/v/release/hans-tsang/DynamicGlow)](https://github.com/hans-tsang/DynamicGlow/releases/latest)

An adaptive Windows wallpaper that uses **your current Windows wallpaper**, whatever it is, and follows it when it changes (including slideshows and Spotlight / Bing apps). Desktop icons get a soft frosted-glass, shadow or glow patch that follows them as you move them around, and the picture can slowly pan and zoom.

Dynamic Glow is a personal fork of [Deskland](https://github.com/BLEZZ0102/Deskland) (GPL-3.0). It was built by analysing Deskland with [REA](https://github.com/morluto/rea), then replacing the built-in Beach and Meadow themes with a single "My Wallpaper" theme.

## Requirements

- Windows 10 / 11, 64-bit
- Microsoft Edge WebView2 Runtime (already present on most Windows 10/11 PCs)

## Download

Download **DynamicGlow-<version>-win-x64.zip** from the [latest release](https://github.com/hans-tsang/DynamicGlow/releases/latest), extract the whole zip to a folder (for example `%LOCALAPPDATA%\Programs\DynamicGlow`) and run `DynamicGlow.exe` - no installer needed. Keep the DLLs and the `pages` folder next to the exe.

Prefer one file? **DynamicGlow-<version>-win-x64.exe** is the same app as a single exe: on first start it unpacks its DLLs and pages to `%LOCALAPPDATA%\DynamicGlow\app`. Some antivirus products are more suspicious of exes that unpack files, so the zip is the safer choice.

Windows SmartScreen may warn because the exe is unsigned; choose *More info > Run anyway*. `SHA256SUMS.txt` in each release lists the file hashes.

The app reads desktop icon positions through the Windows accessibility interface (the one screen readers use) and never reads or writes another program's memory, so behaviour-based antivirus (e.g. Bitdefender Advanced Threat Defense) has nothing to flag. Versions before 0.2.3 used the older cross-process memory method and could be blocked.

## Fast and smooth

- Draws only when something changes (icon moved, wallpaper changed, setting changed); idle CPU/GPU use is effectively zero.
- Pan & zoom motion is capped at 30 fps, and pauses automatically behind full-screen windows.
- Effect layers are cached and the image is decoded off the main thread before the first frame.

## Build from source

```powershell
powershell -ExecutionPolicy Bypass -File host\build.ps1
host\bin\DynamicGlow.exe
```

`host\build.ps1 -Release` additionally writes the portable zip, the single exe and `SHA256SUMS.txt` to `dist`. Only the .NET Framework compiler that ships with Windows is needed; the WebView2 SDK is downloaded automatically.

It picks up your current Windows wallpaper automatically; change the wallpaper in Windows Settings > Personalization > Background and Dynamic Glow follows within a few seconds. To use a different picture instead, click **Use a different image...** in settings; **Use my Windows wallpaper** switches back. Quit from the tray icon (or `host\bin\DynamicGlow.exe --exit`) to restore your normal Windows wallpaper.

## Settings

| Group | Setting | What it does |
| --- | --- | --- |
| Your image | Your Windows wallpaper / a chosen image | The picture (a copy is kept in `%LOCALAPPDATA%\DynamicGlow\wallpaper\`). |
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
- `tools/` - version number and optional NSIS installer build.
- `.github/workflows/build.yml` - CI: builds every push / PR; pushing a `v*` tag publishes a GitHub release.

## Releasing

Bump `tools/version.json`, commit, then `git tag v<version>` and `git push --tags`.

## License

GPL-3.0, see [LICENSE](LICENSE) and [NOTICE](NOTICE). Not affiliated with or endorsed by the Deskland authors; see [TRADEMARKS.md](TRADEMARKS.md).