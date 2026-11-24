using System;
using System.Collections.Generic;
using System.Linq;

namespace Sattleme.Checkout.Internal;

/// <summary>
/// Lists of Nostr relays, as the merchant's app writes them (SPEC.md §9): every address a
/// <c>wss://</c> URL in its one spelling, 1 to 10 of them, none twice. A list that is not so is
/// refused, never repaired.
/// </summary>
internal static class Relays
{
    public const string Prefix = "wss://";

    /// <summary>
    /// Whether <paramref name="url"/> is a relay address in its one spelling: <c>wss://</c>, a
    /// lowercase host (not <c>localhost</c> nor a name under it), an optional port (1 to 65535) and
    /// an optional path without whitespace (ECMAScript's), <c>?</c>, <c>#</c> nor <c>,</c>, not
    /// ending with <c>/</c>; 120 code points at most.
    /// </summary>
    public static bool IsRelayUrl(string url)
    {
        if (!url.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var rest = url[Prefix.Length..];
        // A host holds neither ':' nor '/': it ends at the first of them.
        var hostEnd = rest.IndexOfAny([':', '/']);
        var host = hostEnd < 0 ? rest : rest[..hostEnd];
        var after = hostEnd < 0 ? "" : rest[hostEnd..];
        string? port = null;
        if (after.StartsWith(':'))
        {
            var portEnd = after.IndexOf('/', StringComparison.Ordinal);
            port = portEnd < 0 ? after[1..] : after[1..portEnd];
            if (!Text.IsDigits(port, 1, 5))
            {
                return false;
            }
            after = portEnd < 0 ? "" : after[portEnd..];
        }
        var path = after;
        if (path.Length > 0)
        {
            if (path[0] != '/' || path.EndsWith('/'))
            {
                return false;
            }
            foreach (var c in path)
            {
                if (IsEcmaScriptWhitespace(c) || c is '?' or '#' or ',')
                {
                    return false;
                }
            }
        }
        return Urls.IsHost(host)
            && Urls.IsPort(port)
            // A lone surrogate (a JSON escape in a channel makes one) is no text (SPEC.md §6).
            && Text.IsWellFormed(path)
            && Text.CodePointLength(url) <= Protocol.MaxRelayUrlLength;
    }

    /// <summary>
    /// ECMAScript's whitespace (the merchant's app checks with JavaScript's <c>\s</c>): U+0009 to
    /// U+000D, U+0020, U+00A0, U+1680, U+2000 to U+200A, U+2028, U+2029, U+202F, U+205F, U+3000, U+FEFF.
    /// </summary>
    public static bool IsEcmaScriptWhitespace(char c) =>
        (int)c is (>= 0x09 and <= 0x0d) or 0x20 or 0xa0 or 0x1680 or (>= 0x2000 and <= 0x200a)
            or 0x2028 or 0x2029 or 0x202f or 0x205f or 0x3000 or 0xfeff;

    /// <summary>A valid list (1 to 10 addresses in their one spelling, none twice), else null.</summary>
    public static IReadOnlyList<string>? ParseList(Json? value)
    {
        if (value is not JsonArray array || array.Count == 0 || array.Count > Protocol.MaxRelays)
        {
            return null;
        }
        var list = new List<string>();
        foreach (var item in array.Items)
        {
            if (item is not JsonString { Value: var url } || !IsRelayUrl(url) || list.Contains(url))
            {
                return null;
            }
            list.Add(url);
        }
        return list;
    }

    /// <summary>A valid list of <paramref name="urls"/>, else null.</summary>
    public static IReadOnlyList<string>? ParseList(IEnumerable<string> urls) =>
        ParseList(new JsonArray(urls.Select(url => (Json)Json.Of(url))));

    /// <summary>
    /// The list of a key's or a fragment's part: the hosts without <c>wss://</c>, joined by commas,
    /// in base64url (strict, SPEC.md §2), their UTF-8 strict (a byte order mark is kept: U+FEFF, which
    /// makes no relay address). Null when it is not a valid list.
    /// </summary>
    public static IReadOnlyList<string>? DecodeList(string text)
    {
        var bytes = Base64.FromBase64Url(text);
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }
        var decoded = Text.DecodeUtf8(bytes);
        if (decoded is null)
        {
            return null;
        }
        return ParseList(decoded.Split(',').Select(host => Prefix + host));
    }

    /// <summary>The list as a key's or a fragment's part writes it.</summary>
    public static string EncodeList(IReadOnlyList<string> relays) =>
        Base64.ToBase64Url(Text.Utf8(string.Join(',', relays.Select(relay => relay[Prefix.Length..]))));

    /// <summary>Whether two lists are the same: the same addresses, in the same order.</summary>
    public static bool SameList(IReadOnlyList<string> a, IReadOnlyList<string> b) => a.SequenceEqual(b, StringComparer.Ordinal);
}
