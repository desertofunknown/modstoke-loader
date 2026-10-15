using System;
using System.IO;
using System.Text.RegularExpressions;

namespace BepInEx;

/// <summary>
/// Provides the public Modstoke product name and version strings shown by the loader.
/// </summary>
public static class OpenACAIBranding
{
    /// <summary>
    /// Human-facing product name for the Sons Of The Forest loader.
    /// </summary>
    public const string LoaderName = "Modstoke Loader";

    /// <summary>
    /// Version label used when a release manifest is not available.
    /// </summary>
    public const string DevelopmentLoaderVersion = "development";

    private static readonly Regex ManifestVersionPattern =
        new Regex("\"version\"\\s*:\\s*\"(?<version>[^\"]+)\"", RegexOptions.Compiled);

    /// <summary>
    /// Gets the Modstoke loader version from environment or installed manifest metadata.
    /// </summary>
    public static string LoaderVersion
    {
        get
        {
            var version = Environment.GetEnvironmentVariable("OPENACAI_LOADER_VERSION");
            if (!string.IsNullOrEmpty(version))
                return version;

            version = TryReadInstalledManifestVersion();
            return string.IsNullOrEmpty(version) ? DevelopmentLoaderVersion : version;
        }
    }

    /// <summary>
    /// Gets the product name with the Modstoke loader version appended for logs and windows.
    /// </summary>
    public static string LoaderDisplayName
    {
        get
        {
            var version = LoaderVersion;
            return version == DevelopmentLoaderVersion ? LoaderName + " (" + version + ") by OpenACAI" : LoaderName + " v" + version + " by OpenACAI";
        }
    }

    /// <summary>
    /// Gets the Windows console title used by the preloader.
    /// </summary>
    public static string ConsoleTitle => LoaderDisplayName + " - " + Paths.ProcessName;

    /// <summary>
    /// Gets the BepInEx compatibility runtime version preserved for plugin compatibility.
    /// </summary>
    public static string BepInExRuntimeVersion
    {
        get
        {
            var version = Paths.BepInExVersion;
            return new SemanticVersioning.Version(version.Major, version.Minor, version.Patch, version.PreRelease).ToString();
        }
    }

    private static string TryReadInstalledManifestVersion()
    {
        try
        {
            if (string.IsNullOrEmpty(Paths.BepInExRootPath))
                return null;

            var manifestPath = Path.Combine(
                Paths.BepInExRootPath,
                "plugins",
                "OpenACAILoader",
                "openacai-loader.manifest.json");

            if (!File.Exists(manifestPath))
                return null;

            var match = ManifestVersionPattern.Match(File.ReadAllText(manifestPath));
            return match.Success ? match.Groups["version"].Value : null;
        }
        catch
        {
            return null;
        }
    }
}
