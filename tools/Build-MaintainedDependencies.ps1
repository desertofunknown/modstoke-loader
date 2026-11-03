[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$ForkRoot = (Join-Path $PSScriptRoot '..\..\Forks\ReleaseDependencies'),
    [string]$DotNet = (Join-Path $PSScriptRoot '..\.tools\dotnet-11-rc3\dotnet.exe')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$recipeHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory; existing builds are not overwritten.' }
if (!(Test-Path -LiteralPath $DotNet -PathType Leaf)) { throw "Missing .NET SDK host: $DotNet" }
$cmake = (Get-Command cmake-msvc -ErrorAction Stop).Source
$forks = @(
    @{ Name = 'Alt.Json'; Revision = '4e13299d4b0ec96bd4df9954ef646bd2d1b5bf2a'; Url = 'https://github.com/desertofunknown/openacai-newtonsoft-json' },
    @{ Name = 'Dobby'; Revision = '888d971214900374edbca6206fad6ded8a2c1311'; Url = 'https://github.com/desertofunknown/openacai-dobby' }
)
foreach ($fork in $forks) {
    $source = Join-Path $ForkRoot $fork.Name
    $head = & git -C $source rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $head -ne $fork.Revision) { throw "Unexpected revision in $source" }
    $dirty = & git -C $source status --porcelain --untracked-files=all
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw "Source must be clean: $source" }
}

New-Item -ItemType Directory -Path $output | Out-Null
foreach ($fork in $forks) {
    $archive = Join-Path $output ($fork.Name + '-source.zip')
    & git -C (Join-Path $ForkRoot $fork.Name) archive --format=zip "--output=$archive" $fork.Revision
    if ($LASTEXITCODE -ne 0) { throw "Cannot export $($fork.Name)" }
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $output $fork.Name)
}

# Keep the compatibility namespace and assembly identity without rewriting upstream source.
$jsonSource = Join-Path $output 'Alt.Json\Src\Newtonsoft.Json'
$jsonBuild = Join-Path $output 'alt-json-build'
New-Item -ItemType Directory -Path $jsonBuild | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
foreach ($file in Get-ChildItem -LiteralPath $jsonSource -Filter '*.cs' -Recurse) {
    $relative = [IO.Path]::GetRelativePath($jsonSource, $file.FullName)
    $target = Join-Path $jsonBuild $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    [IO.File]::WriteAllText($target, [IO.File]::ReadAllText($file.FullName).Replace('Newtonsoft.Json', 'Alt.Json'), $utf8)
}
[xml]$upstreamProject = Get-Content -LiteralPath (Join-Path $jsonSource 'Newtonsoft.Json.csproj') -Raw
$frameworkGroup = @($upstreamProject.Project.PropertyGroup | Where-Object { $_.GetAttribute('Condition') -eq "'`$(TargetFramework)'=='net6.0'" })
if ($frameworkGroup.Count -ne 1) { throw 'Cannot locate the upstream net6.0 feature configuration.' }
$constants = $frameworkGroup[0].SelectSingleNode('DefineConstants').InnerText
$constants = $constants.Replace(';$(AdditionalConstants)', '')
$project = [xml]'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup /></Project>'
$properties = [ordered]@{
    TargetFramework = 'net6.0'; OutputType = 'Library'; AssemblyName = 'Alt.Json'; RootNamespace = 'Alt.Json'
    AssemblyVersion = '1.0.0.0'; FileVersion = '13.0.4.0'; InformationalVersion = '13.0.4+openacai.4e13299'
    Authors = 'James Newton-King'; Company = 'OpenACAI Inc'; Product = 'Alt.Json compatibility library'
    Copyright = 'Copyright James Newton-King 2008; compatibility build maintained by OpenACAI Inc'
    Description = 'Newtonsoft.Json 13.0.4 source built with the Alt.Json compatibility namespace'
    LangVersion = '9.0'; Nullable = 'enable'; DefineConstants = $constants
    Deterministic = 'true'; ContinuousIntegrationBuild = 'true'; DebugType = 'embedded'
    EnableSourceControlManagerQueries = 'false'; EnableSourceLink = 'false'
    IncludeSourceRevisionInInformationalVersion = 'false'; SourceRevisionId = $forks[0].Revision
    EmbedAllSources = 'true'
    PathMap = "$jsonBuild=/_/Alt.Json"; GenerateDocumentationFile = 'true'
    EnableNETAnalyzers = 'false'; EnablePackageValidation = 'false'
}
foreach ($property in $properties.GetEnumerator()) {
    $node = $project.CreateElement($property.Key)
    $node.InnerText = $property.Value
    $project.SelectSingleNode('/Project/PropertyGroup').AppendChild($node) | Out-Null
}
$projectPath = Join-Path $jsonBuild 'Alt.Json.csproj'
$project.Save($projectPath)
$generatedInputs = @(Get-ChildItem -LiteralPath $jsonBuild -File -Recurse | Sort-Object FullName | ForEach-Object {
    @{ path = [IO.Path]::GetRelativePath($jsonBuild, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$jsonOutput = Join-Path $output 'managed'
& $DotNet build $projectPath -c Release -o $jsonOutput --nologo
if ($LASTEXITCODE -ne 0) { throw 'Alt.Json source build failed.' }

$dobbySource = Join-Path $output 'Dobby'
$dobbyBuild = Join-Path $output 'dobby-build'
# Match BepInEx v1.0.5 Windows release features; do not remove hook functionality to alter AV verdicts.
& $cmake -S $dobbySource -B $dobbyBuild -G 'Visual Studio 17 2022' -A x64 `
    '-DCMAKE_POLICY_VERSION_MINIMUM=3.5' '-DCMAKE_SYSTEM_PROCESSOR=amd64' '-DCMAKE_SYSTEM_NAME=Windows' `
    '-DCMAKE_BUILD_TYPE=Release' '-DDynamicBinaryInstrument=OFF' '-DPlugin.SymbolResolver=OFF'
if ($LASTEXITCODE -ne 0) { throw 'Dobby configuration failed.' }
& $cmake --build $dobbyBuild --config Release --target dobby --parallel 4
if ($LASTEXITCODE -ne 0) { throw 'Dobby source build failed.' }
$nativeOutput = Join-Path $output 'native\win-x64'
New-Item -ItemType Directory -Path $nativeOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $dobbyBuild 'Release\dobby.dll') -Destination $nativeOutput
$licenses = Join-Path $output 'licenses'
New-Item -ItemType Directory -Path $licenses | Out-Null
Copy-Item -LiteralPath (Join-Path $output 'Alt.Json\LICENSE.md') -Destination (Join-Path $licenses 'Newtonsoft.Json-MIT.txt')
Copy-Item -LiteralPath (Join-Path $dobbySource 'LICENSE') -Destination (Join-Path $licenses 'Dobby-Apache-2.0.txt')
$receipt = [ordered]@{
    schemaVersion = 1; createdUtc = [DateTime]::UtcNow.ToString('O'); status = 'staged-not-installed'
    sources = $forks; sdk = (& $DotNet --version)
    recipeSha256 = $recipeHash
    generatedManagedInputs = $generatedInputs
    nativeConfiguration = 'MSVC 2022 x64 Release; DynamicBinaryInstrument=OFF; Plugin.SymbolResolver=OFF; upstream defaults otherwise'
    reproducibility = 'Source revisions pinned; native build timestamps and compiler version can change the binary hash'
    compatibility = 'Alt.Json namespace and unsigned assembly version 1.0.0.0; runtime compatibility not yet verified'
    binaries = @('managed\Alt.Json.dll', 'native\win-x64\dobby.dll') | ForEach-Object {
        $path = Join-Path $output $_
        @{ path = $_; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash; sizeBytes = (Get-Item -LiteralPath $path).Length }
    }
}
$receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'build-provenance.json') -Encoding utf8
Write-Output "Dependency candidates staged at $output. No cache, installed files or packages changed."
