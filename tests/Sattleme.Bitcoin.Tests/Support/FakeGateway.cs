using System.Globalization;
using System.Text.Json.Nodes;
using Sattleme.Bitcoin.Services;
using SmartstoreSattleme.Sattleme.Checkout;

namespace Sattleme.Bitcoin.Tests.Support;

/// <summary>
/// The SDK, faked at the plugin's seam (ISattlemeGateway): each client is still made by the real
/// SDK first, which refuses what is not an integration key, then the fake answers as the test
/// says (an invoice, a status, an error), counting the calls.
/// </summary>
public sealed class FakeGateway : ISattlemeGateway
{
    private readonly ManualTime _time;
    private int _creates;
    private int _statuses;
    private int _merchants;

    /// <summary>A gateway whose invoices are made at <paramref name="time"/>.</summary>
    public FakeGateway(ManualTime time)
    {
        _time = time;
        Create = (order, _) => Task.FromResult(SdkData.InvoiceFor(order, _time.UnixNow));
        Status = (_, _) => Task.FromResult<PaymentStatus>(new PaymentStatus.Open(0, "0", null));
        Merchant = _ => Task.FromResult(new Merchant("Sunrise Bakery", "1", "100000000", 10));
    }

    /// <summary>What CreateInvoiceAsync does.</summary>
    public Func<Order, CancellationToken, Task<Invoice>> Create { get; set; }

    /// <summary>What GetStatusAsync does.</summary>
    public Func<IInvoiceToCheck, CancellationToken, Task<PaymentStatus>> Status { get; set; }

    /// <summary>What GetMerchantAsync does.</summary>
    public Func<CancellationToken, Task<Merchant>> Merchant { get; set; }

    /// <summary>How many times each was called.</summary>
    public int Creates => Volatile.Read(ref _creates);

    /// <inheritdoc cref="Creates"/>
    public int Statuses => Volatile.Read(ref _statuses);

    /// <inheritdoc cref="Creates"/>
    public int Merchants => Volatile.Read(ref _merchants);

    /// <summary>The orders asked for, in order.</summary>
    public List<Order> Orders { get; } = [];

    /// <summary>The keys clients were asked for.</summary>
    public List<string> Keys { get; } = [];

    /// <inheritdoc/>
    public ISattlemeClient ClientOf(string key)
    {
        // The real SDK checks the key: an invalid one throws InvalidKeyException, as in the shop.
        _ = new SattlemeClient(new SattlemeClientOptions { Key = key });
        lock (Keys)
        {
            Keys.Add(key);
        }

        return new Client(this);
    }

    private sealed class Client : ISattlemeClient
    {
        private readonly FakeGateway _gateway;

        public Client(FakeGateway gateway)
        {
            _gateway = gateway;
        }

        public Task<Merchant> GetMerchantAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _gateway._merchants);
            return _gateway.Merchant(cancellationToken);
        }

        public Task<Invoice> CreateInvoiceAsync(Order order, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _gateway._creates);
            lock (_gateway.Orders)
            {
                _gateway.Orders.Add(order);
            }

            return _gateway.Create(order, cancellationToken);
        }

        public Task<PaymentStatus> GetStatusAsync(IInvoiceToCheck invoice, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _gateway._statuses);
            return _gateway.Status(invoice, cancellationToken);
        }
    }
}

/// <summary>The SDK's objects, as the SDK writes them (SPEC.md §3), for the fake to give.</summary>
public static class SdkData
{
    /// <summary>A valid integration key (the specification's own vector: its relay is never reached here).</summary>
    public const string KEY = "sattleme_ck_qEVejHuCZpF4XMmBSw5p7iZYxCtm20XxzT1fnJ9VB8o.uPTAz1d8bbQaw2Jx22Wwrga2vI2F8allY5NrPC3Aogk.cmVsYXkuZXhhbXBsZQ";

    /// <summary>A deposit address.</summary>
    public const string ADDRESS = "bc1pmzfrwwndsqmk5yh69yjr5lfgfg4ev8c0tsc06e";

    /// <summary>A transaction.</summary>
    public const string TXID = "5e2383defe7efcbdc9fdd6dba55da148b206617bbb49e6bb93fce7bfbb459d44";

    /// <summary>A Lightning payment's proof.</summary>
    public const string PREIMAGE = "0101010101010101010101010101010101010101010101010101010101010101";

    /// <summary>The sats of a price: 1 000 sats per unit of the currency (24.50 CHF: 24 500 sats).</summary>
    public static long SatsOf(string amount)
    {
        return (long)(decimal.Parse(amount, CultureInfo.InvariantCulture) * 1000m);
    }

    /// <summary>The invoice the SDK would make for <paramref name="order"/> at <paramref name="now"/>.</summary>
    public static Invoice InvoiceFor(Order order, long now, bool onchain = true)
    {
        ArgumentNullException.ThrowIfNull(order);
        return Invoice.FromJson(InvoiceJson(order.Ref, order.Fiat?.Amount ?? "1.00", order.Fiat?.Currency ?? "CHF",
            now, now + (order.DurationSeconds ?? 900), order.Note ?? order.Ref, onchain).ToJsonString());
    }

    /// <summary>An invoice's JSON.</summary>
    public static JsonObject InvoiceJson(string reference, string amount, string currency, long createdAt, long expiresAt, string note, bool onchain = true)
    {
        var sats = SatsOf(amount);
        var bolt11 = "lnbc" + sats.ToString(CultureInfo.InvariantCulture) + "n1fake" + reference.Replace("-", "", StringComparison.Ordinal);
        var btc = (sats / 100_000_000m).ToString("0.########", CultureInfo.InvariantCulture);
        return new JsonObject
        {
            ["ref"] = reference,
            ["bolt11"] = bolt11,
            ["paymentHash"] = new string('a', 64),
            ["verifyUrl"] = "https://wallet.example/verify/" + reference,
            ["onchainAddress"] = onchain ? ADDRESS : null,
            ["paymentUri"] = onchain ? $"bitcoin:{ADDRESS}?amount={btc}&lightning={bolt11}" : "lightning:" + bolt11,
            ["sats"] = sats.ToString(CultureInfo.InvariantCulture),
            ["fiat"] = new JsonObject { ["amount"] = amount, ["currency"] = currency, ["minor"] = (decimal.Parse(amount, CultureInfo.InvariantCulture) * 100m).ToString("0", CultureInfo.InvariantCulture), ["digits"] = 2 },
            ["rate"] = new JsonObject { ["source"] = "coingecko", ["currency"] = currency, ["price"] = "100000", ["fetchedAt"] = createdAt, ["time"] = null },
            ["note"] = note,
            ["createdAt"] = createdAt,
            ["expiresAt"] = expiresAt,
            ["checkoutUrl"] = null,
            ["redirectUrl"] = null,
        };
    }

    /// <summary>The invoice the merchant's registry declares for <paramref name="invoice"/> (InvoicePending, OrderPaid).</summary>
    public static DeclaredInvoice Declared(Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var json = (JsonObject)JsonNode.Parse(invoice.ToJson())!;
        json.Remove("rate");
        json.Remove("checkoutUrl");
        json.Remove("redirectUrl");
        json["declaredAt"] = invoice.CreatedAt;
        return DeclaredInvoice.FromJson(json.ToJsonString());
    }

    /// <summary>Open, nothing received.</summary>
    public static PaymentStatus Open(long expiresAt)
    {
        return new PaymentStatus.Open(expiresAt, "0", null);
    }

    /// <summary>Open, <paramref name="received"/> on-chain, the rest to pay (an invoice for the rest when it is 1 000 sats or more: <paramref name="restReady"/>).</summary>
    public static PaymentStatus OpenWithRest(long expiresAt, long received, long rest, bool restReady = true)
    {
        var bolt11 = "lnbc" + rest.ToString(CultureInfo.InvariantCulture) + "n1rest";
        return new PaymentStatus.Open(expiresAt, received.ToString(CultureInfo.InvariantCulture),
            new Rest(rest.ToString(CultureInfo.InvariantCulture), restReady ? bolt11 : null, restReady ? "lightning:" + bolt11 : null));
    }

    /// <summary>Enough came on-chain, being confirmed.</summary>
    public static PaymentStatus Unconfirmed(long received, int confirmations)
    {
        return new PaymentStatus.Unconfirmed(received.ToString(CultureInfo.InvariantCulture), confirmations, [TXID]);
    }

    /// <summary>Paid with Lightning.</summary>
    public static PaymentStatus PaidLightning()
    {
        return PaymentStatus.Paid.InLightning(PREIMAGE);
    }

    /// <summary>Paid on-chain.</summary>
    public static PaymentStatus PaidOnchain(long received)
    {
        return PaymentStatus.Paid.Onchain(received.ToString(CultureInfo.InvariantCulture), [TXID]);
    }

    /// <summary>Paid on-chain and its rest with Lightning.</summary>
    public static PaymentStatus PaidMixed(long received)
    {
        return PaymentStatus.Paid.Mixed(received.ToString(CultureInfo.InvariantCulture), [TXID], [PREIMAGE]);
    }

    /// <summary>Ended, with <paramref name="received"/> (an underpayment when above 0).</summary>
    public static PaymentStatus Expired(long received = 0)
    {
        return new PaymentStatus.Expired(received.ToString(CultureInfo.InvariantCulture));
    }
}
