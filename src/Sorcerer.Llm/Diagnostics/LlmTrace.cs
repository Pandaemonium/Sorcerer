namespace Sorcerer.Llm.Diagnostics;

/// <summary>An immutable snapshot of one LLM call for the debug view.</summary>
public sealed record LlmTraceEntry(
    int Id,
    DateTimeOffset StartedAt,
    string Purpose,
    string Model,
    string SystemPrompt,
    string UserPrompt,
    bool Completed,
    string? Response,
    string? Error,
    double ElapsedMs);

/// <summary>
/// Process-wide, thread-safe log of every LLM call. The prompt is recorded the instant a call is
/// dispatched (<see cref="Begin"/>, before the HTTP request goes out) so a developer can read it
/// while the model is still working; the response or error is filled in when the call resolves
/// (<see cref="End"/>). A monotonic <see cref="Revision"/> lets a UI poll cheaply and rebuild only
/// when something changed. This is a pure developer diagnostic — no game logic depends on it, and a
/// bounded ring of entries keeps memory flat over a long session.
/// </summary>
public static class LlmTrace
{
    private const int MaxEntries = 250;

    private static readonly object Gate = new();
    private static readonly List<MutableEntry> Entries = new();
    private static int _nextId;
    private static long _revision;

    /// <summary>Increments on every Begin/End/Clear so a poller can detect changes without copying.</summary>
    public static long Revision
    {
        get
        {
            lock (Gate)
            {
                return _revision;
            }
        }
    }

    /// <summary>Records a dispatched call and returns its id. Call immediately before sending the request.</summary>
    public static int Begin(string purpose, string? model, string? systemPrompt, string? userPrompt)
    {
        lock (Gate)
        {
            var id = ++_nextId;
            Entries.Add(new MutableEntry
            {
                Id = id,
                StartedAt = DateTimeOffset.Now,
                Purpose = string.IsNullOrWhiteSpace(purpose) ? "llm" : purpose,
                Model = model ?? "",
                SystemPrompt = systemPrompt ?? "",
                UserPrompt = userPrompt ?? "",
            });
            if (Entries.Count > MaxEntries)
            {
                Entries.RemoveRange(0, Entries.Count - MaxEntries);
            }

            _revision++;
            return id;
        }
    }

    /// <summary>Fills in the response (or error) for a previously begun call. Safe to call once per call.</summary>
    public static void End(int id, string? response, string? error)
    {
        lock (Gate)
        {
            var entry = Entries.FindLast(candidate => candidate.Id == id);
            if (entry is null || entry.Completed)
            {
                return;
            }

            entry.Completed = true;
            entry.Response = response;
            entry.Error = error;
            entry.ElapsedMs = (DateTimeOffset.Now - entry.StartedAt).TotalMilliseconds;
            _revision++;
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
            _revision++;
        }
    }

    /// <summary>An immutable, point-in-time copy of the log, oldest first.</summary>
    public static IReadOnlyList<LlmTraceEntry> Snapshot()
    {
        lock (Gate)
        {
            return Entries
                .Select(entry => new LlmTraceEntry(
                    entry.Id,
                    entry.StartedAt,
                    entry.Purpose,
                    entry.Model,
                    entry.SystemPrompt,
                    entry.UserPrompt,
                    entry.Completed,
                    entry.Response,
                    entry.Error,
                    entry.ElapsedMs))
                .ToList();
        }
    }

    private sealed class MutableEntry
    {
        public int Id;
        public DateTimeOffset StartedAt;
        public string Purpose = "";
        public string Model = "";
        public string SystemPrompt = "";
        public string UserPrompt = "";
        public bool Completed;
        public string? Response;
        public string? Error;
        public double ElapsedMs;
    }
}
