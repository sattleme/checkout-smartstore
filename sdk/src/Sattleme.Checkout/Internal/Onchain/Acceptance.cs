using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sattleme.Checkout.Internal.Crypto;
using Sattleme.Checkout.Internal.Nostr;

namespace Sattleme.Checkout.Internal.Onchain;

/// <summary>An acceptance record, opened as the page reads it: its <c>received</c>; or one of a newer format.</summary>
internal sealed record OpenedAcceptance(bool Newer, long Received, NostrEvent Event);

/// <summary>
/// Acceptance records (SPEC.md §31, "Acceptance records"): the page of a checkout link cannot know the
/// merchant's tolerance nor read the merchant's acceptance (<see cref="MerchantAcceptances"/>), so an
/// order either accepted would read underpaid there, its customer asked for the rest and never sent
/// back to the shop. A check whose status is paid (or being confirmed) with less than the amount
/// publishes what it received then, signed by the rest secret, for the pages of the order's checkout
/// links: they count the payment as enough from that on. Never the tolerance itself.
/// </summary>
internal static class Acceptances
{
    /// <summary>
    /// What a check whose status is <paramref name="status"/> must have the pages of the order's
    /// checkout links know (SPEC.md §31, "Publishing it"): the status's <c>received</c>, when the
    /// order's tolerance or the merchant's acceptance accepted less than its amount (unconfirmed, or
    /// paid with transactions) before the links' life ends; null otherwise.
    /// </summary>
    public static long? ToPublish(PaymentStatus status, long sats, long expiresAt, long now)
    {
        if (now >= expiresAt + Protocol.CheckoutLifeSeconds)
        {
            return null;
        }
        string? received = status switch
        {
            PaymentStatus.Unconfirmed unconfirmed => unconfirmed.Received,
            PaymentStatus.Paid { Method: not PaymentMethod.Lightning, Txids.Count: > 0 } paid => paid.Received,
            _ => null,
        };
        if (received is null)
        {
            return null;
        }
        var value = long.Parse(received, System.Globalization.CultureInfo.InvariantCulture);
        return value < sats ? value : null;
    }

    /// <summary>
    /// The acceptance record of the invoice of <paramref name="keys"/> (its rest keys):
    /// <paramref name="received"/>, sealed with the rest encryption key at its acceptance record
    /// address, signed by the rest secret at <paramref name="createdAt"/>, dropped by the relays a week
    /// after the invoice's end (<paramref name="expiresAt"/>), as its checkout links.
    /// </summary>
    public static NostrEvent RecordEvent(RestKeys keys, long received, long expiresAt, long createdAt, RandomSource random) =>
        Records.Seal(
            Protocol.AcceptanceRecordType,
            new JsonObject().Set("version", Protocol.AcceptanceVersion).Set("received", received),
            keys.SecretKey!,
            keys.EncryptionKey,
            Derive.AcceptanceRecordAddress(keys.Key),
            createdAt,
            random,
            [["expiration", Numbers.Decimal(expiresAt + Protocol.CheckoutLifeSeconds)]]);

    /// <summary>The <c>received</c> of an acceptance record's value, boxed for <see cref="Records.Open{T}"/>.</summary>
    private sealed record Received(long Value);

    private static OpenedRecord<Received>? Open(Json? value, RestKeys keys, long sats)
    {
        var address = Derive.AcceptanceRecordAddress(keys.Key);
        if (Records.DTagOf(value) != address)
        {
            return null;
        }
        return Records.Open(value, Protocol.AcceptanceRecordType, keys.EncryptionKey, address, keys.Author, (v, _) =>
        {
            if (v is not JsonObject o)
            {
                return null;
            }
            if (JsonRead.IsNewerVersion(o["version"], Protocol.AcceptanceVersion))
            {
                throw new NewerRecordException();
            }
            if (!JsonRead.IsNumber(o["version"], Protocol.AcceptanceVersion))
            {
                return null;
            }
            return JsonRead.SafeInteger(o["received"]) is { } received && received >= 1 && received < sats ? new Received(received) : null;
        });
    }

    /// <summary>
    /// What a relay sent, if it is an acceptance record of the rest author and rest key of a checkout
    /// link of an invoice of <paramref name="sats"/> (SPEC.md §31, "Reading an acceptance record"):
    /// its <c>received</c>, from 1, below <paramref name="sats"/>; null when it is not one;
    /// <see cref="OpenedAcceptance.Newer"/> for one of a newer format, left aside.
    /// </summary>
    public static OpenedAcceptance? OpenRecord(Json? value, RestKeys keys, long sats) =>
        Open(value, keys, sats) is { } opened ? new OpenedAcceptance(opened.Newer, opened.Value?.Value ?? 0, opened.Event) : null;

    /// <summary>
    /// Whether the pages already know <paramref name="received"/> (SPEC.md §31, "Publishing it"): a
    /// majority of the merchant's <paramref name="relays"/> each sent a valid acceptance record of the
    /// invoice of <paramref name="keys"/> (of <paramref name="sats"/>) whose <c>received</c>, the
    /// newest it sent, is at most <paramref name="received"/>. Read with a key of this read alone, not
    /// paged (one record per invoice). True when a relay sent one of a newer format, never written
    /// over (SPEC.md §12), and logged. Throws <see cref="NetworkException"/> when fewer than a majority
    /// answered: no answer, never an absence (SPEC.md §13); the check logs it, and the next one
    /// publishes the record.
    /// </summary>
    public static async Task<bool> KnownAsync(RelayPool pool, RestKeys keys, long sats, long received, IReadOnlyList<string> relays,
        RandomSource random, Warnings warnings, CancellationToken cancellationToken)
    {
        var needed = RelayPool.Majority(relays.Count);
        var answers = await pool.QueryRelaysAsync(
            relays,
            new RelayFilter { Kinds = [Protocol.RecordKind], Authors = [keys.Author], D = [Derive.AcceptanceRecordAddress(keys.Key)] },
            random.Bytes(32),
            needed,
            paged: false,
            Records.ThatOpen((_, author) => author == keys.Author ? keys.EncryptionKey : null)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var holders = 0;
        foreach (var events in answers.Answers.Values)
        {
            var newest = Records.Newest([[.. ToJson(events)]], value => Open(value, keys, sats));
            if (newest.NewestUnreadable is not null)
            {
                warnings.Warn("an acceptance record of this order has a newer format than this version reads; update the package");
                return true;
            }
            if (newest.Value is { } value && value.Value <= received)
            {
                holders++;
            }
        }
        if (answers.Answered < needed)
        {
            throw new NetworkException("Fewer than a majority of the merchant's relays answered the read of the acceptance records");
        }
        return holders >= needed;
    }

    private static IEnumerable<Json> ToJson(IReadOnlyList<object> events)
    {
        foreach (var value in events)
        {
            yield return (Json)value;
        }
    }

    /// <summary>
    /// Publishes an acceptance record on the merchant's <paramref name="relays"/>, signed and
    /// authenticated with the rest secret: completes once a majority took it, else throws
    /// <see cref="NetworkException"/>.
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
            throw new NetworkException("Fewer than a majority of the merchant's relays took the acceptance record of a payment", error);
        }
    }
}
