# Agent notes

- Communicate and write UI text in English. The app is English-only (`prototype/i18n/en.json`); do not add other languages.
- Product name is "Dynamic Island" (exe `DynamicIsland.exe`, data dir `%LOCALAPPDATA%\DynamicIsland`). Do not reintroduce the Deskland name or icon except as attribution.
- There is a single theme, `custom` (`prototype/custom.html`): the user's own image with a frosted / shadow / glow effect around desktop icons. Its parameter defaults in `prototype/themes.json` must match `P` in `custom.html`.
- The chosen image is copied to `%LOCALAPPDATA%\DynamicIsland\wallpaper\` and served by the host as `https://wall.local/<name>` (with CORS so canvases stay exportable).
- Build: `powershell -ExecutionPolicy Bypass -File host\build.ps1`. Page-only changes: use tray "Reload"; C# changes need exit + rebuild.
- Browser-only UI testing: `node prototype\serve.mjs`, then open http://localhost:5178/.
- Keep LICENSE (GPL-3.0) and NOTICE attribution to Deskland.