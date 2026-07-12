using System.IO;

namespace Sorcerer.Godot.Portraits;

/// <summary>
/// Environment-driven configuration for the out-of-process portrait worker. The heavy
/// SDXL stack lives in a machine-level venv shared with WildMagic (the worker scripts in
/// tools/portraits/ are verbatim copies); when the venv or worker is absent, portraits are
/// simply unavailable and the UI omits the feature.
/// </summary>
public static class PortraitConfig
{
    private const string DefaultPython = @"C:\Games\wm_image_venv\Scripts\python.exe";

    public static string PythonPath =>
        NonBlank(System.Environment.GetEnvironmentVariable("SORCERER_PORTRAIT_PYTHON")) ?? DefaultPython;

    /// <summary>tools/portraits/worker.py, found by walking up from the working directory and
    /// app base (same repo-root discovery the content catalogs use). Null when not found.</summary>
    public static string? WorkerPath
    {
        get
        {
            foreach (var root in CandidateRoots())
            {
                var candidate = Path.Combine(root, "tools", "portraits", "worker.py");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    /// <summary>Explicit SORCERER_PORTRAIT_ENABLED=1/0 wins; otherwise auto — enabled exactly
    /// when both the venv python and the worker script exist, so absence degrades gracefully.</summary>
    public static bool Enabled
    {
        get
        {
            var value = System.Environment.GetEnvironmentVariable("SORCERER_PORTRAIT_ENABLED")?.Trim().ToLowerInvariant();
            return value switch
            {
                "1" or "true" or "yes" or "on" => true,
                "0" or "false" or "no" or "off" => false,
                _ => File.Exists(PythonPath) && WorkerPath is not null,
            };
        }
    }

    public static string OutputDir =>
        NonBlank(System.Environment.GetEnvironmentVariable("SORCERER_PORTRAIT_DIR"))
        ?? Path.Combine("runs", "portraits");

    public static int Size => IntInRange("SORCERER_PORTRAIT_SIZE", 768, 256, 1280);

    public static int Steps => IntInRange("SORCERER_PORTRAIT_STEPS", 28, 1, 80);

    public static string Quant
    {
        get
        {
            var value = System.Environment.GetEnvironmentVariable("SORCERER_PORTRAIT_QUANT")?.Trim().ToLowerInvariant();
            return value is "int8" or "fp8" or "none" ? value : "int8";
        }
    }

    /// <summary>Whether the worker should evict resident Ollama models before generating —
    /// on a small shared GPU, SDXL plus a resident LLM overcommits VRAM. The LLM reloads on
    /// the next spell. Best-effort and harmless when the run uses a remote provider.</summary>
    public static bool FreeVram =>
        System.Environment.GetEnvironmentVariable("SORCERER_PORTRAIT_FREE_VRAM")?.Trim() is not ("0" or "false" or "no" or "off");

    private static IEnumerable<string> CandidateRoots()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                yield return directory.FullName;
                directory = directory.Parent;
            }
        }
    }

    private static int IntInRange(string variable, int fallback, int min, int max) =>
        int.TryParse(System.Environment.GetEnvironmentVariable(variable), out var parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;

    private static string? NonBlank(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
