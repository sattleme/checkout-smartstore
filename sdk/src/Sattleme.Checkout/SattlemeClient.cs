using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Sattleme.Checkout.Internal;
using Sattleme.Checkout.Internal.Crypto;
using Sattleme.Checkout.Internal.Lightning;
using Sattleme.Checkout.Internal.Nostr;
using Sattleme.Checkout.Internal.Onchain;

namespace Sattleme.Checkout;

/// <summary>
/// A shop's server for one sattle.me integration (SPEC.md §4): makes the invoice of each order (a
/// Lightning invoice, a deposit address of the merchant's wallet, or both), declares it in the
/// merchant's registry, checks its payment. Made for a server that may live for one call: nothing is
/// kept between calls, each call reads the merchant (a removal stops the next invoice) and closes
/// its connections; there is nothing to dispose. Thread-safe: one client may serve every request of
/// a shop. Its <see cref="ToString"/> and debugger view name the team and the relays of the key,
/// never the key.
/// </summary>
[DebuggerDisplay("{ToString(),nq}")]
public sealed class SattlemeClient
{
    private readonly IntegrationKey _access;
    private readonly IReadOnlyList<RateSource> _rateSources;
    private readonly string? _coingeckoApiKey;
    private readonly Http _http;
    private readonly IWebSocketFactory _webSockets;
    private readonly int _relayTimeoutMs;
    private readonly Warnings _warnings;
    private readonly TimeProvider _time;
    private readonly RandomSource _random;

    /// <summary>One invoice at a time per order, in this process.</summary>
    private readonly KeyedLock _orders = new();

    /// <summary>One invoice of the rest at a time per invoice (<c>ref/address</c>), in this process.</summary>
    private readonly KeyedLock _restAsks = new();

    /// <summary>
    /// A client of the integration <paramref name="options"/> names. Throws
    /// <see cref="InvalidKeyException"/> when its key is not an integration key, and
    /// <see cref="InvalidOptionException"/> naming an invalid option: before any request.
    /// </summary>
    public SattlemeClient(SattlemeClientOptions options)
    {
        if (options is null)
        {
            throw new InvalidOptionException("options", "expected new SattlemeClientOptions { Key = … }");
        }
        _access = IntegrationKey.Parse(options.Key);
        _rateSources = OrderChecks.ClientRateSources(options.RateSources);
        // Visible ASCII: its HTTP header carries it as it is.
        if (options.CoinGeckoApiKey is not null && !OrderChecks.IsVisibleAscii(options.CoinGeckoApiKey))
        {
            throw new InvalidOptionException("coingeckoApiKey", "1 or more visible ASCII characters");
        }
        _coingeckoApiKey = options.CoinGeckoApiKey;
        _relayTimeoutMs = OrderChecks.CheckMilliseconds(options.RelayTimeoutMs, Protocol.DefaultTimeoutMs, Protocol.MinTimeoutMs, Protocol.MaxTimeoutMs, "relayTimeoutMs");
        var httpTimeoutMs = OrderChecks.CheckMilliseconds(options.HttpTimeoutMs, Protocol.DefaultTimeoutMs, Protocol.MinTimeoutMs, Protocol.MaxTimeoutMs, "httpTimeoutMs");
        _http = new Http(options.HttpHandler, httpTimeoutMs);
        _webSockets = options.WebSocketFactory ?? ClientWebSocketFactory.Instance;
        _warnings = new Warnings(options.Logger ?? NullLogger.Instance);
        _time = options.TimeProvider ?? TimeProvider.System;
        _random = options.Random is null ? RandomSource.Secure : new RandomSource(options.Random);
    }

    /// <summary>The integration key's team and relays: never the key.</summary>
    public override string ToString() => $"SattlemeClient {{ team = {_access.TeamPublicKey}, relays = [{string.Join(", ", _access.Relays)}] }}";

    /// <summary>
    /// The merchant (SPEC.md §4): its business's name, the bounds of its Lightning address in sats,
    /// and how many deposit addresses the integration's lot holds. Throws
    /// <see cref="RevokedException"/> when the merchant removed the integration. Never its
    /// Lightning address.
    /// </summary>
    public Task<Merchant> GetMerchantAsync(CancellationToken cancellationToken = default) =>
        WithRelaysAsync(async pool =>
        {
            var merchant = await ChannelAsync(pool, _warnings, cancellationToken).ConfigureAwait(false);
            // Asked while the lot is read; stopped and waited for when the lot's read fails first.
            using var beside = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var payRequest = Lnurl.FetchPayRequestAsync(merchant.LightningAddress, _http, beside.Token);
            try
            {
                var lot = await LotAsync(pool, merchant, cancellationToken).ConfigureAwait(false);
                var (min, max) = Lnurl.SendableSats(await payRequest.ConfigureAwait(false));
                return new Merchant(merchant.BusinessName, Numbers.Decimal(min), Numbers.Decimal(max), lot.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The call's own cancellation, never the one its token of their own passed on.
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            finally
            {
                await StopAsync(beside, payRequest).ConfigureAwait(false);
            }
        }, cancellationToken);

    /// <summary>
    /// The invoice of an order (SPEC.md §17): a Lightning invoice asked to the merchant's Lightning
    /// address and checked, when its amount is within the address's bounds; a deposit address of the
    /// merchant's wallet leased from the integration's lot, from 1 000 sats (or both). Then the invoice
    /// is declared in the merchant's registry on a majority of its relays. Only then may it be shown:
    /// store the result with the order.
    /// <para>
    /// One order, one invoice at a time: throws <see cref="InvoicePendingException"/> (with that
    /// invoice) while an earlier invoice of the same reference may still be paid (or its on-chain
    /// payment is being confirmed), <see cref="OrderPaidException"/> when one was paid.
    /// <see cref="OnchainUnavailableException"/> when the order can only be paid on-chain and no
    /// address could be leased.
    /// </para>
    /// <para>
    /// With <c>Checkout</c>, the invoice also gets a checkout link (<see cref="Invoice.CheckoutUrl"/>):
    /// a page of the merchant's web app that shows it to the customer, checks its payment and, once
    /// it is paid, sends the customer to <c>RedirectUrl</c>. It is published on a majority of the
    /// merchant's relays before the invoice is declared. Throws <see cref="CheckoutUnavailableException"/>
    /// when the merchant's app gives no web app for it.
    /// </para>
    /// </summary>
    public async Task<Invoice> CreateInvoiceAsync(Order order, CancellationToken cancellationToken = default)
    {
        var checkedOrder = OrderChecks.Check(order, _rateSources);
        // One at a time per order in this process: two calls for one reference would both pass the
        // check of its earlier invoices.
        using (await _orders.AcquireAsync(checkedOrder.Ref, cancellationToken).ConfigureAwait(false))
        {
            return await CreateAsync(checkedOrder, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<Invoice> CreateAsync(CheckedOrder order, CancellationToken ct) =>
        WithRelaysAsync(async pool =>
        {
            var merchant = await ChannelAsync(pool, _warnings, ct).ConfigureAwait(false);
            if (order.Checkout && merchant.CheckoutOrigin is null)
            {
                throw new CheckoutUnavailableException();
            }
            var sale = Derive.SaleOf(_access.Key, merchant.Registry, order.Ref);
            // Asked while the registry is read: the Lightning address and the rate need nothing of it.
            // When the registry's check fails first, or one of them, the others are stopped and
            // waited for: nothing of them runs on once the call returned (SPEC.md §13).
            using var beside = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var payRequest = Lnurl.FetchPayRequestAsync(merchant.LightningAddress, _http, beside.Token);
            var quote = order.Fiat is not null && order.RateSources is not null
                ? Rates.FetchFirstRateAsync(order.RateSources, order.Fiat.Currency, new RateContext(_http, _coingeckoApiKey, beside.Token), Seconds, _warnings)
                : null;
            PayRequest pay;
            Rate? got;
            try
            {
                await CheckEarlierInvoicesAsync(pool, sale, merchant, order, ct).ConfigureAwait(false);
                pay = await payRequest.ConfigureAwait(false);
                got = quote is null ? null : await quote.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The call's own cancellation, never the one its token of their own passed on.
                ct.ThrowIfCancellationRequested();
                throw;
            }
            finally
            {
                await StopAsync(beside, payRequest, quote).ConfigureAwait(false);
            }

            long sats;
            RateInfo? rate = null;
            if (order.Fiat is not null)
            {
                var converted = Prices.MinorToSats(BigInteger.Parse(order.Fiat.Minor, CultureInfo.InvariantCulture), order.Fiat.Digits, got!.Price);
                rate = got.Info;
                if (converted > Protocol.MaxSats)
                {
                    throw new InvalidOrderException("fiat", $"{converted.ToString(CultureInfo.InvariantCulture)} sats is more than 21 million bitcoins");
                }
                sats = (long)converted;
            }
            else
            {
                sats = order.Sats!.Value;
            }
            var methods = PaymentMethods.Of(sats, Lnurl.SendableSats(pay), order.Fiat is not null ? "fiat" : "sats");

            InvoiceAnswer? answer = null;
            Bolt11Invoice? decoded = null;
            if (methods.Lightning)
            {
                answer = await Lnurl.RequestInvoiceAsync(pay, sats, order.DurationSeconds, order.Label, _http, ct).ConfigureAwait(false);
                decoded = Lnurl.CheckInvoice(answer, pay, sats, order.DurationSeconds, Seconds());
            }
            var leasedAt = Seconds();
            var createdAt = decoded?.Timestamp ?? leasedAt;
            var expiresAt = decoded is not null ? decoded.Timestamp + decoded.ExpirySeconds : leasedAt + order.DurationSeconds;
            var price = order.Fiat is not null
                ? new DeclaredPrice(long.Parse(order.Fiat.Minor, CultureInfo.InvariantCulture), order.Fiat.Digits, order.Fiat.Currency)
                : new DeclaredPrice(sats, 0, Protocol.SatsCurrency);

            NostrEvent? declaration = null;
            if (answer is not null && decoded is not null)
            {
                try
                {
                    declaration = Declarations.Event(sale, answer.Pr, answer.Verify, decoded.PaymentHash, price, order.Label, Seconds(), null, _random);
                }
                catch (TooLargeException error)
                {
                    // A valid invoice too long for a NIP-44 plaintext (65 535 bytes) cannot be declared.
                    throw new InvalidInvoiceException("The invoice is too large to be declared", error);
                }
            }

            // What the lease of an address holds, with on-chain.
            var terms = new LeaseTerms("", sats, answer?.Pr, answer?.Verify, createdAt, expiresAt, price, order.Label,
                // The order's for good: every check judges its payment with it.
                merchant.Tolerance, null);
            if (methods.Onchain)
            {
                // The lease holds the invoice too, sealed twice, with some 150 bytes more than the
                // declaration: a lease that does not fit a NIP-44 plaintext fails now, before anything
                // is published. The real one differs from this one by its address alone, of one length
                // for every deposit address; its random bytes are zeros, never the client's.
                try
                {
                    _ = Leases.Event(sale, terms with { Address = SizedAddress, Confirmed = false }, leasedAt, SizingRandom);
                }
                catch (ArgumentException error)
                {
                    throw new InvalidInvoiceException("The invoice is too large for the lease of an address", error);
                }
            }

            // The address before the link and the declaration: when the lease fails, nothing is
            // published.
            string? onchainAddress = null;
            if (methods.Onchain)
            {
                // Without an invoice, the lease's date is the Invoice's.
                var leased = await LeaseAddressAsync(pool, merchant, sale, terms, decoded is null ? leasedAt : null, ct).ConfigureAwait(false);
                PaymentMethods.With(methods, leased.Outcome);
                if (leased.Outcome.Status == LeaseOutcomeStatus.Leased)
                {
                    onchainAddress = leased.Address;
                }
                else if (leased.Outcome.Status == LeaseOutcomeStatus.ExplorerFailed)
                {
                    _warnings.Warn($"no deposit address leased, the explorer did not answer ({leased.Outcome.Error!.Message}): Lightning only");
                }
            }

            // On a failure below, the lease stays: it holds its address until its end (a withdrawal on
            // some relays only would leave them disagreeing), and was confirmed: §21 may hand it out.
            string? checkoutUrl = null;
            // The link before the declaration: when the link fails, nothing is declared and the order
            // gets a new invoice at once; when the declaration fails, the link's URL was never given
            // and its keys are gone, so nobody can open it.
            if (order.Checkout)
            {
                checkoutUrl = await PublishCheckoutAsync(
                    pool,
                    merchant,
                    Internal.Checkout.Value(new CheckoutValueInput(
                        merchant.BusinessName,
                        answer?.Pr,
                        answer?.Verify,
                        onchainAddress,
                        sats,
                        createdAt,
                        expiresAt,
                        order.Fiat is not null ? price : null,
                        order.Label,
                        order.RedirectUrl,
                        onchainAddress is null ? null : RestReaderOf(order.Ref, onchainAddress, expiresAt),
                        // With an address: the node the page reads, when the channel's says payers.
                        onchainAddress is null ? null : BitcoinNodes.RecordNodeFor(merchant.BitcoinNode))),
                    expiresAt,
                    ct).ConfigureAwait(false);
            }
            if (declaration is not null)
            {
                await DeclareAsync(pool, declaration, sale, merchant, ct).ConfigureAwait(false);
            }

            return new Invoice(
                order.Ref,
                answer?.Pr,
                decoded?.PaymentHash,
                answer?.Verify,
                onchainAddress,
                Address.PaymentUri(onchainAddress, sats, answer?.Pr),
                Numbers.Decimal(sats),
                order.Fiat,
                rate,
                order.Label,
                createdAt,
                expiresAt,
                checkoutUrl,
                order.Checkout ? order.RedirectUrl : null);
        }, ct);

    /// <summary>A text of a deposit address's length (<c>bc1p</c> and 58 more): a lease's size before its address.</summary>
    private static readonly string SizedAddress = "bc1p" + new string('q', 58);

    /// <summary>Zeros: the random bytes of a lease made for its size alone, never published.</summary>
    private static readonly RandomSource SizingRandom = new(new ZeroBytes());

    /// <summary>A generator of zeros (a lease's size does not depend on its random bytes).</summary>
    private sealed class ZeroBytes : RandomNumberGenerator
    {
        public override void GetBytes(byte[] data) => Array.Clear(data);
    }

    /// <summary>The rest author and rest key of the invoice with <paramref name="address"/> and <paramref name="expiresAt"/> of the order <paramref name="reference"/>.</summary>
    private RestReader RestReaderOf(string reference, string address, long expiresAt)
    {
        var keys = Derive.RestKeys(_access.Key, reference, address, expiresAt);
        return new RestReader(keys.Author, keys.Key);
    }

    /// <summary>What leasing an address gave: its outcome, and the address with its marked lease.</summary>
    private sealed record Leased(LeaseOutcome Outcome, string? Address = null, NostrEvent? Event = null);

    /// <summary>
    /// Leases a deposit address of the integration's lot for <paramref name="sale"/> (SPEC.md §31,
    /// "Leasing an address"): the lot and its leases read; of its free addresses, in a random order,
    /// at most 3 tried, one at a time: one the explorer shows paid already is skipped (its witness
    /// none published, so that the merchant's app takes it out of the lot), the explorer read with
    /// its fallback (SPEC.md §31: the merchant's Bitcoin node with <c>"none"</c> and down, no address,
    /// as when mempool.space does not answer); else the lease is
    /// published on a majority, the address's leases read again, and the lease confirmed then marked,
    /// or withdrawn when the read is not complete or another author's leases it too (the later of two
    /// servers sees both). A lease once confirmed is never withdrawn. <paramref name="leasedAt"/>: the
    /// lease's date when it is the Invoice's (an order paid on-chain only). Throws
    /// <see cref="NetworkException"/> when the relays fail.
    /// </summary>
    private async Task<Leased> LeaseAddressAsync(RelayPool pool, MerchantChannel merchant, Sale sale, LeaseTerms terms, long? leasedAt, CancellationToken ct)
    {
        var lot = await LotAsync(pool, merchant, ct).ConfigureAwait(false);
        if (lot.Count == 0)
        {
            return new Leased(LeaseOutcome.None);
        }
        var relays = merchant.Relays;
        // The leases, complete or not (the confirmation decides), and the addresses a witness names.
        var leasesTask = Leases.ReadAsync(pool, merchant.Registry.Key, lot, relays, Seconds(), _random, everyRelay: false, ct);
        var namedTask = Witnesses.ReadNamedAddressesAsync(pool, Witnesses.SeenOf(_access.Key), lot, relays, _random, ct);
        await Payments.WhenAllAsync(leasesTask, namedTask).ConfigureAwait(false);
        var leases = await leasesTask.ConfigureAwait(false);
        var named = await namedTask.ConfigureAwait(false);
        if (!leases.Answered)
        {
            throw new NetworkException("Fewer than a majority of the merchant's relays answered");
        }
        // A record of a newer format (it holds its address, never written over): logged (SPEC.md §12).
        // Its signature is not checked, as of any record that holds: only a holder of the registry
        // key seals one.
        if (leases.Records.Values.Any(records => records.Any(record => record.Outside.Kind == OutsideKind.Newer)))
        {
            _warnings.Warn("a lease of the integration's addresses has a newer format than this version reads; update the package");
        }
        var now = Seconds();
        // Not where the read shows a record of the sale (its signature checked): an earlier call of
        // the order tried it, and a new lease could be of the second of its withdrawal, and either
        // replace the other.
        var free = Shuffle(
            lot.Where(address =>
            {
                var records = leases.Records[address];
                return !named.Contains(address) && !Leases.IsLeased(records, now)
                    && !records.Any(record => record.Event.Pubkey == sale.Author && record.Signed());
            }).ToList(),
            _random);
        foreach (var address in free.Take(Protocol.MaxAddressTries))
        {
            IReadOnlyList<ExplorerTx> txs;
            try
            {
                txs = await merchant.Explorer.ReadAsync((explorer, token) => Explorer.AddressTransactionsAsync(explorer, address, _http, token), ct)
                    .ConfigureAwait(false);
            }
            catch (NetworkException error)
            {
                return new Leased(new LeaseOutcome(LeaseOutcomeStatus.ExplorerFailed, error));
            }
            var paid = txs.Where(tx => Explorer.PaysTo(tx, address) > 0).ToList();
            if (paid.Count > 0)
            {
                // Paid on-chain already: of no order, for good.
                await PublishWitnessesOfNoneAsync(pool, merchant, address, paid, ct).ConfigureAwait(false);
                continue;
            }
            // Published unconfirmed: it holds the address, never gives the invoice one.
            var lease = Leases.Event(sale, terms with { Address = address, Confirmed = false }, leasedAt ?? Seconds(), _random);
            LeaseRead again;
            try
            {
                await PublishLeaseRecordAsync(pool, relays, lease, sale, ct).ConfigureAwait(false);
                // On every relay, each until its timeout: a relay that hides a lease cannot hide it
                // while one that holds it answers in time (SPEC.md §13, §27).
                again = await Leases.ReadAsync(pool, merchant.Registry.Key, [address], relays, Seconds(), _random, everyRelay: true, ct).ConfigureAwait(false);
            }
            catch
            {
                // On some relays only (or the call cancelled): withdrawn, never the order's.
                await WithdrawAsync(pool, relays, sale, address, lease, ct).ConfigureAwait(false);
                throw;
            }
            var records = again.Records[address];
            var confirmed = again.Answered && again.Complete && Leases.IsConfirmed(records, new LeaseEvent(lease.Id, lease.Pubkey, lease.CreatedAt, lease.Sig), Seconds());
            if (!confirmed)
            {
                // Another server leased it at once (the later reader sees both), the read could not
                // be complete, or fewer than a majority answered: withdrawn (safe on some relays
                // only: the address was never the order's), unless the read shows another record of
                // the sale there whose signature checks out, even from fewer than a majority: another
                // call of the order made it at once (its lock failed, SPEC.md §17), and it is not this
                // call's to end.
                var another = records.Any(record => record.Event.Pubkey == sale.Author && record.Event.Id != lease.Id && record.Signed());
                if (!another)
                {
                    await WithdrawAsync(pool, relays, sale, address, lease, ct).ConfigureAwait(false);
                }
                if (!again.Answered)
                {
                    throw new NetworkException("Fewer than a majority of the merchant's relays answered");
                }
                // The next one.
                continue;
            }
            // Confirmed: never withdrawn from now on, whatever fails (a withdrawal on some relays
            // only would leave them disagreeing on an address that may be the order's). Marked
            // confirmed: from now on the address is the order's, its invoice's in §21 too.
            var marked = Leases.Event(sale, terms with { Address = address, Confirmed = true }, Math.Max(Seconds(), lease.CreatedAt + 1), _random);
            await PublishLeaseRecordAsync(pool, relays, marked, sale, ct).ConfigureAwait(false);
            return new Leased(LeaseOutcome.Leased, address, marked);
        }
        return new Leased(LeaseOutcome.None);
    }

    /// <summary>
    /// Withdraws <paramref name="lease"/>, the lease of <paramref name="address"/> by the sale that
    /// was not confirmed: its withdrawal, a second after it (so that it replaces the lease, and no
    /// later record of the sale), published on the merchant's <paramref name="relays"/>; a failure
    /// only logged (the lease then holds its address until its end, and never gives its invoice one).
    /// </summary>
    private async Task WithdrawAsync(RelayPool pool, IReadOnlyList<string> relays, Sale sale, string address, NostrEvent lease, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }
        var withdrawal = Leases.WithdrawalEvent(sale, address, lease.CreatedAt + 1, _random);
        try
        {
            await pool.PublishToQuorumAsync(relays, withdrawal, sale.SecretKey, RelayPool.Majority(relays.Count)).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A withdrawal that fails is only logged: the lease then holds its address until its end.
        catch (Exception)
#pragma warning restore CA1031
        {
            if (!ct.IsCancellationRequested)
            {
                _warnings.Warn("a lease that was not confirmed could not be withdrawn; it holds its deposit address until its end");
            }
        }
    }

    /// <summary>Publishes a lease record on a majority of the merchant's <paramref name="relays"/>, else <see cref="NetworkException"/>.</summary>
    private static async Task PublishLeaseRecordAsync(RelayPool pool, IReadOnlyList<string> relays, NostrEvent lease, Sale sale, CancellationToken ct)
    {
        try
        {
            await pool.PublishToQuorumAsync(relays, lease, sale.SecretKey, RelayPool.Majority(relays.Count)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            throw new NetworkException("Fewer than a majority of the merchant's relays took the lease of a deposit address: try again", error);
        }
    }

    /// <summary>Publishes a witness none of each transaction of <paramref name="txs"/> paying <paramref name="address"/>, failures logged.</summary>
    private async Task PublishWitnessesOfNoneAsync(RelayPool pool, MerchantChannel merchant, string address, IReadOnlyList<ExplorerTx> txs, CancellationToken ct)
    {
        var seen = Witnesses.SeenOf(_access.Key);
        await Payments.WhenAllAsync([.. txs.Select(async tx =>
        {
            try
            {
                var witness = new Witness(address, tx.Txid, null, Explorer.PaysTo(tx, address), [.. tx.Coins.Take(Protocol.MaxWitnessInputs)], false, null);
                await Witnesses.PublishAsync(pool, seen, witness, merchant.Relays, Seconds(), _random, ct).ConfigureAwait(false);
            }
            catch (NetworkException error)
            {
                _warnings.Warn($"the witness of a paid address was not published: {error.Message}");
            }
        })]).ConfigureAwait(false);
    }

    /// <summary>The integration's lot of deposit addresses, read on the merchant's relays.</summary>
    private Task<IReadOnlyList<string>> LotAsync(RelayPool pool, MerchantChannel merchant, CancellationToken ct) =>
        Lot.ReadAsync(pool, _access, merchant.Relays, _random, _warnings, ct);

    /// <summary>
    /// Publishes a new checkout link holding <paramref name="value"/> on a majority of the merchant's
    /// relays, and gives its URL. The author's secret answers the relays' authentication, then is
    /// dropped with the link: nobody can change it afterwards. <see cref="NetworkException"/> when
    /// fewer than a majority took it.
    /// </summary>
    private async Task<string> PublishCheckoutAsync(RelayPool pool, MerchantChannel merchant, JsonObject value, long expiresAt, CancellationToken ct)
    {
        NewCheckoutLink link;
        try
        {
            link = Internal.Checkout.NewLink(merchant.CheckoutOrigin!, merchant.Relays, value, expiresAt, Seconds(), _random);
        }
        catch (ArgumentException error)
        {
            throw new InvalidInvoiceException("The invoice is too large for a checkout link", error);
        }
        try
        {
            await pool.PublishToQuorumAsync(merchant.Relays, link.Event, link.SecretKey, RelayPool.Majority(merchant.Relays.Count)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            throw new NetworkException("Fewer than a majority of the merchant's relays took the checkout link: try again", error);
        }
        return link.Url;
    }

    /// <summary>
    /// Refuses a new invoice for the sale while an earlier one may still be paid (until its end plus
    /// 120 s, or while its on-chain payment is being confirmed or undecided), or when one was paid
    /// (SPEC.md §21, "The check"): read in the registry, so that it holds even when the shop lost
    /// what it stored.
    /// </summary>
    private async Task CheckEarlierInvoicesAsync(RelayPool pool, Sale sale, MerchantChannel merchant, CheckedOrder order, CancellationToken ct)
    {
        var records = await DeclaredAsync(pool, sale, merchant, _warnings, everyRelay: false, ct).ConfigureAwait(false);
        var earlier = records.Invoices;
        var scope = new RelayScope(pool, merchant, records);
        // A few at a time; a paid invoice wins over a service that did not answer about another one.
        Task<List<Settled<PaymentStatus>>> Check(IReadOnlyList<SaleDeclaration> invoices) =>
            SettleEachAsync(invoices, Protocol.MaxParallelPaymentChecks, async invoice =>
            {
                var stored = OrderChecks.StoredOf(invoice.Invoice);
                return StatusOf(stored, await CheckPartsAsync(stored, _warnings, scope, ct).ConfigureAwait(false), Seconds());
            }, ct);
        var mayBePaid = Sales.MayStillBePaid([.. earlier.Select(declared => declared.Invoice)], Seconds()).Select(index => earlier[index]).ToList();
        if (mayBePaid.Count > 0)
        {
            // Their payments checked, even of one alone: an invoice already paid is never handed
            // out again. Several: an earlier createInvoice failed once a minority of the relays took
            // its last record, and a later one made another invoice and showed it; the sale's records
            // are read on every relay at the same time, and the one shown is the one a majority holds.
            var statusesTask = Check(mayBePaid);
            var everywhereTask = mayBePaid.Count > 1
                ? Settled<SaleRecords>.OfAsync(DeclaredAsync(pool, sale, merchant, _warnings, everyRelay: true, ct), ct)
                : Task.FromResult<Settled<SaleRecords>>(null!);
            await Payments.WhenAllAsync(statusesTask, everywhereTask).ConfigureAwait(false);
            var statuses = await statusesTask.ConfigureAwait(false);
            var everywhere = mayBePaid.Count > 1 ? await everywhereTask.ConfigureAwait(false) : null;
            ct.ThrowIfCancellationRequested();
            var needed = RelayPool.Majority(merchant.Relays.Count);
            IReadOnlyList<bool>? held = everywhere is { Ok: true }
                ? [.. mayBePaid.Select(invoice => everywhere.Value!.SentBy.GetValueOrDefault(invoice.LastRecord.Id) >= needed)]
                : null;
            var outcome = Sales.PendingOutcomeOf(statuses, held);
            switch (outcome.Kind)
            {
                case PendingKind.Paid:
                    throw new OrderPaidException(mayBePaid[outcome.Index].Invoice, (PaymentStatus.Paid)statuses[outcome.Index].Value!);
                case PendingKind.Failed:
                    ExceptionDispatchInfo.Capture(statuses[outcome.Index].Error!).Throw();
                    break;
                case PendingKind.Unread:
                    // Fewer than a majority answered the read again: after a payment and an error.
                    if (everywhere is { Ok: false })
                    {
                        ExceptionDispatchInfo.Capture(everywhere.Error!).Throw();
                    }
                    throw new NetworkException("Fewer than a majority of the merchant's relays answered");
            }
            var open = mayBePaid[outcome.Index];
            // Its createInvoice may have failed with fewer than a majority of the relays holding its
            // declaration or its lease ("do not show this invoice"): published again before it is
            // handed out. Its lease, with an address, was confirmed (SPEC.md §31): the address is
            // the order's.
            if (open.Event is not null)
            {
                await DeclareAsync(pool, open.Event, sale, merchant, ct).ConfigureAwait(false);
            }
            if (open.Lease is not null)
            {
                await PublishLeaseAsync(pool, open.Lease, sale, merchant, ct).ConfigureAwait(false);
            }
            var invoice = open.Invoice;
            // A new link to it, for a shop that lost the one it stored (or never got it): links only
            // show the invoice, several of them change nothing.
            var checkoutUrl = order.Checkout
                ? await PublishCheckoutAsync(
                    pool,
                    merchant,
                    Internal.Checkout.Value(new CheckoutValueInput(
                        merchant.BusinessName,
                        invoice.Bolt11,
                        invoice.VerifyUrl,
                        invoice.OnchainAddress,
                        long.Parse(invoice.Sats, CultureInfo.InvariantCulture),
                        invoice.CreatedAt,
                        invoice.ExpiresAt,
                        invoice.Fiat is null
                            ? null
                            : new DeclaredPrice(long.Parse(invoice.Fiat.Minor, CultureInfo.InvariantCulture), invoice.Fiat.Digits, invoice.Fiat.Currency),
                        invoice.Note,
                        order.RedirectUrl,
                        invoice.OnchainAddress is null ? null : RestReaderOf(order.Ref, invoice.OnchainAddress, invoice.ExpiresAt),
                        invoice.OnchainAddress is null ? null : BitcoinNodes.RecordNodeFor(merchant.BitcoinNode))),
                    invoice.ExpiresAt,
                    ct).ConfigureAwait(false)
                : null;
            throw new InvoicePendingException(invoice, checkoutUrl);
        }
        var all = await Check(earlier).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        for (var index = 0; index < all.Count; index++)
        {
            if (all[index] is { Ok: true, Value: PaymentStatus.Paid paid })
            {
                throw new OrderPaidException(earlier[index].Invoice, paid);
            }
        }
        // An on-chain payment being confirmed, or a transaction undecided: its time to be paid is
        // over, nothing is published again, and no new link (the shop keeps the one it got, whose
        // page goes on following the payment).
        for (var index = 0; index < all.Count; index++)
        {
            if (all[index] is { Ok: true, Value: PaymentStatus.Unconfirmed or PaymentStatus.Open })
            {
                throw new InvoicePendingException(earlier[index].Invoice, null);
            }
        }
        foreach (var status in all)
        {
            if (!status.Ok)
            {
                ExceptionDispatchInfo.Capture(status.Error!).Throw();
            }
        }
    }

    /// <summary>Publishes a declaration on a majority of the merchant's relays, else <see cref="NetworkException"/>.</summary>
    private static async Task DeclareAsync(RelayPool pool, NostrEvent declaration, Sale sale, MerchantChannel merchant, CancellationToken ct)
    {
        try
        {
            await pool.PublishToQuorumAsync(merchant.Relays, declaration, sale.SecretKey, RelayPool.Majority(merchant.Relays.Count)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            throw new NetworkException("Fewer than a majority of the merchant's relays took the invoice's declaration: do not show this invoice, try again", error);
        }
    }

    /// <summary>Publishes a lease again on a majority of the merchant's relays, else <see cref="NetworkException"/>.</summary>
    private static async Task PublishLeaseAsync(RelayPool pool, NostrEvent lease, Sale sale, MerchantChannel merchant, CancellationToken ct)
    {
        try
        {
            await pool.PublishToQuorumAsync(merchant.Relays, lease, sale.SecretKey, RelayPool.Majority(merchant.Relays.Count)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            throw new NetworkException("Fewer than a majority of the merchant's relays took the invoice's lease: do not show this invoice, try again", error);
        }
    }

    /// <summary>
    /// Where the payment of an invoice is (SPEC.md §22), checked: its Lightning part from the
    /// merchant's Lightning address (LUD-21), its on-chain part from the explorer (mempool.space, or
    /// the merchant's Bitcoin node with its fallback, SPEC.md §31) and the witnesses of its order,
    /// and the Lightning invoices of the rest of an on-chain underpayment, all at once.
    /// <see cref="PaymentStatus.Open"/> (not paid yet, 120 s after its end included; <c>Received</c>
    /// above 0 for an on-chain underpayment, whose <c>Rest</c> is still payable),
    /// <see cref="PaymentStatus.Unconfirmed"/> (enough received in time, its on-chain part not 3
    /// confirmations deep yet), <see cref="PaymentStatus.Paid"/> (in Lightning, with the preimage,
    /// the proof of payment; on-chain; or mixed), or <see cref="PaymentStatus.Expired"/>, for good.
    /// Underpaid on-chain, it asks the merchant's Lightning address for an invoice of the rest,
    /// declares it and publishes it for the order's checkout links (<c>Rest.Bolt11</c>; a failure is
    /// logged, the next check asks again). It never opens the explorer's WebSocket. Throws
    /// <see cref="InvalidOrderException"/> (<c>invoice</c>) for an invoice that is not one,
    /// <see cref="NetworkException"/> (retry) when a service does not answer (the merchant's Bitcoin
    /// node with <c>fallback</c> <c>"none"</c> included: never <see cref="PaymentStatus.Expired"/>
    /// for want of an answer),
    /// <see cref="InvalidInvoiceException"/> when an answer is not about this invoice or its
    /// preimage is wrong.
    /// </summary>
    public async Task<PaymentStatus> GetStatusAsync(IInvoiceToCheck invoice, CancellationToken cancellationToken = default)
    {
        var stored = OrderChecks.CheckStoredInvoice(invoice);
        return StatusOf(stored, await CheckPartsAsync(stored, _warnings, null, cancellationToken).ConfigureAwait(false), Seconds());
    }

    /// <summary>The relays of a call, its merchant, and the sale's records when already read.</summary>
    private sealed record RelayScope(RelayPool Pool, MerchantChannel Merchant, SaleRecords? Records);

    /// <summary>
    /// What the reads of the explorer of one call share (SPEC.md §31, "in the same call"): when the
    /// merchant's Bitcoin node failed, and, for WaitForPaymentAsync, who hears the explorer each
    /// channel read names (its WebSocket follows it).
    /// </summary>
    private sealed record CallScope(NodeFailures Failures, Action<CallExplorer>? OnExplorer = null);

    /// <summary>The last result of each part of an invoice (null: it has none).</summary>
    private sealed record Parts(Settled<LightningPart>? Lightning, Settled<OnchainPart>? Onchain, RestPart? Rests);

    /// <summary>
    /// Every part of <paramref name="stored"/> checked at once (SPEC.md §31, "The on-chain
    /// payment"): its Lightning part, and with an address its on-chain part and its rest part, read
    /// on the merchant's relays (over <paramref name="scope"/> when given, with the sale's records
    /// when already read; else over the connections of a call of its own); an invoice of the rest
    /// asked when the status calls for one. A part that fails is its error, never thrown; a
    /// cancellation is.
    /// </summary>
    private async Task<Parts> CheckPartsAsync(StoredInvoice stored, Warnings warnings, RelayScope? scope, CancellationToken ct, CallScope? call = null)
    {
        var lightning = stored.Lightning is not null
            ? Settled<LightningPart>.OfAsync(Lnurl.VerifyPaymentAsync(stored.Lightning, _http, Seconds(), ct), ct)
            : Task.FromResult<Settled<LightningPart>>(null!);
        if (stored.OnchainAddress is null)
        {
            var parts = new Parts(stored.Lightning is null ? null : await lightning.ConfigureAwait(false), null, null);
            ct.ThrowIfCancellationRequested();
            return parts;
        }
        try
        {
            return scope is not null
                ? await AddressPartsAsync(stored, scope, lightning, warnings, ct).ConfigureAwait(false)
                : await WithRelaysAsync(async pool =>
                {
                    var merchant = await ChannelAsync(pool, warnings, ct, call?.Failures).ConfigureAwait(false);
                    call?.OnExplorer?.Invoke(merchant.Explorer);
                    return await AddressPartsAsync(stored, new RelayScope(pool, merchant, null), lightning, warnings, ct).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            // The relays or the channel failed: the address's parts cannot be told.
            return new Parts(stored.Lightning is null ? null : await lightning.ConfigureAwait(false), Settled<OnchainPart>.Failed(error), null);
        }
    }

    /// <summary>
    /// The on-chain part and the rest part of <paramref name="stored"/> (it has an address and a
    /// reference), at once: the sale's records (its rest invoices, its lease) read while the explorer
    /// is asked; then, when the status is open with a rest and no invoice of it, one asked; and the
    /// records of its rest invoices published again where no majority holds them.
    /// </summary>
    private async Task<Parts> AddressPartsAsync(StoredInvoice stored, RelayScope scope, Task<Settled<LightningPart>> lightningCheck, Warnings warnings, CancellationToken ct)
    {
        var (pool, merchant, records) = scope;
        var address = stored.OnchainAddress!;
        var sale = Derive.SaleOf(_access.Key, merchant.Registry, stored.Ref!);
        var read = records is not null ? Task.FromResult(records) : DeclaredAsync(pool, sale, merchant, warnings, everyRelay: false, ct);
        var restCheck = Settled<RestPart>.OfAsync(RestPartAsync(), ct);
        async Task<RestPart> RestPartAsync()
        {
            var found = await read.ConfigureAwait(false);
            // This invoice's own: another invoice of the order may have leased the address before.
            var invoices = found.Rests.TryGetValue(Sales.RestKeyOf(address, stored.ExpiresAt), out var list) ? list : [];
            // One whose declaration may be on a minority of the relays only (its publication failed):
            // on a majority before it counts, is shown or recorded, else the rest part fails (network).
            foreach (var invoice in invoices)
            {
                if (found.UnsureRests.TryGetValue(invoice.PaymentHash, out var unsure))
                {
                    await DeclareAsync(pool, unsure, sale, merchant, ct).ConfigureAwait(false);
                }
            }
            var results = await Task.WhenAll(invoices.Select(invoice =>
                Settled<LightningPart>.OfAsync(Lnurl.VerifyPaymentAsync(invoice.ToLightning(), _http, Seconds(), ct), ct))).ConfigureAwait(false);
            return new RestPart(invoices, results);
        }
        // The order's tolerance, its lease's (the invoice's own terms): without the records, or another
        // lease of the address, none.
        var tolerance = ToleranceAsync();
        async Task<int> ToleranceAsync()
        {
            try
            {
                var found = await read.ConfigureAwait(false);
                return found.Leases.TryGetValue(address, out var lease) && lease.Terms.Sats == stored.Sats && lease.Terms.ExpiresAt == stored.ExpiresAt
                    ? lease.Terms.Tolerance
                    : 0;
            }
#pragma warning disable CA1031 // Without the sale's records, no tolerance: the payment is judged without.
            catch (Exception)
#pragma warning restore CA1031
            {
                return 0;
            }
        }
        var seen = Witnesses.SeenOf(_access.Key);
        // The merchant's acceptance of this invoice: the team's record, read on every relay.
        var accepted = MerchantAcceptances.KeysOf(_access.Key, _access.TeamPublicKey, sale.Author, address, stored.ExpiresAt);
        Task<OnchainPart> CheckOnchain(Task<long> paidByRests) => Payments.CheckOnchainPartAsync(new OnchainCheck
        {
            Address = address,
            Sats = stored.Sats,
            ExpiresAt = stored.ExpiresAt,
            Sale = sale.Author,
            Clock = Seconds,
            Explorer = merchant.Explorer,
            Http = _http,
            ReadWitnesses = everyRelay => Witnesses.ReadOrderWitnessesAsync(pool, seen, address, sale.Author, stored.ExpiresAt, merchant.Relays, _random, everyRelay, ct),
            WitnessAddress = (txid, of) => Witnesses.WitnessAddress(seen.AddressKey, address, txid, of),
            Publish = witness => Witnesses.PublishAsync(pool, seen, witness, merchant.Relays, Seconds(), _random, ct),
            Warnings = warnings,
            PaidByRests = paidByRests,
            Tolerance = tolerance,
            ReadMerchantAcceptance = () =>
                MerchantAcceptances.ReadAsync(pool, accepted, new AcceptedInvoice(stored.Sats, stored.ExpiresAt), merchant.Relays, _random, ct),
            CancellationToken = ct,
        });
        // A rest invoice that failed counts as not paid: the on-chain part then needs more.
        async Task<long> PaidByRestsAsync()
        {
            var rest = await restCheck.ConfigureAwait(false);
            return rest.Ok ? Payments.Summarize(rest.Value).Paid : 0;
        }
        var onchainCheck = Settled<OnchainPart>.OfAsync(CheckOnchain(PaidByRestsAsync()), ct);
        await Payments.WhenAllAsync(lightningCheck, onchainCheck, restCheck).ConfigureAwait(false);
        var lightning = stored.Lightning is null ? null : await lightningCheck.ConfigureAwait(false);
        var onchain = await onchainCheck.ConfigureAwait(false);
        var restResult = await restCheck.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        // The sale's records unread: whether a rest invoice was paid cannot be told.
        var rests = restResult.Ok ? restResult.Value! : new RestPart([], [], restResult.Error);
        if (!restResult.Ok)
        {
            return new Parts(lightning, onchain, rests);
        }
        var foundRecords = await read.ConfigureAwait(false);
        var now = Seconds();
        PaymentStatus? status;
        try
        {
            status = StatusOf(stored, new Parts(lightning, onchain, rests), now);
        }
#pragma warning disable CA1031 // A part failed: the status is its error, nothing is asked.
        catch (Exception)
#pragma warning restore CA1031
        {
            status = null;
        }
        var leaseTerms = foundRecords.Leases.TryGetValue(address, out var saleLease) ? saleLease.Terms : null;
        // The rest invoices whose rest record a majority took in this check.
        var recorded = new HashSet<string>(StringComparer.Ordinal);
        // Open with a rest and no invoice of it: whether one is asked is decided in the order's
        // critical section, at the time read there.
        if (status is PaymentStatus.Open { Rest: { Bolt11: null } rest } && leaseTerms is not null)
        {
            var asked = await AskRestAsync(stored, rest, leaseTerms, rests.Invoices, pool, merchant, sale, warnings, ct).ConfigureAwait(false);
            if (asked is not null)
            {
                if (asked.Recorded)
                {
                    recorded.Add(asked.Invoice.PaymentHash);
                }
                rests = new RestPart([.. rests.Invoices, asked.Invoice], [.. rests.Results, asked.Result]);
                // Asked meanwhile by another check and paid already: less is due on-chain.
                if (IsPaid(asked.Result))
                {
                    onchain = await Settled<OnchainPart>.OfAsync(CheckOnchain(Task.FromResult(Payments.Summarize(rests).Paid)), ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                }
            }
        }
        if (rests.Invoices.Count > 0 && now < stored.ExpiresAt + Protocol.GraceSeconds)
        {
            var unrecorded = await PublishMissingRestsAsync(stored, rests.Invoices, recorded, pool, merchant, warnings, ct).ConfigureAwait(false);
            rests = rests with { Unrecorded = unrecorded };
        }
        var parts = new Parts(lightning, onchain, rests);
        await PublishAcceptanceAsync(stored, parts, pool, merchant, warnings, ct).ConfigureAwait(false);
        return parts;
    }

    /// <summary>
    /// When the order's tolerance or the merchant's acceptance accepted less than its amount (the
    /// status the parts give is being confirmed, or paid on-chain), has the pages of its checkout
    /// links know it (SPEC.md §31, "Acceptance records"): unless a majority of the merchant's relays
    /// hold what it received, or less, publishes an acceptance record of it. A failure is logged,
    /// never the check's: the next check publishes it again. Never the tolerance itself.
    /// </summary>
    private async Task PublishAcceptanceAsync(StoredInvoice stored, Parts parts, RelayPool pool, MerchantChannel merchant, Warnings warnings, CancellationToken ct)
    {
        PaymentStatus status;
        try
        {
            status = StatusOf(stored, parts, Seconds());
        }
#pragma warning disable CA1031 // A part failed: nothing is accepted.
        catch (Exception)
#pragma warning restore CA1031
        {
            return;
        }
        if (Acceptances.ToPublish(status, stored.Sats, stored.ExpiresAt, Seconds()) is not { } received)
        {
            return;
        }
        var keys = Derive.RestKeys(_access.Key, stored.Ref!, stored.OnchainAddress!, stored.ExpiresAt);
        try
        {
            if (await Acceptances.KnownAsync(pool, keys, stored.Sats, received, merchant.Relays, _random, warnings, ct).ConfigureAwait(false))
            {
                return;
            }
            await Acceptances.PublishRecordAsync(pool, Acceptances.RecordEvent(keys, received, stored.ExpiresAt, Seconds(), _random), keys, merchant.Relays, ct)
                .ConfigureAwait(false);
        }
        catch (NetworkException error)
        {
            warnings.Warn($"the acceptance record of a payment was not published ({error.Message}); published again at the next check");
        }
    }

    /// <summary>A rest invoice asked (or found, asked meanwhile by another check): its Lightning part, and whether a majority took its rest record in this call.</summary>
    private sealed record AskedRest(RestInvoice Invoice, Settled<LightningPart> Result, bool Recorded);

    /// <summary>
    /// Whether an invoice of the <paramref name="rest"/> a check's status gave is asked (SPEC.md §31,
    /// "Asking for a rest invoice"), decided in the order's critical section (one at a time per
    /// invoice in this process) once the rest invoices were read again, at the time read then: the
    /// decision, the invoice's expiry, its checks and its records all use that time, never one read
    /// before the explorer, the witnesses or BlockCypher answered. Another check's invoice of the
    /// same rest, declared meanwhile, is taken instead; else the merchant's Lightning address is
    /// asked for one, which is declared with <c>rest</c> (the price and note of the sale's
    /// <paramref name="lease"/> of the address) on a majority of the merchant's relays, then its rest
    /// record published. Null when none is asked or could be made: less than a minute left, a lease of
    /// other terms, outside the address's sendable sats, or a failure, logged as a warning (the next
    /// check asks again).
    /// </summary>
    private async Task<AskedRest?> AskRestAsync(StoredInvoice stored, Rest rest, LeaseTerms lease, IReadOnlyList<RestInvoice> known, RelayPool pool,
        MerchantChannel merchant, Sale sale, Warnings warnings, CancellationToken ct)
    {
        var address = stored.OnchainAddress!;
        using (await _restAsks.AcquireAsync($"{sale.Ref}/{address}", ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Another check may have asked for it meanwhile.
                var again = await DeclaredAsync(pool, sale, merchant, warnings, everyRelay: false, ct).ConfigureAwait(false);
                // The time of the decision: read once, here.
                var now = Seconds();
                if (Rests.ToAsk(rest, stored.Sats, stored.ExpiresAt, now, (lease.Sats, lease.ExpiresAt)) is not { } ask)
                {
                    return null;
                }
                var before = known.Select(invoice => invoice.PaymentHash).ToHashSet(StringComparer.Ordinal);
                var meanwhile = again.Rests.TryGetValue(Sales.RestKeyOf(address, stored.ExpiresAt), out var found)
                    ? found.FirstOrDefault(invoice => !before.Contains(invoice.PaymentHash) && invoice.Sats == ask.Sats && now < invoice.ExpiresAt)
                    : null;
                if (meanwhile is not null)
                {
                    // On a majority, as any rest invoice found (a failure is logged below).
                    if (again.UnsureRests.TryGetValue(meanwhile.PaymentHash, out var unsure))
                    {
                        await DeclareAsync(pool, unsure, sale, merchant, ct).ConfigureAwait(false);
                    }
                    // Checked as any rest invoice: it may have been paid already.
                    var result = await Settled<LightningPart>.OfAsync(Lnurl.VerifyPaymentAsync(meanwhile.ToLightning(), _http, now, ct), ct).ConfigureAwait(false);
                    return new AskedRest(meanwhile, result, false);
                }
                var pay = await Lnurl.FetchPayRequestAsync(merchant.LightningAddress, _http, ct).ConfigureAwait(false);
                if (!Rests.Sendable(ask.Sats, Lnurl.SendableSats(pay)))
                {
                    return null;
                }
                // Until the invoice's end, from the time of the decision (60 s at least).
                var expirySeconds = ask.ExpirySeconds;
                var answer = await Lnurl.RequestInvoiceAsync(pay, ask.Sats, expirySeconds, lease.Note, _http, ct).ConfigureAwait(false);
                var decoded = Lnurl.CheckInvoice(answer, pay, ask.Sats, expirySeconds, now);
                NostrEvent declaration;
                try
                {
                    declaration = Declarations.Event(sale, answer.Pr, answer.Verify, decoded.PaymentHash, lease.Price, lease.Note, now,
                        new RestOf(address, stored.ExpiresAt), _random);
                }
                catch (TooLargeException error)
                {
                    throw new InvalidInvoiceException("The invoice is too large to be declared", error);
                }
                await DeclareAsync(pool, declaration, sale, merchant, ct).ConfigureAwait(false);
                var restInvoice = new RestInvoice(answer.Pr, decoded.PaymentHash, answer.Verify, ask.Sats, decoded.Timestamp, decoded.Timestamp + decoded.ExpirySeconds);
                // Declared: it may be shown. Its record, for the checkout links' pages.
                var recorded = await PublishRestAsync(stored, restInvoice, pool, merchant, warnings, now, ct).ConfigureAwait(false);
                return new AskedRest(restInvoice, Settled<LightningPart>.Of(LightningPart.Open), recorded);
            }
            catch (SattlemeException error)
            {
                ct.ThrowIfCancellationRequested();
                warnings.Warn($"no Lightning invoice of the rest of an underpayment ({error.Message}); asked again at the next check");
                return null;
            }
        }
    }

    /// <summary>Publishes the rest record of <paramref name="invoice"/>, dated <paramref name="now"/>, on a majority; a failure only logged.</summary>
    private async Task<bool> PublishRestAsync(StoredInvoice stored, RestInvoice invoice, RelayPool pool, MerchantChannel merchant, Warnings warnings, long now,
        CancellationToken ct)
    {
        var keys = Derive.RestKeys(_access.Key, stored.Ref!, stored.OnchainAddress!, stored.ExpiresAt);
        try
        {
            var record = Rests.RecordEvent(keys, invoice.Bolt11, invoice.VerifyUrl, invoice.PaymentHash, stored.ExpiresAt, now, _random);
            await Rests.PublishRecordAsync(pool, record, keys, merchant.Relays, ct).ConfigureAwait(false);
            return true;
        }
        catch (NetworkException error)
        {
            warnings.Warn($"the record of an invoice of the rest was not published ({error.Message}); published again at the next check");
            return false;
        }
    }

    /// <summary>
    /// Publishes again the rest records of <paramref name="invoices"/> that fewer than a majority of
    /// the relays sent (their first publication may have failed), so that the checkout links' pages
    /// see each of them; never over a record of a newer format at its rest record address, that a
    /// relay sent (a newer version recorded it: it counts as known). Gives the payment hashes of
    /// those still not known on a majority: neither sent by a majority nor taken by one in this check
    /// (<paramref name="recorded"/>, or published again here), nor held by a record of a newer format;
    /// when fewer than a majority answered, all of them but <paramref name="recorded"/>. Those are
    /// never the status's <c>bolt11</c>: the next check tries again.
    /// </summary>
    private async Task<HashSet<string>> PublishMissingRestsAsync(StoredInvoice stored, IReadOnlyList<RestInvoice> invoices, HashSet<string> recorded,
        RelayPool pool, MerchantChannel merchant, Warnings warnings, CancellationToken ct)
    {
        var keys = Derive.RestKeys(_access.Key, stored.Ref!, stored.OnchainAddress!, stored.ExpiresAt);
        var read = await Rests.ReadRecordsAsync(pool, keys, new RestLinkTerms(stored.Sats, -Protocol.MaxSafeInteger, stored.ExpiresAt), merchant.Relays, _random, ct)
            .ConfigureAwait(false);
        var unknown = invoices.Where(invoice => !recorded.Contains(invoice.PaymentHash)).ToList();
        // Not read: whether the page can see the others is not known.
        if (read is null)
        {
            return unknown.Select(invoice => invoice.PaymentHash).ToHashSet(StringComparer.Ordinal);
        }
        if (read.Newer.Count > 0)
        {
            warnings.Warn("a record of an invoice of the rest of an underpayment has a newer format than this version reads; update the package");
        }
        var unrecorded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var invoice in unknown)
        {
            if (read.Held.Contains(invoice.PaymentHash))
            {
                continue;
            }
            // A record of a newer format at its address: a newer version recorded it. Never written
            // over (SPEC.md §12), and it counts as known.
            if (read.Newer.Contains(Derive.RestRecordAddress(keys.Key, invoice.PaymentHash)))
            {
                continue;
            }
            if (!await PublishRestAsync(stored, invoice, pool, merchant, warnings, Seconds(), ct).ConfigureAwait(false))
            {
                unrecorded.Add(invoice.PaymentHash);
            }
        }
        return unrecorded;
    }

    /// <summary>
    /// Checks the payment until it is paid or expired (SPEC.md §23): its Lightning part, and the
    /// invoices of the rest of an on-chain underpayment, every <c>IntervalMs</c>; its on-chain part
    /// (with an address) each time the explorer's WebSocket says something changed for the address,
    /// every 60 s at least, and at once when an invoice of the rest is paid. Network errors are
    /// retried (one that each check meets again logged once, and again only after it stopped),
    /// <c>OnStatus</c> hears each status. Fails with the cancellation when it is cancelled, and with
    /// any error that is not a network one. An on-chain payment takes about 30 minutes to be paid once
    /// unconfirmed: a server that cannot wait that long checks again later with
    /// <see cref="GetStatusAsync"/>. Gives a <see cref="PaymentStatus.Paid"/> or a
    /// <see cref="PaymentStatus.Expired"/>.
    /// </summary>
    public async Task<PaymentStatus> WaitForPaymentAsync(IInvoiceToCheck invoice, WaitOptions? options = null, CancellationToken cancellationToken = default)
    {
        var interval = OrderChecks.CheckMilliseconds(options?.IntervalMs, Protocol.DefaultIntervalMs, Protocol.MinIntervalMs, Protocol.MaxIntervalMs, "intervalMs");
        var onStatus = options?.OnStatus;
        var stored = OrderChecks.CheckStoredInvoice(invoice);
        // A failure each check meets again is logged once, and again only after it stopped (§23).
        var warnings = new OnceWarnings(_warnings.Logger);
        if (stored.OnchainAddress is not null)
        {
            return await new PaymentWait(this, stored, interval, onStatus, warnings, cancellationToken).RunAsync().ConfigureAwait(false);
        }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var parts = await CheckPartsAsync(stored, warnings, null, cancellationToken).ConfigureAwait(false);
                var status = StatusOf(stored, parts, Seconds());
                onStatus?.Invoke(status);
                if (status is PaymentStatus.Paid or PaymentStatus.Expired)
                {
                    return status;
                }
            }
            catch (NetworkException error)
            {
                cancellationToken.ThrowIfCancellationRequested();
                warnings.Failed(error);
            }
            warnings.Checked();
            await Task.Delay(TimeSpan.FromMilliseconds(interval), _time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// WaitForPaymentAsync of an invoice with an address (SPEC.md §31, "Waiting for an on-chain
    /// payment"): each status combines the last result of each part. A full check (every part, an
    /// invoice of the rest asked when needed) at each change the explorer's WebSocket pushes and every
    /// 60 s; between them, the Lightning invoice and the rest invoices the last full check found,
    /// every interval. Its state lives on one loop: every event (a check that ended, a timer, a
    /// message of the WebSocket) is one of its steps, in order.
    /// </summary>
    private sealed class PaymentWait
    {
        private readonly SattlemeClient _client;
        private readonly StoredInvoice _stored;
        private readonly int _interval;
        private readonly Action<PaymentStatus>? _onStatus;
        private readonly OnceWarnings _warnings;
        private readonly CancellationToken _cancellationToken;
        private readonly System.Threading.Channels.Channel<Action> _steps =
            System.Threading.Channels.Channel.CreateUnbounded<Action>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
        private readonly TaskCompletionSource<PaymentStatus> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Cancelled once the wait is over: its timers and the checks still running stop.</summary>
        private readonly CancellationTokenSource _work;

        /// <summary>The checks started (full checks, Lightning checks) that may still run: waited for before it returns.</summary>
        private readonly List<Task> _running = [];

        /// <summary>
        /// The merchant's Bitcoin node's failures, the wait's own: shared by its checks and its
        /// WebSocket (SPEC.md §31, "in the same call"); each check's channel read names the explorer
        /// the WebSocket follows.
        /// </summary>
        private readonly CallScope _call;

        /// <summary>
        /// The explorer's WebSocket: of the node the last channel read names (SPEC.md §31), with its
        /// fallback, opened once a check read the channel (before, whether mempool.space may be asked
        /// is not known), and opened again when the channel names another node or fallback.
        /// </summary>
        private AddressWatcher? _watcher;

        /// <summary>What <see cref="_watcher"/> watches: the node and its fallback, or mempool.space.</summary>
        private string? _watched;

        /// <summary>Every watch started, waited for before the wait returns.</summary>
        private readonly List<AddressWatcher> _watchers = [];

        // The last result of each part; not known until it was checked once.
        private Settled<LightningPart>? _lightning;
        private bool _lightningKnown;
        private Settled<OnchainPart>? _onchain;
        private bool _onchainKnown;
        private RestPart? _rests;
        private bool _done;
        private bool _lightningTimer;
        private int _onchainTimer;
        private bool _checking;
        private bool _again;
        private bool _lightningChecking;

        // Full checks started, the last one that ended, and the first one a status must wait for.
        private int _started;
        private int _ended;
        private int _waitFor;

        public PaymentWait(SattlemeClient client, StoredInvoice stored, int interval, Action<PaymentStatus>? onStatus, OnceWarnings warnings, CancellationToken cancellationToken)
        {
            _client = client;
            _stored = stored;
            _interval = interval;
            _onStatus = onStatus;
            _warnings = warnings;
            _cancellationToken = cancellationToken;
            _work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _lightningKnown = stored.Lightning is null;
            _call = new CallScope(new NodeFailures(), explorer => Post(() => Watch(explorer)));
        }

        public async Task<PaymentStatus> RunAsync()
        {
            try
            {
                using var registration = _cancellationToken.Register(() => Post(Abort));
                Post(CheckOnchain);
                await foreach (var step in _steps.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    step();
                    if (_done)
                    {
                        break;
                    }
                }
                return await _result.Task.ConfigureAwait(false);
            }
            finally
            {
                // Over: what it started stops first, and has ended once it returns, its watch of the
                // explorer's WebSocket and every check still running (SPEC.md §23). The steps they
                // post meanwhile are never run.
                await _work.CancelAsync().ConfigureAwait(false);
                _watcher?.Dispose();
                await Task.WhenAll([.. _running, .. _watchers.Select(watcher => watcher.Running)]).ContinueWith(
                    static all => _ = all.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default).ConfigureAwait(false);
                _work.Dispose();
            }
        }

        /// <summary>
        /// Watches the address on the WebSocket of <paramref name="explorer"/>, the one a check's
        /// channel read named, unless it already watches that one: the node's with its fallback, or
        /// mempool.space's.
        /// </summary>
        private void Watch(CallExplorer explorer)
        {
            var watched = explorer.WatchedNode ?? "mempool";
            if (_done || watched == _watched)
            {
                return;
            }
            _watcher?.Dispose();
            _watched = watched;
            _watcher = AddressWatcher.Start(_client._webSockets, explorer, _stored.OnchainAddress!, () => Post(CheckOnchain), _client._relayTimeoutMs);
            _watchers.RemoveAll(watcher => watcher.Running.IsCompleted);
            _watchers.Add(_watcher);
        }

        /// <summary>Keeps <paramref name="check"/>, a check just started, to wait for it at the end (and forgets those that ended).</summary>
        private void Started(Task check)
        {
            _running.RemoveAll(task => task.IsCompleted);
            _running.Add(check);
        }

        private void Post(Action step) => _steps.Writer.TryWrite(step);

        private void Finish(Action outcome)
        {
            if (_done)
            {
                return;
            }
            _done = true;
            outcome();
        }

        private void Abort() => Finish(() => _result.TrySetCanceled(_cancellationToken));

        private void Report()
        {
            if (_done || !_lightningKnown || !_onchainKnown)
            {
                return;
            }
            // A rest invoice was paid after the last full check began: its on-chain part was judged
            // against what was due then, never combined with it (it might read underpaid for good).
            if (_ended < _waitFor)
            {
                return;
            }
            PaymentStatus status;
            try
            {
                status = StatusOf(_stored, new Parts(_lightning, _onchain, _rests), _client.Seconds());
                _onStatus?.Invoke(status);
            }
            catch (NetworkException error)
            {
                _warnings.Failed(error);
                return;
            }
#pragma warning disable CA1031 // Any other error ends the wait with it.
            catch (Exception error)
#pragma warning restore CA1031
            {
                Finish(() => _result.TrySetException(error));
                return;
            }
            if (status is PaymentStatus.Paid or PaymentStatus.Expired)
            {
                Finish(() => _result.TrySetResult(status));
            }
        }

        /// <summary>Whether a Lightning invoice (the order's, or one of the rest) may still change.</summary>
        private bool LightningPending()
        {
            var own = !_lightningKnown || (_lightning is not null && !IsFinal(_lightning));
            return own || (_rests?.Results.Any(result => !IsFinal(result)) ?? false);
        }

        private void ScheduleLightning(int delay)
        {
            if (_done || _lightningTimer || _lightningChecking || !LightningPending())
            {
                return;
            }
            _lightningTimer = true;
            After(delay, CheckLightning);
        }

        /// <summary>Posts <paramref name="step"/> after <paramref name="delayMs"/>, unless the wait is over by then.</summary>
        private void After(int delayMs, Action step)
        {
            _ = Task.Delay(TimeSpan.FromMilliseconds(delayMs), _client._time, _work.Token).ContinueWith(
                _ => Post(step),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
        }

        private void CheckLightning()
        {
            _lightningTimer = false;
            _lightningChecking = true;
            var known = _rests;
            var lightningKnown = _lightningKnown;
            var lightning = _lightning;
            var token = _work.Token;
            var http = _client._http;
            var checkOwn = _stored.Lightning is not null && (!lightningKnown || (lightning is not null && !IsFinal(lightning)));
            Started(CheckAsync());

            async Task CheckAsync()
            {
                Settled<LightningPart>? own = null;
                Settled<LightningPart>[] results = [];
                try
                {
                    var ownTask = checkOwn
                        ? Settled<LightningPart>.OfAsync(Lnurl.VerifyPaymentAsync(_stored.Lightning!, http, _client.Seconds(), token), token)
                        : Task.FromResult<Settled<LightningPart>>(null!);
                    var restTasks = (known?.Invoices ?? []).Select((invoice, index) =>
                        IsFinal(known!.Results[index])
                            ? Task.FromResult(known.Results[index])
                            : Settled<LightningPart>.OfAsync(Lnurl.VerifyPaymentAsync(invoice.ToLightning(), http, _client.Seconds(), token), token)).ToList();
                    await Payments.WhenAllAsync([ownTask, .. restTasks]).ConfigureAwait(false);
                    own = checkOwn ? await ownTask.ConfigureAwait(false) : null;
                    results = new Settled<LightningPart>[restTasks.Count];
                    for (var i = 0; i < restTasks.Count; i++)
                    {
                        results[i] = await restTasks[i].ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // The wait is over (cancelled, or done): nothing to report.
                    Post(() => _lightningChecking = false);
                    return;
                }
                Post(() =>
                {
                    _lightningChecking = false;
                    if (_done)
                    {
                        return;
                    }
                    if (checkOwn)
                    {
                        _lightning = own;
                        _lightningKnown = true;
                    }
                    var paid = false;
                    // A full check may have found other rest invoices meanwhile: its results stand.
                    if (known is not null && ReferenceEquals(_rests, known))
                    {
                        paid = results.Where((result, index) => IsPaid(result) && !IsPaid(known.Results[index])).Any();
                        _rests = known with { Results = results };
                    }
                    // Paid by a rest invoice: what is due on-chain changed, a full check tells the status.
                    if (paid)
                    {
                        _waitFor = _started + 1;
                        CheckOnchain();
                    }
                    else
                    {
                        Report();
                    }
                    ScheduleLightning(_interval);
                });
            }
        }

        private void CheckOnchain()
        {
            if (_done)
            {
                return;
            }
            if (_checking)
            {
                _again = true;
                return;
            }
            _checking = true;
            _started++;
            var mine = _started;
            // Every 60 s at least: a socket may go quiet without closing.
            var timer = ++_onchainTimer;
            After(Protocol.OnchainCheckIntervalMs, () =>
            {
                if (timer == _onchainTimer)
                {
                    CheckOnchain();
                }
            });
            var token = _work.Token;
            Started(CheckAsync());

            async Task CheckAsync()
            {
                Parts parts;
                try
                {
                    parts = await _client.CheckPartsAsync(_stored, _warnings, null, token, _call).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // A check that fails ends the wait with its error, but for a cancellation.
                catch (Exception error)
#pragma warning restore CA1031
                {
                    Post(() =>
                    {
                        _checking = false;
                        if (!_cancellationToken.IsCancellationRequested && !token.IsCancellationRequested)
                        {
                            Finish(() => _result.TrySetException(error));
                        }
                    });
                    return;
                }
                Post(() =>
                {
                    _checking = false;
                    if (_done)
                    {
                        return;
                    }
                    _ended = mine;
                    _onchain = parts.Onchain;
                    _onchainKnown = true;
                    _rests = parts.Rests;
                    if (parts.Lightning is not null)
                    {
                        _lightning = parts.Lightning;
                        _lightningKnown = true;
                    }
                    Report();
                    // A full check ended: what it, and the checks of the Lightning invoices since the
                    // last one, no longer met has stopped.
                    _warnings.Checked();
                    ScheduleLightning(_interval);
                    if (_again)
                    {
                        _again = false;
                        CheckOnchain();
                    }
                });
            }
        }
    }

    /// <summary>
    /// The invoices declared for the order <paramref name="reference"/> in the merchant's registry,
    /// oldest first (SPEC.md §24): to find an order's invoice again after a failure. Read on a
    /// majority of the merchant's relays (any declaration and lease was published on one); throws
    /// <see cref="NetworkException"/> when fewer answered. It publishes nothing: to show an order's
    /// invoice again, call <see cref="CreateInvoiceAsync"/>, whose <see cref="InvoicePendingException"/>
    /// carries the pending invoice once its records are on a majority of the relays again.
    /// </summary>
    public Task<IReadOnlyList<DeclaredInvoice>> FindInvoicesAsync(string reference, CancellationToken cancellationToken = default)
    {
        var checkedRef = OrderChecks.CheckRef(reference);
        return WithRelaysAsync<IReadOnlyList<DeclaredInvoice>>(async pool =>
        {
            var merchant = await ChannelAsync(pool, _warnings, cancellationToken).ConfigureAwait(false);
            var sale = Derive.SaleOf(_access.Key, merchant.Registry, checkedRef);
            var declared = await DeclaredAsync(pool, sale, merchant, _warnings, everyRelay: false, cancellationToken).ConfigureAwait(false);
            return [.. declared.Invoices.Select(entry => entry.Invoice)];
        }, cancellationToken);
    }

    private static Task<SaleRecords> DeclaredAsync(RelayPool pool, Sale sale, MerchantChannel merchant, Warnings warnings, bool everyRelay, CancellationToken ct) =>
        Sales.ReadAsync(pool, sale, merchant.Relays, warnings, everyRelay, ct);

    /// <summary>
    /// What the server needs of the merchant, read with the key alone (the integration's channel),
    /// with the explorer of the call: the merchant's Bitcoin node with its fallback (SPEC.md §31),
    /// else mempool.space.
    /// </summary>
    private sealed record MerchantChannel(
        string BusinessName,
        string LightningAddress,
        Registry Registry,
        IReadOnlyList<string> Relays,
        string? CheckoutOrigin,
        int Tolerance,
        BitcoinNode? BitcoinNode,
        CallExplorer Explorer)
    {
        /// <summary>No Lightning address, no registry key: what it says of the merchant.</summary>
        public override string ToString() => $"MerchantChannel {{ {BusinessName} }}";
    }

    /// <summary>
    /// The integration's channel, read on the connections of <paramref name="pool"/>, with the
    /// explorer of the call: its node's failures in <paramref name="failures"/> (a new call's own by
    /// default).
    /// </summary>
    private async Task<MerchantChannel> ChannelAsync(RelayPool pool, Warnings warnings, CancellationToken ct, NodeFailures? failures = null)
    {
        var channel = await Channels.ReadAsync(pool, _access, _random, warnings, ct).ConfigureAwait(false);
        return new MerchantChannel(
            channel.BusinessName,
            channel.LightningAddress,
            Registry.Of(channel.RegistryKey, channel.RegistryReader),
            channel.Relays ?? _access.Relays,
            channel.CheckoutOrigin,
            channel.Tolerance,
            channel.BitcoinNode,
            new CallExplorer(channel.BitcoinNode, failures ?? new NodeFailures(), Milliseconds));
    }

    /// <summary>
    /// Runs <paramref name="run"/> with the relays of one call: one connection per relay, closed once
    /// the call is done (a write still running past the quorum first ends, bounded by its timeout).
    /// </summary>
    private async Task<T> WithRelaysAsync<T>(Func<RelayPool, Task<T>> run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var pool = new RelayPool(_webSockets, _relayTimeoutMs, _random, Seconds, ct);
        try
        {
            return await run(pool).ConfigureAwait(false);
        }
        finally
        {
            pool.Release();
        }
    }

    /// <summary>Now, in Unix seconds, rounded down.</summary>
    private long Seconds() => _time.GetUtcNow().ToUnixTimeSeconds();

    /// <summary>Now, in Unix milliseconds: when the merchant's Bitcoin node failed.</summary>
    private long Milliseconds() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>The status of <paramref name="stored"/> from its parts, at <paramref name="now"/> (Unix seconds); throws a part's error.</summary>
    private static PaymentStatus StatusOf(StoredInvoice stored, Parts parts, long now) =>
        Payments.Combine(new InvoiceTerms(stored.Sats, stored.ExpiresAt, stored.OnchainAddress), now, parts.Lightning, parts.Onchain, parts.Rests);

    /// <summary>Whether a Lightning part is final: paid, or expired for good.</summary>
    private static bool IsFinal(Settled<LightningPart> result) => result.Ok && result.Value!.State != LightningState.Open;

    private static bool IsPaid(Settled<LightningPart> result) => result is { Ok: true, Value.State: LightningState.Paid };

    /// <summary>
    /// Stops what <paramref name="beside"/>'s token bounds, then waits until <paramref name="tasks"/>
    /// ended, whatever they end with (an error the call does not give is not its own): a call
    /// returns once what it started beside its own flow ended.
    /// </summary>
    private static async Task StopAsync(CancellationTokenSource beside, params Task?[] tasks)
    {
        await beside.CancelAsync().ConfigureAwait(false);
        foreach (var task in tasks)
        {
            if (task is null)
            {
                continue;
            }
            try
            {
                await task.ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Its error, if the call needs it, was awaited in the call's flow.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

    /// <summary>
    /// <paramref name="items"/> in a random order, from the CSPRNG (so that two servers rarely try the
    /// same address first): Fisher–Yates, each draw without bias.
    /// </summary>
    private static List<T> Shuffle<T>(List<T> items, RandomSource random)
    {
        var shuffled = new List<T>(items);
        for (var i = shuffled.Count - 1; i > 0; i--)
        {
            var bound = (ulong)(i + 1);
            // The largest multiple of bound within 2^32: draws at or above it are drawn again.
            var limit = (1UL << 32) - ((1UL << 32) % bound);
            ulong draw;
            do
            {
                var bytes = random.Bytes(4);
                draw = ((ulong)bytes[0] << 24) | ((ulong)bytes[1] << 16) | ((ulong)bytes[2] << 8) | bytes[3];
            }
            while (draw >= limit);
            var j = (int)(draw % bound);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }
        return shuffled;
    }

    /// <summary><paramref name="run"/> on every item, <paramref name="limit"/> at a time: the outcome of each, in order (never fails but for a cancellation).</summary>
    private static async Task<List<Settled<TResult>>> SettleEachAsync<TItem, TResult>(IReadOnlyList<TItem> items, int limit, Func<TItem, Task<TResult>> run, CancellationToken ct)
    {
        var results = new Settled<TResult>[items.Count];
        var next = -1;
        async Task Worker()
        {
            while (true)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= items.Count)
                {
                    return;
                }
                results[index] = await Settled<TResult>.OfAsync(run(items[index]), ct).ConfigureAwait(false);
            }
        }
        await Payments.WhenAllAsync([.. Enumerable.Range(0, Math.Min(limit, items.Count)).Select(_ => Worker())]).ConfigureAwait(false);
        return [.. results];
    }
}
