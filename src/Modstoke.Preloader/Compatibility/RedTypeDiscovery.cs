// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility discovery and cache behavior adapted from RedLoader's and BepInEx's TypeLoader.cs.
// Copyright (c) RedLoader and BepInEx contributors. Copyright (C) 2026 Alex Cooper.

extern alias RedCompatibility;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using RedCompatibility::RedLoader.Bootstrap;
using RedICacheable = RedCompatibility::RedLoader.Bootstrap.ICacheable;
using Utility = RedCompatibility::RedLoader.Utility;
using RLog = RedCompatibility::RedLoader.RLog;
using LoaderEnvironment = RedCompatibility::RedLoader.Utils.LoaderEnvironment;

namespace Modstoke.Preloader;

internal static class RedTypeDiscovery
{
    internal static Dictionary<string, List<T>> Find<T>(string directory,
        Func<TypeDefinition, string, T> selectType, Func<AssemblyDefinition, bool> acceptAssembly,
        string cacheName, ReaderParameters readerParameters, Func<bool> cacheEnabled)
        where T : RedICacheable, new()
    {
        var matchesByPath = new Dictionary<string, List<T>>();
        var hashesByPath = new Dictionary<string, string>();
        var useCache = cacheName != null && cacheEnabled();
        var cache = useCache ? LoadCache<T>(cacheName, () => true) : null;

        // Keep directory enumeration outside per-assembly recovery, as in the legacy API.
        foreach (var path in Directory.GetFiles(Path.GetFullPath(directory), "*.dll", SearchOption.AllDirectories))
        {
            try
            {
                using var image = new MemoryStream(File.ReadAllBytes(path));
                var hash = useCache ? Utility.HashStream(image) : null;
                if (useCache)
                {
                    hashesByPath[path] = hash;
                    image.Position = 0;
                }
                if (cache != null && cache.TryGetValue(path, out var cached) && hash == cached.Hash)
                {
                    matchesByPath[path] = cached.CacheItems;
                    continue;
                }

                using var assembly = AssemblyDefinition.ReadAssembly(image, readerParameters);
                LogDebug(() => $"Examining '{path}'");
                if (acceptAssembly != null && !acceptAssembly(assembly))
                {
                    matchesByPath[path] = new List<T>();
                    continue;
                }

                var matches = new List<T>();
                matchesByPath[path] = matches;
                foreach (var type in assembly.MainModule.Types)
                {
                    try
                    {
                        var item = selectType(type, path);
                        if (item != null)
                            matches.Add(item);
                    }
                    catch (Exception error)
                    {
                        // Keep valid siblings, but retry incomplete discovery on the next scan.
                        hashesByPath.Remove(path);
                        Log(RLog.Error, () => $"Failed to examine type '{type.FullName}' in '{path}': {error}");
                    }
                }
            }
            catch (BadImageFormatException error)
            {
                hashesByPath.Remove(path);
                LogDebug(
                    () => $"Skipping loading {path} because it's not a valid .NET assembly. Full error: {error.Message}");
            }
            catch (Exception error)
            {
                hashesByPath.Remove(path);
                Log(RLog.Error, () => error.ToString());
            }
        }

        if (useCache)
            SaveCache(cacheName, matchesByPath, hashesByPath, cacheEnabled);
        return matchesByPath;
    }

    internal static Dictionary<string, CachedAssembly<T>> LoadCache<T>(string cacheName, Func<bool> cacheEnabled)
        where T : RedICacheable, new()
    {
        if (!cacheEnabled())
            return null;

        var entries = new Dictionary<string, CachedAssembly<T>>();
        try
        {
            var path = Path.Combine(LoaderEnvironment.CachePath, $"{cacheName}_typeloader.dat");
            if (!File.Exists(path))
                return null;

            using var reader = new BinaryReader(File.OpenRead(path));
            var count = reader.ReadInt32();
            if (count < 0)
                throw new InvalidDataException("Negative assembly count in type cache.");
            for (var i = 0; i < count; i++)
            {
                var key = reader.ReadString();
                var hash = reader.ReadString();
                var itemCount = reader.ReadInt32();
                if (itemCount < 0)
                    throw new InvalidDataException("Negative item count in type cache.");
                var items = new List<T>();
                for (var j = 0; j < itemCount; j++)
                {
                    var item = new T();
                    item.Load(reader);
                    items.Add(item);
                }
                entries[key] = new CachedAssembly<T> { Hash = hash, CacheItems = items };
            }
        }
        catch (Exception error)
        {
            Log(RLog.Warning,
                () => $"Failed to load cache \"{cacheName}\"; skipping loading cache. Reason: {error.Message}.");
        }

        // A failed read retains complete entries read before the failure.
        return entries;
    }

    internal static void SaveCache<T>(string cacheName, Dictionary<string, List<T>> entries,
        Dictionary<string, string> hashes, Func<bool> cacheEnabled) where T : RedICacheable
    {
        if (!cacheEnabled())
            return;

        string temporaryPath = null;
        try
        {
            if (!Directory.Exists(LoaderEnvironment.CachePath))
                Directory.CreateDirectory(LoaderEnvironment.CachePath);

            var path = Path.Combine(LoaderEnvironment.CachePath, $"{cacheName}_typeloader.dat");
            var candidatePath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(candidatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                temporaryPath = candidatePath;
                using var writer = new BinaryWriter(stream);
                writer.Write(entries.Count);
                foreach (var entry in entries)
                {
                    writer.Write(entry.Key);
                    writer.Write(hashes.TryGetValue(entry.Key, out var hash) ? hash : "");
                    writer.Write(entry.Value.Count);
                    foreach (var item in entry.Value)
                        item.Save(writer);
                }
            }
            File.Move(temporaryPath, path, true);
            temporaryPath = null;
        }
        catch (Exception error)
        {
            Log(RLog.Warning,
                () => $"Failed to save cache \"{cacheName}\"; skipping saving cache. Reason: {error.Message}.");
        }
        finally
        {
            if (temporaryPath != null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception error)
                {
                    Log(RLog.Warning, () => $"Failed to remove incomplete type cache '{temporaryPath}': {error.Message}");
                }
            }
        }
    }

    internal static AssemblyDefinition ResolveFailure(object sender, AssemblyNameReference reference,
        ReaderParameters readerParameters, IEnumerable<string> additionalDirectories,
        Func<object, AssemblyNameReference, AssemblyDefinition> fallback)
    {
        if (!Utility.TryParseAssemblyName(reference.FullName, out var name))
            return null;

        var directories = new[]
        {
            LoaderEnvironment.LoaderAssemblyDirectory,
            LoaderEnvironment.ModsDirectory,
            LoaderEnvironment.PatcherPluginPath,
            LoaderEnvironment.LibsDirectory
        }.Concat(additionalDirectories);
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                LogDebug(() => $"Unable to resolve cecil search directory '{directory}'");
                continue;
            }

            if (Utility.TryResolveDllAssembly(name, directory, readerParameters, out var assembly))
                return assembly;
        }

        return fallback(sender, reference);
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void LogDebug(Func<string> message)
    {
        try { RLog.Debug(message()); }
        catch { }
    }

    private static void Log(Action<string> write, Func<string> message)
    {
        try { write(message()); }
        catch { }
    }
}
