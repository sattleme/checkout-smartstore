using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Sattleme.Checkout.Internal.Onchain;

/// <summary>An output of a transaction: its sats, and its address when the explorer names one.</summary>
internal readonly record struct TxOutput(long Sats, string? Address);

/// <summary>A transaction, as an explorer gives it, checked.</summary>
/// <param name="Txid">64 lowercase hex characters.</param>
/// <param name="Coins">The coins it spends (<c>&lt;txid&gt;:&lt;vout&gt;</c>), coinbase inputs left out, in their order.</param>
/// <param name="Outputs">Its outputs.</param>
/// <param name="BlockHeight">The height of its block; null while it is not confirmed.</param>
internal sealed record ExplorerTx(string Txid, IReadOnlyList<string> Coins, IReadOnlyList<TxOutput> Outputs, long? BlockHeight);

/// <summary>
/// What the blockchain shows (SPEC.md §31, "Explorers"): one explorer of the Esplora API, the
/// explorer, asked over HTTP: mempool.space, or the merchant's Bitcoin node when the channel names
/// one (<see cref="BitcoinNode"/>), mempool.space answering in its place when the node fails and its
/// <c>fallback</c> is <c>"mempool"</c> (<see cref="CallExplorer"/>); and BlockCypher, for when a
/// transaction was received, asked only about a transaction no check saw while its lease ran. Third
/// parties: they see the server's IP address and what it asks about, never a key. Their answers are
/// checked as strictly as a relay's: anything else is a failure (<see cref="NetworkException"/>). A
/// payment counts as made on what the explorer read alone shows.
/// </summary>
internal static class Explorer
{
    private const long MaxVout = 4_294_967_295;

    /// <summary>
    /// An Esplora transaction, checked; null when it is not one (SPEC.md §31, "Explorers"): of its
    /// outputs, together, 2 100 000 000 000 000 sats at most (every bitcoin there will ever be).
    /// </summary>
    public static ExplorerTx? ParseTransaction(Json? value)
    {
        if (value is not JsonObject tx || JsonRead.String(tx["txid"]) is not { } txid || !Text.IsHex32(txid))
        {
            return null;
        }
        if (tx["vin"] is not JsonArray vin || tx["vout"] is not JsonArray vout || tx["status"] is not JsonObject status)
        {
            return null;
        }
        var coins = new List<string>();
        foreach (var item in vin.Items)
        {
            if (item is not JsonObject input)
            {
                return null;
            }
            if (JsonRead.IsTrue(input["is_coinbase"]))
            {
                continue;
            }
            var spent = JsonRead.String(input["txid"]);
            if (!Text.IsHex32(spent) || SafeNatural(input["vout"]) is not { } index || index > MaxVout)
            {
                return null;
            }
            coins.Add($"{spent}:{Numbers.Decimal(index)}");
        }
        var outputs = new List<TxOutput>();
        long held = 0;
        foreach (var item in vout.Items)
        {
            if (item is not JsonObject output || SafeNatural(output["value"]) is not { } sats)
            {
                return null;
            }
            held = Numbers.SaturatingAdd(held, sats);
            string? address = null;
            if (output.Has("scriptpubkey_address"))
            {
                address = JsonRead.String(output["scriptpubkey_address"]);
                if (address is null)
                {
                    return null;
                }
            }
            outputs.Add(new TxOutput(sats, address));
        }
        // More than every bitcoin there will ever be: no transaction, whatever an explorer says.
        if (held > Protocol.MaxSats)
        {
            return null;
        }
        if (JsonRead.Boolean(status["confirmed"]) is not { } confirmed)
        {
            return null;
        }
        long? blockHeight = null;
        if (confirmed)
        {
            if (SafeNatural(status["block_height"]) is not { } height)
            {
                return null;
            }
            blockHeight = height;
        }
        return new ExplorerTx(txid, coins, outputs, blockHeight);
    }

    /// <summary>What <paramref name="tx"/> pays <paramref name="address"/>: the sum of its outputs to it.</summary>
    public static long PaysTo(ExplorerTx tx, string address)
    {
        long sum = 0;
        foreach (var output in tx.Outputs)
        {
            if (output.Address == address)
            {
                sum = Numbers.SaturatingAdd(sum, output.Sats);
            }
        }
        return sum;
    }

    /// <summary>Its confirmations at the chain's <paramref name="height"/>: 0 unconfirmed, else height − its block's + 1, 1 at least.</summary>
    public static long ConfirmationsOf(ExplorerTx tx, long height) =>
        tx.BlockHeight is not { } block ? 0 : Math.Max(1, height - block + 1);

    /// <summary>
    /// The transactions of <paramref name="address"/> on <paramref name="explorer"/>: its first answer
    /// (a deposit address is paid a few times at most), in the explorer's order.
    /// </summary>
    public static async Task<IReadOnlyList<ExplorerTx>> AddressTransactionsAsync(ExplorerSource explorer, string address, Http http, CancellationToken cancellationToken)
    {
        var url = $"{explorer.Rest}/address/{address}/txs";
        var answer = await http.GetJsonAsync(url, cancellationToken, node: explorer.Name == ExplorerName.Node).ConfigureAwait(false);
        if (!answer.Ok)
        {
            throw new NetworkException($"{Http.HostOf(url)} answered HTTP {Numbers.Decimal(answer.Status)}");
        }
        if (answer.Body is not JsonArray body)
        {
            throw new NetworkException($"{Http.HostOf(url)} gave no transactions");
        }
        var txs = new List<ExplorerTx>(body.Count);
        foreach (var value in body.Items)
        {
            txs.Add(ParseTransaction(value) ?? throw new NetworkException($"{Http.HostOf(url)} gave a transaction that is not one"));
        }
        return txs;
    }

    /// <summary>The chain's height on <paramref name="explorer"/>: a body of 1 to 9 digits (plain text, not JSON).</summary>
    public static async Task<long> ChainHeightAsync(ExplorerSource explorer, Http http, CancellationToken cancellationToken)
    {
        var url = $"{explorer.Rest}/blocks/tip/height";
        var answer = await http.GetTextAsync(url, cancellationToken, node: explorer.Name == ExplorerName.Node).ConfigureAwait(false);
        if (!answer.Ok)
        {
            throw new NetworkException($"{Http.HostOf(url)} answered HTTP {Numbers.Decimal(answer.Status)}");
        }
        if (answer.Text is not { } text || !Text.IsDigits(text, 1, 9))
        {
            throw new NetworkException($"{Http.HostOf(url)} gave no height");
        }
        return long.Parse(text, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The transactions of <paramref name="address"/> and the chain's height on <paramref name="explorer"/>,
    /// asked at once (SPEC.md §31, "The on-chain payment", step 1): one read, of one explorer, since a
    /// transaction's confirmations compare its block's height with that explorer's height. It fails
    /// as soon as either fails, the other's request then stopped and waited for: the whole read is
    /// asked again of the next explorer (<see cref="CallExplorer.ReadAsync"/>), never part of it.
    /// </summary>
    public static async Task<(IReadOnlyList<ExplorerTx> Txs, long Height)> TransactionsAndHeightAsync(ExplorerSource explorer, string address, Http http,
        CancellationToken cancellationToken)
    {
        using var both = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var txsTask = AddressTransactionsAsync(explorer, address, http, both.Token);
        var heightTask = ChainHeightAsync(explorer, http, both.Token);
        try
        {
            var first = await Task.WhenAny(txsTask, heightTask).ConfigureAwait(false);
            if (!first.IsCompletedSuccessfully)
            {
                // Its error is the read's: the other one ends now.
                await both.CancelAsync().ConfigureAwait(false);
                await first.ConfigureAwait(false);
            }
            return (await txsTask.ConfigureAwait(false), await heightTask.ConfigureAwait(false));
        }
        finally
        {
            // Neither runs on once the read returned (nor is left unobserved).
            await Task.WhenAll(txsTask, heightTask).ContinueWith(
                static all => _ = all.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Unix milliseconds of a time as BlockCypher writes it (UTC, a fraction of 1 to 9 digits or
    /// none, <c>Z</c>: <c>2026-10-10T13:05:12.345Z</c>), the fraction past milliseconds dropped
    /// (never rounded); null when it is not one, or not a real date and time (a month of 01 to 12, a
    /// day of that month, hours 00 to 23, minutes and seconds 00 to 59: no leap second). Another
    /// spelling of RFC 3339 is not BlockCypher's.
    /// </summary>
    public static long? BlockCypherMilliseconds(string text)
    {
        // yyyy-mm-ddThh:mm:ss[.f{1,9}]Z
        if (text.Length < 20 || text[4] != '-' || text[7] != '-' || text[10] != 'T' || text[13] != ':' || text[16] != ':' || text[^1] != 'Z')
        {
            return null;
        }
        if (!Text.IsDigits(text[..4], 4, 4) || !Text.IsDigits(text[5..7], 2, 2) || !Text.IsDigits(text[8..10], 2, 2)
            || !Text.IsDigits(text[11..13], 2, 2) || !Text.IsDigits(text[14..16], 2, 2) || !Text.IsDigits(text[17..19], 2, 2))
        {
            return null;
        }
        var fraction = "";
        if (text.Length > 20)
        {
            if (text[19] != '.' || !Text.IsDigits(text[20..^1], 1, 9))
            {
                return null;
            }
            fraction = text[20..^1];
        }
        int Number(Range range) => int.Parse(text[range], CultureInfo.InvariantCulture);
        var year = Number(..4);
        var month = Number(5..7);
        var day = Number(8..10);
        var hour = Number(11..13);
        var minute = Number(14..16);
        var second = Number(17..19);
        if (month is < 1 or > 12 || hour > 23 || minute > 59 || second > 59 || day < 1 || day > DaysInMonth(year, month))
        {
            return null;
        }
        var milliseconds = fraction.Length == 0 ? 0 : int.Parse(fraction.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
        return (((((DaysFromCivil(year, month, day) * 24) + hour) * 60) + minute) * 60 + second) * 1000L + milliseconds;
    }

    /// <summary>
    /// When BlockCypher first received <paramref name="txid"/>, in Unix milliseconds (it never
    /// changes); null when it does not know it yet (HTTP 404: it receives it later). Without a token,
    /// BlockCypher takes 100 requests an hour from an IP address: it is asked only about a
    /// transaction no check saw in time.
    /// </summary>
    public static async Task<long?> ReceivedAtAsync(string txid, Http http, CancellationToken cancellationToken)
    {
        var url = $"{Protocol.BlockCypherUrl}/txs/{txid}?limit=1&includeHex=false";
        var answer = await http.GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (answer.Status == 404)
        {
            return null;
        }
        if (!answer.Ok)
        {
            throw new NetworkException($"{Http.HostOf(url)} answered HTTP {Numbers.Decimal(answer.Status)}");
        }
        if (answer.Body is not JsonObject body || JsonRead.String(body["hash"]) != txid || JsonRead.String(body["received"]) is not { } received)
        {
            throw new NetworkException($"{Http.HostOf(url)} gave no time for the transaction");
        }
        var time = BlockCypherMilliseconds(received);
        return time is null || time <= 0 ? throw new NetworkException($"{Http.HostOf(url)} gave no time for the transaction") : time;
    }

    /// <summary>A non-negative safe integer, else null.</summary>
    private static long? SafeNatural(Json? value) => JsonRead.SafeInteger(value) is { } n && n >= 0 ? n : null;

    private static int DaysInMonth(int year, int month) => month switch
    {
        2 => (year % 4 == 0 && year % 100 != 0) || year % 400 == 0 ? 29 : 28,
        4 or 6 or 9 or 11 => 30,
        _ => 31,
    };

    /// <summary>Days from 1970-01-01 to a date of the proleptic Gregorian calendar (H. Hinnant's days_from_civil).</summary>
    private static long DaysFromCivil(long year, int month, int day)
    {
        year -= month <= 2 ? 1 : 0;
        var era = (year >= 0 ? year : year - 399) / 400;
        var yearOfEra = year - (era * 400);
        var dayOfYear = ((153 * (month + (month > 2 ? -3 : 9))) + 2) / 5 + day - 1;
        var dayOfEra = (yearOfEra * 365) + (yearOfEra / 4) - (yearOfEra / 100) + dayOfYear;
        return (era * 146_097) + dayOfEra - 719_468;
    }
}

/// <summary>An explorer a read may ask: the merchant's Bitcoin node, or mempool.space.</summary>
internal enum ExplorerName
{
    Node,
    Mempool,
}

/// <summary>An explorer asked: its name, its REST URL (<c>&lt;explorer&gt;</c>, SPEC.md §31) and its WebSocket's.</summary>
internal sealed record ExplorerSource(ExplorerName Name, string Rest, string WebSocket)
{
    /// <summary>mempool.space.</summary>
    public static readonly ExplorerSource Mempool = new(ExplorerName.Mempool, Protocol.MempoolUrl, Protocol.MempoolWebSocketUrl);
}

/// <summary>
/// When the nodes a call read failed a read (or a WebSocket connection), by node URL, in Unix
/// milliseconds: of one call alone (CreateInvoiceAsync, GetStatusAsync, one WaitForPaymentAsync),
/// shared by its reads and its WebSocket, never kept between calls. Thread-safe: a call checks
/// several invoices at once.
/// </summary>
internal sealed class NodeFailures
{
    private readonly ConcurrentDictionary<string, long> _failedAt = new(StringComparer.Ordinal);

    /// <summary>When the node at <paramref name="url"/> last failed, null when it did not in this call.</summary>
    public long? FailedAt(string url) => _failedAt.TryGetValue(url, out var at) ? at : null;

    public void Failed(string url, long at) => _failedAt[url] = at;
}

/// <summary>
/// The explorer of one call (SPEC.md §31, "The merchant's Bitcoin node"): mempool.space, or the
/// channel's node with its fallback. Each read follows the fallback on its own; a node that fails a
/// read is remembered in the call's <see cref="NodeFailures"/>, so that for 60 s it is asked after
/// mempool.space. BlockCypher is asked as without a node, and the rate source <c>mempool</c> is
/// mempool.space's: neither goes through here.
/// </summary>
internal sealed class CallExplorer
{
    private readonly ExplorerSource? _node;
    private readonly string? _url;
    private readonly NodeFallback _fallback;
    private readonly NodeFailures _failures;
    private readonly Func<long> _nowMs;

    /// <summary><paramref name="nowMs"/>: the clock, Unix milliseconds.</summary>
    public CallExplorer(BitcoinNode? node, NodeFailures failures, Func<long> nowMs)
    {
        if (node is not null && NodeUrl.Parse(node.Url) is { } at)
        {
            _node = new ExplorerSource(ExplorerName.Node, at.Rest, at.WebSocket);
            _url = at.Url;
            _fallback = node.Fallback;
        }
        _failures = failures;
        _nowMs = nowMs;
    }

    /// <summary>The node of the call, null for mempool.space: what its WebSocket's watch follows.</summary>
    public string? WatchedNode => _url is null ? null : $"{BitcoinNodes.FallbackText(_fallback)} {_url}";

    /// <summary>
    /// The explorers one read asks, in order (SPEC.md §31, <c>fallback</c>): without a node,
    /// mempool.space; with <c>"none"</c>, the node alone; with <c>"mempool"</c>, the node then
    /// mempool.space, or mempool.space first when the node failed a read less than 60 s ago in the
    /// same call (<paramref name="failedRecently"/>). The next one is asked only when the one before
    /// failed. Vectors: bitcoin-nodes.json, <c>reads</c>.
    /// </summary>
    public static IReadOnlyList<ExplorerName> ToAsk(NodeFallback? fallback, bool failedRecently) => fallback switch
    {
        null => [ExplorerName.Mempool],
        NodeFallback.None => [ExplorerName.Node],
        _ => failedRecently ? [ExplorerName.Mempool, ExplorerName.Node] : [ExplorerName.Node, ExplorerName.Mempool],
    };

    /// <summary>The explorers the next read asks, in order (<see cref="ToAsk"/>).</summary>
    public IReadOnlyList<ExplorerSource> Sources()
    {
        if (_node is null)
        {
            return [ExplorerSource.Mempool];
        }
        var failedRecently = _failures.FailedAt(_url!) is { } at && _nowMs() - at < Protocol.NodeFailedRecentlyMs;
        return [.. ToAsk(_fallback, failedRecently).Select(name => name == ExplorerName.Node ? _node : ExplorerSource.Mempool)];
    }

    /// <summary>The node failed a read (or its WebSocket a connection): remembered for the call.</summary>
    public void NodeFailed()
    {
        if (_url is not null)
        {
            _failures.Failed(_url, _nowMs());
        }
    }

    /// <summary>
    /// One read of the explorer: <paramref name="run"/> with each explorer of <see cref="Sources"/>,
    /// in order, until one answers; the next one only when the one before failed
    /// (<see cref="NetworkException"/>). Each is asked with a token of its own, cancelled once it
    /// failed: what <paramref name="run"/> asked of it at the same time ends then, and the whole read
    /// is asked again of the next one, never part of it. <see cref="NetworkException"/> when every one
    /// failed; a cancellation of <paramref name="cancellationToken"/> is thrown as it is.
    /// </summary>
    public async Task<T> ReadAsync<T>(Func<ExplorerSource, CancellationToken, Task<T>> run, CancellationToken cancellationToken)
    {
        var failures = new List<NetworkException>();
        foreach (var source in Sources())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                return await run(source, attempt.Token).ConfigureAwait(false);
            }
            catch (NetworkException error)
            {
                await attempt.CancelAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (source.Name == ExplorerName.Node)
                {
                    NodeFailed();
                }
                failures.Add(error);
            }
        }
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        throw new NetworkException(
            $"Neither the merchant's Bitcoin node nor mempool.space answered ({string.Join("; ", failures.Select(error => error.Message))})",
            failures[^1]);
    }
}
