<#
.SYNOPSIS
  Builds the self-contained app, a portable zip and the MSI installer into artifacts\out.

.EXAMPLE
  .\build.ps1                      # version from the csproj (0.0.1)
  .\build.ps1 -Version 0.2.0       # what CI runs for tag v0.2.0
  .\build.ps1 -SkipInstaller       # just publish + zip
#>
param(
    [string]$Version = "",
    [string]$Configuration = "Release",
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root "artifacts\publish"
$out = Join-Path $root "artifacts\out"
$WixVersion = "5.0.2"

if (-not $Version) {
    $Version = ([xml](Get-Content "$root\AmpTray.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
# MSI/file versions must be numeric: drop any -prerelease suffix.
$numericVersion = ($Version -split '-')[0]

Remove-Item -Recurse -Force $publish, $out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out | Out-Null

if (-not (Test-Path "$root\Assets\app.ico")) { & "$root\build\make-icon.ps1" }

Write-Host "== Publishing $Version (self-contained win-x64)"
dotnet publish "$root\AmpTray.csproj" -c $Configuration -r win-x64 --self-contained true `
    -p:Version=$Version -p:FileVersion=$numericVersion -p:PublishSingleFile=false -o $publish --nologo -v q
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }
Get-ChildItem $publish -Filter *.pdb | Remove-Item -Force

Write-Host "== Portable zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = "$out\AmpTray-$Version-win-x64-portable.zip"
# Freshly published files are sometimes still being scanned by antivirus; retry briefly instead of failing.
for ($attempt = 1; ; $attempt++) {
    try {
        Remove-Item $zip -ErrorAction SilentlyContinue
        [System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
        break
    } catch {
        if ($attempt -ge 5) { throw }
        Write-Host "   zip attempt $attempt failed ($($_.Exception.Message.Trim())); retrying"
        Start-Sleep -Seconds 3
    }
}

if ($SkipInstaller) { return }

Write-Host "== MSI installer (WiX $WixVersion)"
$wix = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wix) {
    $toolPath = Join-Path $root "artifacts\tools"
    if (-not (Test-Path "$toolPath\wix.exe")) {
        dotnet tool install wix --version $WixVersion --tool-path $toolPath | Out-Null
    }
    $wixExe = "$toolPath\wix.exe"
} else {
    $wixExe = $wix.Source
}
Push-Location $root
try {
    & $wixExe extension add "WixToolset.Util.wixext/$WixVersion" | Out-Null
    & $wixExe extension add "WixToolset.UI.wixext/$WixVersion" | Out-Null
    & $wixExe build "$root\installer\Product.wxs" -arch x64 -ext WixToolset.Util.wixext -ext WixToolset.UI.wixext `
        -d "Version=$numericVersion" -d "PublishDir=$publish" -d "SourceDir=$root" `
        -pdbtype none -o "$out\AmpTray-$Version-x64.msi"
    if ($LASTEXITCODE) { throw "wix build failed ($LASTEXITCODE)" }
} finally {
    Pop-Location
}

Write-Host "== Done"
Get-ChildItem $out | Format-Table Name, @{ n = 'MB'; e = { [Math]::Round($_.Length / 1MB, 1) } } -AutoSize
