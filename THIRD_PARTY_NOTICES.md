# Third-party dependencies

## Microsoft Edge WebView2

The host uses the Microsoft Edge WebView2 SDK and Runtime. SDK binaries are downloaded by the build script from NuGet and are not checked into this repository.

- Package: `Microsoft.Web.WebView2`, version `1.0.4191.47`
- Package and license: https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4191.47
- Docs: https://learn.microsoft.com/microsoft-edge/webview2/

WebView2 and Windows / .NET Framework components keep their own license terms; this project's GPL-3.0 license does not change them.

## NSIS

The optional Windows installer is built with NSIS 3.13. The portable compiler is downloaded at build time only. NSIS and its compression modules keep their own licenses: https://nsis.sourceforge.io/License. The installer ships the matching source ZIP.