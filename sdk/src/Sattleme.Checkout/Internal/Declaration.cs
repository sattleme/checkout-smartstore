using System;
using Sattleme.Checkout.Internal.Crypto;
using Sattleme.Checkout.Internal.Lightning;
using Sattleme.Checkout.Internal.Nostr;
using Sattleme.Checkout.Internal.Onchain;

namespace Sattleme.Checkout.Internal;

/// <summary>The invoice an invoice of the rest is of: its deposit address and its end (Unix seconds).</summary>
internal sealed record RestOf(string Address, long ExpiresAt)
{
    public JsonObject ToJson() => new JsonObject().Set("address", Address).Set("expiresAt", ExpiresAt);
}

/// <summary>A declaration of the sale, opened by its holder.</summary>
internal sealed record OpenedDeclaration(
    NostrEvent Event,
    string Invoice,
    Bolt11Invoice Decoded,
    string VerifyUrl,
    DeclaredPrice Price,
    string Note,
    long DeclaredAt,
    RestOf? Rest);

/// <summary>A declaration's inside is too large for a NIP-44 plaintext (65 535 bytes): it cannot be declared.</summary>
internal sealed class TooLargeException(string message, Exception innerException) : Exception(message, innerException);

/// <summary>
/// The declaration of an invoice in the merchant's registry of payments (SPEC.md §20), as the
/// merchant's app reads it: published on a majority of the merchant's relays before the invoice is
/// shown, so that its payment counts for this order's sale and for no other. Its format is the
/// app's, version 1: changing it needs a new version there first, but for an optional field the app
/// ignores (as <c>taker</c>): <c>rest</c>, in a rest invoice's.
/// </summary>
internal static class Declarations
{
    /// <summary>
    /// The signed declaration of an invoice for <paramref name="sale"/>: the declaration (inside,
    /// NIP-44 with the ECDH conversation key of the sale's secret and the merchant's reading key),
    /// then <c>{"t": "registry-declaration", "v": {"sealed": …}}</c> padded to a multiple of 256
    /// bytes (outside, NIP-44 with the outer key), kind 30078, <c>d</c> = the registry address,
    /// signed by the sale's secret. Throws <see cref="TooLargeException"/> when it does not fit a
    /// NIP-44 plaintext.
    /// </summary>
    public static NostrEvent Event(Sale sale, string invoice, string verifyUrl, string paymentHash, DeclaredPrice price, string note,
        long createdAt, RestOf? rest, RandomSource random)
    {
        var declaration = new JsonObject()
            .Set("version", Protocol.DeclarationVersion)
            .Set("invoice", invoice)
            .Set("verifyUrl", verifyUrl)
            .Set("sale", new JsonObject().Set("key", Text.Hex(sale.Key)).Set("fiat", price.ToJson()).Set("note", note))
            // "By" in the app: this integration.
            .Set("taker", sale.Taker);
        if (rest is not null)
        {
            // Only in a rest invoice's: the app ignores it, and counts the payment for the sale.
            declaration.Set("rest", rest.ToJson());
        }
        var address = Derive.RegistryAddress(sale.Registry.Key, paymentHash);
        try
        {
            return Records.Seal(
                Protocol.DeclarationRecordType,
                new JsonObject().Set("sealed", Nip44.Encrypt(declaration.Write(), sale.InnerKey, random)),
                sale.SecretKey,
                Derive.OuterKey(sale.Registry.Key, address, sale.Author),
                address,
                createdAt,
                random);
        }
        catch (ArgumentException error)
        {
            throw new TooLargeException("The invoice is too large to be declared", error);
        }
    }

    /// <summary>
    /// What a relay sent, if it is a declaration of <paramref name="sale"/> (SPEC.md §20,
    /// "Opening"): author, <c>d</c> tag (the address of its own invoice), outer and inner encryption,
    /// signature, and the sale's own key inside. Null otherwise; throws
    /// <see cref="NewerRecordException"/> for one of a newer format.
    /// </summary>
    public static OpenedDeclaration? Open(Json? value, Sale sale)
    {
        if (Records.DTagOf(value) is not { } d)
        {
            return null;
        }
        var opened = Records.Open(
            value,
            Protocol.DeclarationRecordType,
            Derive.OuterKey(sale.Registry.Key, d, sale.Author),
            d,
            sale.Author,
            (outside, @event) => ParseInside(outside, @event, sale, d));
        if (opened is { Newer: true })
        {
            throw new NewerRecordException();
        }
        return opened?.Value;
    }

    private static OpenedDeclaration? ParseInside(Json outside, NostrEvent @event, Sale sale, string d)
    {
        if (outside is not JsonObject o || JsonRead.String(o["sealed"]) is not { } sealedText)
        {
            return null;
        }
        Json? inside;
        try
        {
            inside = Json.Parse(Nip44.Decrypt(sealedText, sale.InnerKey));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (UnknownNip44VersionException)
        {
            return null;
        }
        if (inside is not JsonObject v)
        {
            return null;
        }
        if (JsonRead.IsNewerVersion(v["version"], Protocol.DeclarationVersion))
        {
            throw new NewerRecordException();
        }
        if (!JsonRead.IsNumber(v["version"], Protocol.DeclarationVersion) || JsonRead.String(v["invoice"]) is not { } invoice)
        {
            return null;
        }
        if (JsonRead.String(v["verifyUrl"]) is not { } verifyUrl || Urls.ParseHttps(verifyUrl) is null)
        {
            return null;
        }
        if (v["sale"] is not JsonObject saleValue || JsonRead.String(saleValue["key"]) != Text.Hex(sale.Key)
            || JsonRead.String(saleValue["note"]) is not { } note)
        {
            return null;
        }
        if (DeclaredPrice.Parse(saleValue["fiat"], 0) is not { } price)
        {
            return null;
        }
        if (!TryParseRest(v, out var rest))
        {
            return null;
        }
        var decoded = Bolt11.Decode(invoice);
        if (decoded is null || decoded.Network != "bc" || decoded.AmountMsat is not { } msat || msat <= 0 || msat % 1000 != 0)
        {
            return null;
        }
        if (d != Derive.RegistryAddress(sale.Registry.Key, decoded.PaymentHash))
        {
            return null;
        }
        return new OpenedDeclaration(@event, invoice, decoded, verifyUrl, price, note, @event.CreatedAt, rest);
    }

    /// <summary>A declaration's <c>rest</c>: absent (null), or the invoice it names. False when invalid.</summary>
    private static bool TryParseRest(JsonObject declaration, out RestOf? rest)
    {
        rest = null;
        if (!declaration.Has("rest"))
        {
            return true;
        }
        if (declaration["rest"] is not JsonObject value)
        {
            return false;
        }
        var address = JsonRead.String(value["address"]);
        if (!Address.IsDepositAddress(address))
        {
            return false;
        }
        if (JsonRead.SafeInteger(value["expiresAt"]) is not { } expiresAt || expiresAt < 0)
        {
            return false;
        }
        rest = new RestOf(address!, expiresAt);
        return true;
    }
}
