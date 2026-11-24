namespace Sattleme.Bitcoin.Models;

/// <summary>
/// The payment page of an order: what it shows of its invoice, as text (the view encodes it), and its
/// QR code, an SVG the module draws from numbers alone.
/// </summary>
public class PaymentPageModel
{
    /// <summary>Where the payment is (PaymentPhase, in lower case).</summary>
    public string Phase { get; set; } = "";

    /// <summary>The order's number, as the shop shows it.</summary>
    public string OrderNumber { get; set; } = "";

    /// <summary>The order's page in the shop.</summary>
    public string OrderUrl { get; set; } = "";

    /// <summary>What the page's script asks for news of the payment.</summary>
    public string StatusUrl { get; set; } = "";

    /// <summary>The page itself, its check asked (for a browser without JavaScript).</summary>
    public string CheckUrl { get; set; } = "";

    /// <summary>Where the form asking for a new invoice posts.</summary>
    public string NewInvoiceUrl { get; set; } = "";

    /// <summary>Whether a new invoice can be asked for (none was made, or the last one ended unpaid, the order is still to pay).</summary>
    public bool CanMakeNew { get; set; }

    /// <summary>Why no invoice was made, when the last try failed (in plain words).</summary>
    public string? Problem { get; set; }

    /// <summary>The payment URI to pay (the invoice's, or its rest's): one QR code, and the wallet's link.</summary>
    public string? Uri { get; set; }

    /// <summary>Its QR code, an inline SVG; null when there is none to show.</summary>
    public string? QrSvg { get; set; }

    /// <summary>The Lightning invoice to copy.</summary>
    public string? Lightning { get; set; }

    /// <summary>The address to copy, when it may be paid on-chain.</summary>
    public string? Address { get; set; }

    /// <summary>Whether the invoice has a deposit address (its payment may take 30 minutes to be confirmed).</summary>
    public bool HasOnchain { get; set; }

    /// <summary>What is to pay now (the amount, or the rest of an underpayment), in BTC and in sats.</summary>
    public string ToPayBtc { get; set; } = "";

    /// <inheritdoc cref="ToPayBtc"/>
    public string ToPaySats { get; set; } = "";

    /// <summary>The invoice's amount in sats.</summary>
    public string Sats { get; set; } = "";

    /// <summary>The invoice's price in the store's currency, as the shop writes prices.</summary>
    public string? Price { get; set; }

    /// <summary>What came, in sats.</summary>
    public string ReceivedSats { get; set; } = "";

    /// <summary>Whether something came.</summary>
    public bool HasReceived { get; set; }

    /// <summary>Seconds left to pay, and the same as m:ss.</summary>
    public long SecondsLeft { get; set; }

    /// <inheritdoc cref="SecondsLeft"/>
    public string TimeLeft { get; set; } = "";

    /// <summary>When the invoice ends, in the customer's time zone.</summary>
    public string? Until { get; set; }

    /// <summary>The confirmations of an on-chain payment being confirmed.</summary>
    public int Confirmations { get; set; }

    /// <summary>Every how many seconds the page's script asks for news; null when it waits for none.</summary>
    public int? PollSeconds { get; set; }

    /// <summary>What the page shows, in a few characters: the page reloads when it changes.</summary>
    public string Version { get; set; } = "";
}
