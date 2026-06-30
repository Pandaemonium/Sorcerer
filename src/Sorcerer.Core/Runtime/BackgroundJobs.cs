namespace Sorcerer.Core.Runtime;

public enum BackgroundJobState
{
    Queued,
    Running,
    Completed,
    Failed,
    Applied,
}

public sealed record BackgroundJob(
    string Id,
    string Purpose,
    string TargetId,
    int Priority,
    BackgroundJobState State,
    DateTimeOffset CreatedAt,
    string? Error = null);

public sealed class BackgroundJobQueue
{
    private readonly List<BackgroundJob> _jobs = new();

    public IReadOnlyList<BackgroundJob> Jobs => _jobs;

    public BackgroundJob Enqueue(string purpose, string targetId, int priority)
    {
        var job = new BackgroundJob(
            $"job_{_jobs.Count + 1}",
            purpose,
            targetId,
            priority,
            BackgroundJobState.Queued,
            DateTimeOffset.UtcNow);
        _jobs.Add(job);
        return job;
    }
}
