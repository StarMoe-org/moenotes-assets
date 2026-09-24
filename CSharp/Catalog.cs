using System.Buffers.Binary;
using System.Text;
using static MoenotesAssets.Config;
namespace MoenotesAssets;

public sealed record BundleOptions(string Hash, string BundleName, uint Crc, long Size);
public sealed record Location(uint Id, string Key, string Internal, string Provider, string ResourceType, List<uint> Dependencies, BundleOptions? Options);
public sealed class Catalog
{
    public const string Crypt = "Fwk.Crypt.AssetBundleCryptProvider";
    public const string Plain = "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider";
    public const string Cri = "CriWare.Assets.CriResourceProvider";
    public SortedDictionary<string, List<uint>> Keys { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<uint, Location> Locations { get; set; } = new();
    private const uint Null = uint.MaxValue;
    private sealed class Reader(byte[] data)
    {
        private readonly Dictionary<(uint, char), string> strings = new();
        private int stringBytes;
        public ReadOnlySpan<byte> Take(uint offset, int length)
        {
            Require(length >= 0 && (long)offset + length <= data.Length, "Catalog out of bounds");
            return data.AsSpan((int)offset, length);
        }
        public uint U32(uint offset) => BinaryPrimitives.ReadUInt32LittleEndian(Take(offset, 4));
        public uint[] Array(uint offset, int stride)
        {
            if (offset == Null) return [];
            Require(offset >= 4, "Invalid array offset");
            var size = U32(offset - 4);
            Require(size % stride == 0 && size / stride <= 200000, "Catalog array limit");
            Take(offset, checked((int)size));
            return Enumerable.Range(0, (int)size / stride).Select(i => offset + (uint)(i * stride)).ToArray();
        }
        public string String(uint offset, char separator)
        {
            if (offset == Null) return "";
            if (strings.TryGetValue((offset, separator), out var cached)) return cached;
            string value;
            if ((offset & 0x40000000) != 0)
            {
                Require(separator != '\0', "Nested dynamic string");
                var parts = new List<string>();
                var seen = new HashSet<uint>();
                var length = 0;
                for (var p = offset; p != Null;)
                {
                    var at = p & 0x3fffffff;
                    Require(seen.Add(at) && seen.Count <= 4096, "Cyclic/long string");
                    var part = String(U32(at), '\0');
                    length += Encoding.UTF8.GetByteCount(part);
                    Require(length < 65536, "String too long");
                    parts.Add(part);
                    p = U32(at + 4);
                }
                parts.Reverse();
                value = string.Join(separator, parts);
            }
            else
            {
                var at = offset & 0x3fffffff;
                Require(at >= 4, "Invalid string offset");
                var size = U32(at - 4);
                Require(size <= 65536, "String limit");
                var bytes = Take(at, (int)size);
                if ((offset & 0x80000000) != 0)
                {
                    Require(size % 2 == 0, "Invalid UTF16 size");
                    value = new UnicodeEncoding(false, false, true).GetString(bytes);
                }
                else
                {
                    foreach (var b in bytes) Require(b < 128, "Invalid ASCII string");
                    value = Encoding.ASCII.GetString(bytes);
                }
            }
            stringBytes += Encoding.UTF8.GetByteCount(value);
            Require(stringBytes <= 64 << 20, "Catalog string budget");
            strings.Add((offset, separator), value);
            return value;
        }
        public string Type(uint offset) { Take(offset, 8); return String(U32(offset + 4), '.'); }
        public string? Key(uint offset)
        {
            Take(offset, 8);
            var kind = Type(U32(offset));
            var at = U32(offset + 4);
            if (kind == "System.String")
                return String(U32(at), (char)BinaryPrimitives.ReadUInt16LittleEndian(Take(at + 4, 2)));
            Require(kind is "System.Int32" or "System.Int64" or "System.Boolean", $"Unsupported key type {kind}");
            return null;
        }
        public Location Location(uint id)
        {
            Take(id, 28);
            var key = String(U32(id), '/');
            var internalId = String(U32(id + 4), '/');
            var provider = String(U32(id + 8), '.');
            var dependencies = Array(U32(id + 12), 4).Select(U32).ToList();
            var resourceType = Type(U32(id + 24));
            var extra = U32(id + 20);
            BundleOptions? options = null;
            if (extra != Null)
            {
                Take(extra, 8);
                Require(Type(U32(extra)) == "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleRequestOptions", "Unsupported options");
                var at = U32(extra + 4);
                Take(at, 20);
                options = new(Convert.ToHexStringLower(Take(U32(at), 16)), String(U32(at + 4), '_'), U32(at + 8), U32(at + 12));
            }
            Require(internalId.Length > 0 && provider.Length > 0, "Empty location");
            return new(id, key, internalId, provider, resourceType, dependencies, options);
        }
    }
    public static Catalog Parse(byte[] data)
    {
        Require(data.Length <= 32 << 20, "Catalog size limit");
        var reader = new Reader(data);
        reader.Take(0, 32);
        Require(reader.U32(0) == 0x0de38942 && reader.U32(4) == 2, "Unsupported catalog format");
        var catalog = new Catalog();
        var pending = new SortedSet<uint>();
        var references = 0;
        long materialized = 0;
        foreach (var at in reader.Array(reader.U32(8), 8))
        {
            var ids = reader.Array(reader.U32(at + 4), 4).Select(reader.U32).ToList();
            references += ids.Count;
            Require(references <= 1000000, "Catalog reference budget");
            pending.UnionWith(ids);
            var key = reader.Key(reader.U32(at));
            if (key == null) continue;
            materialized += key.Length * 2L + ids.Count * 4L;
            Require(materialized <= 128 << 20, "Catalog materialization budget");
            Require(catalog.Keys.TryAdd(key, ids), "Duplicate catalog key");
        }
        var edges = 0;
        while (pending.Count > 0)
        {
            var id = pending.Min;
            pending.Remove(id);
            if (catalog.Locations.ContainsKey(id)) continue;
            Require(catalog.Locations.Count < 200000, "Location limit");
            var location = reader.Location(id);
            edges += location.Dependencies.Count;
            materialized += (location.Key.Length + location.Internal.Length + location.Provider.Length + location.ResourceType.Length) * 2L + location.Dependencies.Count * 4L;
            Require(edges <= 1000000 && materialized <= 128 << 20, "Catalog dependency budget");
            pending.UnionWith(location.Dependencies.Where(d => !catalog.Locations.ContainsKey(d)));
            catalog.Locations.Add(id, location);
        }
        return catalog;
    }
    public Location Target(string key)
    {
        Require(Keys.TryGetValue(key, out var ids) && ids.Count > 0, "Asset key not found");
        var first = Locations[ids![0]];
        foreach (var id in ids)
        {
            var other = Locations[id];
            Require(other.Internal == first.Internal && other.Provider == first.Provider && other.Dependencies.SequenceEqual(first.Dependencies), "Ambiguous asset key");
        }
        return first;
    }
    public Location[] Closure(string key)
    {
        var todo = new Stack<uint>([Target(key).Id]);
        var seen = new HashSet<uint>();
        var output = new List<Location>();
        while (todo.TryPop(out var id))
        {
            if (!seen.Add(id)) continue;
            var location = Locations[id];
            foreach (var dependency in location.Dependencies) todo.Push(dependency);
            if (location.Options == null) continue;
            Require(location.Provider is Crypt or Plain or Cri, "Unsupported provider");
            output.Add(location);
        }
        Require(output.Count > 0, "No downloadable dependencies");
        return output.OrderBy(l => l.Id).ToArray();
    }
}
