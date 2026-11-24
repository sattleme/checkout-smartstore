using Microsoft.EntityFrameworkCore;
using Smartstore.Core.Data;

namespace Sattleme.Bitcoin.Domain;

/// <summary>The module's tables in Smartstore's database context (Startup adds its model assembly).</summary>
public static class SattlemeDbContextExtensions
{
    /// <summary>The orders paid with Bitcoin.</summary>
    public static DbSet<SattlemeOrder> SattlemeOrders(this SmartDbContext db)
        => db.Set<SattlemeOrder>();

    /// <summary>The invoices of those orders.</summary>
    public static DbSet<SattlemeInvoice> SattlemeInvoices(this SmartDbContext db)
        => db.Set<SattlemeInvoice>();
}
