using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Sattleme.Checkout.Internal;

/// <summary>An HTTP answer: its status and its body (JSON, or the text of the one plain-text answer).</summary>
internal sealed record HttpAnswer(int Status, Json? Body, string? Text)
{
    /// <summary>Whether the status is 2xx.</summary>
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>
/// How the package makes HTTP requests (SPEC.md §15): a GET with <c>Accept: application/json</c>
/// and the User-Agent of every implementation; no redirect followed (each URL is checked before it
/// is asked, a redirect would skip the check: it is no answer); the whole request bounded by the
/// timeout; the body read up to 1 MiB, one leading byte order mark ignored. Every failure but the
/// caller's cancellation is <see cref="NetworkException"/>; that one is the platform's
/// <see cref="OperationCanceledException"/>. An https URL only, but for the merchant's Bitcoin node
/// (<c>node</c>), whose URL follows a rule of its own (<see cref="Onchain.NodeUrl"/>: <c>http</c>
/// for a local host, an IPv4 address): every other rule holds for it too.
/// </summary>
internal sealed class Http
{
    /// <summary>The HTTP of a client given none (<see cref="NewHandler"/>).</summary>
    private static readonly HttpMessageInvoker Default = new(NewHandler(), disposeHandler: false);

    /// <summary>
    /// The HTTP of a client given none: no redirect followed, no cookie kept, no compression asked
    /// (a few compressed bytes would unpack into a large body), the system's proxy. An answer's head
    /// is bounded (SPEC.md §15): its status line and headers by .NET's own bound (64 KiB in all),
    /// and everything one answer reads (a chunked body's size lines, which .NET bounds one by one
    /// only, its body) by <see cref="Protocol.MaxHttpAnswerBytes"/>.
    /// </summary>
    internal static SocketsHttpHandler NewHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        // In KiB: .NET's default, the bound of the status line and the headers.
        MaxResponseHeadersLength = 64,
        PlaintextStreamFilter = static (context, _) => ValueTask.FromResult<Stream>(new AnswerBound(context.PlaintextStream)),
    };

    private readonly HttpMessageInvoker _invoker;

    public Http(HttpMessageHandler? handler, int timeoutMs)
    {
        _invoker = handler is null ? Default : new HttpMessageInvoker(handler, disposeHandler: false);
        TimeoutMs = timeoutMs;
    }

    /// <summary>Per request, body included.</summary>
    public int TimeoutMs { get; }

    /// <summary>The host of <paramref name="url"/>, for messages: never its path (a Lightning address's name is in it).</summary>
    public static string HostOf(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        }
        return "(invalid URL)";
    }

    /// <summary>
    /// GET <paramref name="url"/> asking for JSON: the status, and the body when it is JSON in UTF-8
    /// (else null). <paramref name="node"/>: a URL under the merchant's Bitcoin node's, checked by
    /// <see cref="Onchain.NodeUrl.Parse"/> (<c>http</c> too); else an https URL only.
    /// </summary>
    public async Task<HttpAnswer> GetJsonAsync(string url, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? headers = null, bool node = false)
    {
        var (status, bytes) = await GetBytesAsync(url, headers, node, cancellationToken).ConfigureAwait(false);
        var text = BodyText(bytes);
        return new HttpAnswer(status, text is null ? null : Json.Parse(text), null);
    }

    /// <summary>
    /// GET <paramref name="url"/> for the one plain-text answer (an explorer's height): its text, null
    /// when it is not UTF-8. <paramref name="node"/>: as for <see cref="GetJsonAsync"/>.
    /// </summary>
    public async Task<HttpAnswer> GetTextAsync(string url, CancellationToken cancellationToken, bool node = false)
    {
        var (status, bytes) = await GetBytesAsync(url, null, node, cancellationToken).ConfigureAwait(false);
        return new HttpAnswer(status, null, BodyText(bytes));
    }

    /// <summary>
    /// The text of an HTTP body (SPEC.md §15): UTF-8, one leading byte order mark (EF BB BF)
    /// ignored, as WHATWG's UTF-8 decode does; null when it is not UTF-8. Only here: everywhere
    /// else a byte order mark is a character like any other (SPEC.md §6).
    /// </summary>
    public static string? BodyText(ReadOnlySpan<byte> bytes)
    {
        var bom = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;
        return Text.DecodeUtf8(bom ? bytes[3..] : bytes);
    }

    private async Task<(int Status, byte[] Bytes)> GetBytesAsync(string url, IReadOnlyDictionary<string, string>? headers, bool node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Checked before (an https URL, SPEC.md §15; the node's own rule, SPEC.md §31): what .NET
        // cannot ask is no answer either, as a host of a punycode label that decodes to nothing
        // (xn--abc.example), which WHATWG's URL parser refuses too. Plain http for the node alone.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsIdnReadable(uri.Host)
            || !(uri.Scheme == Uri.UriSchemeHttps || (node && uri.Scheme == Uri.UriSchemeHttp)))
        {
            throw new NetworkException($"No answer from {HostOf(url)}: it cannot be asked");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeoutMs);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", Protocol.UserAgent);
        int status;
        byte[]? bytes;
        try
        {
            using var response = await SendWithinAsync(request, timeout.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            // A handler of the integrator's may follow a redirect itself: the request it answers
            // is then another one.
            var answered = response.RequestMessage?.RequestUri;
            if (answered is not null && answered != uri)
            {
                throw new NetworkException($"{HostOf(url)} answered a redirect");
            }
            if (status is >= 300 and < 400)
            {
                throw new NetworkException($"{HostOf(url)} answered a redirect (HTTP {status})");
            }
            bytes = await ReadBodyAsync(response.Content, timeout.Token).ConfigureAwait(false);
        }
        catch (NetworkException)
        {
            throw;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
#pragma warning disable CA1031 // Whatever the request meets (connection, TLS, timeout, a handler's failure): no answer.
        catch (Exception error)
#pragma warning restore CA1031
        {
            throw new NetworkException($"No answer from {HostOf(url)}", error);
        }
        return bytes is null
            ? throw new NetworkException($"{HostOf(url)} answered more than {Protocol.MaxHttpBodyBytes} bytes")
            : (status, bytes);
    }

    /// <summary>
    /// The answer to <paramref name="request"/>, waited for until <paramref name="cancellationToken"/>
    /// at most, whatever the handler does with it (SPEC.md §13, §15: the wait for the connection, its
    /// DNS lookup included, is the request's; SPEC.md §4: a cancelled call ends as such at once).
    /// Asked off the caller's thread, so that a handler that blocks before it returns holds no
    /// caller; an answer that comes later is disposed.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithinAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sending = Task.Run(() => _invoker.SendAsync(request, cancellationToken), CancellationToken.None);
        try
        {
            return await sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Given up (or failed): whatever it ends with, observed; an answer it gives, disposed.
            _ = sending.ContinueWith(
                static late =>
                {
                    if (late.IsCompletedSuccessfully)
                    {
                        late.Result.Dispose();
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

    /// <summary>Whether every punycode label (<c>xn--</c>) of <paramref name="host"/> decodes (IDNA).</summary>
    private static bool IsIdnReadable(string host)
    {
        if (!host.Contains("xn--", StringComparison.Ordinal))
        {
            return true;
        }
        try
        {
            _ = new System.Globalization.IdnMapping { UseStd3AsciiRules = true }.GetUnicode(host);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The body's bytes, or null when it is larger than 1 MiB (it is then dropped, unread beyond).</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > Protocol.MaxHttpBodyBytes)
        {
            return null;
        }
        // Each wait bounded by the request's time, whatever the content does with the token.
        var stream = await content.ReadAsStreamAsync(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var body = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return body.ToArray();
                }
                if (body.Length + read > Protocol.MaxHttpBodyBytes)
                {
                    return null;
                }
                body.Write(buffer, 0, read);
            }
        }
    }
}

/// <summary>
/// The plaintext of one HTTP/1.1 connection, bounding what each answer reads: from a request's
/// write on, <see cref="Protocol.MaxHttpAnswerBytes"/> at most, its head and its body together
/// (SPEC.md §15); beyond, the answer fails (no answer: <see cref="NetworkException"/>). A client
/// writes nothing on HTTP/1.1 but its requests.
/// </summary>
internal sealed class AnswerBound(Stream inner) : Stream
{
    private long _read;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override void Write(byte[] buffer, int offset, int count)
    {
        Interlocked.Exchange(ref _read, 0);
        inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Interlocked.Exchange(ref _read, 0);
        inner.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _read, 0);
        return inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _read, 0);
        return inner.WriteAsync(buffer, cancellationToken);
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private int Count(int read)
    {
        if (Interlocked.Add(ref _read, read) > Protocol.MaxHttpAnswerBytes)
        {
            throw new IOException($"An answer of more than {Protocol.MaxHttpAnswerBytes} bytes, its head included");
        }
        return read;
    }
}
