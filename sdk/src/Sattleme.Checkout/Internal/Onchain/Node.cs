using System;

namespace Sattleme.Checkout.Internal.Onchain;

/// <summary>What the server does when the merchant's Bitcoin node does not answer a read (SPEC.md §31, <c>fallback</c>).</summary>
internal enum NodeFallback
{
    /// <summary><c>"mempool"</c>: the same read of mempool.space.</summary>
    Mempool,

    /// <summary><c>"none"</c>: never mempool.space; the read fails (<c>network</c>).</summary>
    None,
}

/// <summary>The channel's <c>bitcoinNode</c>, valid (SPEC.md §14, §31).</summary>
/// <param name="Url">A node URL (<see cref="NodeUrl.Parse"/>), as the channel writes it.</param>
/// <param name="Fallback">What a read the node does not answer asks.</param>
/// <param name="Payers">Whether the pages of checkout links read the node too (a node of the Internet only).</param>
internal sealed record BitcoinNode(string Url, NodeFallback Fallback, bool Payers)
{
    /// <summary>Its URL and fallback: the node is the merchant's choice, not a secret, but stays out of logs all the same.</summary>
    public override string ToString() => $"BitcoinNode {{ {BitcoinNodes.FallbackText(Fallback)}, payers = {(Payers ? "true" : "false")} }}";
}

/// <summary>The node a checkout link's record names (SPEC.md §30): its URL and its fallback, never <c>payers</c>.</summary>
internal sealed record RecordNode(string Url, NodeFallback Fallback);

/// <summary>
/// A node URL, checked (SPEC.md §31, "A node URL"), with where the node is read.
/// </summary>
/// <param name="Url">The URL, as it is written.</param>
/// <param name="Rest"><c>&lt;url&gt;/api</c>: the <c>&lt;explorer&gt;</c> of SPEC.md §31, "Explorers".</param>
/// <param name="WebSocket"><c>&lt;url&gt;</c> with <c>ws</c> for its leading <c>http</c>, then <c>/api/v1/ws</c>.</param>
/// <param name="Local">Whether its host is of a local network: an IPv4 address of a local range, or a name under <c>.local</c>.</param>
/// <param name="Internet">Whether it is a node of the Internet: <c>https</c>, and a name whose last label is not <c>local</c>.</param>
internal sealed record NodeUrl(string Url, string Rest, string WebSocket, bool Local, bool Internet)
{
    /// <summary>A name's length, at most.</summary>
    private const int MaxNameLength = 253;

    /// <summary>A label's length, at most.</summary>
    private const int MaxLabelLength = 63;

    /// <summary>
    /// <paramref name="value"/> as a node URL, in its one spelling: <c>&lt;scheme&gt;://&lt;host&gt;</c>,
    /// an optional <c>:&lt;port&gt;</c>, an optional path, 200 characters at most. <c>https</c>, or
    /// <c>http</c> for a local host; a host in lowercase, an IPv4 address (never in 0.0.0.0/8 nor
    /// 127.0.0.0/8) or a name (never <c>localhost</c> nor under it); a port from 1 to 65535 without a
    /// leading zero; a path of visible ASCII but <c>?</c>, <c>#</c>, <c>@</c> and <c>\</c>, ending
    /// neither with <c>/</c> nor with <c>/api</c> (ignoring ASCII case). So no user name nor password,
    /// no query, no fragment, no IPv6 address, no whitespace. Null when it is not one: nothing is
    /// repaired. This rule is the node's alone: every other URL the package asks is an https URL
    /// (<see cref="Urls.ParseHttps"/>), never an IP address.
    /// </summary>
    public static NodeUrl? Parse(string? value)
    {
        if (value is null || value.Length > Protocol.MaxNodeUrlLength)
        {
            return null;
        }
        bool https;
        string rest;
        if (value.StartsWith("https://", StringComparison.Ordinal))
        {
            https = true;
            rest = value["https://".Length..];
        }
        else if (value.StartsWith("http://", StringComparison.Ordinal))
        {
            https = false;
            rest = value["http://".Length..];
        }
        else
        {
            return null;
        }
        // The host, up to a port or a path: nothing that may start a user name, a query or a fragment.
        var hostEnd = rest.IndexOfAny([':', '/']);
        var host = hostEnd < 0 ? rest : rest[..hostEnd];
        var after = hostEnd < 0 ? "" : rest[hostEnd..];
        if (host.Length == 0)
        {
            return null;
        }
        string path;
        if (after.StartsWith(':'))
        {
            var portEnd = after.IndexOf('/', StringComparison.Ordinal);
            var port = portEnd < 0 ? after[1..] : after[1..portEnd];
            path = portEnd < 0 ? "" : after[portEnd..];
            if (!IsPort(port))
            {
                return null;
            }
        }
        else
        {
            path = after;
        }
        if (!IsPath(path))
        {
            return null;
        }
        bool local;
        var ipv4 = Ipv4Of(host);
        if (ipv4 is { } octets)
        {
            // Never the machine itself, nor "this network".
            if (octets.A is 0 or 127)
            {
                return null;
            }
            local = IsLocalIpv4(octets.A, octets.B);
        }
        else
        {
            if (!IsName(host))
            {
                return null;
            }
            local = host.EndsWith(".local", StringComparison.Ordinal);
        }
        if (!https && !local)
        {
            return null;
        }
        return new NodeUrl(
            value,
            $"{value}/api",
            $"ws{value["http".Length..]}/api/v1/ws",
            local,
            https && ipv4 is null && !local);
    }

    /// <summary>A port: 1 to 5 digits, without a leading zero, from 1 to 65535.</summary>
    private static bool IsPort(string digits) =>
        Text.IsDigits(digits, 1, 5) && digits[0] != '0' && int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture) <= 65535;

    /// <summary>
    /// A path (after the authority, empty when absent): <c>/</c> and characters from U+0021 to U+007E
    /// but <c>?</c>, <c>#</c>, <c>@</c> and <c>\</c>; the node's URL, not its API's: not <c>/</c>
    /// alone, no trailing <c>/</c>, no <c>/api</c> at its end, ignoring ASCII case.
    /// </summary>
    private static bool IsPath(string path)
    {
        if (path.Length == 0)
        {
            return true;
        }
        if (path[0] != '/')
        {
            return false;
        }
        foreach (var c in path)
        {
            if (c is < '\x21' or > '\x7e' or '?' or '#' or '@' or '\\')
            {
                return false;
            }
        }
        return !path.EndsWith('/') && !path.EndsWith("/api", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The first two numbers of an IPv4 address (4 numbers from 0 to 255, none with a leading zero), null when <paramref name="host"/> is not one.</summary>
    private static (int A, int B)? Ipv4Of(string host)
    {
        var parts = host.Split('.');
        if (parts.Length != 4)
        {
            return null;
        }
        var numbers = new int[4];
        for (var i = 0; i < 4; i++)
        {
            var part = parts[i];
            if (!Text.IsDigits(part, 1, 3) || (part.Length > 1 && part[0] == '0'))
            {
                return null;
            }
            numbers[i] = int.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
            if (numbers[i] > 255)
            {
                return null;
            }
        }
        return (numbers[0], numbers[1]);
    }

    /// <summary>Whether an IPv4 address is of a local range: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 100.64.0.0/10 (RFC 6598, which Tailscale uses).</summary>
    private static bool IsLocalIpv4(int a, int b) =>
        a == 10 || (a == 172 && b is >= 16 and <= 31) || (a == 192 && b == 168) || (a == 100 && b is >= 64 and <= 127);

    /// <summary>
    /// Whether <paramref name="host"/> is a name: 2 labels or more separated by <c>.</c>, each of 1 to
    /// 63 characters <c>a–z 0–9 -</c>, neither starting nor ending with <c>-</c>, the last starting
    /// with a letter <c>a–z</c>; 253 characters at most; never <c>localhost</c> nor under it.
    /// </summary>
    private static bool IsName(string host)
    {
        if (host.Length > MaxNameLength)
        {
            return false;
        }
        var labels = host.Split('.');
        if (labels.Length < 2)
        {
            return false;
        }
        foreach (var label in labels)
        {
            if (label.Length is 0 or > MaxLabelLength || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }
            foreach (var c in label)
            {
                if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                {
                    return false;
                }
            }
        }
        var last = labels[^1];
        return last[0] is >= 'a' and <= 'z' && last != "localhost";
    }
}

/// <summary>
/// The merchant's Bitcoin node (SPEC.md §31, "The merchant's Bitcoin node"): a node of its own that
/// serves mempool.space's API (an Umbrel, a server of its own), which the channel names
/// (<c>bitcoinNode</c>, SPEC.md §14) and the server then reads the blockchain from, rather than
/// from mempool.space. Nothing is repaired: a <c>bitcoinNode</c> that is not in its one spelling
/// names no node, and the channel stays valid.
/// </summary>
internal static class BitcoinNodes
{
    /// <summary>
    /// The channel's <c>bitcoinNode</c> (SPEC.md §14, §31): an object whose <c>url</c> is a node URL,
    /// whose <c>fallback</c> is exactly <c>"mempool"</c> or <c>"none"</c> and whose <c>payers</c> is
    /// a JSON boolean, <c>true</c> only for a node of the Internet (a link must never make each
    /// customer's browser probe its own local network); its other members ignored. Null when it is
    /// absent or invalid in any way: there is none, the server reads mempool.space.
    /// </summary>
    public static BitcoinNode? Of(Json? value)
    {
        if (value is not JsonObject node)
        {
            return null;
        }
        if (NodeUrl.Parse(JsonRead.String(node["url"])) is not { } url || FallbackOf(node["fallback"]) is not { } fallback
            || JsonRead.Boolean(node["payers"]) is not { } payers)
        {
            return null;
        }
        if (payers && !url.Internet)
        {
            return null;
        }
        return new BitcoinNode(url.Url, fallback, payers);
    }

    /// <summary>
    /// What a checkout link's record of version 2 holds of <paramref name="node"/> (SPEC.md §30): its
    /// <c>url</c> and <c>fallback</c>, copied as they are, never <c>payers</c>; null (the field
    /// absent) when the channel names no node or its <c>payers</c> is false.
    /// </summary>
    public static RecordNode? RecordNodeFor(BitcoinNode? node) => node is { Payers: true } ? new RecordNode(node.Url, node.Fallback) : null;

    /// <summary>
    /// The node a page reads from a record of version 2's <c>bitcoinNode</c> (SPEC.md §30, "Reading a
    /// record"): an object whose <c>url</c> is a node URL of a node of the Internet and whose
    /// <c>fallback</c> is exactly <c>"mempool"</c> or <c>"none"</c> (its other members ignored);
    /// anything else (<c>null</c> included) is set aside, the page reading mempool.space, and leaves
    /// the record valid.
    /// </summary>
    public static RecordNode? RecordNodeOf(Json? value)
    {
        if (value is not JsonObject node)
        {
            return null;
        }
        if (NodeUrl.Parse(JsonRead.String(node["url"])) is not { Internet: true } url || FallbackOf(node["fallback"]) is not { } fallback)
        {
            return null;
        }
        return new RecordNode(url.Url, fallback);
    }

    /// <summary>The JSON of a record's node: <c>{"url": …, "fallback": …}</c>.</summary>
    public static JsonObject ToJson(RecordNode node) => new JsonObject().Set("url", node.Url).Set("fallback", FallbackText(node.Fallback));

    /// <summary><c>"mempool"</c> or <c>"none"</c>.</summary>
    public static string FallbackText(NodeFallback fallback) => fallback == NodeFallback.Mempool ? "mempool" : "none";

    /// <summary>The fallback <paramref name="value"/> names: exactly <c>"mempool"</c> or <c>"none"</c>, else null.</summary>
    private static NodeFallback? FallbackOf(Json? value) => JsonRead.String(value) switch
    {
        "mempool" => NodeFallback.Mempool,
        "none" => NodeFallback.None,
        _ => null,
    };
}
