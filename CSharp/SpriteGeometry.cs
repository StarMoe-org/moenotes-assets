using AssetsTools.NET;
using System.Buffers.Binary;
using System.Numerics;
using static MoenotesAssets.Config;
namespace MoenotesAssets;

public static class SpriteGeometry
{
    public static void Mask(AssetTypeValueField sprite, AssetTypeValueField renderData, byte[] pixels, int width, int height)
    {
        var rd = sprite["m_RD"]; var vertices = new List<Vector2>(); var triangles = new List<(int, int, int)>();
        if (!rd["vertices"].IsDummy)
        {
            foreach (var v in rd["vertices"]["Array"].Children) vertices.Add(new(v["pos"]["x"].AsFloat, v["pos"]["y"].AsFloat));
            var indices = rd["indices"]["Array"].Children; Require(indices.Count % 3 == 0, "Sprite triangle indices");
            for (int i = 0; i < indices.Count; i += 3) triangles.Add((indices[i].AsInt, indices[i + 1].AsInt, indices[i + 2].AsInt));
        }
        else
        {
            var vd = rd["m_VertexData"]; var count = vd["m_VertexCount"].AsUInt; Require(count is > 0 and <= 1000000, "Sprite vertex budget");
            var channels = vd["m_Channels"]["Array"].Children; Require(channels.Count > 0, "Missing position channel");
            var position = channels[0]; int stream = position["stream"].AsInt, format = position["format"].AsInt, dimension = position["dimension"].AsInt & 15;
            Require(format == 0 && dimension >= 2 && stream is >= 0 and <= 15, "Unsupported sprite position format");
            var strides = new int[16];
            foreach (var channel in channels)
            {
                var d = channel["dimension"].AsInt & 15; if (d == 0) continue;
                int s = channel["stream"].AsInt, f = channel["format"].AsInt, channelOffset = channel["offset"].AsInt;
                Require(s is >= 0 and < 16, "Invalid vertex stream");
                var size = f switch { 0 or 10 or 11 => 4, 1 or 4 or 5 or 8 or 9 => 2, 2 or 3 or 6 or 7 => 1, _ => throw new InvalidDataException("Unsupported vertex channel format") };
                strides[s] = Math.Max(strides[s], checked(channelOffset + d * size));
            }
            long streamOffset = 0;
            for (var i = 0; i < stream; i++) streamOffset = (streamOffset + (long)count * strides[i] + 15) / 16 * 16;
            var data = Bytes(vd["m_DataSize"]);
            for (var i = 0; i < count; i++)
            {
                var at = streamOffset + (long)i * strides[stream] + position["offset"].AsInt;
                Require(at >= 0 && at + 8 <= data.Length, "Truncated sprite vertices");
                vertices.Add(new(BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan((int)at)), BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan((int)at + 4))));
            }
            var indices = Bytes(rd["m_IndexBuffer"]);
            foreach (var submesh in rd["m_SubMeshes"]["Array"].Children)
            {
                var at = submesh["firstByte"].AsUInt; var n = submesh["indexCount"].AsUInt;
                Require(submesh["topology"].AsInt == 0 && n % 3 == 0 && (long)at + n * 2L <= indices.Length, $"Unsupported sprite topology: {submesh["topology"].AsInt}, first={at}, count={n}, bytes={indices.Length}");
                var baseVertex = submesh["baseVertex"].IsDummy ? 0 : (int)submesh["baseVertex"].AsUInt;
                for (var i = 0; i < n; i += 3)
                    triangles.Add((Index(i), Index(i + 1), Index(i + 2)));
                int Index(int i) => checked(baseVertex + BinaryPrimitives.ReadUInt16LittleEndian(indices.AsSpan((int)at + i * 2)));
            }
        }
        Require(triangles.Count is > 0 and <= 200000, "Sprite triangle budget");
        var offsetField = renderData["textureRectOffset"]; var rect = sprite["m_Rect"]; var pivot = sprite["m_Pivot"]; var scale = sprite["m_PixelsToUnits"].AsFloat;
        Require(float.IsFinite(scale) && scale > 0, "Invalid sprite scale");
        var offset = new Vector2(rect["width"].AsFloat * pivot["x"].AsFloat - offsetField["x"].AsFloat, rect["height"].AsFloat * pivot["y"].AsFloat - offsetField["y"].AsFloat);
        for (var i = 0; i < vertices.Count; i++) { vertices[i] = vertices[i] * scale + offset; Require(float.IsFinite(vertices[i].X) && float.IsFinite(vertices[i].Y), "Invalid sprite vertex"); }
        var mask = new bool[checked(width * height)]; long work = 0;
        foreach (var (ai, bi, ci) in triangles)
        {
            Require(ai >= 0 && bi >= 0 && ci >= 0 && ai < vertices.Count && bi < vertices.Count && ci < vertices.Count, "Sprite index out of range");
            var a = vertices[ai]; var b = vertices[bi]; var c = vertices[ci];
            var left = (int)Math.Clamp(Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))), 0, width - 1); var right = (int)Math.Clamp(Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))), 0, width - 1);
            var bottom = (int)Math.Clamp(Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))), 0, height - 1); var top = (int)Math.Clamp(Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))), 0, height - 1);
            work += (long)(right - left + 1) * (top - bottom + 1); Require(work <= 200000000, "Sprite rasterization budget");
            if (Math.Abs(Cross(b - a, c - a)) < 0.000001) continue;
            for (var y = bottom; y <= top; y++) for (var x = left; x <= right; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f); var e0 = Cross(b - a, p - a); var e1 = Cross(c - b, p - b); var e2 = Cross(a - c, p - c);
                if ((e0 >= 0 && e1 >= 0 && e2 >= 0) || (e0 <= 0 && e1 <= 0 && e2 <= 0)) mask[y * width + x] = true;
            }
        }
        for (var i = 0; i < mask.Length; i++) if (!mask[i]) Array.Clear(pixels, i * 4, 4);
    }
    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
    private static byte[] Bytes(AssetTypeValueField field) => field.Value?.ValueType == AssetValueType.ByteArray ? field.AsByteArray : field["Array"].Value?.ValueType == AssetValueType.ByteArray ? field["Array"].AsByteArray : field["Array"].Children.Select(c => c.AsByte).ToArray();
    public static (byte[] Pixels, int Width, int Height) Resize(byte[] pixels, int width, int height, float multiplier, long limit)
    {
        Require(float.IsFinite(multiplier) && multiplier > 0, "Invalid sprite downscale");
        if (multiplier == 1) return (pixels, width, height);
        var w = (int)(width / multiplier); var h = (int)(height / multiplier);
        Require(w is > 0 and <= 16384 && h is > 0 and <= 16384 && (long)w * h * 4 <= limit, "Sprite resize budget");
        var result = new byte[checked(w * h * 4)];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            var sx = Math.Clamp((x + 0.5) * width / w - 0.5, 0, width - 1); var sy = Math.Clamp((y + 0.5) * height / h - 0.5, 0, height - 1);
            int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1); var fx = sx - x0; var fy = sy - y0;
            for (int channel = 0; channel < 4; channel++) result[(y * w + x) * 4 + channel] = (byte)Math.Round(
                (pixels[(y0 * width + x0) * 4 + channel] * (1 - fx) + pixels[(y0 * width + x1) * 4 + channel] * fx) * (1 - fy) +
                (pixels[(y1 * width + x0) * 4 + channel] * (1 - fx) + pixels[(y1 * width + x1) * 4 + channel] * fx) * fy);
        }
        return (result, w, h);
    }
}
