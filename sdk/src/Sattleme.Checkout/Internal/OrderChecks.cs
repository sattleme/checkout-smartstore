using System.Collections.Generic;
using System.Linq;
using Sattleme.Checkout.Internal.Lightning;
using Sattleme.Checkout.Internal.Onchain;

namespace Sattleme.Checkout.Internal;

/// <summary>An order, checked.</summary>
/// <param name="Ref">Its reference.</param>
/// <param name="Sats">The amount in sats; null for an order in a currency. Exactly one of the two.</param>
/// <param name="Fiat">The price in a currency, read; null in sats.</param>
/// <param name="RateSources">The rate sources of an order in a currency, in the order they are asked; null in sats.</param>
/// <param name="DurationSeconds">60 to 3600.</param>
/// <param name="Label">The note, else the reference: what the declaration and the invoice's comment carry.</param>
/// <param name="Checkout">Whether a checkout link is asked.</param>
/// <param name="RedirectUrl">With a checkout link only: the redirect URL, else null.</param>
internal sealed record CheckedOrder(
    string Ref,
    long? Sats,
    Fiat? Fiat,
    IReadOnlyList<RateSource>? RateSources,
    int DurationSeconds,
    string Label,
    bool Checkout,
    string? RedirectUrl);

/// <summary>A stored invoice, checked: what its payment check asks.</summary>
/// <param name="Ref">Its order's reference, with an address (null without one).</param>
/// <param name="Lightning">Its Lightning invoice, null for an order paid on-chain only.</param>
/// <param name="OnchainAddress">Its deposit address, null without one.</param>
/// <param name="Sats">Its amount.</param>
/// <param name="ExpiresAt">Its end.</param>
internal sealed record StoredInvoice(string? Ref, LightningInvoice? Lightning, string? OnchainAddress, long Sats, long ExpiresAt);

/// <summary>
/// What a caller gives, checked before any request (SPEC.md §17, §22, §23): an order, a stored
/// invoice, an option. Refused, never repaired.
/// </summary>
internal static class OrderChecks
{
    public const string RateSourceRule = "\"coingecko\", \"mempool\", \"opennode\", \"coinbase\", \"kraken\" or a source of your own, or a non-empty list of them without the same name twice";

    private const string NotStored = "not an invoice made by CreateInvoiceAsync";

    /// <summary>Whether <paramref name="reference"/> is 1 to 64 characters <c>A–Z a–z 0–9 . _ : -</c>.</summary>
    public static bool IsRef(string? reference)
    {
        if (reference is null || reference.Length is < 1 or > Protocol.MaxRefLength)
        {
            return false;
        }
        foreach (var c in reference)
        {
            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or ':' or '-'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary><paramref name="reference"/>, checked: <see cref="InvalidOrderException"/> (<c>ref</c>) unless it is a reference.</summary>
    public static string CheckRef(string? reference) =>
        IsRef(reference) ? reference! : throw new InvalidOrderException("ref", Protocol.RefRule);

    /// <summary>
    /// The order's parameters, checked (SPEC.md §17, "The order"). <paramref name="clientRateSources"/>
    /// are the client's: an order in a currency without its own sources uses them.
    /// </summary>
    public static CheckedOrder Check(Order? order, IReadOnlyList<RateSource> clientRateSources)
    {
        if (order is null)
        {
            throw new InvalidOrderException("order", "expected { Ref, Sats | Fiat, DurationSeconds?, Note? }");
        }
        var reference = CheckRef(order.Ref);
        if ((order.Sats is null) == (order.Fiat is null))
        {
            throw new InvalidOrderException("sats", "give either Sats or Fiat");
        }
        long? sats = null;
        if (order.Sats is { } givenSats)
        {
            if (givenSats < 1 || givenSats > Protocol.MaxSats)
            {
                throw new InvalidOrderException("sats", $"a whole number of sats, from 1 to {Numbers.Decimal(Protocol.MaxSats)}");
            }
            sats = givenSats;
        }
        var fiat = order.Fiat is null ? null : Prices.ParseFiat(order.Fiat);

        IReadOnlyList<RateSource>? rateSources = null;
        if (order.RateSources is not null)
        {
            if (fiat is null)
            {
                throw new InvalidOrderException("rateSource", "only for an order priced in a currency (Fiat)");
            }
            rateSources = RateSource.CheckList(order.RateSources) ?? throw new InvalidOrderException("rateSource", RateSourceRule);
        }
        else if (fiat is not null)
        {
            rateSources = clientRateSources;
        }

        var durationSeconds = order.DurationSeconds ?? Protocol.DefaultDurationSeconds;
        if (durationSeconds is < Protocol.MinDurationSeconds or > Protocol.MaxDurationSeconds)
        {
            throw new InvalidOrderException("durationSeconds",
                $"{Numbers.Decimal(Protocol.MinDurationSeconds)} to {Numbers.Decimal(Protocol.MaxDurationSeconds)} seconds");
        }
        var label = LabelOf(order.Note, reference);
        var redirectUrl = RedirectOf(order);
        return new CheckedOrder(reference, sats, fiat, rateSources, durationSeconds, label, order.Checkout, redirectUrl);
    }

    /// <summary>The redirect URL: with <c>Checkout</c> only, a redirect URL (SPEC.md §30); null when absent.</summary>
    private static string? RedirectOf(Order order)
    {
        if (order.RedirectUrl is null)
        {
            return null;
        }
        if (!order.Checkout)
        {
            throw new InvalidOrderException("redirectUrl", "only with Checkout = true");
        }
        if (Internal.Checkout.RedirectShown(order.RedirectUrl) is null)
        {
            throw new InvalidOrderException("redirectUrl",
                "an https URL (lowercase host, no user name nor password) or an app's link (scheme://…), 2048 characters at most, without spaces nor other characters to escape");
        }
        return order.RedirectUrl;
    }

    /// <summary>
    /// The note, without the spaces (U+0020) around it, else the reference: 100 code points at most,
    /// well-formed, without control characters.
    /// </summary>
    public static string LabelOf(string? note, string reference)
    {
        if (note is null)
        {
            return reference;
        }
        if (Text.HasControlCharacter(note))
        {
            throw new InvalidOrderException("note", "no control characters");
        }
        if (!Text.IsWellFormed(note))
        {
            throw new InvalidOrderException("note", "well-formed Unicode");
        }
        var label = Text.TrimSpaces(note);
        if (label.Length == 0)
        {
            label = reference;
        }
        if (Text.CodePointLength(label) > Protocol.MaxNoteLength)
        {
            throw new InvalidOrderException("note", $"{Numbers.Decimal(Protocol.MaxNoteLength)} characters at most");
        }
        return label;
    }

    /// <summary>
    /// A stored invoice, checked (SPEC.md §22, "Before asking"): its sats and end; a mainnet invoice
    /// of exactly its sats, its own payment hash and end, an https verify URL, or none of the three; a
    /// deposit address, or none; at least one of the two. With an address, its reference: the
    /// witnesses of its payment are its order's. Throws <see cref="InvalidOrderException"/> (<c>invoice</c>).
    /// </summary>
    public static StoredInvoice CheckStoredInvoice(IInvoiceToCheck? invoice)
    {
        if (invoice is null)
        {
            throw new InvalidOrderException("invoice", NotStored);
        }
        var satsText = invoice.Sats;
        long sats = 0;
        if (satsText is null || !Text.IsDigits(satsText, 1, 16)
            || (sats = long.Parse(satsText, System.Globalization.CultureInfo.InvariantCulture)) < 1 || sats > Protocol.MaxSats
            || !Numbers.IsSafeInteger(invoice.ExpiresAt))
        {
            throw new InvalidOrderException("invoice", NotStored);
        }
        var expiresAt = invoice.ExpiresAt;
        LightningInvoice? lightning = null;
        if (invoice.Bolt11 is null)
        {
            if (invoice.PaymentHash is not null || invoice.VerifyUrl is not null)
            {
                throw new InvalidOrderException("invoice", NotStored);
            }
        }
        else
        {
            var decoded = Bolt11.Decode(invoice.Bolt11);
            if (decoded is null || decoded.Network != "bc" || decoded.AmountMsat != sats * 1000 || !Text.IsHex32(invoice.PaymentHash)
                || decoded.PaymentHash != invoice.PaymentHash || decoded.Timestamp + decoded.ExpirySeconds != expiresAt
                || Urls.ParseHttps(invoice.VerifyUrl) is null)
            {
                throw new InvalidOrderException("invoice", NotStored);
            }
            lightning = new LightningInvoice(invoice.Bolt11, invoice.PaymentHash!, invoice.VerifyUrl!, expiresAt);
        }
        var address = invoice.OnchainAddress;
        if (address is not null && !Address.IsDepositAddress(address))
        {
            throw new InvalidOrderException("invoice", NotStored);
        }
        if (lightning is null && address is null)
        {
            throw new InvalidOrderException("invoice", NotStored);
        }
        string? reference = null;
        if (address is not null)
        {
            if (!IsRef(invoice.Ref))
            {
                throw new InvalidOrderException("invoice", $"{NotStored}: with an address, its ref ({Protocol.RefRule})");
            }
            reference = invoice.Ref;
        }
        return new StoredInvoice(reference, lightning, address, sats, expiresAt);
    }

    /// <summary>A whole number of milliseconds within [<paramref name="min"/>, <paramref name="max"/>], <paramref name="fallback"/> when null.</summary>
    public static int CheckMilliseconds(int? value, int fallback, int min, int max, string name)
    {
        if (value is null)
        {
            return fallback;
        }
        if (value < min || value > max)
        {
            throw new InvalidOptionException(name, $"a whole number of milliseconds, {Numbers.Decimal(min)} to {Numbers.Decimal(max)}");
        }
        return value.Value;
    }

    /// <summary>What a payment check asks of a declared invoice.</summary>
    public static StoredInvoice StoredOf(DeclaredInvoice invoice) => new(
        invoice.Ref,
        invoice.Bolt11 is null ? null : new LightningInvoice(invoice.Bolt11, invoice.PaymentHash!, invoice.VerifyUrl!, invoice.ExpiresAt),
        invoice.OnchainAddress,
        long.Parse(invoice.Sats, System.Globalization.CultureInfo.InvariantCulture),
        invoice.ExpiresAt);

    /// <summary>The rate sources of the client's option, checked: the default list when null.</summary>
    public static IReadOnlyList<RateSource> ClientRateSources(IReadOnlyList<RateSource>? sources) =>
        sources is null
            ? RateSource.Defaults
            : RateSource.CheckList(sources) ?? throw new InvalidOptionException("rateSource", RateSourceRule);

    /// <summary>Whether every character of <paramref name="value"/> is visible ASCII (U+0021 to U+007E), and there is one.</summary>
    public static bool IsVisibleAscii(string value) => value.Length > 0 && value.All(c => c is >= '!' and <= '~');
}
