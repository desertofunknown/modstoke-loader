using System.IO;
using System.Text;

namespace RedLoader.Utils;

/// <summary>
/// Helper class for writing text to files
/// </summary>
public class FileWriter
{
    public string Content => _content.ToString();

    public FileWriter(string path)
    {
        _extension = Path.GetExtension(path);
        _path = Path.ChangeExtension(path, null);
        if (string.IsNullOrEmpty(_extension))
        {
            _extension = ".txt";
        }
    }

    public FileWriter Line(string text)
    {
        _content.Append(text).Append('\n');
        return this;
    }

    public FileWriter Line()
    {
        _content.Append('\n');
        return this;
    }

    public FileWriter Add(string text)
    {
        _content.Append(text);
        return this;
    }

    public FileWriter Tab()
    {
        _content.Append('\t');
        return this;
    }

    public void Save()
    {
        File.WriteAllText(_path + _extension, Content);
    }

    private readonly StringBuilder _content = new();
    private readonly string _path;
    private readonly string _extension;
}