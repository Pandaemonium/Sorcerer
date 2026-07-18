using Sorcerer.Core.World;
using Xunit;

namespace Sorcerer.Tests;

public sealed class ContentPackLoaderTests
{
    [Fact]
    public void LooseAndEmbeddedPacksLoadTheSameRegionalCorpus()
    {
        var loose = RegionCatalog.LoadDefault();
        var embedded = RegionCatalog.LoadBuiltIn();

        Assert.Equal(
            loose.Regions.Select(region => region.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase),
            embedded.Regions.Select(region => region.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(
            loose.Traditions.Select(tradition => tradition.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase),
            embedded.Traditions.Select(tradition => tradition.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(14, loose.Regions.Count);
        Assert.Equal(14, embedded.Regions.Count);
    }

    [Fact]
    public void DataOwnedVocabularyReplacesTheDeletedRegionSwitches()
    {
        var registry = RegionCatalog.LoadDefault();
        var hollowmere = registry.Region("hollowmere_margin");

        Assert.NotNull(hollowmere?.Vocabulary);
        Assert.Contains("grudge-walker", hollowmere!.Vocabulary!.ThreatNouns);
        Assert.Equal("folded-road checkpoint", hollowmere.Vocabulary.PromisedSiteName);
        Assert.Contains("memory shrine", hollowmere.Vocabulary.FixtureNouns);
    }

    [Fact]
    public void DuplicatePackIdIsRejected()
    {
        using var tree = new PackTree();
        tree.WritePack("alpha", id: "shared_id");
        tree.WritePack("beta", id: "shared_id");

        var error = Assert.Throws<ContentPackException>(() => ContentPackLoader.LoadLoose(tree.Root));
        Assert.Contains("shared_id", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnsupportedSchemaVersionIsRejected()
    {
        using var tree = new PackTree();
        tree.WritePack("future", id: "future", schemaVersion: ContentPackLoader.CurrentSchemaVersion + 1);

        Assert.Throws<ContentPackException>(() => ContentPackLoader.LoadLoose(tree.Root));
    }

    [Fact]
    public void MissingDependencyIsRejected()
    {
        using var tree = new PackTree();
        tree.WritePack("orphan", id: "orphan", dependencies: new[] { "ghost" });

        var error = Assert.Throws<ContentPackException>(() => ContentPackLoader.LoadLoose(tree.Root));
        Assert.Contains("ghost", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DependenciesLoadBeforeDependents()
    {
        using var tree = new PackTree();
        tree.WritePack("dependent", id: "dependent", dependencies: new[] { "base" });
        tree.WritePack("base", id: "base");

        var entries = ContentPackLoader.LoadLoose(tree.Root);
        var basePosition = FirstIndex(entries, "base");
        var dependentPosition = FirstIndex(entries, "dependent");

        Assert.True(basePosition >= 0 && dependentPosition >= 0);
        Assert.True(basePosition < dependentPosition, "a dependency must load before its dependent");
    }

    [Fact]
    public void ManifestIdMayDifferFromNestedDirectoryName()
    {
        using var tree = new PackTree();
        tree.WritePack("category/base_folder", id: "base");
        tree.WritePack("category/dependent_folder", id: "dependent", dependencies: new[] { "base" });

        var entries = ContentPackLoader.LoadLoose(tree.Root);

        Assert.True(FirstIndex(entries, "base") < FirstIndex(entries, "dependent"));
        Assert.DoesNotContain(entries, entry => entry.PackId.Equals("base_folder", StringComparison.OrdinalIgnoreCase));
    }

    private static int FirstIndex(IReadOnlyList<ContentPackEntry> entries, string packId)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index].PackId.Equals(packId, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private sealed class PackTree : IDisposable
    {
        public PackTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "sorcerer_packs_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void WritePack(
            string directory,
            string id,
            int schemaVersion = 1,
            IReadOnlyList<string>? dependencies = null)
        {
            var dir = Path.Combine(Root, directory);
            Directory.CreateDirectory(dir);
            var deps = string.Join(", ", (dependencies ?? Array.Empty<string>()).Select(dep => $"\"{dep}\""));
            File.WriteAllText(
                Path.Combine(dir, "pack.json"),
                $"{{\"schemaVersion\": {schemaVersion}, \"id\": \"{id}\", \"dependencies\": [{deps}]}}");
            File.WriteAllText(
                Path.Combine(dir, "region.json"),
                $"{{\"regions\": [{{\"id\": \"{id}\", \"name\": \"{id}\"}}]}}");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
