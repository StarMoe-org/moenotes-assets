using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoenotesAssets;
using VGAudio.Codecs.CriHca;
using VGAudio.Containers.Adx;
using VGAudio.Containers.Hca;
using VGAudio.Formats.Pcm16;
using Xunit;
namespace MoenotesAssets.Tests;

public class MediaTests
{
    [Fact]
    public async Task UsmAlphaVideoIsRecordedAsSkipped()
    {
        using var dir = new TempDirectory(); var config = new Config { CdnRoot = "https://cdn.invalid" };
        // Demux stops at the @ALP chunk, so the video payload never reaches FFmpeg.
        var usm = CriFixture.Usm(new byte[0x300], config.CriKey, alpha: true);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var cdn = builder.Build(); cdn.MapGet("/asset/Android/fixture.bundle", () => Results.Bytes(usm)); await cdn.StartAsync();
        var url = cdn.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        await using var service = new AssetService(config with { DataDir = Path.Combine(dir.Path, "data"), CdnRoot = url, AllowLoopbackHttp = true });
        var catalog = Fixture.Catalog(usm.Length, 0, Catalog.Cri, "CriWare.Assets.CriManaUsmAsset"); var digest = Crypto.Sha256(catalog);
        File.WriteAllBytes(Path.Combine(dir.Path, "data", "catalogs", digest + ".bin"), catalog);
        var snapshot = new Snapshot("snapshot", digest, "hk", "zh-Hant", "main", url, "", AssetService.Now); service.Store.IndexSnapshot(snapshot, Catalog.Parse(catalog));
        var task = await service.Wait(service.StartExport(new(Keys: [Fixture.Key], Snapshot: snapshot.Id)).Id).WaitAsync(TimeSpan.FromSeconds(60));
        // Skips are counted, not listed.
        Assert.Equal("succeeded", task.State); Assert.Equal(1, task.Skipped); Assert.Equal(1, task.Completed); Assert.Empty(task.Results);
        await cdn.StopAsync();
    }
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
        File.WriteAllBytes(path, CriFixture.Acb(hca, blocks: true));
        var blocks = await Processes.Worker(job with { Output = Path.Combine(dir.Path, "blocks") }, dir.Path, CancellationToken.None); Assert.Single(blocks); Assert.Equal("synthetic-tone", blocks[0].Label);
    }
    // Some movies are written without the CRI mask (plain MPEG, as MemberCard previews) or without audio_codec in the
    // audio header (ADX identified by its magic).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UsmMaskAndAdxAreDetectedFromTheStreams(bool plain)
    {
        using var dir = new TempDirectory(); var config = new Config { CdnRoot = "https://cdn.invalid" }; var raw = Path.Combine(dir.Path, "source.m2v");
        await Processes.Run(config.Ffmpeg, ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=64x48:rate=25", "-t", "0.4", "-c:v", "mpeg2video", "-f", "mpeg2video", raw], CancellationToken.None);
        var pcm = new[] { Enumerable.Range(0, 19200).Select(i => (short)(Math.Sin(i * 0.05) * 6000)).ToArray() };
        var adx = new AdxWriter().GetFile(new Pcm16FormatBuilder(pcm, 48000).Build());
        var path = Path.Combine(dir.Path, "movie.usm"); File.WriteAllBytes(path, CriFixture.Usm(File.ReadAllBytes(raw), config.CriKey, 10, 25, 1, adx, plain: plain, audioCodec: false));
        var job = Job(config, path, Path.Combine(dir.Path, "out"), "CriWare.Assets.CriManaUsmAsset");
        var result = await Processes.Worker(job, dir.Path, CancellationToken.None); Assert.Single(result);
        var streams = (await CriMedia.Probe(config, Path.Combine(job.Output, result[0].Name), CancellationToken.None))["streams"]!.AsArray();
        Assert.Equal("10", (string?)streams.Single(s => (string?)s!["codec_type"] == "video")!["nb_read_frames"]);
        Assert.Equal("aac", (string?)streams.Single(s => (string?)s!["codec_type"] == "audio")!["codec_name"]);
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
    // Both lengths end in a final read FFmpeg's ADX demuxer rejects with -xerror: frame-aligned
    // for mono, and only the 18-byte end frame for stereo with a multiple of 128 frames.
    [Theory]
    [InlineData(1, 48000, 19200)]
    [InlineData(2, 32000, 12288)]
    public async Task UsmAdxAudioIsDecodedWithoutFfmpegDemuxer(int channels, int sampleRate, int samples)
    {
        using var dir = new TempDirectory(); var config = new Config { CdnRoot = "https://cdn.invalid" }; var raw = Path.Combine(dir.Path, "source.ivf");
        await Processes.Run(config.Ffmpeg, ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=25", "-t", "0.4", "-pix_fmt", "yuv420p", "-c:v", "libvpx-vp9", "-f", "ivf", raw], CancellationToken.None);
        var pcm = Enumerable.Range(0, channels).Select(c => Enumerable.Range(0, samples).Select(i => (short)(Math.Sin(i * 0.05 * (c + 1)) * 6000)).ToArray()).ToArray();
        var adx = new AdxWriter().GetFile(new Pcm16FormatBuilder(pcm, sampleRate).Build());
        var path = Path.Combine(dir.Path, "movie.usm"); File.WriteAllBytes(path, CriFixture.Usm(File.ReadAllBytes(raw), config.CriKey, 10, 25, 9, adx));
        var job = Job(config, path, Path.Combine(dir.Path, "out"), "CriWare.Assets.CriManaUsmAsset");
        var result = await Processes.Worker(job, dir.Path, CancellationToken.None); Assert.Single(result);
        var streams = (await CriMedia.Probe(config, Path.Combine(job.Output, result[0].Name), CancellationToken.None))["streams"]!.AsArray();
        var audio = streams.Single(s => (string?)s!["codec_type"] == "audio")!;
        Assert.Equal("aac", (string?)audio["codec_name"]);
        Assert.Equal(channels, (int?)audio["channels"]);
        Assert.Equal(sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)audio["sample_rate"]);
    }
}
