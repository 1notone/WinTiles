# Contributing to WinTiles

Thanks for helping. Bug reports, ideas and pull requests are all welcome.

## Reporting a bug

Open an issue with the **Bug report** template. The most useful details are:

- the WinTiles version (bottom of the Settings window) and your Windows version and edition (x64 or Arm)
- what you did, what you expected and what happened instead
- anything relevant from `%APPDATA%\WinTiles\wintiles.log`
- for a service tile, which service and version it is — but **never paste API keys, tokens, passwords or your public IP address**

Security problems go through [SECURITY.md](SECURITY.md) instead, not a public issue.

## Building

On Windows 11 with the [.NET 10 SDK](https://dotnet.microsoft.com/download):

```powershell
dotnet test                              # unit tests (they also run on macOS and Linux)
dotnet run --project src\WinTiles        # run WinTiles
.\build.ps1                              # installers and portable zips in artifacts\release (needs Inno Setup 6)
```

Or double-click **Build Installer.cmd**, which installs what's missing first.

## Pull requests

- Keep it light: no heavyweight dependencies, and nothing that polls while Start can't be seen.
- Logic that doesn't need Windows belongs in `src/WinTiles.Core`, with tests in `tests/WinTiles.Core.Tests`. Service parsers are pure functions so they can be tested with sample JSON — please use made-up data (`example.com`, `192.168.1.10`), not output copied from your own servers.
- `layout.json` is shared with [MacTiles](https://github.com/1notone/MacTiles); keep new keys compatible.
- Add a line to the top of [CHANGELOG.md](CHANGELOG.md) under an *Unreleased* heading.
- Run `.\Scripts\privacy-check.ps1` before pushing.

By contributing, you agree that your contribution is licensed under the [MIT License](LICENSE).
