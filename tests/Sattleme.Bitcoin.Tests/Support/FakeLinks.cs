using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Sattleme.Bitcoin.Tests.Support;

/// <summary>
/// ASP.NET Core's LinkGenerator, faked: the module's routes as its controllers declare them
/// (sattleme/pay/{orderGuid}, sattleme/status/{orderGuid}, sattleme/invoice/{orderGuid}), any other
/// action as /{controller}/{action}/{id}; the other values in the query string, encoded.
/// </summary>
public sealed class FakeLinks : LinkGenerator
{
    /// <summary>The path of <paramref name="values"/>.</summary>
    public static string PathOf(RouteValueDictionary values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var rest = new RouteValueDictionary(values);
        var action = Take(rest, "action");
        var controller = Take(rest, "controller");
        _ = Take(rest, "area");
        string path;
        if (controller == "SattlemePayment")
        {
            var route = action switch
            {
                "Pay" => "pay",
                "Status" => "status",
                "NewInvoice" => "invoice",
                _ => throw new InvalidOperationException("No route of the payment page: " + action),
            };
            path = $"/sattleme/{route}/{Take(rest, "orderGuid")}";
        }
        else
        {
            var id = Take(rest, "id");
            path = $"/{controller}/{action}" + (id is null ? "" : "/" + id);
        }
        var query = new StringBuilder();
        foreach (var (key, value) in rest)
        {
            if (value is null)
            {
                continue;
            }
            query.Append(query.Length == 0 ? '?' : '&')
                .Append(Uri.EscapeDataString(key)).Append('=')
                .Append(Uri.EscapeDataString(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""));
        }
        return path + query;
    }

    private static string? Take(RouteValueDictionary values, string key)
    {
        if (values.Remove(key, out var value))
        {
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        return null;
    }

    /// <inheritdoc/>
    public override string? GetPathByAddress<TAddress>(HttpContext httpContext, TAddress address, RouteValueDictionary values,
        RouteValueDictionary? ambientValues = null, PathString? pathBase = null, FragmentString fragment = default, LinkOptions? options = null)
    {
        return PathOf(values);
    }

    /// <inheritdoc/>
    public override string? GetPathByAddress<TAddress>(TAddress address, RouteValueDictionary values,
        PathString pathBase = default, FragmentString fragment = default, LinkOptions? options = null)
    {
        return PathOf(values);
    }

    /// <inheritdoc/>
    public override string? GetUriByAddress<TAddress>(HttpContext httpContext, TAddress address, RouteValueDictionary values,
        RouteValueDictionary? ambientValues = null, string? scheme = null, HostString? host = null, PathString? pathBase = null,
        FragmentString fragment = default, LinkOptions? options = null)
    {
        return "https://bakery.example" + PathOf(values);
    }

    /// <inheritdoc/>
    public override string? GetUriByAddress<TAddress>(TAddress address, RouteValueDictionary values, string? scheme, HostString host,
        PathString pathBase = default, FragmentString fragment = default, LinkOptions? options = null)
    {
        return "https://bakery.example" + PathOf(values);
    }
}
