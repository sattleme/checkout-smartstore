using System.Numerics;

namespace Sattleme.Checkout.Internal.Onchain;

/// <summary>
/// The merchant's tolerance of on-chain underpayments (SPEC.md §31, "The merchant's tolerance"):
/// the share of an order's amount its payment in time may miss and still pay it, in basis points.
/// The channel gives it, an order's lease keeps the one of its creation: every check of the order
/// judges its payment with that one, as the merchant's app does. It is never written where the
/// shop's page or the customer reads (the Invoice, the PaymentStatus, a checkout link's record, an
/// error, a log): a customer who knew it could pay that much less on purpose.
/// </summary>
internal static class Tolerance
{
    /// <summary>A tolerance as a channel or a lease writes it: an integer from 0 to 1000, else 0.</summary>
    public static int Of(Json? value) =>
        JsonRead.Number(value) is { } number && Numbers.IsInteger(number) && number is >= 0 and <= Protocol.MaxTolerance
            ? (int)number
            : 0;

    /// <summary>A tolerance kept as it is when it is one, else 0.</summary>
    public static int Of(int tolerance) => tolerance is >= 0 and <= Protocol.MaxTolerance ? tolerance : 0;

    /// <summary>
    /// The least accepted of <paramref name="sats"/> with <paramref name="tolerance"/> (basis
    /// points): <paramref name="sats"/> less the share the tolerance allows, rounded down to the
    /// whole sat, so that the merchant never accepts more missing than its tolerance. On integers.
    /// </summary>
    public static long LeastAccepted(long sats, int tolerance) =>
        sats - (long)(new BigInteger(sats) * Of(tolerance) / Protocol.ToleranceScale);

    /// <summary>
    /// What is due on-chain of an invoice of <paramref name="sats"/> with <paramref name="tolerance"/>,
    /// its rest invoices having paid <paramref name="paidByRests"/>, with the <c>received</c> of the
    /// merchant's acceptance that counts, <paramref name="merchantAccepted"/> (null without one:
    /// <see cref="MerchantAcceptances"/>) (SPEC.md §31, "The on-chain payment", step 4): nothing (0)
    /// when they paid <paramref name="sats"/> or more; else max(1, min(L, M) − R), L the least
    /// accepted, M the merchant's acceptance's <c>received</c> (<paramref name="sats"/> without one), R
    /// what they paid. R is taken once from the lesser: M counts the rest invoices paid when the
    /// merchant accepted, as L counts every payment. At least 1 sat: something must count on-chain for
    /// the tolerance or the merchant's acceptance to apply.
    /// </summary>
    public static long DueOnchain(long sats, int tolerance, long paidByRests, long? merchantAccepted = null)
    {
        if (paidByRests >= sats)
        {
            return 0;
        }
        var least = LeastAccepted(sats, tolerance);
        var accepted = merchantAccepted is { } m && m < least ? m : least;
        var due = accepted - paidByRests;
        return due > 0 ? due : 1;
    }
}
