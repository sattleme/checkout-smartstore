using System.Globalization;
using System.Text.Json;
using Sattleme.Bitcoin.Domain;

namespace Sattleme.Bitcoin.Services;

/// <summary>
/// What a page shows of a stored invoice and of its last status, read from their JSON (SPEC.md §3:
/// the SDK's Invoice or DeclaredInvoice, and its PaymentStatus). Only what is shown is read; the
/// checks give the SDK the invoice's JSON itself.
/// </summary>
public sealed class StoredInvoice
{
    #region Ctor

    private StoredInvoice()
    {
    }

    #endregion

    #region Properties

    /// <summary>The BOLT11 invoice, null for an order paid on-chain only.</summary>
    public string? Bolt11 { get; private set; }

    /// <summary>The deposit address of the merchant's wallet the order leased, null without one.</summary>
    public string? OnchainAddress { get; private set; }

    /// <summary>One QR code for both: <c>bitcoin:…?…&amp;lightning=…</c> or <c>lightning:…</c>.</summary>
    public string? PaymentUri { get; private set; }

    /// <summary>The amount, in sats.</summary>
    public long Sats { get; private set; }

    /// <summary>The price, as the SDK converted it (<c>24.50</c>), and its currency; null for a price in sats.</summary>
    public string? FiatAmount { get; private set; }

    /// <inheritdoc cref="FiatAmount"/>
    public string? FiatCurrency { get; private set; }

    /// <summary>The source of the rate the price was converted at, the price of one bitcoin, and when it was read.</summary>
    public string? RateSource { get; private set; }

    /// <inheritdoc cref="RateSource"/>
    public string? RatePrice { get; private set; }

    /// <inheritdoc cref="RateSource"/>
    public long? RateFetchedAt { get; private set; }

    /// <summary>What the merchant's app shows of the sale.</summary>
    public string? Note { get; private set; }

    /// <summary>The invoice's date and end (Unix seconds).</summary>
    public long CreatedAt { get; private set; }

    /// <inheritdoc cref="CreatedAt"/>
    public long ExpiresAt { get; private set; }

    /// <summary>The state of the last status (open, unconfirmed, paid, expired), null before the first check.</summary>
    public string? State { get; private set; }

    /// <summary>What came in time, in sats (on-chain, and by the invoices of the rest).</summary>
    public long Received { get; private set; }

    /// <summary>What is left to pay of an on-chain underpayment, in sats, and how: its invoice and its QR code.</summary>
    public long? RestSats { get; private set; }

    /// <inheritdoc cref="RestSats"/>
    public string? RestBolt11 { get; private set; }

    /// <inheritdoc cref="RestSats"/>
    public string? RestPaymentUri { get; private set; }

    /// <summary>How it was paid (lightning, onchain, mixed), when paid.</summary>
    public string? Method { get; private set; }

    /// <summary>The least confirmations of the order's on-chain transactions, when being confirmed.</summary>
    public int? Confirmations { get; private set; }

    /// <summary>The order's on-chain transactions, as the last status lists them.</summary>
    public IReadOnlyList<string> Txids { get; private set; } = [];

    /// <summary>The proofs of the Lightning payments (preimages): of the invoice, and of the invoices of the rest.</summary>
    public IReadOnlyList<string> Preimages { get; private set; } = [];

    #endregion

    #region Methods

    /// <summary>What <paramref name="invoice"/> holds; null when its invoice is not made.</summary>
    public static StoredInvoice? Of(SattlemeInvoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        if (invoice.InvoiceJson is null)
        {
            return null;
        }

        var read = new StoredInvoice();
        using (var document = JsonDocument.Parse(invoice.InvoiceJson))
        {
            var json = document.RootElement;
            read.Bolt11 = StringOf(json, "bolt11");
            read.OnchainAddress = StringOf(json, "onchainAddress");
            read.PaymentUri = StringOf(json, "paymentUri");
            read.Sats = SatsOf(StringOf(json, "sats"));
            read.Note = StringOf(json, "note");
            read.CreatedAt = IntegerOf(json, "createdAt") ?? 0;
            read.ExpiresAt = IntegerOf(json, "expiresAt") ?? invoice.ExpiresAt;
            if (json.TryGetProperty("fiat", out var fiat) && fiat.ValueKind == JsonValueKind.Object)
            {
                read.FiatAmount = StringOf(fiat, "amount");
                read.FiatCurrency = StringOf(fiat, "currency");
            }
            if (json.TryGetProperty("rate", out var rate) && rate.ValueKind == JsonValueKind.Object)
            {
                read.RateSource = StringOf(rate, "source");
                read.RatePrice = StringOf(rate, "price");
                read.RateFetchedAt = IntegerOf(rate, "fetchedAt");
            }
        }

        if (invoice.StatusJson is not null)
        {
            using var document = JsonDocument.Parse(invoice.StatusJson);
            var json = document.RootElement;
            read.State = StringOf(json, "state");
            read.Received = SatsOf(StringOf(json, "received"));
            read.Method = StringOf(json, "method");
            read.Confirmations = (int?)IntegerOf(json, "confirmations");
            read.Txids = StringsOf(json, "txids");
            var preimages = new List<string>();
            if (StringOf(json, "preimage") is { } preimage)
            {
                preimages.Add(preimage);
            }

            preimages.AddRange(StringsOf(json, "preimages"));
            read.Preimages = preimages;
            if (json.TryGetProperty("rest", out var rest) && rest.ValueKind == JsonValueKind.Object)
            {
                read.RestSats = SatsOf(StringOf(rest, "sats"));
                read.RestBolt11 = StringOf(rest, "bolt11");
                read.RestPaymentUri = StringOf(rest, "paymentUri");
            }
        }
        return read;
    }

    /// <summary>A number of sats written as the SDK writes them (a string of digits); 0 when it is not one.</summary>
    public static long SatsOf(string? digits)
    {
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var sats) ? sats : 0;
    }

    #endregion

    #region Utilities

    private static string? StringOf(JsonElement json, string name)
    {
        return json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static long? IntegerOf(JsonElement json, string name)
    {
        return json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
            ? number
            : null;
    }

    private static IReadOnlyList<string> StringsOf(JsonElement json, string name)
    {
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)];
    }

    #endregion
}
