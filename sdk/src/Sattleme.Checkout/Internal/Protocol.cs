using System.Collections.Frozen;
using System.Numerics;

namespace Sattleme.Checkout.Internal;

/// <summary>
/// Every constant of the protocol, in one place (SPEC.md §28): every implementation, in any
/// language, uses these values. Those marked "never changes" are fixed by data already published
/// (keys, channels, declarations): changing one loses merchants' sales or stops their integrations.
/// </summary>
internal static class Protocol
{
    /// <summary>The beginning of every integration key. Never changes: a later format takes another.</summary>
    public const string IntegrationKeyPrefix = "sattleme_ck_";

    /// <summary>Bytes of the team's public key and of the integration's key.</summary>
    public const int KeyBytes = 32;

    // HKDF-SHA256 salts (UTF-8). Never change.
    public const string IntegrationSalt = "sattle.me/integration";
    public const string SaleSalt = "sattle.me/sale";
    public const string DeclarationSalt = "sattle.me/registry-declaration";

    // HKDF-SHA256 `info` strings (UTF-8). Never change.
    public const string ChannelAddressInfo = "channel/address/v1";
    public const string ChannelEncryptionInfo = "channel/encryption-key/v1";
    public const string TakerInfo = "taker/v1";
    public const string SaleNostrSecretInfo = "nostr-secret-key/v1";

    public static string SaleInfo(string reference) => $"sale/{reference}/v1";

    public static string OuterInfo(string address, string author) => $"outer/v1/{address}/{author}";

    /// <summary>Bytes of HMAC-SHA256(taker key, author) kept as the taker tag.</summary>
    public const int TakerTagBytes = 16;

    /// <summary>NIP-78 application data: every record of the app.</summary>
    public const int RecordKind = 30078;

    /// <summary>NIP-42 authentication.</summary>
    public const int AuthKind = 22242;

    /// <summary>A record's plaintext is padded with spaces to a multiple of this many bytes.</summary>
    public const int RecordBlockBytes = 256;

    public const string ChannelRecordType = "team-channel";
    public const int ChannelVersion = 1;

    /// <summary>At most this many moves of the merchant's relays are followed.</summary>
    public const int MaxRelayMoves = 3;

    /// <summary>Longest business name, in Unicode code points.</summary>
    public const int MaxBusinessNameLength = 200;

    public const string DeclarationRecordType = "registry-declaration";
    public const int DeclarationVersion = 1;

    /// <summary>The currency of a sale declared in sats: its `minor` is the sats, its `digits` 0.</summary>
    public const string SatsCurrency = "SAT";

    /// <summary>Most decimals a declared price may have.</summary>
    public const int MaxDeclaredDigits = 8;

    /// <summary>Largest amount in minor units (2^53 − 1: a JSON number every reader holds exactly).</summary>
    public const long MaxMinorUnits = 9_007_199_254_740_991;

    /// <summary>The largest safe integer of a JSON number (2^53 − 1).</summary>
    public const long MaxSafeInteger = 9_007_199_254_740_991;

    // Checkout links (SPEC.md §30). The salt and `info` strings never change.
    public const string CheckoutSalt = "sattle.me/checkout";
    public const string CheckoutAddressInfo = "address/v1";
    public const string CheckoutEncryptionInfo = "encryption-key/v1";
    public const string CheckoutRecordType = "checkout";
    public const int CheckoutVersion = 1;

    /// <summary>The first part of a checkout link's fragment: a link of this version. Never changes.</summary>
    public const string CheckoutMarker = "c";

    /// <summary>The page of the merchant's web app that reads checkout links.</summary>
    public const string CheckoutPath = "/pay";

    /// <summary>A checkout link's record lives this long after its invoice's end (NIP-40): a week.</summary>
    public const long CheckoutLifeSeconds = 604_800;

    /// <summary>Longest redirect URL, in characters (ASCII only).</summary>
    public const int MaxRedirectUrlLength = 2048;

    /// <summary>Longest scheme of an app's redirect URL.</summary>
    public const int MaxRedirectSchemeLength = 32;

    /// <summary>
    /// Schemes a redirect URL never has (SPEC.md §30, "Redirect URLs"): the browser's own, files
    /// and scripts; other browsers; other transports; any Android activity; payments; calls and
    /// messages; installing software or running system tools. `https` follows its own rule.
    /// </summary>
    public static readonly FrozenSet<string> RefusedRedirectSchemes = new[]
    {
        "about", "blob", "chrome", "chrome-extension", "content", "data", "file", "filesystem",
        "javascript", "moz-extension", "resource", "safari-extension", "vbscript", "view-source",
        "brave", "com-apple-mobilesafari-tab", "ddgquicklink", "firefox", "firefox-focus",
        "googlechrome", "googlechrome-x-callback", "googlechromes", "microsoft-edge",
        "microsoft-edge-http", "microsoft-edge-https", "onionbrowser", "onionbrowsers", "opera-http",
        "opera-https", "x-safari-http", "x-safari-https",
        "ftp", "http", "ws", "wss",
        "android-app", "intent",
        "bitcoin", "keyauth", "lightning", "liquidnetwork", "lnurl", "lnurlc", "lnurlp", "lnurlw",
        "callto", "facetime", "facetime-audio", "mailto", "sip", "sms", "tel",
        "itms-services", "ms-msdt", "search-ms",
    }.ToFrozenSet(System.StringComparer.Ordinal);

    /// <summary>Most relays a list holds.</summary>
    public const int MaxRelays = 10;

    /// <summary>The longest relay address (`wss://` included), in code points.</summary>
    public const int MaxRelayUrlLength = 120;

    /// <summary>Longest host of a relay address, a checkout origin, a redirect URL or an https URL.</summary>
    public const int MaxHostLength = 253;

    /// <summary>Longest https URL, in characters (ASCII only).</summary>
    public const int MaxHttpsUrlLength = 2048;

    /// <summary>An order's reference: 1 to 64 characters of these.</summary>
    public const int MaxRefLength = 64;

    public const string RefRule = "1 to 64 characters A-Z a-z 0-9 . _ : -";

    // Invoice durations, in seconds.
    public const int MinDurationSeconds = 60;
    public const int MaxDurationSeconds = 3600;
    public const int DefaultDurationSeconds = 900;

    /// <summary>Longest note, in Unicode code points.</summary>
    public const int MaxNoteLength = 100;

    /// <summary>After an invoice's end, a payment of its last seconds may still be reported.</summary>
    public const long GraceSeconds = 120;

    /// <summary>An invoice's date may be this far from the server's clock (synchronized by NTP).</summary>
    public const long ClockToleranceSeconds = 300;

    /// <summary>The LUD-21 refusal that means "never paid" once the grace period is over.</summary>
    public const string NotFoundReason = "Not found";

    /// <summary>A dated price older than this is refused.</summary>
    public const long MaxRateAgeSeconds = 3600;

    /// <summary>Longest price text a rate source may give.</summary>
    public const int MaxPriceLength = 64;

    /// <summary>Largest exponent (either sign) of a price written in exponent notation.</summary>
    public const int MaxPriceExponent = 64;

    /// <summary>Most events read from one relay for one query.</summary>
    public const int MaxEventsPerQuery = 1000;

    /// <summary>Most pages of a paged or complete read.</summary>
    public const int MaxPages = 20;

    /// <summary>The `limit` of each page of a complete read: a page of fewer events is the last.</summary>
    public const int CompleteReadLimit = 100;

    /// <summary>The User-Agent of every HTTP request and WebSocket handshake, in every implementation.</summary>
    public const string UserAgent = "sattleme-Checkout/1";

    /// <summary>Largest message read from a WebSocket.</summary>
    public const int MaxRelayMessageBytes = 1 << 20;

    /// <summary>Largest HTTP answer body read.</summary>
    public const int MaxHttpBodyBytes = 1 << 20;

    /// <summary>What one HTTP answer may read in all, its head (status line, headers, a chunked body's size lines) included: 10 MiB (SPEC.md §15).</summary>
    public const int MaxHttpAnswerBytes = 10 << 20;

    /// <summary>Earlier invoices of an order checked at once (createInvoice).</summary>
    public const int MaxParallelPaymentChecks = 4;

    // Per request timeouts, in milliseconds: the default and the bounds a caller may choose.
    public const int DefaultTimeoutMs = 10_000;
    public const int MinTimeoutMs = 100;
    public const int MaxTimeoutMs = 60_000;

    // waitForPayment's interval, in milliseconds.
    public const int DefaultIntervalMs = 2000;
    public const int MinIntervalMs = 250;
    public const int MaxIntervalMs = 60_000;

    /// <summary>Longest `x` (expiry) field of a BOLT11 invoice, in 5-bit words.</summary>
    public const int MaxBolt11ExpiryWords = 7;

    /// <summary>Largest BOLT11 amount: 21 million bitcoins, in millisatoshis.</summary>
    public const long MaxBolt11AmountMsat = 2_100_000_000_000_000_000;

    // On-chain payments (SPEC.md §31). The types, salts and `info` strings never change.

    /// <summary>Below this amount, an order is Lightning only.</summary>
    public const long OnchainFloorSats = 1000;

    /// <summary>The largest amount of an order: 21 million bitcoins, in sats.</summary>
    public const long MaxSats = 2_100_000_000_000_000;

    public const long SatsPerBtc = 100_000_000;

    /// <summary>The version of a checkout link's record for an invoice with an address.</summary>
    public const int CheckoutOnchainVersion = 2;

    public const string LotRecordType = "integration-onchain-lot";
    public const int LotVersion = 1;
    public const string LotAddressInfo = "onchain-lot/address/v1";

    /// <summary>Most addresses a lot holds.</summary>
    public const int MaxLotAddresses = 100;

    public const string LeaseRecordType = "registry-onchain";
    public const int LeaseVersion = 1;

    /// <summary>What a deposit address's registry address is the HMAC of: this, then the address.</summary>
    public const string LeaseAddressPrefix = "onchain/v1/";

    /// <summary>Leases read back this far: a lease ends at most 4 320 s after its `created_at`.</summary>
    public const long LeaseLookbackSeconds = 7200;

    /// <summary>Free addresses tried for one order.</summary>
    public const int MaxAddressTries = 3;

    public const string WitnessRecordType = "integration-onchain-seen";
    public const int WitnessVersion = 1;
    public const string SeenSecretInfo = "onchain-seen/nostr-secret-key/v1";
    public const string SeenAddressKeyInfo = "onchain-seen/address-key/v1";

    /// <summary>The tag of a witness that names its order, and its address.</summary>
    public const string OrderTag = "o";

    /// <summary>The coins of a transaction a witness keeps: its first 100.</summary>
    public const int MaxWitnessInputs = 100;

    /// <summary>The `sale` a witness names when the transaction came at no lease's time.</summary>
    public const string NoSale = "none";

    /// <summary>
    /// mempool.space (Esplora REST): the explorer, unless the channel names the merchant's Bitcoin
    /// node (Onchain/Node.cs), and the node's fallback when it says <c>"mempool"</c>.
    /// </summary>
    public const string MempoolUrl = "https://mempool.space/api";

    /// <summary>mempool.space's WebSocket, which waitForPayment watches an address on without a node.</summary>
    public const string MempoolWebSocketUrl = "wss://mempool.space/api/v1/ws";

    /// <summary>When a transaction was received, asked only for one no check saw while its lease ran.</summary>
    public const string BlockCypherUrl = "https://api.blockcypher.com/v1/btc/main";

    /// <summary>A node URL (SPEC.md §31, "A node URL"): 200 characters at most.</summary>
    public const int MaxNodeUrlLength = 200;

    /// <summary>
    /// A node that failed a read less than this long ago, in the same call, is asked after
    /// mempool.space (<c>fallback</c> <c>"mempool"</c>): a node that does not answer does not make
    /// each read wait for its timeout.
    /// </summary>
    public const long NodeFailedRecentlyMs = 60_000;

    /// <summary>Confirmations of an on-chain payment, on the explorer read.</summary>
    public const int Confirmations = 3;

    /// <summary>The on-chain part is checked at least this often while waitForPayment waits.</summary>
    public const int OnchainCheckIntervalMs = 60_000;

    /// <summary>The explorer's WebSocket, lost: connected again after this, doubled at each failure…</summary>
    public const int ReconnectFirstMs = 1000;

    /// <summary>…up to this.</summary>
    public const int ReconnectMaxMs = 30_000;

    // The rest of an on-chain underpayment (SPEC.md §31). Never change.
    public const string RestRecordType = "checkout-rest";
    public const int RestVersion = 1;

    public static string RestKeyInfo(string reference, string address, long expiresAt) =>
        $"checkout-rest/{reference}/{address}/{Numbers.Decimal(expiresAt)}/key/v1";

    public static string RestSecretInfo(string reference, string address, long expiresAt) =>
        $"checkout-rest/{reference}/{address}/{Numbers.Decimal(expiresAt)}/nostr-secret-key/v1";

    public const string RestEncryptionInfo = "rest/encryption-key/v1";

    public static string RestAddressInfo(string paymentHash) => $"rest/address/{paymentHash}/v1";

    /// <summary>A rest invoice is asked only with this long left before the invoice's end.</summary>
    public const long MinRestSeconds = 60;

    /// <summary>The merchant's tolerance of on-chain underpayments: 0 to this many basis points.</summary>
    public const int MaxTolerance = 1000;

    /// <summary>Basis points in a whole.</summary>
    public static readonly BigInteger ToleranceScale = 10_000;

    // Acceptance records (SPEC.md §31). Never change.
    public const string AcceptanceRecordType = "checkout-accepted";
    public const int AcceptanceVersion = 1;
    public const string AcceptanceAddressInfo = "rest/accepted/v1";

    // The merchant's acceptance (SPEC.md §31, "The merchant's acceptance"): the merchant accepts by
    // hand, in its app, what an order underpaid on-chain received as paying it. A record of the team
    // (never the integration's: it opens one but cannot make one), sealed with the channel's key, at
    // an address the integration key derives (salt IntegrationSalt) from the order's sale author, the
    // invoice's deposit address and its expiresAt (Unix seconds, decimal). Never change: the
    // merchant's app writes the same records.
    public const string MerchantAcceptanceRecordType = "integration-onchain-accepted";
    public const int MerchantAcceptanceVersion = 1;

    public static string MerchantAcceptanceInfo(string sale, string address, long expiresAt) =>
        $"onchain-accepted/{sale}/{address}/{Numbers.Decimal(expiresAt)}/v1";
}
