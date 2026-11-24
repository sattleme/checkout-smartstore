using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SmartstoreSattleme.Sattleme.Checkout;

namespace Sattleme.Bitcoin.Services;

/// <summary>
/// The SDK's clients, one per integration key for the application (the SDK's README, "In a
/// platform's plugin": a singleton per key, thread-safe, holding no connection between calls), with
/// the module's timeouts, the SDK's warnings going to Smartstore's log (System → Log).
/// </summary>
public class SattlemeGateway : ISattlemeGateway
{
    // By the key's SHA-256: the dictionary holds no copy of the key besides the client's own.
    private readonly ConcurrentDictionary<string, ISattlemeClient> _clients = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    /// <summary>The gateway, the SDK's warnings going to Smartstore's log through <paramref name="loggerFactory"/>.</summary>
    public SattlemeGateway(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger("Sattleme.Bitcoin.Sdk");
    }

    /// <inheritdoc/>
    public ISattlemeClient ClientOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        if (_clients.TryGetValue(id, out var client))
        {
            return client;
        }

        // Made outside GetOrAdd's factory: an invalid key throws from here, before anything is stored.
        var made = new SdkClient(new SattlemeClient(new SattlemeClientOptions
        {
            Key = key,
            RelayTimeoutMs = SattlemeDefaults.RelayTimeoutMs,
            HttpTimeoutMs = SattlemeDefaults.HttpTimeoutMs,
            Logger = _logger,
        }));
        return _clients.GetOrAdd(id, made);
    }

    /// <summary>The SDK's client, as the module asks it.</summary>
    private sealed class SdkClient : ISattlemeClient
    {
        private readonly SattlemeClient _client;

        public SdkClient(SattlemeClient client)
        {
            _client = client;
        }

        public Task<Merchant> GetMerchantAsync(CancellationToken cancellationToken)
        {
            return _client.GetMerchantAsync(cancellationToken);
        }

        public Task<Invoice> CreateInvoiceAsync(Order order, CancellationToken cancellationToken)
        {
            return _client.CreateInvoiceAsync(order, cancellationToken);
        }

        public Task<PaymentStatus> GetStatusAsync(IInvoiceToCheck invoice, CancellationToken cancellationToken)
        {
            return _client.GetStatusAsync(invoice, cancellationToken);
        }

        public override string ToString()
        {
            return _client.ToString();
        }
    }
}
