using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sattleme.Checkout.Internal;

namespace Sattleme.Checkout;

// What the client gives and takes (SPEC.md §3). Everything an integrator stores is the JSON of
// SPEC.md §3 as it is (ToJson, or System.Text.Json, which these types' converters write the same):
// every amount a string of digits (sats, minor units), never a float; every date an integer of Unix
// seconds; an absent value null. An invoice another implementation stored reads back (FromJson), and
// the other way round.

/// <summary>A price in a currency, as an Invoice holds it: <see cref="Minor"/> units with <see cref="Digits"/> decimals.</summary>
[JsonConverter(typeof(SattlemeJsonConverter<Fiat>))]
public sealed class Fiat : ISattlemeJson
{
    /// <summary>A price in a currency.</summary>
    public Fiat(string amount, string currency, string minor, int digits)
    {
        Amount = amount;
        Currency = currency;
        Minor = minor;
        Digits = digits;
    }

    /// <summary>The price as written, with the currency's decimals: <c>24.50</c>.</summary>
    public string Amount { get; }

    /// <summary>ISO 4217, upper case: <c>CHF</c>.</summary>
    public string Currency { get; }

    /// <summary>The price in the currency's minor units, a string of digits: <c>2450</c>.</summary>
    public string Minor { get; }

    /// <summary>The currency's decimals: 2.</summary>
    public int Digits { get; }

    /// <summary>Its JSON (SPEC.md §3).</summary>
    public string ToJson() => ToJsonValue().Write();

    /// <summary>A Fiat from its JSON; <see cref="InvalidOrderException"/> (<c>invoice</c>) when it is not one.</summary>
    public static Fiat FromJson(string json) => Read(StoredJson.Parse(json));

    JsonObject ISattlemeJson.ToJsonValue() => ToJsonValue();

    internal JsonObject ToJsonValue() => new JsonObject()
        .Set("amount", Amount)
        .Set("currency", Currency)
        .Set("minor", Minor)
        .Set("digits", Digits);

    internal static Fiat Read(Json? value)
    {
        var o = StoredJson.Object(value);
        return new Fiat(
            StoredJson.String(o, "amount"),
            StoredJson.String(o, "currency"),
            StoredJson.String(o, "minor"),
            (int)StoredJson.Integer(o, "digits", int.MinValue, int.MaxValue));
    }
}

/// <summary>The rate an amount in a currency was converted at: for the audit, and to answer a customer.</summary>
[JsonConverter(typeof(SattlemeJsonConverter<RateInfo>))]
public sealed class RateInfo : ISattlemeJson
{
    /// <summary>A rate.</summary>
    public RateInfo(string source, string currency, string price, long fetchedAt, long? time)
    {
        Source = source;
        Currency = currency;
        Price = price;
        FetchedAt = fetchedAt;
        Time = time;
    }

    /// <summary><c>coingecko</c>, <c>mempool</c>, <c>opennode</c>, <c>coinbase</c>, <c>kraken</c> or <c>custom</c>.</summary>
    public string Source { get; }

    /// <summary>ISO 4217, upper case.</summary>
    public string Currency { get; }

    /// <summary>The price of one bitcoin in <see cref="Currency"/>, plain decimal, shortest: <c>69012.5</c>.</summary>
    public string Price { get; }

    /// <summary>Unix seconds: when the client read it.</summary>
    public long FetchedAt { get; }

    /// <summary>Unix seconds: when the source says the price was set, null when it does not say.</summary>
    public long? Time { get; }

    /// <summary>Its JSON (SPEC.md §3).</summary>
    public string ToJson() => ToJsonValue().Write();

    /// <summary>A RateInfo from its JSON; <see cref="InvalidOrderException"/> (<c>invoice</c>) when it is not one.</summary>
    public static RateInfo FromJson(string json) => Read(StoredJson.Parse(json));

    JsonObject ISattlemeJson.ToJsonValue() => ToJsonValue();

    internal JsonObject ToJsonValue() => new JsonObject()
        .Set("source", Source)
        .Set("currency", Currency)
        .Set("price", Price)
        .Set("fetchedAt", FetchedAt)
        .Set("time", Json.OfNullable(Time));

    internal static RateInfo Read(Json? value)
    {
        var o = StoredJson.Object(value);
        return new RateInfo(
            StoredJson.String(o, "source"),
            StoredJson.String(o, "currency"),
            StoredJson.String(o, "price"),
            StoredJson.Integer(o, "fetchedAt"),
            StoredJson.NullableInteger(o, "time"));
    }
}

/// <summary>
/// What a payment check needs of an invoice (SPEC.md §3, InvoiceToCheck): an <see cref="Invoice"/>
/// or a <see cref="DeclaredInvoice"/> is one, and so is an <see cref="InvoiceToCheck"/> read from
/// what a shop stored. <see cref="Ref"/> is needed with an address: the witnesses of its payment are
/// its order's. System.Text.Json writes one as its own JSON (an Invoice's, a DeclaredInvoice's, an
/// InvoiceToCheck's), and reads one as an <see cref="InvoiceToCheck"/>.
/// </summary>
[JsonConverter(typeof(InvoiceToCheckJsonConverter))]
public interface IInvoiceToCheck
{
    /// <summary>The order's reference (needed with an address).</summary>
    string? Ref { get; }

    /// <summary>The BOLT11 invoice, null for an order paid on-chain only.</summary>
    string? Bolt11 { get; }

    /// <summary>Its payment hash (hex), null without <see cref="Bolt11"/>.</summary>
    string? PaymentHash { get; }

    /// <summary>Its LUD-21 verify URL, null without <see cref="Bolt11"/>.</summary>
    string? VerifyUrl { get; }

    /// <summary>The deposit address the order leased, null without one.</summary>
    string? OnchainAddress { get; }

    /// <summary>The amount, in sats: a string of digits.</summary>
    string Sats { get; }

    /// <summary>Unix seconds: when it can no longer be paid.</summary>
    long ExpiresAt { get; }
}

/// <summary>
/// The invoice of an order (CreateInvoiceAsync): store it with the order. The shop's page needs
/// <see cref="PaymentUri"/> (or <see cref="Bolt11"/> and <see cref="OnchainAddress"/>) and
/// <see cref="ExpiresAt"/> only; with a checkout link, the shop sends its customer to
/// <see cref="CheckoutUrl"/> instead. At least one of <see cref="Bolt11"/> and
/// <see cref="OnchainAddress"/> is not null.
/// </summary>
[JsonConverter(typeof(SattlemeJsonConverter<Invoice>))]
public sealed class Invoice : IInvoiceToCheck, ISattlemeJson
{
    internal Invoice(
        string reference,
        string? bolt11,
        string? paymentHash,
        string? verifyUrl,
        string? onchainAddress,
        string paymentUri,
        string sats,
        Fiat? fiat,
        RateInfo? rate,
        string note,
        long createdAt,
        long expiresAt,
        string? checkoutUrl,
        string? redirectUrl)
    {
        Ref = reference;
        Bolt11 = bolt11;
        PaymentHash = paymentHash;
        VerifyUrl = verifyUrl;
        OnchainAddress = onchainAddress;
        PaymentUri = paymentUri;
        Sats = sats;
        Fiat = fiat;
        Rate = rate;
        Note = note;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        CheckoutUrl = checkoutUrl;
        RedirectUrl = redirectUrl;
    }

    /// <summary>The order's reference.</summary>
    public string Ref { get; }

    /// <summary>The BOLT11 invoice, as the Lightning address wrote it; null for an order paid on-chain only.</summary>
    public string? Bolt11 { get; }

    /// <summary>Its payment hash (hex); null without <see cref="Bolt11"/>.</summary>
    public string? PaymentHash { get; }

    /// <summary>Its LUD-21 verify URL (https); null without <see cref="Bolt11"/>.</summary>
    public string? VerifyUrl { get; }

    /// <summary>The deposit address of the merchant's wallet the order leased (<c>bc1p…</c>), null without one.</summary>
    public string? OnchainAddress { get; }

    /// <summary>
    /// What the shop's page shows as one QR code: <c>bitcoin:&lt;address&gt;?amount=…&amp;lightning=…</c>
    /// with an address, else <c>lightning:&lt;bolt11&gt;</c>.
    /// </summary>
    public string PaymentUri { get; }

    /// <summary>The amount, in sats (a string of digits): the invoice's, and what the address must receive.</summary>
    public string Sats { get; }

    /// <summary>The order's price when it was given in a currency, else null.</summary>
    public Fiat? Fiat { get; }

    /// <summary>The rate <see cref="Fiat"/> was converted at, else null.</summary>
    public RateInfo? Rate { get; }

    /// <summary>What the merchant's app shows: the note, else the reference.</summary>
    public string Note { get; }

    /// <summary>Unix seconds: its BOLT11 timestamp; without <see cref="Bolt11"/>, when the address was leased.</summary>
    public long CreatedAt { get; }

    /// <summary>Unix seconds: when it can no longer be paid.</summary>
    public long ExpiresAt { get; }

    /// <summary>The URL of its checkout link (the order asked for one), else null.</summary>
    public string? CheckoutUrl { get; }

    /// <summary>Where its checkout link sends the customer once it is paid, else null.</summary>
    public string? RedirectUrl { get; }

    /// <summary>Its JSON (SPEC.md §3): store it with the order.</summary>
    public string ToJson() => ToJsonValue().Write();

    /// <summary>
    /// The Invoice a shop stored (its JSON, SPEC.md §3, as any implementation writes it). Throws
    /// <see cref="InvalidOrderException"/> (<c>invoice</c>) when it is not an Invoice's JSON; the
    /// payment check checks its values.
    /// </summary>
    public static Invoice FromJson(string json) => Read(StoredJson.Parse(json));

    JsonObject ISattlemeJson.ToJsonValue() => ToJsonValue();

    internal JsonObject ToJsonValue() => new JsonObject()
        .Set("ref", Ref)
        .Set("bolt11", Json.OfNullable(Bolt11))
        .Set("paymentHash", Json.OfNullable(PaymentHash))
        .Set("verifyUrl", Json.OfNullable(VerifyUrl))
        .Set("onchainAddress", Json.OfNullable(OnchainAddress))
        .Set("paymentUri", PaymentUri)
        .Set("sats", Sats)
        .Set("fiat", Fiat?.ToJsonValue() ?? (Json)Json.Null)
        .Set("rate", Rate?.ToJsonValue() ?? (Json)Json.Null)
        .Set("note", Note)
        .Set("createdAt", CreatedAt)
        .Set("expiresAt", ExpiresAt)
        .Set("checkoutUrl", Json.OfNullable(CheckoutUrl))
        .Set("redirectUrl", Json.OfNullable(RedirectUrl));

    internal static Invoice Read(Json? value)
    {
        var o = StoredJson.Object(value);
        return new Invoice(
            StoredJson.String(o, "ref"),
            StoredJson.NullableString(o, "bolt11"),
            StoredJson.NullableString(o, "paymentHash"),
            StoredJson.NullableString(o, "verifyUrl"),
            StoredJson.OptionalString(o, "onchainAddress"),
            StoredJson.String(o, "paymentUri"),
            StoredJson.String(o, "sats"),
            StoredJson.Nullable(o, "fiat", Fiat.Read),
            StoredJson.Nullable(o, "rate", RateInfo.Read),
            StoredJson.String(o, "note"),
            StoredJson.Integer(o, "createdAt"),
            StoredJson.Integer(o, "expiresAt"),
            StoredJson.NullableString(o, "checkoutUrl"),
            StoredJson.NullableString(o, "redirectUrl"));
    }
}

/// <summary>
/// An invoice declared for an order, as the merchant's registry holds it (FindInvoicesAsync,
/// <see cref="InvoicePendingException"/>, <see cref="OrderPaidException"/>): the fields of an
/// <see cref="Invoice"/> but <c>rate</c>, <c>checkoutUrl</c> and <c>redirectUrl</c>, which only the
/// shop keeps, and <see cref="DeclaredAt"/>.
/// </summary>
[JsonConverter(typeof(SattlemeJsonConverter<DeclaredInvoice>))]
public sealed class DeclaredInvoice : IInvoiceToCheck, ISattlemeJson
{
    internal DeclaredInvoice(
        string reference,
        string? bolt11,
        string? paymentHash,
        string? verifyUrl,
        string? onchainAddress,
        string paymentUri,
        string sats,
        Fiat? fiat,
        string note,
        long createdAt,
        long expiresAt,
        long declaredAt)
    {
        Ref = reference;
        Bolt11 = bolt11;
        PaymentHash = paymentHash;
        VerifyUrl = verifyUrl;
        OnchainAddress = onchainAddress;
        PaymentUri = paymentUri;
        Sats = sats;
        Fiat = fiat;
        Note = note;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        DeclaredAt = declaredAt;
    }

    /// <inheritdoc cref="Invoice.Ref"/>
    public string Ref { get; }

    /// <inheritdoc cref="Invoice.Bolt11"/>
    public string? Bolt11 { get; }

    /// <inheritdoc cref="Invoice.PaymentHash"/>
    public string? PaymentHash { get; }

    /// <inheritdoc cref="Invoice.VerifyUrl"/>
    public string? VerifyUrl { get; }

    /// <inheritdoc cref="Invoice.OnchainAddress"/>
    public string? OnchainAddress { get; }

    /// <inheritdoc cref="Invoice.PaymentUri"/>
    public string PaymentUri { get; }

    /// <inheritdoc cref="Invoice.Sats"/>
    public string Sats { get; }

    /// <inheritdoc cref="Invoice.Fiat"/>
    public Fiat? Fiat { get; }

    /// <inheritdoc cref="Invoice.Note"/>
    public string Note { get; }

    /// <inheritdoc cref="Invoice.CreatedAt"/>
    public long CreatedAt { get; }

    /// <inheritdoc cref="Invoice.ExpiresAt"/>
    public long ExpiresAt { get; }

    /// <summary>Unix seconds: when it was declared (without <see cref="Bolt11"/>, when its lease was marked confirmed).</summary>
    public long DeclaredAt { get; }

    /// <summary>Its JSON (SPEC.md §3).</summary>
    public string ToJson() => ToJsonValue().Write();

    /// <summary>A DeclaredInvoice from its JSON; <see cref="InvalidOrderException"/> (<c>invoice</c>) when it is not one.</summary>
    public static DeclaredInvoice FromJson(string json) => Read(StoredJson.Parse(json));

    JsonObject ISattlemeJson.ToJsonValue() => ToJsonValue();

    internal JsonObject ToJsonValue() => new JsonObject()
        .Set("ref", Ref)
        .Set("bolt11", Json.OfNullable(Bolt11))
        .Set("paymentHash", Json.OfNullable(PaymentHash))
        .Set("verifyUrl", Json.OfNullable(VerifyUrl))
        .Set("onchainAddress", Json.OfNullable(OnchainAddress))
        .Set("paymentUri", PaymentUri)
        .Set("sats", Sats)
        .Set("fiat", Fiat?.ToJsonValue() ?? (Json)Json.Null)
        .Set("note", Note)
        .Set("createdAt", CreatedAt)
        .Set("expiresAt", ExpiresAt)
        .Set("declaredAt", DeclaredAt);

    internal static DeclaredInvoice Read(Json? value)
    {
        var o = StoredJson.Object(value);
        return new DeclaredInvoice(
            StoredJson.String(o, "ref"),
            StoredJson.NullableString(o, "bolt11"),
            StoredJson.NullableString(o, "paymentHash"),
            StoredJson.NullableString(o, "verifyUrl"),
            StoredJson.OptionalString(o, "onchainAddress"),
            StoredJson.String(o, "paymentUri"),
            StoredJson.String(o, "sats"),
            StoredJson.Nullable(o, "fiat", Fiat.Read),
            StoredJson.String(o, "note"),
            StoredJson.Integer(o, "createdAt"),
            StoredJson.Integer(o, "expiresAt"),
            StoredJson.Integer(o, "declaredAt"));
    }
}

/// <summary>
/// What a payment check needs of an invoice, read from what a shop stored: an Invoice's or a
/// DeclaredInvoice's JSON, of this implementation or another, of which it reads these fields alone
/// (<c>onchainAddress</c> absent reads as null; any other absent, it is not such JSON). Its own JSON
/// (<see cref="ToJson"/>, or System.Text.Json) holds these fields with the names of SPEC.md §3.
/// </summary>
[JsonConverter(typeof(SattlemeJsonConverter<InvoiceToCheck>))]
public sealed class InvoiceToCheck : IInvoiceToCheck, ISattlemeJson
{
    /// <summary>The fields a payment check needs.</summary>
    public InvoiceToCheck(string? reference, string? bolt11, string? paymentHash, string? verifyUrl, string? onchainAddress, string sats, long expiresAt)
    {
        Ref = reference;
        Bolt11 = bolt11;
        PaymentHash = paymentHash;
        VerifyUrl = verifyUrl;
        OnchainAddress = onchainAddress;
        Sats = sats;
        ExpiresAt = expiresAt;
    }

    /// <inheritdoc/>
    public string? Ref { get; }

    /// <inheritdoc/>
    public string? Bolt11 { get; }

    /// <inheritdoc/>
    public string? PaymentHash { get; }

    /// <inheritdoc/>
    public string? VerifyUrl { get; }

    /// <inheritdoc/>
    public string? OnchainAddress { get; }

    /// <inheritdoc/>
    public string Sats { get; }

    /// <inheritdoc/>
    public long ExpiresAt { get; }

    /// <summary>
    /// What a payment check needs of a stored invoice's JSON. Throws
    /// <see cref="InvalidOrderException"/> (<c>invoice</c>) when it does not hold them: every member
    /// the check reads is there (SPEC.md §22), <c>bolt11</c>, <c>paymentHash</c> and <c>verifyUrl</c>
    /// null when there is none, but <c>onchainAddress</c>, which may be left out, and <c>ref</c>,
    /// read with an address only (where it must be a reference).
    /// </summary>
    public static InvoiceToCheck FromJson(string json) => Read(StoredJson.Parse(json));

    /// <summary>
    /// Its JSON: <c>ref</c> (null when it has none), <c>bolt11</c>, <c>paymentHash</c>,
    /// <c>verifyUrl</c>, <c>onchainAddress</c>, <c>sats</c> and <c>expiresAt</c>, as SPEC.md §3 names
    /// them: what <see cref="FromJson"/> reads back.
    /// </summary>
    public string ToJson() => ToJsonValue().Write();

    /// <inheritdoc/>
    public override string ToString() => ToJson();

    JsonObject ISattlemeJson.ToJsonValue() => ToJsonValue();

    internal JsonObject ToJsonValue() => Of(this);

    /// <summary>The JSON of what a payment check needs of <paramref name="invoice"/>.</summary>
    internal static JsonObject Of(IInvoiceToCheck invoice) => new JsonObject()
        .Set("ref", Json.OfNullable(invoice.Ref))
        .Set("bolt11", Json.OfNullable(invoice.Bolt11))
        .Set("paymentHash", Json.OfNullable(invoice.PaymentHash))
        .Set("verifyUrl", Json.OfNullable(invoice.VerifyUrl))
        .Set("onchainAddress", Json.OfNullable(invoice.OnchainAddress))
        .Set("sats", invoice.Sats)
        .Set("expiresAt", invoice.ExpiresAt);

    internal static InvoiceToCheck Read(Json? value)
    {
        var o = StoredJson.Object(value);
        return new InvoiceToCheck(
            // Read with an address only (SPEC.md §22: the others are not read): anything but a
            // string is no reference, which the check refuses with an address, and ignores without.
            JsonRead.String(o["ref"]),
            StoredJson.NullableString(o, "bolt11"),
            StoredJson.NullableString(o, "paymentHash"),
            StoredJson.NullableString(o, "verifyUrl"),
            StoredJson.OptionalString(o, "onchainAddress"),
            StoredJson.String(o, "sats"),
            StoredJson.Integer(o, "expiresAt"));
    }
}

/// <summary>
/// What is left to pay of an invoice underpaid on-chain (<see cref="PaymentStatus.Open"/>): its sats,
/// a Lightning invoice of exactly that declared for the order (null while there is none), and what
/// to show as one QR code: <c>bitcoin:&lt;address&gt;?amount=…&amp;lightning=…</c> from 1 000 sats,
/// <c>lightning:&lt;bolt11&gt;</c> below; null with neither.
/// </summary>
[JsonConverter(typeof(SattlemeJsonConverter<Rest>))]
public sealed class Rest : ISattlemeJson
{
    /// <summary>A rest.</summary>
    public Rest(string sats, string? bolt11, string? paymentUri)
    {
        Sats = sats;
        Bolt11 = bolt11;
        PaymentUri = paymentUri;
    }

    /// <summary>What is left, in sats: the amount less what was received, whatever the merchant's tolerance.</summary>
    public string Sats { get; }

    /// <summary>A Lightning invoice of exactly that, declared for the order; null while there is none.</summary>
    public string? Bolt11 { get; }

    /// <summary>What to show as one QR code; null with neither an on-chain rest nor an invoice.</summary>
    public string? PaymentUri { get; }

    /// <summary>Its JSON (SPEC.md §3).</summary>
    public string ToJson() => ToJsonValue().Write();

    JsonObject ISattlemeJson.ToJsonValue() => ToJsonValue();

    internal JsonObject ToJsonValue() => new JsonObject()
        .Set("sats", Sats)
        .Set("bolt11", Json.OfNullable(Bolt11))
        .Set("paymentUri", Json.OfNullable(PaymentUri));

    internal static Rest Read(Json? value)
    {
        var o = StoredJson.Object(value);
        return new Rest(StoredJson.String(o, "sats"), StoredJson.NullableString(o, "bolt11"), StoredJson.NullableString(o, "paymentUri"));
    }
}

/// <summary>Where the payment of an invoice is (<see cref="PaymentStatus.State"/>).</summary>
public enum PaymentState
{
    /// <summary>Not paid yet: it may still be, until its end, and is reported up to 120 s later.</summary>
    Open,

    /// <summary>Enough was received in time on-chain, not 3 confirmations deep yet: not paid yet.</summary>
    Unconfirmed,

    /// <summary>Paid: in Lightning, on-chain (3 confirmations on the explorer read), or both.</summary>
    Paid,

    /// <summary>Never paid, for good.</summary>
    Expired,
}

/// <summary>How an invoice was paid (<see cref="PaymentStatus.Paid.Method"/>).</summary>
public enum PaymentMethod
{
    /// <summary>In Lightning, by its own invoice: the preimage is the proof of payment.</summary>
    Lightning,

    /// <summary>On-chain, by transactions to its address.</summary>
    Onchain,

    /// <summary>On-chain, and in Lightning by invoices of the rest of that underpayment.</summary>
    Mixed,
}

/// <summary>
/// Where the payment of an invoice is (SPEC.md §3): <see cref="Open"/>, <see cref="Unconfirmed"/>,
/// <see cref="Paid"/> or <see cref="Expired"/>. System.Text.Json writes each as SPEC.md §3's JSON,
/// whether it is typed as <see cref="PaymentStatus"/>, as its own class or as <c>object</c>. <c>Received</c> is what the order received in
/// time: on-chain to its address, and by the Lightning invoices of the rest of an on-chain
/// underpayment; below the amount in <see cref="Unconfirmed"/> and <see cref="Paid"/> when the
/// merchant's tolerance of on-chain underpayments, or the merchant's acceptance of what came (in its
/// app), accepted it: the order is paid all the same.
/// </summary>
[JsonConverter(typeof(SattlemeJsonConverter<PaymentStatus>))]
#pragma warning disable CA1034 // The states are read as PaymentStatus.Open, PaymentStatus.Paid…
public abstract class PaymentStatus : ISattlemeJson
{
    private protected PaymentStatus()
    {
    }

    /// <summary>Which state this is.</summary>
    public abstract PaymentState State { get; }

    /// <summary>Its JSON (SPEC.md §3).</summary>
    public string ToJson() => ToJsonValue().Write();

    /// <summary>A status from its JSON; <see cref="InvalidOrderException"/> (<c>invoice</c>) when it is not one.</summary>
    public static PaymentStatus FromJson(string json) => Read(StoredJson.Parse(json));

    /// <inheritdoc/>
    public override string ToString() => ToJson();

    JsonObject ISattlemeJson.ToJsonValue() => ToJsonValue();

    internal abstract JsonObject ToJsonValue();

    internal static PaymentStatus Read(Json? value)
    {
        var o = StoredJson.Object(value);
        switch (StoredJson.String(o, "state"))
        {
            case "open":
                return new Open(StoredJson.Integer(o, "expiresAt"), StoredJson.String(o, "received"), StoredJson.Nullable(o, "rest", Rest.Read));
            case "unconfirmed":
                return new Unconfirmed(StoredJson.String(o, "received"), (int)StoredJson.Integer(o, "confirmations", 0, int.MaxValue), StoredJson.Strings(o, "txids"));
            case "expired":
                return new Expired(StoredJson.String(o, "received"));
            case "paid":
                return StoredJson.String(o, "method") switch
                {
                    "lightning" => Paid.InLightning(StoredJson.String(o, "preimage")),
                    "onchain" => Paid.Onchain(StoredJson.String(o, "received"), StoredJson.Strings(o, "txids")),
                    "mixed" => Paid.Mixed(StoredJson.String(o, "received"), StoredJson.Strings(o, "txids"), StoredJson.Strings(o, "preimages")),
                    _ => throw StoredJson.Invalid(),
                };
            default:
                throw StoredJson.Invalid();
        }
    }

    /// <summary>
    /// Not paid (yet): it may still be, until <see cref="ExpiresAt"/>, and is reported up to 120 s
    /// later. <see cref="Received"/> above 0: an on-chain underpayment, whose <see cref="Rest"/> is
    /// still payable until <see cref="ExpiresAt"/>.
    /// </summary>
    [JsonConverter(typeof(SattlemeJsonConverter<Open>))]
    public sealed class Open : PaymentStatus
    {
        /// <summary>An open status.</summary>
        public Open(long expiresAt, string received, Rest? rest)
        {
            ExpiresAt = expiresAt;
            Received = received;
            Rest = rest;
        }

        /// <inheritdoc/>
        public override PaymentState State => PaymentState.Open;

        /// <summary>Unix seconds: the invoice's end.</summary>
        public long ExpiresAt { get; }

        /// <summary>What was received in time, in sats; "0" without an address.</summary>
        public string Received { get; }

        /// <summary>What is left to pay of an underpayment before the invoice's end, else null.</summary>
        public Rest? Rest { get; }

        internal override JsonObject ToJsonValue() => new JsonObject()
            .Set("state", "open")
            .Set("expiresAt", ExpiresAt)
            .Set("received", Received)
            .Set("rest", Rest?.ToJsonValue() ?? (Json)Json.Null);
    }

    /// <summary>
    /// Enough was received in time (the amount, or less within the merchant's tolerance or down to
    /// what the merchant accepted), on-chain by the transactions <see cref="Txids"/> (and by invoices
    /// of the rest), not deep enough yet (3 confirmations): not paid yet, it may still fail (a
    /// transaction replaced or dropped).
    /// </summary>
    [JsonConverter(typeof(SattlemeJsonConverter<Unconfirmed>))]
    public sealed class Unconfirmed : PaymentStatus
    {
        /// <summary>An unconfirmed status.</summary>
        public Unconfirmed(string received, int confirmations, IReadOnlyList<string> txids)
        {
            Received = received;
            Confirmations = confirmations;
            Txids = txids;
        }

        /// <inheritdoc/>
        public override PaymentState State => PaymentState.Unconfirmed;

        /// <summary>What was received in time, in sats.</summary>
        public string Received { get; }

        /// <summary>The least confirmations of the order's transactions.</summary>
        public int Confirmations { get; }

        /// <summary>The order's transactions.</summary>
        public IReadOnlyList<string> Txids { get; }

        internal override JsonObject ToJsonValue() => new JsonObject()
            .Set("state", "unconfirmed")
            .Set("received", Received)
            .Set("confirmations", Confirmations)
            .Set("txids", StoredJson.ArrayOf(Txids));
    }

    /// <summary>
    /// Paid: in Lightning (<see cref="Preimage"/>, the proof of payment, to keep with the order),
    /// on-chain by <see cref="Txids"/> (3 confirmations on the explorer read), or mixed: on-chain by
    /// <see cref="Txids"/> (none when the invoices of the rest alone pay it) and in Lightning by
    /// invoices of the rest of that underpayment, whose <see cref="Preimages"/> are the proofs.
    /// </summary>
    [JsonConverter(typeof(SattlemeJsonConverter<Paid>))]
    public sealed class Paid : PaymentStatus
    {
        private Paid(PaymentMethod method, string? preimage, string? received, IReadOnlyList<string> txids, IReadOnlyList<string> preimages)
        {
            Method = method;
            Preimage = preimage;
            Received = received;
            Txids = txids;
            Preimages = preimages;
        }

        /// <summary>Paid in Lightning, with <paramref name="preimage"/> (hex).</summary>
        public static Paid InLightning(string preimage) => new(PaymentMethod.Lightning, preimage, null, [], []);

        /// <summary>Paid on-chain by <paramref name="txids"/>.</summary>
        public static Paid Onchain(string received, IReadOnlyList<string> txids) => new(PaymentMethod.Onchain, null, received, txids, []);

        /// <summary>Paid on-chain by <paramref name="txids"/> and by invoices of the rest, whose <paramref name="preimages"/> prove it.</summary>
        public static Paid Mixed(string received, IReadOnlyList<string> txids, IReadOnlyList<string> preimages) =>
            new(PaymentMethod.Mixed, null, received, txids, preimages);

        /// <inheritdoc/>
        public override PaymentState State => PaymentState.Paid;

        /// <summary>How it was paid.</summary>
        public PaymentMethod Method { get; }

        /// <summary>The preimage of its Lightning payment (hex, lowercase): only in Lightning.</summary>
        public string? Preimage { get; }

        /// <summary>What was received in time, in sats: on-chain or mixed; null in Lightning.</summary>
        public string? Received { get; }

        /// <summary>The transactions that paid it on-chain; empty in Lightning.</summary>
        public IReadOnlyList<string> Txids { get; }

        /// <summary>The preimages of the invoices of the rest that paid it: mixed only.</summary>
        public IReadOnlyList<string> Preimages { get; }

        internal override JsonObject ToJsonValue()
        {
            var json = new JsonObject().Set("state", "paid");
            switch (Method)
            {
                case PaymentMethod.Lightning:
                    return json.Set("method", "lightning").Set("preimage", Preimage!);
                case PaymentMethod.Onchain:
                    return json.Set("method", "onchain").Set("received", Received!).Set("txids", StoredJson.ArrayOf(Txids));
                default:
                    return json.Set("method", "mixed").Set("received", Received!).Set("txids", StoredJson.ArrayOf(Txids))
                        .Set("preimages", StoredJson.ArrayOf(Preimages));
            }
        }
    }

    /// <summary>
    /// Never paid, for good: the order may get a new invoice. <see cref="Received"/> above 0: the
    /// merchant received that much, less than the amount (settle it with the customer).
    /// </summary>
    [JsonConverter(typeof(SattlemeJsonConverter<Expired>))]
    public sealed class Expired : PaymentStatus
    {
        /// <summary>An expired status.</summary>
        public Expired(string received) => Received = received;

        /// <inheritdoc/>
        public override PaymentState State => PaymentState.Expired;

        /// <summary>What was received in time, in sats; "0" without an address.</summary>
        public string Received { get; }

        internal override JsonObject ToJsonValue() => new JsonObject().Set("state", "expired").Set("received", Received);
    }
}
#pragma warning restore CA1034

/// <summary>The merchant, as the integration's channel, Lightning address and lot give it.</summary>
[JsonConverter(typeof(SattlemeJsonConverter<Merchant>))]
public sealed class Merchant : ISattlemeJson
{
    /// <summary>A merchant.</summary>
    public Merchant(string businessName, string minSats, string maxSats, int onchainAddresses)
    {
        BusinessName = businessName;
        MinSats = minSats;
        MaxSats = maxSats;
        OnchainAddresses = onchainAddresses;
    }

    /// <summary>The business's name.</summary>
    public string BusinessName { get; }

    /// <summary>Smallest invoice the Lightning address makes, in sats (a string of digits).</summary>
    public string MinSats { get; }

    /// <summary>Largest invoice the Lightning address makes, in sats (a string of digits).</summary>
    public string MaxSats { get; }

    /// <summary>The deposit addresses of the integration's lot, leased or not: 0 takes no on-chain payment.</summary>
    public int OnchainAddresses { get; }

    /// <summary>Its JSON (SPEC.md §3).</summary>
    public string ToJson() => ToJsonValue().Write();

    /// <summary>A Merchant from its JSON; <see cref="InvalidOrderException"/> (<c>invoice</c>) when it is not one.</summary>
    public static Merchant FromJson(string json) => Read(StoredJson.Parse(json));

    JsonObject ISattlemeJson.ToJsonValue() => ToJsonValue();

    internal JsonObject ToJsonValue() => new JsonObject()
        .Set("businessName", BusinessName)
        .Set("minSats", MinSats)
        .Set("maxSats", MaxSats)
        .Set("onchainAddresses", OnchainAddresses);

    internal static Merchant Read(Json? value)
    {
        var o = StoredJson.Object(value);
        return new Merchant(StoredJson.String(o, "businessName"), StoredJson.String(o, "minSats"), StoredJson.String(o, "maxSats"),
            (int)StoredJson.Integer(o, "onchainAddresses", 0, int.MaxValue));
    }
}

/// <summary>A type of the data model, as JSON.</summary>
internal interface ISattlemeJson
{
    JsonObject ToJsonValue();
}

/// <summary>
/// System.Text.Json reads and writes the data model as its own JSON (SPEC.md §3), whatever the
/// serializer's options (a naming policy never renames its fields). Reading keeps SPEC.md §2's
/// rules: of members with one name the last counts, numbers are doubles, a lone surrogate stays one.
/// </summary>
internal sealed class SattlemeJsonConverter<T> : JsonConverter<T>
    where T : class, ISattlemeJson
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = StoredJson.Parse(document.RootElement.GetRawText());
        object read = typeof(T) switch
        {
            var t when t == typeof(Invoice) => Invoice.Read(value),
            var t when t == typeof(DeclaredInvoice) => DeclaredInvoice.Read(value),
            var t when t == typeof(Fiat) => Fiat.Read(value),
            var t when t == typeof(RateInfo) => RateInfo.Read(value),
            var t when t == typeof(Rest) => Rest.Read(value),
            var t when t == typeof(Merchant) => Merchant.Read(value),
            var t when t == typeof(InvoiceToCheck) => InvoiceToCheck.Read(value),
            _ => PaymentStatus.Read(value),
        };
        // A status of another state than the class asked (an Open read as a Paid): not that JSON.
        return read as T ?? throw StoredJson.Invalid($"not a {typeof(T).Name} status");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteRawValue(value.ToJsonValue().Write(), skipInputValidation: true);
    }
}

/// <summary>
/// System.Text.Json and an <see cref="IInvoiceToCheck"/>: written as its own JSON (an Invoice's, a
/// DeclaredInvoice's, an InvoiceToCheck's; of an integrator's own class, the fields a payment check
/// needs), read as an <see cref="InvoiceToCheck"/>.
/// </summary>
internal sealed class InvoiceToCheckJsonConverter : JsonConverter<IInvoiceToCheck>
{
    public override IInvoiceToCheck Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return InvoiceToCheck.Read(StoredJson.Parse(document.RootElement.GetRawText()));
    }

    public override void Write(Utf8JsonWriter writer, IInvoiceToCheck value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        var json = value is ISattlemeJson known ? known.ToJsonValue() : InvoiceToCheck.Of(value);
        writer.WriteRawValue(json.Write(), skipInputValidation: true);
    }
}

/// <summary>Reading what a shop stored: anything that is not the JSON of SPEC.md §3 is invalid_order (<c>invoice</c>).</summary>
internal static class StoredJson
{
    private const string NotStored = "not an invoice made by CreateInvoiceAsync";

    public static InvalidOrderException Invalid(string message = NotStored) => new("invoice", message);

    public static Json Parse(string json) => Json.Parse(json ?? throw Invalid()) ?? throw Invalid("not JSON");

    public static JsonObject Object(Json? value) => value as JsonObject ?? throw Invalid();

    public static string String(JsonObject o, string name) => JsonRead.String(o[name]) ?? throw Invalid($"{NotStored}: {name}");

    /// <summary>
    /// A member SPEC.md §3 writes as a string or null: absent, it is not that JSON (absent is not
    /// null where SPEC.md says null).
    /// </summary>
    public static string? NullableString(JsonObject o, string name) => o[name] switch
    {
        JsonNull => null,
        JsonString s => s.Value,
        _ => throw Invalid($"{NotStored}: {name}"),
    };

    /// <summary>A member a stored invoice may lack (<c>onchainAddress</c> alone, SPEC.md §22): absent reads as null.</summary>
    public static string? OptionalString(JsonObject o, string name) => o[name] is null ? null : NullableString(o, name);

    public static long Integer(JsonObject o, string name, long min = -Protocol.MaxSafeInteger, long max = Protocol.MaxSafeInteger) =>
        JsonRead.SafeInteger(o[name]) is { } value && value >= min && value <= max ? value : throw Invalid($"{NotStored}: {name}");

    /// <summary>A member SPEC.md §3 writes as an integer or null: absent, it is not that JSON.</summary>
    public static long? NullableInteger(JsonObject o, string name) => o[name] is JsonNull ? null : Integer(o, name);

    /// <summary>A member SPEC.md §3 writes as an object or null: absent, it is not that JSON.</summary>
    public static T? Nullable<T>(JsonObject o, string name, Func<Json?, T> read)
        where T : class => o[name] switch
        {
            null => throw Invalid($"{NotStored}: {name}"),
            JsonNull => null,
            var value => read(value),
        };

    public static IReadOnlyList<string> Strings(JsonObject o, string name)
    {
        if (o[name] is not JsonArray array)
        {
            throw Invalid($"{NotStored}: {name}");
        }
        return array.Items.Select(item => JsonRead.String(item) ?? throw Invalid($"{NotStored}: {name}")).ToList();
    }

    public static JsonArray ArrayOf(IEnumerable<string> items) => new(items.Select(item => (Json)Json.Of(item)));
}
