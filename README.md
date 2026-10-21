# WON

Miller Columns file explorer for Windows (WinUI 3 / .NET 8).

WON is a fork of [SPAN Finder](https://github.com/LumiBearStudio/SpanFinder) by LumiBear Studio, licensed under **GPL-3.0** (see `LICENSE`). All upstream copyright notices are preserved.

## What's different from upstream

- **Servers sidebar section** — saved SFTP / FTP / FTPS / SMB connections now live in their own "Servers" group (with a `+` button to add one) instead of being mixed into "Network".
- **Hide sidebar sections** — right-click the header of Local Drives / Cloud / Network / Servers and choose *Hide section*. Re-enable under Settings → Sidebar or from the command palette.
- **No outbound crash reporting** — the upstream Sentry DSN was removed; the app no longer sends crash data anywhere.
- **No bundled signing key** — the upstream repository committed a private signing key (`SpanFinder_Dev.pfx`) together with its password. It is removed here; never reuse it.
- **Local Servers** sidebar group — UNC servers you open from the path bar (`\server`, only if the server answers) are remembered; add locations yourself with `+` and give them a **display name** that is stored only in WON (the real folder is never renamed). Right-click to rename or remove.
- **Path bar toggle** — `Ctrl+Shift+L` hides/shows the path bar; `Ctrl+L` summons it while hidden.
- **Column fold** — right-click a Miller column: *Close this column* (also folds every column to its left) / *Show parent columns* (reopens the nearest folded one).
- **AI assistant** (toolbar button next to Split View, or `Ctrl+Shift+K`) — a chat panel that slides in from the right; give folder commands in plain language through the Claude or OpenAI API, switch models, attach files/images, and add skills. See below.
- **Custom app icon** (a blue folder with a W).
- Rebranded package identity (`WON.FileExplorer`, publisher `CN=WON`).

## AI assistant

Off by default. Open the panel (toolbar button or `Ctrl+Shift+K`), turn it on in the settings area, and paste your own API key. Pick the provider/model from the model menu at the bottom right of the message box (custom model IDs are allowed). Enter sends, Shift+Enter inserts a new line.

- The key is stored in Windows Credential Manager (PasswordVault), never in settings files or this repo.
- Only folder/file **names, paths, sizes and dates** that the model asks about are sent. File and image **contents are sent only when you attach them yourself** (paperclip menu, drag & drop, or paste; text files ≤ 200 KB, images ≤ 5 MB, at most 5 per message). Key and credential files (`.pem`, `.key`, `.pfx`, …) cannot be attached.
- The model can look around (list / search / info) but **cannot change anything**. Moves, copies, renames, new folders and deletes are shown as a plan; only the items you tick are executed, through WON's normal file-operation engine (deletes go to the Recycle Bin; `Ctrl+Z` undoes).
- Protected locations (drive roots, Windows, Program Files, ProgramData, your profile root and its main folders) and overwriting existing items are refused.
- **Skills** are extra instructions for the model: import a Claude `SKILL.md` (file or folder) or write your own, and switch them on/off. Only the text is used — scripts in a skill folder are never imported or run, and a skill cannot grant new tools or permissions.
- A custom base URL (for example an in-house OpenAI-compatible server) must use https, or http on localhost.
- Check your organisation's policy first: names and paths leave the machine when this is on.

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
