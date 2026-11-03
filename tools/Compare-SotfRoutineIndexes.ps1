[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BeforePath,
    [Parameter(Mandatory = $true)][string]$AfterPath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-Index {
    param([string]$Path)
    $index = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($index.schema -ne 1 -or $index.truncated -or
        $index.recordCount -ne $index.methods.Count -or
        $index.gameAssemblySha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
        $index.metadataSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
        throw "Incomplete or invalid routine index: $Path"
    }
    return $index
}

function Get-Identity {
    param($Method)
    $parts = @($Method.assembly, $Method.type, $Method.name, $Method.signature)
    foreach ($part in $parts) {
        if ([string]::IsNullOrEmpty($part) -or $part.Contains([char]31)) {
            throw 'A method has an invalid qualified identity.'
        }
    }
    return $parts -join [char]31
}

function Get-MethodMap {
    param($Index)
    $map = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($method in $Index.methods) {
        $key = Get-Identity $method
        if (-not $map.TryAdd($key, $method)) {
            throw "Ambiguous qualified method in index: $($method.type).$($method.name)"
        }
    }
    return ,$map
}

$before = Read-Index $BeforePath
$after = Read-Index $AfterPath
foreach ($selector in @('namespacePrefix', 'typeFullName', 'methodName')) {
    if ($before.$selector -cne $after.$selector) {
        throw "Routine indexes use different $selector filters; comparison would be incomplete."
    }
}
$beforeMap = Get-MethodMap $before
$afterMap = Get-MethodMap $after
$changes = [Collections.Generic.List[object]]::new()
$added = 0
$removed = 0
$changed = 0
$sameEvidence = 0
foreach ($key in $beforeMap.Keys) {
    $old = $beforeMap[$key]
    if (-not $afterMap.ContainsKey($key)) {
        $changes.Add([ordered]@{ kind = 'removed'; before = $old; after = $null })
        $removed++
        continue
    }
    $new = $afterMap[$key]
    $differences = [Collections.Generic.List[string]]::new()
    if ($old.originalToken -cne $new.originalToken) { $differences.Add('originalToken') }
    if ($old.mappedCode -ne $new.mappedCode) { $differences.Add('nativeMapping') }
    if ($old.entryDeclarationCount -ne $new.entryDeclarationCount) { $differences.Add('entryAliases') }
    if ($old.entryPrefixSha256 -cne $new.entryPrefixSha256) { $differences.Add('entryPrefix') }
    if ($old.rva -cne $new.rva) { $differences.Add('rva') }
    if ($old.rawOffset -ne $new.rawOffset) { $differences.Add('rawOffset') }
    if ($differences.Count -eq 0) {
        $sameEvidence++
        continue
    }
    $changes.Add([ordered]@{
        kind = 'changed'
        differences = $differences.ToArray()
        before = $old
        after = $new
    })
    $changed++
}
foreach ($key in $afterMap.Keys) {
    if ($beforeMap.ContainsKey($key)) { continue }
    $changes.Add([ordered]@{ kind = 'added'; before = $null; after = $afterMap[$key] })
    $added++
}

$destination = [IO.Path]::GetFullPath($OutputPath)
if ([IO.File]::Exists($destination)) { throw "Comparison already exists: $destination" }
$directory = [IO.Path]::GetDirectoryName($destination)
[void][IO.Directory]::CreateDirectory($directory)
$temporary = Join-Path $directory ('.' + [IO.Path]::GetFileName($destination) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    $result = [ordered]@{
        schema = 1
        beforeGameAssemblySha256 = $before.gameAssemblySha256
        beforeMetadataSha256 = $before.metadataSha256
        afterGameAssemblySha256 = $after.gameAssemblySha256
        afterMetadataSha256 = $after.metadataSha256
        namespacePrefix = $before.namespacePrefix
        typeFullName = $before.typeFullName
        methodName = $before.methodName
        addedCount = $added
        removedCount = $removed
        changedEvidenceCount = $changed
        sameRecordedEvidenceCount = $sameEvidence
        changes = $changes.ToArray()
    }
    [IO.File]::WriteAllText($temporary, ($result | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $destination)
}
finally {
    if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}
Write-Output ("Added {0}, removed {1}, changed evidence {2}, same recorded evidence {3}: {4}" -f
    $added, $removed, $changed, $sameEvidence, $destination)
