using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Sattleme.Checkout.Internal.Onchain;

/// <summary>A part's outcome: its result, or the error it failed with.</summary>
internal sealed record Settled<T>(bool Ok, T? Value, Exception? Error)
{
    public static Settled<T> Of(T value) => new(true, value, null);

    public static Settled<T> Failed(Exception error) => new(false, default, error);

    /// <summary>The outcome of <paramref name="task"/>, never thrown; a cancellation of <paramref name="cancellationToken"/> is.</summary>
    public static async Task<Settled<T>> OfAsync(Task<T> task, CancellationToken cancellationToken)
    {
        try
        {
            return Of(await task.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A part that fails is its error, kept for the status.
        catch (Exception error)
#pragma warning restore CA1031
        {
            return Failed(error);
        }
    }
}

/// <summary>What the on-chain part of an invoice is.</summary>
internal enum OnchainKind
{
    /// <summary>Less than the amount came in time: final once the lease ended and nothing is undecided.</summary>
    Underpaid,
    Unconfirmed,

    /// <summary>3 confirmations on the explorer read.</summary>
    Paid,
}

/// <summary>The on-chain part of an invoice with an address.</summary>
internal sealed record OnchainPart(OnchainKind Kind, long Received, bool Final = false, long Confirmations = 0, IReadOnlyList<string>? Txids = null);

/// <summary>What the on-chain part of an invoice needs.</summary>
internal sealed class OnchainCheck
{
    /// <summary>The invoice's deposit address.</summary>
    public required string Address { get; init; }

    public required long Sats { get; init; }

    public required long ExpiresAt { get; init; }

    /// <summary>The order's sale: its author (hex).</summary>
    public required string Sale { get; init; }

    /// <summary>
    /// The server's clock, Unix seconds: read once the explorer's answer of step 1 came (SPEC.md
    /// §31, "The on-chain payment": a transaction listed then was broadcast by then, step 3.4).
    /// </summary>
    public required Func<long> Clock { get; init; }

    /// <summary>
    /// The explorer of the call (mempool.space, or the merchant's Bitcoin node with its fallback):
    /// the address's transactions and the chain's height are one read of it, both of one explorer.
    /// </summary>
    public required CallExplorer Explorer { get; init; }

    /// <summary>For the explorer and BlockCypher.</summary>
    public required Http Http { get; init; }

    /// <summary>The witnesses of the order (network when fewer than a majority answered); with true, read on every relay.</summary>
    public required Func<bool, Task<OrderWitnesses>> ReadWitnesses { get; init; }

    /// <summary>The witness address of a txid paying the address for the sale, or for none (null).</summary>
    public required Func<string, string?, string> WitnessAddress { get; init; }

    /// <summary>Publishes a witness on a majority of the merchant's relays, else network.</summary>
    public required Func<Witness, Task> Publish { get; init; }

    public required Warnings Warnings { get; init; }

    /// <summary>What the invoice's rest invoices paid (its rest part, checked at the same time; one that failed counts as not paid).</summary>
    public Task<long>? PaidByRests { get; init; }

    /// <summary>The order's tolerance (its lease's, basis points), read with the sale's records at the same time; 0 without.</summary>
    public Task<int>? Tolerance { get; init; }

    /// <summary>
    /// The merchant's acceptance of the invoice (<see cref="MerchantAcceptances"/>), read on every
    /// relay at the same time as the witnesses, when some transaction pays the address (network when
    /// fewer than a majority answered). None without.
    /// </summary>
    public Func<Task<MerchantAcceptance>>? ReadMerchantAcceptance { get; init; }

    public required CancellationToken CancellationToken { get; init; }
}

/// <summary>The rest part of an invoice: its rest invoices, and the Lightning part of each, in order.</summary>
/// <param name="Invoices">The rest invoices.</param>
/// <param name="Results">The Lightning part of each.</param>
/// <param name="Error">The sale's records could not be read: whether a rest invoice was paid cannot be told.</param>
/// <param name="Unrecorded">
/// The payment hashes of the rest invoices whose rest record is not known on a majority of the
/// relays: never the status's <c>bolt11</c> (a checkout link's page might never see their payment).
/// </param>
internal sealed record RestPart(
    IReadOnlyList<RestInvoice> Invoices,
    IReadOnlyList<Settled<LightningPart>> Results,
    Exception? Error = null,
    IReadOnlySet<string>? Unrecorded = null);

/// <summary>What an invoice is, for its status: its amount, its end and its address (null without one).</summary>
internal sealed record InvoiceTerms(long Sats, long ExpiresAt, string? Address);

/// <summary>What the rest invoices paid, their preimages in order, whether each is final, and the first error.</summary>
internal sealed record RestSummary(long Paid, IReadOnlyList<string> Preimages, IReadOnlySet<string> Hashes, bool Final, Exception? Error);

/// <summary>
/// The payment of an invoice (SPEC.md §31, "The on-chain payment"): its on-chain part, from the
/// explorer and the witnesses of its order, its rest part (the Lightning invoices of the rest of an
/// underpayment), and how they combine with its Lightning part.
/// </summary>
internal static class Payments
{
    private enum Kind
    {
        InTime,
        Replacement,
        Late,
        Undecided,
        Anothers,
    }

    /// <summary>
    /// The on-chain part of an invoice, checked at the time its explorer answered (<c>now</c>, read
    /// then from <see cref="OnchainCheck.Clock"/>): each transaction paying its address in
    /// time (while the lease ran, as a witness of the sale or BlockCypher's time says), a replacement
    /// of payments in time, late, or undecided; what counts of them; and, once enough counts, their
    /// confirmations on the explorer read: paid at 3. Publishes the witnesses it finds (of the sale on a majority,
    /// else network; of none for the merchant's app only, a failure logged). What is due on-chain is
    /// the least the order's tolerance accepts of the amount (the amount without tolerance), or the
    /// <c>received</c> of the merchant's acceptance a majority of the relays sent when less, less what
    /// its rest invoices paid, a sat at least (<see cref="Tolerance.DueOnchain"/>): when they pay the
    /// amount, the on-chain part is paid without transactions. Throws <see cref="NetworkException"/>
    /// when the explorer (with its fallback: the merchant's Bitcoin node with <c>"none"</c> and down
    /// included, never judged underpaid nor expired for want of an answer), BlockCypher or the relays
    /// fail.
    /// </summary>
    public static async Task<OnchainPart> CheckOnchainPartAsync(OnchainCheck check)
    {
        var address = check.Address;
        var ct = check.CancellationToken;
        var end = check.ExpiresAt + Protocol.GraceSeconds;
        // One read, of one explorer: a transaction's confirmations compare its block's height with
        // that explorer's height. Either failing, both are asked of the next explorer (SPEC.md §31).
        var (txs, height) = await check.Explorer.ReadAsync(
            (explorer, token) => Explorer.TransactionsAndHeightAsync(explorer, address, check.Http, token), ct).ConfigureAwait(false);
        // Now: after the listing, which step 3.4 judges ("listed now, it was broadcast by now").
        var now = check.Clock();
        var paying = new List<(ExplorerTx Tx, long Pays)>();
        foreach (var tx in txs)
        {
            var pays = Explorer.PaysTo(tx, address);
            if (pays > 0 && !paying.Any(other => other.Tx.Txid == tx.Txid))
            {
                paying.Add((tx, pays));
            }
        }
        // The witnesses, and the merchant's acceptance with them (on every relay), when some
        // transaction pays the address.
        var witnesses = new OrderWitnesses();
        var accepted = MerchantAcceptance.None;
        if (paying.Count > 0)
        {
            var witnessesRead = check.ReadWitnesses(false);
            var acceptedRead = check.ReadMerchantAcceptance?.Invoke() ?? Task.FromResult(MerchantAcceptance.None);
            await WhenAllAsync(witnessesRead, acceptedRead).ConfigureAwait(false);
            witnesses = await witnessesRead.ConfigureAwait(false);
            accepted = await acceptedRead.ConfigureAwait(false);
        }
        // A record of a newer format is never used nor written over, and logged (SPEC.md §12).
        var warnedOfNewer = false;
        void WarnOfNewer(OrderWitnesses read)
        {
            if (read.Newer.Count > 0 && !warnedOfNewer)
            {
                warnedOfNewer = true;
                check.Warnings.Warn("a witness of this invoice's deposit address has a newer format than this version reads; update the package");
            }
        }
        WarnOfNewer(witnesses);
        if (accepted.Newer)
        {
            check.Warnings.Warn("a merchant's acceptance of this order has a newer format than this version reads; update the package");
        }
        var listed = paying.Select(entry => entry.Tx.Txid).ToHashSet(StringComparer.Ordinal);

        // The parts: from the witnesses of this invoice and of other invoices (in the order they were
        // seen), then the transactions no such witness names (their order unknown): a replacement of
        // another invoice's payment stands for it.
        Dictionary<string, List<Part>> PartsOf(OrderWitnesses read) =>
            Replacements.PaymentParts(
                read.Sale.Concat(read.Others)
                    .Select(entry => new PaymentTx(entry.Key, entry.Value.Sats, entry.Value.Inputs, entry.Value.SeenAt, listed.Contains(entry.Key)))
                    .Concat(paying
                        .Where(entry => !read.Sale.ContainsKey(entry.Tx.Txid) && !read.Others.ContainsKey(entry.Tx.Txid))
                        .Select(entry => new PaymentTx(entry.Tx.Txid, entry.Pays, entry.Tx.Coins, null, true))));
        // Another invoice's: a witness of another invoice names it, or the root of one of its parts.
        static bool Anothers(OrderWitnesses read, Dictionary<string, List<Part>> partsRead, string txid) =>
            read.Others.ContainsKey(txid) || partsRead[txid].Any(part => read.Others.ContainsKey(part.Root));
        var parts = PartsOf(witnesses);

        bool NewerNames(string txid, string? sale) => witnesses.Newer.Contains(check.WitnessAddress(txid, sale));
        // A witness of a newer format that names no transaction listed may be of a payment replaced.
        var newerElsewhere = witnesses.Newer.Any(d => !paying.Any(entry =>
            d == check.WitnessAddress(entry.Tx.Txid, check.Sale) || d == check.WitnessAddress(entry.Tx.Txid, null)));

        var kinds = new Dictionary<string, Kind>(StringComparer.Ordinal);
        // Witnesses of the sale to publish (on a majority), and of none (logged when they fail).
        var ofSale = new List<Witness>();
        var ofNone = new List<Witness>();
        Witness WitnessOf(ExplorerTx tx, long pays, bool forSale, bool replacement) => new(
            address,
            tx.Txid,
            forSale ? check.Sale : null,
            pays,
            [.. tx.Coins.Take(Protocol.MaxWitnessInputs)],
            replacement,
            forSale ? check.ExpiresAt : null);
        foreach (var (tx, pays) in paying)
        {
            var txid = tx.Txid;
            // Another invoice's: a transaction counts for one invoice at most.
            if (Anothers(witnesses, parts, txid))
            {
                kinds[txid] = Kind.Anothers;
                continue;
            }
            witnesses.Sale.TryGetValue(txid, out var witness);
            if (witness is { Replacement: false })
            {
                kinds[txid] = Kind.InTime;
                continue;
            }
            if (NewerNames(txid, check.Sale))
            {
                kinds[txid] = Kind.Undecided;
                continue;
            }
            if (now <= end)
            {
                // Listed now, it was broadcast by now: while the lease runs.
                kinds[txid] = Kind.InTime;
                ofSale.Add(WitnessOf(tx, pays, forSale: true, replacement: false));
                continue;
            }
            if (witness is not null)
            {
                // An earlier check found it a replacement, and BlockCypher's time of it late.
                kinds[txid] = Kind.Replacement;
                continue;
            }
            // No check saw it in time.
            var own = parts[txid];
            var replaces = own.Any(part => part.Root != txid);
            if (own.Any(part => part.Root == txid))
            {
                var time = await Explorer.ReceivedAtAsync(txid, check.Http, ct).ConfigureAwait(false);
                if (time is { } received && received <= end * 1000)
                {
                    kinds[txid] = Kind.InTime;
                    ofSale.Add(WitnessOf(tx, pays, forSale: true, replacement: false));
                    continue;
                }
            }
            if (newerElsewhere)
            {
                kinds[txid] = Kind.Undecided;
            }
            else if (replaces)
            {
                kinds[txid] = Kind.Replacement;
                ofSale.Add(WitnessOf(tx, pays, forSale: true, replacement: true));
            }
            else
            {
                // Its payment reaches the merchant's wallet, but no order. Its witness none, unless one
                // names it already, or a witness of a newer format is where it would be: never
                // written over (SPEC.md §12), that one names it.
                kinds[txid] = Kind.Late;
                if (!witnesses.None.Contains(txid) && !NewerNames(txid, null))
                {
                    ofNone.Add(WitnessOf(tx, pays, forSale: false, replacement: false));
                }
            }
        }
        var publications = ofSale.Select(witness => check.Publish(witness))
            .Concat(ofNone.Select(async witness =>
            {
                try
                {
                    await check.Publish(witness).ConfigureAwait(false);
                }
                catch (NetworkException error)
                {
                    check.Warnings.Warn($"the witness of a late payment was not published: {error.Message}");
                }
            }))
            .ToList();
        await WhenAllAsync([.. publications]).ConfigureAwait(false);
        // Its witnesses on a majority, read again on every relay: of two checks that publish witnesses
        // of one transaction at once, the later reader sees both, whatever one relay hides.
        if (ofSale.Count > 0)
        {
            var again = await check.ReadWitnesses(true).ConfigureAwait(false);
            WarnOfNewer(again);
            var partsAgain = PartsOf(again);
            foreach (var (tx, _) in paying)
            {
                if (Anothers(again, partsAgain, tx.Txid))
                {
                    kinds[tx.Txid] = Kind.Anothers;
                }
            }
        }

        bool InTime(string root) =>
            listed.Contains(root)
                ? kinds.TryGetValue(root, out var kind) && kind == Kind.InTime
                : witnesses.Sale.TryGetValue(root, out var witness) && !witness.Replacement;
        var counted = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (tx, _) in paying)
        {
            var kind = kinds[tx.Txid];
            if (kind is Kind.Undecided or Kind.Anothers)
            {
                continue;
            }
            long sum = 0;
            foreach (var part in parts[tx.Txid])
            {
                if (InTime(part.Root))
                {
                    sum = Numbers.SaturatingAdd(sum, part.Sats);
                }
            }
            if (sum > 0)
            {
                counted[tx.Txid] = sum;
            }
        }
        long receivedInTime = 0;
        foreach (var (tx, _) in paying)
        {
            if (counted.TryGetValue(tx.Txid, out var sum))
            {
                receivedInTime = Numbers.SaturatingAdd(receivedInTime, sum);
            }
        }
        var paidByRests = check.PaidByRests is null ? 0 : await check.PaidByRests.ConfigureAwait(false);
        var tolerance = check.Tolerance is null ? 0 : await check.Tolerance.ConfigureAwait(false);
        // Nothing due when the rest invoices paid it all; else the least the order's tolerance
        // accepts, or what the merchant accepted when less, less what they paid, at least a sat.
        var due = Tolerance.DueOnchain(check.Sats, tolerance, paidByRests, accepted.Received);
        if (due == 0)
        {
            return new OnchainPart(OnchainKind.Paid, receivedInTime, Txids: []);
        }
        if (receivedInTime < due)
        {
            var undecided = kinds.Values.Any(kind => kind == Kind.Undecided);
            return new OnchainPart(OnchainKind.Underpaid, receivedInTime, Final: now > end && !undecided);
        }
        var order = paying.Where(entry => counted.ContainsKey(entry.Tx.Txid)).Select(entry => entry.Tx).ToList();
        var txids = order.Select(tx => tx.Txid).ToList();
        var confirmations = order.Min(tx => Explorer.ConfirmationsOf(tx, height));
        if (confirmations >= Protocol.Confirmations)
        {
            return new OnchainPart(OnchainKind.Paid, receivedInTime, Txids: txids);
        }
        return new OnchainPart(OnchainKind.Unconfirmed, receivedInTime, Confirmations: confirmations, Txids: txids);
    }

    /// <summary>
    /// What the rest invoices of <paramref name="rests"/> paid (each payment hash once; one that
    /// failed counts as not paid), their preimages in order, whether each is final, and the first error.
    /// </summary>
    public static RestSummary Summarize(RestPart? rests)
    {
        long paid = 0;
        var preimages = new List<string>();
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var final = true;
        Exception? error = null;
        if (rests?.Error is not null)
        {
            final = false;
            error = rests.Error;
        }
        // In the order of the rest invoices (by date, then payment hash), whatever the order given.
        var order = (rests?.Invoices ?? [])
            .Select((invoice, index) => (Invoice: invoice, Result: rests!.Results[index]))
            .OrderBy(entry => entry.Invoice, Comparer<RestInvoice>.Create(Rests.ByRestOrder))
            .ToList();
        foreach (var (invoice, result) in order)
        {
            if (!result.Ok)
            {
                final = false;
                error ??= result.Error;
                continue;
            }
            if (result.Value!.State == LightningState.Paid)
            {
                if (!hashes.Add(invoice.PaymentHash))
                {
                    continue;
                }
                paid = Numbers.SaturatingAdd(paid, invoice.Sats);
                preimages.Add(result.Value.Preimage!);
            }
            else if (result.Value.State == LightningState.Open)
            {
                final = false;
            }
        }
        return new RestSummary(paid, preimages, hashes, final, error);
    }

    /// <summary>
    /// The status of an invoice from its parts (SPEC.md §31, "The on-chain payment"): paid in
    /// Lightning, else paid by its rest invoices alone, else paid on-chain (mixed when rest invoices
    /// paid part of it), else unconfirmed on-chain, else an error of a part (the Lightning part's
    /// first, then the rest part's, then the on-chain part's, thrown), else expired once every part
    /// is final, else open, with its rest. A part is null when the invoice has none (no Lightning
    /// invoice, no address). <c>received</c> counts what the rest invoices paid.
    /// </summary>
    public static PaymentStatus Combine(InvoiceTerms invoice, long now, Settled<LightningPart>? lightning, Settled<OnchainPart>? onchain, RestPart? rests = null)
    {
        if (lightning is { Ok: true, Value.State: LightningState.Paid })
        {
            return PaymentStatus.Paid.InLightning(lightning.Value.Preimage!);
        }
        var summary = Summarize(rests);
        if (summary.Paid >= invoice.Sats)
        {
            // What came on-chain in time counts too (0 when the on-chain part failed).
            var onchainReceived = onchain is { Ok: true } ? onchain.Value!.Received : 0;
            return PaymentStatus.Paid.Mixed(Numbers.Decimal(Numbers.SaturatingAdd(onchainReceived, summary.Paid)), [], summary.Preimages);
        }
        if (onchain is { Ok: true, Value.Kind: OnchainKind.Paid })
        {
            var received = Numbers.Decimal(Numbers.SaturatingAdd(onchain.Value.Received, summary.Paid));
            return summary.Paid > 0
                ? PaymentStatus.Paid.Mixed(received, onchain.Value.Txids!, summary.Preimages)
                : PaymentStatus.Paid.Onchain(received, onchain.Value.Txids!);
        }
        if (onchain is { Ok: true, Value.Kind: OnchainKind.Unconfirmed })
        {
            return new PaymentStatus.Unconfirmed(
                Numbers.Decimal(Numbers.SaturatingAdd(onchain.Value.Received, summary.Paid)),
                (int)Math.Min(onchain.Value.Confirmations, int.MaxValue),
                onchain.Value.Txids!);
        }
        if (lightning is { Ok: false })
        {
            ExceptionDispatchInfo.Capture(lightning.Error!).Throw();
        }
        if (summary.Error is not null)
        {
            ExceptionDispatchInfo.Capture(summary.Error).Throw();
        }
        if (onchain is { Ok: false })
        {
            ExceptionDispatchInfo.Capture(onchain.Error!).Throw();
        }
        var underpaid = onchain is { Ok: true, Value.Kind: OnchainKind.Underpaid } ? onchain.Value : null;
        var receivedSoFar = Numbers.SaturatingAdd(underpaid?.Received ?? 0, summary.Paid);
        var lightningFinal = lightning is null || lightning.Value!.State == LightningState.Expired;
        var onchainFinal = onchain is null || underpaid?.Final == true;
        if (lightningFinal && onchainFinal && summary.Final)
        {
            return new PaymentStatus.Expired(Numbers.Decimal(receivedSoFar));
        }
        return new PaymentStatus.Open(
            invoice.ExpiresAt,
            Numbers.Decimal(receivedSoFar),
            invoice.Address is null
                ? null
                : Rests.RestOf(
                    invoice.Sats,
                    invoice.ExpiresAt,
                    invoice.Address,
                    receivedSoFar,
                    now,
                    (rests?.Invoices ?? []).Where(rest => rests?.Unrecorded?.Contains(rest.PaymentHash) != true),
                    summary.Hashes));
    }

    /// <summary>Waits for every task, then throws the first error (by order) if any: none is left unobserved.</summary>
    internal static async Task WhenAllAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The first failure, in the order given: a cancellation before another error.
            foreach (var task in tasks)
            {
                if (task.IsCanceled)
                {
                    await task.ConfigureAwait(false);
                }
            }
            foreach (var task in tasks)
            {
                if (task.IsFaulted)
                {
                    await task.ConfigureAwait(false);
                }
            }
            throw;
        }
    }
}
