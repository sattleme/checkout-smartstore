namespace Sattleme.Checkout.Internal;

/// <summary>An https URL's host and port: 443 when it names none.</summary>
internal readonly record struct HttpsUrl(string Host, int Port);

/// <summary>
/// Hosts and https URLs, in their one spelling (SPEC.md §9, §15): what the package asks over the
/// network (relays, the Lightning address's pay URL, its callback and verify URLs) and what a
/// checkout link sends the customer to. Nothing is repaired: a URL that is not so is refused, so
/// that no request goes to an IP address, to the machine itself (<c>localhost</c> and the names
/// under it, RFC 6761) or to a host other than the one checked.
/// </summary>
internal static class Urls
{
    /// <summary>
    /// Whether <paramref name="host"/> is a host as in a relay address: labels of <c>a–z 0–9 -</c>
    /// separated by <c>.</c>, the last of at least two letters <c>a–z</c> and not <c>localhost</c>,
    /// at most 253 characters (so never an IP address, never upper case).
    /// </summary>
    public static bool IsHost(string host)
    {
        if (host.Length == 0 || host.Length > Protocol.MaxHostLength)
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
            if (label.Length == 0)
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
        if (last.Length < 2)
        {
            return false;
        }
        foreach (var c in last)
        {
            if (c is < 'a' or > 'z')
            {
                return false;
            }
        }
        return last != "localhost";
    }

    /// <summary>Whether <paramref name="digits"/> (1 to 5 digits, null when absent) is absent or 1 to 65535.</summary>
    public static bool IsPort(string? digits)
    {
        if (digits is null)
        {
            return true;
        }
        if (!Text.IsDigits(digits, 1, 5))
        {
            return false;
        }
        var port = int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        return port is >= 1 and <= 65535;
    }

    /// <summary>
    /// Whether every character of <paramref name="value"/> is one a redirect URL or an https URL
    /// may hold: RFC 3986's <c>A–Z a–z 0–9 - . _ ~ : / ? # [ ] @ ! $ &amp; ' ( ) * + , ; =</c>, or
    /// <c>%</c> followed by two hex digits; and there is at least one.
    /// </summary>
    public static bool HasUrlCharacters(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '%')
            {
                if (i + 2 >= value.Length || !char.IsAsciiHexDigit(value[i + 1]) || !char.IsAsciiHexDigit(value[i + 2]))
                {
                    return false;
                }
                i += 2;
                continue;
            }
            var allowed = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or '-' or '.' or '_' or '~' or ':' or '/' or '?' or '#' or '[' or ']' or '@' or '!' or '$'
                or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '=';
            if (!allowed)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// <paramref name="value"/> as an https URL (SPEC.md §15: rule 1 of a redirect URL): 1 to 2048
    /// characters of a redirect URL, <c>https://</c>, a host, an optional port, then nothing or
    /// <c>/</c>, <c>?</c> or <c>#</c> and anything. Null when it is not one.
    /// </summary>
    public static HttpsUrl? ParseHttps(string? value)
    {
        if (value is null || value.Length > Protocol.MaxHttpsUrlLength || !HasUrlCharacters(value))
        {
            return null;
        }
        var authority = AuthorityOf(value);
        if (authority is null)
        {
            return null;
        }
        var colon = authority.IndexOf(':', System.StringComparison.Ordinal);
        var host = colon < 0 ? authority : authority[..colon];
        var port = colon < 0 ? null : authority[(colon + 1)..];
        if (port is not null && !Text.IsDigits(port, 1, 5))
        {
            return null;
        }
        if (!IsHost(host) || !IsPort(port))
        {
            return null;
        }
        return new HttpsUrl(host, port is null ? 443 : int.Parse(port, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The authority of an https URL as written (up to its first <c>/</c>, <c>?</c> or <c>#</c>), else null.</summary>
    public static string? AuthorityOf(string value)
    {
        const string Scheme = "https://";
        if (!value.StartsWith(Scheme, System.StringComparison.Ordinal))
        {
            return null;
        }
        var rest = value[Scheme.Length..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        return end < 0 ? rest : rest[..end];
    }

    /// <summary>Whether two https URLs are on the same host: their hosts and their ports are equal.</summary>
    public static bool SameHost(HttpsUrl a, HttpsUrl b) => a.Host == b.Host && a.Port == b.Port;
}
