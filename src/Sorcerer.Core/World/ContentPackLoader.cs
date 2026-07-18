using System.Reflection;

namespace Sorcerer.Core.World;

/// <summary>Raised when a content pack tree is structurally invalid: a duplicate pack id, an
/// unsupported schema version, a missing dependency, or a dependency cycle. These are authoring
/// errors that integrity tests catch before a build ships.</summary>
public sealed class ContentPackException : Exception
{
    public ContentPackException(string message) : base(message)
    {
    }
}

/// <summary>One JSON file inside a content pack (or the shared root), with a lazy text reader so
/// loose files and embedded resources look identical to catalogs.</summary>
public sealed record ContentPackEntry(string PackId, string FileName, Func<string> ReadText);

/// <summary>
/// WP1 (docs/CONTENT_SPRINT_PLAN.md): the single owner of content-pack discovery. It finds packs
/// recursively (a directory — or embedded-resource prefix — carrying <c>pack.json</c> is a pack),
/// rejects duplicate pack ids, validates schema versions and dependencies, orders packs so
/// dependencies load first, and yields the shared root first. Catalogs (regions today; items,
/// actors, encounters as later WPs land) parse their own records from the entries it returns, so
/// loose development files and the embedded build load the exact same corpus.
/// </summary>
public static class ContentPackLoader
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFile = "pack.json";
    public const string EmbeddedPrefix = "Sorcerer.Core.Content.RegionPacks.";

    /// <summary>The reserved shared-root pack id: files not under any pack (e.g. cross-regional
    /// traditions) load first, before every pack.</summary>
    public const string SharedPackId = "";

    public static bool HasLoosePacks(string packsRoot) =>
        Directory.Exists(packsRoot)
        && Directory.EnumerateFiles(packsRoot, ManifestFile, SearchOption.AllDirectories).Any();

    /// <summary>Discovers loose packs under <paramref name="packsRoot"/>. Each directory that
    /// contains a <c>pack.json</c> is a pack; JSON files elsewhere (e.g. a <c>_shared</c> folder)
    /// load first as shared content.</summary>
    public static IReadOnlyList<ContentPackEntry> LoadLoose(string packsRoot)
    {
        if (!Directory.Exists(packsRoot))
        {
            return Array.Empty<ContentPackEntry>();
        }

        var packDirs = Directory
            .EnumerateFiles(packsRoot, ManifestFile, SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(dir => !string.IsNullOrEmpty(dir))
            .Cast<string>()
            .Select(dir => new PackDirectory(
                Path.GetFullPath(dir),
                NormalizePath(Path.GetRelativePath(packsRoot, dir))))
            .OrderByDescending(pack => pack.FullPath.Length)
            .ToArray();

        var raw = new List<RawEntry>();
        foreach (var path in Directory.EnumerateFiles(packsRoot, "*.json", SearchOption.AllDirectories))
        {
            var dir = Path.GetDirectoryName(path) ?? packsRoot;
            var fileName = Path.GetFileName(path);
            var packId = packDirs.FirstOrDefault(pack => IsWithin(dir, pack.FullPath))?.SourceKey
                ?? SharedPackId;
            var captured = path;
            raw.Add(new RawEntry(packId, fileName, () => File.ReadAllText(captured)));
        }

        return Order(raw);
    }

    /// <summary>Discovers embedded packs from resource names shaped
    /// <c>Sorcerer.Core.Content.RegionPacks.&lt;packDir&gt;\&lt;file&gt;</c>. Mirrors
    /// <see cref="LoadLoose"/> so the embedded build is bit-for-bit the same corpus.</summary>
    public static IReadOnlyList<ContentPackEntry> LoadEmbedded(Assembly assembly, string prefix = EmbeddedPrefix)
    {
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(name => new EmbeddedResource(name, NormalizePath(name[prefix.Length..])))
            .ToArray();
        var packDirs = resources
            .Where(resource => resource.RelativePath.EndsWith('/' + ManifestFile, StringComparison.OrdinalIgnoreCase))
            .Select(resource => resource.RelativePath[..^(ManifestFile.Length + 1)])
            .OrderByDescending(path => path.Length)
            .ToArray();

        var raw = new List<RawEntry>();
        foreach (var resource in resources)
        {
            var packId = packDirs.FirstOrDefault(pack => IsWithin(resource.RelativePath, pack))
                ?? SharedPackId;
            if (packId.StartsWith('_'))
            {
                packId = SharedPackId;
            }

            var fileName = resource.RelativePath.Split('/')[^1];
            var captured = resource.ResourceName;
            raw.Add(new RawEntry(packId, fileName, () => ReadEmbedded(assembly, captured)));
        }

        return Order(raw);
    }

    private static string ReadEmbedded(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new ContentPackException($"Embedded content resource '{resourceName}' vanished.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<ContentPackEntry> Order(IReadOnlyList<RawEntry> raw)
    {
        var byPack = raw
            .GroupBy(entry => entry.PackId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var manifests = new Dictionary<string, PackManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sourcePackId, entries) in byPack)
        {
            if (sourcePackId == SharedPackId)
            {
                continue;
            }

            var manifestEntry = entries.FirstOrDefault(entry =>
                entry.FileName.Equals(ManifestFile, StringComparison.OrdinalIgnoreCase));
            var manifest = manifestEntry is null
                ? new PackManifest(sourcePackId, sourcePackId, CurrentSchemaVersion, Array.Empty<string>())
                : ParseManifest(sourcePackId, manifestEntry.ReadText());
            if (manifest.SchemaVersion is < 1 or > CurrentSchemaVersion)
            {
                throw new ContentPackException(
                    $"Pack '{manifest.Id}' declares schemaVersion {manifest.SchemaVersion}; supported range is 1..{CurrentSchemaVersion}.");
            }

            if (manifests.ContainsKey(manifest.Id))
            {
                throw new ContentPackException($"Duplicate content pack id '{manifest.Id}'.");
            }

            manifests[manifest.Id] = manifest;
        }

        foreach (var manifest in manifests.Values)
        {
            foreach (var dependency in manifest.Dependencies)
            {
                if (!manifests.ContainsKey(dependency))
                {
                    throw new ContentPackException(
                        $"Pack '{manifest.Id}' depends on unknown pack '{dependency}'.");
                }
            }
        }

        var ordered = new List<ContentPackEntry>();
        foreach (var entry in byPack.TryGetValue(SharedPackId, out var shared) ? shared : Array.Empty<RawEntry>())
        {
            ordered.Add(entry.ToEntry(SharedPackId));
        }

        foreach (var packId in TopologicalOrder(manifests))
        {
            var manifest = manifests[packId];
            foreach (var entry in byPack[manifest.SourcePackId]
                .Where(entry => !entry.FileName.Equals(ManifestFile, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(entry.ToEntry(manifest.Id));
            }
        }

        return ordered;
    }

    private static IEnumerable<string> TopologicalOrder(IReadOnlyDictionary<string, PackManifest> manifests)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void Visit(string id)
        {
            if (visited.Contains(id))
            {
                return;
            }

            if (!visiting.Add(id))
            {
                throw new ContentPackException($"Content pack dependency cycle involving '{id}'.");
            }

            foreach (var dependency in manifests[id].Dependencies.OrderBy(dep => dep, StringComparer.OrdinalIgnoreCase))
            {
                Visit(dependency);
            }

            visiting.Remove(id);
            visited.Add(id);
            result.Add(id);
        }

        foreach (var id in manifests.Keys.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            Visit(id);
        }

        return result;
    }

    private static PackManifest ParseManifest(string sourcePackId, string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        var id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == System.Text.Json.JsonValueKind.String
            ? idElement.GetString() ?? sourcePackId
            : sourcePackId;
        var schema = root.TryGetProperty("schemaVersion", out var schemaElement) && schemaElement.TryGetInt32(out var parsed)
            ? parsed
            : CurrentSchemaVersion;
        var dependencies = root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == System.Text.Json.JsonValueKind.Array
            ? deps.EnumerateArray()
                .Where(item => item.ValueKind == System.Text.Json.JsonValueKind.String)
                .Select(item => item.GetString()!)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray()
            : Array.Empty<string>();
        return new PackManifest(sourcePackId, string.IsNullOrWhiteSpace(id) ? sourcePackId : id, schema, dependencies);
    }

    private sealed record RawEntry(string PackId, string FileName, Func<string> ReadText)
    {
        public ContentPackEntry ToEntry(string effectivePackId) => new(effectivePackId, FileName, ReadText);
    }

    private static bool IsWithin(string candidate, string directory)
    {
        var normalizedCandidate = NormalizePath(Path.GetFullPath(candidate)).TrimEnd('/');
        var normalizedDirectory = NormalizePath(Path.GetFullPath(directory)).TrimEnd('/');
        return normalizedCandidate.Equals(normalizedDirectory, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedDirectory + '/', StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private sealed record PackDirectory(string FullPath, string SourceKey);

    private sealed record EmbeddedResource(string ResourceName, string RelativePath);

    private sealed record PackManifest(
        string SourcePackId,
        string Id,
        int SchemaVersion,
        IReadOnlyList<string> Dependencies);
}
