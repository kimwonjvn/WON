<#
.SYNOPSIS
  Builds WON_Setup_<version>_x64.exe: a one-file installer that needs no administrator approval and no certificate.

.DESCRIPTION
  1. publishes the app unpackaged and self-contained (same sources as the MSIX, -p:WonUnpackaged=true)
  2. zips it, builds the small uninstaller, and embeds both in WON_Setup.exe
  3. signs the exe (integrity for people who check the signature; self-signed does not remove the SmartScreen notice)
  The MSIX (for administrators) is a separate build, see README "Signing an MSIX". Signing keys stay outside the
  repository (-SigningDir).

.EXAMPLE
  .\tools\build-installer.ps1 -DistDir C:\Users\me\dev\WON-dist -SigningDir C:\Users\me\dev\WON-signing
#>
param(
    [Parameter(Mandatory)] [string] $DistDir,
    [Parameter(Mandatory)] [string] $SigningDir,
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $env:Path += ";C:\Program Files\dotnet" }

if (-not $Version) {
    $m = Get-Content (Join-Path $repo 'src\Span\Span\Package.appxmanifest') -Raw
    $Version = [regex]::Match($m, '<Identity[^>]*Version="([\d.]+)"').Groups[1].Value
}
if (-not $Version) { throw 'Could not read the version from Package.appxmanifest.' }
$semver = ($Version -split '\.')[0..2] -join '.'

New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
$appDir = Join-Path $DistDir "WON_${semver}_app"
$zip = Join-Path $DistDir "WON_${semver}_payload.zip"
foreach ($p in $appDir, $zip) { if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force } }

Write-Host '== publishing the app (unpackaged, self-contained)'
dotnet publish (Join-Path $repo 'src\Span\Span\Span.csproj') -c Release -p:Platform=x64 -r win-x64 `
    -p:WonUnpackaged=true -p:PublishReadyToRun=false "-p:PublishDir=$appDir\"
if ($LASTEXITCODE -ne 0) { throw 'Publishing the app failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $appDir 'Span.exe'))) { throw 'Span.exe is missing from the publish output.' }
Get-ChildItem -LiteralPath $appDir -Recurse -File -Filter *.pdb | Remove-Item -Force -Confirm:$false

Write-Host '== zipping the payload'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($appDir, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
"{0:N1} MB" -f ((Get-Item $zip).Length / 1MB)

Write-Host '== building the uninstaller'
dotnet build (Join-Path $repo 'src\WonUninstall\WonUninstall.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Uninstaller build failed.' }
$uninstall = Join-Path $repo 'src\WonUninstall\bin\Release\net472\Uninstall.exe'

Write-Host '== building the installer'
dotnet build (Join-Path $repo 'src\WonSetup\WonSetup.csproj') -c Release "-p:PayloadZip=$zip" "-p:PayloadUninstall=$uninstall" "-p:AppVersion=$semver"
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

$built = Join-Path $repo 'src\WonSetup\bin\Release\net472\WON_Setup.exe'
$out = Join-Path $DistDir "WON_Setup_${semver}_x64.exe"
Copy-Item -LiteralPath $built -Destination $out -Force

$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object FullName -match '\\x64\\' | Sort-Object FullName | Select-Object -Last 1 -ExpandProperty FullName
if (-not $signtool) { throw 'signtool.exe (Windows SDK) not found.' }
$pw = (Get-Content (Join-Path $SigningDir 'password.txt') -Raw).Trim()
& $signtool sign /fd SHA256 /f (Join-Path $SigningDir 'WON_Dev.pfx') /p $pw /d 'WON installer' $out
if ($LASTEXITCODE -ne 0) { throw 'Signing the installer failed.' }

$s = Get-AuthenticodeSignature -LiteralPath $out
"{0}  signature thumbprint {1}" -f $out, $s.SignerCertificate.Thumbprint
"{0:N1} MB" -f ((Get-Item $out).Length / 1MB)
"SHA-256: " + (Get-FileHash $out -Algorithm SHA256).Hash
