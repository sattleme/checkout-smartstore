using System.Globalization;
using Sattleme.Bitcoin.Domain;
using Smartstore.Core.Checkout.Orders;
using PaymentState = SmartstoreSattleme.Sattleme.Checkout.PaymentState;
using SattlemeException = SmartstoreSattleme.Sattleme.Checkout.SattlemeException;
using SdkPaymentStatus = SmartstoreSattleme.Sattleme.Checkout.PaymentStatus;
using ShopPaymentStatus = Smartstore.Core.Checkout.Payment.PaymentStatus;

namespace Sattleme.Bitcoin.Services;

/// <summary>Where an order's Bitcoin payment is, as its customer is told (the payment page).</summary>
public enum PaymentPhase
{
    /// <summary>No invoice is made: the last try failed.</summary>
    None,

    /// <summary>Waiting for the payment.</summary>
    Open,

    /// <summary>Less than the amount came on-chain: the rest may still be paid.</summary>
    Underpaid,

    /// <summary>Its time is over, its final status not known yet.</summary>
    Ended,

    /// <summary>Enough came on-chain, being confirmed.</summary>
    Unconfirmed,

    /// <summary>The SDK reported it paid; Smartstore's completion of the order is under way.</summary>
    Arrived,

    /// <summary>
    /// The SDK reported it paid, and the order was not marked paid: the merchant looks at it (its
    /// total changed since the invoice was made, it was cancelled, or Smartstore could not mark it).
    /// </summary>
    Review,

    /// <summary>The order is paid.</summary>
    Paid,

    /// <summary>Never paid, for good (with what came, when something did: underpaid).</summary>
    Expired,

    /// <summary>The order was cancelled.</summary>
    Cancelled,
}

/// <summary>The rules every part of the module applies to an order's invoices.</summary>
public static class PaymentRules
{
    /// <summary>
    /// Whether the invoice may still be paid, or a payment of it be confirmed: until its final
    /// status is known, from a check made after its end and its grace period (expired, nothing
    /// received; or paid). Its time being over is not enough: a payment made in its last seconds,
    /// or an on-chain one dated in time, is found by that check. An order has one such invoice at
    /// a time: it gets a new one only once the last one is known to have ended unpaid.
    /// </summary>
    public static bool MayBePaid(SattlemeInvoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return invoice.InvoiceJson is not null && !invoice.Final;
    }

    /// <summary>
    /// What the order still owes: its total (which Smartstore computes less its gift cards, reward
    /// points and wallet credit: OrderCalculationService.GetShoppingCartTotalAsync), less what was
    /// refunded. In the store's primary currency, as Smartstore keeps an order's amounts.
    /// </summary>
    public static decimal Owed(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return Math.Max(0m, order.OrderTotal - order.RefundedAmount);
    }

    /// <summary>Whether Smartstore counts the order as paid (or paid and refunded since).</summary>
    public static bool IsPaid(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return order.PaymentStatus is ShopPaymentStatus.Paid or ShopPaymentStatus.PartiallyRefunded or ShopPaymentStatus.Refunded;
    }

    /// <summary>What the customer is told of the order's payment, from its last invoice.</summary>
    public static PaymentPhase PhaseOf(Order order, SattlemeInvoice? invoice, long now)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (IsPaid(order))
        {
            return PaymentPhase.Paid;
        }
        var cancelled = order.OrderStatus == OrderStatus.Cancelled || order.Deleted;
        if (invoice?.InvoiceJson is null)
        {
            return cancelled ? PaymentPhase.Cancelled : PaymentPhase.None;
        }
        return invoice.State switch
        {
            "paid" when invoice.Final => PaymentPhase.Review,
            "paid" => PaymentPhase.Arrived,
            "expired" when invoice.Final => PaymentPhase.Expired,
            "unconfirmed" => PaymentPhase.Unconfirmed,
            _ when cancelled => PaymentPhase.Cancelled,
            _ when invoice.Final || now >= invoice.ExpiresAt => PaymentPhase.Ended,
            _ when invoice.Received > 0 => PaymentPhase.Underpaid,
            _ => PaymentPhase.Open,
        };
    }

    /// <summary>
    /// Whether the order's pages lead the customer to pay (the customer's order page's link, and
    /// Smartstore's "Complete payment"): only where a payment can be made, an invoice of the order
    /// that can be paid (its rest, after an on-chain underpayment), or a new one (none made, or the
    /// last ended with nothing received); never while a payment is being confirmed or once one came,
    /// nor after an invoice ended underpaid (no new invoice then), nor while a check after its end
    /// is awaited.
    /// </summary>
    public static bool OffersPayment(Order order, SattlemeInvoice? invoice, long now)
    {
        ArgumentNullException.ThrowIfNull(order);
        return PhaseOf(order, invoice, now) switch
        {
            PaymentPhase.None or PaymentPhase.Open or PaymentPhase.Underpaid => true,
            PaymentPhase.Expired => invoice!.Received == 0,
            _ => false,
        };
    }

    /// <summary>
    /// When the background checks look at the invoice next, after a check that ended at
    /// <paramref name="now"/>: a minute later while it can be paid, just before the end of its grace
    /// period (an on-chain payment a check lists by then came in time, SPEC.md §31) and just after
    /// (its final status); then five minutes later until it is final, or, after failed checks, one
    /// minute doubled at each failure, up to half an hour; never less than a few seconds after the
    /// check. Those waits count from <paramref name="now"/> for a check of the payment page, and from
    /// the start of the scheduled task's run (<paramref name="run"/>) for one of its checks, less
    /// <see cref="SattlemeDefaults.RunSlack"/>: a run checks what is due when it starts, a check ends
    /// some seconds after its run started, and Smartstore's scheduler starts the next run a minute
    /// after the last one started: a wait counted from the check's end would miss the run it was
    /// meant for (every minute would be every two, every five minutes every six).
    /// </summary>
    public static DateTime NextCheck(long expiresAt, int errors, DateTime now, DateTime? run = null)
    {
        var from = run is { } started && started - SattlemeDefaults.RunSlack < now ? started - SattlemeDefaults.RunSlack : now;
        var end = DateTimeOffset.FromUnixTimeSeconds(expiresAt + SattlemeDefaults.GraceSeconds).UtcDateTime;
        DateTime next;
        if (now < end + SattlemeDefaults.EndMargin)
        {
            var mark = now < end - SattlemeDefaults.EndMargin ? end - SattlemeDefaults.EndMargin : end + SattlemeDefaults.EndMargin;
            next = from + SattlemeDefaults.OpenInterval < mark ? from + SattlemeDefaults.OpenInterval : mark;
        }
        else if (errors > 0)
        {
            var backoff = SattlemeDefaults.OpenInterval * Math.Pow(2, Math.Min(errors - 1, 10));
            next = from + (backoff < SattlemeDefaults.MaxBackoff ? backoff : SattlemeDefaults.MaxBackoff);
        }
        else
        {
            next = from + SattlemeDefaults.SettlingInterval;
        }
        return next < now + SattlemeDefaults.PageInterval ? now + SattlemeDefaults.PageInterval : next;
    }

    /// <summary>A short, stable name of an error of a call, for the logs and the record: the SDK's codes (SPEC.md §5), and the module's own.</summary>
    public static string ErrorCode(Exception error)
    {
        return error switch
        {
            SattlemeException sdk => sdk.Code,
            OperationCanceledException or TimeoutException => "timeout",
            MissingKeyException => "no_key",
            MismatchException => "mismatch",
            _ => "error",
        };
    }

    /// <summary>
    /// The price the SDK is asked for: <paramref name="total"/> with exactly <paramref name="digits"/>
    /// decimals, rounded up to the currency's smallest unit (the merchant never receives less); null
    /// when it is not above 0.
    /// </summary>
    public static string? PriceAmount(decimal total, int digits)
    {
        if (digits is < 0 or > 4)
        {
            return null;
        }
        var factor = (decimal)Math.Pow(10, digits);
        var units = decimal.Ceiling(total * factor);
        if (units <= 0)
        {
            return null;
        }
        return (units / factor).ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>Whether the invoice was asked for the price <paramref name="amount"/> in <paramref name="currency"/>.</summary>
    public static bool SamePrice(SattlemeInvoice invoice, string amount, string currency)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return invoice.PriceCurrency == currency
            && decimal.TryParse(invoice.PriceAmount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var asked)
            && decimal.TryParse(amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var other)
            && asked == other;
    }

    /// <summary>The sats a status says came in time (0 when it says none).</summary>
    public static long ReceivedOf(SdkPaymentStatus status)
    {
        return StoredInvoice.SatsOf(status switch
        {
            SdkPaymentStatus.Open open => open.Received,
            SdkPaymentStatus.Unconfirmed unconfirmed => unconfirmed.Received,
            SdkPaymentStatus.Paid paid => paid.Received,
            SdkPaymentStatus.Expired expired => expired.Received,
            _ => null,
        });
    }

    /// <summary>
    /// What came for a paid invoice of <paramref name="sats"/> sats: in Lightning, the invoice's own
    /// amount (a Lightning status has no <c>received</c>); on-chain or mixed, what the status says
    /// came in time.
    /// </summary>
    public static long CameOf(SdkPaymentStatus.Paid paid, long sats)
    {
        ArgumentNullException.ThrowIfNull(paid);
        return paid.Method == SmartstoreSattleme.Sattleme.Checkout.PaymentMethod.Lightning ? sats : ReceivedOf(paid);
    }

    /// <summary>The state of a status, as the SDK's JSON writes it.</summary>
    public static string StateOf(SdkPaymentStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.State switch
        {
            PaymentState.Open => "open",
            PaymentState.Unconfirmed => "unconfirmed",
            PaymentState.Paid => "paid",
            _ => "expired",
        };
    }
}

/// <summary>No integration key is set for the order's store, or the one saved cannot be read.</summary>
public sealed class MissingKeyException : Exception
{
    /// <summary>No key.</summary>
    public MissingKeyException()
        : base("No sattle.me integration key is set for this store")
    {
    }
}

/// <summary>The invoice the SDK gave is not of the price asked: never shown.</summary>
public sealed class MismatchException : Exception
{
    /// <summary>An invoice of another price.</summary>
    public MismatchException(string message)
        : base(message)
    {
    }
}
