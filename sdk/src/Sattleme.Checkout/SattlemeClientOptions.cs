using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Sattleme.Checkout;

/// <summary>
/// The options of a <see cref="SattlemeClient"/> (SPEC.md §4, "Construction"). Each is checked when
/// the client is made, before any request: an invalid one is <see cref="InvalidOptionException"/>
/// naming it. Its <see cref="ToString"/>, its debugger view and its JSON (System.Text.Json, Json.NET,
/// any serializer that reads public properties) never hold the key nor the CoinGecko key: both are
/// set, never read back, from outside the package.
/// </summary>
[DebuggerDisplay("{ToString(),nq}")]
public sealed class SattlemeClientOptions
{
    /// <summary>
    /// The integration's secret key (<c>sattleme_ck_…</c>, "Integrations" in the merchant's app):
    /// from the environment or a vault, never from a repository, a web page or a log. Required: a
    /// client without one is <see cref="InvalidKeyException"/>. Set only: no serializer reads it.
    /// </summary>
    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string Key { internal get; init; } = "";

    /// <summary>
    /// Where orders priced in a currency get their rate: sources asked in this order, the next one
    /// only when the previous one gives no price; null: <see cref="RateSource.Defaults"/>. Each
    /// order may give its own.
    /// </summary>
    public IReadOnlyList<RateSource>? RateSources { get; init; }

    /// <summary>
    /// CoinGecko's Demo API key, sent to CoinGecko only: 1 or more visible ASCII characters (an
    /// empty string is invalid, not absent). Null: CoinGecko's keyless API, limited by IP. Set only:
    /// no serializer reads it.
    /// </summary>
    [JsonIgnore]
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public string? CoinGeckoApiKey { internal get; init; }

    /// <summary>
    /// The HTTP handler of every request (the Lightning address, the rate sources, the explorer:
    /// mempool.space or the merchant's Bitcoin node, BlockCypher), to restrict what the server
    /// reaches or go through a proxy; it should follow no redirect (one followed is refused all the
    /// same) and ask no compression. The merchant's Bitcoin node may be asked over plain
    /// <c>http://</c>, at an address of a private network (README.md, "The merchant's Bitcoin
    /// node"): those requests go through it too, and a handler that refuses them leaves the node
    /// unreachable, its fallback then applying. Null: a <see cref="SocketsHttpHandler"/> that
    /// follows no redirect, keeps no cookies, asks no compression. The client never disposes it.
    /// </summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>
    /// The WebSockets to the relays and to the explorer's WebSocket (mempool.space's, or the
    /// merchant's Bitcoin node's, <c>ws://</c> too for a node on a local network). Null:
    /// <see cref="ClientWebSocketFactory"/> (no redirect followed, no compression offered, the
    /// User-Agent of the package).
    /// </summary>
    public IWebSocketFactory? WebSocketFactory { get; init; }

    /// <summary>Per relay request, connection included: 100 to 60 000 ms; null: 10 000.</summary>
    public int? RelayTimeoutMs { get; init; }

    /// <summary>Per HTTP request (Lightning address, rate sources, the explorer, BlockCypher), body included: 100 to 60 000 ms; null: 10 000.</summary>
    public int? HttpTimeoutMs { get; init; }

    /// <summary>Where warnings go (never a secret): a newer format met, a failure only logged. Null: none.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>The clock: for tests only. Null: the system's.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Random bytes: for reproducible tests only. Null: the platform's CSPRNG (<see cref="RandomNumberGenerator"/>).</summary>
    public RandomNumberGenerator? Random { get; init; }

    /// <summary>The options, without the keys.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"SattlemeClientOptions {{ Key = (secret), RelayTimeoutMs = {RelayTimeoutMs}, HttpTimeoutMs = {HttpTimeoutMs} }}");
}

/// <summary>The options of <see cref="SattlemeClient.WaitForPaymentAsync"/> (SPEC.md §23).</summary>
public sealed class WaitOptions
{
    /// <summary>Between two checks: 250 to 60 000 ms; null: 2000.</summary>
    public int? IntervalMs { get; init; }

    /// <summary>Hears each status, as it is checked.</summary>
    public Action<PaymentStatus>? OnStatus { get; init; }
}
