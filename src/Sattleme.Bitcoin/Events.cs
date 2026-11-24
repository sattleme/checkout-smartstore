using Microsoft.EntityFrameworkCore;
using Sattleme.Bitcoin.Services;
using Smartstore.Core.Data;
using Smartstore.Core.Identity;
using Smartstore.Core.Localization;
using Smartstore.Events;
using Smartstore.Web.Modelling;
using Smartstore.Web.Rendering.Events;

namespace Sattleme.Bitcoin;

/// <summary>
/// What the module does when Smartstore publishes these events:
/// <list type="bullet">
/// <item>the admin's order page is drawn (its tab strip order-edit): a tab "Bitcoin (sattle.me)"
/// shows the order's invoices;</item>
/// <item>a customer's personal data is exported (Admin → Customers → Export personal data:
/// IGdprTool.ExportCustomerAsync): it gets the customer's Bitcoin payments;</item>
/// <item>a customer is anonymized (IGdprTool.AnonymizeCustomerAsync): their invoices lose what
/// leads to their coins.</item>
/// </list>
/// </summary>
public class Events : IConsumer
{
    private readonly SmartDbContext _db;
    private readonly PersonalDataExport _export;
    private readonly ILocalizationService _localizationService;
    private readonly PersonalDataShredder _shredder;

    /// <summary>The consumers.</summary>
    public Events(SmartDbContext db, PersonalDataExport export, ILocalizationService localizationService, PersonalDataShredder shredder)
    {
        _db = db;
        _export = export;
        _localizationService = localizationService;
        _shredder = shredder;
    }

    /// <summary>Adds the tab of the order's Bitcoin payment to the admin's page of an order paid with Bitcoin.</summary>
    public async Task HandleEventAsync(TabStripCreated message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.TabStripName != "order-edit" || message.Model is not TabbableModel model
            || !await _db.Orders.AsNoTracking().AnyAsync(order => order.Id == model.Id && order.PaymentMethodSystemName == SattlemeDefaults.SystemName))
        {
            return;
        }
        var title = _localizationService.GetResource(SattlemeDefaults.ResourcePrefix + "Admin.Tab");
        await message.TabFactory.AppendAsync(builder => builder
            .Text(title)
            .Name(SattlemeDefaults.AdminTabName)
            .LinkHtmlAttributes(new { data_tab_name = "Sattleme" })
            .Action("OrderPayment", "SattlemeAdmin", new { area = "Admin", orderId = model.Id })
            .Ajax());
    }

    /// <summary>Adds the customer's Bitcoin payments to the export of their personal data.</summary>
    public async Task HandleEventAsync(GdprCustomerDataExportedEvent message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payments = await _export.ExportAsync(message.Customer.Id);
        if (payments.Count > 0)
        {
            message.Result["SattlemeBitcoinPayments"] = payments;
        }
    }

    /// <summary>The customer was anonymized: their invoices lose what leads to their coins.</summary>
    public Task HandleEventAsync(CustomerAnonymizedEvent message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _shredder.ShredCustomerAsync(message.Customer.Id);
    }
}
