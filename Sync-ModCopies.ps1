#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('All','Project3','Steam','TapTap')][string]$Target = 'All',
    [string]$ProjectPath = 'C:\Users\CatStudio\Desktop\Project3',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Outer Space',
    [string]$TapTapPath = 'C:\TapTap\PC Games\242783',
    [switch]$BuildOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$targets = if ($Target -eq 'All') { @('Project3','Steam','TapTap') } else { @($Target) }
$artifactRoot = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
try { $syncLock = [IO.File]::Open((Join-Path $artifactRoot 'mod-copy-sync.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
catch { throw 'Another MOD-copy sync is running. Wait for it to finish.' }
$report = [ordered]@{ StartedAtUtc=[DateTimeOffset]::UtcNow.ToString('o'); SourceRoot=$root; Target=$Target; BuildOnly=[bool]$BuildOnly; Completed=$false; Installations=@(); RuntimeTested=$false }
try {
    if (-not $BuildOnly -and @($targets | Where-Object { $_ -ne 'Project3' }).Count -gt 0) {
        $executables = @($targets | Where-Object { $_ -ne 'Project3' } | ForEach-Object {
            $selectedGamePath = if ($_ -eq 'TapTap') { $TapTapPath } else { $GamePath }
            [IO.Path]::GetFullPath((Join-Path $selectedGamePath 'Outer Space.exe'))
        })
        foreach ($process in @(Get-Process -Name 'Outer Space' -ErrorAction SilentlyContinue)) {
            # A running elevated game can have no readable Path; do not bypass the install guard.
            if ([string]::IsNullOrWhiteSpace($process.Path) -or @($executables | Where-Object { [string]::Equals($process.Path,$_,[StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) { throw 'Close the selected game before syncing (including any Outer Space process with an unreadable path). No process was stopped.' }
        }
    }
    $development = & (Join-Path $root 'tools\Get-DevelopmentRevision.ps1') -Root $root
    $release = & (Join-Path $root 'tools\Get-ModVersion.ps1') -Root $root
    $report.SourceRevision = $development.SourceRevision
    $report.ModVersion = $release.ModVersion
    $report.AssemblyVersion = $release.AssemblyVersion
    # Build every selected target before installing any copy.
    foreach ($selected in $targets) {
        $selectedGamePath = if ($selected -eq 'TapTap') { $TapTapPath } else { $GamePath }
        & (Join-Path $root 'build.ps1') -ReferenceMode $selected -ProjectPath $ProjectPath -GamePath $selectedGamePath -NoDeploy
        if ((& (Join-Path $root 'tools\Get-DevelopmentRevision.ps1') -Root $root).SourceRevision -ne $development.SourceRevision) {
            throw 'Main repository changed during the sync. No further copies will be installed.'
        }
        $label = switch ($selected) { 'Steam' { 'steam-target' } 'TapTap' { 'taptap-target' } default { 'p3-development-reference' } }
        $buildInfoPath = Join-Path $artifactRoot ($label + '\FreeWorldMultiplayer\BUILD-INFO.json')
        $buildInfo = [IO.File]::ReadAllText($buildInfoPath) | ConvertFrom-Json
        if ($buildInfo.SourceRevision -ne $development.SourceRevision -or $buildInfo.ModVersion -ne $release.ModVersion -or $buildInfo.AssemblyVersion -ne $release.AssemblyVersion) {
            throw "The $selected package does not match the selected source revision and MOD release. No copies will be installed."
        }
    }
    if (-not $BuildOnly) {
        foreach ($selected in $targets) {
            $destination = switch ($selected) { 'Project3' { $ProjectPath } 'TapTap' { $TapTapPath } default { $GamePath } }
            $installed = & (Join-Path $root 'Install-Mod.ps1') -TargetType $selected -GamePath $destination -PassThru
            $report.Installations += $installed
        }
    }
    $report.Completed = $true
    Write-Host "MOD release: $($release.ModVersion); assembly: $($release.AssemblyVersion)"
    Write-Host "Source revision: $($development.SourceRevision)"
    Write-Host 'Copies are built only from this main repository. Restart/reload the target before gameplay acceptance.'
}
catch { $report.Error = $_.Exception.Message; throw }
finally {
    $report.FinishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $reportPath = Join-Path $artifactRoot 'last-copy-sync.json'
    [IO.File]::WriteAllText($reportPath,($report | ConvertTo-Json -Depth 7),[Text.UTF8Encoding]::new($false))
    $syncLock.Dispose()
}
