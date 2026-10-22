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
- **AI assistant** (toolbar button next to Split View, or `Ctrl+Shift+K`) — a chat panel that slides in from the right; give folder commands in plain language through the Claude or OpenAI API or a local model (Ollama), switch models, attach files/images, and add skills. See below.
- **Recent documents** — a sidebar section (Excel / PowerPoint / Documents) built from Windows' own recent-items shortcuts. Click a row for the latest files, click a file to open it, right-click for *Show in folder* / *Copy path*. Hide it from the section header's right-click menu or in Settings → Sidebar.
- **PDF conversion** — right-click files → *Convert to PDF* / *Merge into one PDF*, or select files and press `Ctrl+Shift+D` / `Ctrl+Shift+M`. See below.
- **Custom app icon** (a transparent-background blue folder with a W), shipped in every size/variant Windows asks for so the taskbar does not paint a coloured plate behind it.
- Rebranded package identity (`WON.FileExplorer`, publisher `CN=WON`).

## AI assistant

Off by default. Open the panel (toolbar button or `Ctrl+Shift+K`), turn it on in the settings area, and paste your own API key. Pick the provider/model from the model menu at the bottom right of the message box (custom model IDs are allowed). Enter sends, Shift+Enter inserts a new line.

- The key is stored in Windows Credential Manager (PasswordVault), never in settings files or this repo.
- Only folder/file **names, paths, sizes and dates** that the model asks about are sent. File and image **contents are sent only when you attach them yourself** (paperclip menu, drag & drop, or paste; text files ≤ 200 KB, images ≤ 5 MB, at most 5 per message). Key and credential files (`.pem`, `.key`, `.pfx`, …) cannot be attached.
- The model can look around (list / search / info) but **cannot change anything**. Moves, copies, renames, new folders and deletes are shown as a plan; only the items you tick are executed, through WON's normal file-operation engine (deletes go to the Recycle Bin; `Ctrl+Z` undoes).
- Protected locations (drive roots, Windows, Program Files, ProgramData, your profile root and its main folders) and overwriting existing items are refused.
- **Skills** are extra instructions for the model: import a Claude `SKILL.md` (file or folder) or write your own, and switch them on/off. Only the text is used — scripts in a skill folder are never imported or run, and a skill cannot grant new tools or permissions.
- **Chat history** is kept on this PC only (`%LOCALAPPDATA%\WON\assistant-chats`, one JSON file per chat): your messages and the replies, plus the *names* of attached files — never attachment contents, tool results or keys. Reopening the panel resumes the latest chat; the clock button lists past chats to continue or delete, and *Delete all chats* wipes them. Switch saving off in the panel settings.
- A custom base URL (for example an in-house OpenAI-compatible server) must use https, or http on localhost.
- **Local (Ollama)** provider: no API key and nothing leaves your PC. Install [Ollama](https://ollama.com), pull a tool-capable model (for example `ollama pull qwen3.5:9b`), then pick *Local (Ollama)* in the model menu. Only `localhost` addresses are accepted for this provider. Small models plan less reliably — you still tick and approve every change.
- Check your organisation's policy first: with a cloud provider, names and paths leave the machine when this is on.

## PDF conversion

Select one or more files and use the context menu (*Convert to PDF*, *Merge into one PDF*) or the shortcuts `Ctrl+Shift+D` (convert) / `Ctrl+Shift+M` (merge; existing PDFs can be included). The new PDF is created **next to the original**; originals are never modified and existing files are never overwritten (`name (1).pdf`).

| Source | Engine |
|---|---|
| PNG, JPG, BMP, GIF, TIFF, WebP, HEIC → PDF (A4, EXIF rotation applied) | built in — [PDFsharp](https://github.com/empira/PDFsharp) 6.2.4 (MIT; pinned because 6.5.0+ is AGPL) with Windows' image codecs |
| PDF merge | built in — PDFsharp |
| DOC/DOCX/RTF, XLS/XLSX/CSV, PPT/PPTX | Microsoft Office when installed (its own PDF export, macros disabled, opened read-only), otherwise [LibreOffice](https://www.libreoffice.org) (`soffice --headless --convert-to pdf`, own profile folder) |
| HWP / HWPX | Hancom Office when installed, otherwise LibreOffice with the [H2Orestart](https://github.com/ebandal/H2Orestart) extension (GPL) |
| ODT/ODS/ODP/TXT | LibreOffice |

Nothing is uploaded anywhere: conversion runs on your PC. Office / Hancom / LibreOffice are **not bundled** — WON uses whatever is installed. Files from untrusted sources are opened by those programs just like double-clicking them.

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
