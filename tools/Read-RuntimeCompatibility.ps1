#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$GamePath,
    [Parameter(Mandatory = $true)][string]$HarmonyPath,
    [string]$CoreLibraryPath,
    [string]$GameAssemblyPath
)

# Metadata inspection only: no assemblies are executed and no files are written.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Reflection.Metadata
$gameRoot = (Resolve-Path -LiteralPath $GamePath).ProviderPath
$providerPath = (Resolve-Path -LiteralPath $HarmonyPath).ProviderPath
$managedRoot = Join-Path $gameRoot 'Outer Space_Data\Managed'
$runtimePath = if ($CoreLibraryPath) { [IO.Path]::GetFullPath($CoreLibraryPath) } else { Join-Path $managedRoot 'mscorlib.dll' }
$resolvedGameAssemblyPath = if ($GameAssemblyPath) { [IO.Path]::GetFullPath($GameAssemblyPath) } else { Join-Path $managedRoot 'Assembly-CSharp.dll' }
foreach ($path in @($runtimePath, $resolvedGameAssemblyPath, $providerPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required assembly does not exist: $path" }
}

$required = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($signature in @(
    'System.Reflection.AssemblyName::get_ProcessorArchitecture',
    'System.Reflection.AssemblyName::get_HashAlgorithm',
    'System.RuntimeMethodHandle::GetFunctionPointer',
    'System.Reflection.MethodBase::GetMethodBody',
    'System.Reflection.MethodBody::GetILAsByteArray',
    'System.Reflection.MethodBody::get_LocalVariables',
    'System.Reflection.MethodBody::get_ExceptionHandlingClauses',
    'System.Reflection.Emit.MethodBuilder::SetImplementationFlags'
)) { [void]$required.Add($signature) }

$coreStream = [IO.File]::OpenRead($runtimePath)
$corePe = $null
$providerStream = $null
$providerPe = $null
try {
    $corePe = [System.Reflection.PortableExecutable.PEReader]::new($coreStream)
    $available = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $core = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($corePe)
    foreach ($handle in $core.TypeDefinitions) {
        $type = $core.GetTypeDefinition($handle)
        $typeName = $core.GetString($type.Namespace) + '.' + $core.GetString($type.Name)
        foreach ($methodHandle in $type.GetMethods()) {
            $method = $core.GetMethodDefinition($methodHandle)
            [void]$available.Add($typeName + '::' + $core.GetString($method.Name))
        }
    }
    $providerStream = [IO.File]::OpenRead($providerPath)
    $providerPe = [System.Reflection.PortableExecutable.PEReader]::new($providerStream)
    $provider = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($providerPe)
    $missing = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($handle in $provider.MemberReferences) {
        $member = $provider.GetMemberReference($handle)
        if ($member.Parent.Kind -ne [System.Reflection.Metadata.HandleKind]::TypeReference) { continue }
        $type = $provider.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$member.Parent)
        if ($type.ResolutionScope.Kind -ne [System.Reflection.Metadata.HandleKind]::AssemblyReference) { continue }
        $scope = $provider.GetAssemblyReference([System.Reflection.Metadata.AssemblyReferenceHandle]$type.ResolutionScope)
        if ($provider.GetString($scope.Name) -ne 'mscorlib') { continue }
        $signature = $provider.GetString($type.Namespace) + '.' + $provider.GetString($type.Name) + '::' + $provider.GetString($member.Name)
        if ($required.Contains($signature) -and -not $available.Contains($signature)) { [void]$missing.Add($signature) }
    }
    $assembly = $provider.GetAssemblyDefinition()
    $fingerprints = foreach ($path in @($runtimePath, $resolvedGameAssemblyPath, $providerPath)) {
        [pscustomobject]@{
            Path = $path
            Length = (Get-Item -LiteralPath $path).Length
            SHA256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        }
    }
    [pscustomobject]@{
        InspectedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        MetadataOnly = $true
        GamePath = $gameRoot
        HarmonyAssembly = $provider.GetString($assembly.Name)
        HarmonyVersion = $assembly.Version.ToString()
        KnownDirectReferencesCompatible = ($missing.Count -eq 0)
        MissingDirectReferences = @($missing | Sort-Object)
        Fingerprints = @($fingerprints)
        Limitation = 'Only the known direct references are inspected; a compatible result does not establish runtime MOD loading or multiplayer acceptance.'
    }
} finally {
    if ($null -ne $providerPe) { $providerPe.Dispose() }
    if ($null -ne $providerStream) { $providerStream.Dispose() }
    if ($null -ne $corePe) { $corePe.Dispose() }
    $coreStream.Dispose()
}

