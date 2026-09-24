using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using System.Runtime.InteropServices;
using static MoenotesAssets.Config;
namespace MoenotesAssets;

public static class Worker
{
    // The C# implementation has its own identity: codec/library changes can change output bytes.
    public const string Profile = "csharp-json-png-webp-aac-h264-v3";
    // Movies have their own identity so a video codec change leaves image/text/audio exports reusable.
    public const string MovieProfile = "csharp-usm-mp4-vp9copy-h264-aac-v2";
    public static string ProfileFor(Location target) => target.ResourceType == "CriWare.Assets.CriManaUsmAsset" ? MovieProfile : Profile;
    // Full songs: a MonoBehaviour listing TextAsset chunks that together form one ACB.
    public const string SplitAcbType = "Fwk.Sound.SplitAcbData";
    public sealed class Output(WorkerJob job)
    {
        public WorkerJob Job { get; } = job;
        public List<Artifact> Files { get; } = [];
        public List<string> EmbeddedMedia { get; } = [];
        private long total;
        public string PathFor(string extension) => Path.Combine(Job.Output, $"{Files.Count:D5}.{extension}");
        public void Add(string path, string label, string mime, object? metadata = null)
        {
            var size = new FileInfo(path).Length; total += size;
            Require(total <= Job.Config.OutputBytes && Files.Count < 10000, "Output budget");
            using var stream = File.OpenRead(path);
            Files.Add(new(Path.GetFileName(path), label, mime, size, Convert.ToHexStringLower(SHA256.HashData(stream)), metadata));
        }
        public void Bytes(byte[] data, string extension, string label, string mime)
        {
            Require(data.LongLength <= Job.Config.OutputBytes - total, "Output budget");
            var path = PathFor(extension); File.WriteAllBytes(path, data); Add(path, label, mime);
        }
    }
    public static async Task<Artifact[]> Run(WorkerJob job, CancellationToken token = default)
    {
        job.Config.Validate(); Require(job.Inputs.Length > 0 && job.Inputs.Select(i => i.Location.Id).Distinct().Count() == job.Inputs.Length, "Invalid worker inputs");
        Directory.CreateDirectory(job.Output); var output = new Output(job);
        if (job.Target.Provider == Catalog.Cri || (job.Target.ResourceType.StartsWith("CriWare.", StringComparison.Ordinal) && job.Inputs.Any(i => i.Location.Provider == Catalog.Cri)))
        {
            var inputs = job.Inputs.Where(i => i.Location.Provider == Catalog.Cri).ToArray();
            Require(inputs.Length == 1, "Ambiguous CRI dependencies");
            await CriMedia.Export(inputs[0].Path, output, token);
        }
        else Unity(job, output);
        foreach (var path in output.EmbeddedMedia) await CriMedia.Export(path, output, token);
        Require(output.Files.Count > 0, "No supported outputs"); return output.Files.ToArray();
    }
    private static void Unity(WorkerJob job, Output output)
    {
        var manager = new AssetsManager();
        try
        {
            var classData = job.Config.ClassData;
            if (classData.Length == 0)
            {
                using var stream = typeof(Worker).Assembly.GetManifestResourceStream("MoenotesAssets.Resources.classdata.tpk")!;
                manager.LoadClassPackage(stream);
            }
            else manager.LoadClassPackage(classData);
            var files = new List<AssetsFileInstance>(); long expanded = 0;
            foreach (var input in job.Inputs.OrderBy(i => i.Location.Id))
            {
                Require(input.Location.Provider != Catalog.Cri, "Mixed providers");
                var bundle = manager.LoadBundleFile(input.Path, false);
                var info = bundle.file;
                Require(info.Header.Signature == "UnityFS" && info.Header.FileStreamHeader.TotalFileSize == new FileInfo(input.Path).Length, "UnityFS size mismatch");
                var size = info.BlockAndDirInfo.BlockInfos.Sum(b => (long)b.DecompressedSize);
                expanded += size; Require(expanded <= job.Config.ExpandedBytes && info.BlockAndDirInfo.DirectoryInfos.Count <= 10000, "Bundle expansion budget");
                if (info.DataIsCompressed)
                {
                    var unpacked = Path.Combine(Path.GetDirectoryName(job.Output)!, $"unpacked-{input.Location.Id}.bundle");
                    using (var writer = new AssetsFileWriter(File.Create(unpacked))) info.Unpack(writer);
                    manager.UnloadBundleFile(bundle); bundle = manager.LoadBundleFile(unpacked, false); info = bundle.file;
                }
                var reader = info.DataReader; reader.Position = 0; var buffer = new byte[65536]; long remaining = size; uint crc = 0xffffffff;
                while (remaining > 0)
                {
                    var n = reader.BaseStream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    Require(n > 0, "Truncated bundle data"); crc = BinaryTools.Crc32(buffer.AsSpan(0, n), crc); remaining -= n;
                }
                Require(input.Location.Options?.Crc is null or 0 || ~crc == input.Location.Options.Crc, "Bundle CRC mismatch");
                for (var i = 0; i < info.BlockAndDirInfo.DirectoryInfos.Count; i++)
                    // Raw .resS pixels can accidentally resemble a serialized-file
                    // header. UnityFS explicitly marks serialized entries with bit 4.
                    if (info.BlockAndDirInfo.DirectoryInfos[i].IsSerialized)
                        files.Add(manager.LoadAssetsFileFromBundle(bundle, i, false));
            }
            Require(files.Count <= 10000 && files.Sum(f => (long)f.file.Metadata.AssetInfos.Count) <= 1000000, "Unity metadata budget");
            Require(files.Select(f => f.file.Metadata.UnityVersion).Distinct().Count() == 1, "Mixed Unity versions are unsupported");
            var targets = new List<AssetExternal>(); var seen = new HashSet<(string, long)>();
            foreach (var file in files)
            {
                manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
                foreach (var asset in file.file.GetAssetsOfType(AssetClassID.AssetBundle))
                {
                    var reader = file.file.Reader;
                    reader.Position = asset.GetAbsoluteByteOffset(file.file);
                    var end = reader.Position + asset.ByteSize;
                    _ = reader.ReadCountStringInt32(); reader.Align();
                    var preload = reader.ReadInt32(); Require(preload >= 0 && preload <= 1000000 && reader.Position + preload * 12L <= end, "Preload reference budget");
                    reader.Position += preload * 12L;
                    var count = reader.ReadInt32(); Require(count >= 0 && count <= 100000, "Container count budget");
                    var references = new List<(int, long)>();
                    for (var index = 0; index < count; index++)
                    {
                        var path = reader.ReadCountStringInt32(); reader.Align();
                        _ = reader.ReadInt32(); _ = reader.ReadInt32();
                        var fileId = reader.ReadInt32(); var pathId = reader.ReadInt64();
                        Require(reader.Position <= end, "Container exceeds object");
                        if (path == job.Target.Internal) references.Add((fileId, pathId));
                    }
                    foreach (var (fileId, pathId) in references)
                    {
                        var resolved = Resolve(manager, file, fileId, pathId);
                        if (seen.Add((resolved.file.path, resolved.info.PathId))) targets.Add(resolved);
                    }
                }
            }
            Require(targets.Count > 0, "Target InternalId not found in bundle containers");
            foreach (var target in targets.OrderBy(t => files.IndexOf(t.file)).ThenBy(t => t.info.PathId))
                ExportObject(manager, target, output, new HashSet<(string, long)>());
        }
        finally { manager.UnloadAll(true); }
    }
    private static AssetExternal Resolve(AssetsManager manager, AssetsFileInstance file, AssetTypeValueField pointer)
    {
        return Resolve(manager, file, pointer["m_FileID"].AsInt, pointer["m_PathID"].AsLong);
    }
    private static AssetExternal Resolve(AssetsManager manager, AssetsFileInstance file, int fileId, long pathId)
    {
        AssetsFileInstance source = file;
        if (fileId != 0)
        {
            Require(fileId > 0 && fileId <= file.file.Metadata.Externals.Count, "Invalid external reference");
            var name = Path.GetFileName(file.file.Metadata.Externals[fileId - 1].PathName.Replace('\\', '/'));
            var matches = manager.Files.Where(f => f.name == name).ToArray();
            Require(matches.Length == 1, "Missing or ambiguous bundle dependency"); source = matches[0];
        }
        var info = source.file.GetAssetInfo(pathId); Require(info != null, "Null or missing object reference");
        manager.LoadClassDatabaseFromPackage(source.file.Metadata.UnityVersion);
        return new AssetExternal { file = source, info = info, baseField = info!.TypeId == 49 ? null : manager.GetBaseField(source, info) };
    }
    private static void ExportObject(AssetsManager manager, AssetExternal asset, Output output, HashSet<(string, long)> seen)
    {
        Require(seen.Count < 10000 && seen.Add((asset.file.path, asset.info.PathId)), "Cyclic/duplicate atlas reference");
        var field = asset.baseField; var label = field == null || field["m_Name"].IsDummy ? "unnamed" : field["m_Name"].AsString;
        switch ((AssetClassID)asset.info.TypeId)
        {
            case AssetClassID.TextAsset:
                (label, var raw) = TextAssetBytes(asset, Math.Min(output.Job.Config.ExpandedBytes, 64L << 20));
                var data = BinaryTools.DecodeText(raw, Math.Min(output.Job.Config.ExpandedBytes, 64L << 20));
                string ext = "bin", mime = "application/octet-stream";
                try { using var document = JsonDocument.Parse(data); ext = "json"; mime = "application/json"; }
                catch (JsonException)
                {
                    try { var text = new UTF8Encoding(false, true).GetString(data); ext = text.StartsWith("#TITLE ", StringComparison.Ordinal) ? "sus" : "txt"; mime = "text/plain; charset=utf-8"; }
                    catch (DecoderFallbackException) { }
                }
                output.Bytes(data, ext, label, mime); break;
            case AssetClassID.Texture2D:
                var texture = Texture(asset, output.Job.Config); WriteImage(output, label, texture.Pixels, texture.Width, texture.Height); break;
            case AssetClassID.Sprite:
                ExportSprite(manager, asset, output, label); break;
            case AssetClassID.SpriteAtlas:
                foreach (var pointer in field!["m_PackedSprites"]["Array"].Children) ExportObject(manager, Resolve(manager, asset.file, pointer), output, seen);
                break;
            case AssetClassID.MonoBehaviour when output.Job.Target.ResourceType.StartsWith("CriWare.", StringComparison.Ordinal):
                var bytes = EmbeddedCriBytes(field!, output.Job.Config.InputBytes);
                var embedded = Path.Combine(Path.GetDirectoryName(output.Job.Output)!, $"embedded-cri-{output.EmbeddedMedia.Count}.bin");
                File.WriteAllBytes(embedded, bytes); output.EmbeddedMedia.Add(embedded); break;
            case AssetClassID.MonoBehaviour when output.Job.Target.ResourceType == SplitAcbType:
                var chunks = field!["_chunks"]["Array"].Children.Select(pointer =>
                {
                    var chunk = Resolve(manager, asset.file, pointer);
                    Require(chunk.info.TypeId == (int)AssetClassID.TextAsset, "Split ACB chunk is not a TextAsset");
                    return TextAssetBytes(chunk, output.Job.Config.InputBytes).Bytes;
                });
                var acb = JoinSplitAcb(chunks, output.Job.Config.InputBytes);
                var joined = Path.Combine(Path.GetDirectoryName(output.Job.Output)!, $"embedded-cri-{output.EmbeddedMedia.Count}.bin");
                File.WriteAllBytes(joined, acb); output.EmbeddedMedia.Add(joined); break;
            default: throw new InvalidDataException($"Unsupported Unity class {asset.info.TypeId}");
        }
    }
    // Read bytes directly because a TextAsset's m_Script may contain binary or gzip data.
    private static (string Name, byte[] Bytes) TextAssetBytes(AssetExternal asset, long limit)
    {
        var reader = asset.file.file.Reader; var start = asset.info.GetAbsoluteByteOffset(asset.file.file); reader.Position = start;
        var name = reader.ReadCountStringInt32(); reader.Align();
        var length = reader.ReadInt32();
        Require(length >= 0 && reader.Position + length <= start + asset.info.ByteSize && length <= limit, "Text expansion limit");
        return (name, reader.ReadBytes(length));
    }
    /// <summary>
    /// Matches the game's SplitAcbLoader: chunks concatenated in serialized order, every byte XORed
    /// with 0x5A (an asset-format obfuscation, like cri_key). The result must hold one @UTF table.
    /// </summary>
    public static byte[] JoinSplitAcb(IEnumerable<byte[]> chunks, long limit)
    {
        const byte obfuscation = 0x5A;
        using var joined = new MemoryStream(); var count = 0;
        foreach (var chunk in chunks)
        {
            Require(chunk.Length > 0, "Empty split ACB chunk");
            Require(++count <= 10000 && joined.Length + chunk.LongLength <= limit, "Split ACB input budget");
            joined.Write(chunk);
        }
        var bytes = joined.ToArray();
        for (var i = 0; i < bytes.Length; i++) bytes[i] ^= obfuscation;
        Require(bytes.Length >= 32 && bytes.AsSpan().StartsWith("@UTF"u8), "Split ACB chunks do not form an ACB");
        var table = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)) + 8L;
        Require(table >= 32 && table <= bytes.Length, "Split ACB table truncated");
        return bytes;
    }
    public static byte[] EmbeddedCriBytes(AssetTypeValueField field, long limit)
    {
        Require(!field["implementation"].IsDummy && !field["references"].IsDummy, "Unsupported CRI asset implementation");
        var rid = field["implementation"]["rid"].AsLong;
        var matches = field["references"].AsManagedReferencesRegistry.references.Where(r => r.rid == rid && r.type.ClassName == "CriSerializedBytesAssetImpl").ToArray();
        Require(matches.Length == 1, "Missing or ambiguous embedded CRI implementation");
        var bytes = matches[0].data["data"]["Array"].AsByteArray;
        Require(bytes is { Length: >= 4 } && bytes.LongLength <= limit, "Embedded CRI input budget");
        Require(bytes.AsSpan().StartsWith("@UTF"u8) || bytes.AsSpan().StartsWith("CRID"u8), "Unsupported embedded CRI container");
        return bytes;
    }
    private static (byte[] Pixels, int Width, int Height) Texture(AssetExternal asset, Config config)
    {
        var texture = TextureFile.ReadTextureFile(asset.baseField);
        var width = texture.m_Width; var height = texture.m_Height;
        Require(width is > 0 and <= 16384 && height is > 0 and <= 16384 && (long)width * height * 4 <= Math.Min(config.OutputBytes, 512L << 20), "Texture dimensions budget");
        if (texture.pictureData == null || texture.pictureData.Length == 0)
        {
            Require(texture.m_StreamData.size <= config.ExpandedBytes && asset.file.parentBundle != null, "Missing texture stream");
            Require(texture.SetPictureDataFromBundle(asset.file.parentBundle), "Texture stream not found in bundle");
        }
        var pixels = texture.DecodeTextureRaw(texture.pictureData, false);
        Require(pixels != null && pixels.Length == checked(width * height * 4), "Unsupported texture format");
        return (pixels!, width, height);
    }
    private static void WriteImage(Output output, string label, byte[] pixels, int width, int height)
    {
        // Unity stores the first row at the bottom; PNG stores it at the top.
        var flipped = new byte[pixels.Length];
        for (int y = 0; y < height; y++) Buffer.BlockCopy(pixels, y * width * 4, flipped, (height - 1 - y) * width * 4, width * 4);
        var path = output.PathFor("png"); BinaryTools.Png(path, flipped, width, height);
        output.Add(path, label, "image/png", new { width, height });
        var pinned = GCHandle.Alloc(flipped, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul), pinned.AddrOfPinnedObject());
            using var encoded = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 50));
            Require(encoded != null, "WebP encoding failed");
            var webp = output.PathFor("webp");
            using (var stream = File.Create(webp)) encoded!.SaveTo(stream);
            output.Add(webp, label, "image/webp", new { width, height, lossless = true });
        }
        finally { pinned.Free(); }
    }
    private static void ExportSprite(AssetsManager manager, AssetExternal asset, Output output, string label)
    {
        var data = asset.baseField["m_RD"];
        var atlasPointer = asset.baseField["m_SpriteAtlas"];
        if (!atlasPointer.IsDummy && atlasPointer["m_PathID"].AsLong != 0)
        {
            var atlas = Resolve(manager, asset.file, atlasPointer);
            var key = asset.baseField["m_RenderDataKey"];
            var matching = atlas.baseField["m_RenderDataMap"]["Array"].Children.Where(p => EqualField(p["first"], key)).ToArray();
            Require(matching.Length == 1, "Sprite atlas render data missing"); data = matching[0]["second"];
            asset = new AssetExternal { file = atlas.file, info = asset.info, baseField = asset.baseField };
        }
        var tex = Texture(Resolve(manager, asset.file, data["texture"]), output.Job.Config);
        var alphaPointer = data["alphaTexture"];
        if (!alphaPointer.IsDummy && alphaPointer["m_PathID"].AsLong != 0)
        {
            var alpha = Texture(Resolve(manager, asset.file, alphaPointer), output.Job.Config);
            Require(alpha.Width == tex.Width && alpha.Height == tex.Height, "Split alpha dimensions mismatch");
            for (var i = 0; i < tex.Pixels.Length; i += 4) tex.Pixels[i + 3] = alpha.Pixels[i];
        }
        var downscale = data["downscaleMultiplier"].IsDummy ? 1 : data["downscaleMultiplier"].AsFloat;
        tex = SpriteGeometry.Resize(tex.Pixels, tex.Width, tex.Height, downscale, Math.Min(output.Job.Config.OutputBytes, 512L << 20));
        var rect = data["textureRect"];
        int x = (int)Math.Floor(rect["x"].AsFloat), y = (int)Math.Floor(rect["y"].AsFloat), width = (int)Math.Ceiling(rect["x"].AsFloat + rect["width"].AsFloat) - x, height = (int)Math.Ceiling(rect["y"].AsFloat + rect["height"].AsFloat) - y;
        Require(x >= 0 && y >= 0 && width > 0 && height > 0 && x + width <= tex.Width && y + height <= tex.Height, "Sprite rectangle outside texture");
        var pixels = new byte[checked(width * height * 4)];
        for (var row = 0; row < height; row++) Buffer.BlockCopy(tex.Pixels, ((y + row) * tex.Width + x) * 4, pixels, row * width * 4, width * 4);
        var settings = data["settingsRaw"].AsUInt; var rotation = (settings >> 2) & 15;
        if ((settings & 1) != 0 && rotation != 0)
        {
            var transformed = new byte[pixels.Length]; int newWidth = rotation == 4 ? height : width, newHeight = rotation == 4 ? width : height;
            Require(rotation <= 4, "Unsupported sprite rotation");
            for (int row = 0; row < height; row++) for (int col = 0; col < width; col++)
            {
                var (dx, dy) = rotation switch { 1 => (width - 1 - col, row), 2 => (col, height - 1 - row), 3 => (width - 1 - col, height - 1 - row), 4 => (row, width - 1 - col), _ => (col, row) };
                Buffer.BlockCopy(pixels, (row * width + col) * 4, transformed, (dy * newWidth + dx) * 4, 4);
            }
            pixels = transformed; width = newWidth; height = newHeight;
        }
        if (((settings >> 1) & 1) == 0) SpriteGeometry.Mask(asset.baseField, data, pixels, width, height);
        WriteImage(output, label, pixels, width, height);
    }
    private static bool EqualField(AssetTypeValueField a, AssetTypeValueField b)
    {
        if (a.Children.Count != b.Children.Count) return false;
        if (a.Children.Count == 0) return Equals(a.AsObject, b.AsObject);
        return a.Children.Zip(b.Children).All(p => EqualField(p.First, p.Second));
    }
}
