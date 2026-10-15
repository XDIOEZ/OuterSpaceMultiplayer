#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GamePath,
    [ValidateSet('Project3', 'Steam')][string]$TargetType = 'Steam',
    [string]$PackagePath,
    [string]$HarmonyPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspace = [IO.Path]::GetFullPath($PSScriptRoot)
$GamePath = [IO.Path]::GetFullPath($GamePath).TrimEnd('\', '/')
if (-not $PackagePath) {
    $packageLabel = if ($TargetType -eq 'Project3') { 'p3-development-reference' } else { 'steam-target' }
    $PackagePath = Join-Path $workspace ('artifacts\' + $packageLabel + '\FreeWorldMultiplayer')
}
$PackagePath = [IO.Path]::GetFullPath($PackagePath).TrimEnd('\', '/')
$protectedProject = [IO.Path]::GetFullPath('C:\Users\CatStudio\Desktop\Project3').TrimEnd('\', '/')
if ($TargetType -eq 'Project3') {
    if (-not [string]::Equals($GamePath, $protectedProject, [StringComparison]::OrdinalIgnoreCase)) { throw 'Project3 mode installs only to the explicitly authorized Project3 MOD directory.' }
    $gameAssembly = Join-Path $GamePath 'Library\ScriptAssemblies\Assembly-CSharp.dll'
    $projectFile = Join-Path $GamePath 'Assembly-CSharp.csproj'
    if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) { throw 'Project3 generated project file is missing.' }
    [xml]$projectXml = Get-Content -LiteralPath $projectFile -Raw
    $apiReference = @($projectXml.SelectNodes("//*[local-name()='Reference']/*[local-name()='HintPath']") |
        Where-Object { $_.InnerText -like '*unity-4.8-api*mscorlib.dll' } | ForEach-Object InnerText)
    if ($apiReference.Count -ne 1) { throw 'Cannot uniquely locate the Project3 Unity runtime.' }
    $editorData = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent ([IO.Path]::GetFullPath($apiReference[0]))))
    $coreLibrary = Join-Path $editorData 'MonoBleedingEdge\lib\mono\unityjit-win32\mscorlib.dll'
    if (-not $HarmonyPath) { $HarmonyPath = Join-Path $GamePath 'mods\Mod_Harmony\v_common\Assemblies\0Harmony.dll' }
} else {
    if ($GamePath -eq $protectedProject -or $GamePath.StartsWith($protectedProject + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Use explicit Project3 mode for the authorized MOD-only installation.' }
    $gameExecutable = Join-Path $GamePath 'Outer Space.exe'
    if (-not (Test-Path -LiteralPath $gameExecutable -PathType Leaf)) { throw "Game executable missing: $gameExecutable" }
    $gameAssembly = Join-Path $GamePath 'Outer Space_Data\Managed\Assembly-CSharp.dll'
    $coreLibrary = Join-Path $GamePath 'Outer Space_Data\Managed\mscorlib.dll'
    foreach ($process in @(Get-Process -Name 'Outer Space' -ErrorAction SilentlyContinue)) {
        if ([string]::Equals($process.Path, $gameExecutable, [StringComparison]::OrdinalIgnoreCase)) { throw 'Close the target game before installation. No process was stopped.' }
    }
}
foreach ($required in @($gameAssembly, $coreLibrary, (Join-Path $PackagePath 'BUILD-INFO.json'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required file missing: $required" }
}
$info = Get-Content -LiteralPath (Join-Path $PackagePath 'BUILD-INFO.json') -Raw | ConvertFrom-Json
if (-not $info.Compiled -or $info.ReferenceMode -ne $TargetType) {
    throw 'Installation requires a successful build against the selected target APIs. Project3 and Steam packages cannot be interchanged.'
}
$gameReference = @($info.References | Where-Object Assembly -EQ 'Assembly-CSharp')
$harmonyReference = @($info.References | Where-Object Assembly -EQ '0Harmony')
if ($gameReference.Count -ne 1 -or $harmonyReference.Count -ne 1) { throw 'Build reference identities are missing or ambiguous.' }
if ((Get-FileHash -LiteralPath $gameAssembly -Algorithm SHA256).Hash -ne $gameReference[0].SHA256) {
    throw 'The target game assembly differs from the build reference. Rebuild against this game directory before installation.'
}
if ([string]::IsNullOrWhiteSpace($HarmonyPath)) { $HarmonyPath = $harmonyReference[0].Path }
if ((Get-FileHash -LiteralPath $HarmonyPath -Algorithm SHA256).Hash -ne $harmonyReference[0].SHA256) {
    throw 'The shared Harmony provider differs from the build reference. No shared dependency was changed.'
}
$compatibility = & (Join-Path $workspace 'tools\Read-RuntimeCompatibility.ps1') -GamePath $GamePath -HarmonyPath $HarmonyPath -CoreLibraryPath $coreLibrary -GameAssemblyPath $gameAssembly
if (-not $compatibility.KnownDirectReferencesCompatible) {
    throw ('Target runtime is missing Harmony APIs: ' + ($compatibility.MissingDirectReferences -join ', ') + '. No installation files were written.')
}
$manifest = Join-Path $PackagePath 'Manifest\manifest.xml'
[xml]$manifestXml = Get-Content -LiteralPath $manifest -Raw
if ($manifestXml.ModXmlManifest.packageId -ne 'com.catstudio.freeworldmultiplayer') { throw 'Unexpected MOD package identity.' }
$files = @('README.md', 'DEPENDENCIES.md', 'Launch-Direct.ps1', 'BUILD-INFO.json',
    'v_common\Assemblies\FreeWorldMultiplayer.dll', 'v_common\Assemblies\FreeWorldMultiplayer.pdb',
    'Manifest\icon.png', 'Manifest\preview.png', 'Manifest\manifest.xml')
foreach ($relative in $files) {
    if (-not (Test-Path -LiteralPath (Join-Path $PackagePath $relative) -PathType Leaf)) { throw "Package file missing: $relative" }
}
$modsRoot = Join-Path $GamePath 'mods'
$destinationRoot = Join-Path $modsRoot 'FreeWorldMultiplayer'
# Refuse redirected installation paths, including a mods junction into a source project.
$inspect = $destinationRoot
while ($inspect) {
    if (Test-Path -LiteralPath $inspect) {
        if ((Get-Item -LiteralPath $inspect -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Redirected installation path is not supported: $inspect"
        }
    }
    $parent = Split-Path -Parent $inspect
    if ($parent -eq $inspect) { break }
    $inspect = $parent
}
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$receiptRoot = Join-Path $workspace ('artifacts\installations\' + $runId)
$backupRoot = Join-Path $receiptRoot 'previous-files'
New-Item -ItemType Directory -Path $receiptRoot -Force | Out-Null
$changed = [Collections.Generic.List[object]]::new()
try {
    foreach ($relative in $files) {
        $source = Join-Path $PackagePath $relative
        $destination = [IO.Path]::GetFullPath((Join-Path $destinationRoot $relative))
        if (-not $destination.StartsWith($destinationRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Installation file escaped the MOD directory.' }
        $existed = Test-Path -LiteralPath $destination -PathType Leaf
        $backup = Join-Path $backupRoot $relative
        if ($existed) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
            Copy-Item -LiteralPath $destination -Destination $backup
        }
        $changed.Add([pscustomobject]@{ Relative=$relative; Destination=$destination; Existed=$existed; Backup=$backup })
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) { throw "Installed file differs: $relative" }
    }
} catch {
    $failure = $_
    foreach ($entry in @($changed.ToArray()) | Sort-Object Relative -Descending) {
        if ($entry.Existed) { Copy-Item -LiteralPath $entry.Backup -Destination $entry.Destination -Force }
        elseif (Test-Path -LiteralPath $entry.Destination -PathType Leaf) { Remove-Item -LiteralPath $entry.Destination }
    }
    throw $failure
}
$receipt = [ordered]@{ InstalledAtUtc=[DateTimeOffset]::UtcNow.ToString('o'); TargetType=$TargetType; GamePath=$GamePath; PackagePath=$PackagePath;
    Destination=$destinationRoot; Files=@($changed.ToArray() | ForEach-Object { [ordered]@{ Path=$_.Relative; SHA256=(Get-FileHash -LiteralPath $_.Destination -Algorithm SHA256).Hash } });
    TargetRuntimeMetadataInspection=$compatibility;
    ActivationChanged=$false; RuntimeTested=$false; Project3SourceOrSettingsModified=$false; OnlyAuthorizedModDirectoryWritten=$true }
$receiptPath = Join-Path $receiptRoot 'receipt.json'
[IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 7), [Text.UTF8Encoding]::new($false))
Write-Host "Installed only this MOD: $destinationRoot"
Write-Host "Receipt: $receiptPath"
Write-Host 'Player data, other MODs, game assemblies and activation settings were preserved. Loading and gameplay acceptance remain unrun.'
