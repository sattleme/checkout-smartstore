using Microsoft.AspNetCore.Mvc;
using Sattleme.Bitcoin.Services;
using Smartstore.Core.Data;
using Smartstore.Web.Components;

namespace Sattleme.Bitcoin.Components;

/// <summary>
/// What the customer's order page says of its Bitcoin payment, and the link to its payment page
/// (Smartstore's own "Complete payment" button leads there too, while re-posting payments is allowed).
/// </summary>
public class OrderPaymentViewComponent : SmartViewComponent
{
    private readonly SmartDbContext _db;
    private readonly PaymentPages _pages;
    private readonly SattlemeStore _store;

    /// <summary>The component.</summary>
    public OrderPaymentViewComponent(SmartDbContext db, PaymentPages pages, SattlemeStore store)
    {
        _db = db;
        _pages = pages;
        _store = store;
    }

    /// <summary>The block of the order <paramref name="orderId"/>, when it is paid with Bitcoin; nothing otherwise.</summary>
    public async Task<IViewComponentResult> InvokeAsync(int orderId)
    {
        var order = await _db.Orders.FindByIdAsync(orderId, false);
        var record = order is null || order.PaymentMethodSystemName != SattlemeDefaults.SystemName ? null : await _store.GetOrderAsync(order.Id);
        if (order is null || record is null)
        {
            return Empty();
        }
        var invoices = await _store.GetInvoicesAsync(order.Id);
        return View(_pages.PrepareOrderPayment(record, order, invoices.Count == 0 ? null : invoices[^1]));
    }
}
