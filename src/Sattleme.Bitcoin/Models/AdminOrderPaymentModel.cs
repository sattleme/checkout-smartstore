namespace Sattleme.Bitcoin.Models;

/// <summary>What the admin's order page shows of the order's Bitcoin payment: its invoices, the latest first.</summary>
public class AdminOrderPaymentModel
{
    /// <summary>The invoices.</summary>
    public List<AdminInvoiceModel> Invoices { get; } = [];

    /// <summary>What cancelling the order now means for a payment that may still come; null when none may.</summary>
    public string? Warning { get; set; }
}

/// <summary>One invoice, as the admin's order page shows it.</summary>
public class AdminInvoiceModel
{
    /// <summary>Its reference (the order's sale in the merchant's app).</summary>
    public string Reference { get; set; } = "";

    /// <summary>Its state, in plain words.</summary>
    public string State { get; set; } = "";

    /// <summary>Its amount: sats, BTC, and the price it was asked for.</summary>
    public string Amount { get; set; } = "";

    /// <summary>The rate it was converted at, its source and time.</summary>
    public string? Rate { get; set; }

    /// <summary>What the merchant's app shows of the sale.</summary>
    public string? Note { get; set; }

    /// <summary>When it was made, ends, was last checked and is checked next.</summary>
    public string Created { get; set; } = "";

    /// <inheritdoc cref="Created"/>
    public string? Expires { get; set; }

    /// <inheritdoc cref="Created"/>
    public string? LastCheck { get; set; }

    /// <inheritdoc cref="Created"/>
    public string? NextCheck { get; set; }

    /// <summary>What came, in sats.</summary>
    public string? Received { get; set; }

    /// <summary>The confirmations of its on-chain payment.</summary>
    public int? Confirmations { get; set; }

    /// <summary>Its deposit address, and whether it is linked to mempool.space (a plain address).</summary>
    public string? Address { get; set; }

    /// <inheritdoc cref="Address"/>
    public bool AddressLink { get; set; }

    /// <summary>The transactions that paid it (64 hex digits each: linked to mempool.space).</summary>
    public List<string> Txids { get; set; } = [];

    /// <summary>
    /// Whether its transactions are those of its deposit address, on the address's page: something
    /// came on-chain (an underpayment), and its status lists none.
    /// </summary>
    public bool TransactionsAtAddress { get; set; }

    /// <summary>The proofs of its Lightning payments: each preimage, with the payment hash it proves.</summary>
    public List<AdminProofModel> Proofs { get; set; } = [];

    /// <summary>Its last error, in plain words.</summary>
    public string? Error { get; set; }

    /// <summary>Whether the customer's personal data was removed (the address and transactions with it).</summary>
    public bool Shredded { get; set; }
}

/// <summary>A proof of a Lightning payment: the preimage, and the payment hash it proves (its SHA-256).</summary>
public class AdminProofModel
{
    /// <summary>The preimage, in hex.</summary>
    public string Preimage { get; set; } = "";

    /// <summary>Its SHA-256, in hex: the payment hash of the Lightning invoice it paid; null when it is not 32 bytes of hex.</summary>
    public string? PaymentHash { get; set; }
}
