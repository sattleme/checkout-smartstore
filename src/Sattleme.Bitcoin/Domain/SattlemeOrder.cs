using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Smartstore.Domain;

namespace Sattleme.Bitcoin.Domain;

/// <summary>
/// An order paid with Bitcoin: the secret of its payment page, and the lock its invoices are made
/// under. Made with the order's first invoice, once Smartstore stored the order (PostProcessPayment).
/// </summary>
[Table("SattlemeOrder")]
[Index(nameof(OrderId), Name = "IX_SattlemeOrder_OrderId", IsUnique = true)]
[Index(nameof(CustomerId), Name = "IX_SattlemeOrder_CustomerId")]
public class SattlemeOrder : BaseEntity
{
    /// <summary>The order's identifier (Smartstore's Order.Id).</summary>
    public int OrderId { get; set; }

    /// <summary>The order's GUID: with the secret below, it opens the payment page.</summary>
    public Guid OrderGuid { get; set; }

    /// <summary>The order's store: its settings (the integration key) check its invoices.</summary>
    public int StoreId { get; set; }

    /// <summary>The order's customer: their personal data is removed with theirs.</summary>
    public int CustomerId { get; set; }

    /// <summary>
    /// The secret of the payment page's link: 32 bytes of the CSPRNG in hex, compared in constant
    /// time. With the order's GUID, it opens the page: never the order's number alone.
    /// </summary>
    [Required, StringLength(64)]
    public string PageToken { get; set; } = "";

    /// <summary>Who holds the order's lock (making an invoice), if anyone.</summary>
    [StringLength(64)]
    public string? LockId { get; set; }

    /// <summary>Until when the lock is held: a holder that stopped never keeps it.</summary>
    public DateTime? LockedUntilUtc { get; set; }

    /// <summary>When the record was made.</summary>
    public DateTime CreatedOnUtc { get; set; }
}
