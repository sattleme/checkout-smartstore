using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Sattleme.Checkout.Internal.Crypto;

/// <summary>
/// ChaCha20, the IETF variant of RFC 8439 (32-byte key, 12-byte nonce, 32-bit block counter), as
/// NIP-44 v2 uses it: its stream XORed with the data, from block 0. .NET offers ChaCha20 only
/// inside ChaCha20-Poly1305 (an AEAD, which NIP-44 is not), so it is written here, in its
/// constant-time form (additions, rotations and XORs only), and checked against RFC 8439's test
/// vectors and NIP-44's official ones (ChaCha20Tests, Nip44Tests).
/// </summary>
internal static class ChaCha20
{
    /// <summary>
    /// <paramref name="data"/> XORed with the ChaCha20 stream of <paramref name="key"/> and
    /// <paramref name="nonce"/>, starting at block <paramref name="counter"/>.
    /// </summary>
    public static byte[] Xor(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> data, uint counter = 0)
    {
        if (key.Length != 32 || nonce.Length != 12)
        {
            throw new ArgumentException("ChaCha20 takes a 32-byte key and a 12-byte nonce");
        }
        if ((ulong)data.Length > ((ulong)uint.MaxValue - counter + 1) * 64)
        {
            throw new ArgumentException("Too much data for one ChaCha20 stream");
        }
        Span<uint> state = stackalloc uint[16];
        state[0] = 0x61707865;
        state[1] = 0x3320646e;
        state[2] = 0x79622d32;
        state[3] = 0x6b206574;
        for (var i = 0; i < 8; i++)
        {
            state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(4 * i)..]);
        }
        state[12] = counter;
        for (var i = 0; i < 3; i++)
        {
            state[13 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[(4 * i)..]);
        }
        var output = new byte[data.Length];
        Span<byte> stream = stackalloc byte[64];
        for (var offset = 0; offset < data.Length; offset += 64)
        {
            Block(state, stream);
            var length = Math.Min(64, data.Length - offset);
            for (var i = 0; i < length; i++)
            {
                output[offset + i] = (byte)(data[offset + i] ^ stream[i]);
            }
            state[12]++;
        }
        stream.Clear();
        state.Clear();
        return output;
    }

    /// <summary>The ChaCha20 block function (RFC 8439 §2.3): 64 bytes of stream for <paramref name="state"/>.</summary>
    public static void Block(ReadOnlySpan<uint> state, Span<byte> output)
    {
        Span<uint> x = stackalloc uint[16];
        state.CopyTo(x);
        for (var round = 0; round < 10; round++)
        {
            // Column rounds, then diagonal rounds.
            QuarterRound(x, 0, 4, 8, 12);
            QuarterRound(x, 1, 5, 9, 13);
            QuarterRound(x, 2, 6, 10, 14);
            QuarterRound(x, 3, 7, 11, 15);
            QuarterRound(x, 0, 5, 10, 15);
            QuarterRound(x, 1, 6, 11, 12);
            QuarterRound(x, 2, 7, 8, 13);
            QuarterRound(x, 3, 4, 9, 14);
        }
        for (var i = 0; i < 16; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output[(4 * i)..], x[i] + state[i]);
        }
        x.Clear();
    }

    private static void QuarterRound(Span<uint> x, int a, int b, int c, int d)
    {
        x[a] += x[b];
        x[d] = BitOperations.RotateLeft(x[d] ^ x[a], 16);
        x[c] += x[d];
        x[b] = BitOperations.RotateLeft(x[b] ^ x[c], 12);
        x[a] += x[b];
        x[d] = BitOperations.RotateLeft(x[d] ^ x[a], 8);
        x[c] += x[d];
        x[b] = BitOperations.RotateLeft(x[b] ^ x[c], 7);
    }
}
