using System.Text.Json;
using Sorcerer.Core.Dialogue;

namespace Sorcerer.Llm.Auditing;

public sealed class JsonlDialogueAuditSink : IDialogueAuditSink
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    public JsonlDialogueAuditSink(string path)
    {
        _path = path;
    }

    public void Record(DialogueAuditEntry entry)
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
            // Dialogue audit failures should never affect gameplay.
        }
    }
}
