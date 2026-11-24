using System;
using System.Diagnostics;
using Sattleme.Checkout.Internal.Crypto;

namespace Sattleme.Checkout.Internal;

/// <summary>
/// The keys an integration's server derives (SPEC.md §10): HKDF-SHA256 (RFC 5869), 32 bytes, salt
/// and info in UTF-8. They never change: the integration's channel, its sales and their
/// declarations derive from them, in the merchant's app as here (checked against the app's vectors,
/// spec/vectors/app/app.json). Every one is a secret, but the authors (public keys) and addresses.
/// </summary>
internal static class Derive
{
    /// <summary>The integration's channel: its <c>d</c> tag (hex) and its NIP-44 conversation key.</summary>
    public static (string Address, byte[] EncryptionKey) ChannelKeys(byte[] integrationKey) => (
        Text.Hex(Hashes.Hkdf(integrationKey, Protocol.IntegrationSalt, Protocol.ChannelAddressInfo)),
        Hashes.Hkdf(integrationKey, Protocol.IntegrationSalt, Protocol.ChannelEncryptionInfo));

    /// <summary>Where the integration's lot of deposit addresses is: its <c>d</c> tag (hex).</summary>
    public static string LotAddress(byte[] integrationKey) =>
        Text.Hex(Hashes.Hkdf(integrationKey, Protocol.IntegrationSalt, Protocol.LotAddressInfo));

    /// <summary>The seen secret (it signs witnesses), its public key (their author), the key of their addresses.</summary>
    public static (byte[] SecretKey, string Author, byte[] AddressKey) SeenKeys(byte[] integrationKey)
    {
        var secret = Hashes.Hkdf(integrationKey, Protocol.IntegrationSalt, Protocol.SeenSecretInfo);
        return (secret, Secp256k1.PublicKeyOf(secret), Hashes.Hkdf(integrationKey, Protocol.IntegrationSalt, Protocol.SeenAddressKeyInfo));
    }

    /// <summary>The key that marks the integration's declarations ("By" in the app).</summary>
    public static byte[] TakerKey(byte[] integrationKey) => Hashes.Hkdf(integrationKey, Protocol.IntegrationSalt, Protocol.TakerInfo);

    /// <summary>The sale of an order: its own key, its Nostr secret and public key (the declarations' author).</summary>
    public static (byte[] Key, byte[] SecretKey, string Author) SaleKeys(byte[] integrationKey, string reference)
    {
        var key = Hashes.Hkdf(integrationKey, Protocol.IntegrationSalt, Protocol.SaleInfo(reference));
        var secret = Hashes.Hkdf(key, Protocol.SaleSalt, Protocol.SaleNostrSecretInfo);
        return (key, secret, Secp256k1.PublicKeyOf(secret));
    }

    /// <summary>
    /// The keys of the rest records of the order <paramref name="reference"/>'s invoice with the
    /// deposit address <paramref name="address"/> and the end <paramref name="expiresAt"/> (SPEC.md
    /// §31, "Rest records"): secrets but the author.
    /// </summary>
    public static RestKeys RestKeys(byte[] integrationKey, string reference, string address, long expiresAt)
    {
        var key = Hashes.Hkdf(integrationKey, Protocol.IntegrationSalt, Protocol.RestKeyInfo(reference, address, expiresAt));
        var secret = Hashes.Hkdf(integrationKey, Protocol.IntegrationSalt, Protocol.RestSecretInfo(reference, address, expiresAt));
        return new RestKeys(key, secret, Secp256k1.PublicKeyOf(secret), Hashes.Hkdf(key, Protocol.CheckoutSalt, Protocol.RestEncryptionInfo));
    }

    /// <summary>The <c>d</c> tag (hex) of the rest record of the invoice whose payment hash is <paramref name="paymentHash"/>.</summary>
    public static string RestRecordAddress(byte[] restKey, string paymentHash) =>
        Text.Hex(Hashes.Hkdf(restKey, Protocol.CheckoutSalt, Protocol.RestAddressInfo(paymentHash)));

    /// <summary>The <c>d</c> tag (hex) of the acceptance record of the invoice whose rest key is <paramref name="restKey"/>.</summary>
    public static string AcceptanceRecordAddress(byte[] restKey) =>
        Text.Hex(Hashes.Hkdf(restKey, Protocol.CheckoutSalt, Protocol.AcceptanceAddressInfo));

    /// <summary>
    /// The <c>d</c> tag (hex) of the merchant's acceptance of the invoice of the sale
    /// <paramref name="saleAuthor"/> with the deposit address <paramref name="address"/> and the end
    /// <paramref name="expiresAt"/> (SPEC.md §31, "The merchant's acceptance"): one per invoice.
    /// </summary>
    public static string MerchantAcceptanceAddress(byte[] integrationKey, string saleAuthor, string address, long expiresAt) =>
        Text.Hex(Hashes.Hkdf(integrationKey, Protocol.IntegrationSalt, Protocol.MerchantAcceptanceInfo(saleAuthor, address, expiresAt)));

    /// <summary>d(H): where the declarations of payment hash <paramref name="paymentHash"/> (hex) are.</summary>
    public static string RegistryAddress(byte[] registryKey, string paymentHash) =>
        Text.Hex(Hashes.HmacSha256(registryKey, Text.FromHex(paymentHash)));

    /// <summary>The outer key of a record at <paramref name="address"/> by <paramref name="author"/> (hex).</summary>
    public static byte[] OuterKey(byte[] registryKey, string address, string author) =>
        Hashes.Hkdf(registryKey, Protocol.DeclarationSalt, Protocol.OuterInfo(address, author));

    /// <summary>Who took a sale: the first 16 bytes of HMAC-SHA256(taker key, author), hex.</summary>
    public static string TakerTag(byte[] takerKey, string saleAuthor) =>
        Text.Hex(Hashes.HmacSha256(takerKey, Text.FromHex(saleAuthor)).AsSpan(0, Protocol.TakerTagBytes));

    /// <summary>
    /// Everything the sale of <paramref name="reference"/> needs, derived once: its keys, its taker
    /// tag and the conversation key (ECDH) of its declarations' inside with the merchant's reading key.
    /// </summary>
    public static Sale SaleOf(byte[] integrationKey, Registry registry, string reference)
    {
        var (key, secret, author) = SaleKeys(integrationKey, reference);
        return new Sale(reference, key, secret, author, TakerTag(TakerKey(integrationKey), author), registry,
            Nip44.ConversationKey(secret, registry.Reader));
    }
}

/// <summary>The merchant's registry, as the integration's channel gives it.</summary>
[DebuggerDisplay("Registry {{ reader = {Reader} }}")]
internal sealed class Registry(byte[] key, string reader)
{
    /// <summary>The registry key (32 bytes): a secret.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public byte[] Key { get; } = key;

    /// <summary>Hex: the public half of the merchant's reading key.</summary>
    public string Reader { get; } = reader;

    /// <summary>The registry of a channel's hex fields.</summary>
    public static Registry Of(string registryKey, string registryReader) => new(Text.FromHex(registryKey), registryReader);

    public override string ToString() => $"Registry {{ reader = {Reader} }}";
}

/// <summary>The sale of an order, derived once: secrets but its reference, author and taker tag.</summary>
[DebuggerDisplay("Sale {{ ref = {Ref}, author = {Author} }}")]
internal sealed class Sale(string reference, byte[] key, byte[] secretKey, string author, string taker, Registry registry, byte[] innerKey)
{
    public string Ref { get; } = reference;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public byte[] Key { get; } = key;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public byte[] SecretKey { get; } = secretKey;

    public string Author { get; } = author;

    public string Taker { get; } = taker;

    public Registry Registry { get; } = registry;

    /// <summary>The ECDH conversation key of the sale's secret and the registry reader: a secret.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public byte[] InnerKey { get; } = innerKey;

    public override string ToString() => $"Sale {{ ref = {Ref}, author = {Author} }}";
}

/// <summary>The keys of an invoice's rest records and acceptance records: secrets but the author.</summary>
[DebuggerDisplay("RestKeys {{ author = {Author} }}")]
internal sealed class RestKeys(byte[] key, byte[]? secretKey, string author, byte[] encryptionKey)
{
    /// <summary>The rest key: its checkout links' records carry it.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public byte[] Key { get; } = key;

    /// <summary>The rest secret: it signs the records; only the integration key derives it (null for a reader).</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public byte[]? SecretKey { get; } = secretKey;

    public string Author { get; } = author;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public byte[] EncryptionKey { get; } = encryptionKey;

    /// <summary>What a checkout link's page holds of them: the rest key and author, never the secret.</summary>
    public static RestKeys Reader(byte[] key, string author) =>
        new(key, null, author, Hashes.Hkdf(key, Protocol.CheckoutSalt, Protocol.RestEncryptionInfo));

    public override string ToString() => $"RestKeys {{ author = {Author} }}";
}
