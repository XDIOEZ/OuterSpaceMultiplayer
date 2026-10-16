#requires -Version 7.0
param([string]$Root = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root)
$inputs = [Collections.Generic.List[object]]::new()
foreach ($folder in @('src', 'tools', 'package', 'third_party')) {
    foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $Root $folder) -Recurse -File)) {
        if ($file.FullName -like '*\package\v_common\Assemblies\*') { continue }
        $inputs.Add($file)
    }
}
foreach ($name in @('build.ps1', 'Install-Mod.ps1', 'Sync-ModCopies.ps1', 'README.md', 'DEPENDENCIES.md', 'VERSION', 'CHANGELOG.md', 'Agent.md', 'AGENTS.md')) {
    $inputs.Add((Get-Item -LiteralPath (Join-Path $Root $name)))
}
$records = @($inputs | Sort-Object FullName -Unique | ForEach-Object {
    [ordered]@{ Path=$_.FullName.Substring($Root.Length+1).Replace('\','/'); SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$canonical = ($records | ForEach-Object { $_.Path + ' ' + $_.SHA256 }) -join "`n"
$digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
[pscustomobject]@{ SourceRoot=$Root; SourceRevision=$digest; Inputs=$records }
