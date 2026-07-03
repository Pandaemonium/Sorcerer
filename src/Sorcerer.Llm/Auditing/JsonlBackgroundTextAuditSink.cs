using System.Text.Json;

namespace Sorcerer.Llm.Auditing;

public sealed class JsonlBackgroundTextAuditSink : IBackgroundTextAuditSink
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    public JsonlBackgroundTextAuditSink(string path)
    {
        _path = path;
    }

    public void Record(BackgroundTextAuditEntry entry)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(_path, JsonSerializer.Serialize(entry, _options) + Environment.NewLine);
        }
        catch
        {
            // Background audit failures should never affect gameplay.
        }
    }
}
