# Build Dynamic Island from source with the .NET Framework C# compiler that ships with Windows.
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
    $archive = Join-Path ([IO.Path]::GetTempPath()) ('dynamicisland-webview2-' + [Guid]::NewGuid().ToString('N') + '.zip')
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
$versionSource = Join-Path $bin 'DynamicIsland-version.cs'
$versionText = @"
using System.Reflection;
[assembly: AssemblyTitle("Dynamic Island")]
[assembly: AssemblyProduct("Dynamic Island")]
[assembly: AssemblyVersion("$appVersion.0")]
[assembly: AssemblyFileVersion("$appVersion.0")]
[assembly: AssemblyCopyright("Copyright 2026 Deskland authors and Dynamic Island contributors")]
"@
[IO.File]::WriteAllText($versionSource, $versionText, (New-Object Text.UTF8Encoding($false)))
& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /win32manifest:"$root\app.manifest" `
    /win32icon:"$root\DynamicIsland.ico" `
    /out:"$bin\DynamicIsland.exe" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    /r:"$lib\Microsoft.Web.WebView2.Core.dll" /r:"$lib\Microsoft.Web.WebView2.WinForms.dll" `
    "$root\DesktopHost.cs" "$root\Themes.cs" "$versionSource"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
foreach ($assembly in $assemblies) { Copy-Item -LiteralPath (Join-Path $lib $assembly.Name) -Destination $bin -Force }
Copy-Item -LiteralPath (Join-Path $root 'DynamicIsland.ico') -Destination (Join-Path $bin 'DynamicIsland.ico') -Force
Write-Host "Built: $bin\DynamicIsland.exe"
