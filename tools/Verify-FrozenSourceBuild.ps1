#requires -Version 7.0
# Verify a package-only build produced in a byte-verified frozen MOD source copy.
# This proves a repeatable build INPUT, not clean Git provenance or gameplay.
[CmdletBinding()]
param(
    [string]$Root,
    [string]$AuditPath,
    [switch]$RequireReleaseClosure
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
$Root = [IO.Path]::GetFullPath($Root)
if (-not $AuditPath) { $AuditPath = Join-Path $Root 'artifacts\optimization-source-closure\audit.json' }
$AuditPath = [IO.Path]::GetFullPath($AuditPath)
$audit = Get-Content -LiteralPath $AuditPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($audit.Schema -ne 1 -or $audit.InputDigestSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
    throw 'Invalid source audit schema/digest.'
}
$rootSource = [IO.Path]::GetFullPath((Join-Path $Root ('artifacts\optimization-frozen-source\' +
    $audit.InputDigestSha256.ToUpperInvariant())))

& (Join-Path $Root 'tools\Export-FrozenSourceSnapshot.ps1') -Root $Root -AuditPath $AuditPath -VerifyOnly | Out-Null

$receiptPath = Join-Path $rootSource 'artifacts\last-copy-sync.json'
if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
    throw 'Frozen source has no completed official BuildOnly receipt.'
}
$receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $receipt.Completed -or -not $receipt.BuildOnly -or
    @($receipt.Installations).Count -ne 0 -or
    $receipt.SourceRevision -ne $audit.InputDigestSha256 -or
    -not [string]::Equals($receipt.SourceRoot, $rootSource, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Frozen BuildOnly receipt has a different revision, root, incomplete build or installs.'
}

Add-Type -AssemblyName System.IO.Compression
foreach ($target in @(
    [pscustomobject]@{ Mode = 'Project3'; Label = 'p3-development-reference' },
    [pscustomobject]@{ Mode = 'Steam'; Label = 'steam-target' },
    [pscustomobject]@{ Mode = 'TapTap'; Label = 'taptap-target' }
)) {
    $packageRoot = Join-Path $rootSource ('artifacts\' + $target.Label + '\FreeWorldMultiplayer')
    $infoFile = Join-Path $packageRoot 'BUILD-INFO.json'
    $zipPath = Join-Path $rootSource ('artifacts\FreeWorldMultiplayer-' + $target.Label + '.zip')
    $dllPath = Join-Path $packageRoot 'v_common\Assemblies\FreeWorldMultiplayer.dll'
    if (-not (Test-Path -LiteralPath $infoFile -PathType Leaf) -or
        -not (Test-Path -LiteralPath $zipPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
        throw "Missing $($target.Mode) build package, ZIP or main DLL."
    }
    $info = Get-Content -LiteralPath $infoFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($info.ReferenceMode -ne $target.Mode -or
        $info.SourceRevision -ne $receipt.SourceRevision -or
        -not $info.Compiled -or $info.Deployed -or $info.RuntimeTested -or
        -not [string]::Equals($info.SourceRoot, $rootSource, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Wrong source, target or status in $($target.Mode) BUILD-INFO.json."
    }

    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $metaEntry = $zip.GetEntry('FreeWorldMultiplayer/BUILD-INFO.json')
        $dllEntry = $zip.GetEntry('FreeWorldMultiplayer/v_common/Assemblies/FreeWorldMultiplayer.dll')
        if ($null -eq $metaEntry -or $null -eq $dllEntry -or
            $dllEntry.Length -ne (Get-Item -LiteralPath $dllPath).Length) {
            throw "The $($target.Mode) ZIP lacks its matching BUILD-INFO or DLL."
        }
        $stream = $metaEntry.Open()
        try {
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8)
            try { $zipInfo = $reader.ReadToEnd() | ConvertFrom-Json }
            finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }
        if ($zipInfo.ReferenceMode -ne $target.Mode -or
            $zipInfo.SourceRevision -ne $info.SourceRevision) {
            throw "ZIP metadata differs from the built $($target.Mode) package."
        }
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $packedDllStream = $dllEntry.Open()
            try {
                $actualDllHash = [Convert]::ToHexString($sha.ComputeHash($packedDllStream))
            }
            finally { $packedDllStream.Dispose() }
        }
        finally { $sha.Dispose() }
        if ($actualDllHash -ne (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash) {
            throw "ZIP DLL hash differs from the built $($target.Mode) DLL."
        }
    }
    finally { $zip.Dispose() }
    Write-Output ("$($target.Mode): FROZEN_BUILD_VERIFIED " +
        "bytes=$((Get-Item -LiteralPath $zipPath).Length)")
}

$releaseReady = [bool]$audit.CleanGitClosure
Write-Output ("BUILD_ONLY_SOURCE_REVISION=$($audit.InputDigestSha256)")
Write-Output ("CLEAN_GIT_SOURCE_CLOSURE=$releaseReady")
Write-Output 'RUNTIME_TESTED=false INSTALLATIONS=0'
if ($RequireReleaseClosure -and -not $releaseReady) {
    throw 'Build evidence is valid, but Git source closure is incomplete. Release is prohibited.'
}
