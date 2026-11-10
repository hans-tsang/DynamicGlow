# Build Dynamic Glow from source with the .NET Framework C# compiler that ships with Windows.
# -Release also writes dist\DynamicGlow-<version>-win-x64.zip (portable folder) and
# dist\DynamicGlow-<version>-win-x64.exe (single exe that unpacks the same files to %LOCALAPPDATA%\DynamicGlow\app) for GitHub releases.
param([switch]$Release)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$lib = Join-Path $root 'lib'
$bin = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $lib, $bin | Out-Null
$version = '1.0.4191.47'
$assemblies = @(
    @{ Entry = 'lib/net462/Microsoft.Web.WebView2.Core.dll'; Name = 'Microsoft.Web.WebView2.Core.dll' },
    @{ Entry = 'lib/net462/Microsoft.Web.WebView2.WinForms.dll'; Name = 'Microsoft.Web.WebView2.WinForms.dll' },
    @{ Entry = 'runtimes/win-x64/native/WebView2Loader.dll'; Name = 'WebView2Loader.dll' }
)
$missing = @($assemblies | Where-Object { -not (Test-Path -LiteralPath (Join-Path $lib $_.Name)) })
if ($missing.Count -gt 0) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = Join-Path ([IO.Path]::GetTempPath()) ('dynamicglow-webview2-' + [Guid]::NewGuid().ToString('N') + '.zip')
    $package = $null
    try {
        Write-Host "Downloading WebView2 SDK $version..."
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$version/microsoft.web.webview2.$version.nupkg" -OutFile $archive -UseBasicParsing
        $package = [IO.Compression.ZipFile]::OpenRead($archive)
        foreach ($assembly in $assemblies) {
            $entry = $package.GetEntry($assembly.Entry)
            if ($null -eq $entry) { throw "Package is missing $($assembly.Entry)" }
            $sourceStream = $entry.Open()
            $targetStream = $null
            try {
                $targetStream = [IO.File]::Create((Join-Path $lib $assembly.Name))
                $sourceStream.CopyTo($targetStream)
            } finally {
                if ($null -ne $targetStream) { $targetStream.Dispose() }
                $sourceStream.Dispose()
            }
        }
    } finally {
        if ($null -ne $package) { $package.Dispose() }
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    }
}
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw '.NET Framework C# compiler not found; build on 64-bit Windows.' }
$appVersion = (Get-Content -LiteralPath (Join-Path $root '..\tools\version.json') -Raw | ConvertFrom-Json).version
if ($appVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid application version' }
$versionSource = Join-Path $bin 'DynamicGlow-version.cs'
$versionText = @"
using System.Reflection;
[assembly: AssemblyTitle("Dynamic Glow")]
[assembly: AssemblyProduct("Dynamic Glow")]
[assembly: AssemblyVersion("$appVersion.0")]
[assembly: AssemblyFileVersion("$appVersion.0")]
[assembly: AssemblyCopyright("Copyright 2026 Deskland authors and Dynamic Glow contributors")]
"@
[IO.File]::WriteAllText($versionSource, $versionText, (New-Object Text.UTF8Encoding($false)))
& $csc /nologo /target:winexe /platform:x64 /optimize+ /debug- /codepage:65001 /win32manifest:"$root\app.manifest" `
    /win32icon:"$root\DynamicGlow.ico" `
    /out:"$bin\DynamicGlow.exe" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:Accessibility.dll `
    /r:"$lib\Microsoft.Web.WebView2.Core.dll" /r:"$lib\Microsoft.Web.WebView2.WinForms.dll" `
    "$root\DesktopHost.cs" "$root\Themes.cs" "$versionSource"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
foreach ($assembly in $assemblies) { Copy-Item -LiteralPath (Join-Path $lib $assembly.Name) -Destination $bin -Force }
Copy-Item -LiteralPath (Join-Path $root 'DynamicGlow.ico') -Destination (Join-Path $bin 'DynamicGlow.ico') -Force
Remove-Item -LiteralPath (Join-Path $bin 'payload.zip') -Force -ErrorAction SilentlyContinue
Write-Host "Built: $bin\DynamicGlow.exe"

# -Release: portable folder (exe + WebView2 DLLs + icon + pages) packed into one deflate-compressed zip.
# Plain files only - no self-extracting exe, which antivirus heuristics tend to flag.
if ($Release) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $dist = Join-Path $root '..\dist'
    New-Item -ItemType Directory -Force $dist | Out-Null
    $dist = (Resolve-Path $dist).Path
    Get-ChildItem -LiteralPath $dist | Remove-Item -Recurse -Force
    $files = [ordered]@{ 'DynamicGlow.exe' = "$bin\DynamicGlow.exe"; 'DynamicGlow.ico' = "$root\DynamicGlow.ico" }
    foreach ($assembly in $assemblies) { $files[$assembly.Name] = Join-Path $lib $assembly.Name }
    foreach ($f in @('custom.html', 'settings.html', 'share.js', 'themes.json', 'i18n\en.json')) { $files['pages\' + $f] = Join-Path $root "..\prototype\$f" }
    foreach ($f in @('LICENSE', 'NOTICE', 'README.md', 'THIRD_PARTY_NOTICES.md')) { $files[$f] = Join-Path $root "..\$f" }
    $zipPath = Join-Path $dist "DynamicGlow-$appVersion-win-x64.zip"
    $z = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
        foreach ($k in $files.Keys) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $files[$k], 'DynamicGlow/' + $k.Replace('\', '/'), 'Optimal')
        }
    } finally { $z.Dispose() }
    Write-Host "Release: $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1KB)) KB)"

    # Single exe: the same files minus the exe, embedded as files.zip (unpacked once per version at first start)
    $single = Join-Path $bin 'single'
    New-Item -ItemType Directory -Force $single | Out-Null
    $filesZip = Join-Path $single 'files.zip'
    if (Test-Path -LiteralPath $filesZip) { Remove-Item -LiteralPath $filesZip -Force }
    $z = [IO.Compression.ZipFile]::Open($filesZip, 'Create')
    try {
        foreach ($k in $files.Keys) {
            if ($k -eq 'DynamicGlow.exe' -or $k -eq 'README.md') { continue }
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $files[$k], $k.Replace('\', '/'), 'Optimal')
        }
    } finally { $z.Dispose() }
    $exePath = Join-Path $dist "DynamicGlow-$appVersion-win-x64.exe"
    & $csc /nologo /target:winexe /platform:x64 /optimize+ /debug- /codepage:65001 /define:SINGLE /win32manifest:"$root\app.manifest" `
        /win32icon:"$root\DynamicGlow.ico" `
        /out:"$exePath" "/resource:$filesZip,files.zip" `
        /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:Accessibility.dll /r:System.IO.Compression.dll `
        /r:"$lib\Microsoft.Web.WebView2.Core.dll" /r:"$lib\Microsoft.Web.WebView2.WinForms.dll" `
        "$root\DesktopHost.cs" "$root\Themes.cs" "$versionSource"
    if ($LASTEXITCODE -ne 0) { throw 'Single-exe build failed' }
    Write-Host "Release: $exePath ($([math]::Round((Get-Item $exePath).Length / 1KB)) KB)"

    $sums = foreach ($p in @($zipPath, $exePath)) { "$((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path $p -Leaf)" }
    $sums | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
}