using Microsoft.AspNetCore.Mvc.Filters;
using Sattleme.Bitcoin.Components;
using Smartstore.Core.Widgets;

namespace Sattleme.Bitcoin.Filters;

/// <summary>
/// Shows the Bitcoin payment on the customer's order page (OrderController.Details, the zone
/// orderdetails_page_overview of Views/Order/Details.cshtml): the component renders nothing for an
/// order of another payment method.
/// </summary>
public class OrderDetailsFilter : IActionFilter
{
    private readonly IWidgetProvider _widgetProvider;

    /// <summary>The filter.</summary>
    public OrderDetailsFilter(IWidgetProvider widgetProvider)
    {
        _widgetProvider = widgetProvider;
    }

    /// <inheritdoc/>
    public void OnActionExecuting(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ActionArguments.TryGetValue("id", out var value) && value is int orderId)
        {
            _widgetProvider.RegisterWidget(SattlemeDefaults.OrderDetailsZone,
                new ComponentWidget(typeof(OrderPaymentViewComponent), new { orderId }));
        }
    }

    /// <inheritdoc/>
    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
