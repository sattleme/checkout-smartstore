namespace Sattleme.Checkout.Internal;

/// <summary>
/// Reading what a relay, a service or a stored invoice sent, as JavaScript's <c>typeof</c> checks
/// read it: each accessor gives the value when it has that type, else null.
/// </summary>
internal static class JsonRead
{
    /// <summary><paramref name="value"/> when it is an object (not null, not an array).</summary>
    public static JsonObject? Object(Json? value) => value as JsonObject;

    /// <summary><paramref name="value"/> when it is an array.</summary>
    public static JsonArray? Array(Json? value) => value as JsonArray;

    /// <summary>The string <paramref name="value"/> holds, else null.</summary>
    public static string? String(Json? value) => (value as JsonString)?.Value;

    /// <summary>The number <paramref name="value"/> holds (never a boolean), else null.</summary>
    public static double? Number(Json? value) => (value as JsonNumber)?.Value;

    /// <summary>The boolean <paramref name="value"/> holds, else null.</summary>
    public static bool? Boolean(Json? value) => (value as JsonBool)?.Value;

    /// <summary>The safe integer <paramref name="value"/> holds (JavaScript's Number.isSafeInteger).</summary>
    public static long? SafeInteger(Json? value) => value is JsonNumber n ? Numbers.SafeInteger(n.Value) : null;

    /// <summary>Whether <paramref name="value"/> is JSON's null (not absent).</summary>
    public static bool IsNull(Json? value) => value is JsonNull;

    /// <summary>Whether <paramref name="value"/> is the boolean true (nothing else).</summary>
    public static bool IsTrue(Json? value) => value is JsonBool { Value: true };

    /// <summary>
    /// Whether <paramref name="value"/> is a number above <paramref name="known"/>: the <c>version</c>
    /// of a newer format (SPEC.md §12, step 5).
    /// </summary>
    public static bool IsNewerVersion(Json? value, int known) => value is JsonNumber n && n.Value > known;

    /// <summary>Whether <paramref name="value"/> is the number <paramref name="expected"/>, whatever its spelling.</summary>
    public static bool IsNumber(Json? value, long expected) => value is JsonNumber n && n.Value == expected;
}
