// SPDX-License-Identifier: LGPL-2.1-only
// RedLoader/MelonLoader mod contracts with Modstoke registration forwarding.
// Copyright (c) RedLoader and MelonLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

#pragma warning disable 0618

namespace RedLoader
{
    public abstract class ModBase : IModRegistrationCallbacks
    {
        #region Static

        /// <summary>
        /// Called once a Melon is fully registered.
        /// </summary>
        public static readonly MelonEvent<ModBase> OnMelonRegistered = new();

        /// <summary>
        /// Called when a Melon unregisters.
        /// </summary>
        public static readonly MelonEvent<ModBase> OnMelonUnregistered = new();

        /// <summary>
        /// Called before a Melon starts initializing.
        /// </summary>
        public static readonly MelonEvent<ModBase> OnMelonInitializing = new();

        public static ReadOnlyCollection<ModBase> RegisteredMelons => _registeredMelons.AsReadOnly();
        internal static List<ModBase> _registeredMelons = new();

        #endregion

        #region Instance

        public readonly MelonEvent OnRegister = new();
        public readonly MelonEvent OnUnregister = new();

        /// <summary>
        /// MelonAssembly of the Melon.
        /// </summary>
        public Assembly ModAssembly { get; internal set; }

        /// <summary>
        /// Priority of the Melon.
        /// </summary>
        public int Priority { get; internal set; }

        /// <summary>
        /// Console Color of the Melon.
        /// </summary>
        public Color ConsoleColor { get; internal set; }

        /// <summary>
        /// Console Color of the Author that made this melon.
        /// </summary>
        public Color AuthorConsoleColor { get; internal set; }

        /// <summary>
        /// Info Attribute of the Melon.
        /// </summary>
        internal MelonInfoAttribute Info { get; set; }

        /// <summary>
        /// Game Version Attributes of the Melon.
        /// </summary>
        public string SupportedGameVersion { get; internal set; }

        /// <summary>
        /// Optional Dependencies Attribute of the Melon.
        /// </summary>
        public string[] OptionalDependencies { get; internal set; }

        /// <summary>
        /// Auto-Created Harmony Instance of the Melon.
        /// </summary>
        public HarmonyLib.Harmony HarmonyInstance { get; internal set; }

        /// <summary>
        /// Auto-Created MelonLogger Instance of the Melon.
        /// </summary>
        public RLog.Instance LoggerInstance { get; internal set; }

        /// <summary>
        /// Optional ID of the Melon.
        /// </summary>
        public string ID { get; internal set; }
        
        /// <summary>
        /// Description of the Mod.
        /// </summary>
        public string Description { get; internal set; }

        /// <summary>
        /// <see langword="true"/> if the Melon is registered.
        /// </summary>
        public bool Registered { get; private set; }

        private IModRegistrationDriver _registration;
        private IModRegistrationDriver Registration =>
            LazyInitializer.EnsureInitialized(ref _registration, () => ModRegistrationDriverFactory.Create(this));

        /// <summary>
        /// Method that gets called on update (every frame). See <see href="https://docs.unity3d.com/ScriptReference/MonoBehaviour.Update.html">Update</see>
        /// </summary>
        protected LemonAction OnUpdateCallback;
        
        /// <summary>
        /// Method that gets called on late update (every frame). See <see href="https://docs.unity3d.com/ScriptReference/MonoBehaviour.LateUpdate.html">LateUpdate</see>
        /// </summary>
        protected LemonAction OnLateUpdateCallback;
        
        /// <summary>
        /// Method that gets called on fixed update. See <see href="https://docs.unity3d.com/ScriptReference/MonoBehaviour.FixedUpdate.html">FixedUpdate</see>
        /// </summary>
        protected LemonAction OnFixedUpdateCallback;
        
        /// <summary>
        /// Method that gets called on GUI update. See <see href="https://docs.unity3d.com/ScriptReference/MonoBehaviour.OnGUI.html">OnGUI</see>
        /// </summary>
        protected LemonAction OnGUICallback;

        /// <summary>
        /// If true the loader will automatically apply all harmony patches in the assembly.
        /// </summary>
        protected bool HarmonyPatchAll;

        /// <summary>
        /// Name of the current Melon Type.
        /// </summary>
        public abstract string MelonTypeName { get; }

        #region Callbacks

        /// <summary>
        /// Runs before Support Module Initialization and after Assembly Generation for Il2Cpp Games.
        /// </summary>
        protected virtual void OnPreSupportModule() { }

        /// <summary>
        /// Runs on a quit request. It is possible to abort the request in this callback.
        /// </summary>
        protected virtual void OnApplicationQuit() { }

        /// <summary>
        /// Runs when Melon Preferences get saved.
        /// </summary>
        protected virtual void OnPreferencesSaved() { }

        /// <summary>
        /// Runs when Melon Preferences get saved. Gets passed the Preferences's File Path.
        /// </summary>
        protected virtual void OnPreferencesSaved(string filepath) { }

        /// <summary>
        /// Runs when Melon Preferences get loaded.
        /// </summary>
        protected virtual void OnPreferencesLoaded() { }

        /// <summary>
        /// Runs when Melon Preferences get loaded. Gets passed the Preferences's File Path.
        /// </summary>
        protected virtual void OnPreferencesLoaded(string filepath) { }

        /// <summary>
        /// Runs when the Melon is registered. Executed before the Melon's info is printed to the console. This callback should only be used a constructor for the Melon.
        /// </summary>
        /// <remarks>
        /// Please note that this callback may run before the Support Module is loaded.
        /// <br>As a result, using unhollowed assemblies may not be possible yet and you would have to override <see cref="OnInitializeMod"/> instead.</br>
        /// </remarks>
        protected virtual void OnEarlyInitializeMelon() { }

        /// <summary>
        /// Runs after the Mod has registered. This callback waits until the loader has fully initialized (<see cref="GlobalEvents.OnApplicationStart"/>).
        /// </summary>
        protected virtual void OnInitializeMod() { }

        /// <summary>
        /// Runs after <see cref="OnInitializeMod"/>. This callback waits until Unity has invoked the first 'Start' messages (<see cref="GlobalEvents.OnApplicationLateStart"/>).
        /// </summary>
        protected virtual void OnLateInitializeMod() { }

        /// <summary>
        /// Runs when the mod is unregistered. Also runs before the Application is closed (<see cref="GlobalEvents.OnApplicationDefiniteQuit"/>).
        /// </summary>
        protected virtual void OnDeinitializeMod() { }

        #endregion

        public static void PrintIncompatibilities(Incompatibility[] incompatibilities, ModBase mod)
        {
            if (incompatibilities == null || incompatibilities.Length == 0)
                return;

            RLog.WriteLine(Color.Red);
            RLog.MsgDirect(Color.DarkRed, $"'{mod.Info.Name} v{mod.Info.Version}' is incompatible:");
            if (incompatibilities.Contains(Incompatibility.GameVersion))
            {
                RLog.MsgDirect($"- {mod.Info.Name} is only compatible with the following Game Version:");

                RLog.MsgDirect($"    - {mod.SupportedGameVersion}");
            }
            // TODO: Describe incompatibilities other than GameVersion.

            RLog.WriteLine(Color.Red);
            RLog.WriteSpacer();
        }

        /// <summary>
        /// Registers the Melon.
        /// </summary>
        public bool Register() => Registration.Register();

        /// <summary>
        /// Registers a loader callback for the lifetime of this registration.
        /// </summary>
        private protected void RegisterCallback<T>(MelonEventBase<T> evt, T callback, bool once = false) where T : Delegate
        {
            var registration = _registration;
            var callbacks = registration?.Callbacks;
            if (callbacks == null || !Registered)
                return;

            // Eligibility follows this registration, not a later reuse of the mod instance.
            evt.Subscribe(callback, Priority, once, callbacks, () => registration.IsActive(callbacks));
        }

        private void HarmonyInit()
        {
            if (HarmonyPatchAll)
                HarmonyInstance.PatchAll(ModAssembly);
        }

        void IModRegistrationCallbacks.SetRegistered(bool registered) => Registered = registered;
        void IModRegistrationCallbacks.RegisterCallbacks() => RegisterCallbacks();
        void IModRegistrationCallbacks.EarlyInitialize() => OnEarlyInitializeMelon();
        bool IModRegistrationCallbacks.RegisterInternal() => RegisterInternal();
        void IModRegistrationCallbacks.UnregisterInternal() => UnregisterInternal();
        void IModRegistrationCallbacks.Initialize() => OnInitializeMod();
        void IModRegistrationCallbacks.LateInitialize() => OnLateInitializeMod();
        void IModRegistrationCallbacks.Deinitialize() => OnDeinitializeMod();
        void IModRegistrationCallbacks.PrintLoadInfo() => PrintLoadInfo();
        void IModRegistrationCallbacks.PrintUnloadInfo(string reason) => PrintUnloadInfo(reason);
        void IModRegistrationCallbacks.SubscribeInitialization() =>
            RegisterCallback(GlobalEvents.OnApplicationStart, LoaderInitialized, true);
        void IModRegistrationCallbacks.SubscribeLateInitialization() =>
            RegisterCallback(GlobalEvents.OnApplicationLateStart, OnLateInitializeMod, true);

        private void LoaderInitialized() => Registration.InitializeMod();

        protected private virtual bool RegisterInternal()
        {
            return true;
        }

        protected private virtual void UnregisterInternal() { }

        protected private virtual void RegisterCallbacks()
        {
            RegisterCallback(GlobalEvents.OnApplicationQuit, OnApplicationQuit);
            
            if (OnUpdateCallback != null)
                RegisterCallback(GlobalEvents.OnUpdate, OnUpdateCallback);
            
            if (OnLateUpdateCallback != null)
                RegisterCallback(GlobalEvents.OnLateUpdate, OnLateUpdateCallback);
            
            if (OnFixedUpdateCallback != null)
                RegisterCallback(GlobalEvents.OnFixedUpdate, OnFixedUpdateCallback);
            
            if (OnGUICallback != null)
                RegisterCallback(GlobalEvents.OnGUI, OnGUICallback);

            RegisterCallback(ConfigSystem.OnPreferencesLoaded, PrefsLoaded);
            RegisterCallback(ConfigSystem.OnPreferencesSaved, PrefsSaved);
        }

        private void PrefsSaved(string path)
        {
            OnPreferencesSaved(path);
            OnPreferencesSaved();
        }

        private void PrefsLoaded(string path)
        {
            OnPreferencesLoaded(path);
            OnPreferencesLoaded();
        }

        /// <summary>
        /// Tries to find a registered Melon that matches the given Info.
        /// </summary>
        public static ModBase FindMelon(string melonName, string melonAuthor)
        {
            return ModRegistrationDriverFactory.Find(melonName, melonAuthor);
        }

        /// <summary>
        /// Unregisters the Melon and all other Melons located in the same Assembly.
        /// <para>Removes loader-owned callbacks and attempts to remove each mod's Harmony patches, but does not unload the Assembly.</para>
        /// <para>Mods must release their own subscriptions, commands and resources in <see cref="OnDeinitializeMod"/>. An executing callback is not interrupted.</para>
        /// </summary>
        public void Unregister(string reason = null, bool silent = false)
        {
            if (Registered)
                Registration.Unregister(reason, silent);
        }

        internal void UnregisterInstance(string reason, bool silent)
        {
            if (Registered)
                Registration.UnregisterInstance(reason, silent);
        }

        private void PrintLoadInfo()
        {
            RLog.WriteLine(Color.DarkGreen);
            
            RLog.Internal_PrintModName(ConsoleColor, AuthorConsoleColor, Info.Name, Info.Author, "", Info.Version, ID);
            RLog.MsgDirect(Color.DarkGray, $"Assembly: {Path.GetFileName(ModAssembly.Location)}");
        
            RLog.WriteLine(Color.DarkGreen);
        }
        
        private void PrintUnloadInfo(string reason)
        {
            RLog.WriteLine(Color.DarkRed);
        
            RLog.MsgDirect(Color.DarkGray, MelonTypeName + " deinitialized:");
            RLog.Internal_PrintModName(ConsoleColor, AuthorConsoleColor, Info.Name, Info.Author, "", Info.Version, ID);
        
            if (!string.IsNullOrEmpty(reason))
            {
                RLog.MsgDirect(string.Empty);
                RLog.MsgDirect($"Reason: '{reason}'");
            }
        
            RLog.WriteLine(Color.DarkRed);
        }

        public static void ExecuteAll(LemonAction<ModBase> func, bool unregisterOnFail = false, string unregistrationReason = null)
        {
            ExecuteList(func, _registeredMelons, unregisterOnFail, unregistrationReason);
        }

        public static void ExecuteList<T>(LemonAction<T> func, List<T> melons, bool unregisterOnFail = false, string unregistrationReason = null) where T : ModBase
        {
            ModExecutionDriver.ExecuteList(func, melons, unregisterOnFail, unregistrationReason);
        }

        public static void SendMessageAll(string name, params object[] arguments)
        {
            ModExecutionDriver.SendMessageAll(name, arguments);
        }
        
        public object SendMessage(string name, params object[] arguments)
        {
            return ModExecutionDriver.SendMessage(this, name, arguments);
        }
        #endregion

        public enum Incompatibility
        {
            MLVersion,
            MLBuild,
            Game,
            GameVersion,
            ProcessName,
            Domain,
            Platform
        }
    }
}
