<#
.SYNOPSIS
    Builds Revit Text QC for one or more Revit versions.

.EXAMPLE
    ./build.ps1                      # build all versions into artifacts\<version>\
    ./build.ps1 -Install             # build and copy into %APPDATA%\Autodesk\Revit\Addins\<version>\
    ./build.ps1 -Package -Version 0.2.0   # build and zip into dist\ (what the release workflow runs)
#>
param(
    [string[]]$RevitVersions = @('2024', '2025', '2026', '2027'),
    [string]$Version = '',
    [switch]$Install,
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\RevitTextQC\RevitTextQC.csproj'

foreach ($revit in $RevitVersions) {
    Write-Host "== Revit $revit ==" -ForegroundColor Cyan
    $artifacts = Join-Path $root "artifacts\$revit"
    if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }

    $buildArgs = @('build', $project, '-c', 'Release', "-p:RevitVersion=$revit", '--nologo', '-v', 'q')
    if ($Version) { $buildArgs += "-p:Version=$Version" }
    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "Build failed for Revit $revit" }

    if ($Package) {
        $dist = Join-Path $root 'dist'
        New-Item -ItemType Directory -Force $dist | Out-Null
        $zip = Join-Path $dist "RevitTextQC-$revit.zip"
        if (Test-Path $zip) { Remove-Item $zip -Force }
        # The zip mirrors the Addins folder: RevitTextQC.addin + RevitTextQC\ at the top level.
        Compress-Archive -Path (Join-Path $artifacts '*') -DestinationPath $zip
        Write-Host "   packaged $zip"
    }

    if ($Install) {
        $addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$revit"
        if (-not (Test-Path $addins)) {
            Write-Host "   Revit $revit add-ins folder not found, skipped" -ForegroundColor Yellow
            continue
        }
        try {
            Copy-Item (Join-Path $artifacts '*') $addins -Recurse -Force
            Write-Host "   installed to $addins"
        }
        catch {
            Write-Host "   could not install (close Revit $revit first): $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}
