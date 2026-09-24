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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EncryptedUsmToSilentH264(bool containerTiming)
    {
        using var dir = new TempDirectory(); var config = new Config { CdnRoot = "https://cdn.invalid" }; var raw = Path.Combine(dir.Path, "source.m2v");
        await Processes.Run(config.Ffmpeg, ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=64x48:rate=25", "-t", "0.4", "-c:v", "mpeg2video", "-f", "mpeg2video", raw], CancellationToken.None);
        var path = Path.Combine(dir.Path, "movie.usm"); File.WriteAllBytes(path, CriFixture.Usm(File.ReadAllBytes(raw), config.CriKey, containerTiming ? 10 : 0, containerTiming ? 50 : 25));
        var job = Job(config, path, Path.Combine(dir.Path, "out"), "CriWare.Assets.CriManaUsmAsset");
        var result = await Processes.Worker(job, dir.Path, CancellationToken.None); Assert.Single(result); Assert.Equal("video/mp4", result[0].MediaType);
        if (containerTiming)
        {
            var probe = await CriMedia.Probe(config, Path.Combine(job.Output, result[0].Name), CancellationToken.None);
            Assert.Equal("50/1", (string?)probe["streams"]![0]!["r_frame_rate"]);
            File.WriteAllBytes(path, CriFixture.Usm(File.ReadAllBytes(raw), config.CriKey, 11, 50));
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => Processes.Worker(job with { Output = Path.Combine(dir.Path, "wrong-frames") }, dir.Path, CancellationToken.None));
            Assert.Contains("source frame count mismatch", exception.Message);
        }
    }
    [Theory]
    [InlineData(25, false)]
    [InlineData(50, true)]
    public async Task UsmVp9IsCopiedUnlessHeaderTimingDiffers(int headerRate, bool reencoded)
    {
        using var dir = new TempDirectory(); var config = new Config { CdnRoot = "https://cdn.invalid" }; var raw = Path.Combine(dir.Path, "source.ivf");
        await Processes.Run(config.Ffmpeg, ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=63x47:rate=25", "-t", "0.4", "-pix_fmt", "yuv420p", "-c:v", "libvpx-vp9", "-f", "ivf", raw], CancellationToken.None);
        var path = Path.Combine(dir.Path, "movie.usm"); File.WriteAllBytes(path, CriFixture.Usm(File.ReadAllBytes(raw), config.CriKey, 10, headerRate, 9));
        var job = Job(config, path, Path.Combine(dir.Path, "out"), "CriWare.Assets.CriManaUsmAsset");
        var result = await Processes.Worker(job, dir.Path, CancellationToken.None); Assert.Single(result); Assert.Equal("video/mp4", result[0].MediaType);
        var stream = (await CriMedia.Probe(config, Path.Combine(job.Output, result[0].Name), CancellationToken.None))["streams"]![0]!;
        Assert.Equal(reencoded ? "h264" : "vp9", (string?)stream["codec_name"]);
        Assert.Equal($"{headerRate}/1", (string?)stream["r_frame_rate"]);
        Assert.Equal(reencoded ? 64 : 63, (int?)stream["width"]);
        Assert.Equal("10", (string?)stream["nb_read_frames"]);
    }
}
