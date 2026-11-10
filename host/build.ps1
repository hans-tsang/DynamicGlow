# Build Dynamic Glow from source with the .NET Framework C# compiler that ships with Windows.
# -Release also writes dist\DynamicGlow.exe (single file, compressed payload) and a zip for GitHub releases.
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
# Pages + WebView2 DLLs + icon, deflate-compressed, embedded in the exe (unpacked on first run; see Payload in DesktopHost.cs).
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$payload = Join-Path $bin 'payload.zip'
if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Force }
$zip = [IO.Compression.ZipFile]::Open($payload, 'Create')
try {
    $proto = Join-Path $root '..\prototype'
    foreach ($f in @('custom.html', 'settings.html', 'share.js', 'themes.json', 'i18n\en.json')) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $proto $f), 'prototype/' + $f.Replace('\', '/'), 'Optimal')
    }
    foreach ($assembly in $assemblies) { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $lib $assembly.Name), $assembly.Name, 'Optimal') }
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $root 'DynamicGlow.ico'), 'DynamicGlow.ico', 'Optimal')
} finally { $zip.Dispose() }
& $csc /nologo /target:winexe /platform:x64 /optimize+ /debug- /codepage:65001 /win32manifest:"$root\app.manifest" `
    /win32icon:"$root\DynamicGlow.ico" `
    /out:"$bin\DynamicGlow.exe" `
    "/resource:$payload,payload.zip" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll `
    /r:"$lib\Microsoft.Web.WebView2.Core.dll" /r:"$lib\Microsoft.Web.WebView2.WinForms.dll" `
    "$root\DesktopHost.cs" "$root\Themes.cs" "$versionSource"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
foreach ($assembly in $assemblies) { Copy-Item -LiteralPath (Join-Path $lib $assembly.Name) -Destination $bin -Force }
Copy-Item -LiteralPath (Join-Path $root 'DynamicGlow.ico') -Destination (Join-Path $bin 'DynamicGlow.ico') -Force
Write-Host "Built: $bin\DynamicGlow.exe ($([math]::Round((Get-Item "$bin\DynamicGlow.exe").Length / 1KB)) KB, payload $([math]::Round((Get-Item $payload).Length / 1KB)) KB)"

if ($Release) {
    $dist = Join-Path $root '..\dist'
    New-Item -ItemType Directory -Force $dist | Out-Null
    $dist = (Resolve-Path $dist).Path
    Get-ChildItem -LiteralPath $dist -File | Remove-Item -Force
    Copy-Item -LiteralPath "$bin\DynamicGlow.exe" -Destination (Join-Path $dist 'DynamicGlow.exe')
    $zipPath = Join-Path $dist "DynamicGlow-$appVersion-win-x64.zip"
    $z = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
        foreach ($f in @('host\bin\DynamicGlow.exe', 'LICENSE', 'NOTICE', 'README.md', 'THIRD_PARTY_NOTICES.md')) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, (Join-Path $root "..\$f"), (Split-Path $f -Leaf), 'Optimal')
        }
    } finally { $z.Dispose() }
    Get-ChildItem -LiteralPath $dist -File | ForEach-Object {
        $h = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$h  $($_.Name)"
    } | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "Release files in $dist"
}