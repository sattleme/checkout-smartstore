using System;
using System.Collections.Generic;
using System.Linq;
using Sattleme.Checkout.Internal.Crypto;

namespace Sattleme.Checkout.Internal.Nostr;

/// <summary>A signed Nostr event (NIP-01), checked (SPEC.md §11).</summary>
internal sealed class NostrEvent
{
    public NostrEvent(string id, string pubkey, long createdAt, double kind, IReadOnlyList<IReadOnlyList<string>> tags, string content, string sig)
    {
        Id = id;
        Pubkey = pubkey;
        CreatedAt = createdAt;
        Kind = kind;
        Tags = tags;
        Content = content;
        Sig = sig;
    }

    /// <summary>Hex SHA-256 of the serialized event.</summary>
    public string Id { get; }

    /// <summary>Hex x-only public key of the author.</summary>
    public string Pubkey { get; }

    /// <summary>Unix seconds: a safe integer.</summary>
    public long CreatedAt { get; }

    /// <summary>An integer, as a double: any integer JSON may write.</summary>
    public double Kind { get; }

    public IReadOnlyList<IReadOnlyList<string>> Tags { get; }

    public string Content { get; }

    /// <summary>Hex BIP-340 signature of <see cref="Id"/>.</summary>
    public string Sig { get; }

    /// <summary>The value of its first tag named <paramref name="name"/>, if any.</summary>
    public string? TagValue(string name) =>
        Tags.FirstOrDefault(tag => tag.Count > 0 && tag[0] == name) is { Count: > 1 } tag ? tag[1] : null;

    /// <summary>The event as JSON, for a relay (NIP-01).</summary>
    public JsonObject ToJson()
    {
        var tags = new JsonArray();
        foreach (var tag in Tags)
        {
            tags.Add(new JsonArray(tag.Select(item => (Json)Json.Of(item))));
        }
        return new JsonObject()
            .Set("id", Id)
            .Set("pubkey", Pubkey)
            .Set("created_at", CreatedAt)
            .Set("kind", new JsonNumber(Kind))
            .Set("tags", tags)
            .Set("content", Content)
            .Set("sig", Sig);
    }

    public override string ToString() => ToJson().Write();
}

/// <summary>Nostr events: their ids, signatures and checks (SPEC.md §11). A relay is never trusted.</summary>
internal static class Events
{
    /// <summary>
    /// What an event's id hashes (SPEC.md §11): <c>[0, pubkey, created_at, kind, tags, content]</c>
    /// without whitespace, as JavaScript's JSON.stringify writes it: its numbers as integers whatever
    /// their spelling in what the relay sent; its strings with <c>\"</c> and <c>\\</c>, the escapes
    /// <c>\b \t \n \f \r</c>, any other character below U+0020 as <c>\u</c> and four lowercase
    /// hex digits, every other character as it is.
    /// </summary>
    public static string Serialize(string pubkey, long createdAt, double kind, IReadOnlyList<IReadOnlyList<string>> tags, string content)
    {
        var tagsJson = new JsonArray();
        foreach (var tag in tags)
        {
            tagsJson.Add(new JsonArray(tag.Select(item => (Json)Json.Of(item))));
        }
        return new JsonArray()
            .Add(Json.Of(0))
            .Add(Json.Of(pubkey))
            .Add(Json.Of(createdAt))
            .Add(new JsonNumber(kind))
            .Add(tagsJson)
            .Add(Json.Of(content))
            .Write();
    }

    /// <summary>The event id: SHA-256 of the UTF-8 of <see cref="Serialize"/>.</summary>
    public static byte[] Hash(string pubkey, long createdAt, double kind, IReadOnlyList<IReadOnlyList<string>> tags, string content) =>
        Hashes.Sha256(Text.Utf8(Serialize(pubkey, createdAt, kind, tags, content)));

    /// <summary>The id (hex) an event of these members has: what <see cref="WithValidId"/> checks.</summary>
    public static string Id(string pubkey, long createdAt, double kind, IReadOnlyList<IReadOnlyList<string>> tags, string content) =>
        Text.Hex(Hash(pubkey, createdAt, kind, tags, content));

    /// <summary>Signs a template (BIP-340, 32 fresh random bytes of auxiliary data).</summary>
    public static NostrEvent Sign(long createdAt, int kind, IReadOnlyList<IReadOnlyList<string>> tags, string content, ReadOnlySpan<byte> secretKey, RandomSource random)
    {
        var pubkey = Secp256k1.PublicKeyOf(secretKey);
        var hash = Hash(pubkey, createdAt, kind, tags, content);
        var sig = Secp256k1.Sign(hash, secretKey, random.Bytes(32));
        return new NostrEvent(Text.Hex(hash), pubkey, createdAt, kind, tags, content, Text.Hex(sig));
    }

    /// <summary>
    /// What a relay sent, when it has the shape of an event and its id is its hash: what is checked
    /// of a record before anything else where anybody may write (SPEC.md §31, "Opening a lease"),
    /// its signature only where it decides. A string of its content or tags holding a lone
    /// surrogate has no UTF-8, which its hash needs: such an event does not check out. Null otherwise.
    /// </summary>
    public static NostrEvent? WithValidId(Json? value)
    {
        if (value is not JsonObject obj)
        {
            return null;
        }
        var id = JsonRead.String(obj["id"]);
        var pubkey = JsonRead.String(obj["pubkey"]);
        var sig = JsonRead.String(obj["sig"]);
        var createdAt = JsonRead.SafeInteger(obj["created_at"]);
        var kind = JsonRead.Number(obj["kind"]);
        var content = JsonRead.String(obj["content"]);
        if (!Text.IsHex32(id) || !Text.IsHex32(pubkey) || !Text.IsHex64(sig) || createdAt is null
            || kind is null || !Numbers.IsInteger(kind.Value) || content is null || !Text.IsWellFormed(content)
            || obj["tags"] is not JsonArray tagsJson)
        {
            return null;
        }
        var tags = new List<IReadOnlyList<string>>(tagsJson.Count);
        foreach (var tagJson in tagsJson.Items)
        {
            if (tagJson is not JsonArray items)
            {
                return null;
            }
            var tag = new List<string>(items.Count);
            foreach (var item in items.Items)
            {
                if (item is not JsonString { Value: var text } || !Text.IsWellFormed(text))
                {
                    return null;
                }
                tag.Add(text);
            }
            tags.Add(tag);
        }
        if (Id(pubkey!, createdAt.Value, kind.Value, tags, content) != id)
        {
            return null;
        }
        return new NostrEvent(id!, pubkey!, createdAt.Value, kind.Value, tags, content, sig!);
    }

    /// <summary>What a relay sent, when it is an event whose id and signature check out (SPEC.md §11).</summary>
    public static NostrEvent? Valid(Json? value)
    {
        var withId = WithValidId(value);
        return withId is not null && SignatureChecks(withId.Id, withId.Pubkey, withId.Sig) ? withId : null;
    }

    /// <summary>Whether the signature <paramref name="sig"/> of the id <paramref name="id"/>, whose id was checked, verifies.</summary>
    public static bool SignatureChecks(string id, string pubkey, string sig)
    {
        SignatureCount.Count();
        return Secp256k1.Verify(sig, id, pubkey);
    }
}

/// <summary>Counts the signatures checked, for the tests of what a hostile relay costs (S4).</summary>
internal static class SignatureCount
{
    private static readonly System.Threading.AsyncLocal<System.Runtime.CompilerServices.StrongBox<int>?> Counter = new();

    /// <summary>Counts the signatures checked from here on, in this flow of execution.</summary>
    public static System.Runtime.CompilerServices.StrongBox<int> Start()
    {
        var box = new System.Runtime.CompilerServices.StrongBox<int>(0);
        Counter.Value = box;
        return box;
    }

    public static void Count()
    {
        var box = Counter.Value;
        if (box is not null)
        {
            System.Threading.Interlocked.Increment(ref box.Value);
        }
    }
}
