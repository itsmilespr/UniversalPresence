# Contributing

Thanks for taking an interest in Universal Presence.

## Building

```bash
dotnet build UniversalPresence.csproj -c Release
```

Self-contained single-file executable:

```bash
dotnet publish UniversalPresence.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o publish
```

Requires the .NET 8 SDK on Windows.

## Reporting bugs

Use the bug report template. Please include:

- Version (tray menu → Status, or the file version of `UniversalPresence.exe`).
- Windows version and player used.
- The relevant lines from `%LOCALAPPDATA%\UniversalPresence\logs\presence.log`.
- Steps to reproduce.

## Code notes

- The project deliberately has **no NuGet dependencies**. The Discord IPC protocol is
  implemented by hand. Please do not add packages without discussing it first.
- Settings live in `%APPDATA%\UniversalPresence\config.json`; keys are documented in
  `TECNICO.md`.
- Comments in the source are a mix of Spanish and English today; new code should use
  English for identifiers and code comments, Spanish for user-facing strings.

## Pull requests

- Keep changes focused. One concern per PR.
- Update `CHANGELOG.md` under "Unreleased" when you change user-visible behavior.
- Run `dotnet build` before submitting; the CI build must stay green.
