# Pinned build inputs and package payload selection. No loader installation discovery.
function Get-ModstokeDependencyPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    if ([IO.Path]::IsPathRooted($RelativePath) -or $RelativePath -match '(^|[\\/])\.\.([\\/]|$)' -or $RelativePath.Contains(':')) {
        throw "Dependency inventory contains an invalid relative path: $RelativePath"
    }
    return Join-Path $Root $RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
}

function Read-ModstokeDependencyInventory {
    param(
        [Parameter(Mandatory = $true)][string]$DependencyRoot,
        [switch]$IncludeAllNativeRuntimeAssets
    )

    $inventoryPath = Join-Path $PSScriptRoot 'inventory.json'
    $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    if ($inventory.schema -ne 1 -or @($inventory.assets).Count -eq 0) {
        throw "Unsupported or empty Modstoke dependency inventory: $inventoryPath"
    }
    $cachePaths = @{}
    $packagePaths = @{}
    foreach ($asset in $inventory.assets) {
        $source = Get-ModstokeDependencyPath -Root $DependencyRoot -RelativePath $asset.cachePath
        if ($cachePaths.ContainsKey($asset.cachePath)) {
            throw "Duplicate dependency cache path: $($asset.cachePath)"
        }
        $cachePaths[$asset.cachePath] = $true
        if ($asset.sha256 -notmatch '^[0-9A-Fa-f]{64}$' -or $asset.selection -notin @('default', 'all-native')) {
            throw "Invalid hash or selection in dependency inventory: $($asset.cachePath)"
        }
        foreach ($destination in $asset.packagePaths) {
            $null = Get-ModstokeDependencyPath -Root $DependencyRoot -RelativePath $destination
            if ($packagePaths.ContainsKey($destination)) {
                throw "Duplicate dependency package path: $destination"
            }
            $packagePaths[$destination] = $true
        }
        if ($asset.selection -eq 'all-native' -and -not $IncludeAllNativeRuntimeAssets) {
            continue
        }
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Missing pinned dependency: $source. Restore the cache from dependencies/README.md or pass -ModstokeDependencyRoot with a complete cache. No installed or original loader fallback is used."
        }
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $asset.sha256) {
            throw "Pinned dependency SHA256 mismatch: $source. Restore the exact asset recorded in dependencies/inventory.json; do not substitute a loader output."
        }
    }
    return $inventory
}

function Copy-ModstokeDependencyPayload {
    param(
        [Parameter(Mandatory = $true)]$Inventory,
        [Parameter(Mandatory = $true)][string]$DependencyRoot,
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [switch]$IncludeAllNativeRuntimeAssets
    )

    foreach ($asset in $Inventory.assets) {
        if ($asset.selection -eq 'all-native' -and -not $IncludeAllNativeRuntimeAssets) {
            continue
        }
        $source = Get-ModstokeDependencyPath -Root $DependencyRoot -RelativePath $asset.cachePath
        foreach ($relativeDestination in $asset.packagePaths) {
            $destination = Get-ModstokeDependencyPath -Root $PackageRoot -RelativePath $relativeDestination
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination -Force
        }
    }
}
