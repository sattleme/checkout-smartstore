using System.Collections.Generic;
using System.Linq;

namespace Sattleme.Checkout.Internal.Nostr;

/// <summary>A NIP-01 subscription filter, as far as the package uses it.</summary>
internal sealed record RelayFilter
{
    public IReadOnlyList<int>? Kinds { get; init; }

    public IReadOnlyList<string>? Authors { get; init; }

    /// <summary>The <c>#d</c> tags.</summary>
    public IReadOnlyList<string>? D { get; init; }

    /// <summary>The <c>#o</c> tags: a witness's order and address tags (SPEC.md §31, "Witnesses").</summary>
    public IReadOnlyList<string>? O { get; init; }

    public long? Since { get; init; }

    public long? Until { get; init; }

    public int? Limit { get; init; }

    public JsonObject ToJson()
    {
        var json = new JsonObject();
        if (Kinds is not null)
        {
            json.Set("kinds", new JsonArray(Kinds.Select(kind => (Json)Json.Of(kind))));
        }
        if (Authors is not null)
        {
            json.Set("authors", new JsonArray(Authors.Select(author => (Json)Json.Of(author))));
        }
        if (D is not null)
        {
            json.Set("#d", new JsonArray(D.Select(d => (Json)Json.Of(d))));
        }
        if (O is not null)
        {
            json.Set("#o", new JsonArray(O.Select(o => (Json)Json.Of(o))));
        }
        if (Since is not null)
        {
            json.Set("since", Since.Value);
        }
        if (Until is not null)
        {
            json.Set("until", Until.Value);
        }
        if (Limit is not null)
        {
            json.Set("limit", Limit.Value);
        }
        return json;
    }
}
