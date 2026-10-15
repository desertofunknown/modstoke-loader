param(
    [string]$GameRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$Configuration = "Release",
    [string]$DotnetRuntimeMajor = "11",
    [string]$DotnetRoot,
    [bool]$UseHostFxrLayout = $true,
    [switch]$RebuildExternalRuntime,
    [switch]$IncludeAllNativeRuntimeAssets,
    [switch]$UseDoorstopForkNativeHost,
    [string]$BepInExForkRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..")).Path "Forks\BepInEx"),
    [string]$RedLoaderSourceRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..")).Path "RedLoader"),
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
    [switch]$NoRestore,
    [switch]$UpdatePrebuilt,
    [switch]$NoUpdatePrebuilt
)

$ErrorActionPreference = "Stop"

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

function Resolve-RedLoaderCompatibilityDependency {
    param(
        [Parameter(Mandatory = $true)][string]$RedLoaderSourceRoot,
        [Parameter(Mandatory = $true)][string]$FileName
    )

    $candidatePaths = @(
        (Join-Path $RedLoaderSourceRoot "bin\Unity.IL2CPP\$FileName"),
        (Join-Path $RedLoaderSourceRoot "RedLoader\bin\Unity.IL2CPP\$FileName"),
        (Join-Path $RedLoaderSourceRoot "SonsSdk\bin\Debug\net6\$FileName"),
        (Join-Path $RedLoaderSourceRoot "GLTF\bin\Debug\net6\$FileName"),
        (Join-Path $RedLoaderSourceRoot "Libs\$FileName")
    )

    if ($env:USERPROFILE) {
        $candidatePaths += @(
            (Join-Path $env:USERPROFILE ".nuget\packages\samboy063.tomlet\5.4.0\lib\net6.0\$FileName"),
            (Join-Path $env:USERPROFILE ".nuget\packages\semanticversioning\3.0.0\lib\netstandard2.0\$FileName")
        )
    }

    foreach ($candidatePath in $candidatePaths) {
        if (Test-Path -LiteralPath $candidatePath) {
            return $candidatePath
        }
    }

    throw "Required RedLoader/SonsSdk compatibility dependency was not found: $FileName"
}

function Copy-RedLoaderCompatibilityDependencies {
    param(
        [Parameter(Mandatory = $true)][string]$RedLoaderSourceRoot,
        [Parameter(Mandatory = $true)][string[]]$Destinations
    )

    $dependencyFiles = @(
        "Tomlet.dll",
        "SemanticVersioning.dll",
        "Alt.Json.dll",
        "GLTF.dll"
    )

    foreach ($dependencyFile in $dependencyFiles) {
        $source = Resolve-RedLoaderCompatibilityDependency -RedLoaderSourceRoot $RedLoaderSourceRoot -FileName $dependencyFile
        foreach ($destination in $Destinations) {
            Copy-RequiredFile $source (Join-Path $destination $dependencyFile)
        }
    }
}

function Copy-OpenAcaiCompatibilityDependencyToTransitionalCore {
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [Parameter(Mandatory = $true)][string]$CompatibilityRoot,
        [Parameter(Mandatory = $true)][string]$FileName
    )

    $source = Join-Path $CompatibilityRoot $FileName
    $destination = Join-Path $PackageRoot "BepInEx\core\$FileName"
    Copy-RequiredFile $source $destination
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

function Remove-NonWindowsX64RuntimeAssets {
    param(
        [Parameter(Mandatory = $true)][string]$Root
    )

    $runtimeRoot = Join-Path $Root "BepInEx\core\runtimes"
    if (-not (Test-Path -LiteralPath $runtimeRoot)) {
        return
    }

    Get-ChildItem -LiteralPath $runtimeRoot -Directory |
        Where-Object { $_.Name -ne "win-x64" } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
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

function Get-DobbyNativeDetourDll {
    param(
        [Parameter(Mandatory = $true)][string]$CacheRoot,
        [Parameter(Mandatory = $true)][string]$Version,
        [string]$DownloadUrl
    )

    if ([string]::IsNullOrWhiteSpace($DownloadUrl)) {
        $DownloadUrl = "https://github.com/BepInEx/Dobby/releases/download/v$Version/dobby-win.zip"
    }

    $dobbyRoot = Join-Path $CacheRoot "native-detours\dobby\$Version"
    $dobbyZip = Join-Path $dobbyRoot "dobby-win.zip"
    $extractRoot = Join-Path $dobbyRoot "win"
    $dobbyX64 = Join-Path $extractRoot "dobby_x64.dll"

    if (-not (Test-Path -LiteralPath $dobbyX64)) {
        New-Item -ItemType Directory -Force -Path $dobbyRoot | Out-Null
        if (-not (Test-Path -LiteralPath $dobbyZip)) {
            Write-Host "Downloading Dobby native detour provider $Version"
            Invoke-WebRequest -Uri $DownloadUrl -OutFile $dobbyZip
        }

        if (Test-Path -LiteralPath $extractRoot) {
            Remove-Item -LiteralPath $extractRoot -Recurse -Force
        }

        Expand-Archive -LiteralPath $dobbyZip -DestinationPath $extractRoot -Force
    }

    if (-not (Test-Path -LiteralPath $dobbyX64)) {
        throw "Dobby win-x64 native detour provider was not found after extracting $dobbyZip"
    }

    return $dobbyX64
}

function Copy-NativeDetourProviders {
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [Parameter(Mandatory = $true)][string]$CacheRoot,
        [Parameter(Mandatory = $true)][string]$DobbyVersion,
        [string]$DobbyWinZipUrl
    )

    $dobbyDll = Get-DobbyNativeDetourDll -CacheRoot $CacheRoot -Version $DobbyVersion -DownloadUrl $DobbyWinZipUrl
    Copy-RequiredFile $dobbyDll (Join-Path $PackageRoot "BepInEx\core\dobby.dll")
    Copy-RequiredFile $dobbyDll (Join-Path $PackageRoot "BepInEx\core\runtimes\win-x64\native\dobby.dll")
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
        [Parameter(Mandatory = $true)][string]$FallbackSource,
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
            runtimeConfigRelativePath = "OpenACAI/Runtime/OpenACAI.Endnight.Loader.runtimeconfig.json"
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

    $expected = @(Get-NativeHostExpectedExports -DefinitionPath $DefinitionPath)
    $actual = @(Get-NativeHostActualExports -DllPath $DllPath)
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
        exportCount = $actual.Count
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
        $revision = & git -c "safe.directory=$Repository" -C $Repository rev-parse $Ref 2>$null
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
        $status = & git -c "safe.directory=$Repository" -C $Repository status --porcelain 2>$null
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

    $openAcaiRuntimeRoot = Join-Path $Root "OpenACAI\Runtime"
    foreach ($runtimeFile in @("OpenACAI.Endnight.Loader.Bootstrap.dll", "OpenACAI.Endnight.Loader.Preloader.dll", "OpenACAI.Endnight.Loader.Core.dll")) {
        $runtimePath = Join-Path $openAcaiRuntimeRoot $runtimeFile
        if (Test-Path -LiteralPath $runtimePath) {
            $paths.Add($runtimePath)
        }
    }

    $openAcaiCompatibilityRoot = Join-Path $openAcaiRuntimeRoot "Compatibility"
    foreach ($compatFile in @("RedLoader.dll", "SonsSdk.dll", "Tomlet.dll", "SemanticVersioning.dll", "Alt.Json.dll", "GLTF.dll")) {
        $compatPath = Join-Path $openAcaiCompatibilityRoot $compatFile
        if (Test-Path -LiteralPath $compatPath) {
            $paths.Add($compatPath)
        }
    }

    $bootstrapMirrorRoot = Join-Path $Root "BepInEx\plugins\OpenACAILoader"
    foreach ($mirrorFile in @("OpenACAI.Endnight.Loader.Core.dll", "RedLoader.dll", "SonsSdk.dll", "Tomlet.dll", "SemanticVersioning.dll", "Alt.Json.dll", "GLTF.dll")) {
        $mirrorPath = Join-Path $bootstrapMirrorRoot $mirrorFile
        if (Test-Path -LiteralPath $mirrorPath) {
            $paths.Add($mirrorPath)
        }
    }

    $clientCompanion = Join-Path $Root "BepInEx\plugins\Modstoke.ClientCompanion\Modstoke.ClientCompanion.dll"
    if (Test-Path -LiteralPath $clientCompanion) {
        $paths.Add($clientCompanion)
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
$workspaceRoot = Resolve-Path (Join-Path $suiteRoot "..")
$toolCacheRoot = Join-Path $suiteRoot ".tools"
$bootstrapProject = Join-Path $suiteRoot "src\OpenACAI.Endnight.Loader.Bootstrap\OpenACAI.Endnight.Loader.Bootstrap.csproj"
$bootstrapOutput = Join-Path $suiteRoot "src\OpenACAI.Endnight.Loader.Bootstrap\bin\$Configuration\OpenACAI.Endnight.Loader.Bootstrap.dll"
$preloaderProject = Join-Path $suiteRoot "src\OpenACAI.Endnight.Loader.Preloader\OpenACAI.Endnight.Loader.Preloader.csproj"
$preloaderOutput = Join-Path $suiteRoot "src\OpenACAI.Endnight.Loader.Preloader\bin\$Configuration\OpenACAI.Endnight.Loader.Preloader.dll"
$unityCommonOutputRoot = Join-Path $suiteRoot "src\Modstoke.Compatibility.BepInEx.Unity.Common\bin\$Configuration"
$preloaderCompatibilityOutputRoot = Join-Path $suiteRoot "src\Modstoke.Compatibility.BepInEx.Preloader.Core\bin\$Configuration"
$nativeHostProjectRoot = Join-Path $suiteRoot "src\OpenACAI.Endnight.Loader.NativeHost"
$nativeHostCargoManifest = Join-Path $nativeHostProjectRoot "Cargo.toml"
$nativeHostExportDefinition = Join-Path $nativeHostProjectRoot "native_host_exports.def"
$nativeHostCargoProfile = if ($Configuration -eq "Debug") { "debug" } else { "release" }
$nativeHostOutput = Join-Path $nativeHostProjectRoot "target\$nativeHostCargoProfile\openacai_endnight_loader_native_host.dll"
$compatProject = Join-Path $suiteRoot "src\OpenACAI.Endnight.Loader.Core\OpenACAI.Endnight.Loader.Core.csproj"
$compatOutput = Join-Path $suiteRoot "src\OpenACAI.Endnight.Loader.Core\bin\$Configuration\OpenACAI.Endnight.Loader.Core.dll"
$clientCompanionProject = Join-Path $suiteRoot "src\Modstoke.ClientCompanion\Modstoke.ClientCompanion.csproj"
$clientCompanionOutput = Join-Path $suiteRoot "src\Modstoke.ClientCompanion\bin\$Configuration\net11.0\Modstoke.ClientCompanion.dll"
$clientCompanionRelativePath = "BepInEx/plugins/Modstoke.ClientCompanion/Modstoke.ClientCompanion.dll"
$redLoaderSourceRoot = $RedLoaderSourceRoot
$gameInteropRoot = Join-Path $GameRoot "BepInEx\interop"
$distRoot = Join-Path $suiteRoot "dist"
$packageRoot = Join-Path $distRoot "OpenACAILoader"
$zipPath = Join-Path $distRoot "OpenACAILoader.zip"
$manifestPath = Join-Path $distRoot "OpenACAILoader.manifest.json"
$prebuiltRoot = Join-Path $suiteRoot "prebuilt"
$loaderManifest = Get-Content -LiteralPath (Join-Path $suiteRoot "openacai-loader.manifest.json") -Raw | ConvertFrom-Json
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
$bepInExCoreBuildOutput = Join-Path $BepInExForkRoot "Runtimes\Unity\BepInEx.Unity.IL2CPP\bin\Unity.IL2CPP"
$doorstopOutputRoot = Join-Path $DoorstopForkRoot "build\windows\x64\release"

if (-not (Test-Path -LiteralPath $bootstrapProject)) {
    throw "Bootstrap project was not found: $bootstrapProject"
}

if (-not (Test-Path -LiteralPath $preloaderProject)) {
    throw "Preloader project was not found: $preloaderProject"
}

if (-not (Test-Path -LiteralPath $nativeHostCargoManifest)) {
    throw "OpenACAI Rust native host project was not found: $nativeHostCargoManifest"
}

if (-not (Test-Path -LiteralPath $nativeHostExportDefinition)) {
    throw "OpenACAI Rust native host export definition was not found: $nativeHostExportDefinition"
}

if (-not (Test-Path -LiteralPath $compatProject)) {
    throw "Compatibility project was not found: $compatProject"
}

if (-not (Test-Path -LiteralPath $clientCompanionProject)) {
    throw "Client companion project was not found: $clientCompanionProject"
}
$clientCompanionVersion = ([xml](Get-Content -LiteralPath $clientCompanionProject -Raw)).Project.PropertyGroup.Version

if (-not (Test-Path -LiteralPath (Join-Path $GameRoot "SonsOfTheForest.exe"))) {
    throw "GameRoot does not look like a Sons Of The Forest install: $GameRoot"
}

if (-not (Test-Path -LiteralPath (Join-Path $redLoaderSourceRoot "SonsSdk\bin\Debug\net6\SonsSdk.dll"))) {
    throw "RedLoader/SonsSdk support assemblies were not found. Build or restore RedLoader first: $redLoaderSourceRoot"
}

if ($UseHostFxrLayout) {
    $bepInExIl2CppProject = Join-Path $BepInExForkRoot "Runtimes\Unity\BepInEx.Unity.IL2CPP\BepInEx.Unity.IL2CPP.csproj"
    if (-not (Test-Path -LiteralPath $bepInExIl2CppProject)) {
        throw "Experimental BepInEx IL2CPP project was not found: $bepInExIl2CppProject"
    }

    $bepInExRuntimeOutputs = @(
        (Join-Path $bepInExCoreBuildOutput "BepInEx.Core.dll"),
        (Join-Path $bepInExCoreBuildOutput "BepInEx.Preloader.Core.dll"),
        (Join-Path $bepInExCoreBuildOutput "BepInEx.Unity.Common.dll"),
        (Join-Path $bepInExCoreBuildOutput "BepInEx.Unity.IL2CPP.dll")
    )
    if ($RebuildExternalRuntime -or -not (Test-RequiredFiles -Paths $bepInExRuntimeOutputs)) {
        dotnet build $bepInExIl2CppProject --configuration $Configuration --nologo --verbosity minimal -p:GeneratePackageOnBuild=false
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed for BepInEx IL2CPP: $bepInExIl2CppProject"
        }
    } else {
        Write-Host "Reusing existing BepInEx IL2CPP output. Pass -RebuildExternalRuntime to rebuild it."
    }

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
        Write-Host "Building OpenACAI Rust native host."
        Push-Location $nativeHostProjectRoot
        try {
            if ($nativeHostCargoProfile -eq "debug") {
                cargo build
            } else {
                cargo build --release
            }
            if ($LASTEXITCODE -ne 0) {
                throw "cargo build failed for OpenACAI Rust native host: $nativeHostProjectRoot"
            }
        }
        finally {
            Pop-Location
        }

        if (-not (Test-Path -LiteralPath $nativeHostOutput)) {
            throw "OpenACAI Rust native host build did not produce: $nativeHostOutput"
        }
    }
}

$bepInExCoreReferenceRoot = if ($UseHostFxrLayout) { $bepInExCoreBuildOutput } else { Join-Path $GameRoot "BepInEx\core" }
if ($NoRestore) { $restoreArguments = @("--no-restore") }
else { $restoreArguments = @() }
dotnet build $preloaderProject @restoreArguments --configuration $Configuration --nologo --verbosity minimal -p:TargetFramework=$compatTargetFramework -p:GameRoot="$GameRoot" -p:BepInExCoreRoot="$bepInExCoreReferenceRoot"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed for OpenACAI preloader: $preloaderProject"
}
dotnet build $bootstrapProject @restoreArguments --configuration $Configuration --nologo --verbosity minimal -p:TargetFramework=$compatTargetFramework
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed for OpenACAI bootstrap: $bootstrapProject"
}
dotnet build $compatProject @restoreArguments --configuration $Configuration --nologo --verbosity minimal -p:TargetFramework=$compatTargetFramework -p:GameRoot="$GameRoot" -p:BepInExCoreRoot="$bepInExCoreReferenceRoot" -p:RedLoaderSourceRoot="$redLoaderSourceRoot"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed for OpenACAI core: $compatProject"
}
dotnet build $clientCompanionProject @restoreArguments --configuration $Configuration --nologo --verbosity minimal -p:GameRoot="$GameRoot" -p:OpenACAICorePath="$compatOutput" -p:BepInExCoreRoot="$bepInExCoreReferenceRoot" -p:GameInteropRoot="$gameInteropRoot"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed for Modstoke client companion: $clientCompanionProject"
}

$resolvedDistRoot = [System.IO.Path]::GetFullPath($distRoot)
$resolvedPackageRoot = [System.IO.Path]::GetFullPath($packageRoot)
if (-not $resolvedPackageRoot.StartsWith($resolvedDistRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean package output outside dist: $resolvedPackageRoot"
}

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
$openAcaiRootDestination = Join-Path $packageRoot "OpenACAI"
$openAcaiRuntimeDestination = Join-Path $openAcaiRootDestination "Runtime"
$openAcaiCompatibilityDestination = Join-Path $openAcaiRuntimeDestination "Compatibility"
$bootstrapMirrorDestination = Join-Path $packageRoot "BepInEx\plugins\OpenACAILoader"
New-Item -ItemType Directory -Force -Path $openAcaiRuntimeDestination | Out-Null
New-Item -ItemType Directory -Force -Path $openAcaiCompatibilityDestination | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $openAcaiRootDestination "Mods") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $openAcaiRootDestination "UserData") | Out-Null
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

$dotnetRuntime = Copy-DotnetRuntime -Destination (Join-Path $packageRoot "dotnet") -FallbackSource (Join-Path $GameRoot "dotnet") -Version $loaderManifest.runtime.dotnetRuntime -HostFxrLayout $UseHostFxrLayout
$dotnetRuntimeSource = Get-DotnetRuntimeDirectory -Version $loaderManifest.runtime.dotnetRuntime
$dotnetLicenseRoot = [System.IO.Path]::GetFullPath((Join-Path $dotnetRuntimeSource "..\..\.."))
Copy-RequiredFile (Join-Path $dotnetLicenseRoot "LICENSE.txt") (Join-Path $packageRoot "dotnet\LICENSE.txt")
Copy-RequiredFile (Join-Path $dotnetLicenseRoot "ThirdPartyNotices.txt") (Join-Path $packageRoot "dotnet\ThirdPartyNotices.txt")

$bepInExRoot = Join-Path $GameRoot "BepInEx"
$bepInExCoreSource = Join-Path $bepInExRoot "core"
if ($UseHostFxrLayout) {
    $bepInExCoreSource = $bepInExCoreBuildOutput
}
Copy-Directory $bepInExCoreSource (Join-Path $packageRoot "BepInEx\core")
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
$legacyRuntimeConfigPath = Join-Path $packageRoot "BepInEx\core\BepInEx.Unity.IL2CPP.runtimeconfig.json"
if (Test-Path -LiteralPath $legacyRuntimeConfigPath) {
    Remove-Item -LiteralPath $legacyRuntimeConfigPath -Force
}
if (-not $IncludeAllNativeRuntimeAssets) {
    Remove-NonWindowsX64RuntimeAssets -Root $packageRoot
}
Copy-NativeDetourProviders -PackageRoot $packageRoot -CacheRoot $toolCacheRoot -DobbyVersion $DobbyVersion -DobbyWinZipUrl $DobbyWinZipUrl
New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot "BepInEx\config") | Out-Null
# A fresh runtime binds its config defaults and downloads the matching Unity base
# library archive on first interop generation. Never package a developer's config,
# patchers, or cached Unity libraries from the local game installation.
New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot "BepInEx\patchers") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot "BepInEx\unity-libs") | Out-Null

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
target_assembly = OpenACAI\Runtime\OpenACAI.Endnight.Loader.Bootstrap.dll
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

Copy-RequiredFile $bootstrapOutput (Join-Path $openAcaiRuntimeDestination "OpenACAI.Endnight.Loader.Bootstrap.dll")
Copy-RequiredFile $preloaderOutput (Join-Path $openAcaiRuntimeDestination "OpenACAI.Endnight.Loader.Preloader.dll")
Copy-RequiredFile $compatOutput (Join-Path $openAcaiRuntimeDestination "OpenACAI.Endnight.Loader.Core.dll")
Copy-RequiredFile (Join-Path $redLoaderSourceRoot "SonsSdk\bin\Debug\net6\RedLoader.dll") (Join-Path $openAcaiCompatibilityDestination "RedLoader.dll")
Copy-RequiredFile (Join-Path $redLoaderSourceRoot "SonsSdk\bin\Debug\net6\SonsSdk.dll") (Join-Path $openAcaiCompatibilityDestination "SonsSdk.dll")
Copy-RequiredFile (Join-Path $suiteRoot "openacai-loader.manifest.json") (Join-Path $openAcaiRuntimeDestination "openacai-loader.manifest.json")

New-Item -ItemType Directory -Force -Path $bootstrapMirrorDestination | Out-Null
Copy-RedLoaderCompatibilityDependencies -RedLoaderSourceRoot $redLoaderSourceRoot -Destinations @($openAcaiCompatibilityDestination, $bootstrapMirrorDestination)
Copy-OpenAcaiCompatibilityDependencyToTransitionalCore -PackageRoot $packageRoot -CompatibilityRoot $openAcaiCompatibilityDestination -FileName "SemanticVersioning.dll"
Copy-RequiredFile (Join-Path $openAcaiRuntimeDestination "OpenACAI.Endnight.Loader.Core.dll") (Join-Path $bootstrapMirrorDestination "OpenACAI.Endnight.Loader.Core.dll")
Copy-RequiredFile (Join-Path $openAcaiCompatibilityDestination "RedLoader.dll") (Join-Path $bootstrapMirrorDestination "RedLoader.dll")
Copy-RequiredFile (Join-Path $openAcaiCompatibilityDestination "SonsSdk.dll") (Join-Path $bootstrapMirrorDestination "SonsSdk.dll")
Copy-RequiredFile (Join-Path $openAcaiRuntimeDestination "openacai-loader.manifest.json") (Join-Path $bootstrapMirrorDestination "openacai-loader.manifest.json")
Copy-RequiredFile $clientCompanionOutput (Join-Path $packageRoot "BepInEx\plugins\Modstoke.ClientCompanion\Modstoke.ClientCompanion.dll")
Copy-RequiredFile (Join-Path $suiteRoot "LICENSE") (Join-Path $packageRoot "LICENSE")
Copy-RequiredFile (Join-Path $suiteRoot "NOTICE") (Join-Path $packageRoot "NOTICE")
Copy-RequiredFile (Join-Path $suiteRoot "THIRD_PARTY_NOTICES.md") (Join-Path $packageRoot "THIRD_PARTY_NOTICES.md")
Copy-Directory (Join-Path $suiteRoot "LICENSES") (Join-Path $packageRoot "LICENSES")

if ($SignArtifacts) {
    Invoke-AzureCodeSigning -Files (Get-LoaderSignableFiles -Root $packageRoot) -Description "Modstoke Loader by OpenACAI"
}

$initialFileEntries = @(Get-PackageFileEntries -Root $packageRoot)
$clientCompanionFileEntry = $initialFileEntries | Where-Object { $_.path -eq $clientCompanionRelativePath } | Select-Object -First 1
if (-not $clientCompanionFileEntry) {
    throw "Packaged client companion was not included in the file manifest: $clientCompanionRelativePath"
}
$components = [pscustomobject]@{
    loader = [pscustomobject]@{
        version = $loaderVersion
        bootstrapRelativePath = "OpenACAI/Runtime/OpenACAI.Endnight.Loader.Bootstrap.dll"
    }
    sdk = [pscustomobject]@{
        version = $loaderVersion
        bundledInLoaderCore = $true
        assemblyRelativePath = "OpenACAI/Runtime/OpenACAI.Endnight.Loader.Core.dll"
    }
    clientCompanion = [pscustomobject]@{
        version = $clientCompanionVersion
        pluginId = "com.modstoke.sotf.clientcompanion"
        assemblyRelativePath = $clientCompanionRelativePath
        sha256 = $clientCompanionFileEntry.sha256
        inventorySharingDefault = "off"
    }
}
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
        project = "src/OpenACAI.Endnight.Loader.NativeHost"
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
    modstokePreloaderCompatibility = [pscustomobject]@{
        project = "src/Modstoke.Compatibility.BepInEx.Preloader.Core"
        output = "BepInEx/core/BepInEx.Preloader.Core.dll"
        assemblyIdentity = "BepInEx.Preloader.Core, Version=6.0.0.0, Culture=neutral, PublicKeyToken=null"
        patchingEngine = "OpenACAI.Endnight.Loader.Preloader"
        license = "LGPL-2.1-only"
    }
    bepinex = [pscustomobject]@{
        upstream = "https://github.com/BepInEx/BepInEx"
        fork = "https://github.com/desertofunknown/openacai-bepinex"
        branch = "openacai-net11-preview"
        upstreamBaseRef = "upstream/master"
        upstreamBaseCommit = Get-GitRevision -Repository $BepInExForkRoot -Ref "upstream/master"
        forkCommit = Get-GitRevision -Repository $BepInExForkRoot
        dirty = Test-GitDirty -Repository $BepInExForkRoot
        patchDirectory = "patches/bepinex"
    }
    unityDoorstop = [pscustomobject]@{
        upstream = "https://github.com/NeighTools/UnityDoorstop"
        fork = "https://github.com/desertofunknown/openacai-unitydoorstop"
        branch = "openacai-hostfxr-net10"
        packaged = [bool]$UseDoorstopForkNativeHost
        upstreamBaseRef = "origin/master"
        upstreamBaseCommit = Get-GitRevision -Repository $DoorstopForkRoot -Ref "origin/master"
        forkCommit = Get-GitRevision -Repository $DoorstopForkRoot
        dirty = Test-GitDirty -Repository $DoorstopForkRoot
        patchDirectory = "patches/unitydoorstop"
    }
    redLoaderSonsSdk = [pscustomobject]@{
        upstream = "https://github.com/ToniMacaroni/RedLoader"
        fork = "https://github.com/desertofunknown/openacai-redloader"
        branch = "openacai-loader-compat"
        upstreamBaseRef = "origin/rewrite"
        upstreamBaseCommit = Get-GitRevision -Repository $redLoaderSourceRoot -Ref "origin/rewrite"
        forkCommit = Get-GitRevision -Repository $redLoaderSourceRoot
        dirty = Test-GitDirty -Repository $redLoaderSourceRoot
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
    components = $components
    sourceTrail = $sourceTrail
    files = $initialFileEntries
}

$inPackageManifestPath = Join-Path $openAcaiRuntimeDestination "openacai-loader-files.json"
$inPackageManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $inPackageManifestPath -Encoding UTF8
$bootstrapMirrorManifestPath = Join-Path $bootstrapMirrorDestination "openacai-loader-files.json"
Copy-Item -LiteralPath $inPackageManifestPath -Destination $bootstrapMirrorManifestPath -Force
$fileEntries = @(Get-PackageFileEntries -Root $packageRoot)

$zipCompressor = Compress-PackageZip -SourceRoot $packageRoot -DestinationZip $zipPath
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
$distShaPath = Join-Path $distRoot "OpenACAILoader.zip.sha256"
"$zipHash  OpenACAILoader.zip" | Set-Content -LiteralPath $distShaPath -Encoding ASCII

$runtimeSupportPolicy = if ($UseHostFxrLayout -and -not $UseDoorstopForkNativeHost) {
    "OpenACAI Rust native host starts hostfxr/CoreCLR for the OpenACAI .NET $DotnetRuntimeMajor modernization branch"
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
    components = $components
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
        primaryRuntimeDirectory = "OpenACAI/Runtime"
        runtimeCompatibilityDirectory = "OpenACAI/Runtime/Compatibility"
        nativeModsDirectory = "OpenACAI/Mods"
        userDataDirectory = "OpenACAI/UserData"
        bootstrapCompatibilityMirror = "BepInEx/plugins/OpenACAILoader"
        legacyCompatibilityRoots = @(
            "Mods",
            "Libs",
            "BepInEx/plugins"
        )
        stateFile = "OpenACAI/Runtime/openacai-loader-state.json"
        chainloaderStateFile = "OpenACAI/Runtime/openacai-chainloader-state.json"
        il2CppPreloaderStateFile = "OpenACAI/Runtime/openacai-il2cpp-preloader-state.json"
        il2CppChainloaderStateFile = "OpenACAI/Runtime/openacai-il2cpp-chainloader-state.json"
        stateFileCompatibilityMirror = "BepInEx/plugins/OpenACAILoader/openacai-loader-state.json"
        fileManifest = "OpenACAI/Runtime/openacai-loader-files.json"
        fileManifestCompatibilityMirror = "BepInEx/plugins/OpenACAILoader/openacai-loader-files.json"
    }
    runtime = [pscustomobject]@{
        targetFramework = $compatTargetFramework
        dotnetRuntimeVersion = $dotnetRuntime.version
        layout = $dotnetRuntime.layout
        targetAssemblyRelativePath = "OpenACAI/Runtime/OpenACAI.Endnight.Loader.Bootstrap.dll"
        coreclrRelativePath = $dotnetRuntime.coreclrRelativePath
        corlibRelativePath = $dotnetRuntime.corlibRelativePath
        hostfxrRelativePath = $dotnetRuntime.hostfxrRelativePath
        runtimeConfigRelativePath = $dotnetRuntime.runtimeConfigRelativePath
        supportPolicy = $runtimeSupportPolicy
        nativeHost = if ($UseDoorstopForkNativeHost) { "UnityDoorstop fork" } else { "OpenACAI Rust native host" }
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
    Write-Host "Prebuilt folder was not refreshed. Upload dist\\OpenACAILoader.zip, dist\\OpenACAILoader.manifest.json, and dist\\OpenACAILoader.zip.sha256 as GitHub release assets."
}

Write-Host "Modstoke Loader by OpenACAI package written to $zipPath"
Write-Host "Modstoke Loader by OpenACAI manifest written to $manifestPath"
Write-Host "Modstoke Loader by OpenACAI checksum written to $distShaPath"
