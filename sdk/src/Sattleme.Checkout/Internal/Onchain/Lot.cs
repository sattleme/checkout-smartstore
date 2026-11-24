using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sattleme.Checkout.Internal.Crypto;
using Sattleme.Checkout.Internal.Nostr;

namespace Sattleme.Checkout.Internal.Onchain;

/// <summary>The addresses of a lot, as a record's value holds them.</summary>
internal sealed class LotAddresses(IReadOnlyList<string> addresses)
{
    public IReadOnlyList<string> Addresses { get; } = addresses;
}

/// <summary>
/// The integration's lot (SPEC.md §31, "The lot"): deposit addresses of the merchant's wallet, which
/// the merchant's app gives the integration and fills again whenever the merchant opens it. One
/// record signed by the team, sealed with the channel's key, at an address the integration key
/// derives. The package only reads it: it never makes an address, and never writes the lot.
/// </summary>
internal static class Lot
{
    /// <summary>
    /// A lot's value: version 1, <c>updatedAt</c> its event's <c>created_at</c>, 0 to 100 deposit
    /// addresses, none twice. Null when it is not valid; throws <see cref="NewerRecordException"/>
    /// for a newer version.
    /// </summary>
    public static LotAddresses? Parse(Json? value, long createdAt)
    {
        if (value is not JsonObject v)
        {
            return null;
        }
        if (JsonRead.IsNewerVersion(v["version"], Protocol.LotVersion))
        {
            throw new NewerRecordException();
        }
        if (!JsonRead.IsNumber(v["version"], Protocol.LotVersion) || !JsonRead.IsNumber(v["updatedAt"], createdAt))
        {
            return null;
        }
        if (v["addresses"] is not JsonArray addresses || addresses.Count > Protocol.MaxLotAddresses)
        {
            return null;
        }
        var lot = new List<string>(addresses.Count);
        foreach (var item in addresses.Items)
        {
            var address = JsonRead.String(item);
            if (!Address.IsDepositAddress(address) || lot.Contains(address!))
            {
                return null;
            }
            lot.Add(address!);
        }
        return new LotAddresses(lot);
    }

    /// <summary>
    /// The addresses of the integration's lot, read on the merchant's <paramref name="relays"/> as
    /// soon as a majority answered: the newest valid copy. Empty when there is none, or when a copy of
    /// a newer format is as new as it or newer (logged: update the package). The order means
    /// nothing. Throws <see cref="NetworkException"/> when fewer than a majority answered.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReadAsync(RelayPool pool, IntegrationKey access, IReadOnlyList<string> relays,
        RandomSource random, Warnings warnings, CancellationToken cancellationToken)
    {
        var address = Derive.LotAddress(access.Key);
        var (_, encryptionKey) = Derive.ChannelKeys(access.Key);
        var needed = RelayPool.Majority(relays.Count);
        // A relay that asks to authenticate (NIP-42) gets a key of this read alone.
        var answers = await pool.QueryRelaysAsync(
            relays,
            new RelayFilter { Kinds = [Protocol.RecordKind], Authors = [access.TeamPublicKey], D = [address] },
            random.Bytes(32),
            needed,
            paged: false,
            Records.ThatOpen((_, author) => author == access.TeamPublicKey ? encryptionKey : null)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (answers.Answered < needed)
        {
            throw new NetworkException("Fewer than a majority of the merchant's relays answered");
        }
        var newest = Records.Newest(
            Channels.Values(answers),
            value => Records.Open(value, Protocol.LotRecordType, encryptionKey, address, access.TeamPublicKey, (v, e) => Parse(v, e.CreatedAt)));
        if (newest.NewestUnreadable is { } unreadable && unreadable >= (newest.Event?.CreatedAt ?? -1))
        {
            warnings.Warn("the integration's lot of addresses has a newer format than this version reads; update the package (no on-chain payment until then)");
            return [];
        }
        return newest.Value?.Addresses ?? [];
    }
}
