using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Sattleme.Checkout.Internal.Nostr;
using Sattleme.Checkout.Internal.Onchain;

namespace Sattleme.Checkout.Internal;

/// <summary>
/// A declared invoice, with its signed declaration (null for an order paid on-chain only) and lease
/// (null without an address): published again when the invoice may still be paid.
/// </summary>
internal sealed record SaleDeclaration(DeclaredInvoice Invoice, NostrEvent? Event, NostrEvent? Lease)
{
    /// <summary>
    /// The invoice's last record: its declaration, or for an invoice paid on-chain only its lease's
    /// event (its mark). The one shown has it on a majority of the relays; one whose createInvoice
    /// failed before that, on a minority.
    /// </summary>
    public NostrEvent LastRecord => Event ?? Lease!;
}

/// <summary>What the registry holds of a sale (<see cref="Sales.ReadAsync"/>).</summary>
/// <param name="Invoices">The order's invoices, oldest first.</param>
/// <param name="Rests">
/// The rest invoices of each invoice with an address (SPEC.md §31, "The rest of an underpayment"),
/// by <see cref="Sales.RestKeyOf"/> of that invoice (its address and end): its declarations with
/// <c>rest</c>, by date then payment hash.
/// </param>
/// <param name="UnsureRests">
/// The declarations of rest invoices fewer than a majority of the merchant's relays sent, by payment
/// hash: their publication may have reached a minority only (it failed), and a later read may miss
/// them. Published again on a majority before they are used.
/// </param>
/// <param name="Leases">The sale's leases still held (not withdrawn), by address: the order's terms.</param>
/// <param name="SentBy">How many relays sent each event, by id: of a read on every relay, which relays hold it.</param>
internal sealed record SaleRecords(
    IReadOnlyList<SaleDeclaration> Invoices,
    IReadOnlyDictionary<string, List<RestInvoice>> Rests,
    IReadOnlyDictionary<string, NostrEvent> UnsureRests,
    IReadOnlyDictionary<string, OpenedLease> Leases,
    IReadOnlyDictionary<string, int> SentBy);

/// <summary>What step 1 of SPEC.md §21 decides of the invoices that may still be paid.</summary>
internal enum PendingKind
{
    Paid,
    Failed,
    Unread,
    HandOut,
}

/// <summary>Step 1's outcome: of <see cref="PendingKind.Paid"/>, <see cref="PendingKind.Failed"/> and <see cref="PendingKind.HandOut"/>, the invoice's index.</summary>
internal readonly record struct PendingOutcome(PendingKind Kind, int Index = -1);

/// <summary>
/// The invoices declared for a sale, read back from the merchant's registry (SPEC.md §21): its
/// declarations and its leases of deposit addresses. What CreateInvoiceAsync checks before a new
/// invoice, and FindInvoicesAsync gives.
/// </summary>
internal static class Sales
{
    /// <summary>
    /// The invoices of <paramref name="sale"/> on <paramref name="relays"/>, oldest first (by date,
    /// then payment hash, then address): read page after page as soon as a majority of the relays
    /// answered (with <paramref name="everyRelay"/>, on every relay). One per payment hash, with the
    /// address of the sale's confirmed lease naming its invoice; and one per confirmed lease of an
    /// order paid on-chain only. A lease naming an invoice without a declaration is left out: that
    /// invoice was never shown. A declaration with <c>rest</c> is no invoice of the order: one of the
    /// rest invoices of the invoice it names. Throws <see cref="NetworkException"/> when fewer than a
    /// majority answered.
    /// </summary>
    public static async Task<SaleRecords> ReadAsync(RelayPool pool, Sale sale, IReadOnlyList<string> relays, Warnings warnings, bool everyRelay,
        CancellationToken cancellationToken)
    {
        var needed = RelayPool.Majority(relays.Count);
        var answers = await pool.QueryRelaysAsync(
            relays,
            new RelayFilter { Kinds = [Protocol.RecordKind], Authors = [sale.Author] },
            sale.SecretKey,
            needed,
            paged: true,
            // Its declarations and its records at registry addresses: sealed with the outer key of
            // their address and of the sale.
            Records.ThatOpen((d, author) => author == sale.Author && d is not null ? Derive.OuterKey(sale.Registry.Key, d, author) : null),
            everyRelay).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Answered: the relays whose read ended; one still reading on every relay sent events, no answer.
        if (answers.Answered < needed)
        {
            throw new NetworkException("Fewer than a majority of the merchant's relays answered");
        }
        var declarations = new Dictionary<string, OpenedDeclaration>(StringComparer.Ordinal);
        var declarationOrder = new List<string>();
        // The sale's records at registry addresses of addresses.
        var records = new List<SaleLeaseRecord>();
        // Event ids that checked out: another copy of one is the same event, not opened again.
        var checkedIds = new HashSet<string>(StringComparer.Ordinal);
        var sentBy = new Dictionary<string, int>(StringComparer.Ordinal);
        var newer = false;
        foreach (var events in answers.Events)
        {
            // A relay counts for an event when it sent a copy whose id is its hash: it holds that
            // event. One still reading on every relay too, for what its pages hold (it has not
            // answered, but it sent them).
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (Json value in events)
            {
                if (Events.WithValidId(value) is { } withId)
                {
                    ids.Add(withId.Id);
                }
            }
            foreach (var id in ids)
            {
                sentBy[id] = sentBy.GetValueOrDefault(id) + 1;
            }
            foreach (Json value in events)
            {
                if (value is JsonObject o && JsonRead.String(o["id"]) is { } id && checkedIds.Contains(id))
                {
                    continue;
                }
                OpenedDeclaration? declaration;
                SaleLeaseRecord? lease = null;
                try
                {
                    declaration = Declarations.Open(value, sale);
                    if (declaration is null)
                    {
                        lease = Leases.OpenSaleLease(value, sale);
                    }
                }
                catch (NewerRecordException)
                {
                    newer = true;
                    // Authentic (its signature checked before its format), at a registry address: the
                    // newest there, it leaves no lease of the sale.
                    if (Records.DTagOf(value) is { } d && Events.WithValidId(value) is { } @event)
                    {
                        checkedIds.Add(@event.Id);
                        records.Add(new SaleLeaseRecord(SaleLeaseKind.Newer, @event, d));
                    }
                    continue;
                }
                if (declaration is not null)
                {
                    checkedIds.Add(declaration.Event.Id);
                    var hash = declaration.Decoded.PaymentHash;
                    if (declarations.TryAdd(hash, declaration))
                    {
                        declarationOrder.Add(hash);
                    }
                }
                else if (lease is not null)
                {
                    checkedIds.Add(lease.Event.Id);
                    records.Add(lease);
                }
            }
        }
        if (newer)
        {
            warnings.Warn("a declaration or a lease of this order has a newer format than this version reads; update the package");
        }
        var newest = NewestRecords(records);
        var held = new Dictionary<string, OpenedLease>(StringComparer.Ordinal);
        foreach (var record in newest)
        {
            if (record.Kind == SaleLeaseKind.Lease)
            {
                held[record.Lease!.Terms.Address] = record.Lease;
            }
        }
        var rests = new Dictionary<string, List<RestInvoice>>(StringComparer.Ordinal);
        var unsureRests = new Dictionary<string, NostrEvent>(StringComparer.Ordinal);
        var invoices = new List<OpenedDeclaration>();
        foreach (var hash in declarationOrder)
        {
            var declaration = declarations[hash];
            if (declaration.Rest is null)
            {
                invoices.Add(declaration);
                continue;
            }
            if (sentBy.GetValueOrDefault(declaration.Event.Id) < needed)
            {
                unsureRests[declaration.Decoded.PaymentHash] = declaration.Event;
            }
            var key = RestKeyOf(declaration.Rest.Address, declaration.Rest.ExpiresAt);
            if (!rests.TryGetValue(key, out var list))
            {
                list = [];
                rests[key] = list;
            }
            list.Add(Onchain.Rests.InvoiceOf(declaration));
        }
        foreach (var list in rests.Values)
        {
            list.Sort(Onchain.Rests.ByRestOrder);
        }
        return new SaleRecords(InvoicesOf(sale.Ref, invoices, newest), rests, unsureRests, held, sentBy);
    }

    /// <summary>
    /// Of the sale's records at registry addresses, the newest at each (the highest <c>created_at</c>,
    /// then the lowest id, as relays keep them): its lease there, or its withdrawal, which leaves none.
    /// </summary>
    public static List<SaleLeaseRecord> NewestRecords(IEnumerable<SaleLeaseRecord> records)
    {
        var newest = new Dictionary<string, SaleLeaseRecord>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var record in records)
        {
            if (!newest.TryGetValue(record.D, out var known))
            {
                order.Add(record.D);
                newest[record.D] = record;
            }
            else if (Records.Replaces(record.Event.CreatedAt, record.Event.Id, known.Event.CreatedAt, known.Event.Id))
            {
                newest[record.D] = record;
            }
        }
        return order.Select(d => newest[d]).ToList();
    }

    /// <summary>
    /// The sale's invoices, oldest first (by date, then payment hash, none first, then address), from
    /// its declarations (one per payment hash; one with <c>rest</c> gives none) and its newest record
    /// at each registry address. Only a confirmed lease gives an address (SPEC.md §31, "Leasing an
    /// address"): one that is not was left by a createInvoice that failed before the address was the
    /// order's. One invoice per declaration, with the address of the newest confirmed lease naming its
    /// invoice; and one per confirmed lease of an order paid on-chain only. A lease naming an invoice
    /// without a declaration is left out: that invoice was never shown.
    /// </summary>
    public static List<SaleDeclaration> InvoicesOf(string reference, IEnumerable<OpenedDeclaration> declarations, IReadOnlyList<SaleLeaseRecord> newest)
    {
        var confirmed = newest.Where(record => record.Kind == SaleLeaseKind.Lease && record.Lease!.Terms.IsConfirmed).Select(record => record.Lease!).ToList();
        var found = new List<SaleDeclaration>();
        foreach (var declaration in declarations)
        {
            // A rest invoice's: no invoice of the order.
            if (declaration.Rest is not null)
            {
                continue;
            }
            var decoded = declaration.Decoded;
            var invoice = Text.AsciiLower(declaration.Invoice);
            var lease = confirmed
                .Where(candidate => candidate.Terms.Invoice is not null && Text.AsciiLower(candidate.Terms.Invoice) == invoice)
                .OrderBy(candidate => candidate.Event, Comparer<NostrEvent>.Create(NewestFirst))
                .FirstOrDefault();
            var sats = decoded.AmountMsat!.Value / 1000;
            var onchainAddress = lease?.Terms.Address;
            found.Add(new SaleDeclaration(
                new DeclaredInvoice(
                    reference,
                    declaration.Invoice,
                    decoded.PaymentHash,
                    declaration.VerifyUrl,
                    onchainAddress,
                    Address.PaymentUri(onchainAddress, sats, declaration.Invoice),
                    Numbers.Decimal(sats),
                    FiatOf(declaration.Price),
                    declaration.Note,
                    decoded.Timestamp,
                    decoded.Timestamp + decoded.ExpirySeconds,
                    declaration.DeclaredAt),
                declaration.Event,
                lease?.Event));
        }
        foreach (var lease in confirmed)
        {
            if (lease.Terms.Invoice is not null)
            {
                continue;
            }
            var terms = lease.Terms;
            found.Add(new SaleDeclaration(
                new DeclaredInvoice(
                    reference,
                    null,
                    null,
                    null,
                    terms.Address,
                    Address.PaymentUri(terms.Address, terms.Sats, null),
                    Numbers.Decimal(terms.Sats),
                    FiatOf(terms.Price),
                    terms.Note,
                    terms.CreatedAt,
                    terms.ExpiresAt,
                    // Its mark's date (SPEC.md §31, "Leasing an address").
                    lease.Event.CreatedAt),
                null,
                lease.Event));
        }
        return found
            .OrderBy(entry => entry.Invoice.CreatedAt)
            .ThenBy(entry => entry.Invoice.PaymentHash ?? "", StringComparer.Ordinal)
            .ThenBy(entry => entry.Invoice.OnchainAddress ?? "", StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Newest first, as relays keep them.</summary>
    private static int NewestFirst(NostrEvent a, NostrEvent b) =>
        Records.Replaces(a.CreatedAt, a.Id, b.CreatedAt, b.Id) ? -1 : Records.Replaces(b.CreatedAt, b.Id, a.CreatedAt, a.Id) ? 1 : 0;

    /// <summary>
    /// Step 1 of SPEC.md §21: the indices of the invoices (oldest first) that may still be paid at
    /// <paramref name="now"/>, until their end and its grace period.
    /// </summary>
    public static List<int> MayStillBePaid(IReadOnlyList<DeclaredInvoice> invoices, long now)
    {
        var indices = new List<int>();
        for (var index = 0; index < invoices.Count; index++)
        {
            if (now < invoices[index].ExpiresAt + Protocol.GraceSeconds)
            {
                indices.Add(index);
            }
        }
        return indices;
    }

    /// <summary>
    /// Of the invoices that may still be paid (oldest first, one or more), from the payment check of
    /// each and, of several, whether a majority of the relays sent its last record (<paramref name="held"/>,
    /// read on every relay; null when fewer than a majority answered that read): the first paid gives
    /// order_paid, whatever the read (a payment proves itself); else the first error is the error
    /// (that invoice may have been paid); else, when the read was not answered by a majority, it is
    /// unread (network). Else the one handed out is the only one; of several, the only one held (the
    /// one shown), else the newest that received something (unconfirmed, or open with received above
    /// 0), else the newest.
    /// </summary>
    public static PendingOutcome PendingOutcomeOf(IReadOnlyList<Settled<PaymentStatus>> statuses, IReadOnlyList<bool>? held)
    {
        for (var index = 0; index < statuses.Count; index++)
        {
            if (statuses[index] is { Ok: true, Value: PaymentStatus.Paid })
            {
                return new PendingOutcome(PendingKind.Paid, index);
            }
        }
        for (var index = 0; index < statuses.Count; index++)
        {
            if (!statuses[index].Ok)
            {
                return new PendingOutcome(PendingKind.Failed, index);
            }
        }
        if (statuses.Count == 1)
        {
            return new PendingOutcome(PendingKind.HandOut, 0);
        }
        if (held is null)
        {
            return new PendingOutcome(PendingKind.Unread);
        }
        var shown = Enumerable.Range(0, held.Count).Where(index => held[index]).ToList();
        if (shown.Count == 1)
        {
            return new PendingOutcome(PendingKind.HandOut, shown[0]);
        }
        for (var index = statuses.Count - 1; index >= 0; index--)
        {
            var status = statuses[index].Value;
            if (status is PaymentStatus.Unconfirmed
                || (status is PaymentStatus.Open open && BigInteger.Parse(open.Received, System.Globalization.CultureInfo.InvariantCulture) > 0))
            {
                return new PendingOutcome(PendingKind.HandOut, index);
            }
        }
        return new PendingOutcome(PendingKind.HandOut, statuses.Count - 1);
    }

    /// <summary>The key, in <see cref="SaleRecords.Rests"/>, of the invoice with <paramref name="address"/> and the end <paramref name="expiresAt"/>.</summary>
    public static string RestKeyOf(string address, long expiresAt) => $"{address}/{Numbers.Decimal(expiresAt)}";

    /// <summary>The price of a declared sale as a Fiat: null for a sale in sats.</summary>
    public static Fiat? FiatOf(DeclaredPrice price) =>
        price.Currency == Protocol.SatsCurrency
            ? null
            : new Fiat(Prices.FormatDecimal(new PriceDecimal(price.Minor, price.Digits)), price.Currency, Numbers.Decimal(price.Minor), price.Digits);
}
