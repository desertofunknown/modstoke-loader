// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace OpenACAI.Endnight.Loader
{
    // Exposes the legacy data and event contract without upstream discovery or native hooks.
    internal sealed class OpenACAIBepInExRegistry : IL2CPPChainloader
    {
        private readonly Action<string> _addStartupWarning;
        private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

        internal OpenACAIBepInExRegistry(Action<string> addStartupWarning)
        {
            _addStartupWarning = addStartupWarning;
        }

        internal PluginInfo BeginCorePlugin()
        {
            var type = typeof(Plugin);
            var info = new PluginInfo
            {
                Metadata = MetadataHelper.GetMetadata(type),
                Processes = MetadataHelper.GetAttributes<BepInProcess>(type),
                Dependencies = MetadataHelper.GetDependencies(type),
                Incompatibilities = MetadataHelper.GetAttributes<BepInIncompatibility>(type),
                Location = type.Assembly.Location,
                TypeName = type.FullName,
                TargettedBepInExVersion = typeof(BepInPlugin).Assembly.GetName().Version
            };
            BeginPlugin(info);
            return info;
        }

        internal void BeginPlugin(PluginInfo info) => Plugins.Add(info.Metadata.GUID, info);

        internal void BeforeLoad(PluginInfo info, Assembly assembly, BasePlugin plugin) =>
            OnPluginLoad(info, assembly, plugin);

        internal void WithdrawPlugin(PluginInfo info)
        {
            if (info != null && Plugins.TryGetValue(info.Metadata.GUID, out var current)
                && ReferenceEquals(current, info))
                Plugins.Remove(info.Metadata.GUID);
        }

        internal void NotifyPluginLoaded(PluginInfo info) =>
            Notify(() => OnPluginLoaded(info), "PluginLoaded (" + info.Metadata.GUID + ")");

        internal void NotifyFinished() => Notify(OnFinished, "Finished");

        private void Notify(Action notification, string eventName)
        {
            try
            {
                notification();
            }
            catch (Exception ex)
            {
                // Post-load observers cannot undo completed plugin or Core startup.
                try
                {
                    _addStartupWarning?.Invoke("BepInEx " + eventName + " listener failed: " + ex);
                }
                catch
                {
                    // Warning sinks are observers too.
                }
            }
        }

        public override void Initialize(string gameExePath = null) => throw OwnedStartupException();
        public override void Execute() => throw OwnedStartupException();
        public override IList<PluginInfo> LoadPlugins(params string[] pluginsPaths)
        {
            RequireOwnerThread();
            return OpenACAIBepInExPluginHost.LoadDynamicPlugins(pluginsPaths, _addStartupWarning);
        }

        public override BasePlugin LoadPlugin(PluginInfo pluginInfo, Assembly pluginAssembly)
        {
            RequireOwnerThread();
            // Like upstream, this low-level API does not own registration or PluginInfo.Instance.
            return OpenACAIBepInExPluginHost.LoadPlugin(pluginInfo, pluginAssembly, this);
        }

        private void RequireOwnerThread()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                throw new InvalidOperationException("BepInEx plugin loading must run on the OpenACAI startup/Unity thread.");
            if (!ReferenceEquals(Instance, this) || !OpenACAIEndnightRuntime.PluginLoadingAvailable)
                throw new InvalidOperationException("The OpenACAI plugin-loading runtime is not active.");
        }

        private static InvalidOperationException OwnedStartupException() => new InvalidOperationException(
            "OpenACAI owns Core startup. Use LoadPlugins for additional plugin directories; "
            + "reinitializing or executing a second chainloader is not supported.");
    }
}
