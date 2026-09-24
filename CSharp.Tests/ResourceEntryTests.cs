using System.Buffers.Binary;
using AssetsTools.NET;
using MoenotesAssets;
using Xunit;
namespace MoenotesAssets.Tests;

public class ResourceEntryTests
{
    [Fact]
    public async Task RawResourceThatLooksLikeAssetHeaderIsNotParsedAsMetadata()
    {
        using var dir = new TempDirectory();
        var fake = new byte[256];
        BinaryPrimitives.WriteUInt32BigEndian(fake.AsSpan(8), 23);
        "6000.3.12f1\0"u8.CopyTo(fake.AsSpan(48));
        fake[64] = 1; BinaryPrimitives.WriteInt32LittleEndian(fake.AsSpan(65), 1);
        BinaryPrimitives.WriteInt32LittleEndian(fake.AsSpan(108), 1);
        using (var reader = new AssetsFileReader(new MemoryStream(fake))) Assert.True(AssetsFile.IsAssetsFile(reader, 0, fake.Length));
        var serialized = Fixture.Serialized();
        var bundle = Fixture.Bundle(serialized, fake);
        var path = Path.Combine(dir.Path, "fixture.bundle"); File.WriteAllBytes(path, bundle);
        var crc = ~BinaryTools.Crc32(fake, BinaryTools.Crc32(serialized));
        var catalog = Catalog.Parse(Fixture.Catalog(bundle.Length, crc));
        var job = new WorkerJob(new Config { CdnRoot = "https://example.com" }, catalog.Target(Fixture.Key), [new(catalog.Closure(Fixture.Key)[0], path)], Path.Combine(dir.Path, "out"));
        var files = await Processes.Worker(job, dir.Path, CancellationToken.None);
        Assert.Equal(Fixture.Body, File.ReadAllBytes(Path.Combine(job.Output, Assert.Single(files).Name)));
    }
}
