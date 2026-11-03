using Alt.Json;

using System.Text.Json.Serialization;

namespace SonsSdk;

public class ManifestData
{
    /// <summary>
    /// Unique id of the mod
    /// </summary>
    [JsonProperty("id")]
    [JsonInclude, JsonPropertyName("id")]
    public string Id { get; internal set; }

    /// <summary>
    /// Name of the mod
    /// </summary>
    [JsonProperty("name")]
    [JsonInclude, JsonPropertyName("name")]
    public string Name { get; internal set; }

    /// <summary>
    /// Author of the mod
    /// </summary>
    [JsonProperty("author")]
    [JsonInclude, JsonPropertyName("author")]
    public string Author { get; internal set; }

    /// <summary>
    /// Version of the mod
    /// </summary>
    [JsonProperty("version")]
    [JsonInclude, JsonPropertyName("version")]
    public string Version { get; internal set; }

    /// <summary>
    /// Description of the mod
    /// </summary>
    [JsonProperty("description")]
    [JsonInclude, JsonPropertyName("description")]
    public string Description { get; internal set; }

    /// <summary>
    /// Game version the mod is compatible with
    /// </summary>
    [JsonProperty("gameVersion")]
    [JsonInclude, JsonPropertyName("gameVersion")]
    public string GameVersion { get; internal set; }

    /// <summary>
    /// Loader version the mod is compatible with
    /// </summary>
    [JsonProperty("loaderVersion")]
    [JsonInclude, JsonPropertyName("loaderVersion")]
    public string LoaderVersion { get; internal set; }

    /// <summary>
    /// Where this mod is able to run. Possible values: "Client", "Server", "Universal".
    /// </summary>
    [JsonProperty("platform")]
    [JsonInclude, JsonPropertyName("platform")]
    public string Platform { get; internal set; } = "Client";

    /// <summary>
    /// Optional. List of dependencies of the mod
    /// </summary>
    [JsonProperty("dependencies")]
    [JsonInclude, JsonPropertyName("dependencies")]
    public string[] Dependencies { get; internal set; }

    /// <summary>
    /// Optional. The hex string color in which the mod's name will be displayed in the console
    /// </summary>
    /// <example>#ffffff</example>
    [JsonProperty("logColor")]
    [JsonInclude, JsonPropertyName("logColor")]
    public string LogColor { get; internal set; }

    /// <summary>
    /// Optional. Download url of the mod.
    /// </summary>
    [JsonProperty("url")]
    [JsonInclude, JsonPropertyName("url")]
    public string Url { get; internal set; }

    /// <summary>
    /// Optional. Priority of the mod.
    /// </summary>
    [JsonProperty("priority")]
    [JsonInclude, JsonPropertyName("priority")]
    public int Priority { get; internal set; }

    /// <summary>
    /// Type of the assembly. Possible values: "Mod", "Library".
    /// </summary>
    [JsonProperty("type")]
    [JsonInclude, JsonPropertyName("type")]
    public EAssemblyType Type { get; internal set; } = EAssemblyType.Mod;

    [JsonInclude]
    public Version VersionObject { get; internal set; }

    public enum EAssemblyType
    {
        Mod,
        Library
    }
}
