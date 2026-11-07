<#
.SYNOPSIS
  Builds WinTiles: runs the tests, publishes self-contained builds and makes the installers.

.EXAMPLE
  .\build.ps1                       # tests + x64 and arm64 installers in artifacts\release
  .\build.ps1 -Arch x64             # just one architecture
  .\build.ps1 -Version 1.1.0
  .\build.ps1 -CertPath code-signing.pfx -CertPassword ****   # signed build

.NOTES
  Needs the .NET 10 SDK and Inno Setup 6:
    winget install Microsoft.DotNet.SDK.10
    winget install JRSoftware.InnoSetup
#>
param(
    [string]$Version,
    [ValidateSet('x64', 'arm64')][string[]]$Arch = @('x64', 'arm64'),
    [switch]$SkipTests,
    [string]$CertPath = $env:WINTILES_CERT_PATH,
    [string]$CertPassword = $env:WINTILES_CERT_PASSWORD
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Step($text) { Write-Host "`n> $text" -ForegroundColor Cyan }

if (-not $Version) {
    $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Select-Object -First 1
}
$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+') { throw "'$Version' isn't a version like 1.0.0" }
$NumericVersion = ($Version -split '[-+]')[0]      # file versions must be plain numbers

$artifacts = Join-Path $PSScriptRoot 'artifacts'
$release = Join-Path $artifacts 'release'
Remove-Item $release -Recurse -Force -ErrorAction SilentlyContinue
New-Item $release -ItemType Directory -Force | Out-Null

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK isn't installed. Run: winget install Microsoft.DotNet.SDK.10"
}

# ---------- signing (optional) ----------
$signtool = $null
if ($CertPath) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) { throw "signtool.exe not found. Install the Windows SDK, or build without -CertPath." }
}
function Sign($file) {
    if (-not $signtool) { return }
    & $signtool sign /f $CertPath /p $CertPassword /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /d "WinTiles" $file
    if ($LASTEXITCODE) { throw "Signing $file failed" }
}

# ---------- tests ----------
if (-not $SkipTests) {
    Step "Running tests"
    dotnet test tests\WinTiles.Core.Tests\WinTiles.Core.Tests.csproj -c Release --nologo
    if ($LASTEXITCODE) { throw "Tests failed" }
}

# ---------- Inno Setup ----------
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Write-Warning "Inno Setup 6 not found (winget install JRSoftware.InnoSetup). Only portable zips will be made." }

foreach ($a in $Arch) {
    Step "Publishing WinTiles $Version for $a"
    $out = Join-Path $artifacts "publish\$a"
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish src\WinTiles\WinTiles.csproj -c Release -r "win-$a" -p:Version=$Version -o $out --nologo
    if ($LASTEXITCODE) { throw "Publish for $a failed" }
    Get-ChildItem $out -Filter *.pdb | Remove-Item
    Sign (Join-Path $out 'WinTiles.exe')

    Step "Portable zip ($a)"
    Compress-Archive -Path "$out\*" -DestinationPath (Join-Path $release "WinTiles-$Version-$a-portable.zip") -Force

    if ($iscc) {
        Step "Installer ($a)"
        & $iscc /Q "/DAppVersion=$Version" "/DNumericVersion=$NumericVersion" "/DArch=$a" "/DSourceDir=$out" "/DOutputDir=$release" installer\WinTiles.iss
        if ($LASTEXITCODE) { throw "Inno Setup failed for $a" }
        Sign (Join-Path $release "WinTiles-$Version-$a-setup.exe")
    }
}

Step "Checksums"
Get-ChildItem $release -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
} | Set-Content (Join-Path $release 'SHA256SUMS.txt')

Step "Done"
Get-ChildItem $release | Format-Table Name, @{ n = 'Size'; e = { '{0:N1} MB' -f ($_.Length / 1MB) } } -AutoSize
if (-not $CertPath) {
    Write-Host "Unsigned build: Windows SmartScreen will warn on other PCs ('More info' > 'Run anyway'). See RELEASING.md." -ForegroundColor Yellow
}
