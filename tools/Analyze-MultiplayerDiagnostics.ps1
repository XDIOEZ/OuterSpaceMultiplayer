#requires -Version 5.1
# Correlates existing fixed-stage multiplayer diagnostics without reading or
# exporting player credentials, chat text, or raw multiplayer payloads.
[CmdletBinding()]
param(
    [string]$HostLog,
    [string]$GuestLog,
    [string]$OutFile,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'

function New-DiagnosticsState {
    return @{
        Commands = @{}
        Slow = [Collections.Generic.List[object]]::new()
        Profile = [Collections.Generic.List[object]]::new()
        GroundCounts = [Collections.Generic.List[object]]::new()
        InputFiles = [Collections.Generic.List[string]]::new()
        ModVersions = @{}
        AssemblyVersions = @{}
        SourceRevisions = @{}
        RoleMismatchedEntries = 0
    }
}

function Read-DiagnosticLines([object]$state, [string[]]$lines, [string]$sourceSide = 'unknown') {
    foreach ($line in $lines) {
        if ($sourceSide -ne 'unknown') {
            $header = [regex]::Match($line, '^\s*ModVersion:\s*(\S+)')
            if ($header.Success) { $state.ModVersions[$sourceSide] = $header.Groups[1].Value; continue }
            $header = [regex]::Match($line, '^\s*AssemblyVersion:\s*(\S+)')
            if ($header.Success) { $state.AssemblyVersions[$sourceSide] = $header.Groups[1].Value; continue }
            $header = [regex]::Match($line, '^\s*SourceRevision:\s*(\S+)')
            if ($header.Success) { $state.SourceRevisions[$sourceSide] = $header.Groups[1].Value; continue }
        }
        $match = [regex]::Match($line,
            '\[FreeWorldMultiplayer\]\[fleet-command\]\s+stage=(\S+)\s+command=(\S+)\s+fleet=(-?\d+)\s+role=(host|guest)\s+outcome=(\S+)')
        if ($match.Success) {
            if ($sourceSide -ne 'unknown' -and $match.Groups[4].Value -ne $sourceSide) {
                $state.RoleMismatchedEntries++
                continue
            }
            $id = $match.Groups[2].Value
            if ($id -eq 'pending' -or $id.Length -gt 128) { continue }
            if (-not $state.Commands.ContainsKey($id)) {
                $state.Commands.Add($id, [Collections.Generic.List[object]]::new())
            }
            if ($state.Commands[$id].Count -ge 64) { continue }
            $state.Commands[$id].Add([pscustomobject]@{
                Stage = $match.Groups[1].Value
                Role = $match.Groups[4].Value
                LogSide = $sourceSide
                Outcome = $match.Groups[5].Value
            })
            continue
        }
        $match = [regex]::Match($line,
            '\[FreeWorldMultiplayer\]\[slow-operation\]\s+stage=([a-z0-9.]+)\s+ms=([\d.]+)\s+bytes=(-?\d+)\s+map=-?\d+\s+role=(host|guest)')
        if ($match.Success) {
            if ($state.Slow.Count -lt 10000) {
                $state.Slow.Add([pscustomobject]@{
                    Stage = $match.Groups[1].Value
                    Milliseconds = [double]::Parse($match.Groups[2].Value, [Globalization.CultureInfo]::InvariantCulture)
                    Bytes = [long]$match.Groups[3].Value
                    Role = $match.Groups[4].Value
                    LogSide = $sourceSide
                })
            }
            continue
        }
        $match = [regex]::Match($line,
            '\[FreeWorldMultiplayer\]\[profile\]\s+stage=([a-z0-9.]+)\s+windowSeconds=([\d.]+)\s+count=(\d+)\s+p50UpperMs=([\d.]+)\s+p95UpperMs=([\d.]+)\s+p99UpperMs=([\d.]+)\s+maxMs=([\d.]+)')
        if ($match.Success -and $state.Profile.Count -lt 10000) {
            $histogram = [regex]::Match($line, '(?:^|\s)histV=(\d+)\s+bins=(\d+(?:,\d+)*)(?:\s|$)')
            $bucketCounts = @()
            $histogramVersion = 0
            if ($histogram.Success) {
                $histogramVersion = [int]$histogram.Groups[1].Value
                # Preserve each of the fourteen bucket counts as integer data.
                # Missing/old/malformed metadata is never reconstructed from P95.
                foreach ($value in $histogram.Groups[2].Value.Split(',')) {
                    $bucketCounts += [long]::Parse($value, [Globalization.CultureInfo]::InvariantCulture)
                }
            }
            $state.Profile.Add([pscustomobject]@{
                Stage = $match.Groups[1].Value
                LogSide = $sourceSide
                WindowSeconds = [double]::Parse($match.Groups[2].Value, [Globalization.CultureInfo]::InvariantCulture)
                Samples = [long]$match.Groups[3].Value
                P50UpperMs = [double]::Parse($match.Groups[4].Value, [Globalization.CultureInfo]::InvariantCulture)
                P95UpperMs = [double]::Parse($match.Groups[5].Value, [Globalization.CultureInfo]::InvariantCulture)
                P99UpperMs = [double]::Parse($match.Groups[6].Value, [Globalization.CultureInfo]::InvariantCulture)
                MaxMs = [double]::Parse($match.Groups[7].Value, [Globalization.CultureInfo]::InvariantCulture)
                HistogramSchema = $histogramVersion
                HistogramBuckets = @($bucketCounts)
            })
            continue
        }
        $match = [regex]::Match($line,
            '\[FreeWorldMultiplayer\]\[ground-counts\]\s+captures=(\d+)\s+entitiesScanned=(\d+)\s+entityChanges=(\d+)\s+fieldsScanned=(\d+)\s+tilesScanned=(\d+)\s+tileChanges=(\d+)\s+posesSampled=(\d+)\s+poseChanges=(\d+)')
        if ($match.Success -and $state.GroundCounts.Count -lt 10000) {
            $state.GroundCounts.Add([pscustomobject]@{
                LogSide = $sourceSide
                Captures = [long]$match.Groups[1].Value
                EntitiesScanned = [long]$match.Groups[2].Value
                EntityChanges = [long]$match.Groups[3].Value
                FieldsScanned = [long]$match.Groups[4].Value
                TilesScanned = [long]$match.Groups[5].Value
                TileChanges = [long]$match.Groups[6].Value
                PosesSampled = [long]$match.Groups[7].Value
                PoseChanges = [long]$match.Groups[8].Value
            })
        }
    }
}

function Assert-PairedHeaders([object]$state) {
    foreach ($name in @('ModVersions', 'AssemblyVersions')) {
        $versions = $state[$name]
        if ($versions.ContainsKey('host') -and $versions.ContainsKey('guest') -and
            $versions['host'] -ne $versions['guest']) {
            throw "Host/guest $name differ; these logs cannot be used as matching runtime evidence."
        }
    }
    $source = $state.SourceRevisions
    if ($source.ContainsKey('host') -and $source.ContainsKey('guest') -and
        $source['host'] -match '^[A-Fa-f0-9]{64}$' -and
        $source['guest'] -match '^[A-Fa-f0-9]{64}$' -and
        -not [string]::Equals($source['host'], $source['guest'], [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Loaded host/guest SourceRevision differ; do not combine incompatible runtime evidence.'
    }
}

function Summarize-Diagnostics([object]$state) {
    $revisions = $state.SourceRevisions
    $verifiedRevision = $revisions.ContainsKey('host') -and $revisions.ContainsKey('guest') -and
        $revisions['host'] -match '^[A-Fa-f0-9]{64}$' -and
        $revisions['guest'] -match '^[A-Fa-f0-9]{64}$' -and
        [string]::Equals($revisions['host'], $revisions['guest'], [StringComparison]::OrdinalIgnoreCase)
    $commands = @($state.Commands.Keys | Sort-Object | ForEach-Object {
        $commandId = $_
        $events = @($state.Commands[$commandId])
        $guest = @($events | Where-Object { $_.Role -eq 'guest' } | ForEach-Object { $_.Stage } | Select-Object -Unique)
        $hostStages = @($events | Where-Object { $_.Role -eq 'host' } | ForEach-Object { $_.Stage } | Select-Object -Unique)
        $outcomes = @($events | Where-Object { $_.Outcome -eq 'rejected' })
        $diagnosis = 'insufficient-observation'
        if ($outcomes.Count -gt 0) { $diagnosis = 'explicit-rejection' }
        elseif ('client.sent' -in $guest -and 'host.received' -notin $hostStages) {
            $diagnosis = 'sent-but-host-receive-not-observed'
        }
        elseif ('host.native.begin' -in $hostStages -and 'host.native.end' -notin $hostStages) {
            $diagnosis = 'host-native-completion-not-observed'
        }
        elseif ('host.result' -in $hostStages -and 'client.result' -notin $guest) {
            $diagnosis = 'host-result-but-client-receipt-not-observed'
        }
        elseif ('host.result' -in $hostStages -and 'client.result' -in $guest) {
            $motion = @($events | Where-Object { $_.Role -eq 'host' -and $_.Stage -eq 'host.motion' })
            if (@($motion | Where-Object { $_.Outcome -eq 'position-changed' }).Count -gt 0) {
                $diagnosis = 'command-acknowledged-host-position-changed'
            }
            elseif (@($motion | Where-Object { $_.Outcome -eq 'unchanged-after-120-host-ticks' }).Count -gt 0) {
                $diagnosis = 'command-acknowledged-no-position-change-in-120-host-ticks'
            }
            else { $diagnosis = 'command-acknowledged-motion-not-measured' }
        }
        [pscustomobject]@{
            CommandId = $commandId
            GuestStages = $guest
            HostStages = $hostStages
            Outcome = $diagnosis
        }
    })
    $slowGroups = @($state.Slow | Group-Object -Property Role, Stage | ForEach-Object {
        $items = @($_.Group)
        $ms = @($items | Measure-Object -Property Milliseconds -Maximum)
        [pscustomobject]@{
            RoleStage = $_.Name
            CountSlowAbove50Ms = $items.Count
            MaxSlowMs = [double]$ms[0].Maximum
            MaxMessageBytes = [long]($items | Measure-Object -Property Bytes -Maximum).Maximum
        }
    } | Sort-Object -Property MaxSlowMs -Descending)
    return [pscustomobject][ordered]@{
        Schema = 1
        InputLogNames = @($state.InputFiles)
        PairEvidence = [pscustomobject]@{
            HostModVersion = $state.ModVersions['host']
            GuestModVersion = $state.ModVersions['guest']
            HostAssemblyVersion = $state.AssemblyVersions['host']
            GuestAssemblyVersion = $state.AssemblyVersions['guest']
            HostLoadedSourceRevision = $revisions['host']
            GuestLoadedSourceRevision = $revisions['guest']
            RoleMismatchedEntries = $state.RoleMismatchedEntries
            HasBothVersionHeaders = $state.ModVersions.ContainsKey('host') -and $state.ModVersions.ContainsKey('guest')
            SourceRevisionVerified = [bool]$verifiedRevision
            SameRoomSessionVerified = $false
            Notice = 'Identical loaded assembly fingerprints prove matching source inputs, not the same room session, player identity, transport health or gameplay success.'
        }
        FleetCommandsSeen = $commands.Count
        FleetCommands = $commands
        SlowOperationGroups = $slowGroups
        ProfileWindows = @($state.Profile)
        GroundCaptureWindows = @($state.GroundCounts)
        MissingEvidence = @(
            if (-not $verifiedRevision) { 'No matching loaded DLL SourceRevision in both diagnostic headers: runtime source identity unverified.' }
            if ($commands.Count -eq 0) { 'No correlated fleet-command evidence: cannot diagnose movement.' }
            if ($state.Profile.Count -eq 0) { 'No opt-in profile windows: cannot calculate a gameplay P95.' }
            elseif (@($state.Profile | Where-Object { $_.HistogramSchema -ne 1 -or @($_.HistogramBuckets).Count -ne 14 }).Count -gt 0) {
                'Some profile windows have no mergeable 14-bucket histogram; cannot pool their P95 across runs.'
            }
            if ($state.GroundCounts.Count -eq 0) { 'No 2D ground-counts data: scan/change hot spots unverified.' }
        )
        Interpretation = 'Slow-operation records include only throttled >=50 ms events; they cannot yield an overall P95. Profile values are histogram bucket upper bounds; ground counts distinguish scanned from changed entities, 2D poses and tiles, not network bytes. A command ACK is not proof of physical fleet motion.'
    }
}

if ($SelfTest) {
    $state = New-DiagnosticsState
    $testRevision = 'A' * 64
    Read-DiagnosticLines $state @(
        'ModVersion: V1.19',
        'AssemblyVersion: 1.19.0.0',
        ('SourceRevision: ' + $testRevision),
        '[FreeWorldMultiplayer][fleet-command] stage=host.received command=cmdABC fleet=123 role=host outcome=queued',
        '[FreeWorldMultiplayer][fleet-command] stage=host.native.begin command=cmdABC fleet=123 role=host outcome=begin',
        '[FreeWorldMultiplayer][fleet-command] stage=host.native.end command=cmdABC fleet=123 role=host outcome=accepted',
        '[FreeWorldMultiplayer][fleet-command] stage=host.result command=cmdABC fleet=123 role=host outcome=accepted',
        '[FreeWorldMultiplayer][fleet-command] stage=host.motion.armed command=cmdABC fleet=123 role=host outcome=awaiting-host-tick',
        '[FreeWorldMultiplayer][fleet-command] stage=host.motion command=cmdABC fleet=123 role=host outcome=position-changed',
        '[FreeWorldMultiplayer][fleet-command] stage=host.received command=cmdStill fleet=444 role=host outcome=queued',
        '[FreeWorldMultiplayer][fleet-command] stage=host.native.end command=cmdStill fleet=444 role=host outcome=accepted',
        '[FreeWorldMultiplayer][fleet-command] stage=host.result command=cmdStill fleet=444 role=host outcome=accepted',
        '[FreeWorldMultiplayer][fleet-command] stage=host.motion command=cmdStill fleet=444 role=host outcome=unchanged-after-120-host-ticks'
    ) 'host'
    Read-DiagnosticLines $state @(
        'ModVersion: V1.19',
        'AssemblyVersion: 1.19.0.0',
        ('SourceRevision: ' + $testRevision),
        '[FreeWorldMultiplayer][fleet-command] stage=client.sent command=cmdABC fleet=123 role=guest outcome=queued',
        '[FreeWorldMultiplayer][fleet-command] stage=client.result command=cmdABC fleet=123 role=guest outcome=accepted',
        '[FreeWorldMultiplayer][fleet-command] stage=client.sent command=cmdStill fleet=444 role=guest outcome=queued',
        '[FreeWorldMultiplayer][fleet-command] stage=client.result command=cmdStill fleet=444 role=guest outcome=accepted',
        '[FreeWorldMultiplayer][slow-operation] stage=transport.handle ms=6943.3 bytes=20955155 map=-1 role=guest',
        '[FreeWorldMultiplayer][profile] stage=map.cache.apply windowSeconds=30.0 count=8 p50UpperMs=16.00 p95UpperMs=100.00 p99UpperMs=100.00 maxMs=81.25 histV=1 bins=0,1,0,0,0,1,2,2,1,1,0,0,0,0',
        '[FreeWorldMultiplayer][profile] stage=ground.capture.tiles windowSeconds=30.0 count=5 p50UpperMs=4.00 p95UpperMs=16.00 p99UpperMs=16.00 maxMs=14.5 histV=1 bins=1,0,0,1,1,1,1,0,0,0,0,0,0,0',
        '[FreeWorldMultiplayer][ground-counts] captures=5 entitiesScanned=500 entityChanges=2 fieldsScanned=100 tilesScanned=327680 tileChanges=3 posesSampled=40 poseChanges=7'
    ) 'guest'
    Assert-PairedHeaders $state
    $summary = Summarize-Diagnostics $state
    if ($summary.FleetCommandsSeen -ne 2 -or
        @($summary.FleetCommands | Where-Object { $_.CommandId -eq 'cmdABC' -and
            $_.Outcome -eq 'command-acknowledged-host-position-changed' }).Count -ne 1 -or
        @($summary.FleetCommands | Where-Object { $_.CommandId -eq 'cmdStill' -and
            $_.Outcome -eq 'command-acknowledged-no-position-change-in-120-host-ticks' }).Count -ne 1 -or
        $summary.SlowOperationGroups[0].MaxSlowMs -ne 6943.3 -or
        $summary.ProfileWindows[0].P95UpperMs -ne 100 -or
        $summary.ProfileWindows[0].HistogramSchema -ne 1 -or
        @($summary.ProfileWindows[0].HistogramBuckets).Count -ne 14 -or
        (@($summary.ProfileWindows[0].HistogramBuckets) | Measure-Object -Sum).Sum -ne 8 -or
        $summary.ProfileWindows[1].Stage -ne 'ground.capture.tiles' -or
        $summary.GroundCaptureWindows.Count -ne 1 -or
        $summary.GroundCaptureWindows[0].TilesScanned -ne 327680 -or
        $summary.GroundCaptureWindows[0].TileChanges -ne 3 -or
        -not $summary.PairEvidence.HasBothVersionHeaders -or
        -not $summary.PairEvidence.SourceRevisionVerified -or
        $summary.PairEvidence.SameRoomSessionVerified -or
        $summary.ProfileWindows[0].LogSide -ne 'guest') {
        throw 'Multiplayer diagnostics analyzer self-test failed.'
    }
    $state.ModVersions['guest'] = 'V1.18'
    $rejected = $false
    try { Assert-PairedHeaders $state }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Mismatched host/guest versions must be rejected.' }
    $state.ModVersions['guest'] = 'V1.19'
    $state.SourceRevisions['guest'] = 'B' * 64
    $rejected = $false
    try { Assert-PairedHeaders $state }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Mismatched loaded DLL source revisions must be rejected.' }
    $state.SourceRevisions.Remove('guest')
    Assert-PairedHeaders $state
    if ((Summarize-Diagnostics $state).PairEvidence.SourceRevisionVerified) {
        throw 'Missing runtime source fingerprint must never be reported as verified.'
    }
    Write-Output 'PASS: fleet command stage correlation, slow-operation and profile parsing'
    return
}

if (-not $HostLog -or -not $GuestLog) {
    throw 'Pass both -HostLog and -GuestLog from one multiplayer session, or use -SelfTest.'
}
$state = New-DiagnosticsState
$hostFull = [IO.Path]::GetFullPath($HostLog)
$guestFull = [IO.Path]::GetFullPath($GuestLog)
if ([string]::Equals($hostFull, $guestFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'HostLog and GuestLog must be different files. One log cannot prove paired evidence.'
}
foreach ($inputFile in @(
    [pscustomobject]@{ Path = $hostFull; Side = 'host' },
    [pscustomobject]@{ Path = $guestFull; Side = 'guest' }
)) {
    $path = $inputFile.Path
    $full = [IO.Path]::GetFullPath($path)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Missing log file: $full" }
    $state.InputFiles.Add([IO.Path]::GetFileName($full))
    Read-DiagnosticLines $state @(Get-Content -LiteralPath $full -Encoding UTF8) $inputFile.Side
}
Assert-PairedHeaders $state
$report = Summarize-Diagnostics $state
if ($OutFile) {
    $output = [IO.Path]::GetFullPath($OutFile)
    [void](New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force)
    [IO.File]::WriteAllText($output, ($report | ConvertTo-Json -Depth 7), [Text.UTF8Encoding]::new($false))
    Write-Output ('ReportPath=' + $output)
    Write-Output ('FleetCommandsSeen=' + $report.FleetCommandsSeen + ' ProfileWindows=' + $report.ProfileWindows.Count)
}
else { $report | ConvertTo-Json -Depth 7 }
