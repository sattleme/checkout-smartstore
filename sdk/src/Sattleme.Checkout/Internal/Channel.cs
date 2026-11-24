using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sattleme.Checkout.Internal.Crypto;
using Sattleme.Checkout.Internal.Nostr;
using Sattleme.Checkout.Internal.Onchain;

namespace Sattleme.Checkout.Internal;

/// <summary>
/// The integration's channel, as far as the server uses it (SPEC.md §14): active, or removed. The
/// Lightning address is never shown to the integrator by the app: the package does not return it
/// either. Its <see cref="ToString"/> names neither the registry key nor the address.
/// </summary>
internal sealed class IntegrationChannel
{
    private IntegrationChannel(bool removed, string businessName, string lightningAddress, string registryKey, string registryReader,
        IReadOnlyList<string>? relays, string? checkoutOrigin, int tolerance, BitcoinNode? bitcoinNode, long updatedAt)
    {
        Removed = removed;
        BusinessName = businessName;
        LightningAddress = lightningAddress;
        RegistryKey = registryKey;
        RegistryReader = registryReader;
        Relays = relays;
        CheckoutOrigin = checkoutOrigin;
        Tolerance = tolerance;
        BitcoinNode = bitcoinNode;
        UpdatedAt = updatedAt;
    }

    /// <summary>Whether the merchant removed the integration.</summary>
    public bool Removed { get; }

    public string BusinessName { get; }

    public string LightningAddress { get; }

    /// <summary>Hex, 32 bytes: a secret.</summary>
    [System.Diagnostics.DebuggerBrowsable(System.Diagnostics.DebuggerBrowsableState.Never)]
    public string RegistryKey { get; }

    /// <summary>Hex: the public half of the merchant's reading key.</summary>
    public string RegistryReader { get; }

    /// <summary>The account's relays, when the channel names them.</summary>
    public IReadOnlyList<string>? Relays { get; }

    /// <summary>The merchant's web app for checkout links (<c>checkout.origin</c>), null when the merchant's app gives none or an invalid one.</summary>
    public string? CheckoutOrigin { get; }

    /// <summary>
    /// The merchant's tolerance of on-chain underpayments (<c>onchainTolerance</c>, basis points),
    /// 0 when the channel gives none or an invalid one. An order's lease keeps it; never returned to
    /// the integrator.
    /// </summary>
    public int Tolerance { get; }

    /// <summary>
    /// The merchant's Bitcoin node (<c>bitcoinNode</c>, SPEC.md §31, "The merchant's Bitcoin node"):
    /// the explorer the server reads, with its fallback; null when the channel names none or an
    /// invalid one (the server reads mempool.space). Never returned to the integrator.
    /// </summary>
    public BitcoinNode? BitcoinNode { get; }

    /// <summary>Unix seconds: the record's <c>created_at</c>.</summary>
    public long UpdatedAt { get; }

    public override string ToString() => Removed ? "Channel { removed }" : $"Channel {{ {BusinessName} }}";

    /// <summary>
    /// A channel's value, as the merchant's app reads it, for what the server uses (SPEC.md §14, "A
    /// valid channel value"): version 1, <c>updatedAt</c> = <c>created_at</c>, then removed, or
    /// active with the role of an integration, the business's name, a Lightning address, the
    /// registry and an optional valid relay list, the web app of checkout links when it gives a
    /// valid one (an invalid one gives none, and leaves the channel valid), the merchant's
    /// tolerance (absent or invalid: none, the channel valid), and the merchant's Bitcoin node
    /// (absent or invalid: none, the channel valid). The other fields are the app's own
    /// and not read here, so that a change to them never stops a shop's server. Throws
    /// <see cref="NewerRecordException"/> for a newer version.
    /// </summary>
    public static IntegrationChannel? Parse(Json? value, long createdAt)
    {
        if (value is not JsonObject v)
        {
            return null;
        }
        if (JsonRead.IsNewerVersion(v["version"], Protocol.ChannelVersion))
        {
            throw new NewerRecordException();
        }
        if (!JsonRead.IsNumber(v["version"], Protocol.ChannelVersion) || !JsonRead.IsNumber(v["updatedAt"], createdAt))
        {
            return null;
        }
        var status = JsonRead.String(v["status"]);
        if (status == "removed")
        {
            return new IntegrationChannel(true, "", "", "", "", null, null, 0, null, createdAt);
        }
        if (status != "active" || JsonRead.String(v["role"]) != "integration")
        {
            return null;
        }
        if (v["profile"] is not JsonObject profile)
        {
            return null;
        }
        var businessName = JsonRead.String(profile["businessName"]);
        if (!Checkout.IsBusinessName(businessName))
        {
            return null;
        }
        var lightningAddress = JsonRead.String(profile["lightningAddress"]);
        if (lightningAddress is null || Lnurl.LightningAddressUrl(lightningAddress) is null)
        {
            return null;
        }
        // The reading key must be a point of the curve: every declaration's inside is sealed to it.
        var registryKey = JsonRead.String(v["registryKey"]);
        var registryReader = JsonRead.String(v["registryReader"]);
        if (!Text.IsHex32(registryKey) || !Secp256k1.IsPublicKey(registryReader))
        {
            return null;
        }
        IReadOnlyList<string>? relays = null;
        if (v.Has("relays"))
        {
            relays = Internal.Relays.ParseList(v["relays"]);
            if (relays is null)
            {
                return null;
            }
        }
        return new IntegrationChannel(false, businessName!, lightningAddress, registryKey!, registryReader!, relays,
            Checkout.OriginOf(v["checkout"]),
            // Absent or invalid, none: the channel stays valid (SPEC.md §14).
            Onchain.Tolerance.Of(v["onchainTolerance"]),
            // Absent or invalid, none: the channel stays valid (SPEC.md §14).
            BitcoinNodes.Of(v["bitcoinNode"]),
            createdAt);
    }
}

/// <summary>What the relays of one list hold: the channel, none (with a newer-format flag), or no majority answered.</summary>
internal sealed record ChannelLookup(IntegrationChannel? IntegrationChannel, bool Unreachable, bool Newer);

/// <summary>Reading the integration's channel (SPEC.md §14).</summary>
internal static class Channels
{
    /// <summary>
    /// The newest copy of the integration's channel on <paramref name="relays"/>, signed by its
    /// team, over the connections of <paramref name="pool"/>: every relay at once, read as soon as
    /// a majority of them answered (the app publishes the channel, and its removal, on a majority
    /// of them: any two majorities share a relay). Never fails but for a cancellation.
    /// </summary>
    public static async Task<ChannelLookup> LookupAsync(RelayPool pool, IntegrationKey access, IReadOnlyList<string> relays,
        RandomSource random, Warnings warnings, CancellationToken cancellationToken)
    {
        var (address, encryptionKey) = Derive.ChannelKeys(access.Key);
        // A relay that asks to authenticate (NIP-42) gets a key of this read alone.
        var authKey = random.Bytes(32);
        var keep = Records.ThatOpen((_, author) => author == access.TeamPublicKey ? encryptionKey : null);
        var answers = await pool.QueryRelaysAsync(
            relays,
            new RelayFilter { Kinds = [Protocol.RecordKind], Authors = [access.TeamPublicKey], D = [address] },
            authKey,
            RelayPool.Majority(relays.Count),
            paged: false,
            keep).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (answers.Answered < RelayPool.Majority(relays.Count))
        {
            return new ChannelLookup(null, Unreachable: true, Newer: false);
        }
        var newest = Records.Newest(
            Values(answers),
            value => Records.Open(value, Protocol.ChannelRecordType, encryptionKey, address, access.TeamPublicKey,
                (v, e) => IntegrationChannel.Parse(v, e.CreatedAt)));
        if (newest.NewestUnreadable is { } unreadable && unreadable > (newest.Event?.CreatedAt ?? -1))
        {
            warnings.Warn("the integration's channel has a newer format than this version reads; update the package");
        }
        return newest.Value is not null
            ? new ChannelLookup(newest.Value, Unreachable: false, Newer: false)
            : new ChannelLookup(null, Unreachable: false, Newer: newest.NewestUnreadable is not null);
    }

    /// <summary>
    /// The integration's channel, active, on the account's current relays (SPEC.md §14, "Reading
    /// the channel"): read on the key's relays, then where the channel says the account moved (3
    /// times at most), since only the new list gets its later changes, its removal included. Throws
    /// <see cref="RevokedException"/> when it is removed, <see cref="NetworkException"/> when fewer
    /// than a majority of a list answered, <see cref="ChannelNotFoundException"/> when a majority
    /// of the key's relays answered without it.
    /// </summary>
    public static async Task<IntegrationChannel> ReadAsync(RelayPool pool, IntegrationKey access, RandomSource random, Warnings warnings, CancellationToken cancellationToken)
    {
        var relays = access.Relays;
        var lookup = await LookupAsync(pool, access, relays, random, warnings, cancellationToken).ConfigureAwait(false);
        if (lookup.Unreachable)
        {
            throw new NetworkException("Fewer than a majority of the integration key's relays answered");
        }
        var channel = lookup.IntegrationChannel ?? throw new ChannelNotFoundException(lookup.Newer);
        for (var move = 0; move < Protocol.MaxRelayMoves; move++)
        {
            if (channel.Removed || channel.Relays is null || Internal.Relays.SameList(channel.Relays, relays))
            {
                break;
            }
            var next = channel.Relays;
            lookup = await LookupAsync(pool, access, next, random, warnings, cancellationToken).ConfigureAwait(false);
            if (lookup.Unreachable)
            {
                throw new NetworkException("Fewer than a majority of the merchant's relays answered");
            }
            // Not copied there (yet): the channel read so far stands, and the declarations still go
            // to the account's relays it names.
            if (lookup.IntegrationChannel is null)
            {
                break;
            }
            channel = lookup.IntegrationChannel;
            relays = next;
        }
        return channel.Removed ? throw new RevokedException() : channel;
    }

    /// <summary>The events each relay sent, as JSON.</summary>
    public static IEnumerable<IReadOnlyList<Json>> Values(RelayAnswers answers)
    {
        foreach (var events in answers.Events)
        {
            var list = new List<Json>(events.Count);
            foreach (var kept in events)
            {
                list.Add((Json)kept);
            }
            yield return list;
        }
    }
}
