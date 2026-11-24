using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Sattleme.Bitcoin.Configuration;
using Sattleme.Bitcoin.Models;
using Sattleme.Bitcoin.Services;
using Smartstore.ComponentModel;
using Smartstore.Core.Data;
using Smartstore.Core.Security;
using Smartstore.Engine.Modularity;
using Smartstore.Web.Controllers;
using Smartstore.Web.Modelling.Settings;
using SmartstoreSattleme.Sattleme.Checkout;

namespace Sattleme.Bitcoin.Controllers;

/// <summary>
/// The configuration page (Admin → Configuration → Payment methods → Bitcoin (sattle.me) →
/// Configure), and the order's tab of its Bitcoin payment. Who may read payment methods sees the page,
/// who may update them saves it; every change posts its antiforgery token (Smartstore's
/// AdminController). The integration key is write-only: verified when saved (the SDK refuses what is
/// not an integration key, then reads the integration on its relays: a removed one is refused), then
/// never shown again, never sent back to the browser, never logged.
/// </summary>
public class SattlemeAdminController : AdminController
{
    private readonly SmartDbContext _db;
    private readonly IntegrationKeys _keys;
    private readonly InvoiceMaker _maker;
    private readonly ILogger<SattlemeAdminController> _logger;
    private readonly ISattlemeGateway _gateway;
    private readonly IProviderManager _providerManager;
    private readonly PaymentPages _pages;
    private readonly SattlemeStore _store;

    /// <summary>The controller.</summary>
    public SattlemeAdminController(SmartDbContext db,
        IntegrationKeys keys,
        InvoiceMaker maker,
        ILogger<SattlemeAdminController> logger,
        ISattlemeGateway gateway,
        IProviderManager providerManager,
        PaymentPages pages,
        SattlemeStore store)
    {
        _db = db;
        _keys = keys;
        _maker = maker;
        _logger = logger;
        _gateway = gateway;
        _providerManager = providerManager;
        _pages = pages;
        _store = store;
    }

    /// <summary>The page, with the settings of the store chosen in the admin area (or every store's).</summary>
    [LoadSetting]
    [Permission(Permissions.Configuration.PaymentMethod.Read)]
    public async Task<IActionResult> Configure(SattlemeSettings settings)
    {
        var model = MiniMapper.Map<SattlemeSettings, ConfigurationModel>(settings);
        return await ShowAsync(model);
    }

    /// <summary>Saves the settings (Smartstore's SaveSetting, per store scope): a key entered only once verified.</summary>
    [HttpPost]
    [SaveSetting]
    [Permission(Permissions.Configuration.PaymentMethod.Update)]
    public async Task<IActionResult> Configure(ConfigurationModel model, SattlemeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(settings);

        // The key entered never goes back to the browser, whatever happens below.
        var entered = model.IntegrationKey?.Trim();
        model.IntegrationKey = null;
        ModelState.Remove(nameof(model.IntegrationKey));

        if (model.MinutesToPay is < SattlemeDefaults.MinMinutes or > SattlemeDefaults.MaxMinutes)
        {
            ModelState.AddModelError(nameof(model.MinutesToPay), Resource("MinutesToPay.Range"));
        }
        string? business = null;
        if (!string.IsNullOrEmpty(entered))
        {
            var (verified, error) = await VerifyAsync(entered);
            if (error is not null)
            {
                ModelState.AddModelError(nameof(model.IntegrationKey), error);
            }
            business = verified;
        }
        if (!ModelState.IsValid)
        {
            return await ShowAsync(model);
        }

        settings.MinutesToPay = model.MinutesToPay;
        settings.AdditionalFee = model.AdditionalFee;
        settings.AdditionalFeePercentage = model.AdditionalFeePercentage;

        var storeScope = GetActiveStoreScopeConfiguration();
        if (business is not null)
        {
            await _keys.SaveAsync(entered!, storeScope);
            NotifySuccess(string.Format(System.Globalization.CultureInfo.CurrentCulture, Resource("Key.Verified"), business));
        }
        else if (model.RemoveKey)
        {
            await _keys.RemoveAsync(storeScope);
            NotifySuccess(T("Admin.Common.DataSuccessfullySaved"));
        }
        else
        {
            NotifySuccess(T("Admin.Common.DataSuccessfullySaved"));
        }

        // Shown again by a GET: nothing entered ever goes back to the browser.
        return RedirectToAction(nameof(Configure));
    }

    /// <summary>The order's tab of its Bitcoin payment (its invoices), for who may read orders.</summary>
    [Permission(Permissions.Order.Read)]
    public async Task<IActionResult> OrderPayment(int orderId)
    {
        var order = await _db.Orders.FindByIdAsync(orderId, false);
        if (order is null || order.PaymentMethodSystemName != SattlemeDefaults.SystemName)
        {
            return new EmptyResult();
        }
        var model = _pages.PrepareAdminOrderPayment(await _store.GetInvoicesAsync(order.Id));
        return PartialView(model);
    }

    /// <summary>
    /// Checks a key the admin enters: an integration key (the SDK's construction), of an integration
    /// the merchant still has (its channel read on the relays). Gives the merchant's business name,
    /// or why the key is refused, in plain words.
    /// </summary>
    private async Task<(string? Business, string? Error)> VerifyAsync(string key)
    {
        ISattlemeClient client;
        try
        {
            client = _gateway.ClientOf(key);
        }
        catch (InvalidKeyException)
        {
            return (null, Resource("Key.Invalid"));
        }

        try
        {
            using var timeout = new CancellationTokenSource(SattlemeDefaults.VerifyTimeout);
            var merchant = await client.GetMerchantAsync(timeout.Token);
            return (merchant.BusinessName, null);
        }
        catch (RevokedException)
        {
            return (null, Resource("Key.Revoked"));
        }
        catch (ChannelNotFoundException notFound)
        {
            return (null, Resource(notFound.NewerFormat ? "Key.Newer" : "Key.NotFound"));
        }
        catch (Exception exception)
        {
            _logger.LogWarning("sattle.me: an integration key could not be verified: {Code}: {Message}", PaymentRules.ErrorCode(exception), exception.Message);
            return (null, Resource("Key.Unverified"));
        }
    }

    /// <summary>The page, with what it says of the key and of the currency.</summary>
    private async Task<IActionResult> ShowAsync(ConfigurationModel model)
    {
        var storeScope = GetActiveStoreScopeConfiguration();
        var status = await _keys.StatusAsync(storeScope);
        model.KeyStatus = Resource("Key." + status);
        model.HasKey = status is KeyStatus.Saved or KeyStatus.SavedForStore;
        model.IntegrationKey = null;
        model.PrimaryCurrencyCode = Services.CurrencyService.PrimaryCurrency?.CurrencyCode ?? "";
        if (_maker.PriceOf(1m) is null)
        {
            model.CurrencyWarning = Resource("Currency.Unsupported");
        }
        ViewBag.Provider = _providerManager.GetProvider(SattlemeDefaults.SystemName)?.Metadata
            ?? throw new InvalidOperationException("The payment method is not loaded");
        return View(nameof(Configure), model);
    }

    private string Resource(string name)
    {
        return T(SattlemeDefaults.ResourcePrefix + name);
    }
}
