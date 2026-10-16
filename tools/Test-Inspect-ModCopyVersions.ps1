#requires -Version 7.0
# Synthetic file fixtures. Never writes to, or modifies, installed MOD copies.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$testRoot = Join-Path $root ('artifacts\mod-copy-inspection-selftest\' +
    [guid]::NewGuid().ToString('N'))
$inspector = Join-Path $PSScriptRoot 'Inspect-ModCopyVersions.ps1'
$sourceRoot = 'C:\Users\CatStudio\Desktop\Project3\mods\FreeWorldMultiplayer'
$sourceInfo = Join-Path $sourceRoot 'BUILD-INFO.json'
$sourceDll = Join-Path $sourceRoot 'v_common\Assemblies\FreeWorldMultiplayer.dll'
if (-not (Test-Path -LiteralPath $sourceInfo -PathType Leaf) -or
    -not (Test-Path -LiteralPath $sourceDll -PathType Leaf)) {
    throw 'The read-only Project3 MOD copy required for this fixture is unavailable.'
}
$original = Get-Content -LiteralPath $sourceInfo -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]$original.SourceRevision -notmatch '^[a-fA-F0-9]{64}$') {
    throw 'Reference MOD metadata has no usable full source revision.'
}
$sourceDigest = [string]$original.SourceRevision
$wrongDigest = if ($sourceDigest -ceq ('A' * 64)) { 'B' * 64 } else { 'A' * 64 }
$paths = @{}
foreach ($mode in @('Project3', 'Steam', 'TapTap')) {
    $mod = Join-Path $testRoot (Join-Path $mode 'mods\FreeWorldMultiplayer')
    [void](New-Item -Path (Join-Path $mod 'v_common\Assemblies') -ItemType Directory -Force)
    Copy-Item -LiteralPath $sourceDll -Destination (Join-Path $mod 'v_common\Assemblies\FreeWorldMultiplayer.dll')
    $cloned = Get-Content -LiteralPath $sourceInfo -Raw -Encoding UTF8 | ConvertFrom-Json
    $cloned.ReferenceMode = $mode
    $file = Join-Path $mod 'BUILD-INFO.json'
    [IO.File]::WriteAllText($file, ($cloned | ConvertTo-Json -Depth 15),
        [Text.UTF8Encoding]::new($false))
    $paths[$mode] = $mod
}
$checks = 0
function Check([bool]$condition, [string]$why) {
    if (-not $condition) { throw 'FAILED synthetic MOD copy inspection: ' + $why }
    $script:checks++
}

$expected = & $inspector -Root $root -ExpectedSourceRevision $sourceDigest -ModRoots $paths
Check ($expected.AllDiskCopiesMatchCurrent -and $expected.AllCopiesHaveSameDiskSource) 'exact on-disk revision match'
Check (@($expected.Copies | Where-Object {$_.Status -eq 'matching-disk-source'}).Count -eq 3) 'matching three distinct source copies'
Check (-not $expected.ActualRuntimeLoadVerified -and -not $expected.InstallationsChanged) 'metadata evidence never claims game loading or installation'
Check (@($expected.Copies | Where-Object { $_.DllSha256 -match '^[A-F0-9]{64}$'}).Count -eq 3) 'all three synthetic DLL hashes inspected'

$old = & $inspector -Root $root -ExpectedSourceRevision $wrongDigest -ModRoots $paths
Check (-not $old.AllDiskCopiesMatchCurrent -and $old.AllCopiesHaveSameDiskSource) 'same installed version can still be stale versus a different source'
Check (@($old.Copies | Where-Object {$_.Status -eq 'stale-disk-source'}).Count -eq 3) 'all outdated disk copies are separately marked'

$fixture = Join-Path $paths.Steam 'BUILD-INFO.json'
$originalText = [IO.File]::ReadAllText($fixture)
$tampered = $originalText | ConvertFrom-Json
$tampered.ReferenceMode = 'Project3'
[IO.File]::WriteAllText($fixture, ($tampered | ConvertTo-Json -Depth 15),
    [Text.UTF8Encoding]::new($false))
$oneWrongMode = & $inspector -Root $root -ExpectedSourceRevision $sourceDigest -ModRoots $paths
Check (@($oneWrongMode.Copies | Where-Object { $_.Target -eq 'Steam' -and
    $_.Status -eq 'inconsistent-copy' }).Count -eq 1) 'wrong platform package is rejected'
[IO.File]::WriteAllText($fixture, $originalText, [Text.UTF8Encoding]::new($false))

$tampered = $originalText | ConvertFrom-Json
$tampered.SourceRevision = $wrongDigest
$tampered.InformationalVersion = [string]$tampered.ModVersion + '+' + $wrongDigest
[IO.File]::WriteAllText($fixture, ($tampered | ConvertTo-Json -Depth 15),
    [Text.UTF8Encoding]::new($false))
$forged = & $inspector -Root $root -ExpectedSourceRevision $wrongDigest -ModRoots $paths
Check (@($forged.Copies | Where-Object { $_.Target -eq 'Steam' -and
    $_.Status -eq 'inconsistent-copy' }).Count -eq 1) 'BUILD-INFO forged source digest cannot spoof DLL information version'
[IO.File]::WriteAllText($fixture, $originalText, [Text.UTF8Encoding]::new($false))

[IO.File]::WriteAllText($fixture, 'not-json', [Text.UTF8Encoding]::new($false))
$malformed = & $inspector -Root $root -ExpectedSourceRevision $sourceDigest -ModRoots $paths
Check (@($malformed.Copies | Where-Object { $_.Target -eq 'Steam' -and
    $_.Status -eq 'inconsistent-copy' }).Count -eq 1) 'malformed metadata does not produce an invented version'
[IO.File]::WriteAllText($fixture, $originalText, [Text.UTF8Encoding]::new($false))

$dll = Join-Path $paths.TapTap 'v_common\Assemblies\FreeWorldMultiplayer.dll'
Remove-Item -LiteralPath $dll
$incomplete = & $inspector -Root $root -ExpectedSourceRevision $sourceDigest -ModRoots $paths
Check (@($incomplete.Copies | Where-Object { $_.Target -eq 'TapTap' -and
    $_.Status -eq 'incomplete-copy' }).Count -eq 1) 'missing DLL is reported without silently marking the platform current'
Remove-Item -LiteralPath (Join-Path $paths.TapTap 'BUILD-INFO.json')
$missing = & $inspector -Root $root -ExpectedSourceRevision $sourceDigest -ModRoots $paths
Check (@($missing.Copies | Where-Object { $_.Target -eq 'TapTap' -and
    $_.Status -eq 'missing-copy' }).Count -eq 1) 'absent MOD copy reports missing rather than installed'

$reportPath = Join-Path $testRoot 'on-disk-review.json'
$saved = & $inspector -Root $root -ExpectedSourceRevision $sourceDigest -ModRoots $paths -OutFile $reportPath
Check ((Test-Path -LiteralPath $reportPath) -and
    ($saved.ActualRuntimeLoadVerified -eq $false)) 'report writes only the isolated ignored artifact path'
$blockedOverwrite = $false
try { & $inspector -Root $root -ExpectedSourceRevision $sourceDigest -ModRoots $paths -OutFile $reportPath | Out-Null }
catch { $blockedOverwrite = $true }
Check ($blockedOverwrite) 'existing evidence cannot be overwritten'
$blockedOutside = $false
try { & $inspector -Root $root -ExpectedSourceRevision $sourceDigest -ModRoots $paths -OutFile (Join-Path $root 'unapproved-report.json') | Out-Null }
catch { $blockedOutside = $true }
Check ($blockedOutside) 'report outside ignored artifacts is refused'

Write-Output ('PASS: ' + $checks + ' read-only MOD copy/version inspection checks')
Write-Output ('SYNTHETIC_FIXTURE=' + $testRoot)
