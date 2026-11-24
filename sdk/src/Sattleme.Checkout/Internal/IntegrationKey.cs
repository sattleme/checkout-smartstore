using System;
using System.Collections.Generic;
using System.Diagnostics;
using Sattleme.Checkout.Internal.Crypto;

namespace Sattleme.Checkout.Internal;

/// <summary>
/// An integration's secret key, as the merchant's app writes it (SPEC.md §8):
/// <c>sattleme_ck_&lt;team&gt;.&lt;key&gt;.&lt;relays&gt;</c>, each part in canonical base64url without
/// padding. It stays on the integrator's server: never in a web page, a repository nor a log. Its
/// <see cref="ToString"/> and debugger view name the team and the relays only.
/// </summary>
[DebuggerDisplay("{ToString(),nq}")]
internal sealed class IntegrationKey
{
    private IntegrationKey(string teamPublicKey, byte[] key, IReadOnlyList<string> relays)
    {
        TeamPublicKey = teamPublicKey;
        Key = key;
        Relays = relays;
    }

    /// <summary>Hex: the team's public key (x only), the author of the integration's channel.</summary>
    public string TeamPublicKey { get; }

    /// <summary>The integration's key (32 bytes): a secret.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public byte[] Key { get; }

    /// <summary>Where its channel was published when the key was given (<c>wss://</c> URLs).</summary>
    public IReadOnlyList<string> Relays { get; }

    /// <summary>
    /// Reads an integration's secret key. Throws <see cref="InvalidKeyException"/> for anything
    /// else: another prefix (a link holding its parts included), a part too many or missing, a part
    /// that is not canonical base64url, a length other than 32 bytes, a team key off the curve, an
    /// invalid relay list. The message never holds the key.
    /// </summary>
    public static IntegrationKey Parse(string? text)
    {
        if (text is null || !text.StartsWith(Protocol.IntegrationKeyPrefix, StringComparison.Ordinal))
        {
            throw new InvalidKeyException();
        }
        var parts = text[Protocol.IntegrationKeyPrefix.Length..].Split('.');
        if (parts.Length < 3)
        {
            throw new InvalidKeyException("Integration key cut short");
        }
        if (parts.Length > 3)
        {
            throw new InvalidKeyException();
        }
        var team = Base64.FromBase64Url(parts[0]);
        var key = Base64.FromBase64Url(parts[1]);
        var relays = Internal.Relays.DecodeList(parts[2]);
        if (team?.Length != Protocol.KeyBytes || key?.Length != Protocol.KeyBytes || relays is null)
        {
            throw new InvalidKeyException("Damaged integration key");
        }
        var teamPublicKey = Text.Hex(team);
        if (!Secp256k1.IsPublicKey(teamPublicKey))
        {
            throw new InvalidKeyException("Damaged integration key");
        }
        return new IntegrationKey(teamPublicKey, key, relays);
    }

    /// <summary>The team and the relays: never the key.</summary>
    public override string ToString() => $"IntegrationKey {{ team = {TeamPublicKey}, relays = [{string.Join(", ", Relays)}] }}";
}
