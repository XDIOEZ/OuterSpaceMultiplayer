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
    }
}

function Read-DiagnosticLines([object]$state, [string[]]$lines) {
    foreach ($line in $lines) {
        $match = [regex]::Match($line,
            '\[FreeWorldMultiplayer\]\[fleet-command\]\s+stage=(\S+)\s+command=(\S+)\s+fleet=(-?\d+)\s+role=(host|guest)\s+outcome=(\S+)')
        if ($match.Success) {
            $id = $match.Groups[2].Value
            if ($id -eq 'pending' -or $id.Length -gt 128) { continue }
            if (-not $state.Commands.ContainsKey($id)) {
                $state.Commands.Add($id, [Collections.Generic.List[object]]::new())
            }
            if ($state.Commands[$id].Count -ge 64) { continue }
            $state.Commands[$id].Add([pscustomobject]@{
                Stage = $match.Groups[1].Value
                Role = $match.Groups[4].Value
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
                })
            }
            continue
        }
        $match = [regex]::Match($line,
            '\[FreeWorldMultiplayer\]\[profile\]\s+stage=([a-z0-9.]+)\s+windowSeconds=([\d.]+)\s+count=(\d+)\s+p50UpperMs=([\d.]+)\s+p95UpperMs=([\d.]+)\s+p99UpperMs=([\d.]+)\s+maxMs=([\d.]+)')
        if ($match.Success -and $state.Profile.Count -lt 10000) {
            $state.Profile.Add([pscustomobject]@{
                Stage = $match.Groups[1].Value
                WindowSeconds = [double]::Parse($match.Groups[2].Value, [Globalization.CultureInfo]::InvariantCulture)
                Samples = [long]$match.Groups[3].Value
                P50UpperMs = [double]::Parse($match.Groups[4].Value, [Globalization.CultureInfo]::InvariantCulture)
                P95UpperMs = [double]::Parse($match.Groups[5].Value, [Globalization.CultureInfo]::InvariantCulture)
                P99UpperMs = [double]::Parse($match.Groups[6].Value, [Globalization.CultureInfo]::InvariantCulture)
                MaxMs = [double]::Parse($match.Groups[7].Value, [Globalization.CultureInfo]::InvariantCulture)
            })
            continue
        }
        $match = [regex]::Match($line,
            '\[FreeWorldMultiplayer\]\[ground-counts\]\s+captures=(\d+)\s+entitiesScanned=(\d+)\s+entityChanges=(\d+)\s+fieldsScanned=(\d+)\s+tilesScanned=(\d+)\s+tileChanges=(\d+)\s+posesSampled=(\d+)\s+poseChanges=(\d+)')
        if ($match.Success -and $state.GroundCounts.Count -lt 10000) {
            $state.GroundCounts.Add([pscustomobject]@{
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

function Summarize-Diagnostics([object]$state) {
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
            $diagnosis = 'command-acknowledged-motion-not-measured'
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
        FleetCommandsSeen = $commands.Count
        FleetCommands = $commands
        SlowOperationGroups = $slowGroups
        ProfileWindows = @($state.Profile)
        GroundCaptureWindows = @($state.GroundCounts)
        Interpretation = 'Slow-operation records include only throttled >=50 ms events; they cannot yield an overall P95. Profile values are histogram bucket upper bounds; ground counts distinguish scanned from changed entities, 2D poses and tiles, not network bytes. A command ACK is not proof of physical fleet motion.'
    }
}

if ($SelfTest) {
    $state = New-DiagnosticsState
    Read-DiagnosticLines $state @(
        '[FreeWorldMultiplayer][fleet-command] stage=client.sent command=cmdABC fleet=123 role=guest outcome=queued',
        '[FreeWorldMultiplayer][fleet-command] stage=host.received command=cmdABC fleet=123 role=host outcome=queued',
        '[FreeWorldMultiplayer][fleet-command] stage=host.native.begin command=cmdABC fleet=123 role=host outcome=begin',
        '[FreeWorldMultiplayer][fleet-command] stage=host.native.end command=cmdABC fleet=123 role=host outcome=accepted',
        '[FreeWorldMultiplayer][fleet-command] stage=host.result command=cmdABC fleet=123 role=host outcome=accepted',
        '[FreeWorldMultiplayer][fleet-command] stage=client.result command=cmdABC fleet=123 role=guest outcome=accepted',
        '[FreeWorldMultiplayer][slow-operation] stage=transport.handle ms=6943.3 bytes=20955155 map=-1 role=guest',
        '[FreeWorldMultiplayer][profile] stage=map.cache.apply windowSeconds=30.0 count=8 p50UpperMs=16.00 p95UpperMs=50.00 p99UpperMs=100.00 maxMs=81.25',
        '[FreeWorldMultiplayer][profile] stage=ground.capture.tiles windowSeconds=30.0 count=5 p50UpperMs=4.00 p95UpperMs=16.00 p99UpperMs=16.00 maxMs=14.5',
        '[FreeWorldMultiplayer][ground-counts] captures=5 entitiesScanned=500 entityChanges=2 fieldsScanned=100 tilesScanned=327680 tileChanges=3 posesSampled=40 poseChanges=7'
    )
    $summary = Summarize-Diagnostics $state
    if ($summary.FleetCommandsSeen -ne 1 -or
        $summary.FleetCommands[0].Outcome -ne 'command-acknowledged-motion-not-measured' -or
        $summary.SlowOperationGroups[0].MaxSlowMs -ne 6943.3 -or
        $summary.ProfileWindows[0].P95UpperMs -ne 50 -or
        $summary.ProfileWindows[1].Stage -ne 'ground.capture.tiles' -or
        $summary.GroundCaptureWindows.Count -ne 1 -or
        $summary.GroundCaptureWindows[0].TilesScanned -ne 327680 -or
        $summary.GroundCaptureWindows[0].TileChanges -ne 3) {
        throw 'Multiplayer diagnostics analyzer self-test failed.'
    }
    Write-Output 'PASS: fleet command stage correlation, slow-operation and profile parsing'
    return
}

if (-not $HostLog -or -not $GuestLog) {
    throw 'Pass both -HostLog and -GuestLog from one multiplayer session, or use -SelfTest.'
}
$state = New-DiagnosticsState
foreach ($path in @($HostLog, $GuestLog)) {
    $full = [IO.Path]::GetFullPath($path)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Missing log file: $full" }
    $state.InputFiles.Add([IO.Path]::GetFileName($full))
    Read-DiagnosticLines $state @(Get-Content -LiteralPath $full -Encoding UTF8)
}
$report = Summarize-Diagnostics $state
if ($OutFile) {
    $output = [IO.Path]::GetFullPath($OutFile)
    [void](New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force)
    [IO.File]::WriteAllText($output, ($report | ConvertTo-Json -Depth 7), [Text.UTF8Encoding]::new($false))
    Write-Output ('ReportPath=' + $output)
    Write-Output ('FleetCommandsSeen=' + $report.FleetCommandsSeen + ' ProfileWindows=' + $report.ProfileWindows.Count)
}
else { $report | ConvertTo-Json -Depth 7 }
