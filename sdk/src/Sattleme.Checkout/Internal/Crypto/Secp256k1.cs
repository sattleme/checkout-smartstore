using System;
using NBitcoin.Secp256k1;

namespace Sattleme.Checkout.Internal.Crypto;

/// <summary>
/// secp256k1 as Nostr uses it, through NBitcoin.Secp256k1 (a C# port of libsecp256k1): BIP-340
/// Schnorr signatures with 32 bytes of auxiliary random data, their verification, x-only public
/// keys and <c>lift_x</c>, and the ECDH NIP-44 asks for (the shared point's x coordinate, unhashed).
/// Never a curve of its own.
/// </summary>
internal static class Secp256k1
{
    /// <summary>Whether <paramref name="hex"/> is an x-only public key (lowercase hex) of a point of secp256k1 (BIP-340 lift_x).</summary>
    public static bool IsPublicKey(string? hex) => Text.IsHex32(hex) && ECXOnlyPubKey.TryCreate(Text.FromHex(hex!), out _);

    /// <summary>Whether <paramref name="secret"/> is a secret key: 32 bytes from 1 to the curve's order minus 1.</summary>
    public static bool IsSecretKey(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != 32 || !ECPrivKey.TryCreate(secret, out var key))
        {
            return false;
        }
        key!.Dispose();
        return true;
    }

    /// <summary>The x-only public key (hex) of the secret key <paramref name="secret"/>.</summary>
    public static string PublicKeyOf(ReadOnlySpan<byte> secret)
    {
        using var key = SecretKey(secret);
        return Text.Hex(key.CreateXOnlyPubKey().ToBytes());
    }

    /// <summary>The BIP-340 signature (64 bytes) of <paramref name="hash"/> by <paramref name="secret"/>, with <paramref name="auxiliary"/> random data.</summary>
    public static byte[] Sign(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> secret, byte[] auxiliary)
    {
        if (hash.Length != 32 || auxiliary.Length != 32)
        {
            throw new ArgumentException("A BIP-340 signature signs 32 bytes, with 32 bytes of auxiliary data");
        }
        using var key = SecretKey(secret);
        return key.SignBIP340(hash, new BIP340NonceFunction(auxiliary)).ToBytes();
    }

    /// <summary>Whether <paramref name="signature"/> (64 bytes, hex) of <paramref name="hash"/> (hex) verifies for <paramref name="publicKey"/> (hex).</summary>
    public static bool Verify(string signature, string hash, string publicKey)
    {
        if (!Text.IsHex64(signature) || !Text.IsHex32(hash) || !Text.IsHex32(publicKey))
        {
            return false;
        }
        return ECXOnlyPubKey.TryCreate(Text.FromHex(publicKey), out var key)
            && SecpSchnorrSignature.TryCreate(Text.FromHex(signature), out var parsed)
            && key!.SigVerifyBIP340(parsed!, Text.FromHex(hash));
    }

    /// <summary>
    /// The x coordinate of the ECDH of <paramref name="secret"/> with the x-only public key
    /// <paramref name="publicKey"/> (hex, the point of even y), unhashed: what NIP-44's conversation
    /// key is extracted from. Throws for a secret key out of range or a key off the curve.
    /// </summary>
    public static byte[] SharedX(ReadOnlySpan<byte> secret, string publicKey)
    {
        if (!Text.IsHex32(publicKey) || !ECXOnlyPubKey.TryCreate(Text.FromHex(publicKey), out _))
        {
            throw new ArgumentException("Not a public key");
        }
        using var key = SecretKey(secret);
        var point = ECPubKey.Create(Text.FromHex("02" + publicKey));
        var shared = point.GetSharedPubkey(key).ToBytes(true);
        return shared.AsSpan(1, 32).ToArray();
    }

    private static ECPrivKey SecretKey(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != 32 || !ECPrivKey.TryCreate(secret, out var key))
        {
            throw new ArgumentException("Not a secret key");
        }
        return key!;
    }
}
