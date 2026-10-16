#requires -Version 7.0
# R13-03 preflight: inspect on-disk MOD copies. NEVER installs, loads a DLL
# into the current process, starts Unity, reads saves/identity, or claims
# that a version was loaded by a running game.
[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$ExpectedSourceRevision,
    [hashtable]$ModRoots,
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Root = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
if (-not $ExpectedSourceRevision) {
    $revision = & (Join-Path $Root 'tools\Get-DevelopmentRevision.ps1') -Root $Root
    $ExpectedSourceRevision = [string]$revision.SourceRevision
}
if ($ExpectedSourceRevision -notmatch '^[0-9a-fA-F]{64}$') {
    throw 'The expected source revision must be a full SHA-256 from actual MOD source inputs.'
}
$ExpectedSourceRevision = $ExpectedSourceRevision.ToUpperInvariant()

if ($null -eq $ModRoots) {
    $ModRoots = @{
        Project3 = 'C:\Users\CatStudio\Desktop\Project3\mods\FreeWorldMultiplayer'
        Steam = 'C:\Program Files (x86)\Steam\steamapps\common\Outer Space\mods\FreeWorldMultiplayer'
        TapTap = 'C:\TapTap\PC Games\242783\mods\FreeWorldMultiplayer'
    }
}
foreach ($mode in @('Project3', 'Steam', 'TapTap')) {
    if (-not $ModRoots.ContainsKey($mode) -or
        [string]::IsNullOrWhiteSpace([string]$ModRoots[$mode])) {
        throw "All three explicit MOD roots are required, including $mode."
    }
}
if (@($ModRoots.Keys).Count -ne 3) {
    throw 'Only the three named game MOD copy roots may be inspected.'
}

$copies = [Collections.Generic.List[object]]::new()
foreach ($mode in @('Project3', 'Steam', 'TapTap')) {
    $modRoot = [IO.Path]::GetFullPath([string]$ModRoots[$mode])
    if ([IO.Path]::GetFileName($modRoot.TrimEnd('\', '/')) -ne 'FreeWorldMultiplayer') {
        throw "The $mode inspection root must be a FreeWorldMultiplayer MOD directory."
    }

    $infoFile = Join-Path $modRoot 'BUILD-INFO.json'
    $dllFile = Join-Path $modRoot 'v_common\Assemblies\FreeWorldMultiplayer.dll'
    $result = [ordered]@{
        Target = $mode
        Status = 'missing-copy'
        Reason = 'No installed MOD build metadata or main assembly.'
        SourceRevision = $null
        ModVersion = $null
        AssemblyVersion = $null
        DllSha256 = $null
        MatchesCurrentSource = $false
        MetadataConsistent = $false
        GameProcessLoadedThisDllVerified = $false
    }
    $hasInfo = Test-Path -LiteralPath $infoFile -PathType Leaf
    $hasDll = Test-Path -LiteralPath $dllFile -PathType Leaf
    if ($hasInfo -xor $hasDll) {
        $result.Status = 'incomplete-copy'
        $result.Reason = 'BUILD-INFO.json and the main DLL must both exist.'
    }
    elseif ($hasInfo -and $hasDll) {
        try {
            if ((Get-Item -LiteralPath $infoFile -Force).Attributes -band
                    [IO.FileAttributes]::ReparsePoint -or
                (Get-Item -LiteralPath $dllFile -Force).Attributes -band
                    [IO.FileAttributes]::ReparsePoint) {
                throw 'Redirected MOD metadata or DLL cannot be trusted.'
            }
            if ((Get-Item -LiteralPath $infoFile).Length -gt 4MB) {
                throw 'BUILD-INFO.json is unexpectedly large.'
            }
            $info = Get-Content -LiteralPath $infoFile -Raw -Encoding UTF8 | ConvertFrom-Json
            $source = [string]$info.SourceRevision
            $modVersion = [string]$info.ModVersion
            $assemblyVersion = [string]$info.AssemblyVersion
            $informationalVersion = [string]$info.InformationalVersion
            if ($info.Compiled -ne $true -or $info.ReferenceMode -ne $mode -or
                $source -notmatch '^[0-9a-fA-F]{64}$' -or
                $modVersion -notmatch '^V[0-9]+\.[0-9]+$' -or
                $assemblyVersion -notmatch '^[0-9]+\.[0-9]+\.0\.0$' -or
                $informationalVersion -cne ($modVersion + '+' + $source)) {
                throw 'The installed BUILD-INFO source, target, version or build status is inconsistent.'
            }
            # GetAssemblyName and FileVersionInfo inspect PE metadata without
            # loading or executing the game DLL (Assembly.Load is forbidden).
            $dllVersion = [Reflection.AssemblyName]::GetAssemblyName($dllFile).Version.ToString()
            $dllProductVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($dllFile).ProductVersion
            if ($dllVersion -cne $assemblyVersion -or
                $dllProductVersion -cne $informationalVersion) {
                throw 'Actual MOD DLL assembly/informational version differs from BUILD-INFO.json.'
            }
            $result.SourceRevision = $source.ToUpperInvariant()
            $result.ModVersion = $modVersion
            $result.AssemblyVersion = $assemblyVersion
            $result.DllSha256 = (Get-FileHash -LiteralPath $dllFile -Algorithm SHA256).Hash
            $result.MetadataConsistent = $true
            $result.MatchesCurrentSource = [string]::Equals(
                $source, $ExpectedSourceRevision, [StringComparison]::OrdinalIgnoreCase)
            if ($result.MatchesCurrentSource) {
                $result.Status = 'matching-disk-source'
                $result.Reason = 'On-disk metadata matches current inputs; actual game loading remains unverified.'
            }
            else {
                $result.Status = 'stale-disk-source'
                $result.Reason = 'The installed MOD is older or different than the current development source.'
            }
        }
        catch {
            $result.Status = 'inconsistent-copy'
            $result.Reason = $_.Exception.Message
            $result.MetadataConsistent = $false
            $result.MatchesCurrentSource = $false
            $result.DllSha256 = $null
        }
    }
    $copies.Add([pscustomobject]$result)
}

$report = [pscustomobject][ordered]@{
    Schema = 1
    ExpectedSourceRevision = $ExpectedSourceRevision
    AllDiskCopiesMatchCurrent = @($copies | Where-Object {
        $_.Status -eq 'matching-disk-source'
    }).Count -eq 3
    AllCopiesHaveSameDiskSource = @($copies | Where-Object {
        $_.MetadataConsistent
    }).Count -eq 3 -and
        @($copies | Select-Object -ExpandProperty SourceRevision -Unique).Count -eq 1
    ActualRuntimeLoadVerified = $false
    InstallationsChanged = $false
    Copies = @($copies)
    Notice = 'Read-only on-disk metadata. This does not prove actual DLL loading, paired host/guest logs, a clean Git source, or gameplay acceptance.'
}
if ($OutFile) {
    $artifacts = [IO.Path]::GetFullPath((Join-Path $Root 'artifacts')).TrimEnd('\', '/')
    $out = [IO.Path]::GetFullPath($OutFile)
    if (-not $out.StartsWith($artifacts + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Inspection reports may only be created under the ignored MOD artifacts directory.'
    }
    if (Test-Path -LiteralPath $out) {
        throw 'Refusing to overwrite existing inspection evidence.'
    }
    [void](New-Item -ItemType Directory -Force -Path (Split-Path -Parent $out))
    [IO.File]::WriteAllText($out,
        ($report | ConvertTo-Json -Depth 7),
        [Text.UTF8Encoding]::new($false))
}
Write-Output $report
