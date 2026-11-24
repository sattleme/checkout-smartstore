using System;
using System.Globalization;
using System.Text;

namespace Sattleme.Checkout.Internal;

/// <summary>
/// Numbers as JSON gives them (SPEC.md §2, §7): IEEE 754 doubles, whatever their spelling, and the
/// integers this document asks for among them. Amounts are integers (long, or BigInteger for the
/// conversion of a price): never a double in arithmetic.
/// </summary>
internal static class Numbers
{
    /// <summary>2^53: the first integer a double does not tell from its neighbour.</summary>
    private const double TwoTo53 = 9_007_199_254_740_992d;

    /// <summary><paramref name="value"/> in decimal, invariant culture.</summary>
    public static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary><paramref name="value"/> in decimal, invariant culture.</summary>
    public static string Decimal(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether <paramref name="value"/> is an integer (JavaScript's Number.isInteger).</summary>
    public static bool IsInteger(double value) => double.IsFinite(value) && Math.Floor(value) == value;

    /// <summary>Whether <paramref name="value"/> is a safe integer: an integer of magnitude below 2^53.</summary>
    public static bool IsSafeInteger(double value) => IsInteger(value) && Math.Abs(value) < TwoTo53;

    /// <summary>The safe integer <paramref name="value"/> as a long, else null.</summary>
    public static long? SafeInteger(double value) => IsSafeInteger(value) ? (long)value : null;

    /// <summary>
    /// <paramref name="a"/> + <paramref name="b"/> (both at least 0), or <see cref="long.MaxValue"/>
    /// when the sum is beyond it: what an explorer or a relay sends never wraps an amount (SPEC.md §7).
    /// </summary>
    public static long SaturatingAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

    /// <summary>
    /// <paramref name="value"/> written as JavaScript writes a number (Number::toString): the shortest
    /// decimal that reads back as the same double, in plain notation from 1e-6 to below 1e21, in
    /// exponent notation (<c>1e+21</c>, <c>1.5e-7</c>) beyond; -0 as <c>0</c>. Null for NaN and the
    /// infinities, which JSON cannot write.
    /// </summary>
    public static string? JavaScript(double value)
    {
        if (!double.IsFinite(value))
        {
            return null;
        }
        if (value == 0)
        {
            return "0";
        }
        // .NET writes the shortest round-trip digits ("R" since .NET Core 3.0); only the notation
        // differs from ECMAScript's, which this rebuilds from those digits and their exponent.
        var (digits, exponent) = DigitsOf(Math.Abs(value).ToString("R", CultureInfo.InvariantCulture));
        // ECMAScript: the value is 0.d1d2…dk × 10^n, so n = exponent + 1 for d1.d2…dk × 10^exponent.
        var k = digits.Length;
        var n = exponent + 1;
        var builder = new StringBuilder();
        if (value < 0)
        {
            builder.Append('-');
        }
        if (k <= n && n <= 21)
        {
            builder.Append(digits).Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            builder.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            builder.Append("0.").Append('0', -n).Append(digits);
        }
        else
        {
            builder.Append(digits[0]);
            if (k > 1)
            {
                builder.Append('.').Append(digits, 1, k - 1);
            }
            builder.Append('e').Append(n - 1 >= 0 ? '+' : '-').Append(Math.Abs(n - 1).ToString(CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    /// <summary>The significant digits (no leading or trailing zeros) and decimal exponent of a positive "R" text.</summary>
    private static (string Digits, int Exponent) DigitsOf(string text)
    {
        var mantissa = text;
        var exponent = 0;
        var e = text.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            mantissa = text[..e];
            exponent = int.Parse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }
        var point = mantissa.IndexOf('.', StringComparison.Ordinal);
        var integerPart = point < 0 ? mantissa : mantissa[..point];
        var fractionPart = point < 0 ? "" : mantissa[(point + 1)..];
        var all = integerPart + fractionPart;
        // The decimal point sits after integerPart: the exponent of the first digit of `all`.
        var firstExponent = exponent + integerPart.Length - 1;
        var leading = 0;
        while (leading < all.Length - 1 && all[leading] == '0')
        {
            leading++;
        }
        var digits = all[leading..].TrimEnd('0');
        if (digits.Length == 0)
        {
            digits = "0";
        }
        return (digits, firstExponent - leading);
    }
}
