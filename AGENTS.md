# Agent notes

- Communicate and write UI text in English. The app is English-only (`prototype/i18n/en.json`); do not add other languages.
- Product name is "Dynamic Glow" (exe `DynamicGlow.exe`, data dir `%LOCALAPPDATA%\DynamicGlow`). Do not reintroduce the Deskland name or icon except as attribution.
- There is a single theme, `custom` (`prototype/custom.html`): the user's own image with a frosted / shadow / glow effect around desktop icons. Its parameter defaults in `prototype/themes.json` must match `P` in `custom.html`.
- The image defaults to the current Windows wallpaper (params.custom.source = windows, polled every 3 s); a user-chosen file sets source = file. Either way it is copied to `%LOCALAPPDATA%\DynamicGlow\wallpaper\` and served by the host as `https://wall.local/<name>` (with CORS so canvases stay exportable).
- Build: `powershell -ExecutionPolicy Bypass -File host\build.ps1`. Page-only changes: use tray "Reload"; C# changes need exit + rebuild.
- Browser-only UI testing: `node prototype\serve.mjs`, then open http://localhost:5178/.
- Keep LICENSE (GPL-3.0) and NOTICE attribution to Deskland.