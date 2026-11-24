using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sattleme.Checkout.Internal.Crypto;
using Sattleme.Checkout.Internal.Nostr;

namespace Sattleme.Checkout.Internal.Onchain;

/// <summary>The keys of the integration's witnesses (SPEC.md §31, "Witnesses"). Secrets, but the author: never logged.</summary>
[DebuggerDisplay("SeenKeys {{ author = {Author} }}")]
internal sealed class SeenKeys(byte[] secretKey, string author, byte[] addressKey, byte[] encryptionKey)
{
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public byte[] SecretKey { get; } = secretKey;

    /// <summary>Hex: the author of every witness.</summary>
    public string Author { get; } = author;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public byte[] AddressKey { get; } = addressKey;

    /// <summary>The channel's encryption key: the witnesses' NIP-44 conversation key.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public byte[] EncryptionKey { get; } = encryptionKey;

    public override string ToString() => $"SeenKeys {{ author = {Author} }}";
}

/// <summary>What a witness says.</summary>
/// <param name="Address">The deposit address.</param>
/// <param name="Txid">The transaction.</param>
/// <param name="Sale">The sale author (hex), or null: at no lease's time.</param>
/// <param name="Sats">What <paramref name="Txid"/> pays <paramref name="Address"/>.</param>
/// <param name="Inputs">The coins it spends, its first 100, in their order.</param>
/// <param name="Replacement">Held for the sale only as a replacement of payments in time: it came after the lease.</param>
/// <param name="ExpiresAt">The <c>expiresAt</c> of the invoice whose lease it came in (or stands for); null for none.</param>
internal sealed record Witness(string Address, string Txid, string? Sale, long Sats, IReadOnlyList<string> Inputs, bool Replacement, long? ExpiresAt);

/// <summary>A witness a relay sent, opened: authentic, of a newer format (its <c>d</c> tag alone known), or its witness.</summary>
internal sealed record OpenedWitness(bool Newer, NostrEvent Event, Witness? Witness, string D);

/// <summary>A witness of the order's sale, as a check uses it: <paramref name="SeenAt"/> its <c>created_at</c>.</summary>
internal sealed record SaleWitness(long Sats, IReadOnlyList<string> Inputs, bool Replacement, long SeenAt);

/// <summary>
/// The witnesses of an invoice: of this invoice (its sale and its <c>expiresAt</c>), of other
/// invoices (another sale, or another <c>expiresAt</c> of its sale), of none, and of a newer format.
/// </summary>
internal sealed class OrderWitnesses
{
    /// <summary>Of this invoice, by txid.</summary>
    public Dictionary<string, SaleWitness> Sale { get; } = new(StringComparer.Ordinal);

    /// <summary>Of other invoices, by txid: what they name counts for nothing here.</summary>
    public Dictionary<string, SaleWitness> Others { get; } = new(StringComparer.Ordinal);

    /// <summary>The txids a witness none names.</summary>
    public HashSet<string> None { get; } = new(StringComparer.Ordinal);

    /// <summary>The witness addresses (<c>d</c> tags) of the witnesses of a newer format.</summary>
    public HashSet<string> Newer { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Witnesses (SPEC.md §31, "Witnesses"): the integration records each transaction it sees paying an
/// address of its lot, and when: during the lease of an order (that order's sale), or at no lease's
/// time (<c>none</c>). So that every reader, the merchant's app included, tells the same order paid.
/// Only the integration writes them (signed by its seen secret, sealed with its channel's key); the
/// merchant's app reads them to attribute deposits and to take paid addresses out of the lot.
/// </summary>
internal static class Witnesses
{
    public static SeenKeys SeenOf(byte[] integrationKey)
    {
        var (secret, author, addressKey) = Derive.SeenKeys(integrationKey);
        return new SeenKeys(secret, author, addressKey, Derive.ChannelKeys(integrationKey).EncryptionKey);
    }

    private static string Mac(byte[] key, string text) => Text.Hex(Hashes.HmacSha256(key, Text.Utf8(text)));

    /// <summary>Where the witness of <paramref name="txid"/> paying <paramref name="address"/> for <paramref name="sale"/> (a sale author, or null: none) is.</summary>
    public static string WitnessAddress(byte[] addressKey, string address, string txid, string? sale) =>
        Mac(addressKey, $"{address}/{txid}/{sale ?? Protocol.NoSale}");

    /// <summary>The <c>o</c> tag of the witnesses of <paramref name="address"/> for <paramref name="sale"/> (null: none): how a check reads them.</summary>
    public static string OrderTag(byte[] addressKey, string address, string? sale) =>
        Mac(addressKey, $"order/{address}/{sale ?? Protocol.NoSale}");

    /// <summary>The address tag of <paramref name="address"/>: a witness's second <c>o</c> tag, which finds every witness of the address, whatever its sale.</summary>
    public static string AddressTag(byte[] addressKey, string address) => Mac(addressKey, $"address/{address}");

    /// <summary>A coin, as a witness writes it: <c>&lt;txid&gt;:&lt;vout&gt;</c>, the vout a u32 in decimal without leading zeros.</summary>
    public static bool IsCoin(string? value)
    {
        if (value is null || value.Length < 66 || value[64] != ':' || !Text.IsHex32(value[..64]))
        {
            return false;
        }
        var vout = value[65..];
        if (!Text.IsDigits(vout, 1, 10) || (vout.Length > 1 && vout[0] == '0'))
        {
            return false;
        }
        return long.Parse(vout, System.Globalization.CultureInfo.InvariantCulture) <= 4_294_967_295;
    }

    /// <summary>The signed event of a witness, <c>created_at</c> when it was seen.</summary>
    public static NostrEvent Event(SeenKeys seen, Witness witness, long createdAt, RandomSource random)
    {
        var value = new JsonObject()
            .Set("version", Protocol.WitnessVersion)
            .Set("address", witness.Address)
            .Set("txid", witness.Txid)
            .Set("sale", Json.OfNullable(witness.Sale))
            .Set("sats", witness.Sats)
            .Set("inputs", new JsonArray(witness.Inputs.Take(Protocol.MaxWitnessInputs).Select(coin => (Json)Json.Of(coin))))
            .Set("replacement", witness.Replacement)
            .Set("expiresAt", witness.Sale is null ? Json.Null : Json.OfNullable(witness.ExpiresAt));
        return Records.Seal(
            Protocol.WitnessRecordType,
            value,
            seen.SecretKey,
            seen.EncryptionKey,
            WitnessAddress(seen.AddressKey, witness.Address, witness.Txid, witness.Sale),
            createdAt,
            random,
            [
                [Protocol.OrderTag, OrderTag(seen.AddressKey, witness.Address, witness.Sale)],
                [Protocol.OrderTag, AddressTag(seen.AddressKey, witness.Address)],
            ]);
    }

    /// <summary>
    /// A witness's value, checked against its event's tags: its <c>d</c> tag is the witness address
    /// of what it names, an <c>o</c> tag its order tag and an <c>o</c> tag its address's tag. Null
    /// when it is not valid; throws <see cref="NewerRecordException"/> for a newer version.
    /// </summary>
    private static Witness? Parse(Json value, NostrEvent @event, byte[] addressKey)
    {
        if (value is not JsonObject v)
        {
            return null;
        }
        if (JsonRead.IsNewerVersion(v["version"], Protocol.WitnessVersion))
        {
            throw new NewerRecordException();
        }
        if (!JsonRead.IsNumber(v["version"], Protocol.WitnessVersion))
        {
            return null;
        }
        var address = JsonRead.String(v["address"]);
        var txid = JsonRead.String(v["txid"]);
        if (!Address.IsDepositAddress(address) || !Text.IsHex32(txid))
        {
            return null;
        }
        string? sale = null;
        if (!JsonRead.IsNull(v["sale"]))
        {
            sale = JsonRead.String(v["sale"]);
            if (!Text.IsHex32(sale))
            {
                return null;
            }
        }
        if (JsonRead.SafeInteger(v["sats"]) is not { } sats || sats < 1 || sats > Protocol.MaxSats)
        {
            return null;
        }
        if (v["inputs"] is not JsonArray inputsJson || inputsJson.Count > Protocol.MaxWitnessInputs)
        {
            return null;
        }
        var inputs = new List<string>(inputsJson.Count);
        foreach (var item in inputsJson.Items)
        {
            var coin = JsonRead.String(item);
            if (!IsCoin(coin) || inputs.Contains(coin!))
            {
                return null;
            }
            inputs.Add(coin!);
        }
        if (JsonRead.Boolean(v["replacement"]) is not { } replacement || (sale is null && replacement))
        {
            return null;
        }
        // Null for none, the invoice's end for a sale; absent, it is no witness.
        long? expiresAt = null;
        if (sale is null)
        {
            if (!JsonRead.IsNull(v["expiresAt"]))
            {
                return null;
            }
        }
        else
        {
            expiresAt = JsonRead.SafeInteger(v["expiresAt"]);
            if (expiresAt is null)
            {
                return null;
            }
        }
        if (@event.TagValue("d") != WitnessAddress(addressKey, address!, txid!, sale)
            || !HasOrderTag(@event, OrderTag(addressKey, address!, sale))
            || !HasOrderTag(@event, AddressTag(addressKey, address!)))
        {
            return null;
        }
        return new Witness(address!, txid!, sale, sats, inputs, replacement, expiresAt);
    }

    private static bool HasOrderTag(NostrEvent @event, string tag) =>
        @event.Tags.Any(t => t.Count > 1 && t[0] == Protocol.OrderTag && t[1] == tag);

    /// <summary>What a relay sent, if it is a witness of this integration; null otherwise.</summary>
    public static OpenedWitness? Open(Json? value, SeenKeys seen)
    {
        if (Records.DTagOf(value) is not { } d)
        {
            return null;
        }
        var opened = Records.Open(value, Protocol.WitnessRecordType, seen.EncryptionKey, d, seen.Author,
            (v, @event) => Parse(v, @event, seen.AddressKey));
        return opened is null ? null : new OpenedWitness(opened.Newer, opened.Event, opened.Value, d);
    }

    /// <summary>The <c>o</c> tags of the read of an invoice's witnesses: of the sale, of none, of the address.</summary>
    private static IReadOnlyList<string> ReadTags(SeenKeys seen, string address, string saleAuthor) =>
    [
        OrderTag(seen.AddressKey, address, saleAuthor),
        OrderTag(seen.AddressKey, address, null),
        AddressTag(seen.AddressKey, address),
    ];

    /// <summary>
    /// The witnesses of the invoice with the address <paramref name="address"/> and the end
    /// <paramref name="expiresAt"/>, for the sale <paramref name="saleAuthor"/>: read on the
    /// merchant's <paramref name="relays"/> by their order tags (of the sale and of none) and the
    /// address tag (every sale's), as soon as a majority answered (with
    /// <paramref name="everyRelay"/>, on every relay), with a key of this read alone. Throws
    /// <see cref="NetworkException"/> when fewer than a majority answered.
    /// </summary>
    public static async Task<OrderWitnesses> ReadOrderWitnessesAsync(RelayPool pool, SeenKeys seen, string address, string saleAuthor, long expiresAt,
        IReadOnlyList<string> relays, RandomSource random, bool everyRelay, CancellationToken cancellationToken)
    {
        var needed = RelayPool.Majority(relays.Count);
        var answers = await pool.QueryRelaysAsync(
            relays,
            new RelayFilter { Kinds = [Protocol.RecordKind], Authors = [seen.Author], O = ReadTags(seen, address, saleAuthor) },
            random.Bytes(32),
            needed,
            paged: false,
            Records.ThatOpen((_, author) => author == seen.Author ? seen.EncryptionKey : null),
            everyRelay).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Answered: the relays whose read ended; one still reading on every relay sent events, no answer.
        if (answers.Answered < needed)
        {
            throw new NetworkException("Fewer than a majority of the merchant's relays answered");
        }
        return OrderWitnessesOf(answers.Events.SelectMany(events => events.Cast<Json>()), seen, address, saleAuthor, expiresAt);
    }

    /// <summary>
    /// The witnesses of the invoice with the address <paramref name="address"/> and the end
    /// <paramref name="expiresAt"/>, for the sale <paramref name="saleAuthor"/>, from what the relays
    /// sent to its read: of each witness address, the newest event that opens (one of a newer format,
    /// its event checked, is kept as such). A witness that holds none of the read's <c>o</c> tags, or
    /// whose <c>address</c> is not <paramref name="address"/>, is of no invoice of this read and is
    /// ignored, of a newer format or not: only a relay that does not keep to the filter sends one.
    /// </summary>
    public static OrderWitnesses OrderWitnessesOf(IEnumerable<Json> events, SeenKeys seen, string address, string saleAuthor, long expiresAt)
    {
        var tags = new HashSet<string>(ReadTags(seen, address, saleAuthor), StringComparer.Ordinal);
        var newest = new Dictionary<string, OpenedWitness>(StringComparer.Ordinal);
        var newestOrder = new List<string>();
        var checkedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in events)
        {
            if (value is JsonObject o && JsonRead.String(o["id"]) is { } id && checkedIds.Contains(id))
            {
                continue;
            }
            var opened = Open(value, seen);
            if (opened is null)
            {
                continue;
            }
            checkedIds.Add(opened.Event.Id);
            if (!opened.Event.Tags.Any(tag => tag.Count > 1 && tag[0] == Protocol.OrderTag && tags.Contains(tag[1])))
            {
                continue;
            }
            if (!opened.Newer && opened.Witness!.Address != address)
            {
                continue;
            }
            var d = opened.Event.TagValue("d")!;
            if (!newest.TryGetValue(d, out var known))
            {
                newestOrder.Add(d);
                newest[d] = opened;
            }
            else if (Records.Replaces(opened.Event.CreatedAt, opened.Event.Id, known.Event.CreatedAt, known.Event.Id))
            {
                newest[d] = opened;
            }
        }
        var witnesses = new OrderWitnesses();
        foreach (var d in newestOrder)
        {
            var opened = newest[d];
            if (opened.Newer)
            {
                witnesses.Newer.Add(d);
                continue;
            }
            var witness = opened.Witness!;
            var of = new SaleWitness(witness.Sats, witness.Inputs, witness.Replacement, opened.Event.CreatedAt);
            if (witness.Sale is null)
            {
                witnesses.None.Add(witness.Txid);
            }
            else if (witness.Sale == saleAuthor && witness.ExpiresAt == expiresAt)
            {
                witnesses.Sale[witness.Txid] = of;
            }
            else
            {
                witnesses.Others[witness.Txid] = of;
            }
        }
        return witnesses;
    }

    /// <summary>
    /// The addresses of <paramref name="byTag"/> (address tags, to their address) that
    /// <paramref name="value"/>, as a relay sent it, names (SPEC.md §31, "Leasing an address", step
    /// 3): an event of the seen author that checks out (§11) names each address whose address tag it
    /// holds as an <c>o</c> tag, whatever its content (one that does not open as a witness, or of a
    /// newer format, names it all the same); anything else names none. <paramref name="signatureChecks"/>
    /// checks a signature (the read checks each id and signature once).
    /// </summary>
    public static List<string> NamedBy(Json? value, string seenAuthor, IReadOnlyDictionary<string, string> byTag, Func<NostrEvent, bool> signatureChecks)
    {
        var found = new List<string>();
        if (value is not JsonObject o || JsonRead.String(o["pubkey"]) != seenAuthor || o["tags"] is not JsonArray tagsJson)
        {
            return found;
        }
        foreach (var tag in tagsJson.Items)
        {
            if (tag is JsonArray { Count: > 1 } items && JsonRead.String(items[0]) == Protocol.OrderTag
                && JsonRead.String(items[1]) is { } content && byTag.TryGetValue(content, out var address) && !found.Contains(address))
            {
                found.Add(address);
            }
        }
        // The cheap checks first: only an event that would name an address has its id and signature checked.
        if (found.Count == 0 || Events.WithValidId(value) is not { } withId || !signatureChecks(withId))
        {
            return [];
        }
        return found;
    }

    /// <summary>
    /// The addresses of <paramref name="addresses"/> a witness names (SPEC.md §31, "Leasing an
    /// address", step 2): read on the merchant's <paramref name="relays"/> by their address tags, not
    /// paged, as soon as a majority answered, with a key of this read alone; each event as
    /// <see cref="NamedBy"/> says. Throws <see cref="NetworkException"/> when fewer than a majority
    /// answered.
    /// </summary>
    public static async Task<HashSet<string>> ReadNamedAddressesAsync(RelayPool pool, SeenKeys seen, IReadOnlyList<string> addresses,
        IReadOnlyList<string> relays, RandomSource random, CancellationToken cancellationToken)
    {
        var named = new HashSet<string>(StringComparer.Ordinal);
        if (addresses.Count == 0)
        {
            return named;
        }
        var byTag = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var address in addresses)
        {
            byTag[AddressTag(seen.AddressKey, address)] = address;
        }
        // Whether each event (its id and signature) checks out: once, whatever relays send copies.
        var checksOut = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool SignatureChecks(NostrEvent withId)
        {
            var copy = withId.Id + "/" + withId.Sig;
            lock (checksOut)
            {
                if (!checksOut.TryGetValue(copy, out var valid))
                {
                    valid = Events.SignatureChecks(withId.Id, withId.Pubkey, withId.Sig);
                    checksOut[copy] = valid;
                }
                return valid;
            }
        }
        var needed = RelayPool.Majority(relays.Count);
        var answers = await pool.QueryRelaysAsync(
            relays,
            new RelayFilter { Kinds = [Protocol.RecordKind], Authors = [seen.Author], O = [.. byTag.Keys] },
            random.Bytes(32),
            needed,
            paged: false,
            // Of each event, only the addresses it names: nothing else is kept.
            value => NamedBy(value, seen.Author, byTag, SignatureChecks) is { Count: > 0 } found ? found : null).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (answers.Answered < needed)
        {
            throw new NetworkException("Fewer than a majority of the merchant's relays answered");
        }
        foreach (var events in answers.Events)
        {
            foreach (var found in events)
            {
                named.UnionWith((List<string>)found);
            }
        }
        return named;
    }

    /// <summary>
    /// Publishes a witness on the merchant's <paramref name="relays"/>, signed and authenticated with
    /// the seen secret: completes once a majority took it, else throws <see cref="NetworkException"/>.
    /// </summary>
    public static async Task PublishAsync(RelayPool pool, SeenKeys seen, Witness witness, IReadOnlyList<string> relays, long createdAt,
        RandomSource random, CancellationToken cancellationToken)
    {
        var @event = Event(seen, witness, createdAt, random);
        try
        {
            await pool.PublishToQuorumAsync(relays, @event, seen.SecretKey, RelayPool.Majority(relays.Count)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NetworkException("Fewer than a majority of the merchant's relays took the witness of a payment", error);
        }
    }
}
