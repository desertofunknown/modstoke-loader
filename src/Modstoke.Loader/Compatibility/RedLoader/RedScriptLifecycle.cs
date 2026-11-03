// SPDX-License-Identifier: GPL-3.0-or-later
// Script lifecycle adapted from RedLoader.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;
using RedLoader;
using RedLoader.Scripting;
using RedLoader.Utils;
using UnityEngine;
using Color = System.Drawing.Color;

namespace Modstoke.Loader;

internal sealed class RedScriptLifecycle : IRedScriptLifecycle
{
    private static readonly RedScriptLifecycle Driver = new();

    private RedScriptLifecycle() { }

    internal static IRedScriptLifecycle Create() => Driver;

    private const float AUTO_RELOAD_DELAY = 1.0f;

    private static FileSystemWatcher _fileSystemWatcher;
    private static readonly object _queueLock = new();
    private static bool _initialized;
    private static bool _stopping;
    private static float _autoReloadTimer;
    private static readonly Dictionary<string, List<RedScript>> _loadedScripts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _scriptQueue = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _loadedHashes = new(StringComparer.OrdinalIgnoreCase);

    public void Init()
    {
        if (_initialized || _stopping || GlobalEvents.OnApplicationDefiniteQuit.Disposed)
            return;

        Directory.CreateDirectory(LoaderEnvironment.ScriptDirectory);
        try
        {
            StartFileSystemWatcher();
            GlobalEvents.OnApplicationDefiniteQuit.Subscribe(Shutdown);
            _initialized = true;
            ReloadPlugins();
        }
        catch
        {
            Shutdown();
            lock (_queueLock)
                _stopping = false;
            throw;
        }
    }

    public void Update()
    {
        if (!_initialized || _stopping)
            return;

        string[] pending;
        lock (_queueLock)
        {
            if (_scriptQueue.Count == 0)
                return;

            _autoReloadTimer -= Time.unscaledDeltaTime;
            if (_autoReloadTimer > 0)
                return;

            pending = _scriptQueue.ToArray();
            _scriptQueue.Clear();
        }

        foreach (var path in pending)
            LoadScript(path);
    }

    private static void ReloadPlugins()
    {
        var files = Directory.GetFiles(LoaderEnvironment.ScriptDirectory, "*.dll");
        foreach (string path in files)
            LoadScript(Path.GetFullPath(path));
    }

    private static void LoadScript(string path)
    {
        if (_stopping)
            return;

        // Loaded script assemblies are never unloaded, so skip writes that leave the file unchanged.
        var hash = TryHashFile(path);
        if (hash != null && _loadedScripts.ContainsKey(path)
            && _loadedHashes.TryGetValue(path, out var loadedHash) && loadedHash == hash)
        {
            ReportMessage(Color.DarkGray, $"{path} is unchanged; keeping the loaded scripts");
            return;
        }

        // Retire the old identity before invoking script code or scheduling replacements.
        if (_loadedScripts.Remove(path, out var previous))
            UnloadScripts(previous);
        _loadedHashes.Remove(path);

        if (_stopping || !File.Exists(path))
            return;

        var scripts = new List<RedScript>();
        _loadedScripts.Add(path, scripts);
        try
        {
            LoadDLL(path, scripts);
            if (hash != null)
                _loadedHashes[path] = hash;
        }
        catch (Exception error)
        {
            if (_loadedScripts.TryGetValue(path, out var current) && ReferenceEquals(current, scripts))
                _loadedScripts.Remove(path);
            UnloadScripts(scripts);
            ReportError("Failed to load scripts from " + path, error);
        }
    }

    private static string TryHashFile(string path)
    {
        try { return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void LoadDLL(string path, List<RedScript> scripts)
    {
        using var defaultResolver = new DefaultAssemblyResolver();
        defaultResolver.AddSearchDirectory(LoaderEnvironment.ScriptDirectory);
        defaultResolver.AddSearchDirectory(LoaderEnvironment.LoaderAssemblyDirectory);
        defaultResolver.AddSearchDirectory(LoaderEnvironment.Il2CppAssembliesDirectory);

        ReportMessage(Color.DarkGray, $"Loading plugins from {path}");

        using (var dll = AssemblyDefinition.ReadAssembly(path, new ReaderParameters {
                   AssemblyResolver = defaultResolver,
                   ReadSymbols = true,
                   SymbolReaderProvider = new DefaultSymbolReaderProvider(false)
               }))
        {
            dll.Name.Name = $"{dll.Name.Name}-{DateTime.Now.Ticks}";
            Assembly ass;
            
            using (var ms = new MemoryStream())
            {
                dll.Write(ms);
                ass = Assembly.Load(ms.ToArray());
            }
            
            foreach (Type type in GetTypesSafe(ass))
            {
                try
                {
                    if (!typeof(RedScript).IsAssignableFrom(type) || type.IsAbstract || type.ContainsGenericParameters)
                        continue;

                    ReportMessage(Color.DarkGray, $"Loading {type.Name}");
            
                    DelayAction(() =>
                    {
                        if (!IsCurrent(path, scripts))
                            return;

                        RedScript script = null;
                        try
                        {
                            script = (RedScript)Activator.CreateInstance(type);
                            if (script != null && IsCurrent(path, scripts))
                            {
                                scripts.Add(script);
                                script.OnLoad();
                                ReportMessage(Color.GreenYellow, "Loading Successful");
                            }
                            else if (script != null)
                            {
                                UnloadScript(script);
                            }
                        }
                        catch (Exception e)
                        {
                            if (script != null && scripts.Remove(script))
                                UnloadScript(script);
                            ReportError("Failed to load " + type.Name, e);
                        }
                    }).RunCoro();
                }
                catch (Exception e)
                {
                    ReportError("Failed to load plugin " + type.Name, e);
                }
            }
        }
    }

    private static void StartFileSystemWatcher()
    {
        var watcher = new FileSystemWatcher(LoaderEnvironment.ScriptDirectory)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            Filter = "*.dll"
        };
        watcher.Changed += FileChangedEventHandler;
        watcher.Deleted += FileChangedEventHandler;
        watcher.Created += FileChangedEventHandler;
        watcher.Renamed += FileChangedEventHandler;
        lock (_queueLock)
            _fileSystemWatcher = watcher;
        watcher.EnableRaisingEvents = true;
    }

    private static void FileChangedEventHandler(object sender, FileSystemEventArgs args)
    {
        lock (_queueLock)
        {
            if (_stopping || !ReferenceEquals(sender, _fileSystemWatcher))
                return;

            if (args is RenamedEventArgs renamed && IsScriptPath(renamed.OldFullPath))
                _scriptQueue.Add(renamed.OldFullPath);
            if (IsScriptPath(args.FullPath))
                _scriptQueue.Add(args.FullPath);

            // Every write extends the debounce, including writes to an already queued DLL.
            _autoReloadTimer = AUTO_RELOAD_DELAY;
        }
    }

    private static bool IsScriptPath(string path) =>
        string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase);

    private static bool IsCurrent(string path, List<RedScript> scripts)
    {
        lock (_queueLock)
            return !_stopping && !_scriptQueue.Contains(path)
                && _loadedScripts.TryGetValue(path, out var current) && ReferenceEquals(current, scripts);
    }

    private static void Shutdown()
    {
        FileSystemWatcher watcher;
        lock (_queueLock)
        {
            _stopping = true;
            _initialized = false;
            watcher = _fileSystemWatcher;
            _fileSystemWatcher = null;
            _scriptQueue.Clear();
        }

        try { watcher?.Dispose(); }
        catch (Exception error) { ReportError("Failed to dispose the script watcher", error); }
        GlobalEvents.OnApplicationDefiniteQuit.Unsubscribe(Shutdown);

        var loaded = _loadedScripts.Values.ToArray();
        _loadedScripts.Clear();
        _loadedHashes.Clear();
        foreach (var scripts in loaded)
            UnloadScripts(scripts);
    }

    private static void UnloadScripts(List<RedScript> scripts)
    {
        var snapshot = scripts.ToArray();
        scripts.Clear();
        foreach (var script in snapshot)
            UnloadScript(script);
    }

    private static void UnloadScript(RedScript script)
    {
        try { script.OnUnload(); }
        catch (Exception error) { ReportError("Failed to unload " + script.GetType().Name, error); }
    }

    private static IEnumerable<Type> GetTypesSafe(Assembly ass)
    {
        try
        {
            return ass.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (var l in ex.LoaderExceptions)
                ReportError("Script type discovery failed", l);
            return ex.Types.Where(x => x != null);
        }
    }

    private static void ReportError(string message, Exception error)
    {
        try { RLog.Error(message + ": " + error); }
        catch { /* Diagnostics must not stop other script lifetimes. */ }
    }

    private static void ReportMessage(Color color, string message)
    {
        try { RLog.Msg(color, message); }
        catch { /* Diagnostics do not change script activation outcomes. */ }
    }

    private static IEnumerator DelayAction(Action action)
    {
        yield return null;
        action();
    }
}
