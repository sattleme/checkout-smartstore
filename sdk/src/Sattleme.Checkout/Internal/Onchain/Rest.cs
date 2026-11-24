using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sattleme.Checkout.Internal.Lightning;
using Sattleme.Checkout.Internal.Nostr;

namespace Sattleme.Checkout.Internal.Onchain;

/// <summary>A rest invoice: a declaration of the sale with <c>rest</c>, its invoice decoded.</summary>
/// <param name="Bolt11">The invoice.</param>
/// <param name="PaymentHash">Its payment hash (hex).</param>
/// <param name="VerifyUrl">Its LUD-21 verify URL.</param>
/// <param name="Sats">Its amount.</param>
/// <param name="CreatedAt">Its BOLT11 timestamp (Unix seconds).</param>
/// <param name="ExpiresAt">Its timestamp + its expiry (Unix seconds).</param>
internal sealed record RestInvoice(string Bolt11, string PaymentHash, string VerifyUrl, long Sats, long CreatedAt, long ExpiresAt)
{
    /// <summary>What the Lightning part of a payment check needs of it.</summary>
    public LightningInvoice ToLightning() => new(Bolt11, PaymentHash, VerifyUrl, ExpiresAt);
}

/// <summary>What a check asks for: an invoice of <paramref name="Sats"/>, lasting <paramref name="ExpirySeconds"/>.</summary>
internal sealed record RestAsk(long Sats, long ExpirySeconds);

/// <summary>The invoice of the order a rest record belongs to: its amount and dates, as its link holds them.</summary>
internal sealed record RestLinkTerms(long Sats, long CreatedAt, long ExpiresAt);

/// <summary>A rest record, opened as the page reads it: its invoice, decoded; or one of a newer format.</summary>
internal sealed record OpenedRest(bool Newer, RestInvoice? Invoice, NostrEvent Event);

/// <summary>
/// What a check reads of an invoice's rest records (SPEC.md §31, "Rest records"): the payment
/// hashes of those a majority of the merchant's relays sent (a page reading a majority meets each
/// of them), as the page would read them; and the record addresses where a relay sent one of a
/// newer format (its event checked out): such a record holds its address, never written over nor
/// used (SPEC.md §12).
/// </summary>
internal sealed record RestRecordsRead(HashSet<string> Held, HashSet<string> Newer);

/// <summary>
/// The rest of an on-chain underpayment (SPEC.md §31, "The rest of an underpayment"): when less than
/// the amount came on-chain in time, a payment check asks the merchant's Lightning address for an
/// invoice of the rest, declares it for the order's sale with <c>rest</c> (the address), and
/// publishes a rest record, which the pages of the order's checkout links read (they cannot read the
/// registry). From 1 000 sats, the rest is payable on-chain to the same address too; below, in
/// Lightning only.
/// </summary>
internal static class Rests
{
    /// <summary>A rest invoice of a declaration with <c>rest</c> (opened: its invoice decodes, in whole sats).</summary>
    public static RestInvoice InvoiceOf(OpenedDeclaration declaration)
    {
        var decoded = declaration.Decoded;
        return new RestInvoice(declaration.Invoice, decoded.PaymentHash, declaration.VerifyUrl, decoded.AmountMsat!.Value / 1000,
            decoded.Timestamp, decoded.Timestamp + decoded.ExpirySeconds);
    }

    /// <summary>The order of the rest invoices: by date, then payment hash.</summary>
    public static int ByRestOrder(RestInvoice a, RestInvoice b)
    {
        var byDate = a.CreatedAt.CompareTo(b.CreatedAt);
        return byDate != 0 ? byDate : string.CompareOrdinal(a.PaymentHash, b.PaymentHash);
    }

    /// <summary>
    /// The <c>rest</c> of an open status (SPEC.md §31, "The status's rest"): null unless
    /// <paramref name="received"/> is above 0 before the invoice's end; else what is left, the latest
    /// rest invoice of exactly that amount neither paid nor ended (<paramref name="paid"/>: the payment
    /// hashes paid), and its QR code: on-chain to <paramref name="address"/> too from 1 000 sats, in
    /// Lightning only below.
    /// </summary>
    public static Rest? RestOf(long sats, long expiresAt, string address, long received, long now, IEnumerable<RestInvoice> rests, IReadOnlySet<string> paid)
    {
        if (received <= 0 || received >= sats || now >= expiresAt)
        {
            return null;
        }
        var rest = sats - received;
        var invoice = rests
            .OrderBy(candidate => candidate, Comparer<RestInvoice>.Create(ByRestOrder))
            .Reverse()
            .FirstOrDefault(candidate => candidate.Sats == rest && !paid.Contains(candidate.PaymentHash) && now < candidate.ExpiresAt);
        var bolt11 = invoice?.Bolt11;
        var paymentUri = rest >= Protocol.OnchainFloorSats
            ? Address.PaymentUri(address, rest, bolt11)
            : bolt11 is null ? null : $"lightning:{bolt11}";
        return new Rest(Numbers.Decimal(rest), bolt11, paymentUri);
    }

    /// <summary>
    /// Whether a check whose status gave <paramref name="rest"/> asks for an invoice of it (SPEC.md
    /// §31, "Asking for a rest invoice"), and which: none when it has one, with less than 60 s left,
    /// or when the sale's lease of the address is not the invoice's (<paramref name="lease"/>: its
    /// sats and end, null without one). The Lightning address's sendable sats are checked after
    /// (<see cref="Sendable"/>): its pay request is asked only then.
    /// </summary>
    public static RestAsk? ToAsk(Rest? rest, long sats, long expiresAt, long now, (long Sats, long ExpiresAt)? lease)
    {
        if (rest is null || rest.Bolt11 is not null)
        {
            return null;
        }
        if (now + Protocol.MinRestSeconds > expiresAt)
        {
            return null;
        }
        if (lease is not { } terms || terms.Sats != sats || terms.ExpiresAt != expiresAt)
        {
            return null;
        }
        return new RestAsk(long.Parse(rest.Sats, System.Globalization.CultureInfo.InvariantCulture), expiresAt - now);
    }

    /// <summary>Whether <paramref name="sats"/> is within the Lightning address's sendable sats.</summary>
    public static bool Sendable(long sats, (long Min, long Max) bounds) => sats >= bounds.Min && sats <= bounds.Max;

    /// <summary>
    /// The rest record of an invoice (SPEC.md §31, "Rest records"): its invoice and verify URL,
    /// sealed with the rest encryption key at the rest record address of its payment hash, signed by
    /// the rest secret at <paramref name="createdAt"/>, dropped by the relays a week after the end of
    /// the order's invoice (<paramref name="expiresAt"/>), as its checkout links.
    /// </summary>
    public static NostrEvent RecordEvent(RestKeys keys, string bolt11, string verifyUrl, string paymentHash, long expiresAt, long createdAt, Crypto.RandomSource random) =>
        Records.Seal(
            Protocol.RestRecordType,
            new JsonObject().Set("version", Protocol.RestVersion).Set("invoice", bolt11).Set("verifyUrl", verifyUrl),
            keys.SecretKey!,
            keys.EncryptionKey,
            Derive.RestRecordAddress(keys.Key, paymentHash),
            createdAt,
            random,
            [["expiration", Numbers.Decimal(expiresAt + Protocol.CheckoutLifeSeconds)]]);

    /// <summary>
    /// What a relay sent, if it is a rest record of the rest author and rest key of a checkout link
    /// of an invoice <paramref name="terms"/> describes (SPEC.md §31, "Reading a rest record"): null
    /// when it is not one; <see cref="OpenedRest.Newer"/> for one of a newer format, which the page
    /// leaves aside. An event at the link's acceptance record address (same author, which the read of
    /// rest records also returns) is no rest record, of a newer format or not.
    /// </summary>
    public static OpenedRest? OpenRecord(Json? value, RestKeys keys, RestLinkTerms terms)
    {
        if (Records.DTagOf(value) is not { } d || d == Derive.AcceptanceRecordAddress(keys.Key))
        {
            return null;
        }
        var opened = Records.Open(value, Protocol.RestRecordType, keys.EncryptionKey, d, keys.Author, (v, _) => ParseRecord(v, keys, terms, d));
        return opened is null ? null : new OpenedRest(opened.Newer, opened.Value, opened.Event);
    }

    private static RestInvoice? ParseRecord(Json value, RestKeys keys, RestLinkTerms terms, string d)
    {
        if (value is not JsonObject v)
        {
            return null;
        }
        if (JsonRead.IsNewerVersion(v["version"], Protocol.RestVersion))
        {
            throw new NewerRecordException();
        }
        if (!JsonRead.IsNumber(v["version"], Protocol.RestVersion) || JsonRead.String(v["invoice"]) is not { } invoice)
        {
            return null;
        }
        if (JsonRead.String(v["verifyUrl"]) is not { } verifyUrl || Urls.ParseHttps(verifyUrl) is null)
        {
            return null;
        }
        var decoded = Bolt11.Decode(invoice);
        if (decoded is null || decoded.Network != "bc" || decoded.AmountMsat is not { } msat || msat <= 0 || msat % 1000 != 0)
        {
            return null;
        }
        var sats = msat / 1000;
        var end = decoded.Timestamp + decoded.ExpirySeconds;
        if (sats >= terms.Sats || decoded.Timestamp < terms.CreatedAt - Protocol.ClockToleranceSeconds
            || end > terms.ExpiresAt + Protocol.ClockToleranceSeconds)
        {
            return null;
        }
        if (d != Derive.RestRecordAddress(keys.Key, decoded.PaymentHash))
        {
            return null;
        }
        return new RestInvoice(invoice, decoded.PaymentHash, verifyUrl, sats, decoded.Timestamp, end);
    }

    /// <summary>
    /// The rest records of <paramref name="keys"/> on the merchant's <paramref name="relays"/>
    /// (<see cref="RestRecordsRead"/>); null when fewer than a majority answered. Read with a key of
    /// this read alone, not paged (a rest record per rest invoice: a few).
    /// </summary>
    public static async Task<RestRecordsRead?> ReadRecordsAsync(RelayPool pool, RestKeys keys, RestLinkTerms terms, IReadOnlyList<string> relays,
        Crypto.RandomSource random, CancellationToken cancellationToken)
    {
        var needed = RelayPool.Majority(relays.Count);
        var answers = await pool.QueryRelaysAsync(
            relays,
            new RelayFilter { Kinds = [Protocol.RecordKind], Authors = [keys.Author] },
            random.Bytes(32),
            needed,
            paged: false,
            Records.ThatOpen((_, author) => author == keys.Author ? keys.EncryptionKey : null)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (answers.Answered < needed)
        {
            return null;
        }
        // How many relays sent a valid record of each payment hash; where a relay sent one of a newer format.
        var sentBy = new Dictionary<string, int>(StringComparer.Ordinal);
        var newer = new HashSet<string>(StringComparer.Ordinal);
        foreach (var events in answers.Answers.Values)
        {
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in events)
            {
                switch (OpenRecord((Json)value, keys, terms))
                {
                    case { Newer: true } unread:
                        newer.Add(unread.Event.TagValue("d")!);
                        break;
                    case { Invoice: { } invoice }:
                        hashes.Add(invoice.PaymentHash);
                        break;
                }
            }
            foreach (var hash in hashes)
            {
                sentBy[hash] = sentBy.GetValueOrDefault(hash) + 1;
            }
        }
        return new RestRecordsRead(sentBy.Where(entry => entry.Value >= needed).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal), newer);
    }

    /// <summary>
    /// Publishes a rest record on the merchant's <paramref name="relays"/>, signed and authenticated
    /// with the rest secret: completes once a majority took it, else throws <see cref="NetworkException"/>.
    /// </summary>
    public static async Task PublishRecordAsync(RelayPool pool, NostrEvent @event, RestKeys keys, IReadOnlyList<string> relays, CancellationToken cancellationToken)
    {
        try
        {
            await pool.PublishToQuorumAsync(relays, @event, keys.SecretKey!, RelayPool.Majority(relays.Count)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NetworkException("Fewer than a majority of the merchant's relays took the record of an invoice of the rest", error);
        }
    }
}
