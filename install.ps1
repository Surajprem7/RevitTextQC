<#
.SYNOPSIS
    Installs the latest release of Revit Text QC for every Revit version found on this PC.
    After this first install the add-in keeps itself up to date from GitHub releases.

.EXAMPLE
    ./install.ps1
    ./install.ps1 -RevitVersions 2025,2026
#>
param(
    [string[]]$RevitVersions,
    [string]$Repository = 'Surajprem7/RevitTextQC'
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$addinsRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins'
if (-not $RevitVersions) {
    $RevitVersions = Get-ChildItem $addinsRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^20\d\d$' } | ForEach-Object { $_.Name }
}
if (-not $RevitVersions) { throw "No Revit add-ins folder found under $addinsRoot." }

$release = Invoke-RestMethod "https://api.github.com/repos/$Repository/releases/latest" -Headers @{ 'User-Agent' = 'RevitTextQC-installer' }
Write-Host "Installing Revit Text QC $($release.tag_name)"

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
        Expand-Archive $zip -DestinationPath $target -Force
        Get-ChildItem (Join-Path $target 'RevitTextQC') -Recurse -File | Unblock-File
        Write-Host "  Revit ${revit}: installed to $target"
    }
    catch {
        Write-Host "  Revit ${revit}: could not install - close Revit $revit and run again. $($_.Exception.Message)" -ForegroundColor Yellow
    }
    finally {
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
    }
}

Write-Host 'Done. Start Revit and look for the "Text QC" tab.'
