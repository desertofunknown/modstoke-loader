#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$GamePath,
    [switch]$Apply,
    [string]$Restore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Apply -and $Restore) { throw 'Choose cleanup or restore, not both.' }

function Assert-PlainPath([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Linked paths are not supported: $cursor"
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

function Child([string]$Root, [string]$Relative) {
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':') -or
        $Relative -match '(^|[\\/])\.\.([\\/]|$)') { throw "Invalid relative path: $Relative" }
    $path = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $path.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path leaves its root: $Relative"
    }
    Assert-PlainPath $path
    return $path
}

function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Assert-Stopped {
    if (Get-Process -Name SonsOfTheForest,SonsOfTheForestDS,SonsOfTheForestDedicatedServer -ErrorAction SilentlyContinue) {
        throw 'Close Sons of the Forest and its dedicated server before changing files.'
    }
}

if ([string]::IsNullOrWhiteSpace($GamePath)) {
    $GamePath = Read-Host 'Sons of the Forest game folder (Steam > Properties > Installed Files > Browse)'
}
if ([string]::IsNullOrWhiteSpace($GamePath)) { throw 'No game folder selected.' }
$game = [IO.Path]::GetFullPath($GamePath.Trim('"')).TrimEnd('\')
Assert-PlainPath $game
if (-not (Test-Path -LiteralPath (Child $game 'SonsOfTheForest.exe') -PathType Leaf) -and
    -not (Test-Path -LiteralPath (Child $game 'SonsOfTheForestDS.exe') -PathType Leaf) -and
    -not (Test-Path -LiteralPath (Child $game 'SonsOfTheForestDedicatedServer.exe') -PathType Leaf)) {
    throw 'Select the game folder containing SonsOfTheForest.exe or its dedicated-server executable.'
}

# Old files can sit under either root: the loader moves OpenACAI/Mods and
# OpenACAI/UserData into Modstoke/ on first start but leaves OpenACAI/Runtime.
$roots = @('OpenACAI', 'Modstoke')
$runtimeFolders = @($roots | ForEach-Object { "$_/Runtime" })
$assemblies = @{
    'OpenACAI.Endnight.Loader.Core' = $runtimeFolders + 'BepInEx/plugins/OpenACAILoader'
    'OpenACAI.Endnight.Loader.Bootstrap' = $runtimeFolders
    'OpenACAI.Endnight.Loader.Preloader' = $runtimeFolders
    'OpenACAIAdminTool' = @($roots | ForEach-Object { "$_/Mods/Modstoke.Admin" }) + @('BepInEx/plugins/OpenACAIAdminTool', 'BepInEx/plugins', 'Mods')
}
$allowed = @{}
foreach ($name in $assemblies.Keys) {
    foreach ($folder in $assemblies[$name]) {
        foreach ($extension in @('.dll', '.pdb', '.xml', '.deps.json')) {
            $allowed["$folder/$name$extension"] = $name
        }
    }
}
foreach ($root in $roots) {
    $allowed["$root/Runtime/OpenACAI.Endnight.Loader.runtimeconfig.json"] = 'runtimeconfig'
}

function Restore-Files($Receipt, [string]$BackupRoot) {
    Assert-Stopped
    if ($Receipt.schema -ne 1 -or $Receipt.game -ne $game) { throw 'This backup belongs to a different game folder or format.' }
    $seen = @{}
    foreach ($file in $Receipt.files) {
        if (-not $allowed.ContainsKey([string]$file.path) -or $seen.ContainsKey([string]$file.path) -or
            $file.sha256 -notmatch '^[0-9A-Fa-f]{64}$') { throw 'Invalid cleanup backup entry.' }
        $seen[[string]$file.path] = $true
        $source = Child $BackupRoot ('files/' + $file.path)
        $target = Child $game $file.path
        if ((Hash $source) -ne $file.sha256) { throw "Backup changed: $($file.path)" }
        if ((Test-Path -LiteralPath $target) -and (Hash $target) -ne $file.sha256) {
            throw "Restore would overwrite a changed file: $($file.path)"
        }
    }
    foreach ($file in $Receipt.files) {
        $target = Child $game $file.path
        if (-not (Test-Path -LiteralPath $target)) {
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            [IO.File]::Copy((Child $BackupRoot ('files/' + $file.path)), $target, $false)
        }
        if ((Hash $target) -ne $file.sha256) { throw "Restore readback failed: $($file.path)" }
    }
}

if ($Restore) {
    $backup = [IO.Path]::GetFullPath($Restore).TrimEnd('\')
    Assert-PlainPath $backup
    $receipt = Get-Content -LiteralPath (Child $backup 'cleanup.json') -Raw | ConvertFrom-Json
    if ($PSCmdlet.ShouldProcess($game, "Restore legacy files from $backup")) {
        Restore-Files $receipt $backup
        Write-Host 'Legacy files restored. Reinstall the matching old loader package before launching.'
    }
    return
}

$files = @()
foreach ($name in ($assemblies.Keys | Sort-Object)) {
    foreach ($folder in $assemblies[$name]) {
        $dll = Child $game "$folder/$name.dll"
        if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { continue }
        try { $identity = [Reflection.AssemblyName]::GetAssemblyName($dll).Name }
        catch [BadImageFormatException] { Write-Warning "Skipping unrecognized file: $dll"; continue }
        if ($identity -ne $name) { Write-Warning "Assembly identity does not match: $dll"; continue }
        foreach ($extension in @('.dll', '.pdb', '.xml', '.deps.json')) {
            $relative = "$folder/$name$extension"
            $path = Child $game $relative
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                $files += [pscustomobject]@{ path = $relative; sha256 = (Hash $path); size = (Get-Item -LiteralPath $path).Length }
            }
        }
    }
}
foreach ($root in $roots) {
    $legacyConfig = "$root/Runtime/OpenACAI.Endnight.Loader.runtimeconfig.json"
    $config = Child $game $legacyConfig
    if ((Test-Path -LiteralPath $config -PathType Leaf) -and
        @($files | Where-Object path -eq "$root/Runtime/OpenACAI.Endnight.Loader.Bootstrap.dll").Count -gt 0) {
        $files += [pscustomobject]@{ path = $legacyConfig; sha256 = (Hash $config); size = (Get-Item -LiteralPath $config).Length }
    }
}

Write-Host "Game: $game"
$files | Format-Table path,size -AutoSize | Out-Host
Write-Host "$($files.Count) recognized legacy files. Saves, settings, assets and other mods are not selected."
if (Test-Path -LiteralPath (Child $game 'vortex.deployment.json')) {
    Write-Warning 'Vortex deployment detected. Disable the old Modstoke package in Vortex first, or redeployment may restore these files.'
}
if (-not $Apply) { Write-Host 'Preview only. Run again with -Apply to back up and remove these files.'; return }
if ($files.Count -eq 0) { return }
Assert-Stopped
if (-not $PSCmdlet.ShouldProcess($game, 'Back up and remove the listed pre-rename Modstoke files')) { return }

$backupBase = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Modstoke/MigrationBackups'
$backup = [IO.Path]::GetFullPath((Join-Path $backupBase ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))))
Assert-PlainPath $backup
if ($backup.StartsWith($game + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The backup must be outside the game folder.' }
[void][IO.Directory]::CreateDirectory($backup)
foreach ($file in $files) {
    $source = Child $game $file.path
    if ((Hash $source) -ne $file.sha256) { throw "File changed before backup: $($file.path)" }
    $target = Child $backup ('files/' + $file.path)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    [IO.File]::Copy($source, $target, $false)
    if ((Hash $target) -ne $file.sha256) { throw "Backup readback failed: $($file.path)" }
}
$receipt = [pscustomobject]@{ schema = 1; game = $game; createdUtc = [DateTime]::UtcNow.ToString('o'); files = $files }
[IO.File]::WriteAllText((Child $backup 'cleanup.json'), ($receipt | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
Write-Host "Backup: $backup"
try {
    Assert-Stopped
    foreach ($file in $files) {
        $target = Child $game $file.path
        if ((Hash $target) -ne $file.sha256) { throw "File changed before removal: $($file.path)" }
        Remove-Item -LiteralPath $target -Force
        if (Test-Path -LiteralPath $target) { throw "File was not removed: $($file.path)" }
    }
} catch {
    $failure = $_
    try { Restore-Files $receipt $backup }
    catch { Write-Warning "Automatic restoration did not complete. Keep the backup at $backup. $($_.Exception.Message)" }
    throw $failure
}
Write-Host 'Cleanup complete. Install the matching renamed Loader and rebuilt mods before launching the game.'
[pscustomobject]@{ Backup = $backup; Files = $files.Count }
