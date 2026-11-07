<#
  Looks for personal information and secrets before you publish or push.

    .\Scripts\privacy-check.ps1

  Checks every file that would be committed (or every file, outside git) for real-looking
  email addresses, user folders, private keys and access tokens, and checks the name and
  email git will put on your commits. Example values (example.com, C:\Users\me, 192.168.x.x)
  are fine and ignored. Runs on Windows PowerShell 5.1 and PowerShell 7 (also in CI on Linux).
#>
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

$inGit = $false
try { $inGit = (git rev-parse --is-inside-work-tree 2>$null) -eq 'true' } catch { }
if ($inGit) {
    $files = @(git ls-files --cached --others --exclude-standard)
} else {
    $files = Get-ChildItem -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](\.git|bin|obj|artifacts)[\\/]' } |
        ForEach-Object { Resolve-Path -Relative $_.FullName }
}
$files = $files | Where-Object { $_ -notmatch '\.(png|ico|zip|exe|pfx|dll)$' -and $_ -notmatch 'privacy-check\.ps1$' }

$checks = @(
    @{ What = 'Email addresses'; Find = '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}'; Allow = '@example\.(com|org)|me@x\.com|noreply@|users\.noreply\.github\.com' },
    @{ What = 'User folders';    Find = '(?i:[A-Z]:\\Users\\)[A-Za-z0-9._ -]+|/Users/[A-Za-z0-9._-]+|/home/[a-z0-9._-]+'; Allow = '\\Users\\me\\|\\Users\\Public|/Users/me/|/Users/Shared' },
    @{ What = 'Private keys';    Find = 'BEGIN [A-Z ]*PRIVATE KEY'; Allow = '^$' },
    @{ What = 'Access tokens';   Find = '(ghp|gho|github_pat|glpat|xox[abp])_[A-Za-z0-9_]{16,}|AKIA[0-9A-Z]{16}|sk-[A-Za-z0-9]{20,}'; Allow = '^$' }
)

$problems = $false
foreach ($check in $checks) {
    $hits = foreach ($f in $files) {
        if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { continue }
        Select-String -LiteralPath $f -Pattern $check.Find -CaseSensitive -AllMatches -ErrorAction SilentlyContinue |
            Where-Object { $_.Line -notmatch $check.Allow }
    }
    if ($hits) {
        Write-Host "[!] $($check.What):" -ForegroundColor Red
        $hits | Select-Object -First 20 | ForEach-Object { Write-Host "    $($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
        $problems = $true
    }
}

if ($inGit -and -not $env:CI) {
    $name = git config user.name
    $email = git config user.email
    Write-Host "Commits will be signed as: $name <$email>"
    if ($email -notlike '*@users.noreply.github.com') {
        Write-Host "! Your commit email isn't a GitHub no-reply address. See RELEASING.md > Publishing on GitHub." -ForegroundColor Yellow
        $problems = $true
    }
}

if ($problems) { exit 1 }
Write-Host "OK: Nothing personal found." -ForegroundColor Green
