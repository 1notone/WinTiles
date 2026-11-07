# Releasing WinTiles

A release is an installer for each kind of PC (`…-x64-setup.exe`, `…-arm64-setup.exe`), portable zips, and checksums, published on GitHub.

## Publishing on GitHub (first time)

You can do this from your Mac (where git and the GitHub CLI are already set up for MacTiles) or from a PC (`winget install Git.Git GitHub.cli`, then `gh auth login`). Nothing here needs your real name or personal email. GitHub builds the Windows installers itself, so you don't need a PC to publish a release.

**1. Create an empty repository.** On GitHub: **+ → New repository**, name it `WinTiles`, make it **Public**, and leave *Add a README*, *.gitignore* and *license* unticked.

**2. Commit with your private address.** In the WinTiles folder (Terminal on a Mac, PowerShell on a PC):

```sh
git init -b main
git config user.name  "your-github-username"
git config user.email "$(gh api user --jq '"\(.id)+\(.login)@users.noreply.github.com"')"
git add .
git commit -m "WinTiles 1.2.0"
git log -1 --format='%an <%ae>'      # should show your user name and the no-reply address
```

On a PC, run `.\Scripts\privacy-check.ps1` before committing; it looks for real-looking email addresses, user folders, keys and tokens. (It also runs on every push in GitHub Actions.)

**3. Push and release:**

```sh
git remote add origin https://github.com/your-github-username/WinTiles.git
git push -u origin main
git tag v1.2.0
git push origin v1.2.0
```

The *Release* workflow tests, builds both installers and the portable zips on a GitHub Windows machine, and publishes them on the **Releases** page with the CHANGELOG section as the notes.

**4. Repository settings** (on GitHub, under **Settings**):
- **General**: add a description (*Windows 8-style Start screen and Live Tiles for Windows 11*) and topics (`windows`, `windows-11`, `launcher`, `start-menu`, `wpf`, `live-tiles`, `homelab`). Under *Pull Requests*, tick **Automatically delete head branches**.
- **Code security**: turn on **Private vulnerability reporting** (used by [SECURITY.md](SECURITY.md)) and **Dependabot alerts**.

### Signing and your name

A code-signing certificate — Azure Trusted Signing or one from a certificate authority — is issued to a verified person or organization, and Windows shows that name to anyone who checks the installer. To stay anonymous, publish unsigned releases (people click **More info → Run anyway** once) or sign as an organization.

## Signing (recommended, optional)

Unsigned installers work, but Windows SmartScreen warns people the first few hundred times a new file is downloaded ("Windows protected your PC" → **More info → Run anyway**). Signing removes or shortens that warning. Options:

- **Azure Trusted Signing** — the cheapest current option for individuals and small organizations (monthly subscription; identity verification required).
- **An OV or EV code-signing certificate** from a certificate authority, exported as a `.pfx`.

`build.ps1` signs `WinTiles.exe` and both installers when given a `.pfx`:

```powershell
.\build.ps1 -CertPath C:\keys\wintiles.pfx -CertPassword (Read-Host -AsSecureString | ConvertFrom-SecureString -AsPlainText)
```

(It uses `signtool.exe` from the Windows SDK: `winget install Microsoft.WindowsSDK.10.0.26100`.)

## Making a release on your PC

1. Add a `## [x.y.z] - date` section to `CHANGELOG.md`.
2. Set `<Version>` in `Directory.Build.props`.
3. Run `.\build.ps1` (add `-CertPath …` to sign).

| Step | Result |
|---|---|
| Unit tests | stops if anything fails |
| Self-contained publish, x64 and arm64 | `artifacts\publish\<arch>\WinTiles.exe` (no .NET install needed) |
| Installers (Inno Setup) | `artifacts\release\WinTiles-<v>-<arch>-setup.exe` |
| Portable zips | `artifacts\release\WinTiles-<v>-<arch>-portable.zip` |
| Checksums | `artifacts\release\SHA256SUMS.txt` |

## Releasing from GitHub Actions

Push a version tag; `.github/workflows/release.yml` builds, signs (if configured) and publishes a GitHub release, using that version's CHANGELOG section as the notes:

```sh
git tag v1.0.0 && git push origin v1.0.0
```

Running the workflow by hand (*Actions → Release → Run workflow*) creates a **draft** release.

### Repository secrets (optional — for signing)

| Secret | What it is |
|---|---|
| `WINDOWS_CERT_PFX` | Your code-signing `.pfx`, base64-encoded: `[Convert]::ToBase64String([IO.File]::ReadAllBytes('wintiles.pfx')) \| Set-Clipboard` |
| `WINDOWS_CERT_PASSWORD` | The `.pfx` password |

## What the installer does

- Installs per user to `%LOCALAPPDATA%\Programs\WinTiles` — no administrator prompt (it can still install for all users if run as admin).
- Refuses to install on the wrong CPU type or Windows versions older than 10 (2004).
- Closes a running WinTiles before updating it. Tiles and settings in `%APPDATA%\WinTiles` are kept.
- Adds a Start menu shortcut, an optional desktop shortcut, and (ticked by default) "Start WinTiles when I sign in".
- Opens WinTiles when it finishes.
- Removes an installed **Tessera** (WinTiles' name before 1.2.0) first; WinTiles then moves its tiles, settings, saved keys and sign-in startup across.
- The uninstaller stops WinTiles, removes the sign-in entry, and asks whether to delete your tiles, settings and saved service keys.
