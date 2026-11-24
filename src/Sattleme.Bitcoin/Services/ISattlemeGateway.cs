using SmartstoreSattleme.Sattleme.Checkout;

namespace Sattleme.Bitcoin.Services;

/// <summary>
/// The module's one way to the SDK: every client is made here, from the integration key of an
/// order's store. The tests replace it with a fake of the SDK (which still has the real SDK check
/// the key); the shipped module has no switch for that.
/// </summary>
public interface ISattlemeGateway
{
    /// <summary>
    /// The client of <paramref name="key"/>: the SDK's own, one per key for the application (its
    /// "one order, one invoice at a time" holds within one client). Throws the SDK's
    /// <see cref="InvalidKeyException"/> when it is not an integration key.
    /// </summary>
    ISattlemeClient ClientOf(string key);
}

/// <summary>What the module asks of the SDK's client (SattlemeClient).</summary>
public interface ISattlemeClient
{
    /// <summary>The merchant of the integration (SattlemeClient.GetMerchantAsync): a removed one is <see cref="RevokedException"/>.</summary>
    Task<Merchant> GetMerchantAsync(CancellationToken cancellationToken);

    /// <summary>The invoice of an order (SattlemeClient.CreateInvoiceAsync).</summary>
    Task<Invoice> CreateInvoiceAsync(Order order, CancellationToken cancellationToken);

    /// <summary>Where the payment of a stored invoice is (SattlemeClient.GetStatusAsync).</summary>
    Task<PaymentStatus> GetStatusAsync(IInvoiceToCheck invoice, CancellationToken cancellationToken);
}
