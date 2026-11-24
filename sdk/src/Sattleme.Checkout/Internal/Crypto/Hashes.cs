using System;
using System.Security.Cryptography;

namespace Sattleme.Checkout.Internal.Crypto;

/// <summary>SHA-256, HMAC-SHA256 and HKDF-SHA256 (RFC 5869), from .NET's own cryptography (SPEC.md §2).</summary>
internal static class Hashes
{
    public static byte[] Sha256(ReadOnlySpan<byte> data) => SHA256.HashData(data);

    public static byte[] HmacSha256(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data) => HMACSHA256.HashData(key, data);

    /// <summary>HKDF-SHA256 of <paramref name="secret"/>, 32 bytes, salt and info in UTF-8 (SPEC.md §10).</summary>
    public static byte[] Hkdf(ReadOnlySpan<byte> secret, string salt, string info)
    {
        var output = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, output, Text.Utf8(salt), Text.Utf8(info));
        return output;
    }

    /// <summary>HKDF-extract (SHA-256) of <paramref name="secret"/> with <paramref name="salt"/>: NIP-44's conversation key.</summary>
    public static byte[] HkdfExtract(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> salt)
    {
        var output = new byte[32];
        HKDF.Extract(HashAlgorithmName.SHA256, secret, salt, output);
        return output;
    }

    /// <summary>HKDF-expand (SHA-256) of <paramref name="key"/> with <paramref name="info"/>: NIP-44's message keys.</summary>
    public static byte[] HkdfExpand(ReadOnlySpan<byte> key, ReadOnlySpan<byte> info, int length)
    {
        var output = new byte[length];
        HKDF.Expand(HashAlgorithmName.SHA256, key, output, info);
        return output;
    }

    /// <summary>Whether two MACs are equal, compared in constant time (SPEC.md §27).</summary>
    public static bool FixedTimeEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => CryptographicOperations.FixedTimeEquals(a, b);
}
