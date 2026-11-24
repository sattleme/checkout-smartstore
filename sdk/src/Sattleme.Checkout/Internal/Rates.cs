using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sattleme.Checkout.Internal;

/// <summary>What a source answered: a price as text (null when absent), and its date when it gives one, as JSON reads it.</summary>
internal sealed record SourceQuote(string? Price, double? Time);

/// <summary>What a rate source may use. Its <see cref="ToString"/> and debugger view leave out the CoinGecko key.</summary>
[System.Diagnostics.DebuggerDisplay("{ToString(),nq}")]
internal sealed class RateContext(Http http, string? coinGeckoApiKey, CancellationToken cancellationToken)
{
    public Http Http { get; } = http;

    /// <summary>CoinGecko's Demo API key, sent to CoinGecko only: a secret of the integrator's.</summary>
    [System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
    public string? CoinGeckoApiKey { get; } = coinGeckoApiKey;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    public override string ToString() => "RateContext";
}

/// <summary>A price of one bitcoin, checked: its exact decimal, and the RateInfo it gives.</summary>
internal sealed record Rate(PriceDecimal Price, RateInfo Info);

/// <summary>
/// The rate sources (SPEC.md §26): the five the package knows, a custom source, and lists of them
/// asked one at a time, in order.
/// </summary>
internal static class Rates
{
    /// <summary>The currencies of mempool.space's <c>/api/v1/prices</c>.</summary>
    private static readonly string[] MempoolCurrencies = ["USD", "EUR", "GBP", "CAD", "CHF", "AUD", "JPY"];

    /// <summary>
    /// The price of one bitcoin in <paramref name="currency"/> from <paramref name="source"/>,
    /// checked (SPEC.md §25): an exact positive decimal, not older than 3600 s, not further ahead than
    /// 300 s. Undated sources (OpenNode, Coinbase, Kraken, most custom ones) cannot be checked for age.
    /// Throws <see cref="UnsupportedCurrencyException"/> when the source has no price in the
    /// currency, <see cref="NetworkException"/> when it does not answer, <see cref="RateException"/>
    /// for anything else.
    /// </summary>
    public static async Task<Rate> FetchRateAsync(RateSource source, string currency, RateContext context, long now)
    {
        var name = source.Name ?? "custom";
        var quote = source.CustomSource is { } custom
            ? await CustomQuoteAsync(custom, currency, context.CancellationToken).ConfigureAwait(false)
            : await NamedQuoteAsync(name, currency, context).ConfigureAwait(false);
        return Checked(name, currency, quote, now);
    }

    /// <summary>A quote checked as SPEC.md §25 says (the price, then its date): the rate, else <see cref="RateException"/>.</summary>
    public static Rate Checked(string name, string currency, SourceQuote quote, long now)
    {
        var price = Prices.ParseDecimal(quote.Price) ?? throw new RateException($"{name} gave no valid price in {currency}");
        long? time = null;
        if (quote.Time is { } dated)
        {
            if (!Numbers.IsSafeInteger(dated) || dated > now + Protocol.ClockToleranceSeconds)
            {
                throw new RateException($"{name} gave an invalid date for its price");
            }
            if (now - dated > Protocol.MaxRateAgeSeconds)
            {
                throw new RateException($"{name}'s price in {currency} is too old");
            }
            time = (long)dated;
        }
        return new Rate(price, new RateInfo(name, currency, Prices.FormatDecimal(price), now, time));
    }

    private static async Task<SourceQuote> CustomQuoteAsync(IRateSource source, string currency, CancellationToken cancellationToken)
    {
        RateQuote? quote;
        try
        {
            quote = await source.GetPriceAsync(currency, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SattlemeException)
        {
            throw;
        }
#pragma warning disable CA1031 // Any other failure of a custom source is a rate error, its cause kept.
        catch (Exception error)
#pragma warning restore CA1031
        {
            throw new RateException("The custom rate source failed", error);
        }
        if (quote is null)
        {
            throw new RateException("The custom rate source gave no price");
        }
        // A date beyond a safe integer is no date (SPEC.md §25): refused, never rounded.
        double? time = quote.Time is { } t ? (Math.Abs(t) <= Protocol.MaxSafeInteger ? t : double.NaN) : null;
        return new SourceQuote(quote.Price, time);
    }

    private static Task<SourceQuote> NamedQuoteAsync(string name, string currency, RateContext context) => name switch
    {
        "coingecko" => CoinGeckoAsync(currency, context),
        "mempool" => MempoolAsync(currency, context),
        "opennode" => OpenNodeAsync(currency, context),
        "coinbase" => CoinbaseAsync(currency, context),
        "kraken" => KrakenAsync(currency, context),
        _ => throw new RateException($"No rate source named {name}"),
    };

    /// <summary>
    /// The first usable price of <paramref name="sources"/>, asked one at a time in their order
    /// (SPEC.md §26, "A list"): the next source only when the previous one gives no usable price
    /// (network, rate or unsupported_currency, each logged). A cancellation, or any other error a
    /// custom source throws, stops the list. With no price: <see cref="UnsupportedCurrencyException"/>
    /// when no source has the currency, else the last network or rate error.
    /// </summary>
    public static async Task<Rate> FetchFirstRateAsync(IReadOnlyList<RateSource> sources, string currency, RateContext context, Func<long> now,
        Warnings warnings, Func<RateSource, string, RateContext, long, Task<Rate>>? ask = null)
    {
        ask ??= FetchRateAsync;
        SattlemeException? failure = null;
        UnsupportedCurrencyException? unsupported = null;
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            try
            {
                return await ask(source, currency, context, now()).ConfigureAwait(false);
            }
            catch (Exception error) when (error is UnsupportedCurrencyException or NetworkException or RateException)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (error is UnsupportedCurrencyException noPrice)
                {
                    unsupported = noPrice;
                }
                else
                {
                    failure = (SattlemeException)error;
                }
                if (index + 1 < sources.Count)
                {
                    var failed = (SattlemeException)error;
                    warnings.Warn($"Rate source {source} failed ({failed.Code}): {failed.Message}; trying {sources[index + 1]}");
                }
            }
        }
        if (failure is not null)
        {
            throw failure;
        }
        if (sources.Count > 1)
        {
            var names = string.Join(", ", sources);
            throw new UnsupportedCurrencyException(currency, names, $"None of {names} has a price in {currency}");
        }
        throw unsupported!;
    }

    /// <summary>GET a source's JSON object: <see cref="NetworkException"/> without an answer, <see cref="RateException"/> for anything else.</summary>
    private static async Task<JsonObject> SourceJsonAsync(string name, string url, RateContext context, IReadOnlyDictionary<string, string>? headers = null)
    {
        var answer = await context.Http.GetJsonAsync(url, context.CancellationToken, headers).ConfigureAwait(false);
        if (!answer.Ok)
        {
            var status = Numbers.Decimal(answer.Status);
            // SPEC.md §26: 5xx, 408 and 429 are no answer; 600 and above, as any other refusal, the
            // source's (a redirect is no answer either, SPEC.md §15: refused before).
            if (answer.Status is (>= 500 and <= 599) or 408 or 429)
            {
                throw new NetworkException($"{name} answered HTTP {status}");
            }
            throw new RateException($"{name} answered HTTP {status}");
        }
        return answer.Body as JsonObject ?? throw new RateException($"{name} gave no JSON object");
    }

    private static JsonObject ObjectAt(Json? value, string name) => value as JsonObject ?? throw new RateException($"{name}: unexpected answer");

    /// <summary>A source's price: a string as written, a JSON number as its shortest decimal; absent, no price in the currency.</summary>
    private static string PriceOf(Json? value, string name, string currency) => value switch
    {
        null => throw new UnsupportedCurrencyException(currency, name),
        JsonString text => text.Value,
        JsonNumber number => Prices.NumberText(number.Value) ?? throw new RateException($"{name}: unexpected price"),
        _ => throw new RateException($"{name}: unexpected price"),
    };

    /// <summary>
    /// CoinGecko (<c>/simple/price</c>, dated with <c>include_last_updated_at</c>). Without a key,
    /// limited by IP and, by CoinGecko's terms, not meant for production: give a Demo key.
    /// </summary>
    private static async Task<SourceQuote> CoinGeckoAsync(string currency, RateContext context)
    {
        var code = Text.AsciiLower(currency);
        var url = $"https://api.coingecko.com/api/v3/simple/price?ids=bitcoin&vs_currencies={code}&include_last_updated_at=true";
        var headers = context.CoinGeckoApiKey is { } key ? new Dictionary<string, string> { ["x-cg-demo-api-key"] = key } : null;
        var body = await SourceJsonAsync("coingecko", url, context, headers).ConfigureAwait(false);
        var bitcoin = ObjectAt(body["bitcoin"], "coingecko");
        return new SourceQuote(PriceOf(bitcoin[code], "coingecko", currency), JsonRead.Number(bitcoin["last_updated_at"]));
    }

    /// <summary>mempool.space (<c>/api/v1/prices</c>, dated): 7 currencies.</summary>
    private static async Task<SourceQuote> MempoolAsync(string currency, RateContext context)
    {
        if (Array.IndexOf(MempoolCurrencies, currency) < 0)
        {
            throw new UnsupportedCurrencyException(currency, "mempool");
        }
        var body = await SourceJsonAsync("mempool", "https://mempool.space/api/v1/prices", context).ConfigureAwait(false);
        var time = JsonRead.Number(body["time"]) ?? throw new RateException("mempool: no time");
        return new SourceQuote(PriceOf(body[currency], "mempool", currency), time);
    }

    /// <summary>OpenNode (<c>/v1/rates</c>): some 140 currencies, undated.</summary>
    private static async Task<SourceQuote> OpenNodeAsync(string currency, RateContext context)
    {
        var body = await SourceJsonAsync("opennode", "https://api.opennode.com/v1/rates", context).ConfigureAwait(false);
        var data = ObjectAt(body["data"], "opennode");
        var pair = data[$"BTC{currency}"] ?? throw new UnsupportedCurrencyException(currency, "opennode");
        return new SourceQuote(PriceOf(ObjectAt(pair, "opennode")[currency], "opennode", currency), null);
    }

    /// <summary>Coinbase (<c>/v2/exchange-rates?currency=BTC</c>): prices as decimal strings, read as written.</summary>
    private static async Task<SourceQuote> CoinbaseAsync(string currency, RateContext context)
    {
        var body = await SourceJsonAsync("coinbase", "https://api.coinbase.com/v2/exchange-rates?currency=BTC", context).ConfigureAwait(false);
        var data = ObjectAt(body["data"], "coinbase");
        if (JsonRead.String(data["currency"]) != "BTC")
        {
            throw new RateException("coinbase: not the rates of BTC");
        }
        return new SourceQuote(PriceOf(ObjectAt(data["rates"], "coinbase")[currency], "coinbase", currency), null);
    }

    /// <summary>
    /// Kraken (<c>/0/public/Ticker?pair=XBT&lt;currency&gt;</c>): the last trade's price
    /// (<c>c[0]</c>). The result's key is not always the pair asked (XBTUSD gives XXBTZUSD): its only
    /// entry is read.
    /// </summary>
    private static async Task<SourceQuote> KrakenAsync(string currency, RateContext context)
    {
        var body = await SourceJsonAsync("kraken", $"https://api.kraken.com/0/public/Ticker?pair=XBT{currency}", context).ConfigureAwait(false);
        if (body["error"] is not JsonArray errors)
        {
            throw new RateException("kraken: unexpected answer");
        }
        foreach (var error in errors.Items)
        {
            if (JsonRead.String(error) is { } text && text.StartsWith("EQuery:Unknown asset pair", StringComparison.Ordinal))
            {
                throw new UnsupportedCurrencyException(currency, "kraken");
            }
        }
        if (errors.Count > 0)
        {
            throw new RateException($"kraken: {JsonRead.String(errors[0]) ?? errors[0].Write()}");
        }
        var result = ObjectAt(body["result"], "kraken");
        if (result.Count != 1)
        {
            throw new RateException("kraken: unexpected answer");
        }
        if (ObjectAt(result[result.Names[0]], "kraken")["c"] is not JsonArray last)
        {
            throw new RateException("kraken: unexpected answer");
        }
        return new SourceQuote(PriceOf(last.Count > 0 ? last[0] : null, "kraken", currency), null);
    }
}
