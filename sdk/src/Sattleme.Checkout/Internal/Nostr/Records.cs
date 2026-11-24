using System;
using System.Collections.Generic;
using Sattleme.Checkout.Internal.Crypto;

namespace Sattleme.Checkout.Internal.Nostr;

/// <summary>A record of a newer format than this package reads: ignored, never written over (SPEC.md §12).</summary>
internal sealed class NewerRecordException : Exception
{
    public NewerRecordException()
        : base("Record of a newer format")
    {
    }
}

/// <summary>A record a relay sent, opened: its value, or authentic but of a newer format.</summary>
internal sealed record OpenedRecord<T>(bool Newer, NostrEvent Event, T? Value);

/// <summary>The newest of the records relays sent, and the date of the newest of a newer format.</summary>
internal sealed record NewestRecord<T>(NostrEvent? Event, T? Value, long? NewestUnreadable);

/// <summary>
/// Sealed records, as the merchant's app writes and reads them (SPEC.md §12): one addressable event
/// of kind 30078 (NIP-78), an opaque <c>d</c> tag, NIP-44 v2 content whose plaintext
/// <c>{"t": &lt;type&gt;, "v": &lt;value&gt;}</c> is padded with spaces to a multiple of 256 bytes.
/// </summary>
internal static class Records
{
    /// <summary>The signed event of a record. Encryption and signature are randomized.</summary>
    public static NostrEvent Seal(
        string type,
        Json value,
        byte[] signer,
        byte[] encryptionKey,
        string address,
        long createdAt,
        RandomSource random,
        IReadOnlyList<IReadOnlyList<string>>? tags = null)
    {
        var text = new JsonObject().Set("t", type).Set("v", value).Write();
        var length = Text.Utf8Length(text);
        // Spaces after the JSON: ignored when it is parsed.
        var padding = ((length + Protocol.RecordBlockBytes - 1) / Protocol.RecordBlockBytes * Protocol.RecordBlockBytes) - length;
        var padded = text + new string(' ', padding);
        var allTags = new List<IReadOnlyList<string>> { new[] { "d", address } };
        if (tags is not null)
        {
            allTags.AddRange(tags);
        }
        var content = Nip44.Encrypt(padded, encryptionKey, random);
        return Events.Sign(createdAt, Protocol.RecordKind, allTags, content, signer, random);
    }

    /// <summary>
    /// What a relay sent, if it is a record of <paramref name="type"/> at <paramref name="address"/>
    /// by <paramref name="author"/> sealed with <paramref name="encryptionKey"/> (SPEC.md §12,
    /// "Opening"): kind, author, content and <c>d</c> tag; then a content that opens with the key and
    /// holds a value of the type; then the event's checks. <paramref name="parse"/> validates the
    /// value (null: invalid) and may throw <see cref="NewerRecordException"/>. Anything else is null:
    /// a relay is never trusted.
    /// </summary>
    public static OpenedRecord<T>? Open<T>(
        Json? value,
        string type,
        byte[] encryptionKey,
        string address,
        string author,
        Func<Json, NostrEvent, T?> parse)
        where T : class
    {
        if (value is not JsonObject claimed
            || !JsonRead.IsNumber(claimed["kind"], Protocol.RecordKind)
            || JsonRead.String(claimed["pubkey"]) != author
            || JsonRead.String(claimed["content"]) is not { } content
            || claimed["tags"] is not JsonArray tags
            || !HasTag(tags, "d", address))
        {
            return null;
        }
        Json? payload = null;
        var newer = false;
        try
        {
            payload = ReadContent(content, type, encryptionKey);
        }
        catch (NewerRecordException)
        {
            newer = true;
        }
        if (!newer && payload is null)
        {
            return null;
        }
        var checkedEvent = Events.Valid(value);
        if (checkedEvent is null)
        {
            return null;
        }
        if (newer)
        {
            return new OpenedRecord<T>(true, checkedEvent, null);
        }
        try
        {
            var parsed = parse(payload!, checkedEvent);
            return parsed is null ? null : new OpenedRecord<T>(false, checkedEvent, parsed);
        }
        catch (NewerRecordException)
        {
            return new OpenedRecord<T>(true, checkedEvent, null);
        }
    }

    /// <summary>Whether <paramref name="tags"/> holds a tag <c>[name, value, …]</c> (any of them).</summary>
    public static bool HasTag(JsonArray tags, string name, string value)
    {
        foreach (var tag in tags.Items)
        {
            if (tag is JsonArray { Count: >= 2 } items
                && JsonRead.String(items[0]) == name
                && JsonRead.String(items[1]) == value)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The value inside a record's content; null when it does not open or is of another type.
    /// Throws <see cref="NewerRecordException"/> for a content of a newer NIP-44 version.
    /// </summary>
    private static Json? ReadContent(string content, string type, byte[] encryptionKey)
    {
        string text;
        try
        {
            text = Nip44.Decrypt(content, encryptionKey);
        }
        catch (UnknownNip44VersionException)
        {
            throw new NewerRecordException();
        }
        catch (FormatException)
        {
            return null;
        }
        if (Json.Parse(text) is not JsonObject envelope || !envelope.Has("v"))
        {
            return null;
        }
        return JsonRead.String(envelope["t"]) == type ? envelope["v"] : null;
    }

    /// <summary>
    /// What a read of sealed records keeps of each event (SPEC.md §13, "Limits": the event, when it
    /// opens): an event of kind 30078 whose content decrypts with <paramref name="keyOf"/> of its
    /// first <c>d</c> tag (hex32, else null) and its author, or is of a newer NIP-44 version, and that
    /// checks out (§11), its signature included: anybody may send a copy of a record under an id of
    /// its own, its content and signature left as they are. Checked once per id and signature,
    /// whatever relays send copies. Its value is not checked here: the read opens what it keeps. Of
    /// the event, only its NIP-01 members are kept, never the relay's object: a relay may add any
    /// member to an event that checks out (its id and its signature cover none of it).
    /// </summary>
    public static Func<Json, Json?> ThatOpen(Func<string?, string, byte[]?> keyOf)
    {
        var verified = new Dictionary<string, bool>(StringComparer.Ordinal);
        return value =>
        {
            if (value is not JsonObject obj
                || !JsonRead.IsNumber(obj["kind"], Protocol.RecordKind)
                || JsonRead.String(obj["content"]) is not { } content
                || JsonRead.String(obj["pubkey"]) is not { } pubkey)
            {
                return null;
            }
            var key = keyOf(DTagOf(value), pubkey);
            if (key is null)
            {
                return null;
            }
            try
            {
                Nip44.Decrypt(content, key);
            }
            catch (UnknownNip44VersionException)
            {
                // Of a newer format: kept when its event checks out.
            }
            catch (FormatException)
            {
                return null;
            }
            var withId = Events.WithValidId(value);
            if (withId is null)
            {
                return null;
            }
            var copy = withId.Id + "/" + withId.Sig;
            bool valid;
            lock (verified)
            {
                if (!verified.TryGetValue(copy, out valid))
                {
                    valid = Events.SignatureChecks(withId.Id, withId.Pubkey, withId.Sig);
                    verified[copy] = valid;
                }
            }
            return valid ? withId.ToJson() : null;
        };
    }

    /// <summary>The <c>d</c> tag of a value a relay sent, when it is hex32 (its first <c>d</c> tag); null otherwise.</summary>
    public static string? DTagOf(Json? value)
    {
        if (value is not JsonObject { } obj || obj["tags"] is not JsonArray tags)
        {
            return null;
        }
        foreach (var tag in tags.Items)
        {
            if (tag is JsonArray items && items.Count > 0 && JsonRead.String(items[0]) == "d")
            {
                var d = items.Count > 1 ? JsonRead.String(items[1]) : null;
                return Text.IsHex32(d) ? d : null;
            }
        }
        return null;
    }

    /// <summary>Whether <paramref name="aCreatedAt"/>/<paramref name="aId"/> replaces <paramref name="bCreatedAt"/>/<paramref name="bId"/> as relays keep them: a later date, then the lowest id.</summary>
    public static bool Replaces(long aCreatedAt, string aId, long bCreatedAt, string bId) =>
        aCreatedAt > bCreatedAt || (aCreatedAt == bCreatedAt && string.CompareOrdinal(aId, bId) < 0);

    /// <summary>
    /// The answers of a read: every copy opened with <paramref name="open"/>, once per event id that
    /// checked out (a copy with the id of one already checked is the same event); the newest by
    /// <c>created_at</c> wins, on a tie the lowest event id (the one relays keep, NIP-01).
    /// </summary>
    public static NewestRecord<T> Newest<T>(IEnumerable<IReadOnlyList<Json>> answers, Func<Json, OpenedRecord<T>?> open)
        where T : class
    {
        var checkedIds = new HashSet<string>(StringComparer.Ordinal);
        NostrEvent? newestEvent = null;
        T? newestValue = null;
        long? newestUnreadable = null;
        foreach (var events in answers)
        {
            foreach (var value in events)
            {
                if (value is JsonObject obj && JsonRead.String(obj["id"]) is { } id && checkedIds.Contains(id))
                {
                    continue;
                }
                var copy = open(value);
                if (copy is null)
                {
                    continue;
                }
                checkedIds.Add(copy.Event.Id);
                var createdAt = copy.Event.CreatedAt;
                if (copy.Newer)
                {
                    newestUnreadable = Math.Max(newestUnreadable ?? -1, createdAt);
                    continue;
                }
                if (newestEvent is null || Replaces(createdAt, copy.Event.Id, newestEvent.CreatedAt, newestEvent.Id))
                {
                    newestEvent = copy.Event;
                    newestValue = copy.Value;
                }
            }
        }
        return new NewestRecord<T>(newestEvent, newestValue, newestUnreadable);
    }
}
