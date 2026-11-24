namespace Sattleme.Checkout.Internal.Onchain;

/// <summary>The methods an order may be paid with.</summary>
internal readonly record struct Methods(bool Lightning, bool Onchain);

/// <summary>What leasing an address gave: one, none, or the explorer's failure.</summary>
internal enum LeaseOutcomeStatus
{
    Leased,
    None,
    ExplorerFailed,
}

/// <summary>What leasing an address gave, with the explorer's error when it failed.</summary>
internal sealed record LeaseOutcome(LeaseOutcomeStatus Status, SattlemeException? Error = null)
{
    public static readonly LeaseOutcome Leased = new(LeaseOutcomeStatus.Leased);
    public static readonly LeaseOutcome None = new(LeaseOutcomeStatus.None);
}

/// <summary>
/// The methods of an order by its amount (SPEC.md §31, "Methods by amount"): Lightning when the
/// amount is within the Lightning address's sendable sats, on-chain from 1 000 sats when an address
/// can be leased. Nothing is repaired: an amount is never split between the two, nor moved into the
/// sendable sats.
/// </summary>
internal static class PaymentMethods
{
    /// <summary>
    /// The methods <paramref name="sats"/> may be paid with, before an address is leased.
    /// <see cref="InvalidOrderException"/> (<paramref name="param"/>: <c>sats</c> or <c>fiat</c>)
    /// below 1 000 sats and outside the sendable sats.
    /// </summary>
    public static Methods Of(long sats, (long Min, long Max) sendable, string param)
    {
        var lightning = sats >= sendable.Min && sats <= sendable.Max;
        var onchain = sats >= Protocol.OnchainFloorSats;
        if (!lightning && !onchain)
        {
            throw new InvalidOrderException(param, $"{Numbers.Decimal(sats)} sats is outside the merchant's bounds ({Numbers.Decimal(sendable.Min)} to {Numbers.Decimal(sendable.Max)} sats)");
        }
        return new Methods(lightning, onchain);
    }

    /// <summary>
    /// The methods once leasing an address gave <paramref name="outcome"/>: without an address,
    /// Lightning only; an order that can only be paid on-chain then fails with
    /// <see cref="OnchainUnavailableException"/>, or the explorer's <see cref="NetworkException"/>.
    /// </summary>
    public static Methods With(Methods methods, LeaseOutcome outcome)
    {
        if (!methods.Onchain || outcome.Status == LeaseOutcomeStatus.Leased)
        {
            return methods;
        }
        if (!methods.Lightning)
        {
            throw outcome.Status == LeaseOutcomeStatus.ExplorerFailed ? outcome.Error! : new OnchainUnavailableException();
        }
        return new Methods(Lightning: true, Onchain: false);
    }
}
