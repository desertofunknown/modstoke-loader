using System.Text;
using RedLoader.Utils;

namespace SonsSdk;

internal static class AssetMapCreator
{
    public static void CreateAssetMap(PathObject path, string name, IEnumerable<string> values)
    {
        var result = CreateAssetMap(name, values);
        (path / $"{name}AssetMap.cs").WriteText(result);
    }
    
    public static string CreateAssetMap(string name, IEnumerable<string> values)
    {
        var mapping = new Dictionary<string, string>();
        foreach (var assetName in values)
        {
            if (string.IsNullOrEmpty(assetName))
                continue;
            
            mapping[assetName] = CodeWriter.SanitizeVariableName(assetName);
        }
        
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated");
        sb.AppendLine("// ReSharper disable All");
        
        sb.AppendLine("namespace SonsSdk;");
        sb.AppendLine();
        sb.AppendLine($"public static class {name}AssetMap");
        sb.AppendLine("{");
        foreach (var (assetName, memberName) in mapping)
        {
            sb.AppendLine($"    public const string {memberName} = \"{assetName}\";");
        }
        sb.AppendLine("}");
        
        return sb.ToString();
    }
}
