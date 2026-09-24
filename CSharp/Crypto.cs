using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
namespace MoenotesAssets;

public static class Crypto
{
    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
    public static string Identity(params string[] parts) => Sha256(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(parts, Json.Options));
    public static bool Builtin(string name)
    {
        var value = name.ToLowerInvariant();
        return value.Contains("_monoscripts_") || value.Contains("_unitybuiltinassets_") ||
               value.EndsWith("_monoscripts.bundle") || value.EndsWith("_unitybuiltinassets.bundle");
    }
    public static void Decrypt(Span<byte> data, string name, long offset)
    {
        Config.Require(name.Length > 0 && !name.Contains('/') && !name.Contains('\\') && offset >= 0, "Invalid decryption input");
        var length = (int)Math.Min(data.Length, Math.Max(0, 16384 - offset));
        if (length == 0) return;
        using var aes = Aes.Create();
        aes.Key = Convert.FromHexString("7372a4ee777db361ad896c99e408a182");
        Span<byte> counter = stackalloc byte[16];
        var nonce = SHA256.HashData(Convert.FromHexString("ee24a70238e2a0e5").Concat(Encoding.UTF8.GetBytes(name)).ToArray());
        nonce.AsSpan(0, 8).CopyTo(counter);
        Span<byte> mask = stackalloc byte[16];
        for (var at = 0; at < length;)
        {
            var position = offset + at;
            BinaryPrimitives.WriteUInt64BigEndian(counter[8..], (ulong)(position / 16));
            aes.EncryptEcb(counter, mask, PaddingMode.None);
            var skip = (int)(position % 16);
            var count = Math.Min(16 - skip, length - at);
            for (var i = 0; i < count; i++) data[at + i] ^= mask[skip + i];
            at += count;
        }
    }
}
