using AssetsTools.NET;
using MoenotesAssets;
using Xunit;
namespace MoenotesAssets.Tests;

public class EmbeddedCriTests
{
    [Fact]
    public void ReadsOnlyTheSelectedManagedByteImplementation()
    {
        AssetTypeValueField Node(string name, AssetTypeValue? value = null, params AssetTypeValueField[] children) => new()
        { TemplateField = new AssetTypeTemplateField { Name = name, ValueType = value?.ValueType ?? AssetValueType.None }, Value = value, Children = children.ToList() };
        var data = "@UTFsynthetic"u8.ToArray();
        var reference = new AssetTypeReferencedObject { rid = 42, type = new AssetTypeReference { ClassName = "CriSerializedBytesAssetImpl" }, data = Node("Base", null, Node("data", null, Node("Array", new AssetTypeValue(AssetValueType.ByteArray, data)))) };
        var registry = new ManagedReferencesRegistry { version = 2, references = [reference] };
        var field = Node("Base", null, Node("implementation", null, Node("rid", new AssetTypeValue(42L))), Node("references", new AssetTypeValue(registry)));
        Assert.Equal(data, Worker.EmbeddedCriBytes(field, 100));
        Assert.Throws<InvalidDataException>(() => Worker.EmbeddedCriBytes(field, 4));
        field["implementation"]["rid"].AsLong = 43;
        Assert.Throws<InvalidDataException>(() => Worker.EmbeddedCriBytes(field, 100));
        field["implementation"]["rid"].AsLong = 42;
        reference.type.ClassName = "OtherImplementation";
        Assert.Throws<InvalidDataException>(() => Worker.EmbeddedCriBytes(field, 100));
    }
    [Fact]
    public async Task ParentTerminatesOverBudgetWorkerWithoutSelfKillError()
    {
        using var dir = new TempDirectory(); var serialized = Fixture.Serialized(); var bundle = Fixture.Bundle(serialized);
        var path = Path.Combine(dir.Path, "fixture.bundle"); File.WriteAllBytes(path, bundle);
        var catalog = Catalog.Parse(Fixture.Catalog(bundle.Length, ~BinaryTools.Crc32(serialized)));
        var job = new WorkerJob(new Config { CdnRoot = "https://example.com", WorkerMemoryBytes = 1 << 20 }, catalog.Target(Fixture.Key), [new(catalog.Closure(Fixture.Key)[0], path)], Path.Combine(dir.Path, "out"));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Processes.Worker(job, dir.Path, CancellationToken.None));
        Assert.Contains("Worker memory/disk limit", error.Message);
    }
}
