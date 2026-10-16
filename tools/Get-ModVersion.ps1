#requires -Version 7.0
param([string]$Root = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$versionPath = Join-Path ([IO.Path]::GetFullPath($Root)) 'VERSION'
if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) { throw "MOD version source is missing: $versionPath" }
$text = [IO.File]::ReadAllText($versionPath).Trim()
$match = [Regex]::Match($text, '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')
if (-not $match.Success) { throw 'VERSION must contain two nonnegative integers separated by a dot, for example 1.1 or 1.10.' }
# Keep the minor release as an integer component: 1.10 must never become 1.1.
$major = 0
$minor = 0
$style = [Globalization.NumberStyles]::None
$culture = [Globalization.CultureInfo]::InvariantCulture
if (-not [int]::TryParse($match.Groups[1].Value, $style, $culture, [ref]$major) -or
    -not [int]::TryParse($match.Groups[2].Value, $style, $culture, [ref]$minor) -or
    $major -gt 65534 -or $minor -gt 65534) {
    throw 'VERSION components must each be between 0 and 65534 for the DLL assembly version.'
}
$numeric = $major.ToString($culture) + '.' + $minor.ToString($culture)
[pscustomobject]@{
    ModVersion = 'V' + $numeric
    NumericVersion = $numeric
    AssemblyVersion = $numeric + '.0.0'
    Major = $major
    Minor = $minor
}
