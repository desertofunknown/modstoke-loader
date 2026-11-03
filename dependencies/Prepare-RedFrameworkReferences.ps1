#requires -Version 7.2
param(
    [string]$RedCompatibilityLibraryRoot = (Join-Path $PSScriptRoot '../.tools/modstoke-dependencies/build-reference/red')
)
$ErrorActionPreference = 'Stop'
$inventory = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'inventory.json') -Raw | ConvertFrom-Json
$outputRoot = Join-Path $RedCompatibilityLibraryRoot 'net6-framework'
$prepared = @()
foreach ($name in @('Il2Cppmscorlib.dll','UnityEngine.CoreModule-nstrip.dll')) {
    $entries = @($inventory.assets | Where-Object cachePath -eq ('build-reference/red/' + $name))
    if ($entries.Count -ne 1 -or $entries[0].packagePaths.Count -ne 0) { throw "Expected one compile-only inventory entry for $name" }
    $entry = $entries[0]
    $source = Join-Path $RedCompatibilityLibraryRoot $name
    $bytes = [IO.File]::ReadAllBytes($source)
    $sourceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    if ($sourceHash -ne $entry.sha256 -or $bytes.Length -ne $entry.size) { throw "Pinned build reference changed: $source" }
    $stream = [IO.MemoryStream]::new($bytes, $false)
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $reader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        if (($pe.PEHeaders.CorHeader.Flags -band [System.Reflection.PortableExecutable.CorFlags]::StrongNameSigned) -ne 0) { throw 'Do not modify signed build references.' }
        $table = [System.Reflection.Metadata.Ecma335.TableIndex]::AssemblyRef
        $start = $pe.PEHeaders.MetadataStartOffset + [System.Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetTableMetadataOffset($reader, $table)
        $stride = [System.Reflection.Metadata.Ecma335.MetadataReaderExtensions]::GetTableRowSize($reader, $table)
        $row = 0
        $offset = -1
        foreach ($handle in $reader.AssemblyReferences) {
            $reference = $reader.GetAssemblyReference($handle)
            if ($reader.GetString($reference.Name) -eq 'mscorlib') {
                if ($offset -ne -1 -or $reference.Version -ne [Version]'6.0.0.0' -or
                    [Convert]::ToHexString($reader.GetBlobBytes($reference.PublicKeyOrToken)) -ne 'B77A5C561934E089') {
                    throw "Unexpected framework reference in $name"
                }
                $offset = $start + $row * $stride
            }
            $row++
        }
        if ($offset -lt 0 -or $offset + 8 -gt $bytes.Length -or
            [BitConverter]::ToUInt64($bytes, $offset) -ne 6) { throw "Unexpected AssemblyRef version layout in $name" }
    } finally {
        $pe.Dispose()
        $stream.Dispose()
    }
    # ECMA-335 II.22.5 starts AssemblyRef rows with four little-endian UInt16 version fields.
    # net6's mscorlib facade is version 4.0.0.0; change only this one metadata byte.
    $bytes[$offset] = 4
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    $target = Join-Path $outputRoot $name
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hash) { throw "Derived reference differs; preserve and inspect it: $target" }
    } else {
        New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
        $temp = $target + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        try {
            [IO.File]::WriteAllBytes($temp, $bytes)
            if ((Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash -ne $hash) { throw 'Derived reference write mismatch.' }
            [IO.File]::Move($temp, $target)
        } finally {
            if (Test-Path -LiteralPath $temp) { [IO.File]::Delete($temp) }
        }
    }
    $prepared += [pscustomobject]@{File=$name;SourceSha256=$sourceHash;Sha256=$hash;ChangedByteOffset=$offset;OldByte=6;NewByte=4;PackagePaths=@()}
}
$prepared | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputRoot 'provenance.json') -Encoding utf8
Write-Host "Prepared two compile-only net6 framework references in $outputRoot; original inputs preserved."
