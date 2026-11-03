#requires -Version 7.2
<#
.SYNOPSIS
Queries a schema-1 index written by Export-SotfRoutineIndex.ps1 without loading game code.
.DESCRIPTION
AssemblyName, TypeFullName, MethodName and Signature are literal, case-sensitive
ordinal filters combined with AND. Signature matches the complete stored text,
including parameter names. No filter resolves an overload or selects a callable
entry. NamespacePrefix matches the stored full type name at a dotted boundary;
an omitted trailing dot is appended. Schema 1 does not store namespaces separately.

The entire index is read and validated before returning one object containing
provenance, counts, truncation flags and at most MaxResults methods in index order.
queryTruncated describes the result limit; sourceTruncated describes the export.
truncated is true when either is true. Counts and absence apply only to the index.
The recorded game hashes are not checked against any currently installed files.
.EXAMPLE
./tools/Find-SotfRoutine.ps1 -IndexPath ./sons-routines.json -TypeFullName Sons.BlobNetworking.BlobReceive -MethodName get_Data
.EXAMPLE
./tools/Find-SotfRoutine.ps1 -IndexPath ./sons-routines.json -NamespacePrefix Sons.Inventory -MaxResults 100 -Json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$IndexPath,
    [ValidateNotNullOrEmpty()][string]$AssemblyName,
    [ValidateNotNullOrEmpty()][string]$TypeFullName,
    [ValidateNotNullOrEmpty()][string]$MethodName,
    [ValidateNotNullOrEmpty()][string]$Signature,
    [ValidateNotNullOrEmpty()][string]$NamespacePrefix,
    [ValidateRange(1, 1000)][int]$MaxResults = 50,
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-IndexObject {
    param([System.Text.Json.JsonElement]$Element, [string[]]$Fields, [string]$Context)

    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        throw "Invalid routine index: $Context must be an object."
    }
    $values = [hashtable]::new([StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) {
        if ($Fields -cnotcontains $property.Name -or $values.ContainsKey($property.Name)) {
            throw "Invalid routine index: $Context has an unexpected or duplicate '$($property.Name)' field."
        }
        $value = switch ($property.Value.ValueKind) {
            'String' { $property.Value.GetString(); break }
            'Null' { $null; break }
            'True' { $true; break }
            'False' { $false; break }
            'Number' {
                $number = 0L
                if (-not $property.Value.TryGetInt64([ref]$number)) {
                    throw "Invalid routine index: $Context.$($property.Name) must be a 64-bit integer."
                }
                $number
                break
            }
            'Array' { $property.Value; break }
            default { throw "Invalid routine index: unsupported value in $Context.$($property.Name)." }
        }
        $values.Add($property.Name, $value)
    }
    if ($values.Count -ne $Fields.Count) {
        throw "Invalid routine index: $Context is missing required fields."
    }
    return $values
}

$headerFields = @('schema', 'source', 'unityVersion', 'gameAssemblySha256', 'metadataSha256',
    'namespacePrefix', 'typeFullName', 'methodName', 'truncated', 'recordCount',
    'mappedCodeCount', 'sharedEntryRecordCount', 'methods')
$methodFields = @('assembly', 'type', 'name', 'signature', 'originalToken', 'mappedCode',
    'nativeEntryVa', 'rva', 'rawOffset', 'entryDeclarationCount', 'entryPrefixBytes', 'entryPrefixSha256')
$hashPattern = '\A[0-9A-Fa-f]{64}\z'
$addressPattern = '\A0x[0-9A-Fa-f]{1,16}\z'
$ordinal = [StringComparison]::Ordinal
$exactFilters = [ordered]@{}
if ($PSBoundParameters.ContainsKey('AssemblyName')) { $exactFilters.Add('assembly', $AssemblyName) }
if ($PSBoundParameters.ContainsKey('TypeFullName')) { $exactFilters.Add('type', $TypeFullName) }
if ($PSBoundParameters.ContainsKey('MethodName')) { $exactFilters.Add('name', $MethodName) }
if ($PSBoundParameters.ContainsKey('Signature')) { $exactFilters.Add('signature', $Signature) }
$namespaceFilter = $null
if ($PSBoundParameters.ContainsKey('NamespacePrefix')) {
    $namespaceFilter = $NamespacePrefix
    if (-not $namespaceFilter.EndsWith('.', $ordinal)) { $namespaceFilter += '.' }
}

$resolvedPath = Resolve-Path -LiteralPath $IndexPath
if ($resolvedPath.Provider.Name -ne 'FileSystem' -or -not [IO.File]::Exists($resolvedPath.ProviderPath)) {
    throw 'IndexPath must name a local routine index file.'
}
$path = $resolvedPath.ProviderPath
$bytes = [IO.File]::ReadAllBytes($path)
$indexHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
$document = $null
try {
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { $text = $text.Substring(1) }
    $document = [System.Text.Json.JsonDocument]::Parse($text)
    $index = Read-IndexObject $document.RootElement $headerFields 'root'
    if ($index.schema -isnot [long] -or $index.schema -ne 1) {
        throw 'Unsupported routine index schema; expected schema 1.'
    }
    if ($index.source -isnot [string] -or -not [string]::Equals($index.source,
            'LibCpp2IL original IL2CPP metadata and current GameAssembly.dll', $ordinal) -or
        $index.unityVersion -isnot [string] -or [string]::IsNullOrEmpty($index.unityVersion)) {
        throw 'Invalid routine index source or Unity version.'
    }
    foreach ($field in @('gameAssemblySha256', 'metadataSha256')) {
        if ($index[$field] -isnot [string] -or $index[$field] -cnotmatch $hashPattern) {
            throw "Invalid routine index $field."
        }
    }
    if ($index.truncated -isnot [bool] -or
        $index.methods -isnot [System.Text.Json.JsonElement] -or
        $index.methods.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
        throw 'Invalid routine index truncation flag or methods array.'
    }
    foreach ($field in @('recordCount', 'mappedCodeCount', 'sharedEntryRecordCount')) {
        if ($index[$field] -isnot [long] -or $index[$field] -lt 0) {
            throw "Invalid routine index $field."
        }
    }
    if ($index.recordCount -ne $index.methods.GetArrayLength() -or
        $index.mappedCodeCount -gt $index.recordCount -or
        $index.sharedEntryRecordCount -gt $index.mappedCodeCount) {
        throw 'Inconsistent routine index record counts.'
    }
    foreach ($field in @('namespacePrefix', 'typeFullName', 'methodName')) {
        if ($null -ne $index[$field] -and $index[$field] -isnot [string]) {
            throw "Invalid routine index source filter: $field."
        }
    }
    if (($null -eq $index.typeFullName -and $null -eq $index.namespacePrefix) -or
        ($null -ne $index.typeFullName -and
            ($index.typeFullName.Length -eq 0 -or $null -ne $index.namespacePrefix)) -or
        ($null -ne $index.methodName -and $index.methodName.Length -eq 0)) {
        throw 'Inconsistent routine index source filters.'
    }

    $tokens = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $entries = [Collections.Generic.Dictionary[uint64, object]]::new()
    $results = [Collections.Generic.List[object]]::new()
    $mappedCount = 0L
    $sharedCount = 0L
    $matchedCount = 0L
    $position = 0
    foreach ($element in $index.methods.EnumerateArray()) {
        $context = "methods[$position]"
        $position++
        $method = Read-IndexObject $element $methodFields $context
        foreach ($field in @('assembly', 'type', 'name', 'signature')) {
            if ($method[$field] -isnot [string] -or [string]::IsNullOrEmpty($method[$field])) {
                throw "Invalid routine index: $context.$field must be a nonempty string."
            }
        }
        if ($method.originalToken -isnot [string] -or
            $method.originalToken -cnotmatch '\A0x06[0-9A-Fa-f]{6}\z' -or
            $method.originalToken -ceq '0x06000000') {
            throw "Invalid original method token at $context."
        }
        if (-not $tokens.Add($method.assembly + ':' + $method.originalToken.ToUpperInvariant())) {
            throw "Duplicate original method token at $context."
        }
        # Exporter's exact type/name selectors use PowerShell's case-insensitive comparison.
        if (($null -ne $index.typeFullName -and $method.type -ne $index.typeFullName) -or
            ($null -ne $index.namespacePrefix -and -not $method.type.StartsWith($index.namespacePrefix, $ordinal)) -or
            ($null -ne $index.methodName -and $method.name -ne $index.methodName)) {
            throw "Record outside the index's declared source filters at $context."
        }
        if ($method.mappedCode -isnot [bool] -or $method.entryPrefixBytes -isnot [long]) {
            throw "Invalid mapping flag or prefix length at $context."
        }
        if ($method.mappedCode) {
            foreach ($field in @('nativeEntryVa', 'rva')) {
                if ($method[$field] -isnot [string] -or $method[$field] -cnotmatch $addressPattern) {
                    throw "Invalid $field at $context."
                }
            }
            $va = [Convert]::ToUInt64($method.nativeEntryVa.Substring(2), 16)
            $rva = [Convert]::ToUInt64($method.rva.Substring(2), 16)
            if ($va -eq 0 -or $method.rawOffset -isnot [long] -or $method.rawOffset -lt 0 -or
                $method.entryPrefixBytes -lt 1 -or $method.entryPrefixBytes -gt 32 -or
                $method.entryPrefixSha256 -isnot [string] -or $method.entryPrefixSha256 -cnotmatch $hashPattern -or
                ($null -ne $method.entryDeclarationCount -and
                    ($method.entryDeclarationCount -isnot [long] -or $method.entryDeclarationCount -lt 1))) {
                throw "Inconsistent mapped code evidence at $context."
            }
            $mappedCount++
            if ($null -ne $method.entryDeclarationCount -and $method.entryDeclarationCount -gt 1) { $sharedCount++ }
            if ($entries.ContainsKey($va)) {
                $entry = $entries[$va]
                if ($entry.rva -ne $rva -or $entry.rawOffset -ne $method.rawOffset -or
                    $entry.aliases -ne $method.entryDeclarationCount -or
                    $entry.prefixBytes -ne $method.entryPrefixBytes -or
                    $entry.prefixHash -cne $method.entryPrefixSha256.ToUpperInvariant()) {
                    throw "Contradictory evidence for a shared native entry at $context."
                }
                $entry.observed++
            }
            else {
                $entry = [pscustomobject]@{
                    rva = $rva
                    rawOffset = $method.rawOffset
                    aliases = $method.entryDeclarationCount
                    prefixBytes = $method.entryPrefixBytes
                    prefixHash = $method.entryPrefixSha256.ToUpperInvariant()
                    observed = 1L
                }
                $entries.Add($va, $entry)
            }
            if ($null -ne $entry.aliases -and $entry.observed -gt $entry.aliases) {
                throw "Native alias count is smaller than the indexed declaration count at $context."
            }
        }
        else {
            foreach ($field in @('nativeEntryVa', 'rva', 'rawOffset', 'entryDeclarationCount', 'entryPrefixSha256')) {
                if ($null -ne $method[$field]) { throw "Unmapped routine has $field evidence at $context." }
            }
            if ($method.entryPrefixBytes -ne 0) { throw "Unmapped routine has prefix bytes at $context." }
        }

        $isMatch = $true
        foreach ($filter in $exactFilters.GetEnumerator()) {
            if (-not [string]::Equals($method[$filter.Key], $filter.Value, $ordinal)) {
                $isMatch = $false
                break
            }
        }
        if ($isMatch -and $null -ne $namespaceFilter) {
            $isMatch = $method.type.StartsWith($namespaceFilter, $ordinal)
        }
        if ($isMatch) {
            $matchedCount++
            if ($results.Count -lt $MaxResults) {
                $record = [ordered]@{}
                foreach ($field in $methodFields) { $record.Add($field, $method[$field]) }
                $results.Add([pscustomobject]$record)
            }
        }
    }
    if ($mappedCount -ne $index.mappedCodeCount -or $sharedCount -ne $index.sharedEntryRecordCount) {
        throw 'Routine index mapping totals disagree with its records.'
    }

    $provenance = [ordered]@{ path = $path; sha256 = $indexHash; byteLength = $bytes.LongLength }
    foreach ($field in $headerFields) {
        if ($field -cne 'methods') { $provenance.Add($field, $index[$field]) }
    }
    $queryTruncated = $matchedCount -gt $results.Count
    $result = [pscustomobject][ordered]@{
        index = [pscustomobject]$provenance
        exactFilters = [pscustomobject]$exactFilters
        namespacePrefix = $namespaceFilter
        comparison = 'Ordinal'
        maxResults = $MaxResults
        matchedRecordCount = $matchedCount
        returnedRecordCount = $results.Count
        queryTruncated = $queryTruncated
        sourceTruncated = $index.truncated
        truncated = $queryTruncated -or $index.truncated
        methods = $results.ToArray()
        caveats = @(
            'Results cover only the recorded export filters and records; no match does not prove game-wide absence.'
            'Exact filters return all matching declarations within the result limit; they do not resolve overloads.'
            'Game and metadata hashes are recorded provenance, not verification of currently installed files.'
            'Original tokens belong to IL2CPP metadata, not generated interop assembly tokens.'
            'Native entry VA is an analysis-image address, not a live pointer; RVA and raw file offset are distinct.'
            'Entry declaration counts include all loaded metadata, not just these results; null means unknown.'
            'Mapped entries may alias other declarations; one declaration does not establish hook safety.'
            'Prefix hashes cover only the recorded first 1-32 raw bytes, not an entire method.'
            'Index evidence does not establish native behavior, callability, receiver lifetime or gameplay authority.'
        )
    }
    if ($Json) { $result | ConvertTo-Json -Depth 8 }
    else { $result }
}
finally {
    if ($null -ne $document) { $document.Dispose() }
}
