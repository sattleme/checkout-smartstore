using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sattleme.Bitcoin.Domain;
using Smartstore.Core.Checkout.Orders;
using Smartstore.Core.Data;
using SmartstoreSattleme.Sattleme.Checkout;
using SdkPaymentStatus = SmartstoreSattleme.Sattleme.Checkout.PaymentStatus;
using ShopOrder = Smartstore.Core.Checkout.Orders.Order;

namespace Sattleme.Bitcoin.Services;

/// <summary>
/// Checks the payment of an invoice, and applies what the SDK found to its order (README.md,
/// "Payments and orders"):
/// <list type="bullet">
/// <item>only a <c>paid</c> status of the SDK's own check of the invoice the order stored marks the
/// order paid, never the browser; exactly once (the check's lease and the completion's claim are
/// conditional updates), through Smartstore's standard path (Order.CanMarkOrderAsPaid, then
/// IOrderProcessingService.MarkOrderAsPaidAsync), with the invoice's reference as the capture
/// transaction's id;</item>
/// <item><c>expired</c>: never paid, for good; with something received (an on-chain underpayment),
/// a note says what reached the wallet, to settle by hand;</item>
/// <item><c>unconfirmed</c>: waits (3 confirmations, about 30 minutes), with a note once.</item>
/// </list>
/// What the merchant must settle by hand (a payment of a cancelled order, of an order whose total
/// changed, an underpayment) is noted on the order and flagged as a new payment notification
/// (Order.HasNewPaymentNotification: the badge Smartstore's admin shows in its list of orders), and
/// logged. Two checks of one invoice never run at once, and checks in a row are harmless: the SDK's
/// checks are made to be repeated, and settling is idempotent.
/// </summary>
public class PaymentChecker
{
    private readonly SmartDbContext _db;
    private readonly ILogger<PaymentChecker> _logger;
    private readonly IntegrationKeys _keys;
    private readonly InvoiceMaker _maker;
    private readonly IOrderProcessingService _orderProcessingService;
    private readonly ISattlemeGateway _gateway;
    private readonly OrderNotes _notes;
    private readonly PersonalDataShredder _shredder;
    private readonly SattlemeStore _store;
    private readonly TimeProvider _time;

    /// <summary>The checker.</summary>
    public PaymentChecker(SmartDbContext db,
        ILogger<PaymentChecker> logger,
        IntegrationKeys keys,
        InvoiceMaker maker,
        IOrderProcessingService orderProcessingService,
        ISattlemeGateway gateway,
        OrderNotes notes,
        PersonalDataShredder shredder,
        SattlemeStore store,
        TimeProvider time)
    {
        _db = db;
        _logger = logger;
        _keys = keys;
        _maker = maker;
        _orderProcessingService = orderProcessingService;
        _gateway = gateway;
        _notes = notes;
        _shredder = shredder;
        _store = store;
        _time = time;
    }

    /// <summary>
    /// Checks the payment of the invoice <paramref name="invoiceId"/> now, the SDK's call bounded by
    /// <paramref name="timeout"/>, and applies what was found to its order. Skipped (false) when the
    /// invoice is final or not made, when another check of it is under way, or when one ended less
    /// than <paramref name="minInterval"/> ago. <paramref name="run"/>: when the scheduled task's run
    /// that asks started (its next check counts from then, PaymentRules.NextCheck); null for the
    /// payment page.
    /// </summary>
    public virtual async Task<bool> CheckAsync(int invoiceId, TimeSpan minInterval, TimeSpan timeout, DateTime? run = null)
    {
        var now = UtcNow();
        var leaseId = SattlemeStore.NewSecret(16);
        DateTime? checkedBefore = minInterval > TimeSpan.Zero ? now - minInterval : null;
        if (!await _store.TryLeaseCheckAsync(invoiceId, leaseId, now, now + timeout + SattlemeDefaults.LeaseMargin, checkedBefore))
        {
            return false;
        }
        var invoice = await _store.GetInvoiceAsync(invoiceId);
        if (invoice?.InvoiceJson is null)
        {
            return false;
        }

        SdkPaymentStatus status;
        try
        {
            var record = await _store.GetOrderAsync(invoice.OrderId);
            var key = (record is null ? null : await _keys.KeyOfStoreAsync(record.StoreId)) ?? throw new MissingKeyException();
            using var cancel = new CancellationTokenSource(timeout);
            status = await _gateway.ClientOf(key).GetStatusAsync(InvoiceToCheck.FromJson(invoice.InvoiceJson), cancel.Token);
        }
        catch (Exception exception)
        {
            await FailAsync(invoice, leaseId, exception, PaymentRules.ErrorCode(exception), run);
            return true;
        }

        try
        {
            await SettleAsync(invoice, leaseId, status, run);
        }
        catch (Exception exception)
        {
            // Smartstore could not take the update (its database, a module reacting to the payment):
            // the invoice is not final, and a later check applies its status again. What it left
            // unsaved in this request's context is dropped first: never saved by a later call (the
            // scheduled task checks several invoices in one context).
            DiscardUnsaved();
            await FailAsync(invoice, leaseId, exception, "apply", run);
        }
        return true;
    }

    /// <summary>
    /// Drops what the context holds and the database does not: entities added, changed or deleted
    /// since they were read (a completion that stopped half-way in Smartstore's code).
    /// </summary>
    private void DiscardUnsaved()
    {
        foreach (var entry in _db.ChangeTracker.Entries().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.State = EntityState.Detached;
                    break;
                case EntityState.Modified:
                case EntityState.Deleted:
                    entry.CurrentValues.SetValues(entry.OriginalValues);
                    entry.State = EntityState.Unchanged;
                    break;
            }
        }
    }

    private DateTime UtcNow()
    {
        return _time.GetUtcNow().UtcDateTime;
    }

    /// <summary>A check failed: counted, its next one later, logged when it starts failing or fails otherwise.</summary>
    private async Task FailAsync(SattlemeInvoice invoice, string leaseId, Exception exception, string code, DateTime? run)
    {
        var now = UtcNow();
        await _store.FailCheckAsync(invoice.Id, leaseId, code, now);
        await _store.SetNextCheckAsync(invoice.Id, PaymentRules.NextCheck(invoice.ExpiresAt, invoice.Errors + 1, now, run));
        if (code == invoice.Error)
        {
            return;
        }
        if (code is "error" or "apply")
        {
            _logger.LogError(exception, "sattle.me: the check of the Bitcoin invoice {Reference} failed: {Code}", invoice.Reference, code);
        }
        else
        {
            _logger.LogWarning("sattle.me: the check of the Bitcoin invoice {Reference} failed: {Code}: {Message}", invoice.Reference, code, exception.Message);
        }
    }

    /// <summary>
    /// Stores what the check found and updates the order: paid when paid, its invoice final when
    /// paid or expired. Final only once the order was updated, so that a check that stops half-way
    /// is simply done again.
    /// </summary>
    private async Task SettleAsync(SattlemeInvoice invoice, string leaseId, SdkPaymentStatus status, DateTime? run)
    {
        var previous = invoice.State;
        var received = PaymentRules.ReceivedOf(status);
        if (!await _store.StoreStatusAsync(invoice.Id, leaseId, status.ToJson(), PaymentRules.StateOf(status), received, UtcNow()))
        {
            return; // This check outlived its lease: another one decides.
        }

        var order = await LoadOrderAsync(invoice.OrderId);
        var final = false;
        switch (status)
        {
            case SdkPaymentStatus.Paid paid:
                final = await CompleteAsync(invoice, leaseId, order, paid);
                break;
            // Final only from a check made after the end of its grace period (the SDK reports expired
            // only then): until then, a payment of its last seconds may still be found.
            case SdkPaymentStatus.Expired when _time.GetUtcNow().ToUnixTimeSeconds() >= invoice.ExpiresAt + SattlemeDefaults.GraceSeconds:
                final = await ExpireAsync(invoice, order, received);
                break;
            // What came in time, against what was asked: the status gives no method, so the note
            // says nothing of how it came.
            case SdkPaymentStatus.Unconfirmed unconfirmed when previous != "unconfirmed" && order is not null:
                await _notes.AddAsync(order.Id, "Note.Unconfirmed", invoice.Reference, received, StoredInvoice.Of(invoice)?.Sats ?? 0L, unconfirmed.Confirmations);
                break;
        }

        // Released only now: no other check settles the order meanwhile. Never made non-final.
        await _store.ReleaseCheckAsync(invoice.Id, leaseId, final, PaymentRules.NextCheck(invoice.ExpiresAt, 0, UtcNow(), run));
        if (final)
        {
            await _shredder.ShredIfFinalAsync(invoice.Id);
        }
    }

    /// <summary>
    /// The order as the database holds it now (tracked, so that Smartstore's MarkOrderAsPaidAsync
    /// saves it): reloaded when this request's context tracks it already.
    /// </summary>
    private async Task<ShopOrder?> LoadOrderAsync(int orderId)
    {
        var tracked = _db.Orders.Local.FirstOrDefault(order => order.Id == orderId);
        if (tracked is not null)
        {
            await _db.Entry(tracked).ReloadAsync();
            return _db.Entry(tracked).State == EntityState.Detached ? null : tracked;
        }
        return await _db.Orders.IgnoreQueryFilters().FirstOrDefaultAsync(order => order.Id == orderId);
    }

    /// <summary>
    /// The invoice was paid: its order is marked paid, once. Whether the invoice is final (false:
    /// checked again, the completion not done yet).
    /// </summary>
    private async Task<bool> CompleteAsync(SattlemeInvoice invoice, string leaseId, ShopOrder? order, SdkPaymentStatus.Paid paid)
    {
        var now = UtcNow();
        var sats = StoredInvoice.Of(invoice)?.Sats ?? 0;
        // What came: in Lightning, the invoice's own amount (its status gives no received); on-chain
        // or mixed, what the status says came in time.
        var came = PaymentRules.CameOf(paid, sats);
        if (order is null)
        {
            // The order was deleted from the database: the bitcoin came all the same.
            _logger.LogError("sattle.me: the Bitcoin invoice {Reference} was paid ({Came} sats came), and its order {OrderId} no longer exists: settle it by hand", invoice.Reference, came, invoice.OrderId);
            return true;
        }
        if (PaymentRules.IsPaid(order))
        {
            // Paid by this invoice: a completion that stopped before its end. Smartstore's
            // MarkOrderAsPaidAsync saves the order paid (with this invoice's reference, set below),
            // then completes an order with nothing to ship (its e-mails) and publishes OrderPaidEvent:
            // when that throws, the order is paid and this check's notes were not written. They are
            // now, each unless the order has it already; what Smartstore does after a payment is not
            // run again (its log says what failed).
            if (order.CaptureTransactionId == invoice.Reference)
            {
                _logger.LogWarning("sattle.me: the order {OrderId} was marked paid by the Bitcoin invoice {Reference}, and Smartstore's processing of it stopped before its end: see its log", order.Id, invoice.Reference);
                await NotePaidAsync(order, invoice, paid, sats, came, once: true);
                return true;
            }

            // Paid otherwise: then the order was paid twice, which the merchant settles.
            await _notes.AddAsync(order.Id, "Note.PaidTwice", invoice.Reference, came);
            await NoteAmountAsync(order, invoice, paid, sats);
            await NotifyMerchantAsync(order);
            _logger.LogWarning("sattle.me: the Bitcoin invoice {Reference} was paid, and the order {OrderId} was paid otherwise: settle it by hand", invoice.Reference, order.Id);
            return true;
        }

        // One completion at a time, whichever check sees the payment first: the others wait.
        if (!await _store.ClaimCompletionAsync(invoice.Id, leaseId, now, now - SattlemeDefaults.CompletionStale))
        {
            return false;
        }

        // Whatever the order became, the bitcoin came: the module never refuses it, and says what to
        // settle. Smartstore cannot take an order out of Cancelled (Order.CanMarkOrderAsPaid and
        // CanCompleteOrder refuse it, and its admin has no other way): it stays cancelled, noted,
        // and the merchant is told (the order list's payment notification, the log).
        if (order.Deleted || order.OrderStatus == OrderStatus.Cancelled)
        {
            await _notes.AddAsync(order.Id, "Note.PaidCancelled", invoice.Reference, came);
            await NoteAmountAsync(order, invoice, paid, sats);
            await NotifyMerchantAsync(order);
            _logger.LogWarning("sattle.me: the Bitcoin invoice {Reference} of the cancelled order {OrderId} was paid ({Came} sats reached the merchant's wallet): it stays cancelled, refund the customer or settle it by hand", invoice.Reference, order.Id, came);
            return true;
        }

        // What the order still owes now (amount and currency: its total, less gift cards, reward
        // points and wallet credit, less refunds) against the invoice's price: an admin may edit an
        // order's totals in any state (Admin → Orders → Edit, OrderController.EditOrderTotals).
        // Paid at the invoice's amount, an order that owes another amount is never completed:
        // Smartstore has no "on hold" state, so its payment stays pending (the orders Smartstore's
        // admin lists as unpaid), with a note, for the merchant to settle the difference and mark
        // it paid by hand.
        var owed = PaymentRules.Owed(order);
        var price = _maker.PriceOf(owed);
        if (order.PaymentMethodSystemName != SattlemeDefaults.SystemName
            || price is null || !PaymentRules.SamePrice(invoice, price.Amount, price.Currency))
        {
            var owes = price is null
                ? owed.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture)
                : $"{price.Amount} {price.Currency}";
            await _notes.AddAsync(order.Id, "Note.PaidOtherTotal", invoice.Reference, came, $"{invoice.PriceAmount} {invoice.PriceCurrency}", owes);
            await NoteAmountAsync(order, invoice, paid, sats);
            await NotifyMerchantAsync(order);
            _logger.LogWarning("sattle.me: the Bitcoin invoice {Reference} was paid at {Price} {Currency}, and the order {OrderId} owes {Owes} now: its payment stays pending, settle it by hand", invoice.Reference, invoice.PriceAmount, invoice.PriceCurrency, order.Id, owes);
            return true;
        }
        if (!order.CanMarkOrderAsPaid())
        {
            await _notes.AddAsync(order.Id, "Note.PaidNotMarked", invoice.Reference, came);
            await NoteAmountAsync(order, invoice, paid, sats);
            await NotifyMerchantAsync(order);
            _logger.LogWarning("sattle.me: the Bitcoin invoice {Reference} was paid, and Smartstore cannot mark the order {OrderId} paid in its current state: settle it by hand", invoice.Reference, order.Id);
            return true;
        }

        order.CaptureTransactionId = invoice.Reference;
        order.CaptureTransactionResult = paid.Method switch
        {
            PaymentMethod.Lightning => "paid: lightning",
            PaymentMethod.Onchain => "paid: on-chain",
            _ => "paid: on-chain and lightning",
        };
        await _orderProcessingService.MarkOrderAsPaidAsync(order);
        await NotePaidAsync(order, invoice, paid, sats, came, once: false);
        return true;
    }

    /// <summary>
    /// The notes of an order this invoice paid: how it was paid and what came, against the invoice's
    /// amount; on-chain, how many transactions (never which: a note holds no transaction), and in
    /// Lightning by how many invoices of the rest. The status gives no split of what came between the
    /// two. <paramref name="once"/>: each note unless the order has it already.
    /// </summary>
    private async Task NotePaidAsync(ShopOrder order, SattlemeInvoice invoice, SdkPaymentStatus.Paid paid, long sats, long came, bool once)
    {
        switch (paid.Method)
        {
            case PaymentMethod.Lightning:
                await AddNoteAsync(order, once, "Note.PaidLightning", invoice.Reference, sats);
                break;
            case PaymentMethod.Onchain:
                await AddNoteAsync(order, once, "Note.PaidOnchain", invoice.Reference, came, sats, paid.Txids.Count);
                break;
            default:
                await AddNoteAsync(order, once, "Note.PaidMixed", invoice.Reference, came, sats, paid.Txids.Count, paid.Preimages.Count);
                break;
        }
        await NoteAmountAsync(order, invoice, paid, sats, once);
    }

    /// <summary>
    /// A payment that received another amount than the invoice's: less, which only the merchant's
    /// tolerance or the merchant's acceptance in its app makes the SDK report paid (on-chain), or more
    /// (an overpayment). A note says by how much. Never a reason to leave the order unpaid: the SDK's
    /// paid status decides that. <paramref name="once"/>: unless the order has that note already.
    /// </summary>
    private async Task NoteAmountAsync(ShopOrder order, SattlemeInvoice invoice, SdkPaymentStatus.Paid paid, long sats, bool once = false)
    {
        var came = PaymentRules.CameOf(paid, sats);
        if (came < sats)
        {
            await AddNoteAsync(order, once, "Note.PaidShort", invoice.Reference, sats - came);
        }
        else if (came > sats)
        {
            await AddNoteAsync(order, once, "Note.PaidOver", invoice.Reference, came - sats);
        }
    }

    private Task AddNoteAsync(ShopOrder order, bool once, string resource, params object[] arguments)
    {
        return once ? _notes.AddOnceAsync(order.Id, resource, arguments) : _notes.AddAsync(order.Id, resource, arguments);
    }

    /// <summary>The invoice ended unpaid: final; with something received, a note says what to settle.</summary>
    private async Task<bool> ExpireAsync(SattlemeInvoice invoice, ShopOrder? order, long received)
    {
        var sats = StoredInvoice.Of(invoice)?.Sats ?? 0;
        if (received > 0)
        {
            // Some bitcoin came, less than the amount: it reached the merchant's wallet, the order
            // is not paid. The merchant settles it with the customer.
            _logger.LogWarning("sattle.me: the Bitcoin invoice {Reference} ended underpaid: {Received} of {Sats} sats", invoice.Reference, received, sats);
            if (order is not null)
            {
                await _notes.AddAsync(order.Id, "Note.Underpaid", invoice.Reference, received, sats);
                await NotifyMerchantAsync(order);
            }
        }
        else if (order is not null)
        {
            await _notes.AddAsync(order.Id, "Note.Expired", invoice.Reference);
        }
        return true;
    }

    /// <summary>
    /// Tells the merchant to look at the order: Smartstore's "new payment notification" of an order,
    /// a badge in the admin's list of orders until the order is opened (the module documentation of
    /// Smartstore asks payment modules to set it for any payment message received).
    /// </summary>
    private async Task NotifyMerchantAsync(ShopOrder order)
    {
        if (!order.HasNewPaymentNotification)
        {
            order.HasNewPaymentNotification = true;
            await _db.SaveChangesAsync();
        }
    }
}
