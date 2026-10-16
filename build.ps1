#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Project3', 'Steam', 'TapTap')][string]$ReferenceMode = 'Project3',
    [string]$ProjectPath = 'C:\Users\CatStudio\Desktop\Project3',
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Outer Space',
    [string]$HarmonyPath,
    [switch]$NoDeploy = $true
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($ReferenceMode -eq 'TapTap' -and -not $PSBoundParameters.ContainsKey('GamePath')) {
    $GamePath = 'C:\TapTap\PC Games\242783'
}
$modRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$ProjectPath = [IO.Path]::GetFullPath($ProjectPath)
$GamePath = [IO.Path]::GetFullPath($GamePath)
if (-not $NoDeploy) { throw 'This build is package-only. Deployment is deliberately separate; no game or Project3 files will be changed.' }
$development = & (Join-Path $modRoot 'tools\Get-DevelopmentRevision.ps1') -Root $modRoot
$release = & (Join-Path $modRoot 'tools\Get-ModVersion.ps1') -Root $modRoot
function Require-File([string]$Path) { if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file missing: $Path" } }
function Read-Xml([string]$Path) { [xml]([IO.File]::ReadAllText($Path)) }

# The project file is read solely to locate the installed Unity reference APIs and package assemblies.
$projectFile = Join-Path $ProjectPath 'Assembly-CSharp.csproj'
Require-File $projectFile
$projectXml = Read-Xml $projectFile
$hintPaths = @($projectXml.SelectNodes("//*[local-name()='Reference']/*[local-name()='HintPath']") | ForEach-Object {
    if ([IO.Path]::IsPathRooted($_.InnerText)) { [IO.Path]::GetFullPath($_.InnerText) }
    else { [IO.Path]::GetFullPath((Join-Path $ProjectPath $_.InnerText)) }
})
$mscorlib = $hintPaths | Where-Object { [IO.Path]::GetFileName($_) -eq 'mscorlib.dll' } | Select-Object -First 1
if (-not $mscorlib) { throw 'Unity .NET 4.8 API references are absent from Assembly-CSharp.csproj.' }
$apiRoot = Split-Path -Parent $mscorlib
if ($apiRoot -notmatch 'unity-4\.8-api') { throw "Expected Unity net4.8 API references, found: $apiRoot" }
$references = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
Get-ChildItem -LiteralPath $apiRoot -File -Filter '*.dll' | ForEach-Object { [void]$references.Add($_.FullName) }
$facades = Join-Path $apiRoot 'Facades'
if (Test-Path -LiteralPath $facades) { Get-ChildItem -LiteralPath $facades -File -Filter '*.dll' | ForEach-Object { [void]$references.Add($_.FullName) } }

if ($ReferenceMode -ne 'Project3') {
    $managedFolders = @(Get-ChildItem -LiteralPath $GamePath -Directory -Filter '*_Data' | ForEach-Object {
        $candidate = Join-Path $_.FullName 'Managed'
        if (Test-Path -LiteralPath (Join-Path $candidate 'Assembly-CSharp.dll') -PathType Leaf) { $candidate }
    })
    if ($managedFolders.Count -ne 1) { throw 'Player reference mode requires exactly one Mono *_Data/Managed folder.' }
    $managedRoot = $managedFolders[0]
    $gameAssembly = Join-Path $managedRoot 'Assembly-CSharp.dll'
    Get-ChildItem -LiteralPath $managedRoot -File -Filter '*.dll' | Where-Object {
        $_.Name -notmatch '^(mscorlib|System(\..*)?|Mono.Security)\.dll$' -and $_.Name -notmatch 'Editor|TestRunner|nunit'
    } | ForEach-Object { [void]$references.Add($_.FullName) }
} else {
    $gameAssembly = Join-Path $ProjectPath 'Library\ScriptAssemblies\Assembly-CSharp.dll'
    Require-File $gameAssembly
    foreach ($path in $hintPaths) {
        $name = [IO.Path]::GetFileName($path)
        if ($name -match 'Editor|Tests?Runner|nunit' -or $name -eq 'mscorlib.dll' -or $name -match '^System(\..*)?\.dll$') { continue }
        if ($name -match '^UnityEngine(\..*)?\.dll$' -or $name -in @('Unity.TextMeshPro.dll', 'Newtonsoft.Json.dll', 'com.rlabrecque.steamworks.net.dll', 'Assembly-CSharp-firstpass.dll', 'Unity.InputSystem.dll', 'DOTween.dll')) {
            Require-File $path
            [void]$references.Add($path)
        }
    }
    $firstPass = Join-Path $ProjectPath 'Library\ScriptAssemblies\Assembly-CSharp-firstpass.dll'
    if (Test-Path -LiteralPath $firstPass -PathType Leaf) { [void]$references.Add($firstPass) }
    # Unity asmdefs can be ProjectReference entries rather than Reference/HintPath.
    # Read only their already generated runtime assemblies; never substitute the older player DLL.
    foreach ($assemblyName in @('com.rlabrecque.steamworks.net', 'UnityEngine.UI', 'Unity.TextMeshPro')) {
        $generated = Join-Path $ProjectPath ('Library\ScriptAssemblies\' + $assemblyName + '.dll')
        if (Test-Path -LiteralPath $generated -PathType Leaf) { [void]$references.Add($generated) }
    }
    # Player-battle replica gates use the native RVO and behaviour-tree components.
    foreach ($assemblyName in @('AstarPathfindingProject', 'NodeCanvas', 'PackageTools', 'ParadoxNotion', 'Drawing')) {
        $generated = Join-Path $ProjectPath ('Library\ScriptAssemblies\' + $assemblyName + '.dll')
        Require-File $generated
        [void]$references.Add($generated)
    }
    [void]$references.Add($gameAssembly)
}

# Resolve the same com.harmony provider the native MOD loader sees, preferring this game's workshop.
if (-not $HarmonyPath -and $ReferenceMode -eq 'Project3') {
    $HarmonyPath = Join-Path $ProjectPath 'mods\Mod_Harmony\v_common\Assemblies\0Harmony.dll'
}
if (-not $HarmonyPath -and $ReferenceMode -eq 'TapTap') {
    $localMods = Join-Path $GamePath 'mods'
    $providers = @()
    if (Test-Path -LiteralPath $localMods -PathType Container) {
        $providers = @(Get-ChildItem -LiteralPath $localMods -Directory | ForEach-Object {
            $manifest = Join-Path $_.FullName 'Manifest\manifest.xml'
            if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { return }
            $xml = Read-Xml $manifest
            if ($xml.ModXmlManifest.packageId -ne 'com.harmony') { return }
            $assembly = Join-Path $_.FullName 'v_common\Assemblies\0Harmony.dll'
            if (Test-Path -LiteralPath $assembly -PathType Leaf) { $assembly }
        })
    }
    if ($providers.Count -ne 1) { throw 'TapTap requires one local com.harmony provider; pass -HarmonyPath explicitly if needed.' }
    $HarmonyPath = $providers[0]
}
if (-not $HarmonyPath) {
    $commonRoot = Split-Path -Parent $GamePath
    $steamApps = Split-Path -Parent $commonRoot
    $gameFolderName = Split-Path -Leaf $GamePath
    $manifests = @(Get-ChildItem -LiteralPath $steamApps -Filter 'appmanifest_*.acf' -File | Where-Object {
        [IO.File]::ReadAllText($_.FullName) -match ('"installdir"\s+"' + [Regex]::Escape($gameFolderName) + '"')
    })
    if ($manifests.Count -ne 1) { throw 'Cannot uniquely resolve this installed game appmanifest; pass -HarmonyPath explicitly.' }
    $appId = [Regex]::Match($manifests[0].Name, 'appmanifest_(\d+)').Groups[1].Value
    $providers = @()
    foreach ($scope in @((Join-Path $steamApps "workshop\content\$appId"), (Join-Path $GamePath 'mods'))) {
        if (-not (Test-Path -LiteralPath $scope -PathType Container)) { continue }
        $inScope = @(Get-ChildItem -LiteralPath $scope -Directory | ForEach-Object {
            $manifest = Join-Path $_.FullName 'Manifest\manifest.xml'
            if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { return }
            $xml = Read-Xml $manifest
            if ($xml.ModXmlManifest.packageId -ne 'com.harmony') { return }
            $assembly = Join-Path $_.FullName 'v_common\Assemblies\0Harmony.dll'
            if (Test-Path -LiteralPath $assembly -PathType Leaf) { $assembly }
        })
        if ($inScope.Count -gt 0) { $providers = $inScope; break }
    }
    if ($providers.Count -ne 1) { throw 'The installed com.harmony provider is absent or ambiguous; pass -HarmonyPath explicitly.' }
    $HarmonyPath = $providers[0]
}
$HarmonyPath = [IO.Path]::GetFullPath($HarmonyPath)
Require-File $HarmonyPath
$harmonyIdentity = [Reflection.AssemblyName]::GetAssemblyName($HarmonyPath)
if ($harmonyIdentity.Name -ne '0Harmony') { throw '-HarmonyPath must name the installed game provider 0Harmony.dll.' }
[void]$references.Add($HarmonyPath)
$jsonReferenceName = 'Newtonsoft.Json.dll'
$privateJsonRoot = Join-Path $modRoot 'artifacts\private-json\private'
if ($ReferenceMode -ne 'Project3') {
    & (Join-Path $modRoot 'tools\Build-PrivateJson.ps1') -GamePath $GamePath
    foreach ($reference in @($references | Where-Object { [IO.Path]::GetFileName($_) -eq 'Newtonsoft.Json.dll' })) {
        [void]$references.Remove($reference)
    }
    $jsonReferenceName = 'FreeWorldMultiplayer.Newtonsoft.Json.dll'
    [void]$references.Add((Join-Path $privateJsonRoot $jsonReferenceName))
}
foreach ($required in @('Assembly-CSharp.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.UI.dll', 'Unity.TextMeshPro.dll', $jsonReferenceName, 'com.rlabrecque.steamworks.net.dll', 'AstarPathfindingProject.dll', 'NodeCanvas.dll', 'PackageTools.dll', 'ParadoxNotion.dll', 'Drawing.dll')) {
    if (-not @($references | Where-Object { [IO.Path]::GetFileName($_) -eq $required }).Count) { throw "Required reference absent: $required" }
}

$artifactRoot = Join-Path $modRoot 'artifacts'
$label = switch ($ReferenceMode) { 'Steam' { 'steam-target' } 'TapTap' { 'taptap-target' } default { 'p3-development-reference' } }
$buildRoot = Join-Path $artifactRoot $label
$outputPackage = Join-Path $buildRoot 'FreeWorldMultiplayer'
$assemblyRoot = Join-Path $outputPackage 'v_common\Assemblies'
New-Item -ItemType Directory -Force -Path $buildRoot, $assemblyRoot | Out-Null
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$sdkLine = @(& $dotnet --list-sdks) | Select-Object -Last 1
if ($LASTEXITCODE -ne 0 -or $sdkLine -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A .NET SDK with Roslyn is required.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
Require-File $compiler
$sources = @(Get-ChildItem -LiteralPath (Join-Path $modRoot 'src') -Recurse -File -Filter '*.cs' | Sort-Object FullName)
if ($sources.Count -eq 0) { throw 'No source files in src.' }
$informationalVersion = $release.ModVersion + '+' + $development.SourceRevision
$versionSource = Join-Path $buildRoot 'AssemblyRelease.g.cs'
$versionAttributes = @"
// Generated from VERSION by build.ps1; do not edit this artifact.
[assembly: System.Reflection.AssemblyVersion("$($release.AssemblyVersion)")]
[assembly: System.Reflection.AssemblyFileVersion("$($release.AssemblyVersion)")]
[assembly: System.Reflection.AssemblyInformationalVersion("$informationalVersion")]
"@
[IO.File]::WriteAllText($versionSource, $versionAttributes, [Text.UTF8Encoding]::new($false))
$outputDll = Join-Path $assemblyRoot 'FreeWorldMultiplayer.dll'
$response = Join-Path $buildRoot 'compile.rsp'
$arguments = @('/nostdlib+', '/target:library', '/langversion:latest', '/deterministic+', '/optimize+', '/debug:portable', '/utf8output', '/warn:4', ('/out:"' + $outputDll + '"'))
$arguments += @($references | Sort-Object | ForEach-Object { '/reference:"' + $_ + '"' })
$arguments += @($sources | ForEach-Object { '"' + $_.FullName + '"' })
$arguments += '"' + $versionSource + '"'
[IO.File]::WriteAllLines($response, [string[]]$arguments, [Text.UTF8Encoding]::new($false))
Write-Host "Reference mode: $ReferenceMode; game assembly: $gameAssembly"
Write-Host "MOD release: $($release.ModVersion); assembly: $($release.AssemblyVersion)"
Write-Host "Shared Harmony provider: $HarmonyPath ($($harmonyIdentity.Version)); provider DLL will not be bundled."
& $dotnet $compiler /noconfig "@$response"
if ($LASTEXITCODE -ne 0) { throw "MOD compilation failed (exit $LASTEXITCODE). No game files were touched." }
if ([Reflection.AssemblyName]::GetAssemblyName($outputDll).Version.ToString() -ne $release.AssemblyVersion) {
    throw 'Compiled DLL version does not match VERSION. No package will be published.'
}

$runtimeReport = $null
$compatibility = Join-Path $modRoot 'tools\Read-RuntimeCompatibility.ps1'
if ((Test-Path -LiteralPath $compatibility) -and (Test-Path -LiteralPath (Join-Path $GamePath 'Outer Space_Data\Managed\mscorlib.dll'))) {
    $runtimeReport = & $compatibility -GamePath $GamePath -HarmonyPath $HarmonyPath
}
$fingerprints = @($references | Where-Object { [IO.Path]::GetFileName($_) -in @('Assembly-CSharp.dll', '0Harmony.dll', 'com.rlabrecque.steamworks.net.dll', 'UnityEngine.CoreModule.dll', $jsonReferenceName, 'AstarPathfindingProject.dll', 'NodeCanvas.dll', 'PackageTools.dll', 'ParadoxNotion.dll', 'Drawing.dll') } | Sort-Object | ForEach-Object {
    $identity = [Reflection.AssemblyName]::GetAssemblyName($_)
    [ordered]@{ Assembly = $identity.Name; Version = $identity.Version.ToString(); Path = $_; SHA256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
})
$buildInfo = [ordered]@{
    BuiltAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); ReferenceMode = $ReferenceMode
    ModVersion = $release.ModVersion; AssemblyVersion = $release.AssemblyVersion
    InformationalVersion = $informationalVersion
    PackageKind = $label; Compiled = $true; RuntimeTested = $false; Deployed = $false
    SteamCompatibilityConfirmed = $false
    SourceRoot = $development.SourceRoot; SourceRevision = $development.SourceRevision
    SourceInputs = $development.Inputs; UsesPrivateJson = ($ReferenceMode -ne 'Project3')
    Notice = if ($ReferenceMode -eq 'Project3') { 'P3 development reference package only. The installed player may expose older APIs. This package is not declared player-compatible.' } else { "Compiled against the selected $ReferenceMode game at $GamePath. Compilation is not a runtime MOD loading or multiplayer acceptance test." }
    References = $fingerprints; InstalledSteamRuntimeInspection = $runtimeReport
}
[IO.File]::WriteAllText((Join-Path $outputPackage 'BUILD-INFO.json'), ($buildInfo | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
$files = @('Manifest\manifest.xml', 'Manifest\icon.png', 'Manifest\preview.png', 'Launch-Direct.ps1', 'Update-From-Main.ps1', 'README.md', 'DEPENDENCIES.md', 'VERSION', 'CHANGELOG.md')
foreach ($relative in $files) {
    $source = if ($relative -like 'Manifest\*' -or $relative -in @('Launch-Direct.ps1','Update-From-Main.ps1')) { Join-Path (Join-Path $modRoot 'package') $relative } else { Join-Path $modRoot $relative }
    Require-File $source
    $destination = Join-Path $outputPackage $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
}
$manifestPath = Join-Path $outputPackage 'Manifest\manifest.xml'
$manifestTemplate = [IO.File]::ReadAllText($manifestPath)
if (-not $manifestTemplate.Contains('@MOD_DISPLAY_VERSION@')) { throw 'The MOD manifest template is missing its release-version token.' }
[IO.File]::WriteAllText($manifestPath, $manifestTemplate.Replace('@MOD_DISPLAY_VERSION@', $release.ModVersion), [Text.UTF8Encoding]::new($false))
if ($ReferenceMode -ne 'Project3') {
    foreach ($relative in @('v_common\Assemblies\FreeWorldMultiplayer.Newtonsoft.Json.dll','Newtonsoft.Json.LICENSE.md','PRIVATE-JSON-BUILD.json')) {
        Copy-Item -LiteralPath (Join-Path $privateJsonRoot ([IO.Path]::GetFileName($relative))) -Destination (Join-Path $outputPackage $relative) -Force
        $files += $relative
    }
    $manifestPath = Join-Path $outputPackage 'Manifest\manifest.xml'
    $manifest = Read-Xml $manifestPath
    $version = $manifest.ModXmlManifest.versions.ModMetaVersion
    $lib = $manifest.CreateElement('lib'); $lib.InnerText = $jsonReferenceName
    [void]$version.InsertBefore($lib, $version.SelectSingleNode('main'))
    $manifest.Save($manifestPath)
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $artifactRoot "FreeWorldMultiplayer-$label.zip"
$zipTemporary = Join-Path $artifactRoot ([Guid]::NewGuid().ToString('N') + '.zip')
$zip = [IO.Compression.ZipFile]::Open($zipTemporary, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($relative in @($files + @('BUILD-INFO.json', 'v_common\Assemblies\FreeWorldMultiplayer.dll', 'v_common\Assemblies\FreeWorldMultiplayer.pdb'))) {
        $source = Join-Path $outputPackage $relative
        Require-File $source
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $source, ('FreeWorldMultiplayer/' + $relative.Replace('\', '/')), [IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $zip.Dispose() }
if (Test-Path -LiteralPath $zipPath) { [IO.File]::Replace($zipTemporary, $zipPath, [NullString]::Value) } else { [IO.File]::Move($zipTemporary, $zipPath) }
if ((& (Join-Path $modRoot 'tools\Get-DevelopmentRevision.ps1') -Root $modRoot).SourceRevision -ne $development.SourceRevision) {
    throw 'Development inputs changed during compilation; rebuild before installing.'
}
Write-Host "Built package: $outputPackage"
Write-Host "ZIP: $zipPath"
Write-Host 'Package only. No game, Project3, activation list, or shared provider files were modified.'
