using System;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Sattleme.Checkout.Internal.Lightning;

/// <summary>
/// The fields of a BOLT11 invoice the package checks (SPEC.md §19): network, amount, payment hash,
/// description hash, date and expiry. The signature is not verified: the payee is the LNURL
/// server's node, which nothing here can check it against; the description hash ties the invoice to
/// the merchant's Lightning address instead.
/// </summary>
/// <param name="Network"><c>bc</c> mainnet, <c>tb</c> testnet, <c>tbs</c> signet, <c>bcrt</c> regtest.</param>
/// <param name="AmountMsat">The exact amount in millisatoshis, null for an invoice without one.</param>
/// <param name="Timestamp">Unix seconds.</param>
/// <param name="ExpirySeconds">Seconds after <paramref name="Timestamp"/> (3600 when the invoice does not say).</param>
/// <param name="PaymentHash">Hex, 32 bytes.</param>
/// <param name="DescriptionHash">Hex, 32 bytes; null when the invoice carries a plain description.</param>
internal sealed record Bolt11Invoice(
    string Network,
    long? AmountMsat,
    long Timestamp,
    long ExpirySeconds,
    string PaymentHash,
    string? DescriptionHash);

/// <summary>
/// BOLT11 decoding (https://github.com/lightning/bolts/blob/master/11-payment-encoding.md), as
/// SPEC.md §19 says: the first <c>p</c> and <c>h</c> fields of 52 words count and the last
/// <c>x</c>, as the merchant's app reads them; an <c>x</c> field of more than 7 words or an amount
/// above 21 million bitcoins makes the invoice invalid.
/// </summary>
internal static partial class Bolt11
{
    private const int TimestampWords = 7;

    /// <summary>A 65-byte signature, as 5-bit words.</summary>
    private const int SignatureWords = 104;

    private const int TagPaymentHash = 1;
    private const int TagDescriptionHash = 23;
    private const int TagExpiry = 6;

    /// <summary>256 bits as 5-bit words.</summary>
    private const int HashWords = 52;

    private const long DefaultExpirySeconds = 3600;

    /// <summary>The human-readable part: ASCII digits only (never Unicode's).</summary>
    [GeneratedRegex("^ln(bcrt|bc|tbs|tb)([0-9]+)?([munp])?$", RegexOptions.CultureInvariant)]
    private static partial Regex Hrp();

    /// <summary>The invoice <paramref name="input"/> decodes to, or null when it is not a valid one.</summary>
    public static Bolt11Invoice? Decode(string? input)
    {
        if (input is null)
        {
            return null;
        }
        if (Bech32.Decode(input) is not { } decoded)
        {
            return null;
        }
        var (hrpText, words) = decoded;
        var hrp = Hrp().Match(hrpText);
        if (!hrp.Success)
        {
            return null;
        }
        if (!TryAmountMsat(hrp.Groups[2].Success ? hrp.Groups[2].Value : null, hrp.Groups[3].Success ? hrp.Groups[3].Value : null, out var amount))
        {
            return null;
        }
        if (words.Length < TimestampWords + SignatureWords)
        {
            return null;
        }
        var timestamp = Bech32.WordsToInteger(words.AsSpan(0, TimestampWords));
        var fields = words.AsSpan(TimestampWords, words.Length - TimestampWords - SignatureWords);

        string? paymentHash = null;
        string? descriptionHash = null;
        long? expirySeconds = null;
        var i = 0;
        while (i < fields.Length)
        {
            if (i + 3 > fields.Length)
            {
                return null;
            }
            var tag = fields[i];
            var length = (fields[i + 1] * 32) + fields[i + 2];
            if (i + 3 + length > fields.Length)
            {
                // A field running past the signature.
                return null;
            }
            var data = fields.Slice(i + 3, length);
            // Fields of an unexpected length are skipped, as BOLT11 requires for p and h; the first
            // of each counts, and the last x (as the app reads them).
            if (tag == TagPaymentHash && length == HashWords && paymentHash is null)
            {
                paymentHash = Text.Hex(Bech32.WordsToBytes(data));
            }
            else if (tag == TagDescriptionHash && length == HashWords && descriptionHash is null)
            {
                descriptionHash = Text.Hex(Bech32.WordsToBytes(data));
            }
            else if (tag == TagExpiry)
            {
                if (length > Protocol.MaxBolt11ExpiryWords)
                {
                    return null;
                }
                expirySeconds = Bech32.WordsToInteger(data);
            }
            i += 3 + length;
        }
        if (paymentHash is null)
        {
            return null;
        }
        return new Bolt11Invoice(hrp.Groups[1].Value, amount, timestamp, expirySeconds ?? DefaultExpirySeconds, paymentHash, descriptionHash);
    }

    /// <summary>
    /// The amount of the human-readable part in millisatoshis: no digits, no amount (a multiplier
    /// alone is refused); no leading zero; <c>p</c> only for whole millisatoshis; at most 21 million
    /// bitcoins. False when it is refused.
    /// </summary>
    private static bool TryAmountMsat(string? digits, string? multiplier, out long? amount)
    {
        amount = null;
        if (digits is null)
        {
            return multiplier is null;
        }
        // Longer than any amount up to 21 million bitcoins, whatever the multiplier.
        if ((digits.Length > 1 && digits[0] == '0') || digits.Length > 22)
        {
            return false;
        }
        var value = BigInteger.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        BigInteger msat;
        switch (multiplier)
        {
            case "p":
                // A pico-bitcoin is a tenth of a millisatoshi: only whole millisatoshis are valid.
                if (!(value % 10).IsZero)
                {
                    return false;
                }
                msat = value / 10;
                break;
            case "n":
                msat = value * 100;
                break;
            case "u":
                msat = value * 100_000;
                break;
            case "m":
                msat = value * 100_000_000;
                break;
            default:
                msat = value * 100_000_000_000;
                break;
        }
        if (msat > Protocol.MaxBolt11AmountMsat)
        {
            return false;
        }
        amount = (long)msat;
        return true;
    }
}
