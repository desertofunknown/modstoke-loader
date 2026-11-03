param(
    [string]$GameRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$Configuration = "Release",
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "dist"),
    [string]$DotnetRuntimeMajor = "11",
    [string]$DotnetRoot,
    [string]$ModstokeDependencyRoot = (Join-Path $PSScriptRoot ".tools\modstoke-dependencies"),
    [string]$GameInteropRoot,
    [bool]$UseHostFxrLayout = $true,
    [switch]$RebuildExternalRuntime,
    [switch]$IncludeAllNativeRuntimeAssets,
    [switch]$UseDoorstopForkNativeHost,
    [string]$DoorstopForkRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..")).Path "Forks\UnityDoorstop"),
    [switch]$SignArtifacts,
    [string]$SigningEndpoint = $env:AZURE_ARTIFACT_SIGNING_ENDPOINT,
    [string]$SigningAccountName = $env:AZURE_ARTIFACT_SIGNING_ACCOUNT_NAME,
    [string]$SigningCertificateProfile = $env:AZURE_ARTIFACT_SIGNING_CERTIFICATE_PROFILE,
    [string]$SigningDlibPath = $env:AZURE_ARTIFACT_SIGNING_DLIB,
    [string]$SignToolPath = $env:SIGNTOOL_EXE,
    [string]$TimestampUrl = "http://timestamp.acs.microsoft.com",
    [string]$DobbyVersion = "1.0.5",
    [string]$DobbyWinZipUrl = $env:OPENACAI_DOBBY_WIN_ZIP_URL,
    [string]$PackageDownloadUrl = $env:OPENACAI_LOADER_PACKAGE_URL,
    [string]$ManifestDownloadUrl = $env:OPENACAI_LOADER_MANIFEST_URL,
    [switch]$UpdatePrebuilt,
    [switch]$NoUpdatePrebuilt
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "dependencies\Modstoke.Dependencies.ps1")

$ModstokeDependencyRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ModstokeDependencyRoot)
$dependencyInventory = Read-ModstokeDependencyInventory -DependencyRoot $ModstokeDependencyRoot -IncludeAllNativeRuntimeAssets:$IncludeAllNativeRuntimeAssets
if ($DobbyVersion -ne $dependencyInventory.dobbyVersion -or -not [string]::IsNullOrWhiteSpace($DobbyWinZipUrl)) {
    throw "Native detour inputs are pinned in dependencies/inventory.json. Restore or deliberately update that inventory and its cache instead of overriding Dobby downloads."
}
if ([string]::IsNullOrWhiteSpace($GameInteropRoot)) {
    $GameInteropRoot = Join-Path $GameRoot "BepInEx\interop"
}
$GameInteropRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($GameInteropRoot)

if ($DotnetRuntimeMajor -ne "11") {
    throw "Modstoke Loader's net11 preview branch is .NET 11-only. Use openacai-net10-port for the current LTS runtime build."
}

if (-not $UseHostFxrLayout) {
    throw "Modstoke Loader's active branch requires the hostfxr runtime layout."
}

function Copy-Directory {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        throw "Required source directory was not found: $Source"
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination -Recurse -Force
}

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        throw "Required source file was not found: $Source"
    }

    $destinationDirectory = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Test-RequiredFiles {
    param(
        [Parameter(Mandatory = $true)][string[]]$Paths
    )

    foreach ($path in $Paths) {
        if (-not (Test-Path -LiteralPath $path)) {
            return $false
        }
    }

    return $true
}

function Remove-PackagedBackupArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$Root
    )

    if (-not (Test-Path -LiteralPath $Root)) {
        return
    }

    Get-ChildItem -LiteralPath $Root -File -Recurse |
        Where-Object { $_.Name -match '\.bak-' } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
}

function Get-DotnetRuntimeDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Version
    )

    $candidateRoots = if ($DotnetRoot) {
        @(Join-Path $DotnetRoot "shared\Microsoft.NETCore.App")
    } else { @(
        (Join-Path $env:ProgramFiles "dotnet\shared\Microsoft.NETCore.App"),
        (Join-Path ${env:ProgramFiles(x86)} "dotnet\shared\Microsoft.NETCore.App")
    ) }
    $candidateRoots = @($candidateRoots | Where-Object { $_ -and (Test-Path -LiteralPath $_) })

    foreach ($root in $candidateRoots) {
        $runtime = Get-ChildItem -LiteralPath $root -Directory |
            Where-Object { $_.Name -eq $Version } |
            Select-Object -First 1

        if ($runtime) {
            return $runtime.FullName
        }
    }

    return $null
}

function Get-DotnetHostFxrDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Version
    )

    $candidateRoots = if ($DotnetRoot) {
        @(Join-Path $DotnetRoot "host\fxr")
    } else { @(
        (Join-Path $env:ProgramFiles "dotnet\host\fxr"),
        (Join-Path ${env:ProgramFiles(x86)} "dotnet\host\fxr")
    ) }
    $candidateRoots = @($candidateRoots | Where-Object { $_ -and (Test-Path -LiteralPath $_) })

    foreach ($root in $candidateRoots) {
        $hostFxr = Get-ChildItem -LiteralPath $root -Directory |
            Where-Object { $_.Name -eq $Version } |
            Select-Object -First 1

        if ($hostFxr) {
            return $hostFxr.FullName
        }
    }

    return $null
}

function Copy-DotnetRuntime {
    param(
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][bool]$HostFxrLayout
    )

    $runtimeSource = Get-DotnetRuntimeDirectory -Version $Version
    if (-not $runtimeSource) {
        throw ".NET $Version runtime was not found locally. Supply -DotnetRoot with the SDK/runtime matching the loader manifest; MonoMod's JIT layout is version-specific."
    }

    $runtimeVersion = Split-Path -Leaf $runtimeSource

    if ($HostFxrLayout) {
        $hostFxrSource = Get-DotnetHostFxrDirectory -Version $Version
        if (-not $hostFxrSource) {
            throw ".NET $Version hostfxr was not found locally. Supply -DotnetRoot with the matching SDK/runtime."
        }

        $hostFxrVersion = Split-Path -Leaf $hostFxrSource
        $sharedDestination = Join-Path $Destination "shared\Microsoft.NETCore.App\$runtimeVersion"
        $hostFxrDestination = Join-Path $Destination "host\fxr\$hostFxrVersion"
        Copy-Directory $runtimeSource $sharedDestination
        Copy-Directory $hostFxrSource $hostFxrDestination

        return [pscustomobject]@{
            version = $runtimeVersion
            layout = "hostfxr"
            coreclrRelativePath = "dotnet/shared/Microsoft.NETCore.App/$runtimeVersion/coreclr.dll"
            corlibRelativePath = "dotnet/shared/Microsoft.NETCore.App/$runtimeVersion"
            hostfxrRelativePath = "dotnet/host/fxr/$hostFxrVersion/hostfxr.dll"
            runtimeConfigRelativePath = "Modstoke/Runtime/Modstoke.Loader.runtimeconfig.json"
        }
    }

    Copy-Directory $runtimeSource $Destination

    $aspNetRuntimeSource = $runtimeSource -replace [regex]::Escape("Microsoft.NETCore.App"), "Microsoft.AspNetCore.App"
    if (Test-Path -LiteralPath $aspNetRuntimeSource) {
        Get-ChildItem -LiteralPath $aspNetRuntimeSource -File |
            Where-Object { $_.Name -like "Microsoft.Extensions.*.dll" -or $_.Name -eq "Microsoft.Bcl.AsyncInterfaces.dll" } |
            Copy-Item -Destination $Destination -Force
    }

    $versionFile = Join-Path $Destination ".version"
    if (Test-Path -LiteralPath $versionFile) {
        $runtimeVersion = Get-Content -LiteralPath $versionFile | Select-Object -Last 1
    }

    return [pscustomobject]@{
        version = $runtimeVersion
        layout = "flat-coreclr"
        coreclrRelativePath = "dotnet/coreclr.dll"
        corlibRelativePath = "dotnet"
        hostfxrRelativePath = $null
        runtimeConfigRelativePath = $null
    }
}

function Get-RelativePackagePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    return [System.IO.Path]::GetRelativePath($Root, $Path).Replace("\", "/")
}

function Get-PackageFileEntries {
    param(
        [Parameter(Mandatory = $true)][string]$Root
    )

    Get-ChildItem -LiteralPath $Root -File -Recurse |
        Sort-Object FullName |
        Where-Object { -not (Get-RelativePackagePath -Root $Root -Path $_.FullName).StartsWith("BepInEx/config/", [System.StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object {
            [pscustomobject]@{
                path = Get-RelativePackagePath -Root $Root -Path $_.FullName
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                size = $_.Length
            }
        }
}

function Get-NativeHostExpectedExports {
    param(
        [Parameter(Mandatory = $true)][string]$DefinitionPath
    )

    if (-not (Test-Path -LiteralPath $DefinitionPath)) {
        throw "Native host export definition was not found: $DefinitionPath"
    }

    Get-Content -LiteralPath $DefinitionPath |
        ForEach-Object {
            $line = $_.Trim()
            if ($line -match '^([A-Za-z0-9_]+)\s+@(\d+)$') {
                [pscustomobject]@{
                    Name = $matches[1]
                    Ordinal = [int]$matches[2]
                }
            }
        }
}

function Get-NativeHostActualExports {
    param(
        [Parameter(Mandatory = $true)][string]$DllPath
    )

    if (-not (Test-Path -LiteralPath $DllPath)) {
        throw "Native host DLL was not found: $DllPath"
    }

    $llvmReadObj = Get-Command llvm-readobj.exe -ErrorAction SilentlyContinue
    if (-not $llvmReadObj) {
        $llvmReadObj = Get-Command llvm-readobj -ErrorAction SilentlyContinue
    }

    if (-not $llvmReadObj) {
        throw "llvm-readobj was not found. Install LLVM or add llvm-readobj.exe to PATH so the native host export table can be validated."
    }

    $output = & $llvmReadObj.Source --coff-exports $DllPath
    if ($LASTEXITCODE -ne 0) {
        throw "llvm-readobj failed while reading native host exports from $DllPath"
    }

    $exports = @()
    $currentOrdinal = $null
    $currentName = $null
    foreach ($line in $output) {
        if ($line -match '^\s*Export\s+\{') {
            $currentOrdinal = $null
            $currentName = $null
            continue
        }

        if ($line -match '^\s*Ordinal:\s+(\d+)') {
            $currentOrdinal = [int]$matches[1]
            continue
        }

        if ($line -match '^\s*Name:\s+(.+?)\s*$') {
            $currentName = $matches[1].Trim()
            continue
        }

        if ($line -match '^\s*\}') {
            if ($null -ne $currentOrdinal -and -not [string]::IsNullOrWhiteSpace($currentName)) {
                $exports += [pscustomobject]@{
                    Name = $currentName
                    Ordinal = $currentOrdinal
                }
            }
        }
    }

    return $exports
}

function Assert-NativeHostExports {
    param(
        [Parameter(Mandatory = $true)][string]$DllPath,
        [Parameter(Mandatory = $true)][string]$DefinitionPath
    )

    # Exports the host implements itself are not winhttp proxies, so the definition file does not list them.
    $hostExports = @("OpenACAI_IsIl2CppBootstrapWindow")
    $expected = @(Get-NativeHostExpectedExports -DefinitionPath $DefinitionPath)
    $allExports = @(Get-NativeHostActualExports -DllPath $DllPath)
    foreach ($name in $hostExports) {
        if (@($allExports | Where-Object Name -eq $name).Count -ne 1) {
            throw "Native host is missing export ${name}: $DllPath"
        }
    }

    $actual = @($allExports | Where-Object { $hostExports -notcontains $_.Name })
    if ($expected.Count -eq 0) {
        throw "Native host export definition has no exports: $DefinitionPath"
    }

    if ($actual.Count -ne $expected.Count) {
        throw "Native host export count mismatch. Expected $($expected.Count), actual $($actual.Count): $DllPath"
    }

    $expectedByName = @{}
    foreach ($entry in $expected) {
        $expectedByName[$entry.Name] = $entry.Ordinal
    }

    $actualByName = @{}
    foreach ($entry in $actual) {
        if ($actualByName.ContainsKey($entry.Name)) {
            throw "Native host export appeared more than once: $($entry.Name)"
        }

        $actualByName[$entry.Name] = $entry.Ordinal
    }

    if ($actualByName.ContainsKey("DllMain")) {
        throw "Native host must not export DllMain: $DllPath"
    }

    foreach ($entry in $expected) {
        if (-not $actualByName.ContainsKey($entry.Name)) {
            throw "Native host is missing export $($entry.Name) ordinal $($entry.Ordinal): $DllPath"
        }

        if ($actualByName[$entry.Name] -ne $entry.Ordinal) {
            throw "Native host export ordinal mismatch for $($entry.Name). Expected $($entry.Ordinal), actual $($actualByName[$entry.Name]): $DllPath"
        }
    }

    foreach ($entry in $actual) {
        if (-not $expectedByName.ContainsKey($entry.Name)) {
            throw "Native host has unexpected export $($entry.Name) ordinal $($entry.Ordinal): $DllPath"
        }
    }

    return [pscustomobject]@{
        exportCount = $allExports.Count
        definition = Get-RelativePackagePath -Root $suiteRoot -Path $DefinitionPath
        tool = "llvm-readobj"
    }
}

function Get-GitRevision {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [string]$Ref = "HEAD"
    )

    if (-not (Test-Path -LiteralPath $Repository)) {
        return $null
    }

    try {
        $revision = & git -C $Repository rev-parse $Ref 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $revision) {
            return $null
        }

        return ($revision | Select-Object -First 1).Trim()
    }
    catch {
        return $null
    }
}

function Test-GitDirty {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [string[]]$IgnoredPathPrefixes = @()
    )

    if (-not (Test-Path -LiteralPath $Repository)) {
        return $null
    }

    try {
        $status = & git -C $Repository status --porcelain 2>$null
        if ($LASTEXITCODE -ne 0) {
            return $null
        }

        if ($IgnoredPathPrefixes.Count -gt 0) {
            $status = $status | Where-Object {
                $line = $_
                $path = if ($line.Length -gt 3) { $line.Substring(3).Trim() } else { "" }
                -not ($IgnoredPathPrefixes | Where-Object {
                    $path.StartsWith($_, [System.StringComparison]::OrdinalIgnoreCase)
                })
            }
        }

        return [bool]$status
    }
    catch {
        return $null
    }
}

function Get-SevenZipExecutable {
    $command = Get-Command 7z, 7zz, 7za -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) {
        return $command.Source
    }

    $candidates = @(
        "C:\Program Files\7-Zip\7z.exe",
        "C:\Program Files (x86)\7-Zip\7z.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    return $null
}

function Compress-PackageZip {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$DestinationZip
    )

    if (Test-Path -LiteralPath $DestinationZip) {
        Remove-Item -LiteralPath $DestinationZip -Force
    }

    $sevenZip = Get-SevenZipExecutable
    if ($sevenZip) {
        Push-Location $SourceRoot
        try {
            & $sevenZip a -tzip -mm=Deflate -mx=9 -mfb=258 -mpass=15 -bso0 -bsp0 $DestinationZip ".\*" | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "7-Zip failed while creating $DestinationZip"
            }
        }
        finally {
            Pop-Location
        }

        return "7-Zip Deflate Ultra (mx=9,mfb=258,mpass=15)"
    }

    Compress-Archive -Path (Join-Path $SourceRoot "*") -DestinationPath $DestinationZip -Force
    return "PowerShell Compress-Archive"
}

function Get-LatestSignTool {
    if (-not [string]::IsNullOrWhiteSpace($SignToolPath)) {
        if (-not (Test-Path -LiteralPath $SignToolPath)) {
            throw "SIGNTOOL_EXE does not exist: $SignToolPath"
        }

        return (Resolve-Path -LiteralPath $SignToolPath).Path
    }

    $windowsKitBin = "C:\Program Files (x86)\Windows Kits\10\bin"
    $candidate = Get-ChildItem -LiteralPath $windowsKitBin -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\signtool\.exe$" } |
        Sort-Object @{ Expression = {
            try { [version]$_.Directory.Parent.Name } catch { [version]"0.0.0.0" }
        }; Descending = $true } |
        Select-Object -First 1

    if (-not $candidate) {
        throw "x64 SignTool was not found. Install Windows SDK Build Tools or set SIGNTOOL_EXE."
    }

    return $candidate.FullName
}

function Get-ArtifactSigningDlib {
    if (-not [string]::IsNullOrWhiteSpace($SigningDlibPath)) {
        if (-not (Test-Path -LiteralPath $SigningDlibPath)) {
            throw "AZURE_ARTIFACT_SIGNING_DLIB does not exist: $SigningDlibPath"
        }

        return (Resolve-Path -LiteralPath $SigningDlibPath).Path
    }

    $candidateRoots = @(
        (Join-Path $suiteRoot ".tools\artifact-signing"),
        (Join-Path $env:USERPROFILE ".nuget\packages\microsoft.artifactsigning.client")
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

    foreach ($root in $candidateRoots) {
        $candidate = Get-ChildItem -LiteralPath $root -Recurse -Filter "Azure.CodeSigning.Dlib.dll" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\bin\\x64\\Azure\.CodeSigning\.Dlib\.dll$" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($candidate) {
            return $candidate.FullName
        }
    }

    throw "Azure.CodeSigning.Dlib.dll was not found. Restore Microsoft.ArtifactSigning.Client or set AZURE_ARTIFACT_SIGNING_DLIB."
}

function Require-SigningValue {
    param(
        [string]$Name,
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "Missing $Name for Azure Artifact Signing."
    }
}

function New-SigningMetadataFile {
    param(
        [Parameter(Mandatory = $true)][string]$MetadataPath
    )

    Require-SigningValue "AZURE_ARTIFACT_SIGNING_ENDPOINT" $SigningEndpoint
    Require-SigningValue "AZURE_ARTIFACT_SIGNING_ACCOUNT_NAME" $SigningAccountName
    Require-SigningValue "AZURE_ARTIFACT_SIGNING_CERTIFICATE_PROFILE" $SigningCertificateProfile

    $metadata = [ordered]@{
        Endpoint = $SigningEndpoint.Trim()
        CodeSigningAccountName = $SigningAccountName.Trim()
        CertificateProfileName = $SigningCertificateProfile.Trim()
    }

    $metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $MetadataPath -Encoding UTF8
}

function Get-LoaderSignableFiles {
    param(
        [Parameter(Mandatory = $true)][string]$Root
    )

    $paths = New-Object System.Collections.Generic.List[string]
    $winhttp = Join-Path $Root "winhttp.dll"
    if (Test-Path -LiteralPath $winhttp) {
        $paths.Add($winhttp)
    }

    $bepInExCore = Join-Path $Root "BepInEx\core"
    if (Test-Path -LiteralPath $bepInExCore) {
        Get-ChildItem -LiteralPath $bepInExCore -Filter "BepInEx*.dll" -File |
            ForEach-Object { $paths.Add($_.FullName) }

        $dobby = Join-Path $bepInExCore "dobby.dll"
        if (Test-Path -LiteralPath $dobby) {
            $paths.Add($dobby)
        }
    }

    $modstokeRuntimeRoot = Join-Path $Root "Modstoke\Runtime"
    foreach ($runtimeFile in @("Modstoke.Bootstrap.dll", "Modstoke.Preloader.dll", "Modstoke.Loader.dll", "Modstoke.Console.dll", "Modstoke.Console.exe")) {
        $runtimePath = Join-Path $modstokeRuntimeRoot $runtimeFile
        if (Test-Path -LiteralPath $runtimePath) {
            $paths.Add($runtimePath)
        }
    }

    $modstokeCompatibilityRoot = Join-Path $modstokeRuntimeRoot "Compatibility"
    foreach ($compatFile in @("RedLoader.dll", "SonsSdk.dll", "Tomlet.dll", "SemanticVersioning.dll", "Alt.Json.dll", "GLTF.dll")) {
        $compatPath = Join-Path $modstokeCompatibilityRoot $compatFile
        if (Test-Path -LiteralPath $compatPath) {
            $paths.Add($compatPath)
        }
    }

    $bootstrapMirrorRoot = Join-Path $Root "BepInEx\plugins\OpenACAILoader"
    foreach ($mirrorFile in @("Modstoke.Loader.dll", "RedLoader.dll", "SonsSdk.dll", "Tomlet.dll", "SemanticVersioning.dll", "Alt.Json.dll", "GLTF.dll")) {
        $mirrorPath = Join-Path $bootstrapMirrorRoot $mirrorFile
        if (Test-Path -LiteralPath $mirrorPath) {
            $paths.Add($mirrorPath)
        }
    }

    return $paths.ToArray()
}

function Invoke-AzureCodeSigning {
    param(
        [Parameter(Mandatory = $true)][string[]]$Files,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if ($Files.Count -eq 0) {
        return
    }

    $signTool = Get-LatestSignTool
    $dlib = Get-ArtifactSigningDlib
    $metadataPath = Join-Path $suiteRoot ".tools\artifact-signing\metadata.loader.generated.json"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $metadataPath) | Out-Null
    New-SigningMetadataFile -MetadataPath $metadataPath

    foreach ($file in $Files) {
        Write-Host "Signing $file"
        & $signTool sign /v /debug /fd SHA256 /tr $TimestampUrl /td SHA256 /d $Description /dlib $dlib /dmdf $metadataPath $file
        if ($LASTEXITCODE -ne 0) {
            throw "SignTool failed while signing $file"
        }

        & $signTool verify /pa /v $file
        if ($LASTEXITCODE -ne 0) {
            throw "SignTool verification failed for $file"
        }
    }
}

$suiteRoot = Resolve-Path $PSScriptRoot
$toolCacheRoot = Join-Path $suiteRoot ".tools"
$bootstrapProject = Join-Path $suiteRoot "src\Modstoke.Bootstrap\Modstoke.Bootstrap.csproj"
$bootstrapOutput = Join-Path $suiteRoot "src\Modstoke.Bootstrap\bin\$Configuration\Modstoke.Bootstrap.dll"
$preloaderProject = Join-Path $suiteRoot "src\Modstoke.Preloader\Modstoke.Preloader.csproj"
$preloaderOutput = Join-Path $suiteRoot "src\Modstoke.Preloader\bin\$Configuration\Modstoke.Preloader.dll"
$consoleOutputRoot = Join-Path $suiteRoot "src\Modstoke.Console\bin\$Configuration"
$coreCompatibilityOutputRoot = Join-Path $suiteRoot "src\Modstoke.Compatibility.BepInEx.Core\bin\$Configuration"
$unityCommonOutputRoot = Join-Path $suiteRoot "src\Modstoke.Compatibility.BepInEx.Unity.Common\bin\$Configuration"
$preloaderCompatibilityOutputRoot = Join-Path $suiteRoot "src\Modstoke.Compatibility.BepInEx.Preloader.Core\bin\$Configuration"
$il2cppCompatibilityOutputRoot = Join-Path $suiteRoot "src\Modstoke.Compatibility.BepInEx.Unity.IL2CPP\bin\$Configuration"
$nativeHostProjectRoot = Join-Path $suiteRoot "src\Modstoke.NativeHost"
$nativeHostCargoManifest = Join-Path $nativeHostProjectRoot "Cargo.toml"
$nativeHostExportDefinition = Join-Path $nativeHostProjectRoot "native_host_exports.def"
$nativeHostCargoProfile = if ($Configuration -eq "Debug") { "debug" } else { "release" }
$nativeHostOutput = Join-Path $nativeHostProjectRoot "target\$nativeHostCargoProfile\modstoke_native_host.dll"
$compatProject = Join-Path $suiteRoot "src\Modstoke.Loader\Modstoke.Loader.csproj"
$compatOutput = Join-Path $suiteRoot "src\Modstoke.Loader\bin\$Configuration\Modstoke.Loader.dll"
# Compatibility facades retain Debug conditional behavior independently of the host configuration.
$redCompatibilityProject = Join-Path $suiteRoot "src\Modstoke.Compatibility.RedLoader\Modstoke.Compatibility.RedLoader.csproj"
$sonsCompatibilityProject = Join-Path $suiteRoot "src\Modstoke.Compatibility.SonsSdk\Modstoke.Compatibility.SonsSdk.csproj"
$redCompatibilityOutputRoot = Join-Path $suiteRoot "src\Modstoke.Compatibility.RedLoader\bin\Debug"
$sonsCompatibilityOutputRoot = Join-Path $suiteRoot "src\Modstoke.Compatibility.SonsSdk\bin\Debug"
$distRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$packageRoot = Join-Path $distRoot "OpenACAILoader"
$zipPath = Join-Path $distRoot "OpenACAILoader.zip"
$manifestPath = Join-Path $distRoot "OpenACAILoader.manifest.json"
$prebuiltRoot = Join-Path $suiteRoot "prebuilt"
$loaderManifest = Get-Content -LiteralPath (Join-Path $suiteRoot "loader.manifest.json") -Raw | ConvertFrom-Json
$loaderVersion = $loaderManifest.version
$publishBranch = "openacai-net11-preview"
$releaseAssetRoot = "https://github.com/desertofunknown/openacai-loader/releases/latest/download"
$downloadUrl = if ([string]::IsNullOrWhiteSpace($PackageDownloadUrl)) {
    "$releaseAssetRoot/OpenACAILoader.zip"
} else {
    $PackageDownloadUrl
}
$manifestUrl = if ([string]::IsNullOrWhiteSpace($ManifestDownloadUrl)) {
    "$releaseAssetRoot/OpenACAILoader.manifest.json"
} else {
    $ManifestDownloadUrl
}
$compatTargetFramework = "net$DotnetRuntimeMajor.0"
$doorstopOutputRoot = Join-Path $DoorstopForkRoot "build\windows\x64\release"

if (-not (Test-Path -LiteralPath $bootstrapProject)) {
    throw "Bootstrap project was not found: $bootstrapProject"
}

if (-not (Test-Path -LiteralPath $preloaderProject)) {
    throw "Preloader project was not found: $preloaderProject"
}

if (-not (Test-Path -LiteralPath $nativeHostCargoManifest)) {
    throw "Modstoke Native Host project was not found: $nativeHostCargoManifest"
}

if (-not (Test-Path -LiteralPath $nativeHostExportDefinition)) {
    throw "Modstoke Native Host export definition was not found: $nativeHostExportDefinition"
}

if (-not (Test-Path -LiteralPath $compatProject)) {
    throw "Compatibility project was not found: $compatProject"
}

if (-not (Test-Path -LiteralPath (Join-Path $GameRoot "SonsOfTheForest.exe") -PathType Leaf) -and
    -not (Test-Path -LiteralPath (Join-Path $GameRoot "SonsOfTheForestDS.exe") -PathType Leaf)) {
    throw "GameRoot must contain SonsOfTheForest.exe or SonsOfTheForestDS.exe: $GameRoot"
}

foreach ($project in @($redCompatibilityProject, $sonsCompatibilityProject)) {
    if (-not (Test-Path -LiteralPath $project)) {
        throw "Modstoke compatibility project was not found: $project"
    }
}

if ($UseHostFxrLayout) {
    if ($UseDoorstopForkNativeHost) {
        $doorstopProject = Join-Path $DoorstopForkRoot "xmake.lua"
        if (-not (Test-Path -LiteralPath $doorstopProject)) {
            throw "Experimental UnityDoorstop fork was not found: $DoorstopForkRoot"
        }

        $doorstopRuntimeOutputs = @(
            (Join-Path $doorstopOutputRoot ".doorstop_version"),
            (Join-Path $doorstopOutputRoot "doorstop.dll")
        )
        if ($RebuildExternalRuntime -or -not (Test-RequiredFiles -Paths $doorstopRuntimeOutputs)) {
            Push-Location $DoorstopForkRoot
            try {
                xmake f -p windows -a x64 -m release --include_logging=true
                if ($LASTEXITCODE -ne 0) {
                    throw "xmake configuration failed for UnityDoorstop: $DoorstopForkRoot"
                }
                xmake build doorstop
                if ($LASTEXITCODE -ne 0) {
                    throw "xmake build failed for UnityDoorstop: $DoorstopForkRoot"
                }
            }
            finally {
                Pop-Location
            }
        } else {
            Write-Host "Reusing existing UnityDoorstop output. Pass -RebuildExternalRuntime to rebuild it."
        }
    } else {
        Write-Host "Building Modstoke Native Host."
        Push-Location $nativeHostProjectRoot
        try {
            if ($nativeHostCargoProfile -eq "debug") {
                cargo build
            } else {
                cargo build --release
            }
            if ($LASTEXITCODE -ne 0) {
                throw "cargo build failed for Modstoke Native Host: $nativeHostProjectRoot"
            }
        }
        finally {
            Pop-Location
        }

        if (-not (Test-Path -LiteralPath $nativeHostOutput)) {
            throw "Modstoke Native Host build did not produce: $nativeHostOutput"
        }
    }
}

$bepInExCoreReferenceRoot = Join-Path $ModstokeDependencyRoot "managed"
& (Join-Path $suiteRoot 'dependencies/Prepare-RedFrameworkReferences.ps1') -RedCompatibilityLibraryRoot (Join-Path $ModstokeDependencyRoot 'build-reference/red')
dotnet build $preloaderProject --configuration $Configuration --nologo --verbosity minimal -p:TargetFramework=$compatTargetFramework -p:GameRoot="$GameRoot" -p:GameInteropRoot="$GameInteropRoot" -p:ModstokeDependencyRoot="$ModstokeDependencyRoot" -p:BepInExCoreRoot="$bepInExCoreReferenceRoot"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed for OpenACAI preloader: $preloaderProject"
}
dotnet build $bootstrapProject --configuration $Configuration --nologo --verbosity minimal -p:TargetFramework=$compatTargetFramework
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed for OpenACAI bootstrap: $bootstrapProject"
}
dotnet build $compatProject --configuration $Configuration --nologo --verbosity minimal -p:TargetFramework=$compatTargetFramework -p:GameRoot="$GameRoot" -p:GameInteropRoot="$GameInteropRoot" -p:ModstokeDependencyRoot="$ModstokeDependencyRoot" -p:BepInExCoreRoot="$bepInExCoreReferenceRoot"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed for OpenACAI core: $compatProject"
}

$resolvedDistRoot = [System.IO.Path]::GetFullPath($distRoot)
$resolvedPackageRoot = [System.IO.Path]::GetFullPath($packageRoot)
if (-not [string]::Equals((Split-Path -Parent $resolvedPackageRoot), $resolvedDistRoot.TrimEnd('\', '/'), [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean package output outside dist: $resolvedPackageRoot"
}

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
$modstokeRootDestination = Join-Path $packageRoot "Modstoke"
$modstokeRuntimeDestination = Join-Path $modstokeRootDestination "Runtime"
$modstokeCompatibilityDestination = Join-Path $modstokeRuntimeDestination "Compatibility"
$bootstrapMirrorDestination = Join-Path $packageRoot "BepInEx\plugins\OpenACAILoader"
New-Item -ItemType Directory -Force -Path $modstokeRuntimeDestination | Out-Null
New-Item -ItemType Directory -Force -Path $modstokeCompatibilityDestination | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot "Mods") | Out-Null

$nativeHostExportValidation = $null
if ($UseHostFxrLayout) {
    if ($UseDoorstopForkNativeHost) {
        Copy-RequiredFile (Join-Path $doorstopOutputRoot ".doorstop_version") (Join-Path $packageRoot ".doorstop_version")
        Copy-RequiredFile (Join-Path $doorstopOutputRoot "doorstop.dll") (Join-Path $packageRoot "winhttp.dll")
    } else {
        "openacai-rust-native-host-$loaderVersion" | Set-Content -LiteralPath (Join-Path $packageRoot ".doorstop_version") -Encoding ASCII
        $packagedNativeHostPath = Join-Path $packageRoot "winhttp.dll"
        Copy-RequiredFile $nativeHostOutput $packagedNativeHostPath
        $nativeHostExportValidation = Assert-NativeHostExports -DllPath $packagedNativeHostPath -DefinitionPath $nativeHostExportDefinition
    }
} else {
    Copy-RequiredFile (Join-Path $GameRoot ".doorstop_version") (Join-Path $packageRoot ".doorstop_version")
    Copy-RequiredFile (Join-Path $GameRoot "doorstop_config.ini") (Join-Path $packageRoot "doorstop_config.ini")
    Copy-RequiredFile (Join-Path $GameRoot "winhttp.dll") (Join-Path $packageRoot "winhttp.dll")
}

$dotnetRuntime = Copy-DotnetRuntime -Destination (Join-Path $packageRoot "dotnet") -Version $loaderManifest.runtime.dotnetRuntime -HostFxrLayout $UseHostFxrLayout

Copy-ModstokeDependencyPayload -Inventory $dependencyInventory -DependencyRoot $ModstokeDependencyRoot -PackageRoot $packageRoot -IncludeAllNativeRuntimeAssets:$IncludeAllNativeRuntimeAssets
Copy-RequiredFile (Join-Path $coreCompatibilityOutputRoot "BepInEx.Core.dll") (Join-Path $packageRoot "BepInEx\core\BepInEx.Core.dll")
Copy-RequiredFile (Join-Path $coreCompatibilityOutputRoot "BepInEx.Core.xml") (Join-Path $packageRoot "BepInEx\core\BepInEx.Core.xml")
$legacyCoreSymbols = Join-Path $packageRoot "BepInEx\core\BepInEx.Core.pdb"
if (Test-Path -LiteralPath $legacyCoreSymbols) {
    Remove-Item -LiteralPath $legacyCoreSymbols -Force
}
Copy-RequiredFile (Join-Path $preloaderCompatibilityOutputRoot "BepInEx.Preloader.Core.dll") (Join-Path $packageRoot "BepInEx\core\BepInEx.Preloader.Core.dll")
Copy-RequiredFile (Join-Path $preloaderCompatibilityOutputRoot "BepInEx.Preloader.Core.xml") (Join-Path $packageRoot "BepInEx\core\BepInEx.Preloader.Core.xml")
$legacyPreloaderSymbols = Join-Path $packageRoot "BepInEx\core\BepInEx.Preloader.Core.pdb"
if (Test-Path -LiteralPath $legacyPreloaderSymbols) {
    Remove-Item -LiteralPath $legacyPreloaderSymbols -Force
}
Copy-RequiredFile (Join-Path $unityCommonOutputRoot "BepInEx.Unity.Common.dll") (Join-Path $packageRoot "BepInEx\core\BepInEx.Unity.Common.dll")
Copy-RequiredFile (Join-Path $unityCommonOutputRoot "BepInEx.Unity.Common.xml") (Join-Path $packageRoot "BepInEx\core\BepInEx.Unity.Common.xml")
# The owned assembly embeds its symbols; do not retain an upstream sidecar.
$legacyUnityCommonSymbols = Join-Path $packageRoot "BepInEx\core\BepInEx.Unity.Common.pdb"
if (Test-Path -LiteralPath $legacyUnityCommonSymbols) {
    Remove-Item -LiteralPath $legacyUnityCommonSymbols -Force
}
Copy-RequiredFile (Join-Path $il2cppCompatibilityOutputRoot "BepInEx.Unity.IL2CPP.dll") (Join-Path $packageRoot "BepInEx\core\BepInEx.Unity.IL2CPP.dll")
Copy-RequiredFile (Join-Path $il2cppCompatibilityOutputRoot "BepInEx.Unity.IL2CPP.xml") (Join-Path $packageRoot "BepInEx\core\BepInEx.Unity.IL2CPP.xml")
$legacyIl2CppSymbols = Join-Path $packageRoot "BepInEx\core\BepInEx.Unity.IL2CPP.pdb"
if (Test-Path -LiteralPath $legacyIl2CppSymbols) {
    Remove-Item -LiteralPath $legacyIl2CppSymbols -Force
}
$legacyRuntimeConfigPath = Join-Path $packageRoot "BepInEx\core\BepInEx.Unity.IL2CPP.runtimeconfig.json"
if (Test-Path -LiteralPath $legacyRuntimeConfigPath) {
    Remove-Item -LiteralPath $legacyRuntimeConfigPath -Force
}
New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot "BepInEx\config") | Out-Null
Copy-RequiredFile (Join-Path $suiteRoot "dependencies\BepInEx.cfg") (Join-Path $packageRoot "BepInEx\config\BepInEx.cfg")
New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot "BepInEx\patchers") | Out-Null

if ($UseHostFxrLayout) {
    $runtimeConfig = [ordered]@{
        runtimeOptions = [ordered]@{
            tfm = $compatTargetFramework
            framework = [ordered]@{
                name = "Microsoft.NETCore.App"
                version = $dotnetRuntime.version
            }
            rollForward = "LatestPatch"
        }
    }
    $runtimeConfigPath = Join-Path $packageRoot $dotnetRuntime.runtimeConfigRelativePath.Replace("/", "\")
    $runtimeConfig | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $runtimeConfigPath -Encoding UTF8

    $doorstopConfig = @"
# General options for Unity Doorstop
[General]
enabled = true
target_assembly = Modstoke\Runtime\Modstoke.Bootstrap.dll
redirect_output_log = false
boot_config_override =
ignore_disable_switch = false

[UnityMono]
dll_search_path_override =
debug_enabled = false
debug_address = 127.0.0.1:10000
debug_suspend = false

[Il2Cpp]
coreclr_path = $($dotnetRuntime.coreclrRelativePath.Replace("/", "\"))
corlib_dir = $($dotnetRuntime.corlibRelativePath.Replace("/", "\"))
hostfxr_path = $($dotnetRuntime.hostfxrRelativePath.Replace("/", "\"))
runtime_config_path = $($dotnetRuntime.runtimeConfigRelativePath.Replace("/", "\"))
"@
    $doorstopConfig | Set-Content -LiteralPath (Join-Path $packageRoot "doorstop_config.ini") -Encoding ASCII
}

Copy-RequiredFile $bootstrapOutput (Join-Path $modstokeRuntimeDestination "Modstoke.Bootstrap.dll")
Copy-RequiredFile $preloaderOutput (Join-Path $modstokeRuntimeDestination "Modstoke.Preloader.dll")
Copy-RequiredFile $compatOutput (Join-Path $modstokeRuntimeDestination "Modstoke.Loader.dll")
foreach ($consoleFile in @("Modstoke.Console.dll", "Modstoke.Console.exe", "Modstoke.Console.deps.json", "Modstoke.Console.runtimeconfig.json")) {
    Copy-RequiredFile (Join-Path $consoleOutputRoot $consoleFile) (Join-Path $modstokeRuntimeDestination $consoleFile)
}
Copy-RequiredFile (Join-Path $redCompatibilityOutputRoot "RedLoader.dll") (Join-Path $modstokeCompatibilityDestination "RedLoader.dll")
Copy-RequiredFile (Join-Path $sonsCompatibilityOutputRoot "SonsSdk.dll") (Join-Path $modstokeCompatibilityDestination "SonsSdk.dll")
Copy-RequiredFile (Join-Path $suiteRoot "loader.manifest.json") (Join-Path $modstokeRuntimeDestination "openacai-loader.manifest.json")

New-Item -ItemType Directory -Force -Path $bootstrapMirrorDestination | Out-Null
Copy-RequiredFile (Join-Path $modstokeRuntimeDestination "Modstoke.Loader.dll") (Join-Path $bootstrapMirrorDestination "Modstoke.Loader.dll")
Copy-RequiredFile (Join-Path $modstokeCompatibilityDestination "RedLoader.dll") (Join-Path $bootstrapMirrorDestination "RedLoader.dll")
Copy-RequiredFile (Join-Path $modstokeCompatibilityDestination "SonsSdk.dll") (Join-Path $bootstrapMirrorDestination "SonsSdk.dll")
Copy-RequiredFile (Join-Path $modstokeRuntimeDestination "openacai-loader.manifest.json") (Join-Path $bootstrapMirrorDestination "openacai-loader.manifest.json")
Copy-RequiredFile (Join-Path $suiteRoot "LICENSE") (Join-Path $packageRoot "LICENSE")
Copy-RequiredFile (Join-Path $suiteRoot "NOTICE") (Join-Path $packageRoot "NOTICE")
Copy-RequiredFile (Join-Path $suiteRoot "THIRD_PARTY_NOTICES.md") (Join-Path $packageRoot "THIRD_PARTY_NOTICES.md")
Copy-Directory (Join-Path $suiteRoot "LICENSES") (Join-Path $packageRoot "LICENSES")
Copy-RequiredFile (Join-Path $suiteRoot "dependencies\inventory.json") (Join-Path $modstokeRuntimeDestination "dependency-inventory.json")

if ($SignArtifacts) {
    Invoke-AzureCodeSigning -Files (Get-LoaderSignableFiles -Root $packageRoot) -Description "Modstoke Loader by OpenACAI"
}

$initialFileEntries = @(Get-PackageFileEntries -Root $packageRoot)
$sourceTrail = [pscustomobject]@{
    openAcaiLoader = [pscustomobject]@{
        repository = "https://github.com/desertofunknown/openacai-loader"
        branch = $publishBranch
        commit = Get-GitRevision -Repository $suiteRoot
        dirty = Test-GitDirty -Repository $suiteRoot -IgnoredPathPrefixes @(
            "dist/",
            "prebuilt/",
            ".tools/artifact-signing/metadata.loader.generated.json"
        )
    }
    openAcaiNativeHost = [pscustomobject]@{
        language = "Rust"
        project = "src/Modstoke.NativeHost"
        packaged = -not [bool]$UseDoorstopForkNativeHost
        output = if ($UseDoorstopForkNativeHost) { $null } else { "winhttp.dll" }
        cargoTarget = "x86_64-pc-windows-msvc"
        exportValidation = $nativeHostExportValidation
    }
    modstokeUnityMetadata = [pscustomobject]@{
        project = "src/Modstoke.Compatibility.BepInEx.Unity.Common"
        output = "BepInEx/core/BepInEx.Unity.Common.dll"
        assemblyIdentity = "BepInEx.Unity.Common, Version=6.0.0.0, Culture=neutral, PublicKeyToken=null"
        license = "LGPL-2.1-only"
    }
    modstokeCoreCompatibility = [pscustomobject]@{
        project = "src/Modstoke.Compatibility.BepInEx.Core"
        output = "BepInEx/core/BepInEx.Core.dll"
        assemblyIdentity = "BepInEx.Core, Version=6.0.0.0, Culture=neutral, PublicKeyToken=null"
        pluginEngine = "Modstoke.Preloader"
        retainedSupport = "BepInEx configuration, logging, console, paths, metadata and utility implementations"
        license = "LGPL-2.1-only"
    }
    modstokePreloaderCompatibility = [pscustomobject]@{
        project = "src/Modstoke.Compatibility.BepInEx.Preloader.Core"
        output = "BepInEx/core/BepInEx.Preloader.Core.dll"
        assemblyIdentity = "BepInEx.Preloader.Core, Version=6.0.0.0, Culture=neutral, PublicKeyToken=null"
        patchingEngine = "Modstoke.Preloader"
        license = "LGPL-2.1-only"
    }
    modstokeIl2CppCompatibility = [pscustomobject]@{
        project = "src/Modstoke.Compatibility.BepInEx.Unity.IL2CPP"
        output = "BepInEx/core/BepInEx.Unity.IL2CPP.dll"
        assemblyIdentity = "BepInEx.Unity.IL2CPP, Version=6.0.0.0, Culture=neutral, PublicKeyToken=null"
        runtimeOwners = @("Modstoke.Bootstrap", "Modstoke.Preloader", "Modstoke.Loader")
        license = "LGPL-2.1-only"
    }
    # Historical source identities are recorded by the pinned input baseline, not discovered from forks.
    bepinex = $dependencyInventory.baseline.bepinex
    redLoaderSonsSdk = $dependencyInventory.baseline.redLoaderSonsSdk
    pinnedDependencies = [pscustomobject]@{
        inventory = "Modstoke/Runtime/dependency-inventory.json"
        sha256 = (Get-FileHash -LiteralPath (Join-Path $suiteRoot "dependencies\inventory.json") -Algorithm SHA256).Hash
        baseline = $dependencyInventory.baseline
        originalLoaderBuildRequired = $false
    }
    unityDoorstop = [pscustomobject]@{
        upstream = "https://github.com/NeighTools/UnityDoorstop"
        fork = "https://github.com/desertofunknown/openacai-unitydoorstop"
        branch = "openacai-hostfxr-net10"
        packaged = [bool]$UseDoorstopForkNativeHost
        upstreamBaseRef = "origin/master"
        upstreamBaseCommit = if ($UseDoorstopForkNativeHost) { Get-GitRevision -Repository $DoorstopForkRoot -Ref "origin/master" } else { $null }
        forkCommit = if ($UseDoorstopForkNativeHost) { Get-GitRevision -Repository $DoorstopForkRoot } else { $null }
        dirty = if ($UseDoorstopForkNativeHost) { Test-GitDirty -Repository $DoorstopForkRoot } else { $null }
        patchDirectory = "patches/unitydoorstop"
    }
    modstokeRedCompatibility = [pscustomobject]@{
        project = "src/Modstoke.Compatibility.RedLoader"
        output = "Modstoke/Runtime/Compatibility/RedLoader.dll"
        assemblyIdentity = "RedLoader, Version=0.8.6.0, Culture=neutral, PublicKeyToken=null"
        targetFramework = "net6.0"
        configuration = "Debug"
        runtimeOwners = @("Modstoke.Preloader", "Modstoke.Loader")
        license = "LGPL-2.1-only"
    }
    modstokeSonsCompatibility = [pscustomobject]@{
        project = "src/Modstoke.Compatibility.SonsSdk"
        output = "Modstoke/Runtime/Compatibility/SonsSdk.dll"
        assemblyIdentity = "SonsSdk, Version=0.8.6.0, Culture=neutral, PublicKeyToken=null"
        targetFramework = "net6.0"
        configuration = "Debug"
        runtimeOwner = "Modstoke.Loader"
        retainedSupport = "SonsSdk public contracts, native wrappers, shared state, widgets and asset support"
        license = "LGPL-2.1-only"
    }
}

$nativeRuntimeAssetPolicy = if ($IncludeAllNativeRuntimeAssets) {
    "all-platform-native-runtime-assets"
} else {
    "windows-x64-native-runtime-assets"
}

$inPackageManifest = [pscustomobject]@{
    schema = 1
    name = "Modstoke Loader installed file manifest"
    publisher = "OpenACAI"
    version = $loaderVersion
    generatedUtc = (Get-Date).ToUniversalTime().ToString("O")
    dotnetRuntimeVersion = $dotnetRuntime.version
    nativeRuntimeAssetPolicy = $nativeRuntimeAssetPolicy
    sourceTrail = $sourceTrail
    files = $initialFileEntries
}

$inPackageManifestPath = Join-Path $modstokeRuntimeDestination "openacai-loader-files.json"
$inPackageManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $inPackageManifestPath -Encoding UTF8
$bootstrapMirrorManifestPath = Join-Path $bootstrapMirrorDestination "openacai-loader-files.json"
Copy-Item -LiteralPath $inPackageManifestPath -Destination $bootstrapMirrorManifestPath -Force
$fileEntries = @(Get-PackageFileEntries -Root $packageRoot)

$zipCompressor = Compress-PackageZip -SourceRoot $packageRoot -DestinationZip $zipPath
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
$distShaPath = Join-Path $distRoot "OpenACAILoader.zip.sha256"
"$zipHash  OpenACAILoader.zip" | Set-Content -LiteralPath $distShaPath -Encoding ASCII

$runtimeSupportPolicy = if ($UseHostFxrLayout -and -not $UseDoorstopForkNativeHost) {
    "Modstoke Native Host starts hostfxr/CoreCLR for the OpenACAI .NET $DotnetRuntimeMajor modernization branch"
} elseif ($UseHostFxrLayout) {
    "Experimental hostfxr/CoreCLR bootstrap for the OpenACAI .NET $DotnetRuntimeMajor modernization branch"
} else {
    "BepInEx IL2CPP bootstrap compatibility runtime for Sons Of The Forest"
}

$redLoaderCompatibilityTarget = if ($UseHostFxrLayout) {
    "Existing RedLoader/SonsSdk net6.0 mod assemblies are hosted by Modstoke Loader on .NET $DotnetRuntimeMajor"
} else {
    "Existing RedLoader/SonsSdk net6.0 mod assemblies are hosted through Modstoke Loader"
}

$externalManifest = [pscustomobject]@{
    schema = 1
    name = "Modstoke Loader"
    publisher = "OpenACAI"
    description = "Modstoke Loader by OpenACAI. Independent mod loader and SDK for Sons of the Forest."
    version = $loaderVersion
    generatedUtc = (Get-Date).ToUniversalTime().ToString("O")
    package = [pscustomobject]@{
        fileName = "OpenACAILoader.zip"
        downloadUrl = $downloadUrl
        sha256 = $zipHash
        compressor = $zipCompressor
    }
    update = [pscustomobject]@{
        manifestUrl = $manifestUrl
        minManagerVersion = "0.1.2"
    }
    installLayout = [pscustomobject]@{
        primaryRuntimeDirectory = "Modstoke/Runtime"
        runtimeCompatibilityDirectory = "Modstoke/Runtime/Compatibility"
        nativeModsDirectory = "Modstoke/Mods"
        userDataDirectory = "Modstoke/UserData"
        bootstrapCompatibilityMirror = "BepInEx/plugins/OpenACAILoader"
        legacyCompatibilityRoots = @(
            "Mods",
            "Libs",
            "BepInEx/plugins"
        )
        stateFile = "Modstoke/Runtime/openacai-loader-state.json"
        chainloaderStateFile = "Modstoke/Runtime/openacai-chainloader-state.json"
        il2CppPreloaderStateFile = "Modstoke/Runtime/openacai-il2cpp-preloader-state.json"
        il2CppChainloaderStateFile = "Modstoke/Runtime/openacai-il2cpp-chainloader-state.json"
        stateFileCompatibilityMirror = "BepInEx/plugins/OpenACAILoader/openacai-loader-state.json"
        fileManifest = "Modstoke/Runtime/openacai-loader-files.json"
        fileManifestCompatibilityMirror = "BepInEx/plugins/OpenACAILoader/openacai-loader-files.json"
    }
    runtime = [pscustomobject]@{
        targetFramework = $compatTargetFramework
        dotnetRuntimeVersion = $dotnetRuntime.version
        layout = $dotnetRuntime.layout
        targetAssemblyRelativePath = "Modstoke/Runtime/Modstoke.Bootstrap.dll"
        coreclrRelativePath = $dotnetRuntime.coreclrRelativePath
        corlibRelativePath = $dotnetRuntime.corlibRelativePath
        hostfxrRelativePath = $dotnetRuntime.hostfxrRelativePath
        runtimeConfigRelativePath = $dotnetRuntime.runtimeConfigRelativePath
        supportPolicy = $runtimeSupportPolicy
        nativeHost = if ($UseDoorstopForkNativeHost) { "UnityDoorstop fork" } else { "Modstoke Native Host" }
        nativeHostExportValidation = $nativeHostExportValidation
        nativeRuntimeAssetPolicy = $nativeRuntimeAssetPolicy
        redLoaderCompatibilityTarget = $redLoaderCompatibilityTarget
    }
    sourceTrail = $sourceTrail
    files = $fileEntries
}

$externalManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

if ($UpdatePrebuilt -and -not $NoUpdatePrebuilt) {
    New-Item -ItemType Directory -Force -Path $prebuiltRoot | Out-Null
    Copy-Item -LiteralPath $zipPath -Destination (Join-Path $prebuiltRoot "OpenACAILoader.zip") -Force
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $prebuiltRoot "OpenACAILoader.manifest.json") -Force
    "$zipHash  OpenACAILoader.zip" | Set-Content -LiteralPath (Join-Path $prebuiltRoot "OpenACAILoader.zip.sha256") -Encoding ASCII
} elseif ($UpdatePrebuilt -and $NoUpdatePrebuilt) {
    Write-Host "Skipped prebuilt refresh because -NoUpdatePrebuilt was supplied."
} else {
    Write-Host "Prebuilt folder was not refreshed. Release assets are $zipPath, $manifestPath, and $distShaPath."
}

Write-Host "Modstoke Loader by OpenACAI package written to $zipPath"
Write-Host "Modstoke Loader by OpenACAI manifest written to $manifestPath"
Write-Host "Modstoke Loader by OpenACAI checksum written to $distShaPath"
