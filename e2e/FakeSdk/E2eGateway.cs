using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Sattleme.Bitcoin.Services;
using SmartstoreSattleme.Sattleme.Checkout;

namespace Sattleme.E2e;

/// <summary>
/// The SDK, faked for the end-to-end test: the real SDK still checks each key; invoices are made
/// here (1 000 sats per unit of the price), and paid when the test says so (E2eController).
/// </summary>
public class E2eGateway : ISattlemeGateway
{
    private readonly ConcurrentDictionary<string, Invoice> _invoices = new();
    private readonly ConcurrentDictionary<string, bool> _paid = new();

    /// <summary>The references of the invoices made, oldest first.</summary>
    public IList<string> References => [.. _invoices.Values.OrderBy(invoice => invoice.CreatedAt).Select(invoice => invoice.Ref)];

    /// <summary>The invoice of <paramref name="reference"/> is paid (with Lightning).</summary>
    public bool Pay(string reference)
    {
        return _invoices.ContainsKey(reference) && _paid.TryAdd(reference, true);
    }

    /// <inheritdoc/>
    public ISattlemeClient ClientOf(string key)
    {
        _ = new SattlemeClient(new SattlemeClientOptions { Key = key });
        return new Client(this);
    }

    private sealed class Client : ISattlemeClient
    {
        private readonly E2eGateway _gateway;

        public Client(E2eGateway gateway)
        {
            _gateway = gateway;
        }

        public Task<Merchant> GetMerchantAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new Merchant("E2E Bakery", "1", "100000000", 10));
        }

        public Task<Invoice> CreateInvoiceAsync(Order order, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var amount = order.Fiat!.Amount;
            var sats = (long)(decimal.Parse(amount, CultureInfo.InvariantCulture) * 1000m);
            var bolt11 = "lnbc" + sats.ToString(CultureInfo.InvariantCulture) + "n1e2e" + order.Ref.Replace("-", "", StringComparison.Ordinal);
            const string address = "bc1pmzfrwwndsqmk5yh69yjr5lfgfg4ev8c0tsc06e";
            var json = new JsonObject
            {
                ["ref"] = order.Ref,
                ["bolt11"] = bolt11,
                ["paymentHash"] = new string('a', 64),
                ["verifyUrl"] = "https://wallet.example/verify/" + order.Ref,
                ["onchainAddress"] = address,
                ["paymentUri"] = $"bitcoin:{address}?amount={(sats / 100_000_000m).ToString("0.########", CultureInfo.InvariantCulture)}&lightning={bolt11}",
                ["sats"] = sats.ToString(CultureInfo.InvariantCulture),
                ["fiat"] = new JsonObject { ["amount"] = amount, ["currency"] = order.Fiat.Currency, ["minor"] = (decimal.Parse(amount, CultureInfo.InvariantCulture) * 100m).ToString("0", CultureInfo.InvariantCulture), ["digits"] = 2 },
                ["rate"] = new JsonObject { ["source"] = "e2e", ["currency"] = order.Fiat.Currency, ["price"] = "100000", ["fetchedAt"] = now, ["time"] = null },
                ["note"] = order.Note ?? order.Ref,
                ["createdAt"] = now,
                ["expiresAt"] = now + (order.DurationSeconds ?? 900),
                ["checkoutUrl"] = null,
                ["redirectUrl"] = null,
            };
            var invoice = Invoice.FromJson(json.ToJsonString());
            _gateway._invoices[order.Ref] = invoice;
            return Task.FromResult(invoice);
        }

        public Task<PaymentStatus> GetStatusAsync(IInvoiceToCheck invoice, CancellationToken cancellationToken)
        {
            if (_gateway._paid.ContainsKey(invoice.Ref!))
            {
                return Task.FromResult<PaymentStatus>(PaymentStatus.Paid.InLightning(new string('1', 64)));
            }
            return Task.FromResult<PaymentStatus>(DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= invoice.ExpiresAt + 120
                ? new PaymentStatus.Expired("0")
                : new PaymentStatus.Open(invoice.ExpiresAt, "0", null));
        }
    }
}
