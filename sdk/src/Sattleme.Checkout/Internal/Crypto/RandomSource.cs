using System.Security.Cryptography;

namespace Sattleme.Checkout.Internal.Crypto;

/// <summary>
/// Where random bytes come from: the NIP-44 nonces, the BIP-340 auxiliary data of signatures, the
/// keys of a single relay read, the subscription ids, checkout links' keys and the shuffle of a lot.
/// The platform's CSPRNG (<see cref="RandomNumberGenerator"/>), never a general-purpose generator;
/// another generator is given for reproducible tests only.
/// </summary>
internal sealed class RandomSource(RandomNumberGenerator? generator)
{
    private readonly RandomNumberGenerator? _generator = generator;

    /// <summary>The CSPRNG.</summary>
    public static readonly RandomSource Secure = new(null);

    /// <summary><paramref name="length"/> fresh random bytes.</summary>
    public byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        if (_generator is null)
        {
            RandomNumberGenerator.Fill(bytes);
        }
        else
        {
            lock (_generator)
            {
                _generator.GetBytes(bytes);
            }
        }
        return bytes;
    }
}
