[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [string] $DependencyRoot
)

$ErrorActionPreference = 'Stop'
$suite = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot '.artifacts' }
if (-not $DependencyRoot) { $DependencyRoot = Join-Path $suite '.tools/modstoke-dependencies/managed' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$DependencyRoot = [IO.Path]::GetFullPath($DependencyRoot)
$dotnet = Join-Path $suite '.tools/dotnet-11-rc3/dotnet.exe'
$nativeOutput = Join-Path $OutputDirectory 'native'
$managedOutput = Join-Path $OutputDirectory 'managed'
$candidate = Join-Path $OutputDirectory 'candidate'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Push-Location (Join-Path $PSScriptRoot 'Native')
try {
    & cargo build --release --offline --locked --target-dir $nativeOutput 2>&1 |
        Tee-Object -FilePath (Join-Path $OutputDirectory 'native-build.log')
    if ($LASTEXITCODE -ne 0) { throw "Rust compilation failed with exit code $LASTEXITCODE." }
} finally { Pop-Location }

Push-Location $suite
try {
    & $dotnet build (Join-Path $PSScriptRoot 'Managed/Harmony.csproj') -c Release --nologo --verbosity minimal `
        --artifacts-path $managedOutput "-p:HarmonyDependencyRoot=$DependencyRoot" 2>&1 |
        Tee-Object -FilePath (Join-Path $OutputDirectory 'managed-build.log')
    if ($LASTEXITCODE -ne 0) { throw "Managed compilation failed with exit code $LASTEXITCODE." }
} finally { Pop-Location }

New-Item -ItemType Directory -Path $candidate -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $managedOutput 'bin/Harmony/release/0Harmony.dll'),
    (Join-Path $managedOutput 'bin/Harmony/release/0Harmony.xml'),
    (Join-Path $nativeOutput 'release/modstoke_harmony_core.dll') -Destination $candidate
foreach ($name in @('LICENSE', 'LICENSE.Harmony', 'LICENSE.Modstoke', 'LICENSE.JetBrains', 'LICENSE.NET', 'THIRD_PARTY_NOTICES.md', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $candidate
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Managed/License.txt') -Destination (Join-Path $candidate 'LICENSE.Apache-2.0')
$files = @('0Harmony.dll', 'modstoke_harmony_core.dll') | ForEach-Object {
    $file = Join-Path $candidate $_
    [ordered]@{ name = $_; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash; bytes = (Get-Item -LiteralPath $file).Length }
}
[ordered]@{
    format = 1
    upstream = 'HarmonyX 2.16.1 / 02a98f540fa12ac4656f189a3cf67b0c96cc68f7'
    target = 'Windows x64 / .NET 11'
    nativeAbi = 4
    acceptance = 'Compiled candidate; runtime compatibility not established'
    files = @($files)
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $candidate 'candidate.json')
Write-Output "Candidate prepared at $candidate. No installed game files were changed."
