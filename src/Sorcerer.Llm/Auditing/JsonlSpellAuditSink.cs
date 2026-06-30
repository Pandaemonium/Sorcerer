using System.Text.Json;
using Sorcerer.Magic.Auditing;

namespace Sorcerer.Llm.Auditing;

public sealed class JsonlSpellAuditSink : ISpellAuditSink
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    public JsonlSpellAuditSink(string path)
    {
        _path = path;
    }

    public void Record(SpellAuditEntry entry)
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
            // Audits are essential for development, but logging failures must never break gameplay.
        }
    }
}
