using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Sattleme.Checkout.Internal;

namespace Sattleme.Checkout;

/// <summary>
/// Opens the client's WebSockets: to the merchant's relays and to the explorer's WebSocket
/// (<c>wss://mempool.space/api/v1/ws</c>, or the merchant's Bitcoin node's
/// <c>&lt;url&gt;/api/v1/ws</c>, <c>ws://</c> for a node in <c>http</c>). Inject your own to
/// restrict where the server connects (README.md, "Outbound requests"); it must follow no redirect
/// of the handshake and offer no compression. Whatever it gives, the client reads messages of 1 MiB at most and closes a socket
/// that sends a larger one.
/// </summary>
public interface IWebSocketFactory
{
    /// <summary>
    /// A WebSocket connected to <paramref name="uri"/>, or an exception. <paramref name="cancellationToken"/>
    /// bounds the handshake: the client cancels it after <c>RelayTimeoutMs</c>, or when the call ends.
    /// </summary>
    Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken);
}

/// <summary>
/// The client's WebSockets by default: <see cref="ClientWebSocket"/>, with the User-Agent
/// <c>sattleme-Checkout/1</c> on the handshake (relays behind bot protection refuse one without
/// it), no redirect followed (a redirect, 3xx, is a failed connection: it would skip the checks of
/// the relay's address, SPEC.md §13), no compression offered (permessage-deflate, RFC 7692: a few
/// compressed bytes would unpack into many large messages), no keep-alive frames.
/// </summary>
public sealed class ClientWebSocketFactory : IWebSocketFactory
{
    /// <summary>The handshake's HTTP: no redirect, no cookies, the system's proxy.</summary>
    private static readonly HttpMessageInvoker Handshakes = NewHandshakeInvoker(useProxy: true);

    /// <summary>The one instance: it holds no state of its own.</summary>
    public static ClientWebSocketFactory Instance { get; } = new();

    /// <inheritdoc/>
    public async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var socket = Create();
        try
        {
            await socket.ConnectAsync(uri, Handshakes, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>A <see cref="ClientWebSocket"/> configured as the client's, not connected yet.</summary>
    internal static ClientWebSocket Create()
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", Protocol.UserAgent);
        // No permessage-deflate offered: DangerousDeflateOptions stays null.
        socket.Options.DangerousDeflateOptions = null;
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        return socket;
    }

    /// <summary>
    /// The HTTP of a handshake: no redirect followed (a 3xx fails the connection), no cookies;
    /// with <paramref name="useProxy"/>, the system's proxy.
    /// </summary>
    internal static HttpMessageInvoker NewHandshakeInvoker(bool useProxy) => new(
        new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = useProxy },
        disposeHandler: true);

    /// <summary>
    /// Connects <paramref name="socket"/> to <paramref name="uri"/> as <see cref="ConnectAsync(Uri, CancellationToken)"/>
    /// does, over <paramref name="handshakes"/> (tests reach local servers without the system's proxy).
    /// </summary>
    internal static Task ConnectAsync(ClientWebSocket socket, Uri uri, HttpMessageInvoker handshakes, CancellationToken cancellationToken) =>
        socket.ConnectAsync(uri, handshakes, cancellationToken);
}
