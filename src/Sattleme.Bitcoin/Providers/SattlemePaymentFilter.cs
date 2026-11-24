using Sattleme.Bitcoin.Services;
using Smartstore.Core.Checkout.Payment;

namespace Sattleme.Bitcoin.Providers;

/// <summary>
/// Hides the payment method while the store cannot take Bitcoin payments: no integration key that
/// can be read for the store, or a primary currency the SDK does not convert (its table of
/// currencies, Currencies.DigitsOf). Smartstore finds the filter itself (PaymentService
/// .GetAllPaymentMethodFilters). An amount the SDK refuses (out of the merchant's limits) is told on
/// the payment page.
/// </summary>
public class SattlemePaymentFilter : IPaymentMethodFilter
{
    private readonly IntegrationKeys _keys;
    private readonly InvoiceMaker _maker;

    /// <summary>The filter.</summary>
    public SattlemePaymentFilter(IntegrationKeys keys, InvoiceMaker maker)
    {
        _keys = keys;
        _maker = maker;
    }

    /// <inheritdoc/>
    public async Task<bool> IsExcludedAsync(PaymentFilterRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PaymentProvider?.Metadata?.SystemName != SattlemeDefaults.SystemName)
        {
            return false;
        }
        return _maker.PriceOf(1m) is null || await _keys.KeyOfStoreAsync(request.StoreId) is null;
    }
}
