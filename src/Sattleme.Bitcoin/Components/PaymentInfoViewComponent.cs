using Microsoft.AspNetCore.Mvc;
using Sattleme.Bitcoin.Configuration;
using Smartstore.Core.Configuration;
using Smartstore.Core.Stores;
using Smartstore.Web.Components;

namespace Sattleme.Bitcoin.Components;

/// <summary>What the checkout's payment step shows when the customer chooses Bitcoin: how the payment goes, and the time to pay.</summary>
public class PaymentInfoViewComponent : SmartViewComponent
{
    private readonly ISettingFactory _settingFactory;
    private readonly IStoreContext _storeContext;

    /// <summary>The component.</summary>
    public PaymentInfoViewComponent(ISettingFactory settingFactory, IStoreContext storeContext)
    {
        _settingFactory = settingFactory;
        _storeContext = storeContext;
    }

    /// <summary>The text, in the customer's language.</summary>
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var settings = await _settingFactory.LoadSettingsAsync<SattlemeSettings>(_storeContext.CurrentStore.Id);
        return View(Math.Clamp(settings.MinutesToPay, SattlemeDefaults.MinMinutes, SattlemeDefaults.MaxMinutes));
    }
}
