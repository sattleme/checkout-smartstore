using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Sattleme.Bitcoin.Components;
using Sattleme.Bitcoin.Configuration;
using Sattleme.Bitcoin.Controllers;
using Sattleme.Bitcoin.Services;
using Smartstore.Core.Checkout.Cart;
using Smartstore.Core.Checkout.Orders;
using Smartstore.Core.Checkout.Payment;
using Smartstore.Core.Configuration;
using Smartstore.Core.Stores;
using Smartstore.Core.Widgets;
using Smartstore.Engine.Modularity;
using Smartstore.Http;

namespace Sattleme.Bitcoin.Providers;

/// <summary>
/// The payment method (README.md, "How it works"): once Smartstore placed the order, the module
/// makes its invoice and Smartstore sends the customer to the order's payment page, in the shop (a
/// redirection payment method, as Smartstore's documentation describes them: PostProcessPaymentAsync
/// gives the page's address, PostProcessPaymentRequest.RedirectUrl). The invoice is never made while
/// the order is placed: Smartstore calls ProcessPaymentAsync inside the placement's database
/// transaction (OrderProcessingService.PlaceOrder.cs), which a call to the merchant's relays must
/// never hold open.
/// </summary>
[SystemName(SattlemeDefaults.SystemName)]
[FriendlyName("Bitcoin (sattle.me)")]
[Order(1)]
[PaymentMethod(PaymentMethodType.Redirection)]
public class SattlemeProvider : PaymentMethodBase, IConfigurable
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IntegrationKeys _keys;
    private readonly InvoiceMaker _maker;
    private readonly LinkGenerator _linkGenerator;
    private readonly ILogger<SattlemeProvider> _logger;
    private readonly ISettingFactory _settingFactory;
    private readonly IStoreContext _storeContext;
    private readonly SattlemeStore _store;
    private readonly TimeProvider _time;

    /// <summary>The payment method.</summary>
    public SattlemeProvider(IHttpContextAccessor httpContextAccessor,
        IntegrationKeys keys,
        InvoiceMaker maker,
        LinkGenerator linkGenerator,
        ILogger<SattlemeProvider> logger,
        ISettingFactory settingFactory,
        IStoreContext storeContext,
        SattlemeStore store,
        TimeProvider time)
    {
        _httpContextAccessor = httpContextAccessor;
        _keys = keys;
        _maker = maker;
        _linkGenerator = linkGenerator;
        _logger = logger;
        _settingFactory = settingFactory;
        _storeContext = storeContext;
        _store = store;
        _time = time;
    }

    /// <summary>The configuration page.</summary>
    public RouteInfo GetConfigurationRoute()
        => new(nameof(SattlemeAdminController.Configure), "SattlemeAdmin", new { area = "Admin" });

    /// <summary>What the checkout's payment step shows of the method: how a Bitcoin payment goes.</summary>
    public override Widget GetPaymentInfoWidget()
        => new ComponentWidget(typeof(PaymentInfoViewComponent));

    /// <summary>The additional fee, as Smartstore's own payment methods charge it.</summary>
    public override async Task<(decimal FixedFeeOrPercentage, bool UsePercentage)> GetPaymentFeeInfoAsync(ShoppingCart cart)
    {
        var settings = await _settingFactory.LoadSettingsAsync<SattlemeSettings>(_storeContext.CurrentStore.Id);
        return (settings.AdditionalFee, settings.AdditionalFeePercentage);
    }

    /// <summary>
    /// The order is being placed: its payment is pending. Refused, with a message, when the store
    /// cannot take Bitcoin payments now (no key that can be read, or a primary currency the SDK does
    /// not convert): the payment method filter hides the method then, this is the last check.
    /// </summary>
    public override async Task<ProcessPaymentResult> ProcessPaymentAsync(ProcessPaymentRequest processPaymentRequest)
    {
        ArgumentNullException.ThrowIfNull(processPaymentRequest);
        if (await _keys.KeyOfStoreAsync(processPaymentRequest.StoreId) is null || _maker.PriceOf(1m) is null)
        {
            throw new PaymentException(T(SattlemeDefaults.ResourcePrefix + "Error.Unavailable"));
        }
        return new ProcessPaymentResult { NewPaymentStatus = PaymentStatus.Pending };
    }

    /// <summary>
    /// The order is placed (and committed): its invoice is made, then the customer goes to its payment
    /// page, which shows the invoice, or why none could be made and a button to try again. On the
    /// order's "Complete payment" (IsRePostProcessPayment), the customer goes to the page as it is.
    /// </summary>
    public override async Task PostProcessPaymentAsync(PostProcessPaymentRequest postProcessPaymentRequest)
    {
        ArgumentNullException.ThrowIfNull(postProcessPaymentRequest);
        var order = postProcessPaymentRequest.Order;
        if (!postProcessPaymentRequest.IsRePostProcessPayment)
        {
            try
            {
                await _maker.MakeAsync(order);
            }
            catch (Exception exception)
            {
                // The page says that no invoice is made, and offers to try again.
                _logger.LogError(exception, "sattle.me: the Bitcoin invoice of the order {OrderId} could not be made", order.Id);
            }
        }
        var record = await _store.GetOrMakeOrderAsync(order.Id, order.OrderGuid, order.StoreId, order.CustomerId, _time.GetUtcNow().UtcDateTime);
        var httpContext = _httpContextAccessor.HttpContext;
        postProcessPaymentRequest.RedirectUrl = httpContext is null
            ? null
            : _linkGenerator.GetPathByAction(httpContext, nameof(SattlemePaymentController.Pay), "SattlemePayment",
                new { area = "", orderGuid = record.OrderGuid, token = record.PageToken });
    }

    /// <summary>
    /// Whether the order's page offers "Complete payment" (Smartstore checks first that re-posting is
    /// allowed and that the order is pending payment, not cancelled nor deleted: PaymentService
    /// .CanRePostProcessPaymentAsync; its admin's order page then says the customer can complete the
    /// payment): it leads to the payment page, only where a payment can be made there
    /// (PaymentRules.OffersPayment): the invoice that can be paid, or a new one once the last ended
    /// with nothing received; never while a payment is being confirmed or once one came.
    /// </summary>
    public override async Task<bool> CanRePostProcessPaymentAsync(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.PaymentMethodSystemName != SattlemeDefaults.SystemName)
        {
            return false;
        }
        var invoices = await _store.GetInvoicesAsync(order.Id);
        return PaymentRules.OffersPayment(order, invoices.Count == 0 ? null : invoices[^1], _time.GetUtcNow().ToUnixTimeSeconds());
    }
}
