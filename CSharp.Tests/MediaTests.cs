using MoenotesAssets;
using VGAudio.Codecs.CriHca;
using VGAudio.Containers.Hca;
using VGAudio.Formats.Pcm16;
using Xunit;
namespace MoenotesAssets.Tests;

public class MediaTests
{
    private static WorkerJob Job(Config config, string path, string output, string type) => new(config,
        new(1, "media/fixture", "https://cdn.invalid/asset/Android/fixture", Catalog.Cri, type, [], null),
        [new(new(1, "media/fixture", "https://cdn.invalid/asset/Android/fixture", Catalog.Cri, type, [], null), path)], output);
    [Fact]
    public async Task AcbToAacAndMissingExternalBank()
    {
        using var dir = new TempDirectory(); var config = new Config { CdnRoot = "https://cdn.invalid" };
        var samples = Enumerable.Range(0, 24000).Select(i => (short)(Math.Sin(i * 0.08) * 6000)).ToArray();
        var hca = new HcaWriter().GetFile(new Pcm16FormatBuilder([samples], 48000).Build(), new HcaConfiguration { EncryptionKey = new CriHcaKey(config.CriKey) });
        var path = Path.Combine(dir.Path, "bank.acb"); File.WriteAllBytes(path, CriFixture.Acb(hca));
        var job = Job(config, path, Path.Combine(dir.Path, "out"), "CriWare.Assets.CriAtomAcbAsset");
        var result = await Processes.Worker(job, dir.Path, CancellationToken.None); Assert.Single(result); Assert.Equal("audio/mp4", result[0].MediaType); Assert.Equal("synthetic-tone", result[0].Label);
        File.WriteAllBytes(path, CriFixture.Acb(hca, true)); var exception = await Assert.ThrowsAsync<InvalidDataException>(() => Processes.Worker(job with { Output = Path.Combine(dir.Path, "external") }, dir.Path, CancellationToken.None)); Assert.Contains("External AWB", exception.Message);
    }
    [Fact]
    public async Task EncryptedUsmToSilentH264()
    {
        using var dir = new TempDirectory(); var config = new Config { CdnRoot = "https://cdn.invalid" }; var raw = Path.Combine(dir.Path, "source.m2v");
        await Processes.Run(config.Ffmpeg, ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=64x48:rate=25", "-t", "0.4", "-c:v", "mpeg2video", "-f", "mpeg2video", raw], CancellationToken.None);
        var path = Path.Combine(dir.Path, "movie.usm"); File.WriteAllBytes(path, CriFixture.Usm(File.ReadAllBytes(raw), config.CriKey));
        var job = Job(config, path, Path.Combine(dir.Path, "out"), "CriWare.Assets.CriManaUsmAsset");
        var result = await Processes.Worker(job, dir.Path, CancellationToken.None); Assert.Single(result); Assert.Equal("video/mp4", result[0].MediaType);
    }
}
