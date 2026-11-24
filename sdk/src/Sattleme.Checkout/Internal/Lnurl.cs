using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Sattleme.Checkout.Internal.Crypto;
using Sattleme.Checkout.Internal.Lightning;

namespace Sattleme.Checkout.Internal;

/// <summary>The LNURL-pay parameters of the merchant's Lightning address (LUD-06).</summary>
/// <param name="Callback">An https URL (SPEC.md §15).</param>
/// <param name="MinSendableMsat">The smallest amount it takes, in millisatoshis.</param>
/// <param name="MaxSendableMsat">The largest amount it takes, in millisatoshis.</param>
/// <param name="Metadata">The raw metadata: the invoices' description hash is its SHA-256 (LUD-06).</param>
/// <param name="CommentAllowed">The longest comment accepted (LUD-12), 0 when none.</param>
internal sealed record PayRequest(string Callback, long MinSendableMsat, long MaxSendableMsat, string Metadata, long CommentAllowed);

/// <summary>What the Lightning address answered: an invoice and its LUD-21 verify URL, not checked yet.</summary>
internal sealed record InvoiceAnswer(string Pr, string Verify);

/// <summary>What the Lightning part of a payment check needs of an invoice.</summary>
internal sealed record LightningInvoice(string Bolt11, string PaymentHash, string VerifyUrl, long ExpiresAt);

/// <summary>Where the Lightning part of an invoice is (SPEC.md §22).</summary>
internal enum LightningState
{
    Open,
    Expired,
    Paid,
}

/// <summary>The Lightning part of an invoice (SPEC.md §22), from its verify URL: the preimage (lowercase hex) when paid.</summary>
internal sealed record LightningPart(LightningState State, string? Preimage = null)
{
    public static readonly LightningPart Open = new(LightningState.Open);
    public static readonly LightningPart Expired = new(LightningState.Expired);

    public static LightningPart PaidWith(string preimage) => new(LightningState.Paid, preimage);
}

/// <summary>
/// The merchant's Lightning address (LUD-16, LUD-06, LUD-12, LUD-21), as the merchant's point of
/// sale uses it: plain https requests to the merchant's LNURL server (breez.tips), which makes
/// invoices for the merchant's wallet even while it is offline (SPEC.md §16, §18, §22).
/// </summary>
internal static partial class Lnurl
{
    /// <summary>A Lightning address, its ASCII whitespace trimmed and its ASCII letters lowered.</summary>
    [GeneratedRegex(@"^([a-z0-9\-_.+]+)@([a-z0-9-]+(?:\.[a-z0-9-]+)+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LightningAddress();

    /// <summary>An LNURL answer: a JSON object, or the service's refusal (<c>{"status": "ERROR"}</c>) with its reason.</summary>
    private sealed record Answer(JsonObject? Body, string? Refusal);

    /// <summary>
    /// LUD-16: the LNURL-pay endpoint of a Lightning address (ASCII whitespace around it removed,
    /// ASCII letters lowered, as the app reads it), or null when it is not one. The endpoint is an
    /// https URL (SPEC.md §15): its domain is never an IP address, nor <c>localhost</c>.
    /// </summary>
    public static string? LightningAddressUrl(string address)
    {
        var match = LightningAddress().Match(Text.AsciiLower(Text.TrimAscii(address)));
        if (!match.Success)
        {
            return null;
        }
        var url = $"https://{match.Groups[2].Value}/.well-known/lnurlp/{match.Groups[1].Value}";
        return Urls.ParseHttps(url) is null ? null : url;
    }

    /// <summary>
    /// Reads the LNURL-pay parameters of a Lightning address (LUD-16): a refusal is
    /// <see cref="NetworkException"/>; a pay request without its fields, or whose metadata holds a
    /// lone surrogate (it has no UTF-8, whose SHA-256 is its invoices' description hash),
    /// <see cref="InvalidInvoiceException"/>.
    /// </summary>
    public static async Task<PayRequest> FetchPayRequestAsync(string address, Http http, CancellationToken cancellationToken)
    {
        var url = LightningAddressUrl(address) ?? throw new InvalidInvoiceException("The merchant's Lightning address is invalid");
        var answer = await GetAsync(url, http, cancellationToken).ConfigureAwait(false);
        if (answer.Refusal is not null)
        {
            throw new NetworkException($"{Http.HostOf(url)} refused the Lightning address: {answer.Refusal}");
        }
        var json = answer.Body!;
        var callback = JsonRead.String(json["callback"]);
        var min = Msat(json["minSendable"]);
        var max = Msat(json["maxSendable"]);
        var metadata = JsonRead.String(json["metadata"]);
        if (JsonRead.String(json["tag"]) != "payRequest" || callback is null || Urls.ParseHttps(callback) is null
            || min is null || max is null || min > max || metadata is null
            // A lone surrogate (an escape in the JSON) has no UTF-8 to hash (SPEC.md §6, §16).
            || !Text.IsWellFormed(metadata))
        {
            throw new InvalidInvoiceException($"{Http.HostOf(url)} gave no valid LNURL-pay request");
        }
        var commentAllowed = JsonRead.SafeInteger(json["commentAllowed"]) is { } allowed ? Math.Max(0, allowed) : 0;
        return new PayRequest(callback, min.Value, max.Value, metadata, commentAllowed);
    }

    /// <summary>
    /// The description hash of the invoices of <paramref name="pay"/> (hex): SHA-256 of the UTF-8 of
    /// its metadata (LUD-06), the string its answer's JSON gives, whose own text is never read.
    /// </summary>
    public static string DescriptionHashOf(PayRequest pay) => Text.Hex(Hashes.Sha256(Text.Utf8(pay.Metadata)));

    /// <summary>
    /// Whole sats the Lightning address accepts: its millisatoshi bounds rounded inwards (the
    /// minimum up, the maximum down), so that a whole-sat amount within them is within the service's.
    /// </summary>
    public static (long Min, long Max) SendableSats(PayRequest pay) => ((pay.MinSendableMsat + 999) / 1000, pay.MaxSendableMsat / 1000);

    /// <summary>Whether the comment <paramref name="label"/> is sent: its UTF-8 bytes fit <paramref name="commentAllowed"/> (LUD-12).</summary>
    public static bool CommentFits(string label, long commentAllowed) => label.Length > 0 && Text.Utf8Length(label) <= commentAllowed;

    /// <summary>
    /// Asks the Lightning address for an invoice of <paramref name="sats"/> (LUD-06): <c>amount</c>
    /// in millisatoshis, <c>expiry</c> in seconds (breez.tips honours it to the second), and
    /// <c>comment</c> only when its UTF-8 bytes fit <c>commentAllowed</c> (LUD-12 does not say how a
    /// comment's length counts: bytes are never fewer than characters, so the service never refuses
    /// it). The callback's own query parameters are kept, its fragment dropped (the parameters
    /// would come after it, and never reach the server). The answer is not checked:
    /// <see cref="CheckInvoice"/> does.
    /// </summary>
    public static async Task<InvoiceAnswer> RequestInvoiceAsync(PayRequest pay, long sats, long expirySeconds, string? comment, Http http, CancellationToken cancellationToken)
    {
        var parameters = new List<(string Name, string Value)>
        {
            ("amount", Numbers.Decimal(sats * 1000)),
            ("expiry", Numbers.Decimal(expirySeconds)),
        };
        if (comment is not null && CommentFits(comment, pay.CommentAllowed))
        {
            parameters.Add(("comment", comment));
        }
        var answer = await GetAsync(WithParameters(pay.Callback, parameters), http, cancellationToken).ConfigureAwait(false);
        if (answer.Refusal is not null)
        {
            throw new NetworkException($"The Lightning address refused the invoice: {answer.Refusal}");
        }
        var pr = JsonRead.String(answer.Body!["pr"]);
        var verify = JsonRead.String(answer.Body["verify"]);
        if (pr is null || verify is null)
        {
            throw new InvalidInvoiceException("The Lightning address gave no invoice or no verify URL (LUD-21)");
        }
        return new InvoiceAnswer(pr, verify);
    }

    /// <summary>
    /// <paramref name="url"/> with <paramref name="parameters"/> set, as <c>URLSearchParams.set</c>
    /// sets them: its own parameters kept as written, the first of a name set given its value and
    /// the others of that name removed, a name it lacks added at the end, in order; its fragment
    /// (from its first <c>#</c>) dropped.
    /// </summary>
    public static string WithParameters(string url, IReadOnlyList<(string Name, string Value)> parameters)
    {
        var hash = url.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            url = url[..hash];
        }
        var question = url.IndexOf('?', StringComparison.Ordinal);
        var path = question < 0 ? url : url[..question];
        var pairs = question < 0 ? [] : url[(question + 1)..].Split('&');
        var result = new List<string>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            if (pair.Length == 0)
            {
                continue;
            }
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var name = FormDecode(equals < 0 ? pair : pair[..equals]);
            var set = IndexOf(parameters, name);
            if (set < 0)
            {
                result.Add(pair);
            }
            else if (placed.Add(name))
            {
                result.Add(FormEncode(name) + "=" + FormEncode(parameters[set].Value));
            }
        }
        foreach (var (name, value) in parameters)
        {
            if (!placed.Contains(name))
            {
                result.Add(FormEncode(name) + "=" + FormEncode(value));
            }
        }
        return path + "?" + string.Join('&', result);
    }

    private static int IndexOf(IReadOnlyList<(string Name, string Value)> parameters, string name)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            if (parameters[i].Name == name)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>application/x-www-form-urlencoded, as WHATWG's URL serializer writes a value: UTF-8, space as <c>+</c>.</summary>
    public static string FormEncode(string value)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '*' or '-' or '.' or '_')
            {
                builder.Append(c);
            }
            else if (c == ' ')
            {
                builder.Append('+');
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return builder.ToString();
    }

    /// <summary>A name of a query, decoded as a form decodes it (<c>+</c> a space, <c>%XX</c> a byte of UTF-8).</summary>
    private static string FormDecode(string text)
    {
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '+')
            {
                bytes.Add((byte)' ');
            }
            else if (c == '%' && i + 2 < text.Length && char.IsAsciiHexDigit(text[i + 1]) && char.IsAsciiHexDigit(text[i + 2]))
            {
                bytes.Add(byte.Parse(text.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>
    /// Checks an invoice before it is declared or shown (SPEC.md §18), in order: Bitcoin mainnet,
    /// exactly <paramref name="sats"/>, the merchant's description hash (breez.tips serves the
    /// invoices of all its users), a verify URL in https on the callback's host, an expiry no longer
    /// than asked, a date within 5 minutes of the clock, time left before its end. Throws
    /// <see cref="InvalidInvoiceException"/> naming the first check failed.
    /// </summary>
    public static Bolt11Invoice CheckInvoice(InvoiceAnswer answer, PayRequest pay, long sats, long durationSeconds, long now)
    {
        var decoded = Bolt11.Decode(answer.Pr) ?? throw new InvalidInvoiceException("Not a BOLT11 invoice", "bolt11");
        if (decoded.Network != "bc")
        {
            throw new InvalidInvoiceException("Not a Bitcoin mainnet invoice", "network");
        }
        if (decoded.AmountMsat != sats * 1000)
        {
            throw new InvalidInvoiceException("The invoice's amount differs from the order's", "amount");
        }
        if (decoded.DescriptionHash != DescriptionHashOf(pay))
        {
            throw new InvalidInvoiceException("The invoice does not pay the merchant's Lightning address", "description_hash");
        }
        var verify = Urls.ParseHttps(answer.Verify);
        if (verify is null || !Urls.SameHost(verify.Value, Urls.ParseHttps(pay.Callback)!.Value))
        {
            throw new InvalidInvoiceException("No verify URL (LUD-21) on the Lightning address's host", "verify_url");
        }
        if (decoded.ExpirySeconds > durationSeconds)
        {
            throw new InvalidInvoiceException("The invoice lasts longer than asked", "duration");
        }
        if (decoded.ExpirySeconds <= 0)
        {
            throw new InvalidInvoiceException("The invoice has no time left", "expired");
        }
        if (Math.Abs(decoded.Timestamp - now) > Protocol.ClockToleranceSeconds)
        {
            throw new InvalidInvoiceException("The invoice's date is too far from this server's clock", "clock");
        }
        // Dated up to 5 minutes back, a short invoice may already be over: never declared nor shown.
        if (decoded.Timestamp + decoded.ExpirySeconds <= now)
        {
            throw new InvalidInvoiceException("The invoice has no time left", "expired");
        }
        return decoded;
    }

    /// <summary>
    /// The Lightning part of an invoice's payment (LUD-21, SPEC.md §22), checked: the verify answer
    /// names this very invoice (<c>pr</c>, whatever the case of its ASCII letters), <c>settled</c> is
    /// true and the preimage's SHA-256 is the payment hash. After the invoice's end plus 120 s, "not
    /// settled" and "Not found" are final (expired); before, any refusal of the service keeps it
    /// open. Throws <see cref="NetworkException"/> when the service gives no answer (or another
    /// refusal past the grace: breez.tips sometimes answers "Internal server error" with a 200),
    /// <see cref="InvalidInvoiceException"/> when its answer is about another invoice or its preimage
    /// is wrong.
    /// </summary>
    public static async Task<LightningPart> VerifyPaymentAsync(LightningInvoice invoice, Http http, long now, CancellationToken cancellationToken)
    {
        var final = now >= invoice.ExpiresAt + Protocol.GraceSeconds;
        var answer = await GetAsync(invoice.VerifyUrl, http, cancellationToken).ConfigureAwait(false);
        if (answer.Refusal is not null)
        {
            if (!final)
            {
                return LightningPart.Open;
            }
            if (answer.Refusal == Protocol.NotFoundReason)
            {
                return LightningPart.Expired;
            }
            throw new NetworkException($"The payment check was refused: {answer.Refusal}");
        }
        var json = answer.Body!;
        var pr = JsonRead.String(json["pr"]) ?? throw new InvalidInvoiceException("The payment check names no invoice (LUD-21 pr)");
        // BOLT11 is case-insensitive (bech32): the same invoice may be written in upper case.
        if (Text.AsciiLower(pr) != Text.AsciiLower(invoice.Bolt11))
        {
            throw new InvalidInvoiceException("The payment check is about another invoice");
        }
        if (!JsonRead.IsTrue(json["settled"]))
        {
            return final ? LightningPart.Expired : LightningPart.Open;
        }
        var preimage = JsonRead.String(json["preimage"]);
        if (!Text.IsHexAnyCase(preimage, 32))
        {
            throw new InvalidInvoiceException("Paid without a valid preimage");
        }
        var proof = Text.AsciiLower(preimage!);
        if (Text.Hex(Hashes.Sha256(Text.FromHex(proof))) != invoice.PaymentHash)
        {
            throw new InvalidInvoiceException("The preimage does not match the payment hash");
        }
        return LightningPart.PaidWith(proof);
    }

    /// <summary>
    /// An LNURL answer (SPEC.md §16): the service's refusal (<c>{"status": "ERROR"}</c>, any case),
    /// sent with a 2xx or a client error status but 408 and 429 (400 "amount out of range"…); else a
    /// JSON object with a 2xx. A server error (5xx), a timeout (408), a rate limit (429) or anything
    /// that is not a JSON object is no answer: <see cref="NetworkException"/>.
    /// </summary>
    private static async Task<Answer> GetAsync(string url, Http http, CancellationToken cancellationToken)
    {
        var answer = await http.GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        var refusalStatus = answer.Status is >= 400 and < 500 and not 408 and not 429;
        var body = answer.Body as JsonObject;
        if ((answer.Ok || refusalStatus) && body is not null && JsonRead.String(body["status"]) is { } status && Text.AsciiLower(status) == "error")
        {
            return new Answer(null, JsonRead.String(body["reason"]) ?? "");
        }
        if (!answer.Ok || body is null)
        {
            throw new NetworkException($"{Http.HostOf(url)} answered HTTP {answer.Status} without an LNURL answer");
        }
        return new Answer(body, null);
    }

    /// <summary>Millisatoshis of a JSON number: a non-negative safe integer, else null.</summary>
    private static long? Msat(Json? value) => JsonRead.SafeInteger(value) is { } msat && msat >= 0 ? msat : null;
}
