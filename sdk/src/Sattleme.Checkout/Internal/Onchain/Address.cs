using System.Numerics;
using Sattleme.Checkout.Internal.Crypto;
using Sattleme.Checkout.Internal.Lightning;

namespace Sattleme.Checkout.Internal.Onchain;

/// <summary>
/// Deposit addresses (SPEC.md §31, "Deposit addresses", "Payment URIs"): the taproot addresses the
/// merchant's wallet hands out, in their one spelling, and what the shop's page shows of an invoice
/// as one QR code.
/// </summary>
internal static class Address
{
    /// <summary><c>bc1p</c> and 58 more characters.</summary>
    private const int DepositAddressLength = 62;

    /// <summary>The witness version (1) and the 32-byte program, as 5-bit words.</summary>
    private const int ProgramWords = 53;

    /// <summary>
    /// Whether <paramref name="value"/> is a deposit address: the bech32m encoding (BIP-350) of the
    /// human-readable part <c>bc</c>, witness version 1 and a 32-byte program (BIP-341), in
    /// lowercase. Another network, witness version or length, bech32 instead of bech32m, upper case
    /// or a padding with bits that are not zero is not one.
    /// </summary>
    public static bool IsDepositAddress(string? value)
    {
        if (value is null || value.Length != DepositAddressLength || !value.StartsWith("bc1", System.StringComparison.Ordinal))
        {
            return false;
        }
        for (var i = 3; i < value.Length; i++)
        {
            // The bech32 characters, lowercase only.
            if (value[i] is not ((>= '0' and <= '9' and not '1') or (>= 'a' and <= 'z' and not 'b' and not 'i' and not 'o')))
            {
                return false;
            }
        }
        if (Bech32.Decode(value, Bech32.Bech32mConstant) is not { } decoded || decoded.Hrp != "bc")
        {
            return false;
        }
        var words = decoded.Words;
        // 52 words after the version: 256 bits of program and 4 bits of padding, which are zeros.
        return words.Length == ProgramWords && words[0] == 1 && (words[^1] & 0b1111) == 0;
    }

    /// <summary>Where the leases of <paramref name="address"/> are in the registry: HMAC(registry key, "onchain/v1/" + address).</summary>
    public static string LeaseAddress(byte[] registryKey, string address) =>
        Text.Hex(Hashes.HmacSha256(registryKey, Text.Utf8(Protocol.LeaseAddressPrefix + address)));

    /// <summary>
    /// <paramref name="sats"/> in bitcoins, exact, as BIP-21's <c>amount</c>: no trailing zero in
    /// the fraction, no fraction when whole (49 000 sats is "0.00049", 100 000 000 sats is "1").
    /// </summary>
    public static string BtcAmount(long sats)
    {
        var whole = BigInteger.Divide(sats, Protocol.SatsPerBtc);
        var fraction = Numbers.Decimal(sats % Protocol.SatsPerBtc).PadLeft(8, '0').TrimEnd('0');
        var wholeText = whole.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return fraction.Length == 0 ? wholeText : $"{wholeText}.{fraction}";
    }

    /// <summary>
    /// What the shop's page shows as one QR code: <c>bitcoin:&lt;address&gt;?amount=&lt;BTC&gt;</c>, with
    /// <c>&amp;lightning=&lt;invoice&gt;</c> when there is one (BIP-21, the "unified QR" wallets
    /// read), or <c>lightning:&lt;invoice&gt;</c> without an address. Both written as they are.
    /// </summary>
    public static string PaymentUri(string? address, long sats, string? bolt11)
    {
        if (address is null)
        {
            return $"lightning:{bolt11}";
        }
        var uri = $"bitcoin:{address}?amount={BtcAmount(sats)}";
        return bolt11 is null ? uri : $"{uri}&lightning={bolt11}";
    }
}
