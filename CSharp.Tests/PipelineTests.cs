using MoenotesAssets;
using Xunit;
using VGAudio.Codecs.CriHca;
using VGAudio.Containers.Hca;
using VGAudio.Formats.Pcm16;
namespace MoenotesAssets.Tests;

public class PipelineTests
{
    [Fact]
    public void CtrChunkBoundaries()
    {
        var raw = Enumerable.Repeat((byte)7, 17000).ToArray(); var all = (byte[])raw.Clone(); Crypto.Decrypt(all, "a.bundle", 0);
        foreach (var offset in new[] { 0, 1, 15, 16, 16380, 16384 }) { var part = raw.AsSpan(offset, 30).ToArray(); Crypto.Decrypt(part, "a.bundle", offset); Assert.Equal(all.AsSpan(offset, 30).ToArray(), part); }
        Crypto.Decrypt(all, "a.bundle", 0); Assert.Equal(raw, all);
    }
    [Fact]
    public void UrlAndTomlBoundaries()
    {
        var c = new Config { CdnRoot = "https://cdn.invalid/prod" }; c.Validate(); Assert.Equal("https://cdn.invalid/prod/asset/Android/a", c.AssetUri("https://dummy.net/asset/Android/a").AbsoluteUri);
        foreach (var id in new[] { "file:///a", "https://dummy.net/asset/Android/../x", "https://dummy.net/asset/Android/%2e%2e/x", "https://dummy.net/asset/Android/a?x=1" }) Assert.Throws<InvalidDataException>(() => c.AssetUri(id));
        using var dir = new TempDirectory(); var path = Path.Combine(dir.Path, "config.toml"); File.WriteAllText(path, "cdn_root = \"https://cdn.invalid/prod\" # comment\nworkers = 3\ncri_key = 8594927479\n"); Assert.Equal(3, Config.Load(path).Workers);
        File.AppendAllText(path, "typo = 2\n"); Assert.Throws<System.Text.Json.JsonException>(() => Config.Load(path));
    }
    [Fact]
    public void CatalogAndMutations()
    {
        var (bytes, _) = Fixture.Create(); var cat = Catalog.Parse(bytes); Assert.Equal(Fixture.Internal, cat.Target(Fixture.Key).Internal); Assert.Single(cat.Closure(Fixture.Key));
        for (var length = 0; length < 32; length++) Assert.ThrowsAny<Exception>(() => Catalog.Parse(bytes[..length]));
        for (var index = 0; index < bytes.Length; index += 3) { var corrupt = (byte[])bytes.Clone(); corrupt[index] = 255; try { Catalog.Parse(corrupt); } catch (Exception e) { Assert.True(e is InvalidDataException or System.Text.DecoderFallbackException or OverflowException, e.ToString()); } }
    }
    [Fact]
    public async Task UnityTextThroughWorkerAndCrc()
    {
        using var dir = new TempDirectory(); var (bytes, encrypted) = Fixture.Create(); var catalog = Catalog.Parse(bytes); var location = catalog.Closure(Fixture.Key)[0]; Crypto.Decrypt(encrypted, "fixture.bundle", 0);
        var path = Path.Combine(dir.Path, "fixture.bundle"); File.WriteAllBytes(path, encrypted); var job = new WorkerJob(new Config { CdnRoot = "https://cdn.invalid" }, catalog.Target(Fixture.Key), [new(location, path)], Path.Combine(dir.Path, "out"));
        var artifacts = await Processes.Worker(job, dir.Path, CancellationToken.None); Assert.Single(artifacts); Assert.Equal("application/json", artifacts[0].MediaType); Assert.Equal(Fixture.Body, File.ReadAllBytes(Path.Combine(job.Output, artifacts[0].Name)));
        var bad = location with { Options = location.Options! with { Crc = location.Options!.Crc ^ 1 } }; var error = await Assert.ThrowsAsync<InvalidDataException>(() => Processes.Worker(job with { Inputs = [new(bad, path)], Output = Path.Combine(dir.Path, "bad") }, dir.Path, CancellationToken.None)); Assert.Contains("CRC", error.Message);
    }
    [Fact]
    public void HcaEncryptionAndWrongKey()
    {
        using var dir = new TempDirectory(); var samples = Enumerable.Range(0, 4800).Select(i => (short)(Math.Sin(i * 0.08) * 6000)).ToArray(); var pcm = new Pcm16FormatBuilder([samples], 48000).Build(); var config = new Config { CdnRoot = "https://cdn.invalid" };
        var bytes = new HcaWriter().GetFile(pcm, new HcaConfiguration { EncryptionKey = new CriHcaKey(config.CriKey) });
        var path = Path.Combine(dir.Path, "wave.wav"); Assert.Equal(1, CriMedia.DecodeHca(bytes, 0, config, path)); Assert.True(new FileInfo(path).Length > 9000);
        Assert.ThrowsAny<Exception>(() => CriMedia.DecodeHca(bytes, 0, config with { CriKey = config.CriKey + 1 }, Path.Combine(dir.Path, "bad.wav")));
    }
    [Fact]
    public async Task SharedCancellationKeepsOtherConsumer()
    {
        var shared = new SharedWork<string>(); using var cancel = new CancellationTokenSource(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        async Task<string> Produce(CancellationToken token) { Interlocked.Increment(ref calls); started.SetResult(); await finish.Task.WaitAsync(token); return "result"; }
        var one = shared.Join("key", Produce, cancel.Token); await started.Task; var two = shared.Join("key", Produce, CancellationToken.None); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await one); finish.SetResult(); using var lease = await two; Assert.Equal("result", lease.Value); Assert.Equal(1, calls);
    }
}
