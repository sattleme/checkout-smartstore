using Microsoft.EntityFrameworkCore;
using Smartstore.Core.Data;

namespace Sattleme.Bitcoin.Services;

/// <summary>
/// A customer's Bitcoin payments, for the export of their personal data (Smartstore's GDPR export,
/// GdprCustomerDataExportedEvent): for each invoice of their orders, what the module keeps of it,
/// the deposit address and the transactions included (they lead to the customer's coins: they are
/// the customer's data).
/// </summary>
public class PersonalDataExport
{
    private readonly SmartDbContext _db;
    private readonly SattlemeStore _store;

    /// <summary>The export.</summary>
    public PersonalDataExport(SmartDbContext db, SattlemeStore store)
    {
        _db = db;
        _store = store;
    }

    /// <summary>The Bitcoin payments of the customer <paramref name="customerId"/>, one entry per invoice.</summary>
    public virtual async Task<List<Dictionary<string, object?>>> ExportAsync(int customerId)
    {
        var payments = new List<Dictionary<string, object?>>();
        foreach (var record in await _store.GetOrdersOfCustomerAsync(customerId))
        {
            var number = await _db.Orders.AsNoTracking().IgnoreQueryFilters()
                .Where(order => order.Id == record.OrderId)
                .Select(order => order.OrderNumber)
                .FirstOrDefaultAsync();
            foreach (var invoice in await _store.GetInvoicesAsync(record.OrderId))
            {
                var stored = StoredInvoice.Of(invoice);
                payments.Add(new Dictionary<string, object?>
                {
                    ["OrderNumber"] = string.IsNullOrEmpty(number) ? record.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture) : number,
                    ["Reference"] = invoice.Reference,
                    ["CreatedOnUtc"] = invoice.CreatedOnUtc,
                    ["Price"] = $"{invoice.PriceAmount} {invoice.PriceCurrency}",
                    ["State"] = invoice.State,
                    ["Sats"] = stored?.Sats,
                    ["Received"] = invoice.Received,
                    ["OnchainAddress"] = stored?.OnchainAddress,
                    ["Transactions"] = stored?.Txids.ToArray() ?? [],
                    ["LightningInvoice"] = stored?.Bolt11,
                    ["ProofsOfPayment"] = stored?.Preimages.ToArray() ?? [],
                });
            }
        }
        return payments;
    }
}
