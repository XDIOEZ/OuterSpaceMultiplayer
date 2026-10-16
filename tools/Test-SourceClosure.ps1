#requires -Version 7.0
# Isolated regression of the source-closure gate. Never uses the primary Git
# index, and never modifies user/agent source files.
[CmdletBinding()]
param([string]$Root)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
$Root = [IO.Path]::GetFullPath($Root)
$auditScript = Join-Path $Root 'tools\Audit-SourceClosure.ps1'
$fixture = Join-Path $Root ('artifacts\optimization-audit-fixture\' + [guid]::NewGuid().ToString('N'))
$required = @('src\sample.cs', 'tools\sample.ps1', 'package\Manifest\manifest.xml',
    'third_party\license.txt', 'build.ps1', 'Install-Mod.ps1',
    'Sync-ModCopies.ps1', 'README.md', 'DEPENDENCIES.md', 'VERSION',
    'CHANGELOG.md', 'Agent.md', 'AGENTS.md')
foreach ($relative in $required) {
    $file = Join-Path $fixture $relative
    [void](New-Item -ItemType Directory -Force -Path (Split-Path -Parent $file))
    [IO.File]::WriteAllText($file, 'fixture:' + $relative, [Text.UTF8Encoding]::new($false))
}
& git -C $fixture init -q
if ($LASTEXITCODE -ne 0) { throw 'Fixture Git init failed.' }
& git -C $fixture add -- .
if ($LASTEXITCODE -ne 0) { throw 'Fixture Git staging failed.' }
& git -C $fixture -c user.name=OptimizationTest -c user.email=optimization@example.invalid commit -q -m 'fixture baseline'
if ($LASTEXITCODE -ne 0) { throw 'Fixture Git baseline commit failed.' }
$reportPath = Join-Path $fixture 'artifacts\audit.json'

& $auditScript -Root $fixture -ReportPath $reportPath | Out-Null
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (-not $report.CleanGitClosure -or $report.ModifiedTrackedInputCount -ne 0 -or
    $report.UntrackedInputCount -ne 0) { throw 'Clean fixture was not recognized.' }
Write-Output 'PASS: clean committed input closure accepted'

# Staged-only modifications used to evade the previous "git diff" (unstaged)
# implementation. Do not touch the primary repo index to reproduce this.
[IO.File]::AppendAllText((Join-Path $fixture 'README.md'), ':staged')
& git -C $fixture add -- README.md
if ($LASTEXITCODE -ne 0) { throw 'Fixture staged change failed.' }
& $auditScript -Root $fixture -ReportPath $reportPath | Out-Null
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if ($report.CleanGitClosure -or $report.ModifiedTrackedInputCount -ne 1) {
    throw 'Staged-only build input must invalidate clean closure.'
}
$blocked = $false
try { & $auditScript -Root $fixture -ReportPath $reportPath -RequireGitClosure | Out-Null }
catch { $blocked = $true }
if (-not $blocked) { throw 'Strict release gate accepted staged, uncommitted input.' }
Write-Output 'PASS: staged-only input rejected by strict release gate'

$untracked = Join-Path $fixture 'src\added.cs'
[IO.File]::WriteAllText($untracked, '// untracked input', [Text.UTF8Encoding]::new($false))
& $auditScript -Root $fixture -ReportPath $reportPath | Out-Null
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if ($report.CleanGitClosure -or $report.UntrackedInputCount -ne 1) {
    throw 'Untracked build input must invalidate source closure.'
}
Write-Output 'PASS: untracked source input rejected'
Write-Output ('FIXTURE=' + $fixture)
