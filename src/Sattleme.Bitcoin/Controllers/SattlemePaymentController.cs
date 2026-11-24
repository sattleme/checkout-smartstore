using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Sattleme.Bitcoin.Domain;
using Sattleme.Bitcoin.Services;
using Smartstore.Core.Checkout.Orders;
using Smartstore.Core.Data;
using Smartstore.Web.Controllers;

namespace Sattleme.Bitcoin.Controllers;

/// <summary>
/// The payment page of an order, in the shop's design and language, and what its script asks
/// (README.md, "How it works"). Each request is authorized by the order's GUID and the secret of its
/// payment page (32 random bytes, compared in constant time): never by the order's number alone.
/// Nothing the browser sends decides an amount, a currency, a reference or a status: the page shows
/// what the server's own checks found. The pages are never cached nor indexed, and send no referrer
/// (their address holds the secret). The shop's own checks of its public pages apply (a closed
/// store, access to the shop), as Smartstore's PublicController applies them.
/// </summary>
public class SattlemePaymentController : PublicController
{
    private readonly SmartDbContext _db;
    private readonly CustomerMessages _messages;
    private readonly InvoiceMaker _maker;
    private readonly PaymentChecker _checker;
    private readonly PaymentPages _pages;
    private readonly SattlemeStore _store;

    /// <summary>The controller.</summary>
    public SattlemePaymentController(SmartDbContext db,
        CustomerMessages messages,
        InvoiceMaker maker,
        PaymentChecker checker,
        PaymentPages pages,
        SattlemeStore store)
    {
        _db = db;
        _messages = messages;
        _maker = maker;
        _checker = checker;
        _pages = pages;
        _store = store;
    }

    /// <summary>
    /// The page: the invoice's QR code while it can be paid, then what became of the payment. It runs
    /// no check itself, but for a browser without JavaScript that asks for one (<c>check</c>), at most
    /// every few seconds per invoice, then shows the page again.
    /// </summary>
    [HttpGet("sattleme/pay/{orderGuid:guid}")]
    public async Task<IActionResult> Pay(Guid orderGuid, string? token, string? check)
    {
        Private();
        if (await FindAsync(orderGuid, token) is not var (record, order))
        {
            return NotFound();
        }
        var invoice = Last(await _store.GetInvoicesAsync(order.Id));
        if (check is not null)
        {
            if (invoice is { Final: false, InvoiceJson: not null })
            {
                await _checker.CheckAsync(invoice.Id, SattlemeDefaults.PageInterval, SattlemeDefaults.CheckTimeout);
            }
            return Redirect(_pages.PagePath(nameof(Pay), record));
        }
        return View(_pages.PreparePaymentPage(record, order, invoice));
    }

    /// <summary>A new invoice, once the last one ended unpaid (a form, with its antiforgery token).</summary>
    [HttpPost("sattleme/invoice/{orderGuid:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> NewInvoice(Guid orderGuid, string? token)
    {
        Private();
        if (await FindAsync(orderGuid, token) is not var (record, order))
        {
            return NotFound();
        }
        var made = await _maker.MakeAsync(order);
        if (made.Invoice is null)
        {
            NotifyError(_messages.Refusal(made));
        }
        return Redirect(_pages.PagePath(nameof(Pay), record));
    }

    /// <summary>
    /// Asked by the page's script every few seconds: has the invoice checked (the server's own check,
    /// at most every few seconds per invoice, whoever asks), and says where its payment is and the
    /// page's version (the page reloads when it changed).
    /// </summary>
    [HttpGet("sattleme/status/{orderGuid:guid}")]
    public async Task<IActionResult> Status(Guid orderGuid, string? token)
    {
        Private();
        if (await FindAsync(orderGuid, token) is not var (_, order))
        {
            return NotFound();
        }
        var invoice = Last(await _store.GetInvoicesAsync(order.Id));
        if (invoice is { Final: false, InvoiceJson: not null })
        {
            await _checker.CheckAsync(invoice.Id, SattlemeDefaults.PageInterval, SattlemeDefaults.CheckTimeout);
            invoice = await _store.GetInvoiceAsync(invoice.Id);
            await _db.Entry(order).ReloadAsync();
        }
        var (phase, version) = _pages.PrepareStatus(order, invoice);
        return Json(new { phase, version });
    }

    /// <summary>
    /// The order <paramref name="orderGuid"/> and its record, when <paramref name="token"/> is the
    /// secret of its payment page (compared in constant time) and the order is one of this payment
    /// method; else null.
    /// </summary>
    private async Task<(SattlemeOrder Record, Order Order)?> FindAsync(Guid orderGuid, string? token)
    {
        var record = await _store.GetOrderByGuidAsync(orderGuid);
        if (record is null || token is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(record.PageToken), Encoding.UTF8.GetBytes(token)))
        {
            return null;
        }
        var order = await _db.Orders.FindByIdAsync(record.OrderId);
        if (order is null || order.Deleted || order.OrderGuid != orderGuid || order.PaymentMethodSystemName != SattlemeDefaults.SystemName)
        {
            return null;
        }
        return (record, order);
    }

    private static SattlemeInvoice? Last(List<SattlemeInvoice> invoices)
    {
        return invoices.Count == 0 ? null : invoices[^1];
    }

    /// <summary>Never cached, never indexed, no referrer sent.</summary>
    private void Private()
    {
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
