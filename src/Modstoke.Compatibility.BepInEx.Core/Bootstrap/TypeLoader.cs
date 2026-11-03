using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using Mono.Cecil;

namespace BepInEx.Bootstrap;

/// <summary>
///     A cacheable metadata item. Can be used with <see cref="TypeLoader.LoadAssemblyCache{T}" /> and
///     <see cref="TypeLoader.SaveAssemblyCache{T}" /> to cache plugin metadata.
/// </summary>
public interface ICacheable
{
    /// <summary>
    ///     Serialize the object into a binary format.
    /// </summary>
    /// <param name="bw"></param>
    void Save(BinaryWriter bw);

    /// <summary>
    ///     Loads the object from binary format.
    /// </summary>
    /// <param name="br"></param>
    void Load(BinaryReader br);
}

/// <summary>
///     A cached assembly.
/// </summary>
/// <typeparam name="T"></typeparam>
public class CachedAssembly<T> where T : ICacheable
{
    /// <summary>
    ///     List of cached items inside the assembly.
    /// </summary>
    public List<T> CacheItems { get; set; }

    /// <summary>
    ///     Hash of the assembly. Used to verify that the assembly hasn't been changed.
    /// </summary>
    public string Hash { get; set; }
}

/// <summary>
///     Provides methods for loading specified types from an assembly.
/// </summary>
public static class TypeLoader
{
    /// <summary>
    ///     Default assembly resolved used by the <see cref="TypeLoader" />
    /// </summary>
    public static readonly DefaultAssemblyResolver CecilResolver;

    /// <summary>
    ///     Default reader parameters used by <see cref="TypeLoader" />
    /// </summary>
    public static readonly ReaderParameters ReaderParameters;

    public static HashSet<string> SearchDirectories = new();

    private delegate Dictionary<string, List<T>> FindTypes<T>(string directory,
        Func<TypeDefinition, string, T> selector, Func<AssemblyDefinition, bool> filter,
        string cacheName, ReaderParameters readerParameters, Func<bool> cacheEnabled) where T : ICacheable, new();

    private delegate Dictionary<string, CachedAssembly<T>> ReadCache<T>(string cacheName,
        Func<bool> cacheEnabled) where T : ICacheable, new();

    private delegate void WriteCache<T>(string cacheName, Dictionary<string, List<T>> entries,
        Dictionary<string, string> hashes, Func<bool> cacheEnabled) where T : ICacheable;

    private delegate AssemblyDefinition ResolveFailure(object sender, AssemblyNameReference reference,
        ReaderParameters readerParameters, IEnumerable<string> searchDirectories,
        Func<object, AssemblyNameReference, AssemblyDefinition> fallback);

    // Binding stays lazy so the public resolver fields are initialized before owned code is reached.
    private static readonly Lazy<ResolveFailure> ResolveFailureHandler = new(() => Bind<ResolveFailure>("ResolveFailure"));

    private static class ReaderBindings<T> where T : ICacheable, new()
    {
        internal static readonly Lazy<FindTypes<T>> Find = new(() => Bind<FindTypes<T>>("Find", typeof(T)));
        internal static readonly Lazy<ReadCache<T>> Load = new(() => Bind<ReadCache<T>>("LoadCache", typeof(T)));
    }

    // Writing has no constructor constraint, unlike discovery and cache reads.
    private static class WriterBindings<T> where T : ICacheable
    {
        internal static readonly Lazy<WriteCache<T>> Save = new(() => Bind<WriteCache<T>>("SaveCache", typeof(T)));
    }

    private static TDelegate Bind<TDelegate>(string name, Type itemType = null) where TDelegate : Delegate
    {
        var owner = Assembly.Load("Modstoke.Preloader")
            .GetType("Modstoke.Preloader.CompatibilityTypeDiscovery", true);
        var method = owner.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(owner.FullName, name);
        if (itemType != null)
            method = method.MakeGenericMethod(itemType);
        return (TDelegate)method.CreateDelegate(typeof(TDelegate));
    }

    private static bool IsCacheEnabled() => EnableAssemblyCache.Value;

    #region Config

    private static readonly ConfigEntry<bool> EnableAssemblyCache = ConfigFile.CoreConfig.Bind(
     "Caching", "EnableAssemblyCache",
     true,
     "Enable/disable assembly metadata cache\nEnabling this will speed up discovery of plugins and patchers by caching the metadata of all types BepInEx discovers.");

    #endregion

    static TypeLoader()
    {
        CecilResolver = new DefaultAssemblyResolver();
        ReaderParameters = new ReaderParameters { AssemblyResolver = CecilResolver };

        CecilResolver.ResolveFailure += CecilResolveOnFailure;
    }

    public static AssemblyDefinition CecilResolveOnFailure(object sender, AssemblyNameReference reference)
    {
        return ResolveFailureHandler.Value(sender, reference, ReaderParameters, SearchDirectories, InvokeAssemblyResolve);
    }

    internal static AssemblyDefinition InvokeAssemblyResolve(object sender, AssemblyNameReference reference) =>
        AssemblyResolve?.Invoke(sender, reference);

    /// <summary>
    ///     Event fired when <see cref="TypeLoader" /> fails to resolve a type during type loading.
    /// </summary>
    public static event AssemblyResolveEventHandler AssemblyResolve;

    /// <summary>
    ///     Looks up assemblies in the given directory and locates all types that can be loaded and collects their metadata.
    /// </summary>
    /// <typeparam name="T">The specific base type to search for.</typeparam>
    /// <param name="directory">The directory to search for assemblies.</param>
    /// <param name="typeSelector">A function to check if a type should be selected and to build the type metadata.</param>
    /// <param name="assemblyFilter">A filter function to quickly determine if the assembly can be loaded.</param>
    /// <param name="cacheName">The name of the cache to get cached types from.</param>
    /// <returns>
    ///     A dictionary of all assemblies in the directory and the list of type metadatas of types that match the
    ///     selector.
    /// </returns>
    public static Dictionary<string, List<T>> FindPluginTypes<T>(string directory,
                                                                 Func<TypeDefinition, string, T> typeSelector,
                                                                 Func<AssemblyDefinition, bool> assemblyFilter =
                                                                     null,
                                                                 string cacheName = null)
        where T : ICacheable, new()
    {
        return ReaderBindings<T>.Find.Value(directory, typeSelector, assemblyFilter, cacheName, ReaderParameters, IsCacheEnabled);
    }

    /// <summary>
    ///     Loads an index of type metadatas from a cache.
    /// </summary>
    /// <param name="cacheName">Name of the cache</param>
    /// <typeparam name="T">Cacheable item</typeparam>
    /// <returns>
    ///     Cached type metadatas indexed by the path of the assembly that defines the type. If no cache is defined,
    ///     return null.
    /// </returns>
    public static Dictionary<string, CachedAssembly<T>> LoadAssemblyCache<T>(string cacheName)
        where T : ICacheable, new()
    {
        return ReaderBindings<T>.Load.Value(cacheName, IsCacheEnabled);
    }

    /// <summary>
    ///     Saves indexed type metadata into a cache.
    /// </summary>
    /// <param name="cacheName">Name of the cache</param>
    /// <param name="entries">List of plugin metadatas indexed by the path to the assembly that contains the types</param>
    /// <param name="hashes">Hash values that can be used for checking similarity between cached and live assembly</param>
    /// <typeparam name="T">Cacheable item</typeparam>
    public static void SaveAssemblyCache<T>(string cacheName,
                                            Dictionary<string, List<T>> entries,
                                            Dictionary<string, string> hashes)
        where T : ICacheable
    {
        WriterBindings<T>.Save.Value(cacheName, entries, hashes, IsCacheEnabled);
    }

    /// <summary>
    ///     Converts TypeLoadException to a readable string.
    /// </summary>
    /// <param name="ex">TypeLoadException</param>
    /// <returns>Readable representation of the exception</returns>
    public static string TypeLoadExceptionToString(ReflectionTypeLoadException ex)
    {
        var sb = new StringBuilder();
        foreach (var exSub in ex.LoaderExceptions)
        {
            sb.AppendLine(exSub.Message);
            var fusionLog = exSub switch
            {
                FileNotFoundException fileNotFound => fileNotFound.FusionLog,
                FileLoadException fileLoad => fileLoad.FusionLog,
                _ => null
            };
            if (!string.IsNullOrEmpty(fusionLog))
            {
                sb.AppendLine("Fusion Log:");
                sb.AppendLine(fusionLog);
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
