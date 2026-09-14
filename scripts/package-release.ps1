# PowerShell script to package Nebula Release locally
[CmdletBinding()]
param (
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot\..").Path
Set-Location $root

Write-Host "=== Packaging Nebula ($Configuration) ===" -ForegroundColor Cyan

# 1. Parse Version
$baseVer = (Get-Content (Join-Path $root "version.json") -Raw | ConvertFrom-Json).version
$pluginInfoRaw = Get-Content (Join-Path $root "NebulaPatcher\PluginInfo.cs") -Raw
$forkSuffix = if ($pluginInfoRaw -match 'FORK_SUFFIX\s*=\s*"([^"]*)"') { $matches[1] } else { "" }
$fullVersion = "$baseVer$forkSuffix"

Write-Host "Base Version:    $baseVer" -ForegroundColor Gray
Write-Host "Fork Suffix:     $forkSuffix" -ForegroundColor Gray
Write-Host "Full Version:    $fullVersion" -ForegroundColor Green

# 2. Build Solution in Release mode
Write-Host "`nBuilding solution with /p:PublicRelease=true ..." -ForegroundColor Cyan
New-Item -Path (Join-Path $root ".remoteBuild") -ItemType File -Force | Out-Null
dotnet build (Join-Path $root "Nebula.sln") -c $Configuration /p:PublicRelease=true

# 3. Generate BODY.md from CHANGELOG.md
Write-Host "`nGenerating BODY.md from CHANGELOG.md ..." -ForegroundColor Cyan
$changelog = [System.IO.File]::ReadAllText((Join-Path $root "CHANGELOG.md"), [System.Text.Encoding]::UTF8)
$versionRegExp = "\b[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:\.[0-9]+)?(?:-[a-zA-Z0-9.]+)?(?=:)\b"
$allMatches = [regex]::Matches($changelog, $versionRegExp)

if ($allMatches.Count -eq 0) {
    throw "No versions found in CHANGELOG.md"
}

$latestChangelogVer = $allMatches[0].Value
if ($latestChangelogVer -ne $fullVersion) {
    throw "CHANGELOG.md latest version ($latestChangelogVer) does not match expected version ($fullVersion)!"
}

$start = $allMatches[0].Index + $allMatches[0].Length + 1
$end = if ($allMatches.Count -gt 1) { $allMatches[1].Index } else { $changelog.Length }
$body = $changelog.Substring($start, $end - $start).Trim()

$distRelease = Join-Path $root "dist\release"
if (!(Test-Path $distRelease)) {
    New-Item -Path $distRelease -ItemType Directory -Force | Out-Null
}

$bodyFile = Join-Path $distRelease "BODY.md"
$bodyContent = "# Alpha Version $fullVersion`n`n### Changes`n$body"
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($bodyFile, $bodyContent, $utf8NoBom)
Write-Host "BODY.md written to: $bodyFile" -ForegroundColor Green

# 4. Create Release Zip (Nebula_<version>.zip)
Write-Host "`nCreating Release Archive: Nebula_$fullVersion.zip ..." -ForegroundColor Cyan
$archiveName = "Nebula_$fullVersion.zip"
$archivePath = Join-Path $distRelease $archiveName
if (Test-Path $archivePath) {
    Remove-Item $archivePath -Force
}

$tmpDir = Join-Path $root "dist\tmp_archive"
if (Test-Path $tmpDir) {
    Remove-Item $tmpDir -Recurse -Force
}
New-Item -Path $tmpDir -ItemType Directory -Force | Out-Null

$modSource = Join-Path $distRelease "nebula-NebulaMultiplayerMod"
$apiSource = Join-Path $distRelease "nebula-NebulaMultiplayerModApi"

if (Test-Path $modSource) {
    Copy-Item -Path $modSource -Destination (Join-Path $tmpDir "nebula-NebulaMultiplayerMod") -Recurse
}
if (Test-Path $apiSource) {
    Copy-Item -Path $apiSource -Destination (Join-Path $tmpDir "nebula-NebulaMultiplayerModApi") -Recurse
}

# Add README and Licenses
Copy-Item (Join-Path $root "README.md") -Destination $tmpDir -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root "LICENSE") -Destination $tmpDir -ErrorAction SilentlyContinue

Compress-Archive -Path "$tmpDir\*" -DestinationPath $archivePath -Force
Remove-Item $tmpDir -Recurse -Force

$zipItem = Get-Item $archivePath
$zipSizeMb = [math]::Round($zipItem.Length / 1MB, 2)
Write-Host "`n=== Release Package Successfully Created ===" -ForegroundColor Green
Write-Host "Archive:  $archivePath ($zipSizeMb MB)" -ForegroundColor Yellow
Write-Host "BODY.md:  $bodyFile" -ForegroundColor Yellow
