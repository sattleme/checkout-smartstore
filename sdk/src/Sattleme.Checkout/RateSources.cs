using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sattleme.Checkout;

/// <summary>
/// A price of one bitcoin, as a custom rate source gives it (SPEC.md §26): an exact decimal string
/// (<c>"69012.5"</c>, never a number, which may already have lost digits) and, when the source
/// says it, when the price was set (Unix seconds): a dated price is refused when it is more than
/// 3600 s old or more than 300 s ahead of the server's clock.
/// </summary>
/// <param name="Price">The price of one bitcoin in the currency asked, as decimal digits.</param>
/// <param name="Time">Unix seconds: when the source set it; null when it does not say.</param>
public sealed record RateQuote(string Price, long? Time = null)
{
    /// <summary>A price without a date.</summary>
    public static implicit operator RateQuote(string price) => FromString(price);

    /// <summary>A price without a date.</summary>
    public static RateQuote FromString(string price) => new(price);
}

/// <summary>
/// A rate source of your own (SPEC.md §26, "A custom source"): CoinGecko Pro, a self-hosted
/// mempool, several sources checked against each other… Throw <see cref="UnsupportedCurrencyException"/>
/// when it has no price in the currency. An exception of this package it throws goes through as it
/// is; any other becomes a <see cref="RateException"/> (its inner exception); a cancelled call fails
/// with the cancellation.
/// </summary>
public interface IRateSource
{
    /// <summary>The price of one bitcoin in <paramref name="currency"/> (ISO 4217, upper case).</summary>
    Task<RateQuote> GetPriceAsync(string currency, CancellationToken cancellationToken);
}

/// <summary>
/// Where the rate of an order priced in a currency comes from (SPEC.md §26): a source the package
/// knows by its name (<see cref="CoinGecko"/>, <see cref="Mempool"/>, <see cref="OpenNode"/>,
/// <see cref="Coinbase"/>, <see cref="Kraken"/>), or one of your own (<see cref="Custom(IRateSource)"/>).
/// A list of them is asked in its order, the next one only when the previous one gives no usable
/// price; a name appears in a list at most once. A string converts to the source of that name: the
/// client refuses an unknown one (<see cref="InvalidOptionException"/>, <c>rateSource</c>), an order
/// too (<see cref="InvalidOrderException"/>).
/// </summary>
public sealed class RateSource
{
    private RateSource(string? name, IRateSource? custom)
    {
        Name = name;
        CustomSource = custom;
    }

    /// <summary>The names of the sources the package knows.</summary>
    public static IReadOnlyList<string> Names { get; } = ["coingecko", "mempool", "opennode", "coinbase", "kraken"];

    /// <summary>CoinGecko (<c>/simple/price</c>, dated): give a Demo key (<c>CoinGeckoApiKey</c>) for production.</summary>
    public static RateSource CoinGecko { get; } = new("coingecko", null);

    /// <summary>mempool.space (<c>/api/v1/prices</c>, dated): USD, EUR, GBP, CAD, CHF, AUD and JPY only.</summary>
    public static RateSource Mempool { get; } = new("mempool", null);

    /// <summary>OpenNode (<c>/v1/rates</c>, undated): some 140 currencies.</summary>
    public static RateSource OpenNode { get; } = new("opennode", null);

    /// <summary>Coinbase (<c>/v2/exchange-rates?currency=BTC</c>, undated).</summary>
    public static RateSource Coinbase { get; } = new("coinbase", null);

    /// <summary>Kraken (<c>/0/public/Ticker</c>, undated): the last trade's price.</summary>
    public static RateSource Kraken { get; } = new("kraken", null);

    /// <summary>
    /// The sources a client given none asks, in this order: the dated ones first (their price's age
    /// is checked), CoinGecko for its currencies, then mempool.space for its 7; then the undated ones,
    /// Coinbase and Kraken (two exchanges), and OpenNode.
    /// </summary>
    public static IReadOnlyList<RateSource> Defaults { get; } = [CoinGecko, Mempool, Coinbase, Kraken, OpenNode];

    /// <summary>The source's name, null for a custom one.</summary>
    public string? Name { get; }

    /// <summary>A custom source, null for a named one.</summary>
    public IRateSource? CustomSource { get; }

    /// <summary>
    /// The source named <paramref name="name"/>: one of <see cref="Names"/>, exactly (lower case).
    /// Any other name is refused where the source is given (the client's options, an order).
    /// </summary>
    public static RateSource Named(string name) => new(name ?? throw new ArgumentNullException(nameof(name)), null);

    /// <summary>A source of your own.</summary>
    public static RateSource Custom(IRateSource source) => new(null, source ?? throw new ArgumentNullException(nameof(source)));

    /// <summary>A source of your own, as a function of the currency and the call's cancellation.</summary>
    public static RateSource Custom(Func<string, CancellationToken, Task<RateQuote>> source) =>
        Custom(new FunctionSource(source ?? throw new ArgumentNullException(nameof(source))));

    /// <summary>The source named <paramref name="name"/> (<see cref="Named"/>).</summary>
    public static implicit operator RateSource(string name) => Named(name);

    /// <summary>The source named <paramref name="name"/>.</summary>
    public static RateSource FromString(string name) => Named(name);

    /// <summary>Its name, <c>custom</c> for a source of your own.</summary>
    public override string ToString() => Name ?? "custom";

    private sealed class FunctionSource(Func<string, CancellationToken, Task<RateQuote>> source) : IRateSource
    {
        public Task<RateQuote> GetPriceAsync(string currency, CancellationToken cancellationToken) => source(currency, cancellationToken);
    }

    /// <summary>
    /// The sources <paramref name="sources"/> names, in the order they are asked: a non-empty list of
    /// sources, each a known name or a custom source, without the same name twice. Null when it is not.
    /// </summary>
    internal static IReadOnlyList<RateSource>? CheckList(IReadOnlyList<RateSource?> sources)
    {
        if (sources.Count == 0)
        {
            return null;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source is null)
            {
                return null;
            }
            if (source.Name is { } name && (!Names.Contains(name, StringComparer.Ordinal) || !names.Add(name)))
            {
                return null;
            }
        }
        return [.. sources.Select(source => source!)];
    }

    /// <summary>
    /// The sources a JSON value names, as the specification's vectors write them (rate-sources.json):
    /// a name, or a non-empty list of names, without the same name twice. Null for anything else: an
    /// empty list, an unknown name or one in another case, <c>custom</c> (not a name: a custom source
    /// is a function), a name twice, a list inside a list, null, a number, an object.
    /// </summary>
    internal static IReadOnlyList<RateSource>? FromJson(Internal.Json? value)
    {
        if (value is Internal.JsonString { Value: var name })
        {
            return CheckList([Named(name)]);
        }
        if (value is not Internal.JsonArray array)
        {
            return null;
        }
        var sources = new List<RateSource?>(array.Count);
        foreach (var item in array.Items)
        {
            sources.Add(item is Internal.JsonString { Value: var itemName } ? Named(itemName) : null);
        }
        return CheckList(sources);
    }
}
