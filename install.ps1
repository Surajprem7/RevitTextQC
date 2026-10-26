<#
.SYNOPSIS
    Installs the latest release of Revit Text QC for every Revit version found on this PC.
    After this first install the add-in keeps itself up to date from GitHub releases.

    Nothing else needs to be installed first: the release contains the add-in, its spell-check library and the
    dictionaries, and Revit brings the .NET it runs on. Administrator rights are not needed.

.EXAMPLE
    ./install.ps1
    ./install.ps1 -RevitVersions 2025,2026
#>
param(
    [string[]]$RevitVersions,
    [string]$Repository = 'Surajprem7/RevitTextQC'
)

$ErrorActionPreference = 'Stop'
$supported = '2024', '2025', '2026', '2027'

if ($PSVersionTable.PSVersion.Major -lt 5) { throw 'Windows PowerShell 5 or newer is required (it is part of Windows 10 and 11).' }
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

# The files cannot be replaced while Revit has them open.
if (Get-Process -Name Revit -ErrorAction SilentlyContinue) {
    throw 'Revit is open. Close every Revit window, then run this again.'
}

$addinsRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins'

if (-not $RevitVersions) {
    # Versions that have an add-ins folder, plus versions that are installed but were never started
    # (Revit creates the add-ins folder on first start).
    $found = @()
    $found += Get-ChildItem $addinsRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }
    $found += Get-ChildItem (Join-Path $env:ProgramFiles 'Autodesk') -Directory -Filter 'Revit 20*' -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName 'Revit.exe') } | ForEach-Object { $_.Name -replace '^Revit ', '' }
    $RevitVersions = $found | Where-Object { $supported -contains $_ } | Sort-Object -Unique
}
if (-not $RevitVersions) {
    throw "No Revit 2024, 2025, 2026 or 2027 was found on this PC (looked in $addinsRoot and in Program Files)."
}

try {
    $release = Invoke-RestMethod "https://api.github.com/repos/$Repository/releases/latest" -Headers @{ 'User-Agent' = 'RevitTextQC-installer' }
}
catch {
    throw "Could not reach GitHub to find the latest release. Check the internet connection (a company firewall may block github.com). $($_.Exception.Message)"
}
Write-Host "Installing Revit Text QC $($release.tag_name)"

$installed = 0
foreach ($revit in $RevitVersions) {
    $asset = $release.assets | Where-Object { $_.name -eq "RevitTextQC-$revit.zip" }
    if (-not $asset) {
        Write-Host "  Revit ${revit}: no build in this release, skipped" -ForegroundColor Yellow
        continue
    }

    $zip = Join-Path $env:TEMP $asset.name
    Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing

    if ($asset.digest -like 'sha256:*') {
        $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
        if ($actual -ne $asset.digest.Substring(7)) { Remove-Item $zip -Force; throw "Checksum mismatch for $($asset.name)." }
    }

    $target = Join-Path $addinsRoot $revit
    try {
        New-Item -ItemType Directory -Force $target | Out-Null
        Expand-Archive $zip -DestinationPath $target -Force
        # Files downloaded from the internet are marked as blocked; Revit would refuse to load them.
        Get-ChildItem (Join-Path $target 'RevitTextQC') -Recurse -File | Unblock-File
        Unblock-File (Join-Path $target 'RevitTextQC.addin')
        Write-Host "  Revit ${revit}: installed to $target"
        $installed++
    }
    catch {
        Write-Host "  Revit ${revit}: could not install - $($_.Exception.Message)" -ForegroundColor Yellow
    }
    finally {
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
    }
}

if ($installed -gt 0) {
    Write-Host ''
    Write-Host 'Done. Start Revit and look for the "Text QC" tab.'
    Write-Host 'The first time, Revit asks about an unsigned add-in: choose "Always Load".'
}
else {
    Write-Host 'Nothing was installed.' -ForegroundColor Yellow
    exit 1
}
