# WON

Miller Columns file explorer for Windows (WinUI 3 / .NET 8).

WON is a fork of [SPAN Finder](https://github.com/LumiBearStudio/SpanFinder) by LumiBear Studio, licensed under **GPL-3.0** (see `LICENSE`). All upstream copyright notices are preserved.

## What's different from upstream

- **Servers sidebar section** — saved SFTP / FTP / FTPS / SMB connections now live in their own "Servers" group (with a `+` button to add one) instead of being mixed into "Network".
- **Hide sidebar sections** — right-click the header of Local Drives / Cloud / Network / Servers and choose *Hide section*. Re-enable under Settings → Sidebar or from the command palette.
- **No outbound crash reporting** — the upstream Sentry DSN was removed; the app no longer sends crash data anywhere.
- **No bundled signing key** — the upstream repository committed a private signing key (`SpanFinder_Dev.pfx`) together with its password. It is removed here; never reuse it.
- Rebranded package identity (`WON.FileExplorer`, publisher `CN=WON`).

## Build

Requires Windows 10 1809+ and the .NET 8 SDK.

```powershell
cd src\Span\Span
dotnet build Span.csproj -c Release -p:Platform=x64 -r win-x64 -p:PublishReadyToRun=false
dotnet test ..\Span.Tests -c Release -p:Platform=x64
```

### Signing an MSIX

Signing material is **not** in this repo. Pass your own certificate at package time:

```
msbuild Span.csproj -p:WonSignPfx=C:\path\WON.pfx -p:WonSignPassword=*** ...
```

Users must trust the certificate you sign with (or you must use a certificate their organisation trusts).

## Security notes

- `third_party/otterzip` contains a prebuilt native DLL inherited from upstream. It has not been independently audited.
- The SSH.NET 2025.1.0 dependency has known advisories (NU1903) and should be upgraded.

## License

GPL-3.0. Derivative works must remain GPL-3.0 and ship their source.
