[CmdletBinding()]
param(
    [string]$GameRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$UnityVersion = '2022.2.16f1',
    [string]$NamespacePrefix = 'Sons.',
    [string]$TypeFullName = '',
    [string]$MethodName = '',
    [ValidateRange(0, 1000000)][int]$MaxRecords = 0,
    [string]$ExpectedGameAssemblySha256 = '',
    [string]$ExpectedMetadataSha256 = '',
    # The dedicated server keeps its metadata under SonsOfTheForestDS_Data and has no BepInEx.
    [string]$DataFolder = '',
    [string]$LibCpp2IlRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$gameRoot = (Resolve-Path -LiteralPath $GameRoot).Path
$binaryPath = Join-Path $gameRoot 'GameAssembly.dll'
if (-not $DataFolder) {
    $DataFolder = @('SonsOfTheForest_Data', 'SonsOfTheForestDS_Data') |
        Where-Object { Test-Path -LiteralPath (Join-Path $gameRoot $_) } | Select-Object -First 1
    if (-not $DataFolder) { throw 'No Sons of the Forest data folder was found under the game root.' }
}
$metadataPath = Join-Path $gameRoot (Join-Path $DataFolder 'il2cpp_data\Metadata\global-metadata.dat')
$corePath = if ($LibCpp2IlRoot) { $LibCpp2IlRoot } else { Join-Path $gameRoot 'BepInEx\core' }
$binaryHash = (Get-FileHash -LiteralPath $binaryPath -Algorithm SHA256).Hash
$metadataHash = (Get-FileHash -LiteralPath $metadataPath -Algorithm SHA256).Hash
if ($ExpectedGameAssemblySha256 -and $binaryHash -ne $ExpectedGameAssemblySha256) {
    throw 'GameAssembly.dll differs from the expected build.'
}
if ($ExpectedMetadataSha256 -and $metadataHash -ne $ExpectedMetadataSha256) {
    throw 'IL2CPP metadata differs from the expected build.'
}

# LibCpp2IL keeps static state between loads: index one binary per PowerShell process.
foreach ($name in @('AssetRipper.Primitives.dll', 'LibCpp2IL.dll')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $corePath $name))
}
$previousOutput = [Console]::Out
try {
    [Console]::SetOut([IO.TextWriter]::Null)
    $loaded = [LibCpp2IL.LibCpp2IlMain]::LoadFromFile(
        $binaryPath, $metadataPath, [AssetRipper.Primitives.UnityVersion]::Parse($UnityVersion))
}
finally {
    [Console]::SetOut($previousOutput)
}
if (-not $loaded) {
    throw 'Could not load the native binary and IL2CPP metadata.'
}

$methodType = [LibCpp2IL.Metadata.Il2CppMethodDefinition]
$nameProperty = $methodType.GetProperty('Name')
$signatureProperty = $methodType.GetProperty('HumanReadableSignature')
$pointerProperty = $methodType.GetProperty('MethodPointer')
$offsetProperty = $methodType.GetProperty('MethodOffsetInFile')
$rvaProperty = $methodType.GetProperty('Rva')
$tokenField = $methodType.GetField('token')
$binaryBytes = [LibCpp2IL.LibCpp2IlMain]::Binary.GetRawBinaryContent()
$methodsByPointer = [LibCpp2IL.LibCpp2IlMain]::MethodsByPtr
$types = @([LibCpp2IL.LibCpp2IlMain]::TheMetadata.typeDefs |
    Where-Object {
        ($TypeFullName -and [string]::Equals($_.FullName, $TypeFullName, [StringComparison]::Ordinal)) -or
        (-not $TypeFullName -and $_.Namespace.StartsWith($NamespacePrefix, [StringComparison]::Ordinal))
    } |
    Sort-Object { $_.DeclaringAssembly.Name }, FullName)

$records = [Collections.Generic.List[object]]::new()
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$mapped = 0
$shared = 0
$truncated = $false
:types foreach ($type in $types) {
    foreach ($method in $type.Methods) {
        $name = [string]$nameProperty.GetValue($method)
        if ($MethodName -and -not [string]::Equals($name, $MethodName, [StringComparison]::Ordinal)) { continue }
        if ($MaxRecords -gt 0 -and $records.Count -ge $MaxRecords) {
            $truncated = $true
            break types
        }
        $assembly = [string]$type.DeclaringAssembly.Name
        $token = [uint32]$tokenField.GetValue($method)
        $key = '{0}:{1:X8}' -f $assembly, $token
        if (-not $seen.Add($key)) {
            throw "Duplicate original method token in one assembly: $key"
        }
        $pointer = [uint64]$pointerProperty.GetValue($method)
        $offset = [long]$offsetProperty.GetValue($method)
        $mappedCode = $pointer -ne 0 -and $offset -ge 0 -and $offset -lt $binaryBytes.LongLength
        $entryDeclarationCount = $null
        $entryPrefixHash = $null
        $entryPrefixBytes = 0
        if ($mappedCode) {
            if ($methodsByPointer.ContainsKey($pointer)) {
                $entryDeclarationCount = $methodsByPointer[$pointer].Count
                if ($entryDeclarationCount -gt 1) { $shared++ }
            }
            $entryPrefixBytes = [int][Math]::Min(32L, $binaryBytes.LongLength - $offset)
            $prefix = [byte[]]::new($entryPrefixBytes)
            [Array]::Copy($binaryBytes, $offset, $prefix, 0, $entryPrefixBytes)
            $entryPrefixHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($prefix))
            $mapped++
        }
        $records.Add([ordered]@{
            assembly = $assembly
            type = [string]$type.FullName
            name = $name
            signature = [string]$signatureProperty.GetValue($method)
            originalToken = '0x{0:X8}' -f $token
            mappedCode = $mappedCode
            nativeEntryVa = if ($mappedCode) { '0x{0:X}' -f $pointer } else { $null }
            rva = if ($mappedCode) { '0x{0:X}' -f [uint64]$rvaProperty.GetValue($method) } else { $null }
            rawOffset = if ($mappedCode) { $offset } else { $null }
            entryDeclarationCount = $entryDeclarationCount
            entryPrefixBytes = $entryPrefixBytes
            entryPrefixSha256 = $entryPrefixHash
        })
    }
}

if ((Get-FileHash -LiteralPath $binaryPath -Algorithm SHA256).Hash -ne $binaryHash -or
    (Get-FileHash -LiteralPath $metadataPath -Algorithm SHA256).Hash -ne $metadataHash) {
    throw 'The native binary or metadata changed during inspection; no index was written.'
}

$destination = [IO.Path]::GetFullPath($OutputPath)
if ([IO.File]::Exists($destination)) {
    throw "Index already exists: $destination"
}
$directory = [IO.Path]::GetDirectoryName($destination)
[void][IO.Directory]::CreateDirectory($directory)
$temporary = Join-Path $directory ('.' + [IO.Path]::GetFileName($destination) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    $document = [ordered]@{
        schema = 1
        source = 'LibCpp2IL original IL2CPP metadata and current GameAssembly.dll'
        unityVersion = $UnityVersion
        gameAssemblySha256 = $binaryHash
        metadataSha256 = $metadataHash
        namespacePrefix = if ($TypeFullName) { $null } else { $NamespacePrefix }
        typeFullName = if ($TypeFullName) { $TypeFullName } else { $null }
        methodName = if ($MethodName) { $MethodName } else { $null }
        truncated = $truncated
        recordCount = $records.Count
        mappedCodeCount = $mapped
        sharedEntryRecordCount = $shared
        methods = $records.ToArray()
    }
    [IO.File]::WriteAllText($temporary, ($document | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $destination)
}
finally {
    if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}
Write-Output "Indexed $($records.Count) original methods ($mapped with mapped native code): $destination"
