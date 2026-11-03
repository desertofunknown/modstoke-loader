Modstoke Legacy Cleanup 1.1.0

For Windows users upgrading from the old OpenACAI assembly names and the old
OpenACAI\ folder to the renamed Modstoke Loader and rebuilt mods. This is a
migration tool, not a full uninstaller.
Have the matching new Loader and mod packages ready BEFORE using Cleanup.cmd.
The old loader cannot start after its obsolete assemblies are removed.

1. Close the game and dedicated server. If you use Vortex, disable the old
   Modstoke packages first so redeployment does not restore obsolete files.
2. Extract this ZIP anywhere. Run Preview.cmd, then paste your game folder path.
   Find it in Steam > Properties > Installed Files > Browse. Nothing is changed.
3. Run Cleanup.cmd and enter the same path. Read the file list and confirm with Y.
4. Install the matching renamed Loader and rebuilt mods before starting the game.
   Do not use the old pre-rename packages as replacements.

New in 1.1.0:
- Removes the old OpenACAI\Runtime once doorstop_config.ini starts the Loader
  from Modstoke\Runtime. While Doorstop still uses OpenACAI\Runtime, it is kept.
- Removes OpenACAI\Mods, OpenACAI\UserData and OpenACAI\ when they are empty. If
  they still have content, Preview says the Loader moves it on its next start,
  or names the entries that already exist under Modstoke\.
- Removes .NET runtime versions under dotnet\ that doorstop_config.ini does not
  use, and an old System.Private.CoreLib.dll (before .NET 11) in the game folder.
  Both only happen when the runtime named in doorstop_config.ini is installed.
- Lists Client, Community, Terraforming or Virginia copies in BepInEx\plugins or
  the game's Mods folder when the same mod is also in Modstoke\Mods. Those would
  load twice. They are not removed; delete them yourself or in your mod manager.

Only recognized old Loader and Admin assemblies and their matching sidecars, the
old OpenACAI\Runtime, empty OpenACAI folders and unused .NET runtime versions are
removed. Every selected file is copied to a verified backup first; each old .NET
version adds about 80 MB to it. New Modstoke assemblies, saves, settings, mod
assets, other mods and development files stay put. The Modstoke.Virginia assets
folder is never touched.
Old files are found under both OpenACAI\ and Modstoke\. The new Loader moves
OpenACAI\Mods and OpenACAI\UserData into Modstoke\ on first start; openacai.mod.json
files are still used and are intentionally kept.
Client, Community, Terraforming and Virginia kept their DLL names: update those
with matching rebuilt packages. This tool does not decide whether they are current.

Backups are under %LOCALAPPDATA%\Modstoke\MigrationBackups. The exact location is
printed after backup. To restore one, open PowerShell in this extracted folder:

  .\Remove-LegacyModstoke.ps1 -GamePath "D:\SteamLibrary\steamapps\common\Sons Of The Forest" -Restore "C:\path\to\the\printed\backup"

Restore refuses to overwrite files that have changed. It restores only the cleanup
selection, not the whole loader. Use a matching old package when rolling back a
new installation. Keep backups until you have confirmed the new setup works.
Backups made by 1.0.0 can still be restored with this version.

Linked folders are rejected. Shared dependencies, winhttp.dll, Doorstop settings,
the active runtime and generated interop caches are not removed. Runtime files are
only removed from the old OpenACAI\Runtime after the move and from unused .NET
versions. No downloads, uploads, registry changes or permanent execution-policy
changes are made by this utility.
If access is denied, close the game and mod manager; use an administrator terminal
only if that game folder requires it. Do not disable antivirus protection.

The supplied .cmd launchers use Windows PowerShell 5.1 or later. Advanced use:
  .\Remove-LegacyModstoke.ps1 -GamePath "D:\Games\Sons Of The Forest"
  .\Remove-LegacyModstoke.ps1 -GamePath "D:\Games\Sons Of The Forest" -Apply

Independent software; not affiliated with or endorsed by Endnight Games.
Distributed under GPL-3.0-or-later. See LICENSE.
