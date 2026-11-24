using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Smartstore.Domain;

namespace Sattleme.Bitcoin.Domain;

/// <summary>
/// One try to have an order paid: a random reference, stored before the SDK is asked for an invoice
/// of it, then the invoice as the SDK made it (its JSON, SPEC.md §3), then what its checks found.
/// An order has one invoice that may be paid at a time: another one only once the last ended unpaid.
/// </summary>
[Table("SattlemeInvoice")]
[Index(nameof(OrderId), Name = "IX_SattlemeInvoice_OrderId")]
[Index(nameof(Reference), Name = "IX_SattlemeInvoice_Reference", IsUnique = true)]
[Index(nameof(Final), nameof(NextCheckOnUtc), Name = "IX_SattlemeInvoice_Final_NextCheckOnUtc")]
public class SattlemeInvoice : BaseEntity
{
    /// <summary>The order's identifier (Smartstore's Order.Id).</summary>
    public int OrderId { get; set; }

    /// <summary>
    /// The order reference the invoice is made for: <c>ss-</c> and 128 random bits in hex, never used
    /// for another try. A try that follows a failed one (a timeout) asks for the same one.
    /// </summary>
    [Required, StringLength(64)]
    public string Reference { get; set; } = "";

    /// <summary>The price the invoice is asked for, as the SDK takes it: <c>24.50</c>.</summary>
    [Required, StringLength(40)]
    public string PriceAmount { get; set; } = "";

    /// <summary>Its currency, the store's primary one (ISO 4217).</summary>
    [Required, StringLength(3)]
    public string PriceCurrency { get; set; } = "";

    /// <summary>The invoice as the SDK gave it (Invoice or DeclaredInvoice JSON); null until made.</summary>
    [MaxLength]
    public string? InvoiceJson { get; set; }

    /// <summary>When the invoice can no longer be paid (Unix seconds); 0 until made.</summary>
    public long ExpiresAt { get; set; }

    /// <summary>What the last check found (PaymentStatus JSON), null before the first one.</summary>
    [MaxLength]
    public string? StatusJson { get; set; }

    /// <summary>The state of the last status: open, unconfirmed, paid, expired; null before.</summary>
    [StringLength(20)]
    public string? State { get; set; }

    /// <summary>What came in time, in sats, as the last status says (on-chain and by invoices of the rest).</summary>
    public long Received { get; set; }

    /// <summary>Paid or expired, and the order updated to match (or given up): never checked again.</summary>
    public bool Final { get; set; }

    /// <summary>When a check took the order's completion (marking it paid), if one did.</summary>
    public DateTime? CompletionClaimedOnUtc { get; set; }

    /// <summary>When the record was made.</summary>
    public DateTime CreatedOnUtc { get; set; }

    /// <summary>When a check last ended, successfully or not.</summary>
    public DateTime? CheckedOnUtc { get; set; }

    /// <summary>When the background checks look at it next; null when not made or final.</summary>
    public DateTime? NextCheckOnUtc { get; set; }

    /// <summary>The check under way holds the record until then, so that two never run at once.</summary>
    [StringLength(64)]
    public string? LeaseId { get; set; }

    /// <inheritdoc cref="LeaseId"/>
    public DateTime? LeaseUntilUtc { get; set; }

    /// <summary>The code of the error of the last try to make it, or of its last check; empty when none.</summary>
    [Required(AllowEmptyStrings = true), StringLength(40)]
    public string Error { get; set; } = "";

    /// <summary>How many checks in a row failed.</summary>
    public int Errors { get; set; }

    /// <summary>
    /// The customer's personal data was removed: what leads to their coins is gone, or goes once the
    /// invoice is final (its checks need it until then).
    /// </summary>
    public bool Shredded { get; set; }
}
