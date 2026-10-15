using System.Reflection;
using HarmonyLib;
using RedLoader;
using MonoMod.Utils;
using RedLoader.Utils;
using SonsSdk.Attributes;
using SUI;
using TheForest;
using UnityEngine;
using Color = System.Drawing.Color;

namespace SonsSdk;

public abstract class SonsMod : ModTypeBase<SonsMod>
{
    public ManifestData Manifest { get; internal set; }

    internal List<AssetBundleAttribute> AssetBundleAttrs = new();
    internal Func<SonsMod, System.Collections.IEnumerator> CatalogLoader;

    /// <summary>
    /// Method that gets called on update but only when in the world.
    /// </summary>
    [Obsolete("Use the IOnUpdateReceiver interface instead")]
    protected LemonAction OnWorldUpdatedCallback;
    
    /// <summary>
    /// Gets called after all commands have been registered.
    /// </summary>
    protected LemonAction OnCommandsRegisteredCallback;
    
    public PathObject DataPath { get; internal set; }

    static SonsMod()
    {
        TypeName = "SonsMod";
    }

    private void HarmonyInit()
    {
        if (HarmonyPatchAll)
        {
            RLog.DebugBig($"HarmonyPatchAll is true for [{ModAssembly.FullName}], patching assembly");
            if (Registered)
                HarmonyInstance.PatchAll(ModAssembly);
        }
    }

    private protected override bool RegisterInternal()
    {
        DataPath = LoaderEnvironment.GetModDataPath(this);
        
        try
        {
            OnPreSupportModule();
        }
        catch (Exception ex)
        {
            RLog.Error(
                $"Failed to register {MelonTypeName} '{ModAssembly.Location}': Melon failed to initialize in the deprecated OnPreSupportModule callback!");
            RLog.Error(ex.ToString());
            return false;
        }

        if (!Registered || !base.RegisterInternal())
        {
            return false;
        }

        if (GlobalEvents.MelonHarmonyInit.Disposed)
        {
            HarmonyInit();
        }
        else
        {
            RegisterCallback(GlobalEvents.MelonHarmonyInit, HarmonyInit, true);
        }

        return Registered;
    }

    private protected override void RegisterCallbacks()
    {
        base.RegisterCallbacks();
        
        RegisterCallback(GlobalEvents.OnSceneWasLoaded, OnSceneWasLoaded);
        RegisterCallback(GlobalEvents.OnSceneWasInitialized, OnSceneWasInitialized);
        RegisterCallback(GlobalEvents.OnSceneWasUnloaded, OnSceneWasUnloaded);
        RegisterCallback(SdkEvents.OnGameStart, OnGameStart);
        RegisterCallback(SdkEvents.OnSdkInitialized, OnSdkInitialized);
        RegisterCallback(SdkEvents.OnSonsSceneInitialized, OnSonsSceneInitialized);

        if(OnWorldUpdatedCallback != null)
            RegisterCallback(SdkEvents.OnInWorldUpdate, OnWorldUpdatedCallback);
        
        if (this is IOnUpdateReceiver onUpdateReceiver)
            RegisterCallback(GlobalEvents.OnUpdate, onUpdateReceiver.OnUpdate);
        
        if (this is IOnInWorldUpdateReceiver onInWorldUpdateReceiver)
            RegisterCallback(SdkEvents.OnInWorldUpdate, onInWorldUpdateReceiver.OnInWorldUpdate);
        
        if (this is IOnAfterSpawnReceiver onAfterSpawnReceiver)
            RegisterCallback(SdkEvents.OnAfterSpawn, onAfterSpawnReceiver.OnAfterSpawn);
        
        if (this is IOnGameActivatedReceiver onGameActivatedReceiver)
            RegisterCallback(SdkEvents.OnGameActivated, onGameActivatedReceiver.OnGameActivated);
        
        if (this is IOnBeforeLoadSaveReceiver onBeforeLoadSaveReceiver)
            RegisterCallback(SdkEvents.BeforeLoadSave, onBeforeLoadSaveReceiver.OnBeforeLoadSave);
        
        if (this is IOnAfterLoadSaveReceiver onAfterLoadSaveReceiver)
            RegisterCallback(SdkEvents.AfterLoadSave, onAfterLoadSaveReceiver.OnAfterLoadSave);

        RegisterCallback(SdkEvents.OnGameStart, RegisterCommands);
    }
    
    /// <summary>
    /// Returns newly bound callbacks for this mod instance that accept the real SUI button context.
    /// </summary>
    public Dictionary<string, Action<SBgButtonOptions>> GetModPanelActions()
    {
        var actions = new Dictionary<string, Action<SBgButtonOptions>>();
        var methods = GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (var method in methods)
        {
            try
            {
                var attr = method.GetCustomAttribute<ModPanelActionAttribute>();
                if (attr == null)
                    continue;

                var action = (Action<SBgButtonOptions>) Delegate.CreateDelegate(typeof(Action<SBgButtonOptions>), this, method);
                actions[attr.Name] = action;
            }
            catch (Exception ex)
            {
                try
                {
                    RLog.Error($"Failed to bind mod panel action '{method.DeclaringType?.FullName}.{method.Name}' for mod '{GetType().FullName}': {ex}");
                }
                catch
                {
                    // Logging must not prevent the remaining actions from binding.
                }
            }
        }

        return actions;
    }

    #region Callbacks

    /// <summary>
    ///     Runs when a new Scene is loaded.
    /// </summary>
    public virtual void OnSceneWasLoaded(int buildIndex, string sceneName)
    { }

    /// <summary>
    ///     Runs once a Scene is initialized.
    /// </summary>
    public virtual void OnSceneWasInitialized(int buildIndex, string sceneName)
    { }

    /// <summary>
    ///     Runs once a Scene unloads.
    /// </summary>
    public virtual void OnSceneWasUnloaded(int buildIndex, string sceneName)
    { }

    /// <summary>
    /// Runs when the game scene is loaded (right before the player gains control).
    /// </summary>
    protected virtual void OnGameStart()
    { }
    
    /// <summary>
    /// Runs when the SDK is fully initialized. SDK usage like creation of custom UI should be done here.
    /// </summary>
    protected virtual void OnSdkInitialized()
    { }
    
    /// <summary>
    /// Runs when a scene is initialized. But with an enum parameter to check for sons scenes.
    /// </summary>
    /// <param name="sonsScene"></param>
    protected virtual void OnSonsSceneInitialized(ESonsScene sonsScene)
    {}

    #endregion

    #region Common Utils

    protected void Log(object msg, Color? color = null)
    {
        if(color.HasValue)
            LoggerInstance.Msg(color.Value, msg);
        else
            LoggerInstance.Msg(msg);
    }

    protected void RegisterCommand(string command, Func<string, bool> callback)
    {
        GameCommands.RegisterCommand(command, callback);
    }

    protected void StartPropDebugging()
    {
        MemberDebugger.StartDebugging(this);
    }

    protected void StopDebugging()
    {
        MemberDebugger.StopDebugging(this);
    }

    private void RegisterCommands()
    {
        GameCommands.RegisterFromType(GetType(), this);
        OnCommandsRegisteredCallback?.Invoke();
    }

    protected void Log(object obj) => LoggerInstance.Msg(obj);
    protected void Log(Color color, object obj) => LoggerInstance.Msg(color, obj);

    protected void MapResourceBundle(string bundleName, Type type)
    {
        AssetLoaders.MapBundleToFile(AssetLoaders.LoadDataFromAssembly(ModAssembly, $"Resources.{bundleName}"), type);
    }

    #endregion

    #region ModReports

    internal static List<ModReportInfo> ModReports = new();
    
    internal static void ReportMod(string modId, string message)
    {
        ModReports.Add(new ModReportInfo()
        {
            ModId = modId,
            Message = message
        });
    }

    internal class ModReportInfo
    {
        public string ModId;
        public string Message;
    }

    #endregion
    
}
