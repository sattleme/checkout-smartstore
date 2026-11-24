using System;
using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Sattleme.Checkout.Internal;

/// <summary>A WebSocket message larger than 1 MiB: the socket is closed (SPEC.md §13, "Limits").</summary>
internal sealed class MessageTooLargeException : Exception
{
    public MessageTooLargeException()
        : base($"A WebSocket message larger than {Protocol.MaxRelayMessageBytes} bytes")
    {
    }
}

/// <summary>
/// Whole messages of a WebSocket, read up to 1 MiB each (SPEC.md §13, "Limits"): a larger message
/// is refused (<see cref="MessageTooLargeException"/>, the socket then closed), never held whole.
/// </summary>
internal static class WebSocketMessages
{
    /// <summary>
    /// A socket of <paramref name="factory"/> connected to <paramref name="uri"/>, waited for until
    /// <paramref name="cancellationToken"/> at most, whatever the factory does with it (a DNS
    /// lookup that blocks): a socket that comes later is closed. Asked off the caller's thread, so
    /// that a factory that blocks before it returns holds no caller.
    /// </summary>
    public static async Task<WebSocket?> ConnectWithinAsync(IWebSocketFactory factory, Uri uri, CancellationToken cancellationToken)
    {
        var connecting = Task.Run(() => factory.ConnectAsync(uri, cancellationToken), CancellationToken.None);
        try
        {
            return await connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Given up (or failed): whatever it ends with, observed; a socket it gives, closed.
            _ = connecting.ContinueWith(
                static late =>
                {
                    if (late.IsCompletedSuccessfully)
                    {
                        late.Result?.Abort();
                        late.Result?.Dispose();
                    }
                    else
                    {
                        _ = late.Exception;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>
    /// The next message of <paramref name="socket"/>: its text (strict UTF-8; null for invalid
    /// bytes, which nothing reads), or <c>Closed</c> once the socket closed.
    /// </summary>
    public static async Task<(bool Closed, string? Text)> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return (true, null);
                }
                if (message.Length + result.Count > Protocol.MaxRelayMessageBytes)
                {
                    throw new MessageTooLargeException();
                }
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return (false, Text.DecodeUtf8(message.GetBuffer().AsSpan(0, (int)message.Length)));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Sends <paramref name="text"/> as one text message.</summary>
    public static Task SendAsync(WebSocket socket, string text, CancellationToken cancellationToken) =>
        socket.SendAsync(Text.Utf8(text), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
}
