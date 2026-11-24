using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Sattleme.Bitcoin.Configuration;
using Sattleme.Bitcoin.Domain;
using Smartstore.Core.Checkout.Orders;
using Smartstore.Core.Common.Services;
using Smartstore.Core.Configuration;
using Smartstore.Core.Stores;
using SmartstoreSattleme.Sattleme.Checkout;
using SdkOrder = SmartstoreSattleme.Sattleme.Checkout.Order;
using ShopOrder = Smartstore.Core.Checkout.Orders.Order;
using ShopPaymentStatus = Smartstore.Core.Checkout.Payment.PaymentStatus;

namespace Sattleme.Bitcoin.Services;

/// <summary>The price an invoice is asked for: the order's total in the store's primary currency.</summary>
/// <param name="Amount">As the SDK takes it: <c>24.50</c>.</param>
/// <param name="Currency">ISO 4217, in the SDK's table of currencies.</param>
public sealed record PriceToPay(string Amount, string Currency);

/// <summary>An invoice made (or an earlier one of the order handed out again), or why none is.</summary>
public sealed class MakeResult
{
    private MakeResult(SattlemeInvoice? invoice, bool paid, string? refusal, string? errorCode, object[] arguments)
    {
        Invoice = invoice;
        Paid = paid;
        Refusal = refusal;
        ErrorCode = errorCode;
        RefusalArguments = arguments;
    }

    /// <summary>The order's invoice that may be paid, when one is made.</summary>
    public SattlemeInvoice? Invoice { get; }

    /// <summary>The SDK reported its reference paid already (an earlier try made it, and it was paid).</summary>
    public bool Paid { get; }

    /// <summary>When none is made: the module's resource that tells the customer why (a composite format).</summary>
    public string? Refusal { get; }

    /// <summary>The arguments of <see cref="Refusal"/> (a DateTime is in UTC: shown in the customer's time zone).</summary>
    public object[] RefusalArguments { get; }

    /// <summary>When none is made because a call failed: its code (SPEC.md §5, or the module's own).</summary>
    public string? ErrorCode { get; }

    /// <summary>An invoice made.</summary>
    public static MakeResult Made(SattlemeInvoice invoice, bool paid)
    {
        return new(invoice, paid, null, null, []);
    }

    /// <summary>No invoice, for the reason <paramref name="refusal"/>.</summary>
    public static MakeResult Refused(string refusal, string? errorCode = null, params object[] arguments)
    {
        return new(null, false, refusal, errorCode, arguments);
    }
}

/// <summary>
/// Makes the invoices of orders (README.md, "Payments and orders"): the first one once Smartstore
/// placed the order (the payment method's PostProcessPaymentAsync, outside the placement's database
/// transaction), a new one from the payment page once the last one ended unpaid. For each:
/// <list type="bullet">
/// <item>a record with a new random reference (128 bits of the CSPRNG), stored before the SDK is
/// asked for an invoice of it; a try that follows a failed one asks for the same reference, and the
/// SDK hands out the invoice it may have made meanwhile, or reports it paid;</item>
/// <item>the order locked in the database around the SDK's call (SattlemeStore.TryLockOrderAsync):
/// two requests at once (a double click, two servers) get one invoice;</item>
/// <item>one invoice that may be paid per order: a new one only once the last ended unpaid.</item>
/// </list>
/// </summary>
public class InvoiceMaker
{
    /// <summary>How often a request waiting for the order's lock tries to take it.</summary>
    public static readonly TimeSpan LockPoll = TimeSpan.FromMilliseconds(250);

    private readonly ICurrencyService _currencyService;
    private readonly ILogger<InvoiceMaker> _logger;
    private readonly IntegrationKeys _keys;
    private readonly ISattlemeGateway _gateway;
    private readonly ISettingFactory _settingFactory;
    private readonly IStoreContext _storeContext;
    private readonly OrderNotes _notes;
    private readonly SattlemeStore _store;
    private readonly TimeProvider _time;

    /// <summary>The maker of invoices.</summary>
    public InvoiceMaker(ICurrencyService currencyService,
        ILogger<InvoiceMaker> logger,
        IntegrationKeys keys,
        ISattlemeGateway gateway,
        ISettingFactory settingFactory,
        IStoreContext storeContext,
        OrderNotes notes,
        SattlemeStore store,
        TimeProvider time)
    {
        _currencyService = currencyService;
        _logger = logger;
        _keys = keys;
        _gateway = gateway;
        _settingFactory = settingFactory;
        _storeContext = storeContext;
        _notes = notes;
        _store = store;
        _time = time;
    }

    /// <summary>
    /// The invoice of the placed order <paramref name="order"/>: its first one, or a new one once the
    /// last one ended unpaid; the one it has while that one may still be paid.
    /// </summary>
    public virtual async Task<MakeResult> MakeAsync(ShopOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.Id == 0 || order.Deleted || order.OrderStatus == OrderStatus.Cancelled || order.PaymentStatus != ShopPaymentStatus.Pending
            || order.PaymentMethodSystemName != SattlemeDefaults.SystemName)
        {
            return MakeResult.Refused("Error.NotPending");
        }
        var price = PriceOf(PaymentRules.Owed(order));
        if (price is null)
        {
            return MakeResult.Refused("Error.NotPayable", "currency");
        }
        var record = await _store.GetOrMakeOrderAsync(order.Id, order.OrderGuid, order.StoreId, order.CustomerId, UtcNow());
        return await MakeLockedAsync(record, price, order);
    }

    /// <summary>
    /// The price of <paramref name="total"/> (in the store's primary currency), as the SDK takes it;
    /// null when that currency is not one the SDK converts (its table: Currencies.DigitsOf), or the
    /// total is not above 0.
    /// </summary>
    public virtual PriceToPay? PriceOf(decimal total)
    {
        var code = _currencyService.PrimaryCurrency?.CurrencyCode;
        if (code is null || Currencies.DigitsOf(code) is not { } digits)
        {
            return null;
        }
        return PaymentRules.PriceAmount(total, digits) is { } amount ? new PriceToPay(amount, code) : null;
    }

    private DateTime UtcNow()
    {
        return _time.GetUtcNow().UtcDateTime;
    }

    private async Task<MakeResult> MakeLockedAsync(SattlemeOrder record, PriceToPay price, ShopOrder order)
    {
        var key = await _keys.KeyOfStoreAsync(record.StoreId);
        if (key is null)
        {
            _logger.LogWarning("sattle.me: no Bitcoin invoice can be made for the order {OrderId}: no integration key is set for its store, or the one saved cannot be read", order.Id);
            await _notes.AddAsync(order.Id, "Note.Failed", "-", new NoteResource("ErrorCode.no_key"));
            return MakeResult.Refused("Error.Unavailable", "no_key");
        }

        // The order's lock: another request making its invoice holds it until it is done.
        var lockId = SattlemeStore.NewSecret(16);
        var started = _time.GetTimestamp();
        while (true)
        {
            var now = UtcNow();
            if (await _store.TryLockOrderAsync(record.OrderId, lockId, now, now + SattlemeDefaults.CreateTimeout + SattlemeDefaults.LeaseMargin))
            {
                break;
            }
            if (_time.GetElapsedTime(started) > SattlemeDefaults.CreateTimeout + SattlemeDefaults.LockWait)
            {
                return MakeResult.Refused("Error.TryAgain", "busy");
            }
            await Task.Delay(LockPoll, _time);
        }
        var waited = _time.GetElapsedTime(started);

        try
        {
            return await MakeUnderLockAsync(record, price, order, key, waited);
        }
        finally
        {
            await _store.UnlockOrderAsync(record.OrderId, lockId);
        }
    }

    private async Task<MakeResult> MakeUnderLockAsync(SattlemeOrder record, PriceToPay price, ShopOrder order, string key, TimeSpan waited)
    {
        var invoices = await _store.GetInvoicesAsync(record.OrderId);
        var now = UtcNow();
        var unixNow = _time.GetUtcNow().ToUnixTimeSeconds();

        // One invoice that may be paid per order: that one (its page shows where it is), never a
        // second one.
        if (invoices.FirstOrDefault(PaymentRules.MayBePaid) is { } payable)
        {
            return PaymentRules.SamePrice(payable, price.Amount, price.Currency)
                ? MakeResult.Made(payable, payable.State == "paid")
                : Busy(payable, unixNow);
        }

        var last = invoices.LastOrDefault();
        if (last is { Final: true, State: "paid" })
        {
            return MakeResult.Refused("Error.NotPending");
        }
        if (last is { Final: true, Received: > 0 })
        {
            return MakeResult.Refused("Error.Underpaid");
        }

        // Waited long for another request that made no invoice: two calls in a row could outlast this one.
        if (waited > SattlemeDefaults.LockWait)
        {
            return MakeResult.Refused("Error.TryAgain", "busy");
        }

        // A try that made no invoice (a timeout, an error) asks for the same reference again.
        SattlemeInvoice attempt;
        if (last is { InvoiceJson: null, Final: false } && PaymentRules.SamePrice(last, price.Amount, price.Currency))
        {
            attempt = last;
        }
        else
        {
            attempt = new SattlemeInvoice
            {
                OrderId = record.OrderId,
                Reference = SattlemeDefaults.ReferencePrefix + SattlemeStore.NewSecret(16),
                PriceAmount = price.Amount,
                PriceCurrency = price.Currency,
                CreatedOnUtc = now,
            };
            await _store.InsertInvoiceAsync(attempt);
        }

        var settings = await _settingFactory.LoadSettingsAsync<SattlemeSettings>(record.StoreId);
        var minutes = Math.Clamp(settings.MinutesToPay, SattlemeDefaults.MinMinutes, SattlemeDefaults.MaxMinutes);

        string? json = null;
        var paid = false;
        Exception? failure = null;
        using (var timeout = new CancellationTokenSource(SattlemeDefaults.CreateTimeout))
        {
            try
            {
                var invoice = await _gateway.ClientOf(key).CreateInvoiceAsync(new SdkOrder
                {
                    Ref = attempt.Reference,
                    Fiat = new Price(price.Amount, price.Currency),
                    DurationSeconds = minutes * 60,
                    Note = NoteOf(record.StoreId, order),
                }, timeout.Token);
                json = invoice.ToJson();
            }
            catch (InvoicePendingException pending)
            {
                // An earlier try of this reference failed once the SDK had made its invoice: that one.
                json = pending.Invoice.ToJson();
            }
            catch (OrderPaidException orderPaid)
            {
                // That invoice was paid already (this server lost it): stored, then checked, which
                // marks the order paid as any check does.
                json = orderPaid.Invoice.ToJson();
                paid = true;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        if (json is not null && Mismatch(json, price) is { } mismatch)
        {
            failure = mismatch;
        }
        if (failure is not null)
        {
            return await RefuseAsync(attempt, order, failure);
        }

        var made = StoredInvoice.Of(new SattlemeInvoice { InvoiceJson = json })!;
        await _store.StoreMadeInvoiceAsync(attempt.Id, json!, made.ExpiresAt, UtcNow());
        var stored = await _store.GetInvoiceAsync(attempt.Id) ?? attempt;
        await _notes.AddAsync(order.Id, "Note.Created", stored.Reference, made.Sats);
        return MakeResult.Made(stored, paid);
    }

    /// <summary>
    /// Why the order cannot get a new invoice while <paramref name="payable"/> may still be paid:
    /// it can be paid until a time; its time is over and its final status is being checked; or a
    /// payment of it is being confirmed.
    /// </summary>
    private static MakeResult Busy(SattlemeInvoice payable, long now)
    {
        if (payable.Received > 0 || payable.State is "unconfirmed" or "paid")
        {
            return MakeResult.Refused("Error.Confirming");
        }
        var end = payable.ExpiresAt + SattlemeDefaults.GraceSeconds;
        return now < end
            ? MakeResult.Refused("Error.Pending", null, DateTimeOffset.FromUnixTimeSeconds(end).UtcDateTime)
            : MakeResult.Refused("Error.Checking");
    }

    /// <summary>Why the invoice <paramref name="json"/> must not be shown: it is not of the price asked.</summary>
    private static MismatchException? Mismatch(string json, PriceToPay price)
    {
        var made = StoredInvoice.Of(new SattlemeInvoice { InvoiceJson = json });
        var same = made is { FiatAmount: { } amount, FiatCurrency: { } currency }
            && currency == price.Currency
            && decimal.TryParse(amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var asked)
            && asked == decimal.Parse(price.Amount, CultureInfo.InvariantCulture);
        return same ? null : new MismatchException($"an invoice of {made?.FiatAmount} {made?.FiatCurrency}, for a price of {price.Amount} {price.Currency}");
    }

    /// <summary>Logs why no invoice was made (never the key: the SDK's messages hold none), notes it on the order, then tells the customer.</summary>
    private async Task<MakeResult> RefuseAsync(SattlemeInvoice attempt, ShopOrder order, Exception failure)
    {
        var code = PaymentRules.ErrorCode(failure);
        await _store.SetMakeErrorAsync(attempt.Id, code);
        var transient = failure is SattlemeException { Retryable: true } || code == "timeout";
        var ofOrder = failure is InvalidOrderException or UnsupportedCurrencyException;
        if (transient || ofOrder)
        {
            _logger.LogWarning("sattle.me: no Bitcoin invoice for the reference {Reference}: {Code}: {Message}", attempt.Reference, code, failure.Message);
        }
        else if (code == "error")
        {
            _logger.LogError(failure, "sattle.me: no Bitcoin invoice for the reference {Reference}: {Code}: {Message}", attempt.Reference, code, failure.Message);
        }
        else
        {
            _logger.LogError("sattle.me: no Bitcoin invoice for the reference {Reference}: {Code}: {Message}", attempt.Reference, code, failure.Message);
        }
        await _notes.AddAsync(order.Id, "Note.Failed", attempt.Reference, new NoteResource("ErrorCode." + code));
        return MakeResult.Refused(transient ? "Error.TryAgain" : ofOrder ? "Error.NotPayable" : "Error.Unavailable", code);
    }

    /// <summary>
    /// What the merchant's app shows of the sale: the store's name and the order's number, 100
    /// characters at most, no control character (the SDK refuses them).
    /// </summary>
    private string NoteOf(int storeId, ShopOrder order)
    {
        var storeName = _storeContext.GetStoreById(storeId)?.Name ?? "";
        var number = Clean(order.GetOrderNumber());
        if (number.Length > 40)
        {
            number = number[..40];
        }
        var name = Clean(storeName).EnumerateRunes().ToList();
        // In code points, as the SDK counts them: the name, " · " and the number.
        var room = 100 - 3 - number.EnumerateRunes().Count();
        if (name.Count > room)
        {
            name = [.. name.Take(room - 1), new Rune('…')];
        }
        var cleaned = string.Concat(name.Select(rune => rune.ToString())).Trim();
        return cleaned.Length == 0 ? number : number.Length == 0 ? cleaned : $"{cleaned} · {number}";

        static string Clean(string text)
        {
            var runes = (text ?? "").EnumerateRunes()
                .Select(rune => Rune.IsControl(rune) || Rune.IsWhiteSpace(rune) ? new Rune(' ') : rune);
            return string.Join(' ', string.Concat(runes.Select(rune => rune.ToString())).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
