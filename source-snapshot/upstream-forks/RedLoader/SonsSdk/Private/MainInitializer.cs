using System.Collections;
using System.Drawing;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.Runtime;
using RedLoader;
using RedLoader.Utils;
using Sons.Events;
using Sons.Input;
using Sons.Loading;
using SonsSdk.AssetImporting;
using SonsSdk.Building;
using SonsSdk.Networking;
using SonsSdk.Private;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;
using Color = System.Drawing.Color;

namespace SonsSdk;

internal interface ISdkInitializationDriver
{
    bool InitializationFailed { get; }
    void InitTitleScene();
    void InitDedicatedServer();
    void CompleteEventSetup();
    void ReportInitializationFailure(Exception exception);
}

internal class MainInitializer
{
    private static ISdkInitializationDriver _initializationDriver;
    private static bool _initializationFailed;

    internal static bool InitializationFailed => _initializationDriver?.InitializationFailed ?? _initializationFailed;

    internal static void SetInitializationDriver(ISdkInitializationDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (ReferenceEquals(_initializationDriver, driver))
            return;
        if (_initializationDriver != null || _initializationStarted || _eventSetupCompleted ||
            _initializationFinished || _initializationFailed)
            throw new InvalidOperationException("SDK initialization ownership must be selected before startup.");

        _initializationDriver = driver;
    }

    internal static void InitTitleScene()
    {
        if (_initializationDriver != null)
        {
            _initializationDriver.InitTitleScene();
            return;
        }

        if (_initializationStarted || SdkEvents.OnSdkInitializationCompleted.Disposed)
        {
            return;
        }
        _initializationStarted = true;

        try
        {
            RunInitialization(InitCoro()).RunCoro();
        }
        catch (Exception exception)
        {
            ReportInitializationFailure(exception);
        }
    }

    internal static void InitDedicatedServer()
    {
        if (_initializationDriver != null)
        {
            _initializationDriver.InitDedicatedServer();
            return;
        }

        if (_initializationStarted || SdkEvents.OnSdkInitializationCompleted.Disposed)
        {
            return;
        }

        _initializationStarted = true;
        try
        {
            // Keep synchronous service setup inside the server bootstrap prefix.
            var services = InitSystems();
            if (!services.IsCompleted)
            {
                RunInitialization(WaitForServices(services)).RunCoro();
                return;
            }

            services.GetAwaiter().GetResult();
            if (!_initializationFailed)
            {
                _initializationFinished = true;
                TryCompleteInitialization();
            }
        }
        catch (Exception exception)
        {
            ReportInitializationFailure(exception);
            throw;
        }
    }

    internal static void CompleteEventSetup()
    {
        if (_initializationDriver != null)
        {
            _initializationDriver.CompleteEventSetup();
            return;
        }

        _eventSetupCompleted = true;
        TryCompleteInitialization();
    }

    private static void TryCompleteInitialization()
    {
        if (_eventSetupCompleted && _initializationFinished)
        {
            SdkEvents.OnSdkInitializationCompleted.Invoke(null);
        }
    }

    internal static void ReportInitializationFailure(Exception exception)
    {
        if (_initializationDriver != null)
        {
            _initializationDriver.ReportInitializationFailure(exception);
            return;
        }

        if (SdkEvents.OnSdkInitializationCompleted.Disposed)
        {
            return;
        }

        _initializationFailed = true;
        SdkEvents.OnSdkInitializationCompleted.Invoke(exception);
        try
        {
            RLog.Error($"Shared SDK initialization failed: {exception}");
        }
        catch
        {
            // Diagnostic listeners cannot prevent startup failure cleanup.
        }

        try
        {
            GlobalOverlays.ToggleOverlay(false);
        }
        catch
        {
            // The overlay may be only partially constructed; keep the original failure.
        }
    }

    private static IEnumerator RunInitialization(IEnumerator initialization)
    {
        // Advance this SDK's managed child routines here so their failures reach the host.
        var routines = new Stack<IEnumerator>();
        routines.Push(initialization);
        try
        {
            while (routines.Count > 0)
            {
                if (_initializationFailed)
                    yield break;

                object current;
                try
                {
                    var routine = routines.Peek();
                    if (!routine.MoveNext())
                    {
                        routines.Pop();
                        (routine as IDisposable)?.Dispose();
                        continue;
                    }

                    current = routine.Current;
                    if (current is IEnumerator child)
                    {
                        routines.Push(child);
                        continue;
                    }
                }
                catch (Exception exception)
                {
                    ReportInitializationFailure(exception);
                    yield break;
                }

                yield return current;
            }

            if (!_initializationFailed)
            {
                _initializationFinished = true;
                TryCompleteInitialization();
            }
        }
        finally
        {
            while (routines.Count > 0)
            {
                var routine = routines.Pop();
                try
                {
                    (routine as IDisposable)?.Dispose();
                }
                catch (Exception exception)
                {
                    ReportInitializationFailure(exception);
                }
            }

            if (!_initializationFinished && !SdkEvents.OnSdkInitializationCompleted.Disposed)
            {
                ReportInitializationFailure(new OperationCanceledException("Shared SDK initialization stopped before completion."));
            }
        }
    }

    private static IEnumerator AddProgress(float amount)
    {
        GlobalOverlays.ProgressBar.AddProgress(amount);
        yield return null;
    }

    private static IEnumerator LoadAllCatalogs()
    {
        foreach (var mod in SonsMod.RegisteredMods.ToArray())
        {
            if (!mod.Registered)
                continue;

            if (mod.CatalogLoader != null)
            {
                yield return mod.CatalogLoader(mod);
                continue;
            }

            string[] catalogs;
            try
            {
                var modDataFolder = LoaderEnvironment.GetModDataPath(mod);
                if (!modDataFolder.DirectoryExists())
                {
                    continue;
                }

                catalogs = modDataFolder.GetFiles("catalog*.json").ToArray();
            }
            catch (Exception exception)
            {
                AssetLoaders.ReportAssetError($"Couldn't enumerate addressables catalogs for {mod.ModAssembly.FullName}.", exception);
                continue;
            }

            foreach (var catalog in catalogs)
            {
                if (!mod.Registered)
                    break;
                yield return LoadCatalog(catalog);
            }
        }
    }

    private static IEnumerator LoadCatalog(string catalog)
    {
        AsyncOperationHandle<IResourceLocator> operation;
        try
        {
            RLog.Msg(Color.Orange, "Loading addressables catalog " + catalog);
            operation = Addressables.LoadContentCatalogAsync(catalog, false);
        }
        catch (Exception exception)
        {
            AssetLoaders.ReportAssetError($"Couldn't start loading addressables catalog {catalog}.", exception);
            yield break;
        }

        try
        {
            while (true)
            {
                yield return null;
                try
                {
                    if (!operation.IsValid())
                    {
                        AssetLoaders.ReportAssetError($"Addressables catalog {catalog} returned an invalid operation handle.");
                        break;
                    }

                    if (!operation.IsDone)
                    {
                        continue;
                    }

                    if (operation.Status != AsyncOperationStatus.Succeeded)
                    {
                        AssetLoaders.ReportAssetError($"Couldn't load addressables catalog {catalog}: {operation.OperationException}");
                    }
                }
                catch (Exception exception)
                {
                    AssetLoaders.ReportAssetError($"Couldn't observe addressables catalog {catalog}.", exception);
                }

                break;
            }
        }
        finally
        {
            try
            {
                if (operation != null && operation.IsValid())
                {
                    Addressables.Release(operation);
                }
            }
            catch (Exception exception)
            {
                AssetLoaders.ReportAssetError($"Couldn't release the operation for addressables catalog {catalog}.", exception);
            }
        }
    }

    private static IEnumerator InitCoro()
    {
        yield return LoadAllModBundles();
        var loadCatalogsTask = LoadAllCatalogs();
        
        GameResources.Load();

        SceneManager.LoadScene(SonsSceneManager.OptionsMenuSceneName, LoadSceneMode.Additive);
        yield return null;
        SUI.SUI.InitPrefabs();
        SceneManager.UnloadScene(SonsSceneManager.OptionsMenuSceneName);
        
        yield return AddProgress(0.15f);

        yield return loadCatalogsTask;

        yield return WaitForServices(InitSystems());
        
        yield return AddProgress(0.15f);

        var subs = SdkEvents.OnSdkInitialized.BeginInvocation();
        for (var i = 0; i < subs.Length; i++)
        {
            SdkEvents.OnSdkInitialized.InvokeSubscriber(subs[i], callback => callback());
            
            yield return AddProgress(0.4f / subs.Length);
        }

        SdkEvents.OnSdkLateInitialized.Invoke();
        GlobalOverlays.ToggleOverlay(false);
    }

    private static Task InitSystems()
    {
        if (!LoaderEnvironment.IsDedicatedServer)
        {
            GlobalInput.Init();
            GenericModalDialog.Setup();
            TooltipProvider.Setup();
            SonsUiTools.Init();
        }

        SonsSaveTools.Init();
        GameManagers.Init();
        Packets.Init();
        EntityManager.Init();
        AssetImportingInitializer.Init();
        return CustomBlueprintManager.Init();
    }

    private static IEnumerator WaitForServices(Task services)
    {
        while (!services.IsCompleted)
            yield return null;

        services.GetAwaiter().GetResult();
    }

    private static IEnumerator LoadAllModBundles()
    {
        var bundles = SonsMod.RegisteredMods.SelectMany(x => x.AssetBundleAttrs).ToArray();

        for (var i = 0; i < bundles.Length; i++)
        {
            var bundle = bundles[i];
            try
            {
                if (bundle.Mod.Registered)
                    bundle.LoadBundle();
            }
            catch (Exception exception)
            {
                AssetLoaders.ReportAssetError($"Couldn't initialize asset bundle {bundle.AssetBundleName} for {bundle.Mod.ModAssembly.FullName}.", exception);
            }

            yield return AddProgress(0.3f / bundles.Length);
        }
    }

    private static bool _initializationStarted;
    private static bool _initializationFinished;
    private static bool _eventSetupCompleted;
}
