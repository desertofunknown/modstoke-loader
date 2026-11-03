#requires -Version 7.2
<#
.SYNOPSIS
Captures current fork sources and stages a Windows x64 Rust dependency candidate.
.DESCRIPTION
Invoke only after fork writers have finished. OutputDirectory must be new, with an
existing parent outside the game installation and forks. No install, packaging,
inventory changes, tests, dependency updates, or CMake builds are performed.
Cargo.lock files and cached packages must already exist; restores are offline.
Failure leaves logs and partial output for inspection. Never reuse that directory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$OutputDirectory,
    [string]$ForkRoot = (Join-Path $PSScriptRoot '..\..\Forks\ReleaseDependencies'),
    [string]$DotNet = (Join-Path $PSScriptRoot '..\.tools\dotnet-11-rc3\dotnet.exe'),
    [string]$Cargo = 'cargo',
    [string]$Rustc = 'rustc',
    [string]$Linker = 'lld-link.exe',
    [string]$NuGetPackages = 'C:\Users\alext\.nuget\packages'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8 = [Text.UTF8Encoding]::new($false)
$target = 'x86_64-pc-windows-msvc'
$sdkVersion = '11.0.100-rc.3.26522.148'

function Get-FullPath([string]$Path) {
    return [IO.Path]::TrimEndingDirectorySeparator($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path))
}

function Test-Within([string]$Path, [string]$Root) {
    return $Path.Equals($Root, [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoLinks([string]$Path) {
    for ($current = $Path; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -Force -LiteralPath $current).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Reparse points are not permitted in source/output paths: $current"
            }
        }
    }
}

function Write-NewText([string]$Path, [string]$Text) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try {
        $bytes = $utf8.GetBytes($Text)
        $stream.Write($bytes, 0, $bytes.Length)
    } finally { $stream.Dispose() }
}

function Write-NewJson([string]$Path, $Value) {
    Write-NewText $Path (($Value | ConvertTo-Json -Depth 30) + "`n")
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextSha256([string]$Text) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($Text))).ToLowerInvariant()
}

function Get-FileRecord([string]$Root, [string]$Relative) {
    $path = Join-Path $Root $Relative
    Assert-NoLinks $path
    $item = Get-Item -Force -LiteralPath $path
    if ($item.PSIsContainer) { throw "Expected a source file: $path" }
    return [ordered]@{ path = $Relative; sha256 = Get-Sha256 $path; sizeBytes = $item.Length }
}

function Get-ManifestHash($Files) {
    $lines = foreach ($file in $Files) { "$($file.sha256)`t$($file.sizeBytes)`t$($file.path)`n" }
    return Get-TextSha256 ($lines -join '')
}

function Invoke-RecordedCommand([string]$Name, [string]$Executable, [string[]]$Arguments,
    [string]$WorkingDirectory, [hashtable]$Environment = @{}) {
    $prefix = Join-Path $logs $Name
    Write-NewJson "$prefix.command.json" ([ordered]@{
        executable = $Executable; arguments = $Arguments; workingDirectory = $WorkingDirectory
        environmentOverrides = $Environment; startedUtc = [DateTime]::UtcNow.ToString('O')
    })
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Executable
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = $utf8
    $info.StandardErrorEncoding = $utf8
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($entry in $Environment.GetEnumerator()) { $info.Environment[$entry.Key] = $entry.Value }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (!$process.Start()) { throw "Could not start $Executable" }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
    } finally { $process.Dispose() }
    Write-NewText "$prefix.stdout.log" $stdout
    Write-NewText "$prefix.stderr.log" $stderr
    # Do not mistake MSBuild's "0 Warning(s)" summary for a diagnostic.
    $warningPattern = '(?im)(^\s*warning(?:\[[^\]]+\])?\s*:|\bwarning\s+[A-Z]+[0-9]+\s*:|\bwarning LNK[0-9]+\s*:|lld-link: warning:)'
    $hasWarnings = $stderr -match $warningPattern
    if ($Name -notmatch '-diff-') { $hasWarnings = $hasWarnings -or $stdout -match $warningPattern }
    Write-NewJson "$prefix.result.json" ([ordered]@{ exitCode = $exitCode; warnings = $hasWarnings })
    if ($exitCode -ne 0 -or $hasWarnings) {
        throw "Command $Name failed (exit=$exitCode, warnings=$hasWarnings). See $prefix.*.log"
    }
    return $stdout
}

function Invoke-Git([string]$Name, [string]$Root, [string[]]$Arguments) {
    return Invoke-RecordedCommand $Name $git (@('--no-pager', '-c', 'core.quotepath=false', '-C', $Root) + $Arguments) $output @{
        GIT_OPTIONAL_LOCKS = '0'; GIT_TERMINAL_PROMPT = '0'
    }
}

function Test-SourcePath([string]$Name, [string]$Path) {
    if ($Path -match '[\x00-\x1f\\:]|(^|/)\.\.(/|$)' -or [IO.Path]::IsPathRooted($Path)) {
        throw "Unsupported Git source path: $Path"
    }
    if ($Path -match '(?i)(^|/)(\.git|\.vs|\.vscode|bin|obj|target|build[^/]*|[^/]*-build[^/]*|out|dist|artifacts|test[^/]*|bench[^/]*|private)/') {
        return $false
    }
    if ($Path -match '^(LICENSE|LICENCE|COPYING|NOTICE|COPYRIGHT|README)([^/]*)$') { return $true }
    $crate = if ($Name -eq 'Dobby') { 'modstoke-detours' } else { 'modstoke-json' }
    if ($Path -match "^native/$crate/(Cargo\.(toml|lock)|build\.rs|rust-toolchain(?:\.toml)?|\.cargo/config\.toml|(LICENSE|LICENCE|COPYING|NOTICE|COPYRIGHT|README)[^/]*|src/.+\.rs|include/.+\.(h|hpp))$") {
        return $true
    }
    if ($Name -eq 'Alt.Json') {
        return $Path -match '^Src/Newtonsoft\.Json/(.+\.cs|Newtonsoft\.Json\.csproj)$'
    }
    # The old C++ backend is captured as reference, never compiled or linked.
    return $Path -eq 'CMakeLists.txt' -or $Path -eq 'example/CMakeLists.txt' -or
        $Path -match '^(source|include|external|builtin-plugin|cmake)/.+(\.(c|cc|cpp|h|hh|hpp|inc|inl|S|asm|mm|defs|cmake|in)|/CMakeLists\.txt|/(LICENSE|LICENCE|COPYING|NOTICE|COPYRIGHT)[^/]*)$'
}

function Get-DirtyPatch([string]$Name, [string]$Root, [string[]]$Paths, [switch]$Cached) {
    $parts = [Collections.Generic.List[string]]::new()
    for ($offset = 0; $offset -lt $Paths.Count; $offset += 32) {
        $end = [Math]::Min($offset + 31, $Paths.Count - 1)
        $arguments = @('diff', '--binary', '--full-index', '--no-ext-diff', '--no-textconv', '--no-color', '--no-renames')
        if ($Cached) { $arguments += '--cached' }
        $arguments += @('--') + @($Paths[$offset..$end] | ForEach-Object { ":(literal)$_" })
        $parts.Add((Invoke-Git "$Name-diff-$offset" $Root $arguments))
    }
    return $parts -join ''
}

function Get-ForkState([string]$Name, [string]$Root, [string]$Phase) {
    $prefix = "$Name-$Phase"
    $head = (Invoke-Git "$prefix-head" $Root @('rev-parse', '--verify', 'HEAD')).Trim()
    $baseText = Invoke-Git "$prefix-base-paths" $Root @('ls-tree', '-r', '--name-only', '-z', 'HEAD')
    $trackedText = Invoke-Git "$prefix-tracked" $Root @('ls-files', '--cached', '-z')
    $untrackedText = Invoke-Git "$prefix-untracked" $Root @('ls-files', '--others', '--exclude-standard', '-z')
    $tracked = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $paths = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    $basePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $baseText.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
        if (Test-SourcePath $Name $path) { [void]$basePaths.Add($path); [void]$paths.Add($path) }
    }
    foreach ($path in $trackedText.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
        if (Test-SourcePath $Name $path) { [void]$tracked.Add($path); [void]$paths.Add($path) }
    }
    $untracked = @($untrackedText.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries) | Where-Object { Test-SourcePath $Name $_ })
    foreach ($path in $untracked) { [void]$paths.Add($path) }
    $files = @()
    $deleted = @()
    foreach ($path in $paths) {
        if (Test-Path -LiteralPath (Join-Path $Root $path)) { $files += Get-FileRecord $Root $path }
        elseif ($tracked.Contains($path) -or $basePaths.Contains($path)) { $deleted += $path }
        else { throw "Source disappeared during snapshot: $Name/$path" }
    }
    if (!$files.Count) { throw "No allowlisted source in $Root" }
    $staged = Get-DirtyPatch "$prefix-staged" $Root @($paths) -Cached
    $unstaged = Get-DirtyPatch "$prefix-unstaged" $Root @($paths)
    return [ordered]@{
        name = $Name; baseGitHEAD = $head; snapshotSHA256 = Get-ManifestHash $files
        files = $files; deletedTrackedPaths = $deleted; untrackedPaths = @($paths | Where-Object { $_ -in $untracked })
        stagedDiff = $staged; unstagedDiff = $unstaged
    }
}

function Copy-NewFile([string]$Source, [string]$Destination) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination))
    [IO.File]::Copy($Source, $Destination, $false)
}

function Save-RegistrySources($Package) {
    $key = "$($Package.name)-$($Package.version)"
    if ($dependencyRecords.Contains($key)) { return }
    if ($Package.source -notlike 'registry+*') { throw "Only locked registry dependencies are supported: $key" }
    $root = [IO.Path]::GetDirectoryName($Package.manifest_path)
    Assert-NoLinks $root
    $registry = [IO.Directory]::GetParent($root)
    $archive = Join-Path $registry.Parent.Parent.FullName "cache/$($registry.Name)/$key.crate"
    if (!(Test-Path -LiteralPath $archive -PathType Leaf)) { throw "Missing cached registry archive: $archive" }
    $packageHash = Get-Sha256 $archive
    $paths = [string[]]@(Get-ChildItem -LiteralPath $root -File -Force -Recurse | Where-Object {
        $_.Name -notin @('.cargo-ok', '.cargo-checksum.json')
    } | ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/') })
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $files = foreach ($path in $paths) {
        $absolute = Get-FullPath (Join-Path $root $path)
        if (!(Test-Within $absolute $root)) { throw "Dependency source escapes its package: $key/$path" }
        $file = Get-FileRecord $root $path
        $file
    }
    $licensePaths = @($paths | Where-Object { [IO.Path]::GetFileName($_) -match '^(?i:licen[cs]e|copying|notice|copyright)([._-].*)?$' })
    if ($Package.license_file) {
        $licensePath = if ([IO.Path]::IsPathRooted($Package.license_file)) { $Package.license_file } else { Join-Path $root $Package.license_file }
        $licenseRelative = [IO.Path]::GetRelativePath($root, $licensePath).Replace('\', '/')
        if ($licenseRelative -notin $licensePaths) { $licensePaths += $licenseRelative }
    }
    $licenseOutputs = @($licensePaths | ForEach-Object { "licenses/third-party/$key/$_" })
    $supplementalLicenseOrigin = $null
    if (!$licensePaths.Count -and $key -eq 'iced-x86-1.21.0' -and $Package.license -eq 'MIT') {
        $revision = 'c50f29b7bc305696895c075f3fc7719751426b12'
        $vcs = Get-Content -Raw -LiteralPath (Join-Path $root '.cargo_vcs_info.json') | ConvertFrom-Json
        if ($vcs.git.sha1 -ne $revision) { throw "Unexpected source revision for $key" }
        $licenseSource = Join-Path $output 'snapshots/Dobby/native/modstoke-detours/LICENSE.iced-x86'
        $licenseOutput = "licenses/third-party/$key/LICENSE.txt"
        Copy-NewFile $licenseSource (Join-Path $output $licenseOutput)
        $licenseOutputs += $licenseOutput
        $supplementalLicenseOrigin = "https://github.com/icedland/iced/blob/$revision/LICENSE.txt"
    }
    if (!$licenseOutputs.Count) { throw "No license files found for $key" }
    foreach ($path in $licensePaths) {
        if ($path -notin $paths) { throw "License is not in the captured source manifest: $key/$path" }
        Copy-NewFile (Join-Path $root $path) (Join-Path $output "licenses/third-party/$key/$path")
    }
    $manifestRelative = "provenance/dependencies/$key.sources.json"
    [void][IO.Directory]::CreateDirectory((Join-Path $output 'provenance/dependencies'))
    Write-NewJson (Join-Path $output $manifestRelative) ([ordered]@{
        name = $Package.name; version = $Package.version; source = $Package.source
        packageSHA256 = $packageHash; sourceSHA256 = Get-ManifestHash $files; files = @($files)
    })
    $dependencyRecords[$key] = [ordered]@{
        name = $Package.name; version = $Package.version; source = $Package.source; license = $Package.license
        packageSHA256 = $packageHash; sourceSHA256 = Get-ManifestHash $files
        sourceHashScope = 'Cached package files, excluding Cargo extraction markers; archive hash recorded separately.'
        sourceManifest = $manifestRelative; licenses = $licenseOutputs; supplementalLicenseOrigin = $supplementalLicenseOrigin
    }
}

$output = Get-FullPath $OutputDirectory
$forkRootPath = Get-FullPath $ForkRoot
$installRoot = Get-FullPath (Join-Path $PSScriptRoot '..\..\..')
$forks = [ordered]@{ 'Alt.Json' = (Join-Path $forkRootPath 'Alt.Json'); Dobby = (Join-Path $forkRootPath 'Dobby') }
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory; existing files and directories are never overwritten.' }
Assert-NoLinks $output
if (!(Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($output)) -PathType Container)) { throw 'OutputDirectory parent must already exist.' }
foreach ($root in @($installRoot, $forkRootPath) + @($forks.Values)) {
    if (Test-Within $output $root) { throw "OutputDirectory must be outside forks and the game installation: $root" }
}
foreach ($root in $forks.Values) {
    if (!(Test-Path -LiteralPath $root -PathType Container)) { throw "Missing fork: $root" }
    Assert-NoLinks $root
}
$dotnetPath = Get-FullPath $DotNet
$nugetPath = Get-FullPath $NuGetPackages
if (!(Test-Path -LiteralPath $dotnetPath -PathType Leaf)) { throw "Missing pinned .NET host: $dotnetPath" }
if (!(Test-Path -LiteralPath $nugetPath -PathType Container)) { throw "Missing original NuGet cache: $nugetPath" }
$git = (Get-Command git -CommandType Application -ErrorAction Stop).Source
$cargoPath = (Get-Command $Cargo -CommandType Application -ErrorAction Stop).Source
$rustcPath = (Get-Command $Rustc -CommandType Application -ErrorAction Stop).Source
$linkerPath = (Get-Command $Linker -CommandType Application -ErrorAction Stop).Source
New-Item -ItemType Directory -Path $output | Out-Null
$logs = Join-Path $output 'logs'
$provenance = Join-Path $output 'provenance'
foreach ($directory in @($logs, $provenance, (Join-Path $output 'snapshots'), (Join-Path $output 'work'))) {
    New-Item -ItemType Directory -Path $directory | Out-Null
}
$startedUtc = [DateTime]::UtcNow.ToString('O')

try {
    Copy-NewFile $PSCommandPath (Join-Path $provenance 'Build-NativeDependencyCandidate.ps1')
    $baseline = Join-Path $PSScriptRoot 'Build-MaintainedDependencies.ps1'
    Copy-NewFile $baseline (Join-Path $provenance 'Build-MaintainedDependencies.reference.ps1')
    Write-Host 'Capturing allowlisted fork sources; live forks must remain idle until capture completes.'
    $states = [ordered]@{}
    foreach ($name in $forks.Keys) {
        $root = $forks[$name]
        $top = (Invoke-Git "$name-root" $root @('rev-parse', '--show-toplevel')).Trim()
        if ((Get-FullPath $top) -ne $root) { throw "Expected a fork repository root: $root" }
        $states[$name] = Get-ForkState $name $root 'before'
    }
    foreach ($name in $forks.Keys) {
        foreach ($file in $states[$name].files) {
            $destination = Join-Path $output "snapshots/$name/$($file.path)"
            Copy-NewFile (Join-Path $forks[$name] $file.path) $destination
            if ((Get-Sha256 $destination) -ne $file.sha256) { throw "Source changed while copying: $name/$($file.path)" }
        }
    }
    $sourceRecords = @()
    foreach ($name in $forks.Keys) {
        $state = $states[$name]
        $after = Get-ForkState $name $forks[$name] 'after'
        if (($state | ConvertTo-Json -Depth 10 -Compress) -cne ($after | ConvertTo-Json -Depth 10 -Compress)) {
            throw "Source or Git state changed during capture: $name. Wait for writers and use a new output directory."
        }
        Write-NewJson (Join-Path $provenance "$name.snapshot.json") ([ordered]@{
            snapshotSHA256 = $state.snapshotSHA256; files = $state.files
            hashFormat = 'SHA256 of ordinal-path-sorted UTF-8 lines: lowercase sha256 TAB decimal size TAB slash-relative path LF'
        })
        Write-NewText (Join-Path $provenance "$name.staged.patch") $state.stagedDiff
        Write-NewText (Join-Path $provenance "$name.unstaged.patch") $state.unstagedDiff
        $sourceRecords += [ordered]@{
            name = $name; baseGitHEAD = $state.baseGitHEAD; snapshotSHA256 = $state.snapshotSHA256
            hasAllowlistedChanges = [bool]($state.stagedDiff -or $state.unstagedDiff -or $state.untrackedPaths.Count)
            untrackedPaths = $state.untrackedPaths; deletedTrackedPaths = $state.deletedTrackedPaths
            stagedDiff = "$name.staged.patch"; stagedDiffSHA256 = Get-TextSha256 $state.stagedDiff
            unstagedDiff = "$name.unstaged.patch"; unstagedDiffSHA256 = Get-TextSha256 $state.unstagedDiff
        }
    }
    $snapshotSHA256 = Get-TextSha256 (($sourceRecords | ForEach-Object { "$($_.name)`t$($_.snapshotSHA256)`n" }) -join '')
    Write-NewJson (Join-Path $provenance 'source-provenance.json') ([ordered]@{
        schemaVersion = 1; startedUtc = $startedUtc; snapshotSHA256 = $snapshotSHA256; sources = $sourceRecords
        cleanCommitBuild = $false; fullWorktreeCleanliness = 'Not asserted; only allowlisted source is captured.'
        selection = 'Git tracked plus nonignored untracked source; excludes build/test/private/.git artifacts.'
        consistency = 'Source bytes, HEAD, selected paths and index/worktree diffs matched before and after copying; no filesystem-wide atomic snapshot.'
        cppBackend = 'Dobby upstream C++ is reference only, not compiled or linked.'
    })
    Write-Host "Captured source snapshot $snapshotSHA256. Builds now use only owned snapshots."

    $rustEnvironment = @{
        CARGO_NET_OFFLINE = 'true'; CARGO_INCREMENTAL = '0'; CARGO_TERM_COLOR = 'never'; RUSTC = $rustcPath
        CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER = $linkerPath
        CARGO_ENCODED_RUSTFLAGS = (@('-Dwarnings', "--remap-path-prefix=$output=/_/candidate") -join [char]31)
    }
    $linkerVersion = (Invoke-RecordedCommand 'linker-version' $linkerPath @('--version') $output).Trim()
    $dependencyRecords = [ordered]@{}
    $crateRecords = @()
    $crates = @(
        @{ Name = 'json'; Fork = 'Alt.Json'; Directory = 'native/modstoke-json'; Library = 'modstoke_json_native' },
        @{ Name = 'detours'; Fork = 'Dobby'; Directory = 'native/modstoke-detours'; Library = 'dobby' }
    )
    foreach ($crate in $crates) {
        $crateRoot = Join-Path $output "snapshots/$($crate.Fork)/$($crate.Directory)"
        $manifest = Join-Path $crateRoot 'Cargo.toml'
        $lock = Join-Path $crateRoot 'Cargo.lock'
        foreach ($required in @($manifest, $lock)) {
            if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required snapshot input missing: $required. Complete the fork and lock/cache dependencies before invocation." }
        }
        Copy-NewFile $lock (Join-Path $provenance "$($crate.Name).Cargo.lock")
        $cargoVersion = (Invoke-RecordedCommand "$($crate.Name)-cargo-version" $cargoPath @('--version') $crateRoot $rustEnvironment).Trim()
        $rustVersion = (Invoke-RecordedCommand "$($crate.Name)-rustc-version" $rustcPath @('-Vv') $crateRoot $rustEnvironment).Trim()
        $targetDirectory = Join-Path $output "work/$($crate.Name)-target"
        $metadataEnvironment = $rustEnvironment.Clone()
        $metadataEnvironment['CARGO_TARGET_DIR'] = $targetDirectory
        $metadataText = Invoke-RecordedCommand "$($crate.Name)-metadata" $cargoPath @(
            'metadata', '--frozen', '--format-version', '1', '--filter-platform', $target, '--manifest-path', $manifest
        ) $crateRoot $metadataEnvironment
        $metadata = $metadataText | ConvertFrom-Json
        $local = @($metadata.packages | Where-Object { (Get-FullPath $_.manifest_path) -eq $manifest })
        if ($local.Count -ne 1 -or !@($local[0].targets | Where-Object { $_.name -eq $crate.Library -and 'cdylib' -in $_.crate_types }).Count) {
            throw "Expected cdylib '$($crate.Library)' in $manifest"
        }
        foreach ($package in $metadata.packages) {
            if ($package.source) { Save-RegistrySources $package }
            elseif (!(Test-Within (Get-FullPath $package.manifest_path) (Join-Path $output 'snapshots'))) {
                throw "Cargo path dependency escapes captured sources: $($package.manifest_path)"
            }
        }
        if ($crate.Name -eq 'detours') {
            foreach ($pin in @(@{ name = 'iced-x86'; version = '1.21.0' }, @{ name = 'windows-sys'; version = '0.61.2' })) {
                if (!@($metadata.packages | Where-Object { $_.name -eq $pin.name -and $_.version -eq $pin.version }).Count) {
                    throw "Detour dependency pin missing: $($pin.name) $($pin.version)"
                }
            }
        }
        Write-Host "Building Rust $($crate.Name) ($target)."
        [void](Invoke-RecordedCommand "$($crate.Name)-build" $cargoPath @(
            'build', '--frozen', '--release', '--lib', '--target', $target, '--manifest-path', $manifest, '--target-dir', $targetDirectory
        ) $crateRoot $rustEnvironment)
        $crateRecords += [ordered]@{
            name = $local[0].name; library = $crate.Library; target = $target; cargo = $cargoVersion; rustc = $rustVersion
            manifest = "snapshots/$($crate.Fork)/$($crate.Directory)/Cargo.toml"; manifestSHA256 = Get-Sha256 $manifest
            cargoLock = "provenance/$($crate.Name).Cargo.lock"; cargoLockSHA256 = Get-Sha256 $lock
            license = $local[0].license; licenseFile = $local[0].license_file
        }
    }

    # Preserve each target's feature set, namespace rewrite and unsigned identity.
    $managedTargets = [ordered]@{ 'net11.0' = 'managed'; 'netstandard2.1' = 'compat/netstandard2.1' }
    $jsonSource = Join-Path $output 'snapshots/Alt.Json/Src/Newtonsoft.Json'
    $jsonBuild = Join-Path $output 'work/alt-json-build'
    [void][IO.Directory]::CreateDirectory($jsonBuild)
    foreach ($file in $states['Alt.Json'].files | Where-Object { $_.path -like 'Src/Newtonsoft.Json/*.cs' }) {
        $relative = $file.path.Substring('Src/Newtonsoft.Json/'.Length)
        $destination = Join-Path $jsonBuild $relative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        Write-NewText $destination ([IO.File]::ReadAllText((Join-Path $jsonSource $relative)).Replace('Newtonsoft.Json', 'Alt.Json'))
    }
    [xml]$upstream = Get-Content -Raw -LiteralPath (Join-Path $jsonSource 'Newtonsoft.Json.csproj')
    $properties = [ordered]@{
        TargetFrameworks = $managedTargets.Keys -join ';'; OutputType = 'Library'; AssemblyName = 'Alt.Json'; RootNamespace = 'Alt.Json'
        AssemblyVersion = '1.0.0.0'; FileVersion = '13.0.4.0'; InformationalVersion = "13.0.4+modstoke.snapshot.$($snapshotSHA256.Substring(0, 12))"
        Authors = 'James Newton-King'; Company = 'OpenACAI Inc'; Product = 'Alt.Json compatibility library'
        Copyright = 'Copyright James Newton-King 2008; compatibility build maintained by OpenACAI Inc'
        Description = 'Newtonsoft.Json 13.0.4 source built with the Alt.Json compatibility namespace'
        LangVersion = '9.0'; Nullable = 'enable'; AllowUnsafeBlocks = 'true'
        Deterministic = 'true'; ContinuousIntegrationBuild = 'true'; DebugType = 'embedded'
        EnableSourceControlManagerQueries = 'false'; EnableSourceLink = 'false'; IncludeSourceRevisionInInformationalVersion = 'false'
        SourceRevisionId = $snapshotSHA256; EmbedAllSources = 'true'; PathMap = "$jsonBuild=/_/Alt.Json"
        GenerateDocumentationFile = 'true'; EnableNETAnalyzers = 'false'; EnablePackageValidation = 'false'; SignAssembly = 'false'
        TreatWarningsAsErrors = 'true'; NuGetAudit = 'false'; ImportDirectoryBuildProps = 'false'; ImportDirectoryBuildTargets = 'false'
    }
    $project = [xml]'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup /></Project>'
    foreach ($property in $properties.GetEnumerator()) {
        $node = $project.CreateElement($property.Key)
        $node.InnerText = $property.Value
        [void]$project.SelectSingleNode('/Project/PropertyGroup').AppendChild($node)
    }
    foreach ($framework in $managedTargets.Keys) {
        $condition = "'`$(TargetFramework)'=='$framework'"
        $frameworkGroup = @($upstream.Project.PropertyGroup | Where-Object { $_.GetAttribute('Condition') -eq $condition })
        if ($frameworkGroup.Count -ne 1) { throw "Cannot locate the upstream $framework feature configuration." }
        $constants = $frameworkGroup[0].SelectSingleNode('DefineConstants').InnerText.Replace(';$(AdditionalConstants)', '')
        if ('MODSTOKE_NATIVE_JSON' -notin $constants.Split(';')) {
            throw "Native JSON bridge must be enabled for $framework."
        }
        $group = $project.CreateElement('PropertyGroup')
        $group.SetAttribute('Condition', $condition)
        $node = $project.CreateElement('DefineConstants')
        $node.InnerText = $constants
        [void]$group.AppendChild($node)
        [void]$project.DocumentElement.AppendChild($group)
    }
    $projectPath = Join-Path $jsonBuild 'Alt.Json.csproj'
    Write-NewText $projectPath $project.OuterXml
    Write-NewJson (Join-Path $output 'global.json') @{ sdk = @{ version = $sdkVersion; rollForward = 'disable'; allowPrerelease = $true } }
    $nugetConfig = Join-Path $output 'NuGet.offline.config'
    Write-NewText $nugetConfig '<configuration><packageSources><clear /></packageSources></configuration>'
    $generated = @(Get-ChildItem -LiteralPath $jsonBuild -File -Recurse | Sort-Object FullName | ForEach-Object {
        Get-FileRecord $jsonBuild ([IO.Path]::GetRelativePath($jsonBuild, $_.FullName).Replace('\', '/'))
    })
    Write-NewJson (Join-Path $provenance 'managed-generated-inputs.json') $generated
    $dotnetEnvironment = @{
        NUGET_PACKAGES = $nugetPath; DOTNET_CLI_TELEMETRY_OPTOUT = '1'; DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        DOTNET_NOLOGO = '1'
        DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'; DOTNET_CLI_HOME = (Join-Path $output 'work/dotnet-home')
    }
    $sdk = (Invoke-RecordedCommand 'dotnet-version' $dotnetPath @('--version') $jsonBuild $dotnetEnvironment).Trim()
    if ($sdk -ne $sdkVersion) { throw "Expected pinned SDK $sdkVersion; found $sdk" }
    Write-Host 'Restoring namespace-rewritten Alt.Json for net11.0 and netstandard2.1 offline.'
    [void](Invoke-RecordedCommand 'alt-json-restore' $dotnetPath @(
        'restore', $projectPath, '--configfile', $nugetConfig, '--packages', $nugetPath, '--nologo', '-warnaserror',
        '-p:ImportDirectoryBuildProps=false', '-p:ImportDirectoryBuildTargets=false'
    ) $jsonBuild $dotnetEnvironment)
    $managedIdentities = [ordered]@{}
    foreach ($framework in $managedTargets.Keys) {
        Write-Host "Building namespace-rewritten Alt.Json for $framework with the native bridge enabled."
        $managedBuildOutput = Join-Path $output "work/alt-json-output/$framework"
        [void](Invoke-RecordedCommand "alt-json-$framework-build" $dotnetPath @(
            'build', $projectPath, '-c', 'Release', '-f', $framework, '-o', $managedBuildOutput, '--no-restore', '--nologo', '-warnaserror',
            '-p:ImportDirectoryBuildProps=false', '-p:ImportDirectoryBuildTargets=false'
        ) $jsonBuild $dotnetEnvironment)
        $managedDll = Join-Path $managedBuildOutput 'Alt.Json.dll'
        $identity = [Reflection.AssemblyName]::GetAssemblyName($managedDll)
        if ($identity.Name -ne 'Alt.Json' -or $identity.Version.ToString() -ne '1.0.0.0' -or $identity.GetPublicKeyToken().Length -ne 0) {
            throw "Unexpected managed assembly identity for ${framework}: $($identity.FullName)"
        }
        $managedIdentities[$framework] = $identity.FullName
    }

    # Verify the captured bytes (including both Cargo.lock files) survived the builds.
    foreach ($name in $forks.Keys) {
        foreach ($file in $states[$name].files) {
            if ((Get-Sha256 (Join-Path $output "snapshots/$name/$($file.path)")) -ne $file.sha256) {
                throw "Build modified captured source: $name/$($file.path)"
            }
            if ([IO.Path]::GetFileName($file.path) -match '^(?i:licen[cs]e|copying|notice|copyright)([._-].*)?$') {
                Copy-NewFile (Join-Path $output "snapshots/$name/$($file.path)") (Join-Path $output "licenses/$name/$($file.path)")
            }
        }
    }
    $managedRecords = @()
    $binaryPaths = @()
    foreach ($framework in $managedTargets.Keys) {
        $managedPath = "$($managedTargets[$framework])/Alt.Json.dll"
        $nativePath = "$($managedTargets[$framework])/modstoke_json_native.dll"
        Copy-NewFile (Join-Path $output "work/alt-json-output/$framework/Alt.Json.dll") (Join-Path $output $managedPath)
        Copy-NewFile (Join-Path $output "work/json-target/$target/release/modstoke_json_native.dll") (Join-Path $output $nativePath)
        $binaryPaths += @($managedPath, $nativePath)
        $managedRecords += [ordered]@{
            targetFramework = $framework; assemblyIdentity = $managedIdentities[$framework]; nativeBridgeEnabled = $true
            managedBinary = Get-FileRecord $output $managedPath
            nativeBinary = Get-FileRecord $output $nativePath; nativeTarget = $target
        }
    }
    Copy-NewFile (Join-Path $output "work/detours-target/$target/release/dobby.dll") (Join-Path $output 'native/win-x64/dobby.dll')
    $binaries = @($binaryPaths + @('native/win-x64/dobby.dll') | ForEach-Object { Get-FileRecord $output $_ })
    $licenseFiles = @(Get-ChildItem -LiteralPath (Join-Path $output 'licenses') -File -Recurse | Sort-Object FullName | ForEach-Object {
        Get-FileRecord $output ([IO.Path]::GetRelativePath($output, $_.FullName).Replace('\', '/'))
    })
    Write-NewJson (Join-Path $provenance 'build-provenance.json') ([ordered]@{
        schemaVersion = 1; status = 'staged-not-deployed'; createdUtc = [DateTime]::UtcNow.ToString('O')
        snapshotSHA256 = $snapshotSHA256; sources = $sourceRecords; cleanCommitBuild = $false
        recipeSHA256 = Get-Sha256 (Join-Path $provenance 'Build-NativeDependencyCandidate.ps1')
        referenceRecipeSHA256 = Get-Sha256 (Join-Path $provenance 'Build-MaintainedDependencies.reference.ps1')
        sdk = $sdk; sdkHost = $dotnetPath; nugetPackages = $nugetPath; rustCrates = $crateRecords
        linker = $linkerPath; linkerVersion = $linkerVersion
        registryDependencies = @($dependencyRecords.Values); managedIdentity = $managedIdentities['net11.0']
        managedTargetFramework = 'net11.0'; managedTargets = $managedRecords
        binaries = @($binaries); licenses = $licenseFiles
        detours = 'Project-owned Rust Windows x64 backend, Dobby C ABI target; upstream C++ reference only. No cross-platform parity or thread-atomic patching claim; caller quiescence required.'
        json = 'Native Rust JSON primitives, optional JSONPath parsing, and BSON sizing, reader decisions and valid UTF conversion. Optional ABI 3 kernels add Microsoft-date parsing and formatting, nondecimal integer parsing, enum flags selection, path formatting and equal-length binary comparison. Targets remain net11.0 and netstandard2.1. CLR serializer, reflection, converters, callbacks and DOM remain managed, with managed fallback for unavailable or unsupported native operations.'
        reproducibility = 'Exact source bytes and lock files captured; offline frozen Cargo and pinned SDK. Toolchains and logs recorded; byte-identical native rebuilds are not claimed.'
        acceptance = 'Compilation and assembly identity only; no ABI execution, tests, game launch, deployment, packaging or inventory update.'
    })
    Write-Output "Dependency candidate staged at $output. Provenance is separate under provenance/. Nothing deployed."
} catch {
    Write-NewJson (Join-Path $provenance 'failure.json') ([ordered]@{
        status = 'failed-not-deployed'; failedUtc = [DateTime]::UtcNow.ToString('O'); error = $_.Exception.Message
        logs = 'logs/'; outputPolicy = 'Partial output retained; use a new output directory for the next attempt.'
    })
    throw
}
