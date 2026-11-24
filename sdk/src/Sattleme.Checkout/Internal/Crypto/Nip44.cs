using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Sattleme.Checkout.Internal.Crypto;

/// <summary>A NIP-44 payload of another version: written by a newer client (SPEC.md §12, step 2).</summary>
internal sealed class UnknownNip44VersionException : Exception
{
    public UnknownNip44VersionException()
        : base("NIP-44 payload of an unknown version")
    {
    }
}

/// <summary>
/// NIP-44 version 2 (https://github.com/nostr-protocol/nips/blob/master/44.md), as the merchant's
/// app implements it: ChaCha20, HMAC-SHA256 over the nonce and the ciphertext, per-message keys from
/// a random 32-byte nonce, plaintext padded. Checked against its official vectors (Nip44Tests). The
/// conversation key is a 32-byte HKDF output used as is, except inside a registry declaration or a
/// lease: the ECDH of <see cref="ConversationKey"/>.
/// </summary>
internal static class Nip44
{
    private const byte Version = 2;
    private const int MinPlaintext = 1;
    private const int MaxPlaintext = 0xffff;

    private static readonly byte[] Salt = Text.Utf8("nip44-v2");

    /// <summary>
    /// NIP-44's conversation key of two parties (<c>get_conversation_key</c>): the x coordinate of
    /// the ECDH of <paramref name="secret"/> with <paramref name="publicKey"/> (hex), through
    /// HKDF-extract with the salt "nip44-v2". Throws for a key out of range or off the curve.
    /// </summary>
    public static byte[] ConversationKey(ReadOnlySpan<byte> secret, string publicKey) =>
        Hashes.HkdfExtract(Secp256k1.SharedX(secret, publicKey), Salt);

    /// <summary>NIP-44's padded length of a plaintext of <paramref name="length"/> bytes (<c>calc_padded_len</c>).</summary>
    public static int PaddedLength(int length)
    {
        if (length <= 32)
        {
            return 32;
        }
        var nextPower = 1 << (BitOperations.Log2((uint)(length - 1)) + 1);
        var chunk = nextPower <= 256 ? 32 : nextPower / 8;
        return chunk * (((length - 1) / chunk) + 1);
    }

    /// <summary><c>get_message_keys</c>: the ChaCha20 key and nonce and the HMAC key of one message.</summary>
    public static (byte[] ChaChaKey, byte[] ChaChaNonce, byte[] HmacKey) MessageKeys(ReadOnlySpan<byte> conversationKey, ReadOnlySpan<byte> nonce)
    {
        if (conversationKey.Length != 32)
        {
            throw new ArgumentException("Invalid conversation key length");
        }
        if (nonce.Length != 32)
        {
            throw new ArgumentException("Invalid nonce length");
        }
        var keys = Hashes.HkdfExpand(conversationKey, nonce, 76);
        return (keys[..32], keys[32..44], keys[44..76]);
    }

    /// <summary>Encrypts <paramref name="text"/> under <paramref name="conversationKey"/>, with a fresh random nonce.</summary>
    public static string Encrypt(string text, ReadOnlySpan<byte> conversationKey, RandomSource random) =>
        EncryptWithNonce(text, conversationKey, random.Bytes(32));

    /// <summary><see cref="Encrypt"/> with a given nonce: for the official vectors only.</summary>
    public static string EncryptWithNonce(string text, ReadOnlySpan<byte> conversationKey, ReadOnlySpan<byte> nonce)
    {
        var (chachaKey, chachaNonce, hmacKey) = MessageKeys(conversationKey, nonce);
        var ciphertext = ChaCha20.Xor(chachaKey, chachaNonce, Pad(text));
        var payload = new byte[1 + 32 + ciphertext.Length + 32];
        payload[0] = Version;
        nonce.CopyTo(payload.AsSpan(1));
        ciphertext.CopyTo(payload.AsSpan(33));
        Mac(hmacKey, nonce, ciphertext).CopyTo(payload.AsSpan(33 + ciphertext.Length));
        return Base64.ToBase64(payload);
    }

    /// <summary>
    /// The text of a NIP-44 v2 payload. Throws <see cref="UnknownNip44VersionException"/> for
    /// another version (an empty payload, one starting with <c>#</c>, a version byte other than 2),
    /// <see cref="FormatException"/> when it is malformed or not sealed with
    /// <paramref name="conversationKey"/>: its size, strict base64, the MAC (constant time), the
    /// padding, strict UTF-8.
    /// </summary>
    public static string Decrypt(string payload, ReadOnlySpan<byte> conversationKey)
    {
        if (payload.Length == 0 || payload[0] == '#')
        {
            throw new UnknownNip44VersionException();
        }
        if (payload.Length < 132 || payload.Length > 87472)
        {
            throw new FormatException("Invalid payload size");
        }
        var data = Base64.FromBase64(payload);
        if (data is null || data.Length < 99 || data.Length > 65603)
        {
            throw new FormatException("Invalid payload");
        }
        if (data[0] != Version)
        {
            throw new UnknownNip44VersionException();
        }
        var nonce = data.AsSpan(1, 32);
        var ciphertext = data.AsSpan(33, data.Length - 65);
        var (chachaKey, chachaNonce, hmacKey) = MessageKeys(conversationKey, nonce);
        if (!Hashes.FixedTimeEquals(Mac(hmacKey, nonce, ciphertext), data.AsSpan(data.Length - 32)))
        {
            throw new FormatException("Invalid MAC");
        }
        return Unpad(ChaCha20.Xor(chachaKey, chachaNonce, ciphertext));
    }

    private static byte[] Pad(string text)
    {
        var bytes = Text.Utf8(text);
        if (bytes.Length < MinPlaintext || bytes.Length > MaxPlaintext)
        {
            throw new ArgumentException("Invalid plaintext length");
        }
        var padded = new byte[2 + PaddedLength(bytes.Length)];
        BinaryPrimitives.WriteUInt16BigEndian(padded, (ushort)bytes.Length);
        bytes.CopyTo(padded.AsSpan(2));
        return padded;
    }

    private static string Unpad(byte[] padded)
    {
        var length = BinaryPrimitives.ReadUInt16BigEndian(padded);
        if (length < MinPlaintext || padded.Length != 2 + PaddedLength(length))
        {
            throw new FormatException("Invalid padding");
        }
        return Text.DecodeUtf8(padded.AsSpan(2, length)) ?? throw new FormatException("Invalid UTF-8");
    }

    private static byte[] Mac(ReadOnlySpan<byte> hmacKey, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext)
    {
        var message = new byte[nonce.Length + ciphertext.Length];
        nonce.CopyTo(message);
        ciphertext.CopyTo(message.AsSpan(nonce.Length));
        return Hashes.HmacSha256(hmacKey, message);
    }
}
