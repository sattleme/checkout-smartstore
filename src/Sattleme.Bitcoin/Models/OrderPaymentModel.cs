namespace Sattleme.Bitcoin.Models;

/// <summary>What the customer's order page says of its Bitcoin payment.</summary>
public class OrderPaymentModel
{
    /// <summary>Where the payment is (PaymentPhase, in lower case).</summary>
    public string Phase { get; set; } = "";

    /// <summary>
    /// What the page says of it: the module's resource (Order.Phase.…, the phase's, or
    /// expiredUnderpaid once an invoice ended underpaid) and its arguments.
    /// </summary>
    public string Text { get; set; } = "";

    /// <inheritdoc cref="Text"/>
    public object[] TextArguments { get; set; } = [];

    /// <summary>Whether the page leads to the payment page: only where a payment can be made (PaymentRules.OffersPayment).</summary>
    public bool OffersPayment { get; set; }

    /// <summary>The order's payment page.</summary>
    public string PayUrl { get; set; } = "";

    /// <summary>When the invoice ends, in the customer's time zone; null without one.</summary>
    public string? Until { get; set; }
}
