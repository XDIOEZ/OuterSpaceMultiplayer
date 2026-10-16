#requires -Version 7.0
# Materialize an auditable, content-addressed COPY of current MOD build inputs.
# Does NOT stage/commit historical agent files or approve a dirty Git closure.
# Only writes beneath the source repository's ignored artifacts directory.
[CmdletBinding()]
param(
    [string]$Root,
    [string]$AuditPath,
    [string]$SnapshotDirectory,
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
$Root = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
if (-not $AuditPath) {
    $AuditPath = Join-Path $Root 'artifacts\optimization-source-closure\audit.json'
}
$auditFile = [IO.Path]::GetFullPath($AuditPath)
if (-not (Test-Path -LiteralPath $auditFile -PathType Leaf)) {
    throw "Source closure audit does not exist: $auditFile"
}
$audit = Get-Content -LiteralPath $auditFile -Raw -Encoding UTF8 | ConvertFrom-Json
if ($audit.Schema -ne 1 -or $audit.RepoRoot -ne $Root -or
    $audit.InputDigestSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
    throw 'Audit identity, schema, or digest is invalid.'
}
if ($audit.InputCount -ne @($audit.Files).Count -or $audit.InputCount -eq 0) {
    throw 'Audit file list does not match declared input count.'
}

$snapshot = if ($SnapshotDirectory) { [IO.Path]::GetFullPath($SnapshotDirectory) }
    else { [IO.Path]::GetFullPath((Join-Path $Root ('artifacts\optimization-frozen-source\' +
        $audit.InputDigestSha256.ToUpperInvariant()))) }
$safeRoot = [IO.Path]::GetFullPath((Join-Path $Root 'artifacts\optimization-frozen-source'))
if (-not $snapshot.StartsWith($safeRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Frozen source snapshots may only be stored under artifacts\optimization-frozen-source.'
}
$manifestPath = Join-Path $snapshot 'artifacts\frozen-source-manifest.json'
$items = @($audit.Files)
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($item in $items) {
    if ([string]::IsNullOrWhiteSpace($item.Path) -or
        [IO.Path]::IsPathRooted($item.Path) -or
        $item.Path.StartsWith('/') -or $item.Path.EndsWith('/') -or
        $item.Path.Contains('//') -or
        $item.Path -match '[\\:\x00-\x1F]' -or
        $item.Path -match '(^|/)\.(/|$)' -or
        $item.Path -match '(^|/)\.\.(/|$)' -or
        $item.SHA256 -notmatch '^[A-Fa-f0-9]{64}$' -or
        -not $seen.Add($item.Path)) {
        throw 'Unsafe, duplicate or invalid source manifest path/hash.'
    }
}

function Check-FrozenFiles {
    foreach ($item in $items) {
        $path = [IO.Path]::GetFullPath((Join-Path $snapshot $item.Path.Replace('/', '\')))
        if (-not $path.StartsWith($snapshot + '\', [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Frozen source file is missing or outside snapshot: $($item.Path)"
        }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $item.SHA256) {
            throw "Frozen source byte mismatch: $($item.Path)"
        }
    }
    $revision = & (Join-Path $snapshot 'tools\Get-DevelopmentRevision.ps1') -Root $snapshot
    if ($revision.SourceRevision -ne $audit.InputDigestSha256 -or
        $revision.Inputs.Count -ne $items.Count) {
        throw 'Frozen source revision differs from the original build manifest.'
    }
    Write-Output ("VERIFIED_FROZEN_SOURCE=$snapshot")
    Write-Output ("SOURCE_REVISION=$($revision.SourceRevision)")
    Write-Output ("INPUT_COUNT=$($items.Count)")
}

if ($VerifyOnly) {
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'Missing frozen manifest; snapshot cannot be verified.'
    }
    $record = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($record.Schema -ne 1 -or $record.SourceRevision -ne $audit.InputDigestSha256 -or
        $record.InputCount -ne $items.Count) {
        throw 'Frozen manifest does not match the requested source audit.'
    }
    Check-FrozenFiles
    return
}

if (Test-Path -LiteralPath $snapshot) {
    throw "Snapshot destination exists. Use -VerifyOnly to check it; never overwrite it: $snapshot"
}

# A build-time worktree snapshot is NOT a Git clean-checkout proof.
# Validate the live input list before and after copying to stop concurrent edits.
$live = & (Join-Path $Root 'tools\Get-DevelopmentRevision.ps1') -Root $Root
if ($live.SourceRevision -ne $audit.InputDigestSha256 -or
    $live.Inputs.Count -ne $items.Count) {
    throw 'The working copy changed since the audit; regenerate its SHA-256 input manifest.'
}
for ($i = 0; $i -lt $items.Count; ++$i) {
    if ($items[$i].Path -cne $live.Inputs[$i].Path -or
        $items[$i].SHA256 -cne $live.Inputs[$i].SHA256) {
        throw 'The input ordering/hash changed since the audit.'
    }
}

[void](New-Item -ItemType Directory -Path $snapshot -Force)
foreach ($item in $items) {
    $source = [IO.Path]::GetFullPath((Join-Path $Root $item.Path.Replace('/', '\')))
    $dest = [IO.Path]::GetFullPath((Join-Path $snapshot $item.Path.Replace('/', '\')))
    if (-not $source.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not $dest.StartsWith($snapshot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe source/destination: $($item.Path)"
    }
    $file = Get-Item -LiteralPath $source
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refuse to copy a symlinked build input: $($item.Path)"
    }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $item.SHA256) {
        throw "Concurrent input modification: $($item.Path)"
    }
    [void](New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force)
    [IO.File]::Copy($source, $dest, $false)
}

# Artifacts are excluded from SourceRevision, so writing the receipt cannot
# change the copied build inputs or leak this manifest into the MOD package.
[void](New-Item -ItemType Directory -Path (Split-Path -Parent $manifestPath) -Force)
$receipt = [pscustomobject][ordered]@{
    Schema = 1
    CreatedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    SourceRevision = $audit.InputDigestSha256
    InputCount = $items.Count
    OriginalGitHead = $audit.Head
    OriginalCleanGitClosure = $audit.CleanGitClosure
    Notice = 'This frozen build-input copy preserves the current worktree without committing other agents files. It is NOT a Git-complete release.'
}
[IO.File]::WriteAllText($manifestPath, ($receipt | ConvertTo-Json -Depth 5),
    [Text.UTF8Encoding]::new($false))

$after = & (Join-Path $Root 'tools\Get-DevelopmentRevision.ps1') -Root $Root
if ($after.SourceRevision -ne $audit.InputDigestSha256) {
    throw 'Main worktree changed during the source snapshot; do not build or trust this snapshot.'
}
Check-FrozenFiles
