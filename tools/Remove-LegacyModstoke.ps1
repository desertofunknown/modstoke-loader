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
$sharedRuntimes = 'dotnet/shared/Microsoft.NETCore.App'
$hostRuntimes = 'dotnet/host/fxr'

# Nothing in Virginia's assets folder is selected, and it keeps its parent folders from counting as empty.
function Test-Protected([string]$Path) { $Path -match '^(OpenACAI|Modstoke)/Mods/Modstoke\.Virginia/assets(/|$)' }

# Restore only accepts paths a cleanup could have selected.
function Test-Selectable([string]$Path, [bool]$Folder) {
    if (Test-Protected $Path) { return $false }
    if ($Path -match '^OpenACAI/Runtime(/|$)' -or
        $Path -match '^dotnet/(shared/Microsoft\.NETCore\.App|host/fxr)/\d+\.\d+\.\d+[^/]*(/|$)') { return $true }
    if ($Folder) { return $Path -match '^OpenACAI(/(Mods|UserData)(/.*)?)?$' }
    return $allowed.ContainsKey($Path) -or $Path -eq 'System.Private.CoreLib.dll'
}

function Restore-Files($Receipt, [string]$BackupRoot) {
    Assert-Stopped
    if ($Receipt.schema -notin @(1, 2) -or $Receipt.game -ne $game) { throw 'This backup belongs to a different game folder or format.' }
    $folders = @(if ($Receipt.schema -ge 2) { $Receipt.folders })
    $seen = @{}
    foreach ($file in $Receipt.files) {
        if (-not (Test-Selectable ([string]$file.path) $false) -or $seen.ContainsKey([string]$file.path) -or
            $file.sha256 -notmatch '^[0-9A-Fa-f]{64}$') { throw 'Invalid cleanup backup entry.' }
        $seen[[string]$file.path] = $true
        $source = Child $BackupRoot ('files/' + $file.path)
        $target = Child $game $file.path
        if ((Hash $source) -ne $file.sha256) { throw "Backup changed: $($file.path)" }
        if ((Test-Path -LiteralPath $target) -and (Hash $target) -ne $file.sha256) {
            throw "Restore would overwrite a changed file: $($file.path)"
        }
    }
    foreach ($folder in $folders) {
        if (-not (Test-Selectable ([string]$folder) $true)) { throw 'Invalid cleanup backup entry.' }
    }
    foreach ($file in $Receipt.files) {
        $target = Child $game $file.path
        if (-not (Test-Path -LiteralPath $target)) {
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            [IO.File]::Copy((Child $BackupRoot ('files/' + $file.path)), $target, $false)
        }
        if ((Hash $target) -ne $file.sha256) { throw "Restore readback failed: $($file.path)" }
    }
    foreach ($folder in $folders) { [void][IO.Directory]::CreateDirectory((Child $game $folder)) }
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
$folders = @()
$groups = @()
$notes = @()
$selected = @{}

function Add-File([string]$Relative) {
    if ($selected.ContainsKey($Relative)) { return }
    if (Test-Protected $Relative) { throw "Protected path selected: $Relative" }
    $path = Child $game $Relative
    $script:files += [pscustomobject]@{ path = $Relative; sha256 = (Hash $path); size = (Get-Item -LiteralPath $path -Force).Length }
    $selected[$Relative] = $true
}

# Lists every file and folder below a folder, or returns nothing if it holds a link.
function Get-Tree([string]$Relative) {
    $treeFolders = @($Relative)
    $treeFiles = @()
    for ($i = 0; $i -lt $treeFolders.Count; $i++) {
        foreach ($entry in ([IO.DirectoryInfo](Child $game $treeFolders[$i])).GetFileSystemInfos()) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { return }
            $path = "$($treeFolders[$i])/$($entry.Name)"
            if ($entry -is [IO.DirectoryInfo]) { $treeFolders += $path } else { $treeFiles += $path }
        }
    }
    [pscustomobject]@{ Folders = $treeFolders; Files = $treeFiles }
}

function Add-Tree([string]$Relative, [string]$Reason, $Tree) {
    foreach ($file in $Tree.Files) { Add-File $file }
    $script:folders += @($Tree.Folders | Where-Object { $script:folders -notcontains $_ })
    $inner = @($script:groups | Where-Object { $_.path.StartsWith("$Relative/", [StringComparison]::OrdinalIgnoreCase) })
    if ($inner.Count -gt 0) { $Reason = (@(@($inner | ForEach-Object { $_.reason }) + $Reason | Select-Object -Unique) -join ', ') }
    $script:groups = @($script:groups | Where-Object { $inner -notcontains $_ }) + [pscustomobject]@{ path = $Relative; reason = $Reason }
}

# A folder counts as empty when everything left in it is already selected.
function Add-EmptyTree([string]$Relative) {
    $tree = Get-Tree $Relative
    if (-not $tree -or @($tree.Folders | Where-Object { Test-Protected $_ }).Count -gt 0 -or
        @($tree.Files | Where-Object { -not $selected.ContainsKey($_) }).Count -gt 0) { return $false }
    Add-Tree $Relative 'empty' $tree
    return $true
}

$doorstop = @{}
$ini = Child $game 'doorstop_config.ini'
if (Test-Path -LiteralPath $ini -PathType Leaf) {
    foreach ($line in [IO.File]::ReadAllLines($ini)) {
        if ($line -match '^\s*([A-Za-z_]+)\s*=\s*(.*?)\s*$') { $doorstop[$Matches[1]] = $Matches[2].Trim('"') }
    }
}

# Doorstop paths are relative to the game folder. Anything outside it is left alone.
function Doorstop-Path([string]$Key) {
    if (-not $doorstop.ContainsKey($Key) -or [string]::IsNullOrWhiteSpace($doorstop[$Key])) { return $null }
    try { $path = [IO.Path]::GetFullPath([IO.Path]::Combine($game, $doorstop[$Key])) } catch { return $null }
    if (-not $path.StartsWith($game + '\', [StringComparison]::OrdinalIgnoreCase)) { return $null }
    return $path.Substring($game.Length + 1).Replace('\', '/')
}

function Runtime-Version([string]$Path, [string]$Base, [switch]$Folder) {
    $pattern = '^' + [regex]::Escape($Base) + '/([^/]+)' + $(if ($Folder) { '$' } else { '/[^/]+$' })
    if ($Path -and $Path -match $pattern) {
        $version = $Matches[1]
        if (Test-Path -LiteralPath (Child $game $Path) -PathType $(if ($Folder) { 'Container' } else { 'Leaf' })) { return $version }
    }
    return $null
}

foreach ($name in ($assemblies.Keys | Sort-Object)) {
    foreach ($folder in $assemblies[$name]) {
        $dll = Child $game "$folder/$name.dll"
        if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { continue }
        try { $identity = [Reflection.AssemblyName]::GetAssemblyName($dll).Name }
        catch [BadImageFormatException] { Write-Warning "Skipping unrecognized file: $dll"; continue }
        if ($identity -ne $name) { Write-Warning "Assembly identity does not match: $dll"; continue }
        foreach ($extension in @('.dll', '.pdb', '.xml', '.deps.json')) {
            $relative = "$folder/$name$extension"
            if (Test-Path -LiteralPath (Child $game $relative) -PathType Leaf) { Add-File $relative }
        }
    }
}
foreach ($root in $roots) {
    $legacyConfig = "$root/Runtime/OpenACAI.Endnight.Loader.runtimeconfig.json"
    if ((Test-Path -LiteralPath (Child $game $legacyConfig) -PathType Leaf) -and
        $selected.ContainsKey("$root/Runtime/OpenACAI.Endnight.Loader.Bootstrap.dll")) {
        Add-File $legacyConfig
    }
}

# OpenACAI/Runtime is only safe to remove once Doorstop starts the Modstoke runtime.
$target = Doorstop-Path 'target_assembly'
$modstokeActive = $target -and $target.StartsWith('Modstoke/Runtime/', [StringComparison]::OrdinalIgnoreCase) -and
    (Test-Path -LiteralPath (Child $game $target) -PathType Leaf)
if (Test-Path -LiteralPath (Child $game 'OpenACAI/Runtime') -PathType Container) {
    if ($modstokeActive) {
        $tree = Get-Tree 'OpenACAI/Runtime'
        if ($tree) { Add-Tree 'OpenACAI/Runtime' 'old runtime' $tree }
        else { Write-Warning 'Skipping OpenACAI\Runtime: it contains a linked folder.' }
    } else {
        $notes += 'OpenACAI\Runtime is kept: doorstop_config.ini does not point to an installed Modstoke\Runtime.'
    }
}

foreach ($name in @('Mods', 'UserData')) {
    $relative = "OpenACAI/$name"
    if (-not (Test-Path -LiteralPath (Child $game $relative) -PathType Container) -or (Add-EmptyTree $relative)) { continue }
    $clashes = @()
    $waiting = 0
    foreach ($entry in (Get-ChildItem -LiteralPath (Child $game $relative) -Force)) {
        if (Test-Path -LiteralPath (Child $game "Modstoke/$name/$($entry.Name)")) { $clashes += "Modstoke\$name\$($entry.Name)" }
        else { $waiting++ }
    }
    if ($waiting -gt 0) {
        $notes += "OpenACAI\$name still has $waiting item(s). " +
            $(if ($modstokeActive) { "The Loader moves them into Modstoke\$name on its next start." }
              else { "The new Loader moves them into Modstoke\$name on its first start." })
    }
    if ($clashes.Count -gt 0) {
        $notes += "OpenACAI\$name has entries the Loader will not move because these already exist:"
        $notes += @($clashes | ForEach-Object { "  $_" })
        $notes += '  Keep the copy you want, then remove or merge the other one yourself.'
    }
}
if (Test-Path -LiteralPath (Child $game 'OpenACAI') -PathType Container) { [void](Add-EmptyTree 'OpenACAI') }

# Remove other .NET versions only when the INI names a runtime that is actually installed.
$coreVersion = Runtime-Version (Doorstop-Path 'coreclr_path') $sharedRuntimes
$hostVersion = Runtime-Version (Doorstop-Path 'hostfxr_path') $hostRuntimes
$corlibSet = $doorstop.ContainsKey('corlib_dir') -and -not [string]::IsNullOrWhiteSpace($doorstop['corlib_dir'])
$corlibVersion = Runtime-Version (Doorstop-Path 'corlib_dir') $sharedRuntimes -Folder
if ($coreVersion -and $hostVersion -and (-not $corlibSet -or $corlibVersion)) {
    foreach ($base in @($sharedRuntimes, $hostRuntimes)) {
        $keep = if ($base -eq $sharedRuntimes) { @($coreVersion, $corlibVersion) } else { @($hostVersion) }
        foreach ($entry in (Get-ChildItem -LiteralPath (Child $game $base) -Directory -Force)) {
            if ($entry.Name -notmatch '^\d+\.\d+\.\d+' -or $keep -contains $entry.Name) { continue }
            $tree = Get-Tree "$base/$($entry.Name)"
            if ($tree) { Add-Tree "$base/$($entry.Name)" 'unused .NET runtime' $tree }
            else { Write-Warning "Skipping $base/$($entry.Name): it contains a linked folder." }
        }
    }
    if (@($groups | Where-Object { $_.path.StartsWith('dotnet/') }).Count -gt 0) {
        $notes += "doorstop_config.ini uses .NET $coreVersion; other runtime versions are selected."
    }
    $corlib = Child $game 'System.Private.CoreLib.dll'
    if (Test-Path -LiteralPath $corlib -PathType Leaf) {
        try { $identity = [Reflection.AssemblyName]::GetAssemblyName($corlib).Name } catch { $identity = $null }
        $major = [Diagnostics.FileVersionInfo]::GetVersionInfo($corlib).FileMajorPart
        if ($identity -eq 'System.Private.CoreLib' -and $major -gt 0 -and $major -lt 11) {
            Add-File 'System.Private.CoreLib.dll'
            $notes += "System.Private.CoreLib.dll in the game folder is from .NET $major and is not used by the Loader."
        }
    }
} elseif ($doorstop.ContainsKey('coreclr_path') -or $doorstop.ContainsKey('hostfxr_path')) {
    $notes += 'doorstop_config.ini does not name an installed .NET runtime under dotnet\; runtime folders are kept.'
}

# Mods that kept their DLL names can still be installed the old way next to the Modstoke package.
foreach ($name in @('Modstoke.Client', 'Modstoke.Community', 'Modstoke.Terraforming', 'Modstoke.Virginia')) {
    $package = @('Modstoke', 'OpenACAI') | Where-Object { Test-Path -LiteralPath (Child $game "$_/Mods/$name/$name.dll") -PathType Leaf } |
        Select-Object -First 1
    if (-not $package) { continue }
    $copies = @()
    if (Test-Path -LiteralPath (Child $game "Mods/$name.dll") -PathType Leaf) { $copies += "Mods\$name.dll" }
    if (Test-Path -LiteralPath (Child $game 'BepInEx/plugins') -PathType Container) {
        $copies += @(Get-ChildItem -LiteralPath (Child $game 'BepInEx/plugins') -Recurse -File -Force -Filter "$name.dll" |
            ForEach-Object { $_.FullName.Substring($game.Length + 1) })
    }
    if ($copies.Count -gt 0) {
        $notes += "$name is installed in $package\Mods\$name and also here, so it would load twice:"
        $notes += @($copies | ForEach-Object { "  $_" })
        $notes += '  These copies are not removed. Delete them yourself or remove them in your mod manager.'
    }
}

Write-Host "Game: $game"
$loose = @($files | Where-Object { $path = $_.path; @($groups | Where-Object { $path.StartsWith("$($_.path)/", [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0 })
$loose | Format-Table path,size -AutoSize | Out-Host
$groups | ForEach-Object {
    $prefix = "$($_.path)/"
    $inside = @($files | Where-Object { $_.path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
    $size = 0
    foreach ($file in $inside) { $size += $file.size }
    [pscustomobject]@{ folder = $prefix; files = $inside.Count; size = $size; reason = $_.reason }
} | Format-Table -AutoSize | Out-Host
Write-Host "$($files.Count) recognized legacy files and $($groups.Count) folders. Saves, settings, assets and other mods are not selected."
$notes | ForEach-Object { Write-Host $_ }
if (Test-Path -LiteralPath (Child $game 'vortex.deployment.json')) {
    Write-Warning 'Vortex deployment detected. Disable the old Modstoke package in Vortex first, or redeployment may restore these files.'
}
if (-not $Apply) { Write-Host 'Preview only. Run again with -Apply to back up and remove these files and folders.'; return }
if ($files.Count -eq 0 -and $folders.Count -eq 0) { return }
Assert-Stopped
if (-not $PSCmdlet.ShouldProcess($game, 'Back up and remove the listed legacy files and folders')) { return }

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
$receipt = [pscustomobject]@{ schema = 2; game = $game; createdUtc = [DateTime]::UtcNow.ToString('o'); files = $files; folders = $folders }
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
    # Deepest first; a folder that gained new content fails here and triggers the restore.
    foreach ($folder in ($folders | Sort-Object { ($_ -split '/').Count } -Descending)) {
        $target = Child $game $folder
        if (Test-Path -LiteralPath $target) { [IO.Directory]::Delete($target, $false) }
    }
} catch {
    $failure = $_
    try { Restore-Files $receipt $backup }
    catch { Write-Warning "Automatic restoration did not complete. Keep the backup at $backup. $($_.Exception.Message)" }
    throw $failure
}
Write-Host 'Cleanup complete. Install the matching renamed Loader and rebuilt mods before launching the game.'
[pscustomobject]@{ Backup = $backup; Files = $files.Count; Folders = $folders.Count }
