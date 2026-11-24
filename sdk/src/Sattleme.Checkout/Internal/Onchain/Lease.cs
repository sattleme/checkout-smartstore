using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sattleme.Checkout.Internal.Crypto;
using Sattleme.Checkout.Internal.Lightning;
using Sattleme.Checkout.Internal.Nostr;

namespace Sattleme.Checkout.Internal.Onchain;

/// <summary>What a lease holds of its order (the lease's JSON, inside).</summary>
/// <param name="Address">The deposit address.</param>
/// <param name="Sats">The amount.</param>
/// <param name="Invoice">The order's Lightning invoice, null for an order paid on-chain only.</param>
/// <param name="VerifyUrl">Its verify URL, null with it.</param>
/// <param name="CreatedAt">The Invoice's date (Unix seconds).</param>
/// <param name="ExpiresAt">The Invoice's end (Unix seconds).</param>
/// <param name="Price">The price, as declared.</param>
/// <param name="Note">The label: the note, else the reference.</param>
/// <param name="Tolerance">
/// The order's tolerance of on-chain underpayments (basis points): the channel's when the order was
/// created, which judges its payment for good. 0: none (absent from the lease).
/// </param>
/// <param name="Confirmed">Whether the lease is marked confirmed: false as first published, true once confirmed; null when absent (not confirmed either).</param>
internal sealed record LeaseTerms(
    string Address,
    long Sats,
    string? Invoice,
    string? VerifyUrl,
    long CreatedAt,
    long ExpiresAt,
    DeclaredPrice Price,
    string Note,
    int Tolerance,
    bool? Confirmed)
{
    /// <summary>Whether the lease's terms make it confirmed: <c>confirmed</c> true, nothing else.</summary>
    public bool IsConfirmed => Confirmed == true;
}

/// <summary>
/// What a read of leases keeps of a record's event, its id checked: what the walk of its address and a
/// lease's confirmation need, and its signature. A lease opens without its signature, so anybody may
/// send copies of one under ids of their own: of each, only these few bytes are kept, never its tags
/// nor its content (SPEC.md §13, Limits).
/// </summary>
internal sealed record LeaseEvent(string Id, string Pubkey, long CreatedAt, string Sig);

/// <summary>What a record's outside says.</summary>
internal enum OutsideKind
{
    Lease,
    Withdrawn,

    /// <summary>Authentic, but of a newer format: an unreadable lease is never written over.</summary>
    Newer,
}

/// <summary>A record's outside, as anyone holding the registry key reads it: a lease's <c>end</c> and <c>sealed</c>.</summary>
internal sealed record LeaseOutside(OutsideKind Kind, long End = 0, string? Sealed = null);

/// <summary>
/// A record of the registry at a deposit address's registry address <see cref="D"/>, by any author:
/// its id checked, its signature not yet (SPEC.md §31, "Opening a lease"). <see cref="Signed"/>
/// checks it, once: whether a copy of the event (its id) a relay sent carries a signature that
/// checks out, so that a relay's copy with a damaged signature never hides the valid one another
/// relay sent. <see cref="AddCopy"/> takes the signature of another relay's copy of it. The
/// signature check is BIP-340's, but for the vectors, which count the checks a walk makes.
/// </summary>
internal sealed class LeaseRecord(LeaseEvent @event, string d, LeaseOutside outside, Func<string, bool>? signatureChecks = null)
{
    private readonly List<string> _signatures = [@event.Sig];
    private readonly Func<string, bool> _signatureChecks = signatureChecks ?? (sig => Events.SignatureChecks(@event.Id, @event.Pubkey, sig));
    private bool? _signed;

    public LeaseEvent Event { get; } = @event;

    public string D { get; } = d;

    /// <summary>What holding needs of its outside (its <c>sealed</c> dropped).</summary>
    public LeaseOutside Outside { get; } = outside;

    /// <summary>Whether a signature of the event checks out (the first that does, once).</summary>
    public bool Signed()
    {
        lock (_signatures)
        {
            _signed ??= _signatures.Any(_signatureChecks);
            return _signed.Value;
        }
    }

    /// <summary>Another relay's copy of the event (its id is its hash) with <paramref name="sig"/>: one more to try.</summary>
    public void AddCopy(string sig)
    {
        lock (_signatures)
        {
            if (_signatures.Contains(sig))
            {
                return;
            }
            _signatures.Add(sig);
            if (_signed == false)
            {
                _signed = null;
            }
        }
    }
}

/// <summary>The records of the registry at the registry addresses of deposit addresses.</summary>
/// <param name="Records">By address.</param>
/// <param name="Answered">Whether a majority of the relays answered: else the read is no answer (SPEC.md §13).</param>
/// <param name="Complete">Whether a majority of the relays read them completely (SPEC.md §13, "Complete reads").</param>
internal sealed record LeaseRead(Dictionary<string, List<LeaseRecord>> Records, bool Answered, bool Complete);

/// <summary>A lease of the sale, opened by the sale (or the merchant): its terms, and its inside as it was sealed.</summary>
internal sealed record OpenedLease(NostrEvent Event, string D, LeaseTerms Terms, JsonObject Inside);

/// <summary>What a record of the sale at a registry address is.</summary>
internal enum SaleLeaseKind
{
    Lease,
    Withdrawn,

    /// <summary>Authentic, unreadable: the newest at its address, it leaves no lease there.</summary>
    Newer,
}

/// <summary>A record of the sale at a registry address: its lease, its withdrawal, or one of a newer format.</summary>
internal sealed record SaleLeaseRecord(SaleLeaseKind Kind, NostrEvent Event, string D, OpenedLease? Lease = null);

/// <summary>
/// Leases (SPEC.md §31, "Leases"): an order holds a deposit address of the lot for the time of its
/// invoice with a record in the merchant's registry, signed by the order's sale, at the address's
/// registry address. Outside, what anyone holding the registry key reads (when the lease ends, or its
/// withdrawal); inside, sealed to the merchant's reading key as a declaration's, the order's terms.
/// Like a declaration, a lease stays: a payment of its address in its time is the order's for good.
/// </summary>
internal static class Leases
{
    /// <summary>
    /// The lease of <c>terms.Address</c> for <paramref name="sale"/>, signed at
    /// <paramref name="createdAt"/>; it ends at expiresAt + 120. Its tolerance follows
    /// <c>taker</c> when it is above 0, and is absent otherwise.
    /// </summary>
    public static NostrEvent Event(Sale sale, LeaseTerms terms, long createdAt, RandomSource random)
    {
        var inside = new JsonObject()
            .Set("version", Protocol.LeaseVersion)
            .Set("address", terms.Address)
            .Set("sats", terms.Sats)
            .Set("invoice", Json.OfNullable(terms.Invoice))
            .Set("verifyUrl", Json.OfNullable(terms.VerifyUrl))
            .Set("createdAt", terms.CreatedAt)
            .Set("expiresAt", terms.ExpiresAt)
            .Set("sale", new JsonObject().Set("key", Text.Hex(sale.Key)).Set("fiat", terms.Price.ToJson()).Set("note", terms.Note))
            .Set("taker", sale.Taker);
        if (terms.Tolerance > 0)
        {
            inside.Set("tolerance", terms.Tolerance);
        }
        if (terms.Confirmed is { } confirmed)
        {
            inside.Set("confirmed", confirmed);
        }
        var outside = new JsonObject()
            .Set("version", Protocol.LeaseVersion)
            .Set("end", terms.ExpiresAt + Protocol.GraceSeconds)
            .Set("sealed", Nip44.Encrypt(inside.Write(), sale.InnerKey, random));
        return SealSaleRecord(sale, terms.Address, outside, createdAt, random);
    }

    /// <summary>
    /// The withdrawal of <paramref name="sale"/>'s lease of <paramref name="address"/>, which
    /// replaces it (same author and <c>d</c> tag): its <c>created_at</c> is strictly after the lease's.
    /// </summary>
    public static NostrEvent WithdrawalEvent(Sale sale, string address, long createdAt, RandomSource random) =>
        SealSaleRecord(sale, address, new JsonObject().Set("version", Protocol.LeaseVersion).Set("withdrawn", true), createdAt, random);

    private static NostrEvent SealSaleRecord(Sale sale, string address, JsonObject value, long createdAt, RandomSource random)
    {
        var d = Address.LeaseAddress(sale.Registry.Key, address);
        return Records.Seal(Protocol.LeaseRecordType, value, sale.SecretKey, Derive.OuterKey(sale.Registry.Key, d, sale.Author), d, createdAt, random);
    }

    /// <summary>A record's outside: a lease, a withdrawal, or null. Throws <see cref="NewerRecordException"/> for a newer version.</summary>
    private static LeaseOutside? ParseOutside(Json? value)
    {
        if (value is not JsonObject v)
        {
            return null;
        }
        if (JsonRead.IsNewerVersion(v["version"], Protocol.LeaseVersion))
        {
            throw new NewerRecordException();
        }
        if (!JsonRead.IsNumber(v["version"], Protocol.LeaseVersion))
        {
            return null;
        }
        if (JsonRead.IsTrue(v["withdrawn"]))
        {
            return new LeaseOutside(OutsideKind.Withdrawn);
        }
        if (JsonRead.SafeInteger(v["end"]) is not { } end || JsonRead.String(v["sealed"]) is not { } sealedText)
        {
            return null;
        }
        return new LeaseOutside(OutsideKind.Lease, end, sealedText);
    }

    /// <summary>
    /// What a relay sent, opened as a record of type <c>registry-onchain</c> at the registry address
    /// <paramref name="d"/>, with the outer key of <paramref name="d"/> and of the event's own author
    /// (SPEC.md §31, "Opening a lease"): its id first; a content of a newer NIP-44 version is no
    /// record here (anybody can write one at <paramref name="d"/>); its signature is left to
    /// <see cref="IsLeased"/>, which checks it only where it decides. Null when it is not one.
    /// </summary>
    public static LeaseRecord? OpenRecord(Json? value, byte[] registryKey, string d)
    {
        if (OpenOutside(value, registryKey, d) is not { } opened)
        {
            return null;
        }
        var (@event, outside) = opened;
        return new LeaseRecord(@event, d, outside.Kind == OutsideKind.Lease ? outside with { Sealed = null } : outside);
    }

    /// <summary><see cref="OpenRecord"/>'s opening, with the whole outside (its <c>sealed</c> included).</summary>
    public static (LeaseEvent Event, LeaseOutside Outside)? OpenOutside(Json? value, byte[] registryKey, string d)
    {
        if (Events.WithValidId(value) is not { } @event || @event.Kind != Protocol.RecordKind || Records.DTagOf(value) != d)
        {
            return null;
        }
        string plaintext;
        try
        {
            plaintext = Nip44.Decrypt(@event.Content, Derive.OuterKey(registryKey, d, @event.Pubkey));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (UnknownNip44VersionException)
        {
            // Only the outer key seals a newer lease: a content of a newer NIP-44 version is none.
            return null;
        }
        if (Json.Parse(plaintext) is not JsonObject envelope || !envelope.Has("v") || JsonRead.String(envelope["t"]) != Protocol.LeaseRecordType)
        {
            return null;
        }
        LeaseOutside? outside;
        try
        {
            outside = ParseOutside(envelope["v"]);
        }
        catch (NewerRecordException)
        {
            outside = new LeaseOutside(OutsideKind.Newer);
        }
        if (outside is null)
        {
            return null;
        }
        return (new LeaseEvent(@event.Id, @event.Pubkey, @event.CreatedAt, @event.Sig), outside);
    }

    /// <summary>Whether <paramref name="record"/> holds its address at <paramref name="t"/> (a lease not ended, or of a newer format).</summary>
    private static bool Holds(LeaseRecord record, long t) =>
        record.Outside.Kind == OutsideKind.Newer
        || (record.Outside.Kind == OutsideKind.Lease && record.Outside.End >= t - Protocol.ClockToleranceSeconds);

    /// <summary>Newest first, as relays keep them: a later <c>created_at</c>, then the lowest id.</summary>
    private static int NewestFirst(LeaseEvent a, LeaseEvent b) =>
        Records.Replaces(a.CreatedAt, a.Id, b.CreatedAt, b.Id) ? -1 : Records.Replaces(b.CreatedAt, b.Id, a.CreatedAt, a.Id) ? 1 : 0;

    /// <summary>
    /// Whether the records of one address lease it at <paramref name="t"/> (SPEC.md §31, "Leases"):
    /// of each author, its records from the newest (a withdrawal replaces its author's lease): one
    /// that holds the address (a lease whose end is at most 300 s before <paramref name="t"/>, another
    /// server's clock may be behind, or of a newer format) leases it; one that holds nothing ends the
    /// author's records, unless an older one of them holds and its signature does not check out. A
    /// record that holds is never checked for its signature: only a holder of the registry key seals
    /// one, and it may lease.
    /// </summary>
    public static bool IsLeased(IReadOnlyList<LeaseRecord> records, long t)
    {
        var byAuthor = new Dictionary<string, List<LeaseRecord>>(StringComparer.Ordinal);
        var authors = new List<string>();
        foreach (var record in records)
        {
            if (!byAuthor.TryGetValue(record.Event.Pubkey, out var list))
            {
                list = [];
                byAuthor[record.Event.Pubkey] = list;
                authors.Add(record.Event.Pubkey);
            }
            list.Add(record);
        }
        var leased = false;
        // Every author walked, as the vectors count the signatures checked.
        foreach (var author in authors)
        {
            var walk = byAuthor[author];
            walk.Sort((a, b) => NewestFirst(a.Event, b.Event));
            for (var index = 0; index < walk.Count; index++)
            {
                var record = walk[index];
                if (Holds(record, t))
                {
                    leased = true;
                    break;
                }
                var olderHolds = false;
                for (var older = index + 1; older < walk.Count; older++)
                {
                    if (Holds(walk[older], t))
                    {
                        olderHolds = true;
                        break;
                    }
                }
                if (!olderHolds || record.Signed())
                {
                    break;
                }
            }
        }
        return leased;
    }

    /// <summary>
    /// Whether the lease <paramref name="own"/> (an event of the sale) is confirmed by
    /// <paramref name="records"/>, a complete read of its address's records (SPEC.md §31, "Leasing an
    /// address", step 3.3): no record of another author holds the address at <paramref name="t"/>,
    /// and <paramref name="own"/> is the newest record of its author there.
    /// </summary>
    public static bool IsConfirmed(IReadOnlyList<LeaseRecord> records, LeaseEvent own, long t)
    {
        if (!records.Any(record => record.Event.Id == own.Id))
        {
            return false;
        }
        if (IsLeased(records.Where(record => record.Event.Pubkey != own.Pubkey).ToList(), t))
        {
            return false;
        }
        return !records.Any(record => record.Event.Pubkey == own.Pubkey && record.Event.Id != own.Id
            && Records.Replaces(record.Event.CreatedAt, record.Event.Id, own.CreatedAt, own.Id));
    }

    /// <summary>
    /// The records of the registry at the registry addresses of <paramref name="addresses"/>, by
    /// address: a complete read on the merchant's <paramref name="relays"/>, since 7200 s before
    /// <paramref name="now"/> (older leases have ended), with a key of this read alone (leases are
    /// signed by sales the integration does not know). Each relay's first copy of an event counts,
    /// its signature tried with the other relays' (<see cref="LeaseRecord.AddCopy"/>).
    /// </summary>
    public static async Task<LeaseRead> ReadAsync(RelayPool pool, byte[] registryKey, IReadOnlyList<string> addresses, IReadOnlyList<string> relays,
        long now, RandomSource random, bool everyRelay, CancellationToken cancellationToken)
    {
        var byD = new Dictionary<string, string>(StringComparer.Ordinal);
        var found = new Dictionary<string, List<LeaseRecord>>(StringComparer.Ordinal);
        foreach (var address in addresses)
        {
            byD[Address.LeaseAddress(registryKey, address)] = address;
            found[address] = [];
        }
        if (addresses.Count == 0)
        {
            return new LeaseRead(found, Answered: true, Complete: true);
        }
        var needed = RelayPool.Majority(relays.Count);
        var read = await pool.QueryCompleteAsync(
            relays,
            new RelayFilter { Kinds = [Protocol.RecordKind], D = [.. byD.Keys], Since = now - Protocol.LeaseLookbackSeconds },
            random.Bytes(32),
            needed,
            // At a lease's address, by any author: the record each event opens to, as it comes (its id
            // checked; a content of a newer NIP-44 version is no record here: anybody writes one).
            value => Records.DTagOf(value) is { } d && byD.ContainsKey(d) ? OpenRecord(value, registryKey, d) : null,
            everyRelay).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Ids that checked out: another copy of one is the same event, its signature one more to try.
        var checkedIds = new Dictionary<string, LeaseRecord>(StringComparer.Ordinal);
        // Every relay's events: those of the relays that answered, and on every relay the pages of
        // those still reading (events, no answer).
        foreach (var records in read.Events)
        {
            // A relay's first copy of each id: at most one signature per relay to try.
            var ofRelay = new HashSet<string>(StringComparer.Ordinal);
            foreach (LeaseRecord record in records)
            {
                var id = record.Event.Id;
                if (!ofRelay.Add(id))
                {
                    continue;
                }
                if (checkedIds.TryGetValue(id, out var known))
                {
                    known.AddCopy(record.Event.Sig);
                    continue;
                }
                checkedIds[id] = record;
                found[byD[record.D]].Add(record);
            }
        }
        return new LeaseRead(found, read.Answered >= needed, read.Complete >= needed);
    }

    /// <summary>
    /// The inside of a lease's outside, opened with the sale's inner key (SPEC.md §31, "Opening it as
    /// the sale"): its terms, null when they are not valid (its tolerance read as 0 when it is absent
    /// or not one). Throws <see cref="NewerRecordException"/> for a newer version.
    /// </summary>
    public static (LeaseTerms Terms, JsonObject Inside)? OpenInside(LeaseOutside outside, string d, Sale sale)
    {
        Json? parsed;
        try
        {
            parsed = Json.Parse(Nip44.Decrypt(outside.Sealed!, sale.InnerKey));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (UnknownNip44VersionException)
        {
            return null;
        }
        if (parsed is not JsonObject v)
        {
            return null;
        }
        if (JsonRead.IsNewerVersion(v["version"], Protocol.LeaseVersion))
        {
            throw new NewerRecordException();
        }
        if (!JsonRead.IsNumber(v["version"], Protocol.LeaseVersion))
        {
            return null;
        }
        var address = JsonRead.String(v["address"]);
        if (!Address.IsDepositAddress(address) || Address.LeaseAddress(sale.Registry.Key, address!) != d)
        {
            return null;
        }
        if (JsonRead.SafeInteger(v["sats"]) is not { } sats || sats < 1 || sats > Protocol.MaxSats)
        {
            return null;
        }
        if (JsonRead.SafeInteger(v["createdAt"]) is not { } createdAt || JsonRead.SafeInteger(v["expiresAt"]) is not { } expiresAt
            || createdAt >= expiresAt || outside.End != expiresAt + Protocol.GraceSeconds)
        {
            return null;
        }
        string? invoice = null;
        string? verifyUrl = null;
        if (JsonRead.IsNull(v["invoice"]))
        {
            if (!JsonRead.IsNull(v["verifyUrl"]))
            {
                return null;
            }
        }
        else
        {
            invoice = JsonRead.String(v["invoice"]);
            verifyUrl = JsonRead.String(v["verifyUrl"]);
            if (invoice is null || verifyUrl is null || Urls.ParseHttps(verifyUrl) is null)
            {
                return null;
            }
            var decoded = Bolt11.Decode(invoice);
            if (decoded is null || decoded.Network != "bc" || decoded.AmountMsat != sats * 1000 || decoded.Timestamp != createdAt
                || decoded.Timestamp + decoded.ExpirySeconds != expiresAt)
            {
                return null;
            }
        }
        if (v["sale"] is not JsonObject saleValue || JsonRead.String(saleValue["key"]) != Text.Hex(sale.Key)
            || JsonRead.String(saleValue["note"]) is not { } note)
        {
            return null;
        }
        if (DeclaredPrice.Parse(saleValue["fiat"], 0) is not { } price)
        {
            return null;
        }
        // Absent, or anything but true: not confirmed.
        bool? confirmed = v.Has("confirmed") ? JsonRead.IsTrue(v["confirmed"]) : null;
        var terms = new LeaseTerms(address!, sats, invoice, verifyUrl, createdAt, expiresAt, price, note,
            // Absent (a lease without tolerance) or invalid: none.
            Tolerance.Of(v["tolerance"]),
            confirmed);
        return (terms, v);
    }

    /// <summary>
    /// What a relay sent, if it is a lease (or a withdrawal) of <paramref name="sale"/>: at a
    /// registry address (its <c>d</c> tag, hex32), signed by the sale, with valid terms inside. Null
    /// otherwise; throws <see cref="NewerRecordException"/> for one of a newer format.
    /// </summary>
    public static SaleLeaseRecord? OpenSaleLease(Json? value, Sale sale)
    {
        if (Records.DTagOf(value) is not { } d || value is not JsonObject o || JsonRead.String(o["pubkey"]) != sale.Author)
        {
            return null;
        }
        var opened = Records.Open(value, Protocol.LeaseRecordType, Derive.OuterKey(sale.Registry.Key, d, sale.Author), d, sale.Author,
            (v, _) => ParseOutside(v));
        if (opened is null)
        {
            return null;
        }
        if (opened.Newer)
        {
            throw new NewerRecordException();
        }
        var outside = opened.Value!;
        if (outside.Kind == OutsideKind.Withdrawn)
        {
            return new SaleLeaseRecord(SaleLeaseKind.Withdrawn, opened.Event, d);
        }
        return OpenInside(outside, d, sale) is { } inside
            ? new SaleLeaseRecord(SaleLeaseKind.Lease, opened.Event, d, new OpenedLease(opened.Event, d, inside.Terms, inside.Inside))
            : null;
    }
}
