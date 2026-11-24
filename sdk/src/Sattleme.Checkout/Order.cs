using System.Collections.Generic;

namespace Sattleme.Checkout;

/// <summary>
/// An order (SPEC.md §17, "The order"): its reference, and its amount in sats or in a currency. Every
/// parameter is checked before any request; a refusal is <see cref="InvalidOrderException"/> naming
/// the parameter, never repaired.
/// </summary>
public sealed class Order
{
    /// <summary>
    /// The order's reference, 1 to 64 characters <c>A–Z a–z 0–9 . _ : -</c>. One order, one sale:
    /// never reused for another order by anything that holds the key (a test copy of the shop, a
    /// restored database included), never taken from what the customer sends.
    /// </summary>
    public required string Ref { get; init; }

    /// <summary>The amount in sats, from 1 to 2 100 000 000 000 000 (21 million bitcoins). Exactly one of <see cref="Sats"/> and <see cref="Fiat"/>.</summary>
    public long? Sats { get; init; }

    /// <summary>Or the price in a currency (<c>new Price("24.50", "CHF")</c>), converted at the rate of <see cref="RateSources"/>.</summary>
    public Price? Fiat { get; init; }

    /// <summary>
    /// With <see cref="Fiat"/> only: this order's rate sources, asked in their order (the next one
    /// only when the previous one gives no price); null: the client's.
    /// </summary>
    public IReadOnlyList<RateSource>? RateSources { get; init; }

    /// <summary>Seconds the invoice can be paid: 60 to 3600; null: 900.</summary>
    public int? DurationSeconds { get; init; }

    /// <summary>
    /// What the merchant's app shows, and the customer of a checkout link: 100 characters at most
    /// (code points, once its leading and trailing spaces are removed), no control character; null
    /// or empty: the reference.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>A checkout link: a URL of the merchant's web app where the customer pays the invoice (the Invoice's <c>CheckoutUrl</c>).</summary>
    public bool Checkout { get; init; }

    /// <summary>
    /// With <see cref="Checkout"/> only: where the checkout link sends the customer once the invoice
    /// is paid, an https URL or an app's link (<c>myshop://order/42</c>), 2048 characters at most.
    /// Not a proof of payment.
    /// </summary>
    public string? RedirectUrl { get; init; }

    /// <summary>The order's reference and amount (never anything secret).</summary>
    public override string ToString() => $"Order {{ ref = {Ref} }}";
}
