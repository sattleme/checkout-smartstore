using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Sattleme.Checkout.Internal.Onchain;

/// <summary>
/// The explorer's WebSocket (SPEC.md §31, "Waiting for an on-chain payment"): while
/// WaitForPaymentAsync waits, the explorer (mempool.space, or the merchant's Bitcoin node) pushes
/// what changes for the address (a transaction entering the mempool, confirmed, or leaving it) and
/// each new block (one more confirmation). Only a signal to check the on-chain part again: what it
/// pushes is never trusted, the check asks the explorer. <c>onChange</c> is called at each
/// connection (what happened before was not pushed) and at each message about the address or a new
/// block. Each connection tries the WebSockets of the explorer's sources in order
/// (<see cref="CallExplorer.Sources"/>): one that fails, or is lost before any message, goes to the
/// next at once (the node's to mempool.space's with <c>fallback</c> <c>"mempool"</c>, the node then
/// counted failed for the call's 60 s); with <c>"none"</c>, only the node's. A lost socket connects
/// again after 1 s, doubled at each failure (of the last one tried) up to 30 s; a connection lost
/// before it received a message is a failure, so that an explorer that takes and drops each
/// connection (each one starts a full check) is connected to less and less often. Stops at
/// <see cref="Dispose"/>.
/// </summary>
internal sealed class AddressWatcher : IDisposable
{
    /// <summary>The messages that say something changed for the address, or for its confirmations.</summary>
    private static readonly string[] Changes = ["address-transactions", "block-transactions", "address-removed-transactions", "block"];

    private readonly IWebSocketFactory _factory;
    private readonly CallExplorer _explorer;
    private readonly string _address;
    private readonly Action _onChange;
    private readonly int _handshakeTimeoutMs;
    private readonly Func<int, CancellationToken, Task> _wait;
    private readonly CancellationTokenSource _closed = new();
    private readonly Task _running;
    private int _delay = Protocol.ReconnectFirstMs;

    private AddressWatcher(IWebSocketFactory factory, CallExplorer explorer, string address, Action onChange, int handshakeTimeoutMs, Func<int, CancellationToken, Task> wait)
    {
        _factory = factory;
        _explorer = explorer;
        _address = address;
        _onChange = onChange;
        _handshakeTimeoutMs = handshakeTimeoutMs;
        _wait = wait;
        _running = RunAsync();
    }

    /// <summary>
    /// Watches <paramref name="address"/> on <paramref name="explorer"/>'s WebSockets until disposed;
    /// each handshake bounded by <paramref name="handshakeTimeoutMs"/>. <paramref name="wait"/> waits
    /// the milliseconds before a connection again (tests: the delays asked);
    /// <see cref="Task.Delay(int, CancellationToken)"/> by default.
    /// </summary>
    public static AddressWatcher Start(IWebSocketFactory factory, CallExplorer explorer, string address, Action onChange, int handshakeTimeoutMs,
        Func<int, CancellationToken, Task>? wait = null) =>
        new(factory, explorer, address, onChange, handshakeTimeoutMs, wait ?? Task.Delay);

    /// <summary>The watch, running: completes once it stopped (tests).</summary>
    internal Task Running => _running;

    public void Dispose()
    {
        // Not disposed: the loop reads its token once more. A cancelled source holds no timer.
        _closed.Cancel();
    }

    private async Task RunAsync()
    {
        // Off the caller's flow: the first connection starts once the caller went on.
        await Task.Yield();
        while (!_closed.IsCancellationRequested)
        {
            foreach (var source in _explorer.Sources())
            {
                var answered = await WatchOnAsync(source).ConfigureAwait(false);
                if (_closed.IsCancellationRequested)
                {
                    return;
                }
                if (answered)
                {
                    // Lost once it had answered: connected again after the delay (1 s again).
                    break;
                }
                // It failed, or was lost before any message: the next one at once, or later.
                if (source.Name == ExplorerName.Node)
                {
                    _explorer.NodeFailed();
                }
            }
            try
            {
                await _wait(_delay, _closed.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            _delay = Math.Min(_delay * 2, Protocol.ReconnectMaxMs);
        }
    }

    /// <summary>
    /// One connection to <paramref name="source"/>'s WebSocket, until it is lost (or the watch
    /// closed): whether it received a message before it was lost. Never throws.
    /// </summary>
    private async Task<bool> WatchOnAsync(ExplorerSource source)
    {
        WebSocket? socket = null;
        var answered = false;
        try
        {
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token))
            {
                handshake.CancelAfter(_handshakeTimeoutMs);
                socket = await WebSocketMessages.ConnectWithinAsync(_factory, new Uri(source.WebSocket), handshake.Token).ConfigureAwait(false);
            }
            if (socket is null)
            {
                throw new WebSocketException("No socket");
            }
            await WebSocketMessages.SendAsync(socket, new JsonObject().Set("action", "want").Set("data", new JsonArray().Add(Json.Of("blocks"))).Write(), _closed.Token).ConfigureAwait(false);
            await WebSocketMessages.SendAsync(socket, new JsonObject().Set("track-address", _address).Write(), _closed.Token).ConfigureAwait(false);
            Notify();
            while (true)
            {
                var (closed, text) = await WebSocketMessages.ReceiveAsync(socket, _closed.Token).ConfigureAwait(false);
                if (closed)
                {
                    break;
                }
                // The explorer answers: the next loss waits 1 s again. A connection lost before
                // a message (taken, then dropped) is a failure: the delay doubles.
                answered = true;
                _delay = Protocol.ReconnectFirstMs;
                if (text is not null && Json.Parse(text) is JsonObject message && Array.Exists(Changes, message.Has))
                {
                    Notify();
                }
            }
        }
#pragma warning disable CA1031 // The socket lost, whatever the cause: connected again later.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Lost, or closed by Dispose.
        }
        finally
        {
            if (socket is not null)
            {
                socket.Abort();
                socket.Dispose();
            }
        }
        return answered;
    }

    private void Notify()
    {
        if (!_closed.IsCancellationRequested)
        {
            _onChange();
        }
    }
}
