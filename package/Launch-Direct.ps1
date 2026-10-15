#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Outer Space',
    [switch]$PrepareOnly
)

# Development/direct launch uses Valve's documented working-directory AppID convention.
# It never edits the game directory, replaces a DLL, installs a MOD or changes the activation list.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$launcherRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$GamePath = [IO.Path]::GetFullPath($GamePath)
$executable = Join-Path $GamePath 'Outer Space.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Game executable missing: $executable" }
if (Get-Process -Name 'steam' -ErrorAction SilentlyContinue) { throw 'For a no-Steam acceptance run, exit Steam yourself first. This launcher does not terminate any processes.' }
$mods = Join-Path $GamePath 'mods'
$localHarmony = @()
if (Test-Path -LiteralPath $mods -PathType Container) {
    $localHarmony = @(Get-ChildItem -LiteralPath $mods -Directory | ForEach-Object {
        $manifest = Join-Path $_.FullName 'Manifest\manifest.xml'
        if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { return }
        [xml]$data = [IO.File]::ReadAllText($manifest)
        if ($data.ModXmlManifest.packageId -ne 'com.harmony') { return }
        $provider = Join-Path $_.FullName 'v_common\Assemblies\0Harmony.dll'
        if (Test-Path -LiteralPath $provider -PathType Leaf) { $provider }
    })
}
if ($localHarmony.Count -ne 1) {
    throw "No unique local com.harmony provider was found under $mods. Without Steam, the original loader cannot enumerate Workshop providers. Install/enable the legitimate Harmony provider as a local MOD yourself; this launcher does not copy or replace shared dependencies."
}
$multiplayer = Join-Path $mods 'FreeWorldMultiplayer\Manifest\manifest.xml'
if (-not (Test-Path -LiteralPath $multiplayer -PathType Leaf)) { throw "Install/enable this MOD in the native local MOD manager before direct launch: $multiplayer" }
$workingRoot = [IO.Path]::GetFullPath((Join-Path $launcherRoot 'DirectLaunch'))
$within = $launcherRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $workingRoot.StartsWith($within, [StringComparison]::OrdinalIgnoreCase)) { throw 'Direct launch directory escaped the MOD folder.' }
New-Item -ItemType Directory -Force -Path $workingRoot | Out-Null
$appIdFile = Join-Path $workingRoot 'steam_appid.txt'
if (Test-Path -LiteralPath $appIdFile -PathType Leaf) {
    if ([IO.File]::ReadAllText($appIdFile).Trim() -ne '3080070') { throw "Unexpected existing AppID file; it was not overwritten: $appIdFile" }
} else { [IO.File]::WriteAllText($appIdFile, '3080070', [Text.Encoding]::ASCII) }
$logPath = Join-Path $workingRoot ('Player-direct-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '.log')
Write-Host "Original executable: $executable"
Write-Host "MOD-owned working directory: $workingRoot"
Write-Host "Existing local Harmony: $($localHarmony[0])"
Write-Host 'The original SteamManager may report SteamAPI_Init failed and continue without Steam. Real loading and connection acceptance is still required.'
if ($PrepareOnly) { Write-Host 'Prepared only; game was not launched.'; return }
$process = Start-Process -FilePath $executable -WorkingDirectory $workingRoot -ArgumentList @('-fwm-direct', '-logFile', ('"' + $logPath + '"')) -WindowStyle Normal -PassThru
Write-Host "Game process started (PID $($process.Id)); this is a launch request, not confirmation that the MOD loaded. Log: $logPath"
