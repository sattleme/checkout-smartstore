using System;

namespace Sattleme.Checkout;

// The errors of the package (SPEC.md §5). Every one says what failed, never with a secret: neither
// the integration's key, nor a sale's key, nor any key derived from them is ever put in a message,
// in Exception.Data or in an inner exception. Every exception the package throws is one of these,
// but for a cancelled call: it throws the platform's OperationCanceledException.

/// <summary>
/// What every error of this package is: a stable <see cref="Code"/> for programs, and whether the
/// same call may succeed later, unchanged (<see cref="Retryable"/>).
/// </summary>
public class SattlemeException : Exception
{
    /// <summary>An error of <paramref name="code"/>.</summary>
    public SattlemeException(string code, string message, bool retryable = false, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Retryable = retryable;
    }

    /// <summary>A stable code: <c>invalid_key</c>, <c>network</c>…</summary>
    public string Code { get; }

    /// <summary>Whether the same call may succeed later, unchanged (a relay or a service down).</summary>
    public bool Retryable { get; }
}

/// <summary>Not an integration's secret key (<c>sattleme_ck_…</c>), or a damaged one. Never holds the key.</summary>
public sealed class InvalidKeyException : SattlemeException
{
    /// <summary>An invalid key.</summary>
    public InvalidKeyException(string message = "Not a sattle.me integration key")
        : base("invalid_key", message)
    {
    }
}

/// <summary>An option of the client or of a call is invalid, found before any request: <see cref="Param"/> names it.</summary>
public sealed class InvalidOptionException : SattlemeException
{
    /// <summary>The option <paramref name="param"/> is invalid.</summary>
    public InvalidOptionException(string param, string message)
        : base("invalid_option", $"{param}: {message}")
    {
        Param = param;
    }

    /// <summary>The option: <c>relayTimeoutMs</c>, <c>rateSource</c>, <c>intervalMs</c>…</summary>
    public string Param { get; }
}

/// <summary>A parameter of the order, or a stored invoice, is invalid: <see cref="Param"/> names it.</summary>
public sealed class InvalidOrderException : SattlemeException
{
    /// <summary>The parameter <paramref name="param"/> is invalid.</summary>
    public InvalidOrderException(string param, string message)
        : base("invalid_order", $"{param}: {message}")
    {
        Param = param;
    }

    /// <summary>The parameter: <c>ref</c>, <c>sats</c>, <c>fiat</c>, <c>rateSource</c>, <c>durationSeconds</c>, <c>note</c>, <c>checkout</c>, <c>redirectUrl</c>, <c>invoice</c>.</summary>
    public string Param { get; }
}

/// <summary>The merchant removed the integration: no invoice can be made with this key any more.</summary>
public sealed class RevokedException : SattlemeException
{
    /// <summary>The integration was removed.</summary>
    public RevokedException()
        : base("revoked", "The merchant removed this integration")
    {
    }
}

/// <summary>
/// A majority of the relays answered without the integration's channel, or with one of a newer
/// format than this version reads (<see cref="NewerFormat"/>: update the package).
/// </summary>
public sealed class ChannelNotFoundException : SattlemeException
{
    /// <summary>No channel, or only one of a newer format.</summary>
    public ChannelNotFoundException(bool newerFormat)
        : base(
            "channel_not_found",
            newerFormat
                ? "The integration's channel is of a newer format: update Sattleme.Checkout"
                : "The integration's channel was not found on its relays",
            retryable: !newerFormat)
    {
        NewerFormat = newerFormat;
    }

    /// <summary>Whether only a channel of a newer format was found.</summary>
    public bool NewerFormat { get; }
}

/// <summary>
/// A relay, the Lightning address's server, a rate source or an explorer did not answer usably (in
/// time), or fewer than a majority of the relays answered or took a record. Retry later.
/// </summary>
public sealed class NetworkException : SattlemeException
{
    /// <summary>No usable answer.</summary>
    public NetworkException(string message, Exception? innerException = null)
        : base("network", message, retryable: true, innerException)
    {
    }
}

/// <summary>
/// An invoice, a pay request or a payment answer failed the checks: another network, amount,
/// merchant, duration or date, a verify answer about another invoice, a preimage that does not
/// match. Never show such an invoice.
/// </summary>
public sealed class InvalidInvoiceException : SattlemeException
{
    /// <summary>The checks failed.</summary>
    public InvalidInvoiceException(string message, Exception? innerException = null)
        : this(message, null, innerException)
    {
    }

    internal InvalidInvoiceException(string message, string? check, Exception? innerException = null)
        : base("invalid_invoice", message, retryable: false, innerException)
    {
        Check = check;
    }

    /// <summary>The check of SPEC.md §18 that failed (invoice-checks.json names them), when it is one.</summary>
    internal string? Check { get; }
}

/// <summary>
/// The order still has an invoice that may be paid (not expired, or in the 120 s after its end, or
/// its on-chain payment being confirmed): show that one (<see cref="Invoice"/>) instead of making
/// another. One order, one invoice at a time.
/// </summary>
public sealed class InvoicePendingException : SattlemeException
{
    /// <summary>The pending invoice, with a new checkout link to it when the order asked for one.</summary>
    public InvoicePendingException(DeclaredInvoice invoice, string? checkoutUrl)
        : base("invoice_pending", "This order still has an invoice that may be paid")
    {
        Invoice = invoice;
        CheckoutUrl = checkoutUrl;
    }

    /// <summary>The invoice that may still be paid, as the merchant's registry holds it.</summary>
    public DeclaredInvoice Invoice { get; }

    /// <summary>
    /// With <c>Checkout</c>, a new checkout link to that invoice while it may still be paid; else
    /// null (its time to be paid is over: the shop keeps the link it got).
    /// </summary>
    public string? CheckoutUrl { get; }
}

/// <summary>
/// The order was already paid, with <see cref="Invoice"/>: never charge it again.
/// <see cref="Status"/> is its <c>paid</c> status, <see cref="Preimage"/> the proof of a Lightning
/// payment of its own invoice (null on-chain or mixed).
/// </summary>
public sealed class OrderPaidException : SattlemeException
{
    /// <summary>The order was paid with <paramref name="invoice"/>.</summary>
    public OrderPaidException(DeclaredInvoice invoice, PaymentStatus.Paid status)
        : base("order_paid", "This order was already paid")
    {
        ArgumentNullException.ThrowIfNull(status);
        Invoice = invoice;
        Status = status;
        Preimage = status.Method == PaymentMethod.Lightning ? status.Preimage : null;
    }

    /// <summary>The invoice that was paid.</summary>
    public DeclaredInvoice Invoice { get; }

    /// <summary>Its paid status.</summary>
    public PaymentStatus.Paid Status { get; }

    /// <summary>The preimage of its Lightning payment (hex), null when paid on-chain or mixed.</summary>
    public string? Preimage { get; }
}

/// <summary>None of the rate sources has a price in the currency.</summary>
public sealed class UnsupportedCurrencyException : SattlemeException
{
    /// <summary><paramref name="source"/> has no price in <paramref name="currency"/>.</summary>
    public UnsupportedCurrencyException(string currency, string source)
        : this(currency, source, $"{source} has no price in {currency}")
    {
    }

    /// <summary>No price in <paramref name="currency"/>, with a message of your own.</summary>
    public UnsupportedCurrencyException(string currency, string source, string message)
        : base("unsupported_currency", message)
    {
        _ = source;
        Currency = currency;
    }

    /// <summary>The currency (ISO 4217, upper case).</summary>
    public string Currency { get; }
}

/// <summary>
/// No usable price: a source answered something that is not a price, a price too old or too far
/// ahead, or a custom source failed (<see cref="Exception.InnerException"/>). Retry later, or with
/// another source.
/// </summary>
public sealed class RateException : SattlemeException
{
    /// <summary>No usable price.</summary>
    public RateException(string message, Exception? innerException = null)
        : base("rate", message, retryable: true, innerException)
    {
    }
}

/// <summary>
/// A checkout link was asked, and the merchant's app gives no web app for it (its channel names
/// none). Make the invoice without <c>Checkout</c>, and show it on the shop's page.
/// </summary>
public sealed class CheckoutUnavailableException : SattlemeException
{
    /// <summary>No web app for checkout links.</summary>
    public CheckoutUnavailableException()
        : base(
            "checkout_unavailable",
            "The merchant's app gives no web app for checkout links: show the invoice on the shop's page")
    {
    }
}

/// <summary>
/// The order can only be paid on-chain (its amount is outside the Lightning address's bounds), and
/// no deposit address of the integration's lot could be leased. Retry later: the merchant's app
/// fills the lot again whenever it is opened.
/// </summary>
public sealed class OnchainUnavailableException : SattlemeException
{
    /// <summary>No address could be leased.</summary>
    public OnchainUnavailableException()
        : base(
            "onchain_unavailable",
            "No deposit address of the integration's lot could be leased: try again later",
            retryable: true)
    {
    }
}
