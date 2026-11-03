#requires -Version 7.2
<#
.SYNOPSIS
Exports a bounded, offline disassembly for one exact original routine identity.
.DESCRIPTION
Uses a schema-1 routine index and a hash-matched GameAssembly.dll. Uses Iced
without loading LibCpp2IL or the game. The JSON contains preferred-image
addresses, raw bytes and linear disassembly, not callable pointers or proven
method sizes. Assembly, type, name and signature must match exactly, including
case and the signature's parameter names.
.PARAMETER MaxBytes
Requested byte window, capped at 16384 bytes and the file-backed executable
section. A window can end inside an instruction or include other code or data.
#>
[CmdletBinding()]
param(
    [string]$GameRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path,
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$IndexPath,
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$AssemblyName,
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$TypeFullName,
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$MethodName,
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$Signature,
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$OutputPath,
    [ValidateRange(1, 16384)][int]$MaxBytes = 1024,
    # A dedicated server has no BepInEx; point this at another installation's Iced.dll.
    [string]$IcedPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertFrom-HexAddress {
    param([string]$Value)
    if ($Value -notmatch '^0x[0-9A-Fa-f]{1,16}$') {
        throw "Invalid indexed address: $Value"
    }
    return [Convert]::ToUInt64($Value.Substring(2), 16)
}

$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if ([IO.File]::Exists($destination) -or [IO.Directory]::Exists($destination)) {
    throw "Output already exists: $destination"
}
$indexFile = (Resolve-Path -LiteralPath $IndexPath).ProviderPath
$index = Get-Content -LiteralPath $indexFile -Raw | ConvertFrom-Json
if ($index.schema -ne 1 -or $index.truncated -ne $false -or
    $index.recordCount -ne @($index.methods).Count -or
    $index.gameAssemblySha256 -notmatch '^[A-Fa-f0-9]{64}$') {
    throw 'An intact, non-truncated schema-1 routine index is required.'
}
$matches = @($index.methods | Where-Object {
    [string]::Equals($_.assembly, $AssemblyName, [StringComparison]::Ordinal) -and
    [string]::Equals($_.type, $TypeFullName, [StringComparison]::Ordinal) -and
    [string]::Equals($_.name, $MethodName, [StringComparison]::Ordinal) -and
    [string]::Equals($_.signature, $Signature, [StringComparison]::Ordinal)
})
if ($matches.Count -ne 1) {
    throw "Expected one exact assembly/type/name/signature match; found $($matches.Count)."
}
$method = $matches[0]
if ($method.mappedCode -ne $true -or $null -eq $method.rawOffset -or
    $method.originalToken -notmatch '^0x06[0-9A-Fa-f]{6}$') {
    throw 'The selected original method has no usable indexed native mapping.'
}
$entryVa = ConvertFrom-HexAddress $method.nativeEntryVa
$indexedRva = ConvertFrom-HexAddress $method.rva
$rawOffset = [long]$method.rawOffset
$declarationCount = $method.entryDeclarationCount
if ($null -ne $declarationCount -and [long]$declarationCount -lt 1) {
    throw 'The indexed shared-entry declaration count is invalid.'
}

$prefixLength = 0
$prefixHash = $null
$prefixVerified = $false
$prefixBytesProperty = $method.PSObject.Properties['entryPrefixBytes']
$prefixHashProperty = $method.PSObject.Properties['entryPrefixSha256']
if ($null -ne $prefixBytesProperty) { $prefixLength = [int]$prefixBytesProperty.Value }
if ($null -ne $prefixHashProperty) { $prefixHash = [string]$prefixHashProperty.Value }
if ($prefixLength -lt 0 -or $prefixLength -gt 16384 -or
    ($prefixLength -gt 0 -and $prefixHash -notmatch '^[A-Fa-f0-9]{64}$') -or
    ($prefixLength -eq 0 -and -not [string]::IsNullOrEmpty($prefixHash))) {
    throw 'The indexed entry-prefix length or SHA256 is invalid.'
}

$gameDirectory = (Resolve-Path -LiteralPath $GameRoot).ProviderPath
$binaryPath = Join-Path $gameDirectory 'GameAssembly.dll'
$binaryStream = [IO.File]::Open($binaryPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
$hasher = $null
$peReader = $null
$byteReader = $null
try {
    # The same read-only handle supplies the hash, PE mapping and byte window.
    $hasher = [Security.Cryptography.SHA256]::Create()
    $binaryHash = [Convert]::ToHexString($hasher.ComputeHash($binaryStream))
    if (-not [string]::Equals($binaryHash, $index.gameAssemblySha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'GameAssembly.dll does not match the routine index SHA256; no disassembly was exported.'
    }
    $binaryStream.Position = 0
    [void][Reflection.Assembly]::Load('System.Reflection.Metadata')
    $peReader = [System.Reflection.PortableExecutable.PEReader]::new(
        $binaryStream, [System.Reflection.PortableExecutable.PEStreamOptions]::LeaveOpen)
    $headers = $peReader.PEHeaders
    if ($null -eq $headers.PEHeader -or
        $headers.PEHeader.Magic -ne [System.Reflection.PortableExecutable.PEMagic]::PE32Plus -or
        $headers.CoffHeader.Machine -ne [System.Reflection.PortableExecutable.Machine]::Amd64) {
        throw 'Only AMD64 PE32+ native images are supported.'
    }
    $imageBase = [uint64]$headers.PEHeader.ImageBase
    if ($entryVa -lt $imageBase -or ($entryVa - $imageBase) -ne $indexedRva) {
        throw 'The indexed preferred-image address and RVA disagree with the PE image base.'
    }
    $sections = @($headers.SectionHeaders | Where-Object {
        $backedSize = [Math]::Min([long]$_.VirtualSize, [long]$_.SizeOfRawData)
        ($_.SectionCharacteristics -band [System.Reflection.PortableExecutable.SectionCharacteristics]::MemExecute) -ne 0 -and
        $_.VirtualAddress -ge 0 -and $_.PointerToRawData -ge 0 -and $backedSize -gt 0 -and
        $indexedRva -ge [uint64]$_.VirtualAddress -and
        $indexedRva -lt ([uint64]$_.VirtualAddress + [uint64]$backedSize)
    })
    if ($sections.Count -ne 1) {
        throw 'The indexed entry must map to exactly one file-backed executable PE section.'
    }
    $section = $sections[0]
    $sectionOffset = [long]($indexedRva - [uint64]$section.VirtualAddress)
    $mappedOffset = [long]$section.PointerToRawData + $sectionOffset
    if ($rawOffset -ne $mappedOffset -or $rawOffset -lt 0 -or $rawOffset -ge $binaryStream.Length) {
        throw 'The indexed file offset does not match the executable PE mapping.'
    }
    $sectionBytesAvailable = [Math]::Min([long]$section.VirtualSize, [long]$section.SizeOfRawData) - $sectionOffset
    $fileBytesAvailable = $binaryStream.Length - $rawOffset
    $available = [Math]::Min($sectionBytesAvailable, $fileBytesAvailable)
    $windowLength = [int][Math]::Min([long]$MaxBytes, $available)
    if ($prefixLength -gt $fileBytesAvailable) {
        throw 'The recorded entry prefix extends beyond the native file.'
    }
    $byteReader = [IO.BinaryReader]::new($binaryStream, [Text.Encoding]::UTF8, $true)
    $binaryStream.Position = $rawOffset
    $readLength = [Math]::Max($windowLength, $prefixLength)
    $sourceBytes = $byteReader.ReadBytes($readLength)
    if ($sourceBytes.Length -ne $readLength) { throw 'The native byte range could not be read in full.' }
    if ($prefixLength -gt 0) {
        $prefix = [byte[]]::new($prefixLength)
        [Array]::Copy($sourceBytes, 0, $prefix, 0, $prefixLength)
        $actualPrefixHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($prefix))
        if (-not [string]::Equals($actualPrefixHash, $prefixHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The native entry-prefix bytes do not match the routine index SHA256.'
        }
        $prefixVerified = $true
    }
    $body = [byte[]]::new($windowLength)
    [Array]::Copy($sourceBytes, 0, $body, 0, $windowLength)
}
finally {
    if ($null -ne $byteReader) { $byteReader.Dispose() }
    if ($null -ne $peReader) { $peReader.Dispose() }
    if ($null -ne $hasher) { $hasher.Dispose() }
    $binaryStream.Dispose()
}

$icedPath = if ($IcedPath) { $IcedPath } else { Join-Path $gameDirectory 'BepInEx\core\Iced.dll' }
$icedAssembly = [Reflection.Assembly]::LoadFrom($icedPath)
$decoder = [Iced.Intel.Decoder]::Create(64, $body, $entryVa, [Iced.Intel.DecoderOptions]::None)
$instructions = [Collections.Generic.List[object]]::new()
$consumedBytes = 0
$decodedBytes = 0
$decodeStopReason = 'byte-window-end'
while ($consumedBytes -lt $body.Length) {
    $instruction = $decoder.Decode()
    $length = [int]$instruction.Length
    if ($length -le 0 -or $length -gt ($body.Length - $consumedBytes)) {
        throw 'The decoder did not stay within the supplied byte window.'
    }
    $valid = $instruction.Code -ne [Iced.Intel.Code]::INVALID
    $instructions.Add([ordered]@{
        va = '0x{0:X}' -f $instruction.IP
        rva = '0x{0:X}' -f ($indexedRva + [uint64]$consumedBytes)
        rawOffset = $rawOffset + $consumedBytes
        bytes = [BitConverter]::ToString($body, $consumedBytes, $length).Replace('-', ' ')
        text = $instruction.ToString()
        valid = $valid
        decoderError = $decoder.LastError.ToString()
    })
    $consumedBytes += $length
    if (-not $valid) {
        $decodeStopReason = if ($decoder.LastError -eq [Iced.Intel.DecoderError]::NoMoreBytes) {
            'incomplete-instruction-at-byte-window-end'
        } else { 'invalid-instruction' }
        break
    }
    $decodedBytes += $length
}

$limits = [Collections.Generic.List[string]]::new()
if ($windowLength -eq $MaxBytes) { $limits.Add('requested-byte-limit') }
if ($windowLength -eq $sectionBytesAvailable) { $limits.Add('executable-section-end') }
if ($windowLength -eq $fileBytesAvailable) { $limits.Add('file-end') }
$document = [ordered]@{
    schema = 1
    source = 'Original IL2CPP routine index and hash-matched on-disk GameAssembly.dll'
    indexPath = $indexFile
    gameAssemblySha256 = $binaryHash
    indexMetadataSha256 = $index.metadataSha256
    currentMetadataVerified = $false
    identity = [ordered]@{
        assembly = $method.assembly
        type = $method.type
        name = $method.name
        signature = $method.signature
        originalToken = $method.originalToken
    }
    mapping = [ordered]@{
        imageBase = '0x{0:X}' -f $imageBase
        nativeEntryVa = '0x{0:X}' -f $entryVa
        rva = '0x{0:X}' -f $indexedRva
        rawOffset = $rawOffset
        indexEntryDeclarationCount = $declarationCount
        indexOtherEntryAliasCount = if ($null -eq $declarationCount) { $null } else { [long]$declarationCount - 1 }
        entryPrefixBytes = $prefixLength
        entryPrefixSha256 = $prefixHash
        entryPrefixVerification = if ($prefixVerified) { 'matched' } else { 'not-recorded' }
    }
    section = [ordered]@{
        name = $section.Name
        rva = '0x{0:X}' -f $section.VirtualAddress
        virtualSize = $section.VirtualSize
        rawOffset = $section.PointerToRawData
        rawSize = $section.SizeOfRawData
        executableBytesAvailableAtEntry = $available
    }
    window = [ordered]@{
        requestedBytes = $MaxBytes
        maximumRequestBytes = 16384
        exportedBytes = $body.Length
        endVaExclusive = '0x{0:X}' -f ($entryVa + [uint64]$body.Length)
        sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($body))
        bytes = [BitConverter]::ToString($body).Replace('-', ' ')
        limits = $limits.ToArray()
        requestedRangeTruncated = $body.Length -lt $MaxBytes
        boundaryKind = 'heuristic byte window; not a proven method boundary'
        methodSize = $null
        methodTruncation = 'unknown'
    }
    decoding = [ordered]@{
        icedVersion = $icedAssembly.GetName().Version.ToString()
        bitness = 64
        validInstructionBytes = $decodedBytes
        undecodedBytes = $body.Length - $decodedBytes
        truncated = $decodedBytes -lt $body.Length
        stopReason = $decodeStopReason
    }
    caveats = @(
        'Addresses are preferred-image virtual addresses, not live process pointers or callable or hook-safe proof.'
        'Shared-entry counts come from the index and include the selected declaration; null means unknown.'
        'Linear decoding can include padding, embedded data or other routines. Neither a return nor a later entry proves a method size.'
        'The requested window may omit branches or method bytes; completing it does not establish complete method coverage.'
        'The metadata hash is recorded provenance only; current metadata was not loaded or verified.'
    )
    instructions = $instructions.ToArray()
}

$directory = [IO.Path]::GetDirectoryName($destination)
[void][IO.Directory]::CreateDirectory($directory)
$temporary = Join-Path $directory ('.' + [IO.Path]::GetFileName($destination) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
$temporaryOwned = $false
$outputStream = $null
$writer = $null
try {
    $outputStream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $temporaryOwned = $true
    $writer = [IO.StreamWriter]::new($outputStream, [Text.UTF8Encoding]::new($false))
    $writer.WriteLine(($document | ConvertTo-Json -Depth 8))
    $writer.Flush()
    $outputStream.Flush($true)
    $writer.Dispose()
    $writer = $null
    $outputStream.Dispose()
    $outputStream = $null
    [IO.File]::Move($temporary, $destination)
    $temporaryOwned = $false
}
finally {
    if ($null -ne $writer) { $writer.Dispose() }
    if ($null -ne $outputStream) { $outputStream.Dispose() }
    if ($temporaryOwned -and [IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}
Write-Output "Exported $($body.Length) bytes; method boundary unknown; decode stop: $decodeStopReason. $destination"
