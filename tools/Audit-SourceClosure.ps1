#requires -Version 5.1
# Audits the current MOD build inputs without staging, resetting, or modifying source.
# A live-worktree manifest is evidence, not a clean-checkout reproducibility proof.
[CmdletBinding()]
param(
    [string]$Root,
    [string]$ReportPath,
    [string]$VerifyReportPath,
    [switch]$RequireGitClosure
)

$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
$Root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
if (-not $ReportPath) { $ReportPath = Join-Path $Root 'artifacts\optimization-source-closure\audit.json' }
$tracked = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$dirty = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

function To-Relative([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "File is outside the primary MOD repository: $full"
    }
    return $full.Substring($Root.Length + 1).Replace('\', '/')
}
function Is-Generated([string]$Relative) {
    return ('/' + $Relative) -match '(?i)/(bin|obj|artifacts)/'
}

Push-Location $Root
try {
    foreach ($path in @(& git -c core.quotepath=false ls-files --cached)) {
        if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed' }
        [void]$tracked.Add(($path -replace '\\', '/'))
    }
    # Include BOTH index and worktree changes relative to HEAD. An input already
    # staged for another task is still not a clean, reproducible committed source.
    # Disabling only Git's CRLF warning does not change file content/status.
    foreach ($path in @(& git -c core.safecrlf=false -c core.quotepath=false diff HEAD --name-only)) {
        if ($LASTEXITCODE -ne 0) { throw 'git diff failed' }
        [void]$dirty.Add(($path -replace '\\', '/'))
    }

    $inputPaths = [Collections.Generic.List[string]]::new()
    foreach ($folder in @('src', 'tools', 'package', 'third_party')) {
        foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $Root $folder) -File -Recurse)) {
            $rel = To-Relative $file.FullName
            if (Is-Generated $rel -or $rel -match '^package/v_common/Assemblies/') { continue }
            $inputPaths.Add($file.FullName)
        }
    }
    foreach ($name in @('build.ps1', 'Install-Mod.ps1', 'Sync-ModCopies.ps1',
        'README.md', 'DEPENDENCIES.md', 'VERSION', 'CHANGELOG.md', 'Agent.md', 'AGENTS.md')) {
        $absolute = Join-Path $Root $name
        if (-not (Test-Path -LiteralPath $absolute -PathType Leaf)) { throw "Required source input missing: $name" }
        $inputPaths.Add($absolute)
    }

    $records = @($inputPaths | Sort-Object -Unique | ForEach-Object {
        $file = Get-Item -LiteralPath $_
        $rel = To-Relative $file.FullName
        [pscustomobject][ordered]@{
            Path = $rel
            SHA256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            Bytes = $file.Length
            Tracked = $tracked.Contains($rel)
            ModifiedFromHead = $dirty.Contains($rel)
        }
    })
    $canonical = ($records | ForEach-Object { $_.Path + ' ' + $_.SHA256 }) -join "`n"
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))).Replace('-', '') }
    finally { $sha.Dispose() }
    $untracked = @($records | Where-Object { -not $_.Tracked })
    $modified = @($records | Where-Object { $_.Tracked -and $_.ModifiedFromHead })
    $report = [pscustomobject][ordered]@{
        Schema = 1
        CapturedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        RepoRoot = $Root
        Head = (& git rev-parse HEAD)
        InputDigestSha256 = $digest
        CleanGitClosure = ($untracked.Count -eq 0 -and $modified.Count -eq 0)
        InputCount = $records.Count
        UntrackedInputCount = $untracked.Count
        ModifiedTrackedInputCount = $modified.Count
        Files = $records
    }
    if ($VerifyReportPath) {
        $prior = Get-Content -LiteralPath $VerifyReportPath -Encoding UTF8 -Raw | ConvertFrom-Json
        if ($prior.Schema -ne 1 -or $prior.InputCount -ne $report.InputCount -or
            $prior.InputDigestSha256 -ne $report.InputDigestSha256) {
            throw 'Source inputs have changed since the captured audit; do not reuse its BUILD-INFO evidence.'
        }
        $priorFiles = @($prior.Files)
        for ($i = 0; $i -lt $records.Count; ++$i) {
            if ($priorFiles[$i].Path -cne $records[$i].Path -or
                $priorFiles[$i].SHA256 -cne $records[$i].SHA256) {
                throw ('Source closure file changed or reordered: ' + $records[$i].Path)
            }
        }
        Write-Output ('VERIFIED_INPUT_DIGEST=' + $report.InputDigestSha256)
        if ($RequireGitClosure -and -not $report.CleanGitClosure) {
            throw 'Hash manifest verified, but Git source closure is not clean.'
        }
        return
    }
    $parent = Split-Path -Parent ([IO.Path]::GetFullPath($ReportPath))
    [void](New-Item -ItemType Directory -Path $parent -Force)
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($ReportPath),
        ($report | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Write-Output ('InputCount=' + $report.InputCount + ' Untracked=' + $untracked.Count +
        ' Modified=' + $modified.Count + ' CleanGitClosure=' + $report.CleanGitClosure)
    Write-Output ('InputDigestSha256=' + $digest)
    Write-Output ('AuditPath=' + [IO.Path]::GetFullPath($ReportPath))
    if ($RequireGitClosure -and -not $report.CleanGitClosure) {
        throw 'Git source closure is incomplete. Preserve other agents files and resolve ownership before release.'
    }
}
finally { Pop-Location }
