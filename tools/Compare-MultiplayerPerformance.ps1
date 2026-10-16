#requires -Version 7.0
# Compare three or more independently paired host/guest gameplay runs.
# Reads only sanitized Analyze-MultiplayerDiagnostics JSON reports and
# explicit operator attestations. Never reads saves, identity files or chats.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [string]$OutFile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Identical to PerformanceWindow histogram schema 1: 13 finite upper bounds
# and a single overflow bucket whose actual MaxMs bounds the observed tail.
$bounds = [double[]]@(0.25, 0.5, 1, 2, 4, 8, 16, 33, 50, 100, 250, 500, 1000)
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts = Join-Path $root 'artifacts'

function Require([bool]$condition, [string]$reason) {
    if (-not $condition) { throw "INCOMPLETE_PROFILE_EVIDENCE: $reason" }
}
function ValidDigest([string]$value) {
    return $null -ne $value -and $value -match '^[0-9a-fA-F]{64}$'
}
function Aggregate-Runs([object[]]$runs, [string]$variant) {
    Require ($runs.Count -ge 3) "$variant needs at least three independent rounds"
    $rounds = [Collections.Generic.HashSet[int]]::new()
    $revisions = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $groups = @{}
    foreach ($run in $runs) {
        Require ($null -ne $run.Round -and [int]$run.Round -gt 0) "$variant has an invalid round number"
        $round = [int]$run.Round
        Require ($rounds.Add($round)) "$variant repeats round $round"
        Require ($run.OperatorConfirmedSameSession -eq $true) "$variant/$round lacks a manual same-room confirmation"
        Require ($run.OperatorConfirmedSameConditions -eq $true) "$variant/$round lacks conditions confirmation"
        Require ($run.GameplayCheck -eq 'pass') "$variant/$round has unverified gameplay correctness"
        Require (-not [string]::IsNullOrWhiteSpace($run.PairedReport)) "$variant/$round has no paired diagnostic report"

        $reportPath = [IO.Path]::GetFullPath((Join-Path $root ([string]$run.PairedReport)))
        Require ($reportPath.StartsWith($artifacts + '\', [StringComparison]::OrdinalIgnoreCase)) "$variant/$round report must be under ignored artifacts/"
        Require (Test-Path -LiteralPath $reportPath -PathType Leaf) "$variant/$round paired report does not exist"
        Require ((Get-Item -LiteralPath $reportPath).Length -le 16MB) "$variant/$round report is unexpectedly large"
        $report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Require ($report.Schema -eq 1 -and $null -ne $report.PairEvidence) "$variant/$round has incompatible report schema"
        $pair = $report.PairEvidence
        Require ($pair.HasBothVersionHeaders -eq $true) "$variant/$round lacks both version headers"
        Require ($pair.SourceRevisionVerified -eq $true) "$variant/$round did not prove identical loaded DLL sources"
        Require ($pair.RoleMismatchedEntries -eq 0) "$variant/$round contains mismatched host/guest role data"
        Require ((ValidDigest $pair.HostLoadedSourceRevision) -and
                 (ValidDigest $pair.GuestLoadedSourceRevision) -and
                 [string]::Equals($pair.HostLoadedSourceRevision, $pair.GuestLoadedSourceRevision,
                    [StringComparison]::OrdinalIgnoreCase)) "$variant/$round source fingerprints are inconsistent"
        Require (@($report.InputLogNames).Count -eq 2) "$variant/$round lacks two separate log files"
        Require ($report.InputLogNames[0] -ne $report.InputLogNames[1]) "$variant/$round used one log for both sides"
        [void]$revisions.Add([string]$pair.HostLoadedSourceRevision)
        Require (@($report.ProfileWindows).Count -gt 0) "$variant/$round contains no timed profile samples"
        $present = @{}
        foreach ($sample in @($report.ProfileWindows)) {
            $role = [string]$sample.LogSide
            $stage = [string]$sample.Stage
            Require ($role -in @('host', 'guest') -and
                $stage -match '^[a-z0-9.]+$') "$variant/$round has invalid stage or role"
            $key = $role + '/' + $stage
            $counts = @($sample.HistogramBuckets)
            Require ($sample.HistogramSchema -eq 1 -and $counts.Count -eq 14) "$variant/$round $key lacks mergeable histogram v1 buckets"
            $sum = [long]0
            foreach ($count in $counts) {
                Require ($count -ge 0 -and $count -le 100000000) "$variant/$round $key has an invalid bucket"
                $sum += [long]$count
            }
            Require ($sum -eq [long]$sample.Samples -and $sum -gt 0) "$variant/$round $key bucket count differs from recorded samples"
            Require ([double]$sample.MaxMs -ge 0 -and
                -not [double]::IsNaN([double]$sample.MaxMs) -and
                -not [double]::IsInfinity([double]$sample.MaxMs)) "$variant/$round $key has invalid maximum time"
            # Detect tampered, truncated or mispaired profile summaries. A v1
            # bin count must reproduce the logged percentile of that window.
            $rank = [long][Math]::Ceiling($sum * 0.95)
            $seen = [long]0
            $loggedP95 = [double]::NaN
            for ($i = 0; $i -lt 14; $i++) {
                $seen += [long]$counts[$i]
                if ($seen -ge $rank) {
                    $loggedP95 = if ($i -eq 13) { [double]$sample.MaxMs } else { $bounds[$i] }
                    break
                }
            }
            Require ([Math]::Abs($loggedP95 - [double]$sample.P95UpperMs) -le 0.02) "$variant/$round $key histogram disagrees with its P95 summary"
            if (-not $groups.ContainsKey($key)) {
                $groups[$key] = @{
                    Counts = [long[]]::new(14)
                    Samples = [long]0
                    MaxMs = [double]0
                    PerRoundSamples = @{}
                    Windows = [long]0
                }
            }
            $g = $groups[$key]
            for ($i = 0; $i -lt 14; $i++) { $g.Counts[$i] += [long]$counts[$i] }
            $g.Samples += $sum
            $g.MaxMs = [Math]::Max($g.MaxMs, [double]$sample.MaxMs)
            $g.Windows++
            if (-not $g.PerRoundSamples.ContainsKey($round)) { $g.PerRoundSamples[$round] = [long]0 }
            $g.PerRoundSamples[$round] += $sum
            $present[$key] = $true
        }
        Require ($present.Count -gt 0) "$variant/$round has no valid profiling stages"
    }
    Require ($revisions.Count -eq 1) "$variant rounds use different loaded source revisions"
    foreach ($key in @($groups.Keys)) {
        $g = $groups[$key]
        Require ($g.PerRoundSamples.Count -eq $rounds.Count) "$variant $key was not captured in every round"
        $perRound = @($g.PerRoundSamples.Values | Sort-Object)
        Require ($perRound[0] -ge 5) "$variant $key has fewer than five samples in a round"
        Require ($perRound[-1] -le 2 * $perRound[0]) "$variant $key sample volumes vary more than 2x; fix the scenario"
    }
    return [pscustomobject]@{
        Groups = $groups
        Rounds = $rounds.Count
        SourceRevision = @($revisions)[0].ToUpperInvariant()
    }
}

function Get-PooledUpper([hashtable]$group, [int]$percentile) {
    $target = [long][Math]::Ceiling($group.Samples * ($percentile / 100.0))
    $seen = [long]0
    for ($i = 0; $i -lt 14; $i++) {
        $seen += $group.Counts[$i]
        if ($seen -ge $target) {
            if ($i -lt 13) { return [double]$bounds[$i] }
            return [double]$group.MaxMs
        }
    }
    throw 'Histogram merge produced an inconsistent percentile.'
}

$fullManifest = [IO.Path]::GetFullPath($ManifestPath)
Require (Test-Path -LiteralPath $fullManifest -PathType Leaf) 'Manifest file does not exist'
$manifest = Get-Content -LiteralPath $fullManifest -Raw -Encoding UTF8 | ConvertFrom-Json
Require ($manifest.Schema -eq 1 -and $null -ne $manifest.Scenario -and $null -ne $manifest.Target) 'Invalid comparison manifest schema'
$scenario = $manifest.Scenario
foreach ($property in @('Id', 'WorldAlias', 'Network', 'Hardware', 'MapGrid', 'HostPlatform', 'GuestPlatform', 'Workload')) {
    Require (-not [string]::IsNullOrWhiteSpace([string]$scenario.$property)) "Scenario needs $property"
}
Require ($scenario.Players -ge 2 -and $scenario.Players -le 4 -and $scenario.Entities -gt 0) 'Scenario player/entity count is invalid'
$targetRole = [string]$manifest.Target.Role
$targetStage = [string]$manifest.Target.Stage
Require ($targetRole -in @('host','guest') -and $targetStage -match '^[a-z0-9.]+$') 'Invalid target role/stage'
$targetKey = $targetRole + '/' + $targetStage

$before = Aggregate-Runs @($manifest.Baseline) 'baseline'
$after = Aggregate-Runs @($manifest.Candidate) 'candidate'
Require ($before.SourceRevision -ne $after.SourceRevision) 'Baseline and candidate have identical loaded source revisions; no code change can be attributed'
Require ($before.Groups.ContainsKey($targetKey) -and $after.Groups.ContainsKey($targetKey)) 'Target stage/role is not sampled in both versions'
$oldStages = @($before.Groups.Keys | Sort-Object)
$newStages = @($after.Groups.Keys | Sort-Object)
Require ($oldStages.Count -ge 2 -and $oldStages.Count -eq $newStages.Count) 'Missing regression stages or incomparable stage coverage'
for ($i = 0; $i -lt $oldStages.Count; $i++) {
    Require ($oldStages[$i] -ceq $newStages[$i]) "Stage coverage changed between source revisions"
}

$metrics = @($oldStages | ForEach-Object {
    $key = $_
    $a = $before.Groups[$key]
    $b = $after.Groups[$key]
    $old = Get-PooledUpper $a 95
    $new = Get-PooledUpper $b 95
    $ratio = if ($old -eq 0) { if ($new -eq 0) { 1.0 } else { [double]::PositiveInfinity } }
        else { $new / $old }
    [pscustomobject][ordered]@{
        StageRole = $key
        BaselineP95UpperMs = $old
        CandidateP95UpperMs = $new
        ImprovementPercent = if ([double]::IsPositiveInfinity($ratio)) { -1000000 }
            else { [Math]::Round((1 - $ratio) * 100.0, 1) }
        BaselineSamples = $a.Samples
        CandidateSamples = $b.Samples
        Target = ($key -eq $targetKey)
        MeetsStageThreshold = if ($key -eq $targetKey) { $old -gt 0 -and $ratio -le 0.75 }
            else { $ratio -le 1.10 }
    }
})
$target = @($metrics | Where-Object { $_.Target })[0]
$passing = @($metrics | Where-Object { -not $_.MeetsStageThreshold }).Count -eq 0
$report = [pscustomobject][ordered]@{
    Schema = 1
    Status = if ($passing) { 'PROFILE_GATE_MET_REQUIRES_RUNTIME_REVIEW' } else { 'PROFILE_GATE_NOT_MET' }
    Scenario = $scenario
    Target = $manifest.Target
    BaselineRevision = $before.SourceRevision
    CandidateRevision = $after.SourceRevision
    BaselineRounds = $before.Rounds
    CandidateRounds = $after.Rounds
    TargetStageImprovementPercent = $target.ImprovementPercent
    Metrics = @($metrics)
    Caveats = @(
        'P95 is a merged fixed-bucket upper bound, not a raw-sample percentile. The >1000ms bucket uses the observed maximum.',
        'Scenario, room identity and gameplay outcomes are operator-confirmed, not independently verified by these JSON reports.',
        'Passing histogram thresholds does not certify loaded Unity behavior, 2D simulation, assets, save recovery or releases.'
    )
}
$json = $report | ConvertTo-Json -Depth 10
if ($OutFile) {
    $path = [IO.Path]::GetFullPath($OutFile)
    Require ($path.StartsWith($artifacts + '\', [StringComparison]::OrdinalIgnoreCase)) 'Output report must be inside ignored artifacts/'
    Require (-not (Test-Path -LiteralPath $path)) 'Refusing to overwrite an existing comparison report'
    [void](New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force)
    [IO.File]::WriteAllText($path, $json, [Text.UTF8Encoding]::new($false))
    Write-Output ('STATUS=' + $report.Status + ' TARGET_IMPROVEMENT=' + $report.TargetStageImprovementPercent + '%')
    Write-Output ('COMPARE_REPORT=' + $path)
}
else { Write-Output $json }
