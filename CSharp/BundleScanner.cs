using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace MoenotesAssets;

// Enumerates paths actually present in one downloaded bundle. Catalog dependency
// edges are deliberately not used as a substitute for container membership.
public static class BundleScanner
{
    public static BundleContentItem[] Scan(WorkerJob job)
    {
        Config.Require(job.Inputs.Length == 1, "Scan requires one bundle");
        var input = job.Inputs[0];
        Config.Require(input.Location.Options != null, "Missing bundle options");
        var options = input.Location.Options!;
        if (input.Location.Provider == Catalog.Cri)
        {
            // CRI resources are raw payloads rather than UnityFS containers.
            var name = input.Location.Internal.Replace('\\', '/').Split('/').Last();
            Config.Require(name.Length is > 0 and <= 4096, "Invalid payload name");
            return [new(name, "payload", "", null)];
        }

        var manager = new AssetsManager();
        Directory.CreateDirectory(job.Output);
        try
        {
            var bundle = manager.LoadBundleFile(input.Path, false);
            var info = bundle.file;
            Config.Require(info.Header.Signature == "UnityFS" && info.Header.FileStreamHeader.TotalFileSize == new FileInfo(input.Path).Length, "UnityFS size mismatch");
            var expanded = info.BlockAndDirInfo.BlockInfos.Sum(b => (long)b.DecompressedSize);
            Config.Require(expanded <= job.Config.ExpandedBytes && info.BlockAndDirInfo.DirectoryInfos.Count <= 10000, "Bundle expansion budget");
            if (info.DataIsCompressed)
            {
                var unpacked = Path.Combine(job.Output, "unpacked.bundle");
                using (var writer = new AssetsFileWriter(File.Create(unpacked))) info.Unpack(writer);
                manager.UnloadBundleFile(bundle);
                bundle = manager.LoadBundleFile(unpacked, false); info = bundle.file;
            }
            var reader = info.DataReader; reader.Position = 0;
            var buffer = new byte[65536]; long remaining = expanded; uint crc = 0xffffffff;
            while (remaining > 0)
            {
                var n = reader.BaseStream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                Config.Require(n > 0, "Truncated bundle data"); crc = BinaryTools.Crc32(buffer.AsSpan(0, n), crc); remaining -= n;
            }
            Config.Require(options.Crc == 0 || ~crc == options.Crc, "Bundle CRC mismatch");

            var entries = new List<BundleContentItem>();
            for (var i = 0; i < info.BlockAndDirInfo.DirectoryInfos.Count; i++)
            {
                var directory = info.BlockAndDirInfo.DirectoryInfos[i];
                var name = directory.Name.Replace('\\', '/');
                Config.Require(name.Length is > 0 and <= 4096, "Invalid UnityFS entry name");
                entries.Add(new(name, "unity_file", "", null));
                if (!directory.IsSerialized) continue;
                var file = manager.LoadAssetsFileFromBundle(bundle, i, false);
                foreach (var asset in file.file.GetAssetsOfType(AssetClassID.AssetBundle))
                {
                    var data = file.file.Reader;
                    data.Position = asset.GetAbsoluteByteOffset(file.file);
                    var end = data.Position + asset.ByteSize;
                    _ = data.ReadCountStringInt32(); data.Align();
                    var preload = data.ReadInt32();
                    Config.Require(preload >= 0 && preload <= 1000000 && data.Position + preload * 12L <= end, "Preload reference budget");
                    data.Position += preload * 12L;
                    var count = data.ReadInt32();
                    Config.Require(count >= 0 && count <= 100000 && entries.Count + count <= 100000, "Container count budget");
                    for (var index = 0; index < count; index++)
                    {
                        var path = data.ReadCountStringInt32().Replace('\\', '/'); data.Align();
                        _ = data.ReadInt32(); _ = data.ReadInt32();
                        _ = data.ReadInt32(); var pathId = data.ReadInt64();
                        Config.Require(data.Position <= end && path.Length <= 4096, "Invalid container entry");
                        if (path.Length > 0) entries.Add(new(path, "asset", name, pathId));
                    }
                }
            }
            return entries.ToArray();
        }
        finally { manager.UnloadAll(true); }
    }
}
