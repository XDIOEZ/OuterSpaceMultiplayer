#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Outer Space',
    [string]$CecilPath = 'C:\Users\CatStudio\Desktop\Project3\Library\PackageCache\com.unity.nuget.mono-cecil@1.11.5\Mono.Cecil.dll'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$stage = Split-Path -Parent $PSScriptRoot
$vendor = Join-Path $stage 'artifacts\private-json'
$archive = Join-Path $stage 'third_party\Newtonsoft.Json\source-13.0.2.zip'
$sourceHash = '3FE7BBF46E2745E7742ECFA6A18E276999512597B017FB4AC5ED86655FF2FCBA'
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $sourceHash) { throw 'Unexpected upstream source archive.' }
New-Item -ItemType Directory -Path (Join-Path $vendor 'source') -Force | Out-Null
$sourceRoot = Join-Path $vendor 'source\Newtonsoft.Json-13.0.2\Src\Newtonsoft.Json'
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'Newtonsoft.Json.csproj') -PathType Leaf)) {
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $vendor 'source') -Force
}
[xml]$project = Get-Content -LiteralPath (Join-Path $sourceRoot 'Newtonsoft.Json.csproj') -Raw
$net45 = @($project.Project.PropertyGroup | Where-Object { $_.GetAttribute('Condition') -eq "'`$(TargetFramework)'=='net45'" })
if ($net45.Count -ne 1) { throw 'Upstream net45 feature definition is absent or ambiguous.' }
# These optional integrations are not used by the MOD. Their runtime APIs are
# stripped from this player; omit them at upstream-supported compile boundaries.
$disabled = @('HAVE_COMPONENT_MODEL', 'HAVE_BIG_INTEGER', 'HAVE_TRACE_WRITER',
    'HAVE_XLINQ', 'HAVE_XML_DOCUMENT', 'HAVE_XML_DOCUMENT_TYPE', 'HAVE_ADO_NET',
    'HAVE_DYNAMIC', 'HAVE_EXPRESSIONS', 'HAVE_ENTITY_FRAMEWORK', 'HAVE_FSHARP_TYPES', 'HAVE_TYPE_DESCRIPTOR',
    'HAVE_INOTIFY_COLLECTION_CHANGED', 'HAVE_INOTIFY_PROPERTY_CHANGING')
$defines = @($net45[0].DefineConstants.Split(';') | Where-Object { $_ -and $_ -notlike '$(*' -and $_ -notin $disabled })
$managed = Join-Path $GamePath 'Outer Space_Data\Managed'
$references = @(Get-ChildItem -LiteralPath $managed -File -Filter '*.dll' | Where-Object {
    $_.Name -eq 'mscorlib.dll' -or $_.Name -eq 'System.dll' -or $_.Name -like 'System.*.dll'
})
$outRoot = Join-Path $vendor 'private'
New-Item -ItemType Directory -Path $outRoot -Force | Out-Null
$output = Join-Path $outRoot 'FreeWorldMultiplayer.Newtonsoft.Json.dll'
$infoPath = Join-Path $outRoot 'PrivateAssemblyInfo.cs'
$info = @'
[assembly: System.Reflection.AssemblyVersion("13.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("13.0.2.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("13.0.2-freeworld-private-player")]
[assembly: System.Reflection.AssemblyTitle("FreeWorldMultiplayer private Json.NET")]
'@
[IO.File]::WriteAllText($infoPath, $info, [Text.UTF8Encoding]::new($false))
$sdkLine = @(& dotnet --list-sdks) | Select-Object -Last 1
if ($LASTEXITCODE -ne 0 -or $sdkLine -notmatch '^([^ ]+) \[(.+)\]$') { throw 'Roslyn compiler unavailable.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
$sources = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter '*.cs' | Sort-Object FullName)
# Upstream's DEBUG-only assertion still needs Debug to bind during a release
# compilation. This player strips that type. Match its existing Conditional
# behavior with a preprocessor guard, keeping the downloaded source untouched.
$assertSource = Join-Path $sourceRoot 'Utilities\MiscellaneousUtils.cs'
$assertOverride = Join-Path $outRoot 'MiscellaneousUtils.cs'
$assertText = [IO.File]::ReadAllText($assertSource)
$assertLine = '            Debug.Assert(condition, message);'
if (-not $assertText.Contains($assertLine)) { throw 'Upstream assertion changed; review the release-only guard.' }
$assertText = $assertText.Replace($assertLine, "#if DEBUG`n$assertLine`n#endif")
[IO.File]::WriteAllText($assertOverride, $assertText, [Text.UTF8Encoding]::new($false))
$sources = @($sources | Where-Object FullName -NE $assertSource)
$arguments = @('/nostdlib+', '/target:library', '/langversion:9.0', '/nullable:enable',
    '/deterministic+', '/optimize+', '/utf8output', '/warn:0',
    ('/define:' + ($defines -join ';')), ('/out:"' + $output + '"'))
$arguments += @($references | ForEach-Object { '/reference:"' + $_.FullName + '"' })
$arguments += @($sources | ForEach-Object { '"' + $_.FullName + '"' })
$arguments += '"' + $assertOverride + '"'
$arguments += '"' + $infoPath + '"'
$response = Join-Path $outRoot 'compile.rsp'
[IO.File]::WriteAllLines($response, [string[]]$arguments, [Text.UTF8Encoding]::new($false))
& dotnet $compiler /noconfig "@$response"
if ($LASTEXITCODE -ne 0) { throw 'Private Json.NET compilation against selected game runtime failed.' }

# Resolve every emitted direct member reference against the installed player.
# This is metadata inspection, not game execution or reflection-path acceptance.
Add-Type -LiteralPath $CecilPath
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
foreach ($directory in @($resolver.GetSearchDirectories())) { $resolver.RemoveSearchDirectory($directory) }
$resolver.AddSearchDirectory($managed)
$parameters = [Mono.Cecil.ReaderParameters]::new()
$parameters.AssemblyResolver = $resolver
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($output, $parameters)
try {
    $missing = [Collections.Generic.List[string]]::new()
    foreach ($reference in $assembly.MainModule.AssemblyReferences) {
        if (-not (Test-Path -LiteralPath (Join-Path $managed ($reference.Name + '.dll')) -PathType Leaf)) {
            $missing.Add('Assembly: ' + $reference.FullName)
        }
    }
    $members = @($assembly.MainModule.GetMemberReferences())
    foreach ($member in $members) {
        try { if ($null -eq $member.Resolve()) { $missing.Add($member.FullName) } }
        catch { $missing.Add($member.FullName + ': ' + $_.Exception.Message) }
    }
    $report = [ordered]@{
        Assembly = $assembly.Name.FullName; SourceVersion = '13.0.2'
        SourceUrl = 'https://github.com/JamesNK/Newtonsoft.Json/archive/refs/tags/13.0.2.zip'
        SourceArchiveSHA256 = $sourceHash; SHA256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
        RuntimeAssemblies = @($assembly.MainModule.AssemblyReferences | ForEach-Object {
            $path = Join-Path $managed ($_.Name + '.dll')
            [ordered]@{ Name = $_.Name; SHA256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
        })
        CompiledAgainstInstalledSteamRuntime = $true; RuntimeTested = $false
        DisabledOptionalIntegrations = $disabled; Defines = $defines
        ReleaseOnlySourceAdaptation = 'MiscellaneousUtils.Assert: guard the existing DEBUG-only Debug.Assert call with #if DEBUG.'
        DirectMembersInspected = $members.Count; MissingDirectMembers = @($missing)
        Limitation = 'Compilation and emitted direct references only; runtime reflection and game loading remain untested.'
    }
    [IO.File]::WriteAllText((Join-Path $outRoot 'PRIVATE-JSON-BUILD.json'), ($report | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    if ($missing.Count -ne 0) { throw ('Private JSON runtime references missing: ' + ($missing -join '; ')) }
} finally { $assembly.Dispose(); $resolver.Dispose() }
Copy-Item -LiteralPath (Join-Path $vendor 'source\Newtonsoft.Json-13.0.2\LICENSE.md') -Destination (Join-Path $outRoot 'Newtonsoft.Json.LICENSE.md') -Force
Write-Host "Built private JSON: $output"
Write-Host "Resolved all $($members.Count) emitted direct member references against the selected game runtime. Runtime loading remains untested."
