[CmdletBinding()]
param([switch]$Apply)

$ErrorActionPreference = 'Stop'
$sourceRoot = 'H:/_git/lancache-prefill-common'
$roots = @($sourceRoot)
foreach ($service in @('steam', 'epic', 'xbox', 'battlenet', 'riot')) {
    $roots += "H:/_git/$service-prefill-daemon/LancachePrefill.Common"
}
$allowed = @(
    'dotnet/OwnedOperationCoordinator.cs', 'dotnet/OwnedOperationModels.cs',
    'dotnet/RunProgress.cs', 'dotnet/RunProgressModels.cs',
    'dotnet/RequestBudget.cs', 'dotnet/RequestBudgetModels.cs',
    'dotnet/ItemClaims.cs', 'dotnet/PrefillProtocol.cs', 'dotnet/PrefillProtocolModels.cs',
    'dotnet/LancachePrefill.Common.csproj',
    'tests/LancachePrefill.Common.Tests/OwnedOperationCoordinatorTests.cs',
    'tests/LancachePrefill.Common.Tests/RunProgressTests.cs',
    'tests/LancachePrefill.Common.Tests/RequestBudgetTests.cs',
    'tests/LancachePrefill.Common.Tests/ItemClaimsTests.cs',
    'tests/LancachePrefill.Common.Tests/PrefillProtocolTests.cs',
    'tests/LancachePrefill.Common.Tests/LancachePrefill.Common.Tests.csproj',
    'scripts/sync-common.ps1'
)

function Get-CommonPath([string]$Root, [string]$Relative) {
    if ($roots -cnotcontains $Root) { throw "Unrecognized common root: $Root" }
    if ($allowed -cnotcontains $Relative -and $Relative -cne 'scripts/common-files.json') {
        throw "Unrecognized common file: $Relative"
    }
    $base = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $path = [IO.Path]::GetFullPath([IO.Path]::Combine($base, $Relative))
    if (-not $path.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path leaves common root: $path"
    }
    $ancestor = $path
    while ($ancestor.Length -ge $base.Length) {
        if (Test-Path -LiteralPath $ancestor) {
            if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Linked paths are not supported: $ancestor"
            }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    return $path
}

function Get-CommonHash([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $content = [IO.File]::ReadAllText($Path).TrimStart([char]0xFEFF).Replace("`r`n", "`n")
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($content))).ToLowerInvariant()
}

$manifestPath = Get-CommonPath $sourceRoot 'scripts/common-files.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.version -ne 1 -or $manifest.files.Count -ne $allowed.Count) { throw 'Invalid common manifest.' }
if (@($manifest.files.path | Sort-Object -Unique).Count -ne $allowed.Count) { throw 'Duplicate manifest paths.' }
$jobs = @()
foreach ($file in $manifest.files) {
    $source = Get-CommonPath $sourceRoot $file.path
    if ((Get-CommonHash $source) -cne $file.after) { throw "Source hash differs: $source" }
    if (@($file.before.PSObject.Properties).Count -ne $roots.Count) { throw "Missing destination baseline: $($file.path)" }
    foreach ($root in $roots) {
        if (-not $file.before.PSObject.Properties[$root]) { throw "Missing baseline: $root" }
        $destination = Get-CommonPath $root $file.path
        $actual = Get-CommonHash $destination
        if ($actual -ceq $file.after) { continue }
        if (-not $Apply) { throw "Common copy differs: $destination" }
        if ($root -ceq $sourceRoot -or $actual -cne $file.before.$root) {
            throw "Unexpected existing content: $destination"
        }
        $jobs += [pscustomobject]@{
            Source = $source; Destination = $destination; Hash = $file.after
            Root = $root; Relative = $file.path; Before = $actual
        }
    }
}

# The manifest cannot contain its own hash; its copies must match these exact source bytes.
$manifestHash = Get-CommonHash $manifestPath
foreach ($root in $roots | Select-Object -Skip 1) {
    $destination = Get-CommonPath $root 'scripts/common-files.json'
    $actual = Get-CommonHash $destination
    if ($actual -ceq $manifestHash) { continue }
    if (-not $Apply) { throw "Manifest copy differs: $destination" }
    if (-not $manifest.manifestBefore.PSObject.Properties[$root] -or $actual -cne $manifest.manifestBefore.$root) {
        throw "Unexpected existing manifest: $destination"
    }
    $jobs += [pscustomobject]@{
        Source = $manifestPath; Destination = $destination; Hash = $manifestHash
        Root = $root; Relative = 'scripts/common-files.json'; Before = $actual
    }
}

foreach ($job in $jobs) {
    $null = Get-CommonPath $sourceRoot $job.Relative
    $null = Get-CommonPath $job.Root $job.Relative
    if ((Get-CommonHash $job.Source) -cne $job.Hash -or (Get-CommonHash $job.Destination) -cne $job.Before) {
        throw "Content changed after synchronization preflight: $($job.Destination)"
    }
    $directory = [IO.Path]::GetDirectoryName($job.Destination)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    [IO.File]::Copy($job.Source, $job.Destination, $true)
    if ((Get-CommonHash $job.Destination) -cne $job.Hash) { throw "Copy verification failed: $($job.Destination)" }
}
Write-Output "Verified $($allowed.Count + 1) common files across $($roots.Count) roots; applied $($jobs.Count) copies."
