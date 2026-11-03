param(
    [Parameter(Mandatory = $true)][string]$SourceRoot,
    [string]$ModstokeDependencyRoot = (Join-Path $PSScriptRoot '..\.tools\modstoke-dependencies'),
    [switch]$IncludeAllNativeRuntimeAssets
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Modstoke.Dependencies.ps1')

# SourceRoot is a dependency bundle with the inventory's cache layout, never a loader installation.
$inventory = Read-ModstokeDependencyInventory -DependencyRoot $SourceRoot -IncludeAllNativeRuntimeAssets:$IncludeAllNativeRuntimeAssets
foreach ($asset in $inventory.assets) {
    if ($asset.selection -eq 'all-native' -and -not $IncludeAllNativeRuntimeAssets) {
        continue
    }
    $source = Get-ModstokeDependencyPath -Root $SourceRoot -RelativePath $asset.cachePath
    $destination = Get-ModstokeDependencyPath -Root $ModstokeDependencyRoot -RelativePath $asset.cachePath
    if ([IO.Path]::GetFullPath($source) -eq [IO.Path]::GetFullPath($destination)) {
        continue
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
}
$null = Read-ModstokeDependencyInventory -DependencyRoot $ModstokeDependencyRoot -IncludeAllNativeRuntimeAssets:$IncludeAllNativeRuntimeAssets
& (Join-Path $PSScriptRoot 'Prepare-RedFrameworkReferences.ps1') -RedCompatibilityLibraryRoot (Join-Path $ModstokeDependencyRoot 'build-reference/red')
Write-Host "Pinned Modstoke dependencies restored to $ModstokeDependencyRoot"
