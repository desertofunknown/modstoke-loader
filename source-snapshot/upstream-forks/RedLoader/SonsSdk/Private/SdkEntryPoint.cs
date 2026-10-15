using System.Drawing;
using System.Reflection;
using HarmonyLib;
using RedLoader;
using RedLoader.Bootstrap;
using RedLoader.Utils;
using Sons.Events;
using Sons.Gameplay.GameSetup;
using Sons.Gui;
using Sons.Multiplayer;
using Sons.Save;
using Sons.Settings;
using SonsGameManager;
using SonsLoaderPlugin;
using SonsSdk.Attributes;
using SonsSdk.Networking;
using SonsSdk.Private;
using Steamworks;
using SUI;
using TheForest;
using TheForest.Utils;
using UnityEngine;
using Color = System.Drawing.Color;
using Version = System.Version;

namespace SonsSdk;

public class SdkEntryPoint : IModProcessor
{
    internal static HarmonyLib.Harmony Harmony = new("sonssdk_harmony");

    private static bool _shouldCheatsBeEnabled = false;
    private static readonly HashSet<string> ModDependencySearchDirectories = new(StringComparer.OrdinalIgnoreCase);
    private static bool _assemblyResolverRegistered;
    private bool _createModManagerUi;
    private ISdkLifecycleDriver _lifecycleDriver;
    
    public List<ModBase> LoadPlugins()
    {
        var result = new List<ModBase>();

        var candidates = DiscoverModCandidates().ToList();
        RegisterModDependencyResolver(candidates);

        RLog.Msg($"Discovered {candidates.Count} mod {(candidates.Count == 1 ? "assembly" : "assemblies")}");

        var loadedManifestIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.Manifest?.Id) && !loadedManifestIds.Add(candidate.Manifest.Id))
            {
                RLog.Warning($"Skipping duplicate mod id '{candidate.Manifest.Id}' at '{candidate.AssemblyPath}'.");
                SonsMod.ReportMod(candidate.Manifest.Id, "Duplicate mod id");
                continue;
            }

            result.AddRange(TryLoadFromCandidate(candidate));
        }

        return result;
    }
    
    public List<ModBase> TryLoadFromPath(string assemblyPath)
    {
        return TryLoadFromPath(assemblyPath, ResolveLegacyManifestPath(assemblyPath));
    }

    /// <summary>Loads a mod using the manifest selected by the hosting loader.</summary>
    /// <param name="assemblyPath">The mod assembly to load.</param>
    /// <param name="manifestPath">The selected manifest, or null to use the legacy location.</param>
    /// <returns>The accepted mod instances, or an empty list when loading is rejected.</returns>
    public List<ModBase> TryLoadFromPath(string assemblyPath, string manifestPath)
    {
        manifestPath ??= ResolveLegacyManifestPath(assemblyPath);
        return TryLoadFromCandidate(new ModCandidate(assemblyPath, manifestPath, TryReadManifestSafe(manifestPath, assemblyPath), 0));
    }

    private List<ModBase> TryLoadFromCandidate(ModCandidate candidate)
    {
        var mods = new List<ModBase>();

        var assemblyPath = candidate.AssemblyPath;
        var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
        var manifest = candidate.Manifest ?? TryReadManifestSafe(candidate.ManifestPath, assemblyPath);
        if (manifest != null)
        {
            NormalizeManifest(manifest, assemblyName);

            var platform = string.IsNullOrWhiteSpace(manifest.Platform) ? "Client" : manifest.Platform;
            if (string.Equals(platform, "Client", StringComparison.OrdinalIgnoreCase) && LoaderEnvironment.IsDedicatedServer)
            {
                RLog.Error($"Mod {assemblyName} is a client mod and cannot be loaded on a dedicated server.");
                SonsMod.ReportMod(manifest.Id, "Client mod cannot be loaded on a dedicated server.");
            }
            else if (string.Equals(platform, "Server", StringComparison.OrdinalIgnoreCase) && !LoaderEnvironment.IsDedicatedServer)
            {
                RLog.Error($"Mod {assemblyName} is a server mod and cannot be loaded on a client.");
                SonsMod.ReportMod(manifest.Id, "Server mod cannot be loaded on a client.");
            }
            // TODO: Check Compatibility
            else if (!string.IsNullOrEmpty(manifest.LoaderVersion) && !LoaderUtils.IsCompatible(manifest.LoaderVersion))
            {
                RLog.Error($"Mod {assemblyName} requires a different version of RedLoader.");
                SonsMod.ReportMod(manifest.Id, $"Requires RedLoader >={manifest.LoaderVersion}");
            }
            else if (manifest.Type == ManifestData.EAssemblyType.Library)
            {
                RLog.Error($"{assemblyName} is a library but was put into the mods folder.");
                SonsMod.ReportMod(manifest.Id, $"Is a library but was put into the mods folder.");
            }
            else if (InitMod(assemblyPath, manifest, out var mod))
            {
                if (!string.IsNullOrEmpty(manifest.GameVersion) && !CheckGameVersionCompatibility(manifest.GameVersion))
                {
                    RLog.Warning($"{assemblyName} is made for a different version of the game.");
                    RLog.Warning($"The mod will still load, but may not work correctly.");
                    SonsMod.ReportMod(manifest.Id, "Mod was loaded, but was made for a different game version");
                }
                
                RLog.Msg(System.ConsoleColor.Magenta, $"Loaded mod {mod.Manifest.Name} for game {manifest.GameVersion}");
                
                mods.Add(mod);
            }
        }
        else
        {
            RLog.Error($"{assemblyName} does not have a manifest.json file. Expected: {candidate.ManifestPath}");
            SonsMod.ReportMod(assemblyName, "Missing manifest.json");
        }

        return mods;
    }

    private static IEnumerable<ModCandidate> DiscoverModCandidates()
    {
        var candidatesByAssembly = new Dictionary<string, ModCandidate>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(LoaderEnvironment.ModsDirectory))
            return candidatesByAssembly.Values;

        foreach (var assemblyPath in Directory.EnumerateFiles(LoaderEnvironment.ModsDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var manifestPath = ResolveLegacyManifestPath(assemblyPath);
            AddCandidate(candidatesByAssembly, assemblyPath, manifestPath, score: 0);
        }

        foreach (var manifestPath in EnumerateModManifests())
        {
            var manifest = TryReadManifestSafe(manifestPath, manifestPath);
            if (manifest == null)
                continue;

            foreach (var assemblyPath in FindAssembliesForManifest(manifestPath, manifest))
                AddCandidate(candidatesByAssembly, assemblyPath, manifestPath, manifest);
        }

        return candidatesByAssembly.Values
                                   .OrderBy(x => x.Score)
                                   .ThenBy(x => x.AssemblyPath, StringComparer.OrdinalIgnoreCase)
                                   .ToList();
    }

    private static IEnumerable<string> EnumerateModManifests()
    {
        foreach (var manifestPath in Directory.EnumerateFiles(LoaderEnvironment.ModsDirectory, "manifest.json", SearchOption.AllDirectories))
        {
            if (IsDisabledModPath(manifestPath))
                continue;

            yield return manifestPath;
        }
    }

    private static IEnumerable<string> FindAssembliesForManifest(string manifestPath, ManifestData manifest)
    {
        var manifestDirectory = Path.GetDirectoryName(manifestPath);
        if (string.IsNullOrWhiteSpace(manifestDirectory))
            yield break;

        var packageRoot = GetPackageRoot(manifestDirectory);
        var assemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            manifest.Id,
            manifest.Name,
            Path.GetFileName(manifestDirectory),
            Path.GetFileName(packageRoot)
        };

        foreach (var assemblyName in assemblyNames.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            foreach (var path in GetLikelyAssemblyPaths(packageRoot, manifestDirectory, assemblyName))
            {
                if (File.Exists(path) && !IsDisabledModPath(path))
                    yield return path;
            }

            foreach (var path in Directory.EnumerateFiles(packageRoot, $"{assemblyName}.dll", SearchOption.AllDirectories))
            {
                if (!IsDisabledModPath(path))
                    yield return path;
            }
        }
    }

    private static IEnumerable<string> GetLikelyAssemblyPaths(string packageRoot, string manifestDirectory, string assemblyName)
    {
        yield return Path.Combine(manifestDirectory, $"{assemblyName}.dll");
        yield return Path.Combine(Directory.GetParent(manifestDirectory)?.FullName ?? manifestDirectory, $"{assemblyName}.dll");
        yield return Path.Combine(packageRoot, $"{assemblyName}.dll");
        yield return Path.Combine(packageRoot, "Mods", $"{assemblyName}.dll");
        yield return Path.Combine(packageRoot, "Mods", assemblyName, $"{assemblyName}.dll");
    }

    private static void AddCandidate(Dictionary<string, ModCandidate> candidatesByAssembly,
                                     string assemblyPath,
                                     string manifestPath,
                                     ManifestData manifest = null,
                                     int? score = null)
    {
        if (IsDisabledModPath(assemblyPath))
            return;

        var fullAssemblyPath = Path.GetFullPath(assemblyPath);
        var fullManifestPath = Path.GetFullPath(manifestPath);
        var candidateScore = score ?? ScoreCandidate(fullAssemblyPath, fullManifestPath);
        if (manifest != null)
            NormalizeManifest(manifest, Path.GetFileNameWithoutExtension(fullAssemblyPath));

        var candidate = new ModCandidate(fullAssemblyPath, fullManifestPath, manifest, candidateScore);

        if (candidatesByAssembly.TryGetValue(fullAssemblyPath, out var existing) &&
            (existing.Score < candidate.Score || existing.Score == candidate.Score && existing.Manifest != null))
        {
            return;
        }

        candidatesByAssembly[fullAssemblyPath] = candidate;
    }

    private static int ScoreCandidate(string assemblyPath, string manifestPath)
    {
        if (string.Equals(ResolveLegacyManifestPath(assemblyPath), manifestPath, StringComparison.OrdinalIgnoreCase))
            return 0;

        if (string.Equals(Path.GetDirectoryName(assemblyPath), Path.GetDirectoryName(manifestPath), StringComparison.OrdinalIgnoreCase))
            return 1;

        return 10;
    }

    private static string ResolveLegacyManifestPath(string assemblyPath)
    {
        var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
        return Path.Combine(Path.GetDirectoryName(assemblyPath) ?? LoaderEnvironment.ModsDirectory, assemblyName, "manifest.json");
    }

    private static string GetPackageRoot(string path)
    {
        var modsDirectory = Path.GetFullPath(LoaderEnvironment.ModsDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = new DirectoryInfo(Path.GetFullPath(path));

        if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                          modsDirectory,
                          StringComparison.OrdinalIgnoreCase))
        {
            return current.FullName;
        }

        while (current.Parent != null &&
               !string.Equals(current.Parent.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                              modsDirectory,
                              StringComparison.OrdinalIgnoreCase))
        {
            current = current.Parent;
        }

        return current.FullName;
    }

    private static bool IsDisabledModPath(string path)
    {
        var modsDirectory = Path.GetFullPath(LoaderEnvironment.ModsDirectory);
        var disabledDirectory = Path.Combine(modsDirectory, "_Disabled");
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(disabledDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                                disabledDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                                StringComparison.OrdinalIgnoreCase);
    }

    private static ManifestData TryReadManifestSafe(string manifestPath, string reportId)
    {
        try
        {
            return ManifestReader.TryReadManifest(manifestPath);
        }
        catch (Exception ex)
        {
            RLog.Error($"Failed to read manifest '{manifestPath}': {ex.Message}");
            SonsMod.ReportMod(Path.GetFileNameWithoutExtension(reportId), "Invalid manifest.json");
            return null;
        }
    }

    private static void NormalizeManifest(ManifestData manifest, string assemblyName)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id))
            manifest.Id = assemblyName;

        if (string.IsNullOrWhiteSpace(manifest.Name))
            manifest.Name = manifest.Id;

        if (string.IsNullOrWhiteSpace(manifest.Version))
            manifest.Version = "0.0.0";
    }

    private static void RegisterModDependencyResolver(IEnumerable<ModCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            AddDependencySearchDirectory(Path.GetDirectoryName(candidate.AssemblyPath));
            AddDependencySearchDirectory(Path.Combine(Path.GetDirectoryName(candidate.AssemblyPath) ?? "", "Libs"));
            AddDependencySearchDirectory(Path.Combine(GetPackageRoot(Path.GetDirectoryName(candidate.AssemblyPath) ?? ""), "Libs"));
        }

        AddDependencySearchDirectory(LoaderEnvironment.LibsDirectory);
        AddDependencySearchDirectory(LoaderEnvironment.LoaderAssemblyDirectory);

        if (_assemblyResolverRegistered)
            return;

        AppDomain.CurrentDomain.AssemblyResolve += ResolveModDependency;
        _assemblyResolverRegistered = true;
    }

    private static void AddDependencySearchDirectory(string directory)
    {
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            ModDependencySearchDirectories.Add(Path.GetFullPath(directory));
    }

    private static Assembly ResolveModDependency(object sender, ResolveEventArgs args)
    {
        var requestedName = new AssemblyName(args.Name).Name;
        if (string.IsNullOrWhiteSpace(requestedName))
            return null;

        foreach (var directory in ModDependencySearchDirectories)
        {
            var assemblyPath = Path.Combine(directory, $"{requestedName}.dll");
            if (!File.Exists(assemblyPath))
                continue;

            try
            {
                return Assembly.LoadFrom(assemblyPath);
            }
            catch (Exception ex)
            {
                RLog.Warning($"Failed to resolve dependency '{requestedName}' from '{assemblyPath}': {ex.Message}");
            }
        }

        return null;
    }

    private bool CheckGameVersionCompatibility(string version)
    {
        if (version.Contains('.'))
            return true;
        
        return version == Sons.GameApplication.Version.GetVersionString();
    }
    
    private bool InitMod(string assemblyPath, ManifestData data, out SonsMod outMod)
    {
        outMod = null;
        
        try
        {
            var assembly = Assembly.LoadFrom(assemblyPath);
        
            var implementation = assembly.GetTypes().FirstOrDefault(t => t.IsSubclassOf(typeof(SonsMod)));
            if (implementation == null)
            {
                RLog.Error($"Failed to find a valid implementation of SonsMod in {assembly.FullName}");
                return false;
            }
            
            var mod = (SonsMod)Activator.CreateInstance(implementation, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, null, null);
            
            if(mod == null)
            {
                RLog.Error($"Failed to create an instance of {implementation.FullName}");
                return false;
            }
            
            if (!TryParseVersion(data.Version, out var version))
            {
                RLog.Error($"Invalid version '{data.Version}' in manifest for {data.Id}.");
                SonsMod.ReportMod(data.Id, "Invalid mod version");
                return false;
            }

            data.VersionObject = version;
                
            mod.Info = new MelonInfoAttribute(
                                                 mod.GetType(), 
                                                 string.IsNullOrEmpty(data.Name) ? data.Id : data.Name, 
                                                 version.Major, 
                                                 version.Minor,
                                                 version.Build,
                                                 data.Author,
                                                 data.Url);

            mod.Manifest = data;
                
            mod.ModAssembly = assembly;
            mod.Priority = data.Priority;
            mod.ConsoleColor = LoaderUtils.ColorFromString(data.LogColor);
            mod.AuthorConsoleColor = RLog.DefaultTextColor;
            mod.SupportedGameVersion = data.GameVersion;
            mod.OptionalDependencies = data.Dependencies;
            mod.ID = data.Id;

            // Finish metadata discovery before registration publishes callbacks.
            mod.AssetBundleAttrs = AssetBundleAttributeLoader.GetAllTypes(mod);
            foreach (var attr in mod.AssetBundleAttrs)
                attr.Mod = mod;

            if (!mod.Register())
            {
                RLog.Error($"Mod '{data.Id}' failed to register.");
                SonsMod.ReportMod(data.Id, "Mod registration failed");
                return false;
            }

            outMod = mod;
            return true;
        }
        catch (ReflectionTypeLoadException e)
        {
            RLog.Error($"Failed to inspect mod assembly '{assemblyPath}': {TypeLoader.TypeLoadExceptionToString(e)}");
            SonsMod.ReportMod(data.Id, "Could not load mod assembly");
            return false;
        }
        catch (Exception e)
        {
            RLog.Error(e);
            return false;
        }
    }

    private static bool TryParseVersion(string versionText, out Version version)
    {
        if (Version.TryParse(versionText, out version))
            return true;

        var prereleaseSeparator = versionText?.IndexOf('-') ?? -1;
        if (prereleaseSeparator > 0)
            return Version.TryParse(versionText[..prereleaseSeparator], out version);

        return false;
    }

    private sealed class ModCandidate
    {
        internal ModCandidate(string assemblyPath, string manifestPath, ManifestData manifest, int score)
        {
            AssemblyPath = assemblyPath;
            ManifestPath = manifestPath;
            Manifest = manifest;
            Score = score;
        }

        internal string AssemblyPath { get; }
        internal string ManifestPath { get; }
        internal ManifestData Manifest { get; }
        internal int Score { get; }
    }

    public void InitAfterUnity()
    {
        InitAfterUnity(createModManagerUi: true);
    }

    /// <summary>
    /// Initializes the SDK with optional legacy mod-manager UI. Shared UI and settings remain available.
    /// </summary>
    /// <param name="createModManagerUi">Whether to create the legacy manager, mod indicator and menu buttons.</param>
    public void InitAfterUnity(bool createModManagerUi)
    {
        try
        {
            _lifecycleDriver = SdkEvents.BeginLifecycleSetup();
            _createModManagerUi = createModManagerUi;
            GlobalEvents.OnApplicationLateStart.Subscribe(OnAppLateStart);
            SdkEvents.OnSdkInitialized.Subscribe(OnSdkInitialized);
            SdkEvents.OnGameStart.Subscribe(OnGameStart, priority:999);
            SdkEvents.OnCheatsEnabledChanged.Subscribe(OnCheatsEnabledChanged);
            MiscPatches.Init();
        }
        catch (Exception exception)
        {
            MainInitializer.ReportInitializationFailure(exception);
            throw;
        }
    }

    private void OnAppLateStart()
    {
        if (_lifecycleDriver != null)
        {
            _lifecycleDriver.OnApplicationLateStart();
            return;
        }

        if (MainInitializer.InitializationFailed)
        {
            return;
        }

        try
        {
            GlobalOverlays.CreateBlackScreen();
            SdkEvents.Init();
            MainInitializer.CompleteEventSetup();
        }
        catch (Exception exception)
        {
            MainInitializer.ReportInitializationFailure(exception);
        }
    }

    private void OnSdkInitialized()
    {
        if (_lifecycleDriver != null)
        {
            _lifecycleDriver.OnSdkInitialized(_createModManagerUi);
            return;
        }

        if (_createModManagerUi)
        {
            ModManagerUi.Create();
        }

        ModSettingsUi.Create();

        var save = Utility.GetCommandLineArgValue("--savegame");
        if (!string.IsNullOrEmpty(save) && int.TryParse(save, out var id))
        {
            RLog.Msg($"Found save game argument ({save}). Loading now...");
            Resources.FindObjectsOfTypeAll<LoadMenu>()
                     .First(x=>x.name=="SinglePlayerLoadPanel")
                     .LoadSlotActivated(id, SaveGameManager.GetData<GameStateData>(SaveGameType.SinglePlayer, id));
        }
    }

    private void OnGameStart()
    {
        if (_lifecycleDriver != null)
        {
            _lifecycleDriver.OnSdkGameStart();
            return;
        }

        GameCommands.Init();

        if (!BoltNetwork.isRunning)
            _shouldCheatsBeEnabled = true;

        SetConsoleEnabled(_shouldCheatsBeEnabled);
        
        PanelBlur.SetupBlur();
        
        LoadBootFile();
    }

    private void SetConsoleEnabled(bool shouldEnable)
    {
        DebugConsole.SetCheatsAllowed(shouldEnable);
        
        if(DebugConsole.Instance)
            DebugConsole.Instance.SetBlockConsole(!shouldEnable);
    }

    private bool IsPowerPlayer()
    {
        if (!BoltNetwork.isRunning)
            return true;

        if (BoltNetwork.isServer)
            return true;
        
        return NetUtils.HasUserPermission(SonsTools.GetSteamId(), PlayerRoles.Owner);
    }
    
    private void OnCheatsEnabledChanged(bool isEnabled)
    {
        if (_lifecycleDriver != null)
        {
            _lifecycleDriver.OnCheatsEnabledChanged(isEnabled);
            return;
        }

        SetConsoleEnabled(isEnabled);
        if (LocalPlayer._instance)
        {
            SonsTools.ShowMessage("Cheats have been " + (isEnabled ? "enabled" : "disabled"));
        }

        _shouldCheatsBeEnabled = isEnabled;
    }
    
    private void LoadBootFile()
    {
        try
        {
            var bootFile = Path.Combine(LoaderEnvironment.GameRootDirectory, "boot.txt");
            if (!File.Exists(bootFile))
                return;

            var lines = File.ReadAllLines(bootFile);
            foreach (var line in lines)
            {
                DebugConsole.Instance.SendCommand(line);
            }
        }
        catch (Exception e)
        {
            RLog.Error("Error loading boot file: " + e);
        }
    }
}
