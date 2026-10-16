#requires -Version 7.0
# Synthetic fixtures exercise the comparator. They are NOT gameplay samples.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$fixtureName = [guid]::NewGuid().ToString('N')
$folder = Join-Path $root ('artifacts\optimization-profile-selftest\' + $fixtureName)
[void](New-Item -ItemType Directory -Path $folder -Force)
$compare = Join-Path $PSScriptRoot 'Compare-MultiplayerPerformance.ps1'
$manifestPath = Join-Path $folder 'experiment.json'
$manifest = [ordered]@{
    Schema = 1
    Scenario = [ordered]@{
        Id = 'synthetic-2d-tile-scan'
        WorldAlias = 'test-world-alias'
        Network = 'fixed-synthetic-local'
        Hardware = 'fixed-synthetic-machine'
        MapGrid = '128x128'
        HostPlatform = 'Steam'
        GuestPlatform = 'TapTap'
        Workload = 'synthetic-measurement-fixture-not-real-unity'
        Players = 2
        Entities = 20
    }
    Target = [ordered]@{ Role = 'host'; Stage = 'ground.capture.tiles' }
    Baseline = @()
    Candidate = @()
}

function Window([string]$role, [string]$stage, [int]$bucket) {
    $counts = [long[]]::new(14)
    $counts[$bucket] = 20
    return [ordered]@{
        Stage = $stage
        LogSide = $role
        HistogramSchema = 1
        HistogramBuckets = $counts
        Samples = 20
        MaxMs = if ($bucket -eq 8) { 45.5 } elseif ($bucket -eq 6) { 15.0 } else { 7.0 }
        P95UpperMs = if ($bucket -eq 8) { 50.0 } elseif ($bucket -eq 6) { 16.0 } else { 8.0 }
    }
}
foreach ($variant in @('Baseline', 'Candidate')) {
    $digest = if ($variant -eq 'Baseline') { 'A' * 64 } else { 'B' * 64 }
    for ($i = 1; $i -le 3; $i++) {
        $fileName = $variant.ToLowerInvariant() + '-' + $i + '.json'
        $relative = 'artifacts/optimization-profile-selftest/' + $fixtureName + '/' + $fileName
        $payload = [ordered]@{
            Schema = 1
            InputLogNames = @("host-$($i).log", "guest-$($i).log")
            PairEvidence = [ordered]@{
                HasBothVersionHeaders = $true
                SourceRevisionVerified = $true
                HostLoadedSourceRevision = $digest
                GuestLoadedSourceRevision = $digest
                RoleMismatchedEntries = 0
                SameRoomSessionVerified = $false
            }
            ProfileWindows = @(
                (Window 'host' 'ground.capture.tiles' $(if ($variant -eq 'Baseline') { 8 } else { 6 })),
                (Window 'host' 'ground.capture.entities' 5),
                (Window 'guest' 'transport.handle' 5)
            )
            GroundCaptureWindows = @([ordered]@{
                LogSide = 'host'; Captures = 20; EntitiesScanned = 400; EntityChanges = 4
                FieldsScanned = 100; TilesScanned = 327680; TileChanges = 3
                PosesSampled = 40; PoseChanges = 3
            })
        }
        [IO.File]::WriteAllText((Join-Path $folder $fileName), ($payload | ConvertTo-Json -Depth 12),
            [Text.UTF8Encoding]::new($false))
        $manifest[$variant] += [ordered]@{
            Round = $i
            PairedReport = $relative
            OperatorConfirmedSameSession = $true
            OperatorConfirmedSameConditions = $true
            GameplayCheck = 'pass'
        }
    }
}
function Write-Manifest {
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 12),
        [Text.UTF8Encoding]::new($false))
}
function MustReject([string]$reason) {
    $rejected = $false
    try { & $compare -ManifestPath $manifestPath -OutFile (Join-Path $folder ('reject-' + $reason + '.json')) | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw ('Comparator incorrectly accepted ' + $reason) }
    Write-Output ('PASS: comparator rejects ' + $reason)
}
Write-Manifest
$resultPath = Join-Path $folder 'comparison.json'
& $compare -ManifestPath $manifestPath -OutFile $resultPath | Out-Null
$result = Get-Content $resultPath -Raw | ConvertFrom-Json
if ($result.Status -ne 'PROFILE_GATE_MET_REQUIRES_RUNTIME_REVIEW' -or
    $result.TargetStageImprovementPercent -ne 68 -or
    @($result.Metrics).Count -ne 3 -or
    $result.BaselineRounds -ne 3 -or $result.CandidateRounds -ne 3) {
    throw 'Merged histogram P95 regression is incorrect.'
}
Write-Output 'PASS: six synthetic rounds merge 14 histogram buckets, 68% target improvement, no other regression'

$manifest.Candidate[0].OperatorConfirmedSameSession = $false
Write-Manifest; MustReject 'unconfirmed-room'
$manifest.Candidate[0].OperatorConfirmedSameSession = $true
$manifest.Candidate[0].GameplayCheck = 'unverified'
Write-Manifest; MustReject 'unverified-gameplay'
$manifest.Candidate[0].GameplayCheck = 'pass'
$manifest.Candidate = @($manifest.Candidate | Select-Object -First 2)
Write-Manifest; MustReject 'only-two-rounds'
$manifest.Candidate += [ordered]@{
    Round = 3
    PairedReport = 'artifacts/optimization-profile-selftest/' + $fixtureName + '/candidate-3.json'
    OperatorConfirmedSameSession = $true
    OperatorConfirmedSameConditions = $true
    GameplayCheck = 'pass'
}
Write-Manifest
$firstCandidate = Join-Path $folder 'candidate-1.json'
$original = [IO.File]::ReadAllText($firstCandidate)
$changed = $original | ConvertFrom-Json
$changed.ProfileWindows[0].HistogramBuckets[6] = 19
[IO.File]::WriteAllText($firstCandidate, ($changed | ConvertTo-Json -Depth 12),
    [Text.UTF8Encoding]::new($false))
MustReject 'corrupt-bucket-sum'
[IO.File]::WriteAllText($firstCandidate, $original, [Text.UTF8Encoding]::new($false))
$changed = $original | ConvertFrom-Json
$changed.ProfileWindows[0].HistogramSchema = 0
[IO.File]::WriteAllText($firstCandidate, ($changed | ConvertTo-Json -Depth 12),
    [Text.UTF8Encoding]::new($false))
MustReject 'legacy-histogram'
[IO.File]::WriteAllText($firstCandidate, $original, [Text.UTF8Encoding]::new($false))
$changed = $original | ConvertFrom-Json
$changed.PairEvidence.SourceRevisionVerified = $false
[IO.File]::WriteAllText($firstCandidate, ($changed | ConvertTo-Json -Depth 12),
    [Text.UTF8Encoding]::new($false))
MustReject 'unverified-loaded-source'
[IO.File]::WriteAllText($firstCandidate, $original, [Text.UTF8Encoding]::new($false))

$changed = $original | ConvertFrom-Json
$changed.ProfileWindows[0].P95UpperMs = 50.0
[IO.File]::WriteAllText($firstCandidate, ($changed | ConvertTo-Json -Depth 12),
    [Text.UTF8Encoding]::new($false))
MustReject 'histogram-p95-disagreement'
[IO.File]::WriteAllText($firstCandidate, $original, [Text.UTF8Encoding]::new($false))

$changed = $original | ConvertFrom-Json
$changed.ProfileWindows[0].HistogramBuckets[6] = 0
$changed.ProfileWindows[0].HistogramBuckets[8] = 20
$changed.ProfileWindows[0].MaxMs = 45.5
$changed.ProfileWindows[0].P95UpperMs = 50.0
[IO.File]::WriteAllText($firstCandidate, ($changed | ConvertTo-Json -Depth 12),
    [Text.UTF8Encoding]::new($false))
Write-Manifest
$notImproved = Join-Path $folder 'no-improvement.json'
& $compare -ManifestPath $manifestPath -OutFile $notImproved | Out-Null
$noImprovementResult = Get-Content $notImproved -Raw | ConvertFrom-Json
if ($noImprovementResult.Status -ne 'PROFILE_GATE_NOT_MET') {
    throw 'Missing 25% target gain was incorrectly accepted.'
}
Write-Output 'PASS: no target improvement never passes the 25% gate'
[IO.File]::WriteAllText($firstCandidate, $original, [Text.UTF8Encoding]::new($false))

$changed = $original | ConvertFrom-Json
$changed.ProfileWindows[1].HistogramBuckets[5] = 0
$changed.ProfileWindows[1].HistogramBuckets[6] = 20
$changed.ProfileWindows[1].P95UpperMs = 16.0
$changed.ProfileWindows[1].MaxMs = 15.0
[IO.File]::WriteAllText($firstCandidate, ($changed | ConvertTo-Json -Depth 12),
    [Text.UTF8Encoding]::new($false))
$regressedPath = Join-Path $folder 'regression.json'
& $compare -ManifestPath $manifestPath -OutFile $regressedPath | Out-Null
if ((Get-Content $regressedPath -Raw | ConvertFrom-Json).Status -ne 'PROFILE_GATE_NOT_MET') {
    throw 'Other stage P95 regression was incorrectly accepted.'
}
Write-Output 'PASS: a non-target stage 100% regression blocks the performance gate'
[IO.File]::WriteAllText($firstCandidate, $original, [Text.UTF8Encoding]::new($false))

$changed = $original | ConvertFrom-Json
$changed.PairEvidence.HostLoadedSourceRevision = 'A' * 64
$changed.PairEvidence.GuestLoadedSourceRevision = 'A' * 64
[IO.File]::WriteAllText($firstCandidate, ($changed | ConvertTo-Json -Depth 12),
    [Text.UTF8Encoding]::new($false))
MustReject 'mixed-revisions-within-candidate'
[IO.File]::WriteAllText($firstCandidate, $original, [Text.UTF8Encoding]::new($false))
Write-Output ('SYNTHETIC_TEST_FIXTURE=' + $folder)
