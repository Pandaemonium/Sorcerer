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
    int CreatedTurn,
    DateTimeOffset CreatedAt,
    int? StartedTurn = null,
    int? CompletedTurn = null,
    int? AppliedTurn = null,
    string? ResultText = null,
    string? Error = null);

public sealed record BackgroundJobSettings(
    bool Enabled = true,
    int MaxQueuedJobs = 12,
    int JobsPerTurn = 1);

public sealed class BackgroundJobQueue
{
    private readonly List<BackgroundJob> _jobs = new();

    public IReadOnlyList<BackgroundJob> Jobs => _jobs;

    public BackgroundJob Enqueue(string purpose, string targetId, int priority, int turn)
    {
        var job = new BackgroundJob(
            $"job_{_jobs.Count + 1}",
            purpose,
            targetId,
            priority,
            BackgroundJobState.Queued,
            turn,
            DateTimeOffset.UtcNow);
        _jobs.Add(job);
        return job;
    }

    public bool HasActiveJob(string purpose, string targetId) =>
        _jobs.Any(job =>
            job.Purpose.Equals(purpose, StringComparison.OrdinalIgnoreCase)
            && job.TargetId.Equals(targetId, StringComparison.OrdinalIgnoreCase)
            && job.State is BackgroundJobState.Queued or BackgroundJobState.Running or BackgroundJobState.Completed);

    public BackgroundJob? NextQueued() =>
        _jobs
            .Where(job => job.State == BackgroundJobState.Queued)
            .OrderByDescending(job => job.Priority)
            .ThenBy(job => job.CreatedTurn)
            .ThenBy(job => job.Id)
            .FirstOrDefault();

    public void Replace(BackgroundJob updated)
    {
        var index = _jobs.FindIndex(job => job.Id.Equals(updated.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _jobs[index] = updated;
        }
    }

    public IReadOnlyList<BackgroundJob> Snapshot() => _jobs.ToArray();

    public void ReplaceAll(IEnumerable<BackgroundJob> jobs)
    {
        _jobs.Clear();
        _jobs.AddRange(jobs);
    }
}
