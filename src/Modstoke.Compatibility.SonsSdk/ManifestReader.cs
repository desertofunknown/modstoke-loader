using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Alt.Json;
using RedLoader;
using SonsSdk;

namespace SonsLoaderPlugin;

public class ManifestReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() }
    };

    public static ManifestData TryReadManifest(Assembly assembly)
    {
        var manifestName = assembly.GetManifestResourceNames().FirstOrDefault(x => x.EndsWith(".manifest.json"));
        if (manifestName == null)
            return null;

        var manifest = assembly.GetManifestResourceStream(manifestName);
        if (manifest == null)
            return null;
        
        using var reader = new StreamReader(manifest);
        var json = reader.ReadToEnd();
        return ReadJson(json);
    }
    
    public static ManifestData TryReadManifest(string path)
    {
        if (!File.Exists(path))
            return null;
        
        return ReadJson(File.ReadAllText(path));
    }

    private static ManifestData ReadJson(string json)
    {
        if (JsonConvert.DefaultSettings != null)
            return JsonConvert.DeserializeObject<ManifestData>(json);
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<ManifestData>(json, JsonOptions);
        }
        catch (System.Text.Json.JsonException)
        {
            // Some legacy manifests use JSON.NET-only syntax or primitive coercions.
            return JsonConvert.DeserializeObject<ManifestData>(json);
        }
    }
}
