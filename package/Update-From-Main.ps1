#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$sourceRoot = 'C:\Users\CatStudio\Desktop\FreeWorldMods\FreeWorldMultiplayer'
$info = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'BUILD-INFO.json') -Raw | ConvertFrom-Json
if ($info.ReferenceMode -notin @('Project3','Steam','TapTap')) { throw 'Unknown MOD copy target.' }
$sync = Join-Path $sourceRoot 'Sync-ModCopies.ps1'
if (-not (Test-Path -LiteralPath $sync -PathType Leaf)) { throw "Main development repository is unavailable: $sourceRoot" }
$gameRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$targetOptions = switch ($info.ReferenceMode) {
    'Project3' { @{ProjectPath=$gameRoot} }
    'TapTap' { @{TapTapPath=$gameRoot} }
    'Steam' { @{GamePath=$gameRoot} }
}
& $sync -Target $info.ReferenceMode @targetOptions
