using System;
using System.Collections.Generic;
using System.Diagnostics;
using Sattleme.Checkout.Internal.Crypto;
using Sattleme.Checkout.Internal.Lightning;
using Sattleme.Checkout.Internal.Nostr;
using Sattleme.Checkout.Internal.Onchain;

namespace Sattleme.Checkout.Internal;

/// <summary>
/// The price a sale is declared with, as the declaration's JSON holds it (SPEC.md §20):
/// <c>minor</c> is a JSON number (at most 2^53 − 1). For a sale in sats:
/// <c>{ minor: &lt;sats&gt;, digits: 0, currency: "SAT" }</c>.
/// </summary>
internal sealed record DeclaredPrice(long Minor, int Digits, string Currency)
{
    public JsonObject ToJson() => new JsonObject().Set("minor", Minor).Set("digits", Digits).Set("currency", Currency);

    /// <summary>A declared price, as a declaration or a lease holds one: <c>minor</c> a safe integer from <paramref name="minMinor"/>, <c>digits</c> 0 to 8, <c>currency</c> three letters A–Z.</summary>
    public static DeclaredPrice? Parse(Json? value, long minMinor)
    {
        if (value is not JsonObject o)
        {
            return null;
        }
        if (JsonRead.SafeInteger(o["minor"]) is not { } minor || minor < minMinor)
        {
            return null;
        }
        if (JsonRead.Number(o["digits"]) is not { } digits || !Numbers.IsInteger(digits) || digits < 0 || digits > Protocol.MaxDeclaredDigits)
        {
            return null;
        }
        if (JsonRead.String(o["currency"]) is not { } currency || !IsCurrencyCode(currency))
        {
            return null;
        }
        return new DeclaredPrice(minor, (int)digits, currency);
    }

    /// <summary>Three letters A–Z.</summary>
    public static bool IsCurrencyCode(string value) => value.Length == 3 && value[0] is >= 'A' and <= 'Z' && value[1] is >= 'A' and <= 'Z' && value[2] is >= 'A' and <= 'Z';
}

/// <summary>The rest author (hex) and rest key of an invoice with an address, as its checkout links' records carry them.</summary>
[DebuggerDisplay("RestReader {{ author = {Author} }}")]
internal sealed class RestReader(string author, byte[] key)
{
    public string Author { get; } = author;

    /// <summary>The rest key: it only reads rest records (SPEC.md §27), but stays out of strings all the same.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public byte[] Key { get; } = key;

    public override string ToString() => $"RestReader {{ author = {Author} }}";
}

/// <summary>
/// What a link's record holds, to write it (SPEC.md §30, "The record"); <c>BitcoinNode</c>: the
/// merchant's Bitcoin node its page reads the chain from, with an address
/// (<see cref="BitcoinNodes.RecordNodeFor"/>: the channel's with <c>payers</c> true), else null.
/// </summary>
internal sealed record CheckoutValueInput(
    string BusinessName,
    string? Invoice,
    string? VerifyUrl,
    string? OnchainAddress,
    long Sats,
    long CreatedAt,
    long ExpiresAt,
    DeclaredPrice? Fiat,
    string Note,
    string? RedirectUrl,
    RestReader? Rest,
    RecordNode? BitcoinNode = null);

/// <summary>A new link, before it is published.</summary>
internal sealed class NewCheckoutLink(string url, NostrEvent @event, byte[] secretKey)
{
    public string Url { get; } = url;

    public NostrEvent Event { get; } = @event;

    /// <summary>
    /// The author's secret: it answers the relays' authentication while the event is published,
    /// then is dropped. Never stored, returned to the caller nor logged.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public byte[] SecretKey { get; } = secretKey;

    public override string ToString() => "NewCheckoutLink";
}

/// <summary>What a fragment names (SPEC.md §30, "Reading the fragment").</summary>
internal enum FragmentStatus
{
    Link,

    /// <summary>A link of a newer format: the page asks to be updated.</summary>
    Newer,

    Invalid,

    /// <summary>Not a checkout link: another link of the page.</summary>
    Other,
}

/// <summary>A fragment, read: with a link, its author, link key and relays.</summary>
internal sealed record CheckoutFragment(
    FragmentStatus Status,
    string? Author = null,
    [property: DebuggerBrowsable(DebuggerBrowsableState.Never)] byte[]? LinkKey = null,
    IReadOnlyList<string>? Relays = null);

/// <summary>
/// A link's record, checked: what the page shows; <c>BitcoinNode</c>: the merchant's Bitcoin node
/// the page reads the chain from, with its fallback, of a record of version 2 whose
/// <c>bitcoinNode</c> is valid; null for mempool.space (none, one set aside, or a record of version
/// 1, which ignores it).
/// </summary>
internal sealed record CheckoutRecord(
    string BusinessName,
    string? Invoice,
    string? PaymentHash,
    string? VerifyUrl,
    string? OnchainAddress,
    long Sats,
    long CreatedAt,
    long ExpiresAt,
    DeclaredPrice? Fiat,
    string Note,
    string? RedirectUrl,
    string? RedirectShown,
    RestReader? Rest,
    RecordNode? BitcoinNode);

/// <summary>
/// Checkout links (SPEC.md §30): a URL of the merchant's web app,
/// <c>&lt;origin&gt;/pay#c.&lt;author&gt;.&lt;key&gt;.&lt;relays&gt;</c>, whose page reads the
/// invoice from the merchant's relays, shows it, checks its payment from the customer's browser
/// and, once it is paid, sends the customer to the order's redirect URL. The record is sealed with a
/// key of the link alone (in the URL's fragment, which browsers never send to the web app's host)
/// and signed by an author of the link alone, whose secret is forgotten once it is published:
/// nobody, the integrator included, can change a link afterwards. The package writes links; it also
/// reads them (fragments, records), as the page does, for the conformance vectors.
/// </summary>
internal static class Checkout
{
    /// <summary>
    /// A checkout origin: <c>https://</c>, a host as in a relay address, an optional port, nothing
    /// else. What the channel's <c>checkout.origin</c> must be (SPEC.md §30, "The web app's origin").
    /// </summary>
    public static bool IsCheckoutOrigin(string? value) =>
        value is not null && Urls.ParseHttps(value) is not null && Urls.AuthorityOf(value) is { } authority
            && value.Length == "https://".Length + authority.Length;

    /// <summary>The origin a channel's <c>checkout</c> field gives: null when it gives none or an invalid one.</summary>
    public static string? OriginOf(Json? checkout) =>
        checkout is JsonObject o && JsonRead.String(o["origin"]) is { } origin && IsCheckoutOrigin(origin) ? origin : null;

    /// <summary>
    /// What the page shows of a redirect URL ("Redirecting to …"): the host and port of an https
    /// URL as written, the scheme and <c>://</c> of an app's link. Null when <paramref name="value"/>
    /// is not a redirect URL (SPEC.md §30, "Redirect URLs"): nothing is repaired.
    /// </summary>
    public static string? RedirectShown(string? value)
    {
        if (value is null || value.Length > Protocol.MaxRedirectUrlLength || !Urls.HasUrlCharacters(value))
        {
            return null;
        }
        if (value.StartsWith("https://", StringComparison.Ordinal))
        {
            return Urls.ParseHttps(value) is null ? null : Urls.AuthorityOf(value);
        }
        // An app's link: a scheme a–z then a–z 0–9 + . -, then "://".
        var separator = value.IndexOf("://", StringComparison.Ordinal);
        if (separator < 1)
        {
            return null;
        }
        var scheme = value[..separator];
        if (scheme[0] is < 'a' or > 'z')
        {
            return null;
        }
        foreach (var c in scheme)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '.' or '-'))
            {
                return null;
            }
        }
        if (scheme.Length > Protocol.MaxRedirectSchemeLength || scheme == "https" || Protocol.RefusedRedirectSchemes.Contains(scheme))
        {
            return null;
        }
        return scheme + "://";
    }

    /// <summary>The keys of a link (its link key, 32 bytes): its <c>d</c> tag (hex) and its NIP-44 conversation key.</summary>
    public static (string Address, byte[] EncryptionKey) Keys(byte[] linkKey) => (
        Text.Hex(Hashes.Hkdf(linkKey, Protocol.CheckoutSalt, Protocol.CheckoutAddressInfo)),
        Hashes.Hkdf(linkKey, Protocol.CheckoutSalt, Protocol.CheckoutEncryptionInfo));

    /// <summary>The URL of a link: <c>&lt;origin&gt;/pay#c.&lt;author&gt;.&lt;key&gt;.&lt;relays&gt;</c>.</summary>
    public static string Url(string origin, string author, byte[] linkKey, IReadOnlyList<string> relays)
    {
        var parts = new[]
        {
            Protocol.CheckoutMarker,
            Base64.ToBase64Url(Text.FromHex(author)),
            Base64.ToBase64Url(linkKey),
            Internal.Relays.EncodeList(relays),
        };
        return $"{origin}{Protocol.CheckoutPath}#{string.Join('.', parts)}";
    }

    /// <summary>
    /// The record of a link to an invoice (SPEC.md §30, "The record"): version 2 with an address
    /// (its invoice and verify URL null for an order paid on-chain only, and the merchant's Bitcoin
    /// node its page reads, <c>{"url", "fallback"}</c>, when the channel's says <c>payers</c> true:
    /// absent otherwise, never null), else version 1, which never carries a node.
    /// </summary>
    public static JsonObject Value(CheckoutValueInput input)
    {
        var fiat = input.Fiat?.ToJson() ?? (Json)Json.Null;
        var redirect = Json.OfNullable(input.RedirectUrl);
        if (input.OnchainAddress is null)
        {
            return new JsonObject()
                .Set("version", Protocol.CheckoutVersion)
                .Set("businessName", input.BusinessName)
                .Set("invoice", input.Invoice!)
                .Set("verifyUrl", input.VerifyUrl!)
                .Set("fiat", fiat)
                .Set("note", input.Note)
                .Set("redirectUrl", redirect);
        }
        var value = new JsonObject()
            .Set("version", Protocol.CheckoutOnchainVersion)
            .Set("businessName", input.BusinessName)
            .Set("invoice", Json.OfNullable(input.Invoice))
            .Set("verifyUrl", Json.OfNullable(input.VerifyUrl))
            .Set("onchainAddress", input.OnchainAddress)
            .Set("sats", input.Sats)
            .Set("createdAt", input.CreatedAt)
            .Set("expiresAt", input.ExpiresAt)
            .Set("fiat", fiat)
            .Set("note", input.Note)
            .Set("redirectUrl", redirect);
        if (input.Rest is not null)
        {
            value.Set("restAuthor", input.Rest.Author).Set("restKey", Text.Hex(input.Rest.Key));
        }
        if (input.BitcoinNode is not null)
        {
            value.Set("bitcoinNode", BitcoinNodes.ToJson(input.BitcoinNode));
        }
        return value;
    }

    /// <summary>
    /// A new link to the invoice <paramref name="value"/> holds, ending <paramref name="expiresAt"/>
    /// (Unix seconds): a fresh link key and author, the record signed at
    /// <paramref name="createdAt"/>, dropped by the relays a week after the invoice's end, and the
    /// URL on <paramref name="origin"/> naming <paramref name="relays"/> (where it is published).
    /// </summary>
    public static NewCheckoutLink NewLink(string origin, IReadOnlyList<string> relays, JsonObject value, long expiresAt, long createdAt, RandomSource random)
    {
        var linkKey = random.Bytes(Protocol.KeyBytes);
        var secretKey = AuthorSecret(random);
        var (address, encryptionKey) = Keys(linkKey);
        var @event = Records.Seal(
            Protocol.CheckoutRecordType,
            value,
            secretKey,
            encryptionKey,
            address,
            createdAt,
            random,
            [["expiration", Numbers.Decimal(expiresAt + Protocol.CheckoutLifeSeconds)]]);
        return new NewCheckoutLink(Url(origin, Secp256k1.PublicKeyOf(secretKey), linkKey, relays), @event, secretKey);
    }

    /// <summary>A link author's secret: 32 random bytes, drawn again until they are a secp256k1 secret key.</summary>
    private static byte[] AuthorSecret(RandomSource random)
    {
        while (true)
        {
            var secret = random.Bytes(Protocol.KeyBytes);
            if (Secp256k1.IsSecretKey(secret))
            {
                return secret;
            }
        }
    }

    /// <summary>The link a URL's fragment (with or without "#") names.</summary>
    public static CheckoutFragment ParseFragment(string fragment)
    {
        var parts = (fragment.StartsWith('#') ? fragment[1..] : fragment).Split('.');
        var marker = parts[0];
        if (marker != Protocol.CheckoutMarker)
        {
            return IsNewerMarker(marker) ? new CheckoutFragment(FragmentStatus.Newer) : new CheckoutFragment(FragmentStatus.Other);
        }
        if (parts.Length != 4)
        {
            return new CheckoutFragment(FragmentStatus.Invalid);
        }
        var author = Base64.FromBase64Url(parts[1]);
        var linkKey = Base64.FromBase64Url(parts[2]);
        var list = Internal.Relays.DecodeList(parts[3]);
        if (author?.Length != Protocol.KeyBytes || linkKey?.Length != Protocol.KeyBytes || list is null)
        {
            return new CheckoutFragment(FragmentStatus.Invalid);
        }
        var authorHex = Text.Hex(author);
        if (!Secp256k1.IsPublicKey(authorHex))
        {
            return new CheckoutFragment(FragmentStatus.Invalid);
        }
        return new CheckoutFragment(FragmentStatus.Link, authorHex, linkKey, list);
    }

    /// <summary>The first part of a newer link's fragment: 1 to 8 characters a–z 0–9.</summary>
    private static bool IsNewerMarker(string marker)
    {
        if (marker.Length is < 1 or > 8)
        {
            return false;
        }
        foreach (var c in marker)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// A record's value, checked as the page reads it (SPEC.md §30, "The record"): version 1 or 2,
    /// with the node the page reads (<see cref="CheckoutRecord.BitcoinNode"/>). Null when it is not
    /// valid; throws <see cref="NewerRecordException"/> for a newer version.
    /// </summary>
    public static CheckoutRecord? ParseValue(Json? value)
    {
        if (value is not JsonObject v)
        {
            return null;
        }
        if (JsonRead.IsNewerVersion(v["version"], Protocol.CheckoutOnchainVersion))
        {
            throw new NewerRecordException();
        }
        var version1 = JsonRead.IsNumber(v["version"], Protocol.CheckoutVersion);
        var version2 = JsonRead.IsNumber(v["version"], Protocol.CheckoutOnchainVersion);
        if (!version1 && !version2)
        {
            return null;
        }
        if (!IsBusinessName(JsonRead.String(v["businessName"])))
        {
            return null;
        }
        var businessName = JsonRead.String(v["businessName"])!;
        string? invoice = null;
        string? verifyUrl = null;
        Bolt11Invoice? decoded = null;
        if (version1 || !JsonRead.IsNull(v["invoice"]) || !JsonRead.IsNull(v["verifyUrl"]))
        {
            invoice = JsonRead.String(v["invoice"]);
            decoded = Bolt11.Decode(invoice);
            if (decoded is null || decoded.Network != "bc" || decoded.AmountMsat is not { } msat || msat <= 0 || msat % 1000 != 0)
            {
                return null;
            }
            verifyUrl = JsonRead.String(v["verifyUrl"]);
            if (Urls.ParseHttps(verifyUrl) is null)
            {
                return null;
            }
        }
        string? onchainAddress = null;
        RestReader? rest = null;
        RecordNode? bitcoinNode = null;
        long sats;
        long createdAt;
        long expiresAt;
        if (decoded is not null)
        {
            sats = decoded.AmountMsat!.Value / 1000;
            createdAt = decoded.Timestamp;
            expiresAt = decoded.Timestamp + decoded.ExpirySeconds;
        }
        else
        {
            sats = 0;
            createdAt = 0;
            expiresAt = 0;
        }
        if (version2)
        {
            onchainAddress = JsonRead.String(v["onchainAddress"]);
            if (!Address.IsDepositAddress(onchainAddress))
            {
                return null;
            }
            if (JsonRead.SafeInteger(v["sats"]) is not { } recordSats || recordSats < 1 || recordSats > Protocol.MaxSats)
            {
                return null;
            }
            if (JsonRead.SafeInteger(v["createdAt"]) is not { } recordCreatedAt || JsonRead.SafeInteger(v["expiresAt"]) is not { } recordExpiresAt
                || recordCreatedAt >= recordExpiresAt)
            {
                return null;
            }
            if (decoded is not null && (sats != recordSats || createdAt != recordCreatedAt || expiresAt != recordExpiresAt))
            {
                return null;
            }
            sats = recordSats;
            createdAt = recordCreatedAt;
            expiresAt = recordExpiresAt;
            if (v.Has("restAuthor") || v.Has("restKey"))
            {
                var restAuthor = JsonRead.String(v["restAuthor"]);
                var restKey = JsonRead.String(v["restKey"]);
                if (!Secp256k1.IsPublicKey(restAuthor) || !Text.IsHex32(restKey))
                {
                    return null;
                }
                rest = new RestReader(restAuthor!, Text.FromHex(restKey!));
            }
            // A node of the Internet with its fallback, else set aside (null included): the page
            // reads mempool.space, and the record stays valid. Version 1 ignores it.
            bitcoinNode = BitcoinNodes.RecordNodeOf(v["bitcoinNode"]);
        }
        if (!TryParsePrice(v["fiat"], out var fiat))
        {
            return null;
        }
        var note = JsonRead.String(v["note"]);
        if (note is null || !Text.IsWellFormed(note) || Text.HasControlCharacter(note)
            || Text.CodePointLength(note) < 1 || Text.CodePointLength(note) > Protocol.MaxNoteLength)
        {
            return null;
        }
        var redirectValue = v["redirectUrl"];
        string? redirectUrl = null;
        string? shown = null;
        if (!JsonRead.IsNull(redirectValue))
        {
            redirectUrl = JsonRead.String(redirectValue);
            shown = RedirectShown(redirectUrl);
            if (shown is null)
            {
                return null;
            }
        }
        return new CheckoutRecord(
            businessName,
            decoded is null ? null : invoice,
            decoded?.PaymentHash,
            decoded is null ? null : verifyUrl,
            onchainAddress,
            sats,
            createdAt,
            expiresAt,
            fiat,
            note,
            redirectUrl,
            shown,
            rest,
            bitcoinNode);
    }

    /// <summary>A business name: a string, not empty once ASCII whitespace is trimmed, at most 200 code points, well-formed.</summary>
    public static bool IsBusinessName(string? value) =>
        value is not null && Text.IsWellFormed(value) && Text.TrimAscii(value).Length > 0
            && Text.CodePointLength(value) <= Protocol.MaxBusinessNameLength;

    /// <summary>A record's <c>fiat</c>: null, or a price of a currency other than SAT of at least 1 minor unit. False when invalid.</summary>
    private static bool TryParsePrice(Json? value, out DeclaredPrice? price)
    {
        price = null;
        if (JsonRead.IsNull(value))
        {
            return true;
        }
        price = DeclaredPrice.Parse(value, 1);
        return price is not null && price.Currency != Protocol.SatsCurrency;
    }
}
