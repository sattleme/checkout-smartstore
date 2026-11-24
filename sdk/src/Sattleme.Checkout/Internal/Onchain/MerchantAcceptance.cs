using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sattleme.Checkout.Internal.Crypto;
using Sattleme.Checkout.Internal.Nostr;

namespace Sattleme.Checkout.Internal.Onchain;

/// <summary>What a check opens the merchant's acceptance of an invoice with. Its encryption key is a secret: never logged.</summary>
[DebuggerDisplay("MerchantAcceptanceKeys {{ team = {Team}, address = {Address} }}")]
internal sealed class MerchantAcceptanceKeys(string team, byte[] encryptionKey, string address)
{
    /// <summary>The team's public key (hex): its only author.</summary>
    public string Team { get; } = team;

    /// <summary>The channel's encryption key: its NIP-44 conversation key.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public byte[] EncryptionKey { get; } = encryptionKey;

    /// <summary>Its <c>d</c> tag (<see cref="Derive.MerchantAcceptanceAddress"/>).</summary>
    public string Address { get; } = address;

    public override string ToString() => $"MerchantAcceptanceKeys {{ team = {Team}, address = {Address} }}";
}

/// <summary>The invoice a merchant's acceptance is read for: its amount and its end.</summary>
internal sealed record AcceptedInvoice(long Sats, long ExpiresAt);

/// <summary>How a merchant's acceptance a relay sent opened.</summary>
internal enum MerchantAcceptanceFormat
{
    /// <summary>Valid: what the order had received when the merchant accepted it.</summary>
    Valid,

    /// <summary>Of a newer format: logged; the newest a majority sent, it leaves the order without one.</summary>
    Newer,

    /// <summary>Of a newer format, dated after the lease's end: set aside as any record dated so, logged.</summary>
    NewerSetAside,
}

/// <summary>A merchant's acceptance a relay sent, opened (<see cref="MerchantAcceptances.Open"/>): <paramref name="Received"/> of a valid one.</summary>
internal sealed record OpenedMerchantAcceptance(MerchantAcceptanceFormat Format, NostrEvent Event, long Received);

/// <summary>The merchant's acceptance of an invoice, as a check counts it.</summary>
/// <param name="Received">The <c>received</c> of the merchant's acceptance that counts; null without one.</param>
/// <param name="Newer">Whether a relay sent one of a newer format, whatever its date and how many relays sent it.</param>
internal sealed record MerchantAcceptance(long? Received, bool Newer)
{
    /// <summary>None, and none of a newer format: what a check that reads none counts.</summary>
    public static MerchantAcceptance None { get; } = new(null, false);
}

/// <summary>
/// The merchant's acceptance (SPEC.md §31, "The merchant's acceptance"): the merchant may accept by
/// hand, in its app, what an order underpaid on-chain received as paying it in full. The app
/// publishes a record of the team (the integration key opens it but cannot make one) holding what
/// the order had received then; a check judges the payment against that amount when it is less than
/// the least the order's tolerance accepts (<see cref="Tolerance.DueOnchain"/>). The majority of the
/// merchant's relays holds the truth: a check reads it on every relay and counts a record only when a
/// majority of the relays sent it. A check never publishes one, nor publishes one again: the pages of
/// the order's checkout links learn of it only through an acceptance record (<see cref="Acceptances"/>).
/// </summary>
internal static class MerchantAcceptances
{
    /// <summary>
    /// The keys of the merchant's acceptance of the invoice of the sale <paramref name="saleAuthor"/>
    /// with the deposit address <paramref name="address"/> and the end <paramref name="expiresAt"/>,
    /// for the integration <paramref name="integrationKey"/> of <paramref name="team"/>.
    /// </summary>
    public static MerchantAcceptanceKeys KeysOf(byte[] integrationKey, string team, string saleAuthor, string address, long expiresAt) =>
        new(team, Derive.ChannelKeys(integrationKey).EncryptionKey, Derive.MerchantAcceptanceAddress(integrationKey, saleAuthor, address, expiresAt));

    /// <summary>The <c>received</c> of a valid merchant's acceptance, boxed for <see cref="Records.Open{T}"/>.</summary>
    private sealed record Received(long Value);

    /// <summary>
    /// What a relay sent, read as the merchant's acceptance of <paramref name="invoice"/> (SPEC.md §31,
    /// "Reading it"): a record of type <c>integration-onchain-accepted</c>, author the team, sealed
    /// with the channel's key, at the address of <paramref name="keys"/>, that checks out (its id, its
    /// signature); valid when its value is an object whose <c>version</c> is 1 and whose
    /// <c>received</c> is a safe integer from 1, below the invoice's <c>sats</c> (more fields
    /// ignored), of a newer format when its <c>version</c> is above 1 or its content of a newer NIP-44
    /// version. Null for anything else, and for a record dated after the lease's end
    /// (<c>expiresAt</c> + 120), which is set aside whatever its format: one of a newer format is still
    /// told, for the warning (<see cref="MerchantAcceptanceFormat.NewerSetAside"/>). Its
    /// <c>created_at</c> is never compared with now.
    /// </summary>
    public static OpenedMerchantAcceptance? Open(Json? value, MerchantAcceptanceKeys keys, AcceptedInvoice invoice)
    {
        if (Records.DTagOf(value) != keys.Address)
        {
            return null;
        }
        var opened = Records.Open(value, Protocol.MerchantAcceptanceRecordType, keys.EncryptionKey, keys.Address, keys.Team, (v, _) =>
        {
            if (v is not JsonObject o)
            {
                return null;
            }
            if (JsonRead.IsNewerVersion(o["version"], Protocol.MerchantAcceptanceVersion))
            {
                throw new NewerRecordException();
            }
            if (!JsonRead.IsNumber(o["version"], Protocol.MerchantAcceptanceVersion))
            {
                return null;
            }
            return JsonRead.SafeInteger(o["received"]) is { } received && received >= 1 && received < invoice.Sats ? new Received(received) : null;
        });
        if (opened is null)
        {
            return null;
        }
        // After the lease's end: set aside, of a newer format or not (SPEC.md §31, "Reading it").
        var late = opened.Event.CreatedAt > invoice.ExpiresAt + Protocol.GraceSeconds;
        if (opened.Newer)
        {
            return new OpenedMerchantAcceptance(late ? MerchantAcceptanceFormat.NewerSetAside : MerchantAcceptanceFormat.Newer, opened.Event, 0);
        }
        return late ? null : new OpenedMerchantAcceptance(MerchantAcceptanceFormat.Valid, opened.Event, opened.Value!.Value);
    }

    /// <summary>
    /// The merchant's acceptance of <paramref name="invoice"/> (SPEC.md §31, "Counting it"), from what
    /// each relay of the merchant's list (<paramref name="relayCount"/> relays) sent to its read on
    /// every relay (<paramref name="answers"/>, one list per relay that answered): every copy opened
    /// (<see cref="Open"/>), once per id and signature; a relay sent a record when it sent a copy of it
    /// that opens. Of the records valid or of a newer format that a majority of the list sent, the
    /// newest (the highest <c>created_at</c>, then the lowest id, as relays keep them) decides: a
    /// valid one is the merchant's acceptance, one of a newer format leaves the order without one. One
    /// that fewer than a majority sent never counts, whichever relays sent it.
    /// </summary>
    public static MerchantAcceptance Of(IEnumerable<IEnumerable<Json>> answers, MerchantAcceptanceKeys keys, AcceptedInvoice invoice, int relayCount)
    {
        // Each copy opened once, by id and signature, whatever relays send it.
        var copies = new Dictionary<string, OpenedMerchantAcceptance?>(StringComparer.Ordinal);
        OpenedMerchantAcceptance? OpenOnce(Json value)
        {
            if (value is not JsonObject o || JsonRead.String(o["id"]) is not { } id || JsonRead.String(o["sig"]) is not { } sig)
            {
                return Open(value, keys, invoice);
            }
            var copy = id + "/" + sig;
            if (!copies.TryGetValue(copy, out var opened))
            {
                opened = Open(value, keys, invoice);
                copies[copy] = opened;
            }
            return opened;
        }
        // The records that count if enough relays sent them, by id, and how many relays sent each.
        var records = new Dictionary<string, (OpenedMerchantAcceptance Opened, int SentBy)>(StringComparer.Ordinal);
        var newer = false;
        foreach (var events in answers)
        {
            var sent = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in events)
            {
                if (OpenOnce(value) is not { } opened)
                {
                    continue;
                }
                if (opened.Format != MerchantAcceptanceFormat.Valid)
                {
                    newer = true;
                }
                if (opened.Format == MerchantAcceptanceFormat.NewerSetAside)
                {
                    continue;
                }
                var id = opened.Event.Id;
                // One relay counts once for a record, whatever copies of it it sent.
                if (!sent.Add(id))
                {
                    continue;
                }
                records[id] = records.TryGetValue(id, out var known) ? (known.Opened, known.SentBy + 1) : (opened, 1);
            }
        }
        var needed = RelayPool.Majority(relayCount);
        OpenedMerchantAcceptance? newest = null;
        foreach (var (opened, sentBy) in records.Values)
        {
            if (sentBy < needed)
            {
                continue;
            }
            if (newest is null || Records.Replaces(opened.Event.CreatedAt, opened.Event.Id, newest.Event.CreatedAt, newest.Event.Id))
            {
                newest = opened;
            }
        }
        return new MerchantAcceptance(newest is { Format: MerchantAcceptanceFormat.Valid } ? newest.Received : null, newer);
    }

    /// <summary>
    /// The merchant's acceptance of <paramref name="invoice"/> (<see cref="Of"/>), read on every relay
    /// of the merchant's <paramref name="relays"/> (SPEC.md §13: once every relay answered or failed,
    /// or one timeout after a majority answered), not paged, with a key of this read alone. Throws
    /// <see cref="NetworkException"/> when fewer than a majority answered. Publishes nothing.
    /// </summary>
    public static async Task<MerchantAcceptance> ReadAsync(RelayPool pool, MerchantAcceptanceKeys keys, AcceptedInvoice invoice, IReadOnlyList<string> relays,
        RandomSource random, CancellationToken cancellationToken)
    {
        var needed = RelayPool.Majority(relays.Count);
        var answers = await pool.QueryRelaysAsync(
            relays,
            new RelayFilter { Kinds = [Protocol.RecordKind], Authors = [keys.Team], D = [keys.Address] },
            random.Bytes(32),
            needed,
            paged: false,
            Records.ThatOpen((d, author) => author == keys.Team && d == keys.Address ? keys.EncryptionKey : null),
            everyRelay: true).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Answered: the relays whose read ended; one still reading sent nothing that counts.
        if (answers.Answered < needed)
        {
            throw new NetworkException("Fewer than a majority of the merchant's relays answered");
        }
        return Of(answers.Answers.Values.Select(events => events.Cast<Json>()), keys, invoice, relays.Count);
    }
}
