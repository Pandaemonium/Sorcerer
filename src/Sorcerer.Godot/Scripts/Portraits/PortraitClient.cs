using System.Diagnostics;
using System.Text.Json;

namespace Sorcerer.Godot.Portraits;

public enum PortraitStatus
{
    Pending,
    Done,
    Error,
    Unknown,
}

public sealed record PortraitPoll(PortraitStatus Status, string? PathOrError);

/// <summary>
/// Client for the out-of-process portrait worker (tools/portraits/worker.py, running in the
/// shared image venv). Spawns the worker lazily, ships JSON-line requests over stdin, and
/// collects JSON-line results from stdout on a background task so the caller can poll without
/// ever blocking the frame. Not a Godot node: the reader task only touches locked state, and
/// the scene polls from _Process on the main thread.
/// Failure semantics: a spawn failure disables the client permanently; a worker that dies is
/// respawned on the next request; a failed request also tears the worker down, because a GPU
/// device-loss poisons the process and only a fresh one can recover.
/// </summary>
public sealed class PortraitClient : IDisposable
{
    private sealed record WorkerResult(bool Pending, bool Ok, string? Out, string? Error);

    private readonly object _lock = new();
    private readonly Dictionary<string, WorkerResult> _results = new();
    private readonly string? _ollamaHostForVramEviction;
    private Process? _proc;
    private bool _ready;
    private bool _spawnFailed;
    private int _nextId;

    public PortraitClient(string? ollamaHostForVramEviction)
    {
        _ollamaHostForVramEviction = ollamaHostForVramEviction;
    }

    public bool Available => PortraitConfig.Enabled && !_spawnFailed;

    /// <summary>True once a worker is spawned but the model has not finished loading —
    /// the UI shows a "first portrait is slow" note during this window.</summary>
    public bool Warming
    {
        get
        {
            lock (_lock)
            {
                return _proc is not null && !_ready && !_spawnFailed;
            }
        }
    }

    /// <summary>Queue a portrait request. Returns an id to poll, or null when portraits are
    /// unavailable. Safe to call before the model finishes loading (requests wait in the pipe).</summary>
    public string? Request(string description, int? seed = null)
    {
        if (!Available || string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        if (!EnsureWorker())
        {
            return null;
        }

        string requestId;
        lock (_lock)
        {
            requestId = _nextId.ToString();
            _nextId++;
        }

        var outDir = PortraitConfig.OutputDir;
        Directory.CreateDirectory(outDir);
        var outPath = Path.GetFullPath(Path.Combine(
            outDir,
            $"portrait_{requestId}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.png"));

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = requestId,
            ["description"] = description.Trim(),
            ["out"] = outPath,
            ["seed"] = seed,
            ["size"] = PortraitConfig.Size,
            ["steps"] = PortraitConfig.Steps,
        });

        try
        {
            var stdin = _proc?.StandardInput;
            if (stdin is null)
            {
                return null;
            }

            stdin.WriteLine(payload);
            stdin.Flush();
        }
        catch
        {
            RestartWorker();
            return null;
        }

        lock (_lock)
        {
            _results[requestId] = new WorkerResult(Pending: true, Ok: false, null, null);
        }

        return requestId;
    }

    public PortraitPoll Poll(string requestId)
    {
        WorkerResult? result;
        bool procDead;
        lock (_lock)
        {
            _results.TryGetValue(requestId, out result);
            procDead = _proc is null || _proc.HasExited;
        }

        if (result is null)
        {
            return procDead
                ? new PortraitPoll(PortraitStatus.Error, "portrait worker stopped")
                : new PortraitPoll(PortraitStatus.Unknown, null);
        }

        if (result.Pending)
        {
            if (procDead)
            {
                // The worker died (e.g. a GPU device-loss crash) before answering; a fresh
                // one spawns on the next request.
                RestartWorker();
                return new PortraitPoll(PortraitStatus.Error, "portrait worker stopped — try again");
            }

            return new PortraitPoll(PortraitStatus.Pending, null);
        }

        if (result.Ok)
        {
            return new PortraitPoll(PortraitStatus.Done, result.Out);
        }

        RestartWorker();
        return new PortraitPoll(PortraitStatus.Error, result.Error ?? "unknown error");
    }

    public void Close() => RestartWorker();

    public void Dispose() => Close();

    private bool EnsureWorker()
    {
        lock (_lock)
        {
            if (_proc is not null && !_proc.HasExited)
            {
                return true;
            }
        }

        if (_spawnFailed)
        {
            return false;
        }

        var worker = PortraitConfig.WorkerPath;
        if (worker is null)
        {
            _spawnFailed = true;
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = PortraitConfig.PythonPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(worker);
        // The worker scripts are verbatim WildMagic copies, so they read WILDMAGIC_* names;
        // Sorcerer's own config maps onto them here.
        startInfo.Environment["WILDMAGIC_PORTRAIT_QUANT"] = PortraitConfig.Quant;
        if (PortraitConfig.FreeVram && !string.IsNullOrWhiteSpace(_ollamaHostForVramEviction))
        {
            startInfo.Environment["WILDMAGIC_FREE_OLLAMA_HOST"] = _ollamaHostForVramEviction;
        }
        else
        {
            startInfo.Environment.Remove("WILDMAGIC_FREE_OLLAMA_HOST");
        }

        Process proc;
        try
        {
            proc = Process.Start(startInfo) ?? throw new InvalidOperationException("no process");
        }
        catch
        {
            _spawnFailed = true;
            return false;
        }

        lock (_lock)
        {
            _proc = proc;
            _ready = false;
        }

        _ = Task.Run(() => ReadLoop(proc));
        // Drain stderr (worker diagnostics) so the pipe can never fill and stall the worker.
        _ = Task.Run(() =>
        {
            try
            {
                while (proc.StandardError.ReadLine() is not null)
                {
                }
            }
            catch
            {
            }
        });
        return true;
    }

    private void ReadLoop(Process proc)
    {
        try
        {
            while (proc.StandardOutput.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonDocument message;
                try
                {
                    message = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (message)
                {
                    var root = message.RootElement;
                    if (root.TryGetProperty("event", out var evt)
                        && evt.ValueKind == JsonValueKind.String
                        && evt.GetString() == "ready")
                    {
                        lock (_lock)
                        {
                            _ready = true;
                        }

                        continue;
                    }

                    if (root.TryGetProperty("id", out var idElement))
                    {
                        var id = idElement.ValueKind == JsonValueKind.String
                            ? idElement.GetString() ?? ""
                            : idElement.ToString();
                        var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
                        var outPath = root.TryGetProperty("out", out var outElement) && outElement.ValueKind == JsonValueKind.String
                            ? outElement.GetString()
                            : null;
                        var error = root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String
                            ? errorElement.GetString()
                            : null;
                        lock (_lock)
                        {
                            _results[id] = new WorkerResult(Pending: false, ok, outPath, error);
                        }
                    }
                }
            }
        }
        catch
        {
            // Reader dies with its worker; state below marks it not-ready either way.
        }

        lock (_lock)
        {
            if (ReferenceEquals(_proc, proc))
            {
                _ready = false;
            }
        }
    }

    private void RestartWorker()
    {
        Process? proc;
        lock (_lock)
        {
            proc = _proc;
            _proc = null;
            _ready = false;
        }

        if (proc is null)
        {
            return;
        }

        try
        {
            proc.StandardInput.Close();
        }
        catch
        {
        }

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
