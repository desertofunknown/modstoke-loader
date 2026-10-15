[CmdletBinding()]
param(
    [string]$GameRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path,
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-TextIfExists {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    return (Get-Content -LiteralPath $Path -Raw).Trim()
}

function Get-JsonIfExists {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        return [pscustomobject]@{
            parseError = $_.Exception.Message
        }
    }
}

function Get-VersionRecord {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $item = Get-Item -LiteralPath $Path
    return [ordered]@{
        path = $Path
        name = $item.Name
        size = $item.Length
        lastWriteTime = $item.LastWriteTime
        fileVersion = $item.VersionInfo.FileVersion
        productVersion = $item.VersionInfo.ProductVersion
    }
}

function Get-DoorstopConfig {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $values = [ordered]@{}
    $section = ""
    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith("#")) {
            continue
        }

        if ($trimmed.StartsWith("[") -and $trimmed.EndsWith("]")) {
            $section = $trimmed.Trim("[", "]")
            continue
        }

        $parts = $trimmed.Split("=", 2)
        if ($parts.Count -eq 2) {
            $key = if ($section) { "$section.$($parts[0].Trim())" } else { $parts[0].Trim() }
            $values[$key] = $parts[1].Trim()
        }
    }

    return $values
}

function Get-DotnetVersionSortKey {
    param([string]$Version)

    $match = [regex]::Match($Version, '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-(?<prerelease>.+))?$')
    if (-not $match.Success) {
        return $Version
    }

    $preRelease = $match.Groups["prerelease"].Value
    $preReleaseRank = if ([string]::IsNullOrWhiteSpace($preRelease)) { 1 } else { 0 }
    $preReleaseNumbers = [regex]::Matches($preRelease, '\d+') |
        ForEach-Object { [int]$_.Value } |
        ForEach-Object { $_.ToString("D8") }
    $preReleaseKey = if ($preReleaseNumbers) { $preReleaseNumbers -join "." } else { "00000000" }

    return "{0:D8}.{1:D8}.{2:D8}.{3:D1}.{4}" -f `
        [int]$match.Groups["major"].Value,
        [int]$match.Groups["minor"].Value,
        [int]$match.Groups["patch"].Value,
        $preReleaseRank,
        $preReleaseKey
}

function Get-LatestRuntimeDirectory {
    param([string]$GameRoot)

    $runtimeRoot = Join-Path $GameRoot "dotnet\shared\Microsoft.NETCore.App"
    if (-not (Test-Path -LiteralPath $runtimeRoot)) {
        return $null
    }

    $runtime = Get-ChildItem -LiteralPath $runtimeRoot -Directory |
        Sort-Object { Get-DotnetVersionSortKey -Version $_.Name } -Descending |
        Select-Object -First 1

    if ($runtime) {
        return $runtime.FullName
    }

    return $null
}

function Get-Il2CppMetadataInfo {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 8) {
        return [ordered]@{
            path = $Path
            size = $bytes.Length
            error = "File is too small to contain IL2CPP metadata header."
        }
    }

    return [ordered]@{
        path = $Path
        size = $bytes.Length
        sanity = ("0x{0:X8}" -f [BitConverter]::ToUInt32($bytes, 0))
        metadataVersion = [BitConverter]::ToInt32($bytes, 4)
    }
}

function Get-ScriptingAssemblySummary {
    param([string]$Path)
    $json = Get-JsonIfExists -Path $Path
    if ($null -eq $json -or $null -eq $json.names) {
        return $null
    }

    $notable = @(
        "Assembly-CSharp.dll",
        "Sons.dll",
        "Sons.Gui.Options.dll",
        "Sons.Multiplayer.dll",
        "Unity.Burst.dll",
        "Unity.Addressables.dll",
        "Unity.InputSystem.dll",
        "Unity.RenderPipelines.HighDefinition.Runtime.dll",
        "FMOD.Unity.dll",
        "Bolt.Unity.dll",
        "bolt.dll",
        "udpkit.dll"
    )

    return [ordered]@{
        count = @($json.names).Count
        notablePresent = @($notable | Where-Object { $json.names -contains $_ })
    }
}

$gameRootFull = (Resolve-Path -LiteralPath $GameRoot).Path
$dataRoot = Join-Path $gameRootFull "SonsOfTheForest_Data"
$bepInExCore = Join-Path $gameRootFull "BepInEx\core"
$loaderManifestPath = Join-Path $gameRootFull "BepInEx\plugins\OpenACAILoader\openacai-loader.manifest.json"
$loaderStatePath = Join-Path $gameRootFull "BepInEx\plugins\OpenACAILoader\openacai-loader-state.json"

$loaderManifest = Get-JsonIfExists -Path $loaderManifestPath
$loaderState = Get-JsonIfExists -Path $loaderStatePath
$doorstop = Get-DoorstopConfig -Path (Join-Path $gameRootFull "doorstop_config.ini")
$dotnetCorlibDir = Get-LatestRuntimeDirectory -GameRoot $gameRootFull
if ($null -ne $doorstop) {
    $configuredCorlibDir = $doorstop["Il2Cpp.corlib_dir"]
    if ($configuredCorlibDir) {
        $dotnetCorlibDir = Join-Path $gameRootFull $configuredCorlibDir
    }
}

$coreDlls = @(
    "BepInEx.Core.dll",
    "BepInEx.Unity.IL2CPP.dll",
    "Il2CppInterop.Runtime.dll",
    "Il2CppInterop.HarmonySupport.dll",
    "0Harmony.dll",
    "MonoMod.RuntimeDetour.dll",
    "MonoMod.Utils.dll",
    "Cpp2IL.Core.dll"
) | ForEach-Object {
    Get-VersionRecord -Path (Join-Path $bepInExCore $_)
} | Where-Object { $null -ne $_ }

$warnings = New-Object System.Collections.Generic.List[string]
if ($null -ne $loaderManifest -and $null -ne $loaderState) {
    if ($loaderManifest.version -and $loaderState.version -and $loaderManifest.version -ne $loaderState.version) {
        $warnings.Add("Installed loader manifest version '$($loaderManifest.version)' differs from runtime state '$($loaderState.version)'.")
    }
}

if ($null -ne $doorstop) {
    $hostfxr = $doorstop["Il2Cpp.hostfxr_path"]
    $runtimeConfig = $doorstop["Il2Cpp.runtime_config_path"]
    if ($hostfxr -and -not (Test-Path -LiteralPath (Join-Path $gameRootFull $hostfxr))) {
        $warnings.Add("Doorstop hostfxr path does not resolve inside the game root: $hostfxr")
    }
    if ($runtimeConfig -and -not (Test-Path -LiteralPath (Join-Path $gameRootFull $runtimeConfig))) {
        $warnings.Add("Doorstop runtime_config_path does not resolve inside the game root: $runtimeConfig")
    }
}

$dotnetSideBySideVersion = $null
if ($dotnetCorlibDir) {
    $dotnetSideBySideVersion = Get-TextIfExists -Path (Join-Path $dotnetCorlibDir ".version")
}

$report = [ordered]@{
    generatedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    gameRoot = $gameRootFull
    engine = [ordered]@{
        executable = Get-VersionRecord -Path (Join-Path $gameRootFull "SonsOfTheForest.exe")
        unityPlayer = Get-VersionRecord -Path (Join-Path $gameRootFull "UnityPlayer.dll")
        gameAssembly = Get-VersionRecord -Path (Join-Path $gameRootFull "GameAssembly.dll")
        baselib = Get-VersionRecord -Path (Join-Path $gameRootFull "baselib.dll")
        appInfo = Get-TextIfExists -Path (Join-Path $dataRoot "app.info")
        bootConfig = Get-TextIfExists -Path (Join-Path $dataRoot "boot.config")
    }
    il2cpp = [ordered]@{
        metadata = Get-Il2CppMetadataInfo -Path (Join-Path $dataRoot "il2cpp_data\Metadata\global-metadata.dat")
        scriptingAssemblies = Get-ScriptingAssemblySummary -Path (Join-Path $dataRoot "ScriptingAssemblies.json")
    }
    bootstrap = [ordered]@{
        doorstopVersion = Get-TextIfExists -Path (Join-Path $gameRootFull ".doorstop_version")
        doorstopConfig = $doorstop
        runtimeConfig = Get-JsonIfExists -Path (Join-Path $bepInExCore "BepInEx.Unity.IL2CPP.runtimeconfig.json")
    }
    loader = [ordered]@{
        installedManifestVersion = if ($loaderManifest) { $loaderManifest.version } else { $null }
        runtimeStateVersion = if ($loaderState) { $loaderState.version } else { $null }
        runtimeStateMode = if ($loaderState) { $loaderState.mode } else { $null }
        runtimeWarnings = if ($loaderState -and $loaderState.warnings) { @($loaderState.warnings) } else { @() }
    }
    dependencies = [ordered]@{
        bepinexCore = $coreDlls
        dotnetSideBySideVersion = $dotnetSideBySideVersion
    }
    warnings = @($warnings)
}

if ($Json) {
    $report | ConvertTo-Json -Depth 10
    return
}

"OpenACAI Sons Of The Forest runtime inspection"
"Game root: $gameRootFull"
$unityVersion = if ($report.engine.unityPlayer) { $report.engine.unityPlayer.productVersion } else { "<missing>" }
$metadataVersion = if ($report.il2cpp.metadata) { $report.il2cpp.metadata.metadataVersion } else { "<missing>" }
$metadataSanity = if ($report.il2cpp.metadata) { $report.il2cpp.metadata.sanity } else { "<missing>" }
$runtimeTfm = if ($report.bootstrap.runtimeConfig -and $report.bootstrap.runtimeConfig.runtimeOptions) { $report.bootstrap.runtimeConfig.runtimeOptions.tfm } else { "<missing>" }
$runtimeVersion = if ($report.bootstrap.runtimeConfig -and $report.bootstrap.runtimeConfig.runtimeOptions -and $report.bootstrap.runtimeConfig.runtimeOptions.framework) { $report.bootstrap.runtimeConfig.runtimeOptions.framework.version } else { "<missing>" }
"Unity: $unityVersion"
"IL2CPP metadata: version $metadataVersion, sanity $metadataSanity"
"Doorstop: $($report.bootstrap.doorstopVersion)"
"Runtime: $runtimeTfm / $runtimeVersion"
"Loader manifest/state: $($report.loader.installedManifestVersion) / $($report.loader.runtimeStateVersion)"

if ($report.warnings.Count -gt 0) {
    ""
    "Warnings:"
    foreach ($warning in $report.warnings) {
        "- $warning"
    }
}

""
"Core dependency versions:"
$coreDlls | ForEach-Object {
    "- $($_.name): $($_.productVersion)"
}
