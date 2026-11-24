using System;
using System.Globalization;
using System.Numerics;
using Sattleme.Checkout.Internal;

namespace Sattleme.Checkout;

/// <summary>
/// The price of an order in a currency, as the shop gives it (SPEC.md §17, <c>fiat</c>): an amount
/// written as decimal digits (<c>"24.50"</c>, never a float) and an ISO 4217 code in upper case
/// (<c>"CHF"</c>). It is converted to sats at the rate of the order's sources (SPEC.md §25).
/// </summary>
/// <param name="Amount">Digits, an optional fraction: <c>^[0-9]{1,15}(\.[0-9]{1,15})?$</c>, at most the currency's decimals (trailing zeros aside).</param>
/// <param name="Currency">An ISO 4217 code of <see cref="Currencies"/>, upper case.</param>
public sealed record Price(string Amount, string Currency);

/// <summary>An exact positive decimal: <c>Units</c> × 10^-<c>Scale</c>.</summary>
internal readonly record struct PriceDecimal(BigInteger Units, int Scale);

/// <summary>
/// Amounts in a currency, converted to sats on the integrator's server at the rate of the source it
/// chooses (SPEC.md §25). Amounts are exact integers end to end: the price of one bitcoin is read as
/// the exact decimal its source wrote, and the sats are rounded up to the next whole sat, as the
/// merchant's point of sale does: the merchant never receives less than the price.
/// </summary>
public static class Prices
{
    private static readonly BigInteger SatsPerBtc = 100_000_000;

    /// <summary>
    /// Sats for <paramref name="fiat"/> at <paramref name="price"/>, the price of one bitcoin in its
    /// currency (a decimal string): exact, rounded up to the next whole sat.
    /// <c>FiatToSats(new Price("24.50", "CHF"), "69012")</c> is 35502 (the exact quotient is
    /// 35501.07…). Throws <see cref="InvalidOrderException"/> for an invalid price in a currency,
    /// <see cref="RateException"/> for an invalid price of one bitcoin.
    /// </summary>
    public static BigInteger FiatToSats(Price fiat, string price)
    {
        var parsed = ParseFiat(fiat);
        var value = ParseDecimal(price) ?? throw new RateException("A price must be a decimal string above 0");
        return MinorToSats(BigInteger.Parse(parsed.Minor, CultureInfo.InvariantCulture), parsed.Digits, value);
    }

    /// <summary>
    /// A price as an exact decimal: digits, an optional fraction and an optional exponent
    /// (<c>"69025.1084416666694295"</c>, <c>"8e-8"</c>), 64 characters at most, an exponent of 64 at
    /// most either way, nothing around it. Null unless it is strictly positive.
    /// </summary>
    internal static PriceDecimal? ParseDecimal(string? value)
    {
        if (value is null || value.Length == 0 || value.Length > Protocol.MaxPriceLength)
        {
            return null;
        }
        var i = 0;
        while (i < value.Length && char.IsAsciiDigit(value[i]))
        {
            i++;
        }
        if (i == 0)
        {
            return null;
        }
        var integer = value[..i];
        var fraction = "";
        if (i < value.Length && value[i] == '.')
        {
            var start = ++i;
            while (i < value.Length && char.IsAsciiDigit(value[i]))
            {
                i++;
            }
            if (i == start)
            {
                return null;
            }
            fraction = value[start..i];
        }
        BigInteger exponent = 0;
        if (i < value.Length && value[i] is 'e' or 'E')
        {
            i++;
            var negative = false;
            if (i < value.Length && value[i] is '+' or '-')
            {
                negative = value[i] == '-';
                i++;
            }
            var start = i;
            while (i < value.Length && char.IsAsciiDigit(value[i]))
            {
                i++;
            }
            if (i == start)
            {
                return null;
            }
            exponent = BigInteger.Parse(value[start..i], CultureInfo.InvariantCulture);
            if (negative)
            {
                exponent = -exponent;
            }
        }
        if (i != value.Length || BigInteger.Abs(exponent) > Protocol.MaxPriceExponent)
        {
            return null;
        }
        var units = BigInteger.Parse(integer + fraction, CultureInfo.InvariantCulture);
        var scale = fraction.Length - (int)exponent;
        if (scale < 0)
        {
            units *= BigInteger.Pow(10, -scale);
            scale = 0;
        }
        // The shortest exact writing: 69012.50 is 69012.5.
        while (scale > 0 && (units % 10).IsZero)
        {
            units /= 10;
            scale--;
        }
        return units > 0 ? new PriceDecimal(units, scale) : null;
    }

    /// <summary>
    /// The text of a JSON number a source answered: the shortest decimal that reads back as the same
    /// IEEE 754 double (ECMAScript's Number::toString, .NET's "R"). A source that writes more digits
    /// than a double holds gets them rounded, in every implementation alike. Null for a number JSON
    /// cannot write.
    /// </summary>
    internal static string? NumberText(double value) => Numbers.JavaScript(value);

    /// <summary>The plain decimal writing of an exact decimal: <c>69012.5</c>, <c>0.00000008</c>.</summary>
    internal static string FormatDecimal(PriceDecimal value)
    {
        var digits = value.Units.ToString(CultureInfo.InvariantCulture);
        if (value.Scale == 0)
        {
            return digits;
        }
        digits = digits.PadLeft(value.Scale + 1, '0');
        return $"{digits[..^value.Scale]}.{digits[^value.Scale..]}";
    }

    /// <summary>
    /// A price given as <c>new Price("24.50", "CHF")</c>, as integer minor units of its currency
    /// (SPEC.md §25, "The price of an order"): the currency an ISO 4217 code of <see cref="Currencies"/>
    /// (upper case); the amount a string of digits with at most the currency's decimals (trailing
    /// zeros aside), nothing around it: never a float, never rounded. Throws
    /// <see cref="InvalidOrderException"/> (<c>fiat</c>).
    /// </summary>
    internal static Fiat ParseFiat(Price? fiat)
    {
        if (fiat is null)
        {
            throw new InvalidOrderException("fiat", "expected { amount, currency }");
        }
        if (Currencies.DigitsOf(fiat.Currency) is not { } digits)
        {
            throw new InvalidOrderException("fiat", "currency: an ISO 4217 code in upper case, as CHF");
        }
        if (fiat.Amount is not { } amount)
        {
            throw new InvalidOrderException("fiat", "amount: a string of digits, as \"24.50\" (never a float)");
        }
        var point = amount.IndexOf('.', StringComparison.Ordinal);
        var whole = point < 0 ? amount : amount[..point];
        var decimals = point < 0 ? "" : amount[(point + 1)..];
        if (!Text.IsDigits(whole, 1, 15) || (point >= 0 && !Text.IsDigits(decimals, 1, 15)))
        {
            throw new InvalidOrderException("fiat", "amount: digits, as \"24.50\"");
        }
        var fraction = decimals.TrimEnd('0');
        if (fraction.Length > digits)
        {
            throw new InvalidOrderException("fiat", $"amount: {fiat.Currency} has {Numbers.Decimal(digits)} decimals at most");
        }
        var minor = (BigInteger.Parse(whole, CultureInfo.InvariantCulture) * BigInteger.Pow(10, digits))
            + BigInteger.Parse(fraction.PadRight(digits, '0') is { Length: > 0 } padded ? padded : "0", CultureInfo.InvariantCulture);
        if (minor < 1 || minor > Protocol.MaxMinorUnits)
        {
            throw new InvalidOrderException("fiat", "amount: more than 0, and not that large");
        }
        return new Fiat(FormatDecimal(new PriceDecimal(minor, digits)), fiat.Currency, minor.ToString(CultureInfo.InvariantCulture), digits);
    }

    /// <summary>
    /// Sats for <paramref name="minor"/> units of a currency with <paramref name="digits"/> decimals,
    /// at <paramref name="price"/> (one bitcoin in that currency), rounded up to the next whole sat,
    /// on integers: ⌈minor × 10^8 × 10^scale / (units × 10^digits)⌉.
    /// </summary>
    internal static BigInteger MinorToSats(BigInteger minor, int digits, PriceDecimal price)
    {
        var dividend = minor * SatsPerBtc * BigInteger.Pow(10, price.Scale);
        var divisor = price.Units * BigInteger.Pow(10, digits);
        return (dividend + divisor - 1) / divisor;
    }
}
