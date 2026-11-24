using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Sattleme.Checkout.Internal.Crypto;

namespace Sattleme.Checkout.Internal.Nostr;

/// <summary>A relay's connection failed, closed, or a request to it ran out of time.</summary>
internal sealed class RelayException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// One connection to one relay, shared by every request of a pool (SPEC.md §13): opened at once,
/// its messages read one at a time (1 MiB at most each) and heard by the requests that listen,
/// its sends queued in order. NIP-42: the relay's last challenge counts; one authentication per key
/// and challenge.
/// </summary>
#pragma warning disable CA1001 // _lifetime holds no timer: cancelled, never disposed, so that a late Close never meets a disposed source.
internal sealed class Connection
#pragma warning restore CA1001
{
    private readonly object _gate = new();
    private readonly List<Action<JsonArray>> _listeners = [];
    private readonly List<Action<Exception>> _deathListeners = [];
    private readonly Dictionary<string, Task<bool>> _auths = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private string? _challenge;
    private Exception? _dead;

    public Connection(string url, IWebSocketFactory factory, int handshakeTimeoutMs)
    {
        Url = url;
        // Waited for by each request, which reports the failure itself.
        _opened.Task.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
        // The factory is asked off the caller's thread: one that blocks (a DNS lookup) never holds
        // the call up, whose requests are bounded by their timeout and the call's cancellation.
        _ = RunAsync(factory, handshakeTimeoutMs);
    }

    public string Url { get; }

    /// <summary>Completes once the connection is open; fails when it could not open, or died first.</summary>
    public Task Opened => _opened.Task;

    /// <summary>Hears every message of the relay until the returned action is called.</summary>
    public Action Listen(Action<JsonArray> listener)
    {
        lock (_gate)
        {
            if (_dead is null)
            {
                _listeners.Add(listener);
            }
        }
        return () =>
        {
            lock (_gate)
            {
                _listeners.Remove(listener);
            }
        };
    }

    /// <summary>Hears the end of the connection (now, when it is over already).</summary>
    public Action OnDeath(Action<Exception> listener)
    {
        Exception? dead;
        lock (_gate)
        {
            dead = _dead;
            if (dead is null)
            {
                _deathListeners.Add(listener);
            }
        }
        if (dead is not null)
        {
            listener(dead);
            return static () => { };
        }
        return () =>
        {
            lock (_gate)
            {
                _deathListeners.Remove(listener);
            }
        };
    }

    /// <summary>Queues <paramref name="message"/>; nothing once the connection is over.</summary>
    public void Send(Json message)
    {
        lock (_gate)
        {
            if (_dead is not null)
            {
                return;
            }
        }
        _outgoing.Writer.TryWrite(message.Write());
    }

    /// <summary>
    /// Answers the relay's NIP-42 challenge with <paramref name="authKey"/>: whether the relay took
    /// it. One AUTH per key and challenge, shared by the requests that ask for it.
    /// </summary>
    public Task<bool> AuthenticateAsync(byte[] authKey, long createdAt, RandomSource random)
    {
        string challenge;
        lock (_gate)
        {
            if (_dead is not null || _challenge is null)
            {
                return Task.FromResult(false);
            }
            challenge = _challenge;
        }
        var id = Secp256k1.PublicKeyOf(authKey) + ":" + challenge;
        TaskCompletionSource<bool> answer;
        NostrEvent auth;
        lock (_gate)
        {
            if (_auths.TryGetValue(id, out var known))
            {
                return known;
            }
            auth = Events.Sign(createdAt, Protocol.AuthKind, [["relay", Url], ["challenge", challenge]], "", authKey, random);
            answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _auths[id] = answer.Task;
        }
        Action? stopDeath = null;
        Action? stop = null;
        stop = Listen(message =>
        {
            if (JsonRead.String(message.Count > 0 ? message[0] : null) != "OK" || JsonRead.String(message.Count > 1 ? message[1] : null) != auth.Id)
            {
                return;
            }
            stop?.Invoke();
            stopDeath?.Invoke();
            answer.TrySetResult(message.Count > 2 && JsonRead.IsTrue(message[2]));
        });
        stopDeath = OnDeath(_ => answer.TrySetResult(false));
        Send(new JsonArray().Add(Json.Of("AUTH")).Add(auth.ToJson()));
        return answer.Task;
    }

    /// <summary>Closes the connection, once what was queued is sent: every request still on it fails.</summary>
    public void Close() => Die(new RelayException($"{Url}: connection closed"));

    private async Task RunAsync(IWebSocketFactory factory, int handshakeTimeoutMs)
    {
        WebSocket socket;
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            handshake.CancelAfter(handshakeTimeoutMs);
            // Bounded whatever the factory does with the token (the wait for the connection, its DNS
            // lookup included, is the request's: SPEC.md §13).
            socket = await WebSocketMessages.ConnectWithinAsync(factory, new Uri(Url), handshake.Token).ConfigureAwait(false)
                ?? throw new RelayException($"{Url}: no socket");
        }
#pragma warning disable CA1031 // Whatever the factory throws, the connection failed.
        catch (Exception error)
#pragma warning restore CA1031
        {
            Die(new RelayException($"{Url}: connection failed", error));
            return;
        }
        using (socket)
        {
            lock (_gate)
            {
                if (_dead is not null)
                {
                    socket.Abort();
                    return;
                }
            }
            _opened.TrySetResult();
            var sending = SendLoopAsync(socket);
            await ReceiveLoopAsync(socket).ConfigureAwait(false);
            await sending.ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket)
    {
        try
        {
            while (true)
            {
                var (closed, text) = await WebSocketMessages.ReceiveAsync(socket, _lifetime.Token).ConfigureAwait(false);
                if (closed)
                {
                    Die(new RelayException($"{Url}: connection closed"));
                    return;
                }
                if (text is not null && Json.Parse(text) is JsonArray message)
                {
                    Dispatch(message);
                }
            }
        }
#pragma warning disable CA1031 // A connection that fails, or is closed, dies: its requests fail.
        catch (Exception error)
#pragma warning restore CA1031
        {
            Die(new RelayException($"{Url}: connection failed", error));
            socket.Abort();
        }
    }

    private void Dispatch(JsonArray message)
    {
        Action<JsonArray>[] listeners;
        lock (_gate)
        {
            if (message.Count > 1 && JsonRead.String(message[0]) == "AUTH" && JsonRead.String(message[1]) is { } challenge)
            {
                _challenge = challenge;
            }
            listeners = [.. _listeners];
        }
        foreach (var listener in listeners)
        {
            listener(message);
        }
    }

    /// <summary>Sends what is queued, in order; once the connection is over, a close frame, then the end.</summary>
    private async Task SendLoopAsync(WebSocket socket)
    {
        try
        {
            await foreach (var text in _outgoing.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                await WebSocketMessages.SendAsync(socket, text, _lifetime.Token).ConfigureAwait(false);
            }
            // Closed: the relay hears it, then the socket ends (at once when it does not answer).
            using var closing = new CancellationTokenSource(1000);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A send that fails ends the connection.
        catch (Exception error)
#pragma warning restore CA1031
        {
            Die(new RelayException($"{Url}: connection failed", error));
        }
        finally
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            socket.Abort();
        }
    }

    private void Die(Exception error)
    {
        Action<Exception>[] deathListeners;
        lock (_gate)
        {
            if (_dead is not null)
            {
                return;
            }
            _dead = error;
            _listeners.Clear();
            deathListeners = [.. _deathListeners];
            _deathListeners.Clear();
        }
        _opened.TrySetException(error);
        // The queue ends: what was queued is still sent, then the close frame.
        _outgoing.Writer.TryComplete();
        if (!_opened.Task.IsCompletedSuccessfully)
        {
            _lifetime.Cancel();
        }
        foreach (var listener in deathListeners)
        {
            listener(error);
        }
    }
}
