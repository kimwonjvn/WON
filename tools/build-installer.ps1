<#
.SYNOPSIS
  Builds WON_Setup_<version>_x64.exe: a one-file installer that embeds the signed MSIX and its certificate.

.DESCRIPTION
  Run AFTER the signed MSIX exists (dotnet publish ... -p:GenerateAppxPackageOnBuild=true, see README "Signing an MSIX").
  The exe is signed with the same certificate; signing keys stay outside the repository (-SigningDir).

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

$pkgDir = Join-Path $DistDir "Span_${Version}_x64_Test"
$msix = Join-Path $pkgDir "Span_${Version}_x64.msix"
$cer = Join-Path $pkgDir "Span_${Version}_x64.cer"
foreach ($f in $msix, $cer) { if (-not (Test-Path -LiteralPath $f)) { throw "Missing $f - build the signed MSIX first." } }

$thumb = (New-Object Security.Cryptography.X509Certificates.X509Certificate2($cer)).Thumbprint
$sig = Get-AuthenticodeSignature -LiteralPath $msix
if ($sig.SignerCertificate.Thumbprint -ne $thumb) { throw 'The MSIX is not signed by the certificate next to it.' }

$semver = ($Version -split '\.')[0..2] -join '.'
$proj = Join-Path $repo 'src\WonSetup\WonSetup.csproj'
dotnet build $proj -c Release "-p:PayloadMsix=$msix" "-p:PayloadCer=$cer" "-p:CertThumbprint=$thumb" "-p:AppVersion=$semver"
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
