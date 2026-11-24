# Sattleme.Checkout

Take Lightning and on-chain payments for a sattle.me merchant from an online shop's
**server**, in .NET: the invoice of each order (a Lightning invoice, a deposit address of the
merchant's wallet, or both), its declaration in the merchant's registry, and the check of its
payment.

- Everything runs on your server, which holds the integration's secret key. The customer's
  browser only ever gets the invoice and its address: on your page, or on the merchant's web app
  through a [checkout link](#checkout-links), if you ask for one.
- Payments go straight to the merchant's wallet. sattle.me runs no server and no API for
  integrations: this package talks to the merchant's Lightning address (breez.tips), to the
  merchant's Nostr relays and, for [on-chain payments](#on-chain-payments), to a block explorer
  (mempool.space, or [the merchant's own Bitcoin node](#the-merchants-bitcoin-node) when it set
  one) and BlockCypher; nothing else (and to the rate source you choose, for prices in a currency).
- Amounts are exact: strings of digits (sats, a currency's minor units) and integers, never
  floating-point.

The protocol is specified, for implementations in other languages, in
[sattleme/checkout-spec](https://github.com/sattleme/checkout-spec) (`SPEC.md`),
with conformance vectors every implementation passes. This version implements its version 1.0.0
(`spec/PIN.json`).

## Requirements

- .NET 8 or later (the package targets `net8.0`; its tests run on .NET 8, 9, 10 and 11), on a
  **server**.
- A clock synchronized by NTP: invoices more than 5 minutes off the server's clock are refused.
- An integration's secret key: the merchant creates the integration in the sattle.me app
  ("Integrations") and gives you its key, `sattleme_ck_…`.

```sh
dotnet add package Sattleme.Checkout
```

## The key is a secret

Keep the integration's key on the server only: in an environment variable or a secrets vault
(ASP.NET Core's user secrets in development, your platform's vault in production), never in a web
page, an app, a code repository, `appsettings.json` or a log. With it, anyone can make invoices
that pay the merchant and mark sales as the integration's. It can never spend the merchant's funds
nor read their wallet.

If it leaks, the merchant removes the integration in the app ("Remove the integration") and
creates another one: the old key then makes no invoice, finds none and reads no merchant
(`RevokedException`). A check of an invoice it made still finds its Lightning payment: an invoice
without an address is checked as before, and one with an address is `RevokedException` unless its
Lightning invoice was paid (SPEC.md §14). The merchant settles by hand the orders still being paid
on-chain.

**One key, one shop.** The merchant creates one integration for each shop (each system with its
own orders' references), and two shops never share a key: the sale of an order is derived from the
key and the order's reference, so two shops with one key would share their references, and an
order of one would be found paid, or pending, with an invoice of the other. The servers of one
shop share its key.

The package never writes the key, nor any key derived from it, in an exception, a log, a
`ToString()`, a debugger view or a serialization: `SattlemeClientOptions` takes the key and the
CoinGecko key but gives them back to no one outside the package (they are set only), so
`System.Text.Json`, Json.NET or a logger that writes an object's properties writes neither.

## Quick start

```csharp
using Sattleme.Checkout;

// One client for the application (thread-safe; register it as a singleton). Nothing to connect
// nor to dispose: each call reads the merchant, opens one connection per relay and closes it when
// it is done.
var sattleme = new SattlemeClient(new SattlemeClientOptions
{
    Key = Environment.GetEnvironmentVariable("SATTLEME_INTEGRATION_KEY")!,
});

// When the customer checks out, or when the payment page asks again:
async Task<string> InvoiceOfAsync(ShopOrder order, CancellationToken ct)
{
    if (order.InvoiceJson is not null)
    {
        return order.InvoiceJson; // one order, one invoice at a time
    }
    try
    {
        var invoice = await sattleme.CreateInvoiceAsync(new Order
        {
            Ref = order.Reference, // "ORD-1042": the order's reference, its sale in the merchant's app
            Sats = 49_000, // or: Fiat = new Price("24.50", "CHF")
            DurationSeconds = 900, // 60 to 3600
            Note = $"Order {order.Number}", // shown to the merchant, 100 characters at most
        }, ct);
        order.InvoiceJson = invoice.ToJson();
    }
    catch (InvoicePendingException pending)
    {
        // An invoice of this order may still be paid (say, after a crash): keep that one. It is
        // the registry's copy: the same fields, without rate, checkoutUrl and redirectUrl.
        order.InvoiceJson = pending.Invoice.ToJson();
    }
    await db.SaveAsync(order, ct);
    return order.InvoiceJson;
}

// The payment page gets the invoice alone: its PaymentUri ("bitcoin:bc1p…?amount=…&lightning=lnbc…")
// and ExpiresAt (1791378900). When it asks for the order's status (every 1 to 2 seconds):
async Task<PaymentState> StatusOfAsync(ShopOrder order, CancellationToken ct)
{
    var status = await sattleme.GetStatusAsync(InvoiceToCheck.FromJson(order.InvoiceJson!), ct);
    switch (status)
    {
        case PaymentStatus.Paid paid:
            // In Lightning: keep the preimage, the proof of payment. On-chain: the transactions.
            // Mixed (on-chain, its rest in Lightning): both. paid.ToJson() holds them all.
            await db.MarkPaidAsync(order, paid.ToJson(), ct);
            break;
        case PaymentStatus.Expired:
            // Never paid: the order may get a new invoice. Received above "0": an on-chain
            // underpayment the merchant received, to settle with the customer.
            order.InvoiceJson = null;
            await db.SaveAsync(order, ct);
            break;
    }
    return status.State; // Open, Unconfirmed, Paid or Expired
}
```

`CreateInvoiceAsync` gives an `Invoice` to store with the order; `invoice.ToJson()` is this JSON
(and `System.Text.Json` writes the same, whatever its options, of every type of the package's data
model, typed as its own class, as `object` or as `IInvoiceToCheck`: a `PaymentStatus.Paid` too):

```json
{
  "ref": "ORD-1042",
  "bolt11": "lnbc490u1p…",
  "paymentHash": "9c41…",
  "verifyUrl": "https://breez.tips/lnurlp/verify/9c41…",
  "onchainAddress": "bc1p…",
  "paymentUri": "bitcoin:bc1p…?amount=0.00049&lightning=lnbc490u1p…",
  "sats": "49000",
  "fiat": null,
  "rate": null,
  "note": "Order 1042",
  "createdAt": 1791378000,
  "expiresAt": 1791378900,
  "checkoutUrl": null,
  "redirectUrl": null
}
```

`bolt11` (the BOLT11 invoice) is null for an order paid on-chain only; `onchainAddress` (the
deposit address it leased) is null for Lightning only; `paymentUri` is one QR code for both;
`fiat` is `{"amount": "24.50", "currency": "CHF", "minor": "2450", "digits": 2}` for a price in a
currency, and `rate` `{"source": "coingecko", "currency": "CHF", "price": "69012", "fetchedAt", "time"}`;
dates are Unix seconds; `checkoutUrl` is a [checkout link](#checkout-links). Every amount is a
string of digits, every date an integer, an absent value `null`: the JSON of every implementation
of the specification, so that an implementation in another language reads it.
`Invoice.FromJson`, `DeclaredInvoice.FromJson` and `InvoiceToCheck.FromJson` (either of them,
for `GetStatusAsync`) read it back; anything else is `InvalidOrderException` (`Param` `invoice`).

**Stored invoices are your shop's.** Check only the invoices your shop stored itself with their
orders, where only it writes (its database), never one that comes from the customer (a form field,
a URL, a cookie): `GetStatusAsync` checks that a stored invoice is well formed (SPEC.md §22), never
that this integration made it. An invoice someone else wrote, with a Lightning invoice of their own
that they paid, reads as `Paid` (SPEC.md §27).

Instead of polling from your page's requests, a background job may wait for the payment:

```csharp
var final = await sattleme.WaitForPaymentAsync(InvoiceToCheck.FromJson(order.InvoiceJson!), new WaitOptions
{
    IntervalMs = 2000,
    OnStatus = status => logger.LogInformation("{State}", status.State),
}, stoppingToken); // a CancellationToken: stop waiting
// final: PaymentStatus.Paid (Method Lightning with its Preimage, Onchain with Received and Txids,
// Mixed with Received, Txids and Preimages) or PaymentStatus.Expired (Received)
```

A network failure only delays the next check. One that each check meets again (the explorer or the
Lightning address that does not answer…) is reported to your `Logger` once, and again only after
it stopped.

### One order, one invoice at a time

- An order keeps its invoice until it has **expired**: `GetStatusAsync` says `Expired` 120 seconds
  after the invoice's end (a payment of its last seconds may be reported that late). Only then may
  the order get a new invoice. `CreateInvoiceAsync` checks it in the merchant's registry and
  throws `InvoicePendingException` (with that invoice) while an earlier invoice of the same `Ref`
  may still be paid (or its on-chain payment is being confirmed), or `OrderPaidException` (with its
  `Status`, and the `Preimage` of a Lightning payment) when one was paid.
- Only the order that holds an invoice is marked paid with it, in the database transaction that
  marks it paid. Keep the `Preimage` (the proof of a Lightning payment) or the `Txids` (of an
  on-chain one) and the `Rate` the amount was converted at, to answer a customer or an audit.
- Invoices of the same `Ref` are one sale in the merchant's app. One order, one `Ref`: never
  reused for another order by anything that holds the key (a test copy of the shop, a restored
  database included), and never taken from what the customer sends (make it on your server).
- After a failure, `FindInvoicesAsync(reference)` lists the invoices declared for an order, from
  the merchant's registry.
- "One at a time" holds within one `SattlemeClient` and against the registry: when several
  processes may make the invoice of the same order at once, lock the order (in your database)
  around `CreateInvoiceAsync`. In a store whose reads may lag behind its writes (an eventually
  consistent one, as some hosted platforms give), read the lock and what it protects with reads
  that see the latest writes (consistent reads).

### Showing the invoice

This package is for servers only. The shop's page shows the invoice with its own code:

- a QR code of `PaymentUri`, with any QR library: `bitcoin:<address>?amount=…&lightning=<bolt11>`
  with an address (BIP-21, the "unified QR" wallets read: they pay in Lightning or on-chain),
  `lightning:<bolt11>` without one;
- an "Open in wallet" link to the same URI;
- the time left until `ExpiresAt`, then the status your server gives (`Open`, `Unconfirmed`,
  `Paid`, `Expired`). With an on-chain underpayment, `Open` carries what was `Received` and its
  `Rest`: show its `PaymentUri` as the QR code (the rest in Lightning, and on-chain to the same
  address from 1 000 sats), until `ExpiresAt`.

The page talks to your server only: never give it the key, nor ask it to reach breez.tips or the
relays. Or send the customer to a checkout link instead (below).

## Checkout links

Instead of showing the invoice on your page, you may send the customer to a page of the merchant's
sattle.me web app made for this invoice: its QR code, a button that opens the customer's
wallet, the time left, then "Paid" or "Expired". Once the invoice is paid, the page sends the
customer back to your `RedirectUrl`, 7 seconds later or at once with a tap.

```csharp
var invoice = await sattleme.CreateInvoiceAsync(new Order
{
    Ref = "ORD-1042",
    Fiat = new Price("24.50", "CHF"),
    Note = "Order 1042", // shown to the customer too
    Checkout = true,
    RedirectUrl = "https://shop.example/orders/1042/thanks", // or "myshop://orders/1042"
}, ct);
// Store it with the order, then redirect the customer's browser to invoice.CheckoutUrl:
// "https://…/pay#c.…" (nothing after "#" is ever sent to the web app's host).
```

- **Optional, per order.** Without `Checkout = true`, nothing changes. `RedirectUrl` alone is
  refused (`InvalidOrderException`, `Param` `redirectUrl`); without one, the page shows the
  receipt and sends the customer nowhere.
- **Not a proof of payment.** Anyone can open your redirect URL. When the customer arrives there,
  check the order with `GetStatusAsync(invoice)` (or `WaitForPaymentAsync`) on your server, and
  mark it paid only then, as without checkout.
- **What the page gets**: a record the package publishes on a majority of the merchant's relays,
  before the invoice is declared: the invoice, its verify URL, its deposit address (with its amount
  and dates), the business's name, the price, the note and your redirect URL. Never the key, the
  merchant's Lightning address nor its registry. It is signed by a key of that link alone,
  forgotten once it is published: nobody, you included, can change a link afterwards. Relays that
  honor NIP-40 drop it a week after the invoice's end.
- **The rest of an underpayment**: with an address, the record also carries the key and the public
  key with which the page reads the invoices of the rest your server asks for (On-chain payments,
  below): the page shows the rest's invoice as soon as a check of your server published it, so
  keep checking the payment while the customer pays (`WaitForPaymentAsync`, or `GetStatusAsync`
  from your page's polling). Only the integration key signs them.
- **The merchant's Bitcoin node**: with an address, and when the merchant set its node with
  `payers` true, the record also carries the node's URL and fallback: the page reads the address
  on that node rather than on mempool.space ([The merchant's Bitcoin
  node](#the-merchants-bitcoin-node)). Never with `payers` false, nor for an order without an
  address.
- **Who sees the customer**: the customer's browser loads the web app, reads the record from the
  merchant's relays and checks the payment with the merchant's Lightning address (breez.tips), and
  an address on mempool.space (or on the merchant's node, with `payers` true): they see its IP
  address, which they never do when your page shows the invoice.
- **Redirect URLs**: `https://` with a lowercase host name (no IP address, no `localhost` nor a
  name under it, no `user:password@`), or an app's link (`myshop://…`), 2048 characters at most,
  every character of RFC 3986 (anything else percent-encoded, host names in punycode). Refused:
  `http:`, browser and file schemes, other browsers' schemes (`x-safari-https:`, `googlechrome:`,
  `firefox:`…, which would open a web page whose host the page cannot show: a web page is an
  `https` URL), `intent:`, payment schemes (`lightning:`, `bitcoin:`…), calls and messages. The
  page shows "Redirecting to shop.example" (or "myshop://") on its button. Prefer universal links
  (iOS) and App Links (Android), which are `https` URLs: browsers do not open an app on their own
  after a delay (Chrome on Android refuses it), so for an app's link the customer taps the button.
- **One order, one invoice at a time** holds with checkout links too. When an invoice of the
  order may still be paid, `InvoicePendingException` carries, with `Checkout = true`, a new link
  to it: `pending.CheckoutUrl`. Send the customer there.
- **Availability**: the merchant's app says in the integration's channel where its web app is.
  An app without a web app gives none: `CreateInvoiceAsync` with `Checkout = true` throws
  `CheckoutUnavailableException`; show the invoice on your page instead.

## On-chain payments

From 1 000 sats, an order may also be paid on-chain, to a deposit address of the merchant's
wallet. Nothing to ask for: `CreateInvoiceAsync` leases an address when the amount allows it, and
the Invoice's `PaymentUri` shows both methods in one QR code.

| Amount                                          | Methods                                                          |
| ----------------------------------------------- | ---------------------------------------------------------------- |
| below 1 000 sats                                | Lightning (outside its bounds: `InvalidOrderException`)          |
| 1 000 sats or more, within the Lightning bounds | Lightning and on-chain (Lightning only without an address)       |
| 1 000 sats or more, outside them                | on-chain only (`OnchainUnavailableException` without an address) |

- **The lot**: your server has no wallet and makes no address. The merchant's app gives the
  integration a lot of up to 100 taproot addresses (`bc1p…`) of its wallet, and fills it again
  whenever the merchant opens the app. `(await sattleme.GetMerchantAsync()).OnchainAddresses` says
  how many it holds (0: no on-chain payment yet).
- **One address, one order**: an order leases an address in the merchant's registry until its
  invoice's end plus 120 seconds; two servers never keep the same one. The lease is published, read
  again on every relay of the merchant (it is confirmed when no other order holds the address),
  then marked confirmed: only then is the address the order's. An address the explorer shows paid
  already, or that a witness (below) names, is skipped.
- **Unconfirmed, then paid**: `GetStatusAsync` says `Unconfirmed` once enough was sent in time,
  then `Paid` (`Method` `Onchain`) at 3 confirmations on the explorer read (mempool.space, or the
  merchant's node): about 30 minutes. Ship the order only once it is `Paid`. `WaitForPaymentAsync`
  watches the address on the explorer's WebSocket meanwhile; a server that cannot wait that long
  checks again later with `GetStatusAsync` (`CreateInvoiceAsync` for the same `Ref` throws
  `InvoicePendingException` while it is being confirmed).
- **In time**: a transaction counts for the order when it came while the lease ran. The package
  records each transaction it sees in the merchant's registry (a witness), so that the merchant's
  app tells the same order paid: check the payment while the customer pays (your page's polling,
  or `WaitForPaymentAsync`), until the invoice's end and its 120 seconds. A transaction no check
  saw by then is told by BlockCypher's time; one received after the end is late: it reaches the
  merchant, but pays no order. A replacement (RBF, a fee bump) of a payment in time counts for it.
  A transaction counts for one invoice at most: one that a witness of another invoice names counts
  for nothing here.
- **Underpaid**: `Received` is what came in time. Below the amount (and unless the merchant's
  tolerance or acceptance, below, accepted it), the invoice stays `Open` until its end, then
  `Expired` with `Received` above "0": the merchant received it, settle it with the customer. An
  overpayment is `Paid`. `Expired` is final, with one exception, the merchant's acceptance (below):
  when a relay that holds it fails at a check after the lease's end, that check may say `Expired`,
  and a later one, which the relay answers, `Unconfirmed`, then `Paid`. If you cancelled the order
  meanwhile, settle it by hand.
- **The merchant's tolerance**: the merchant may accept, in its app, an on-chain payment a little
  short of the amount (up to 10 %) as paying the order in full. An order keeps the tolerance of its
  creation, whatever the merchant sets later. A payment short by no more than it is
  `Unconfirmed`, then `Paid`, with `Received` below the amount: ship the order as for any `Paid`
  status. One short by more is underpaid: its whole rest is asked. The tolerance is the
  merchant's: the package never gives it to your server, nor to your customer; the page of a
  checkout link learns only that this payment counts (an acceptance record), and sends the
  customer back to your shop.
- **The merchant's acceptance**: the merchant may also accept, by hand in its app, what an order
  underpaid on-chain received as paying it in full: one short by more than its tolerance, which
  the merchant would rather settle at what came. Nothing to configure, nothing to call: every
  check (`GetStatusAsync`, `WaitForPaymentAsync`, and `CreateInvoiceAsync`'s check of an order's
  earlier invoices) reads it on every relay of the merchant with the order's witnesses, and counts
  it only when a majority of those relays hold it. The order is then `Unconfirmed`, then `Paid`
  (`Method` `Onchain`, or `Mixed` with invoices of the rest), with `Received` below the amount, as
  with the tolerance: ship it as for any `Paid` status. `WaitForPaymentAsync` sees it at its next
  check of the on-chain part (within 60 s), the `Rest` still given until then. Only the merchant's
  team can make one: neither your server's key nor a customer can. The page of a checkout link
  learns of it as of the tolerance, through an acceptance record.
- **The rest, in Lightning**: the check that sees an underpayment (`GetStatusAsync`,
  `WaitForPaymentAsync`) asks the merchant's Lightning address for an invoice of the rest, until
  the invoice's end, declares it in the merchant's registry and publishes it for the order's
  checkout links, whose page shows it at once. `PaymentStatus.Open` carries it in `Rest`
  (`open.ToJson()`):

  ```json
  {
    "state": "open",
    "expiresAt": 1791378900,
    "received": "20000",
    "rest": {
      "sats": "29000",
      "bolt11": "lnbc290u1p…",
      "paymentUri": "bitcoin:bc1p…?amount=0.00029&lightning=lnbc290u1p…"
    }
  }
  ```

  `received` is what came in time, `rest.sats` all that is left (whatever the tolerance or the
  merchant's acceptance), `rest.bolt11` its invoice (null while
  there is none: asked at the next check), `rest.paymentUri` one QR code. From 1 000 sats the rest
  is payable on-chain to the same address too; below, in Lightning only (`paymentUri` is
  `lightning:…`, or null while its invoice is asked). No invoice of the rest is asked with less
  than a minute left, nor outside the Lightning address's bounds; a failure is logged as a warning,
  never thrown: the next check asks again. Once the rest is paid, the order is `Unconfirmed` until
  its on-chain part is 3 confirmations deep, then `Paid` with `Method` `Mixed`: keep its `Txids`
  and the `Preimages` of the invoices of the rest.

- **Who sees what**: the explorer (mempool.space, or the merchant's node, and mempool.space too
  when the node fails and its fallback is `"mempool"`) sees your server's IP address and the
  addresses it asks about or watches, and their transactions; BlockCypher, a transaction no check
  saw in time. Never a key. A payment counts as made on what the explorer read alone shows.

### The merchant's Bitcoin node

The merchant may have your server read the blockchain from a node of its own that serves
mempool.space's API (an Umbrel, a server of its own) rather than from mempool.space. It sets it in
its app, which writes it in the integration's channel; nothing to configure on your side, and the
package never gives it to your server:

```json
{ "url": "http://192.168.1.10:3006", "fallback": "mempool", "payers": false }
```

- **Where it is read**: `<url>/api` (`http://192.168.1.10:3006/api`), at mempool.space's paths:
  an address's transactions when an address is leased (one the node shows paid is skipped), and
  the address's transactions with the chain's height at every check, both of one explorer; and
  its WebSocket, `ws://` for `http://` and `wss://` for `https://` (`ws://192.168.1.10:3006/api/v1/ws`),
  while `WaitForPaymentAsync` waits. BlockCypher is asked as without a node, and the rate source
  `mempool` stays mempool.space's.
- **Its URL**: `https`, or `http` only for a host of a local network (10.0.0.0/8, 172.16.0.0/12,
  192.168.0.0/16, 100.64.0.0/10, which Tailscale uses, or a name under `.local`); an IPv4 address
  or a name, never `localhost`, 127.0.0.0/8 nor 0.0.0.0/8; no user name, query nor fragment; the
  node's URL, not its API's (no trailing `/`, no `/api`). Anything else, or a `fallback` or
  `payers` of another spelling, names no node: the package reads mempool.space, and the channel
  stays valid. Only the node's URL has this rule: every other URL the package asks is still
  `https` to a name, never an IP address.
- **`fallback`**, when the node does not answer a read (no answer, a status that is not 2xx, a
  redirect, an answer out of format): `"mempool"`, the same read of mempool.space (both failing:
  `NetworkException`), a node that failed less than 60 s ago in the same call being asked after
  mempool.space; its WebSocket goes to mempool.space's when the node's fails or is lost before any
  message. `"none"`: never mempool.space. While the node does not answer, no address is leased
  (`NetworkException` for an order paid on-chain only, a Lightning-only invoice otherwise) and
  every check of an invoice with an address throws `NetworkException`: never `Expired` nor final
  underpaid for want of an answer, however long the node is down; the order is `Paid` once the
  node answers again. `WaitForPaymentAsync` waits on (it logs the failure once), on the node's
  WebSocket alone.
- **A node on the merchant's network** (`http://192.168.1.10:3006`, `http://umbrel.local`) is
  reachable only by a server on that network: a shop the merchant hosts, or one that joined it (by
  Tailscale, for one). Any other server fails every read of it, and its `fallback` applies: with
  `"none"`, no address is leased and no on-chain payment is judged.
- **`payers`**: `true` (only for a node of the Internet: `https` and a name not under `.local`)
  has the pages of the order's [checkout links](#checkout-links) read the chain from the node
  too: the package writes its `url` and `fallback` (never `payers`) in each link's record of an
  order with an address. Whoever holds a `CheckoutUrl` then reads the node's URL, and the node sees
  the customer's IP address and the address the page follows. With `false`, only your server
  reads the node.
- **Through your `HttpHandler` and `WebSocketFactory`**: the node's requests go through them as
  every other request, plain `http://` and `ws://` ones included (every other rule holds: no
  redirect, the timeouts, the bounds of an answer, the User-Agent). A handler or a factory that
  refuses plain `http`, or the addresses of private networks (as [below](#outbound-requests)
  suggests for a server that holds the keys of several merchants), leaves such a node unreachable:
  its `fallback` then applies.

## Prices in a currency

Give `Fiat` instead of `Sats`: the package converts the price to sats on your server, at the rate
of a price source, and declares the price to the merchant's app, which shows it. Nothing to set
up: without `RateSources`, it asks the default list of sources (below).

```csharp
var invoice = await sattleme.CreateInvoiceAsync(new Order
{
    Ref = "ORD-1043",
    Fiat = new Price("24.50", "CHF"), // a string: never a double nor a decimal
}, ct);
// 24.50 CHF at 69 012 CHF/BTC: ⌈2450 × 10^8 / 6 901 200⌉ = 35 502 sats
```

- `Currency` is an ISO 4217 code in upper case, of the ISO 4217 currencies (List One, without
  funds, metals and test codes). `Amount` is a string of digits with at most the currency's
  decimals (2 for CHF, 0 for JPY, 3 for KWD): those the merchant's app uses, from the package's own
  table, never from the platform's culture data. `Currencies.DigitsOf("CHF")` gives them: SPEC.md
  §4's `currencyDigits`, the decimals of the currency whose code is exactly the text given, null
  for any other text (`"chf"`, `" CHF"`, a fund, a metal, a test code). It needs no client and reads
  nothing: ask it whether your shop may offer its customers to pay in its currency (not null), and
  in how many decimals its prices go to `CreateInvoiceAsync`.
- The conversion is exact, on integers (`BigInteger`): the price of one bitcoin is read as the
  decimal its source wrote, and the sats are **rounded up** to the next whole sat, so that the
  merchant never receives less than the price.
- The sats must fit the bounds of the merchant's Lightning address (`GetMerchantAsync`), as for an
  amount in sats.

### Rate sources

`RateSources` is optional. Without it, the package asks these sources **one after the other**,
the next one only when the previous one does not answer, gives no usable price, or has no price in
the currency (`RateSource.Defaults`):

`coingecko`, then `mempool`, `coinbase`, `kraken` and `opennode`.

The dated sources come first (a dated price is checked for its age), then the undated ones. The
first usable price is the rate, and `invoice.Rate.Source` names the source that gave it. Each
source that failed before it is reported to your `Logger` as a warning.

To choose, give `RateSources` to the client or to an order (an order's wins):

```csharp
new SattlemeClientOptions { Key = key, RateSources = [RateSource.Kraken] }; // this source only
new SattlemeClientOptions { Key = key, RateSources = ["kraken", "coingecko"] }; // asked in this order
new Order { Ref = reference, Fiat = price, RateSources = [RateSource.Mempool, RateSource.Coinbase] }; // this order only
```

A list holds sources (names, which convert from strings, or your own), at least one, no name
twice, every name known; anything else throws `InvalidOptionException` (`InvalidOrderException`
for an order). Each source asked may take up to `HttpTimeoutMs`: a list of 5 sources that all
fail to answer may take 5 times as long.

| Source       | Currencies                  | Dated | Notes                                                                                                                                    |
| ------------ | --------------------------- | ----- | ---------------------------------------------------------------------------------------------------------------------------------------- |
| `coingecko`  | many                        | yes   | No key needed. Optional: a Demo key (`CoinGeckoApiKey`), sent to CoinGecko only. Without one, limited by IP.                             |
| `mempool`    | USD EUR GBP CAD CHF AUD JPY | yes   |                                                                                                                                          |
| `coinbase`   | many                        | no    | Prices are decimal strings, read as written.                                                                                             |
| `kraken`     | Kraken's pairs              | no    | The last trade's price.                                                                                                                  |
| `opennode`   | ~140                        | no    |                                                                                                                                          |
| your own     | yours                       | maybe | `RateSource.Custom((currency, ct) => …)` or an `IRateSource`, giving a `RateQuote` (a string, or `new RateQuote(price, time)`). `rate.source` is then `custom`. |

Any other source (a CoinGecko Pro key, your own mempool instance, several sources checked against
each other) is your own. It gives the price as a **string**, as its source wrote it: a `double`
may already have lost digits.

```csharp
var rates = new HttpClient();
var sattleme = new SattlemeClient(new SattlemeClientOptions
{
    Key = Environment.GetEnvironmentVariable("SATTLEME_INTEGRATION_KEY")!,
    RateSources = [RateSource.Custom(async (currency, ct) =>
    {
        // Your service answers { "price": "69012.50", "time": 1791378000 }: the price as a string.
        using var response = await rates.GetAsync($"https://rates.example.ch/btc/{currency}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new UnsupportedCurrencyException(currency, "rates.example.ch");
        }
        response.EnsureSuccessStatusCode();
        using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return new RateQuote(answer.RootElement.GetProperty("price").GetString()!, answer.RootElement.GetProperty("time").GetInt64());
    })],
});
```

A price its source dates is refused when it is more than an hour old, or more than 5 minutes
ahead: the next source of the list is then asked. When no source gives a price,
`CreateInvoiceAsync` throws `UnsupportedCurrencyException` if none of them has the currency, else
the last `RateException` (no usable price) or `NetworkException` (no answer), both retryable. Your
source throws `UnsupportedCurrencyException` when it has no price in a currency,
`NetworkException` or `RateException` when it cannot give one now: the next source is then asked.
Any other exception of yours becomes a `RateException` (yours as its `InnerException`); an
exception of this package of another kind stops the list and goes through as it is; a cancelled
call fails with its cancellation.

## Errors

Every error is a `SattlemeException` with a stable `Code` and `Retryable`, but for a cancelled
call, which throws `OperationCanceledException` (with the call's `CancellationToken`). None ever
holds a secret: neither the key, nor a sale's key, nor any key derived from them.

| Exception                      | Code                   | When                                                                                                                                                         | Retry?           |
| ------------------------------ | ---------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------ | ---------------- |
| `InvalidKeyException`          | `invalid_key`          | The key is not an integration key (another prefix, damaged, an old `https://…/pay#…` link).                                                                  | no               |
| `InvalidOptionException`       | `invalid_option`       | An option of the client or of `WaitForPaymentAsync` is invalid: `Param` names it.                                                                            | no               |
| `InvalidOrderException`        | `invalid_order`        | A parameter is invalid: `Param` names it (`order`, `ref`, `sats`, `fiat`, `rateSource`, `durationSeconds`, `note`, `redirectUrl`, `invoice`).    | no               |
| `RevokedException`             | `revoked`              | The merchant removed the integration.                                                                                                                        | no               |
| `ChannelNotFoundException`     | `channel_not_found`    | The relays answered without the integration's channel (`NewerFormat`: update the package).                                                                   | when not newer   |
| `NetworkException`             | `network`              | A relay, breez.tips, a rate source or an explorer did not answer, or fewer than a majority of the merchant's relays answered or took a record.               | yes              |
| `InvalidInvoiceException`      | `invalid_invoice`      | An invoice or a payment answer failed the checks. Never show such an invoice.                                                                                | no               |
| `InvoicePendingException`      | `invoice_pending`      | An earlier invoice of this order may still be paid, or is being confirmed on-chain: `Invoice` (and `CheckoutUrl` with `Checkout = true`).                    | after it expires |
| `OrderPaidException`           | `order_paid`           | An earlier invoice of this order was paid: `Invoice`, `Status`, `Preimage` (null on-chain).                                                                  | no               |
| `UnsupportedCurrencyException` | `unsupported_currency` | The rate source has no price in this currency (`Currency`).                                                                                                  | no               |
| `RateException`                | `rate`                 | No usable price: invalid, too old, or your rate source failed.                                                                                               | yes              |
| `CheckoutUnavailableException` | `checkout_unavailable` | `Checkout = true`, and the merchant's app gives no web app for checkout links.                                                                               | no               |
| `OnchainUnavailableException`  | `onchain_unavailable`  | The order can only be paid on-chain, and no address of the integration's lot could be leased.                                                                | yes              |

## What `CreateInvoiceAsync` does

1. Reads the integration's channel on the Nostr relays of the key (signed by the merchant's team,
   following up to 3 moves of the merchant's relays): the merchant's Lightning address, registry
   and relays, or `removed`. At every call: a removal stops the next invoice.
2. Checks that no earlier invoice of the order may still be paid (its payment checked: one already
   paid is never handed out again, `OrderPaidException`; one underpaid on-chain gets the invoice of
   its rest, as with `GetStatusAsync`, before `InvoicePendingException`). Meanwhile, reads the
   Lightning address and, for a price in a currency, the rate.
3. Converts the price to sats, when it is in a currency; its methods follow from the amount.
4. With Lightning, asks the merchant's Lightning address for the invoice (LUD-06/16), and checks
   it: Bitcoin mainnet, exactly the amount, the merchant's description hash, a verify URL (LUD-21)
   on the same host and port, no longer than asked, dated within 5 minutes.
5. With on-chain, leases an address of the integration's lot: publishes the lease on a majority
   of the merchant's relays, reads the address's leases again on every relay to confirm it, then
   marks it confirmed. A lease that was not confirmed is withdrawn, and the next address is tried.
   Without an address, Lightning only (or `OnchainUnavailableException`).
6. With `Checkout = true`, publishes its checkout link on a majority of the merchant's relays.
   Without a majority, no invoice (nothing is declared).
7. With Lightning, declares it in the merchant's registry, on a majority of the merchant's relays:
   so that its payment counts for this order's sale and for no other. Without a majority, no
   invoice.

A confirmed lease is never withdrawn, whatever fails after it (its mark, the link, the
declaration): it holds its address until its end, and the order's next `CreateInvoiceAsync` may
hand its invoice out (`InvoicePendingException`).

`GetStatusAsync` asks the invoice's verify URL (LUD-21) and accepts a Lightning payment only with
the preimage of its payment hash, in an answer about this very invoice; with an address, it asks
the explorer at the same time and reads the witnesses of the order (see
[On-chain payments](#on-chain-payments)).

A call opens one connection per relay, used for all it reads and writes there, and closes them
when it is done. Reads and writes go on as soon as a majority of the relays answered (each
declaration is on a majority of them, and any two majorities share a relay): a slow or down relay
slows nothing. Three reads decide that a record is nobody else's and wait for every relay instead,
up to `RelayTimeoutMs` after a majority answered: a lease's confirmation, the witnesses read again
after one is published, and the choice among several invoices of an order that may still be paid;
so does the read of the merchant's acceptance, which counts only what a majority of the relays
sent. A relay still reading then has not answered (its pages count as events, never as an answer),
and a relay that failed earlier in the call is not waited for again. The relays a read went on
without ask nothing more: no page, no authentication, no random bytes drawn for them. A write to
the relays past the majority goes on in the background, its one authentication and its one write
again included, for `RelayTimeoutMs` at most, then its connection closes. Nothing else a call
started runs on once it returned: what it asked beside its own flow (the Lightning address's pay
request, the rate) is stopped and waited for when the call fails first, and `WaitForPaymentAsync`
stops and waits for its checks and its watch of the explorer's WebSocket before it returns. A
call's connections close as it returns, or once such a write ended: each sends what it queued, then
its close frame.

HTTP requests follow no redirect, read at most 1 MiB of an answer's body and 10 MiB of all of it
(its status line and headers, 64 KiB at most, a chunked body's size lines, 16 KiB at most each, and
its trailers included), and carry `User-Agent: sattleme-Checkout/1`, the same in every
implementation of the specification. The relays' WebSocket handshake (and the explorer's) follows
no redirect either, carries the same User-Agent (relays behind a bot protection refuse a handshake
without one), offers no compression, and takes an answer whose status line and headers hold 64 KiB
at most. Every wait, a connection's (its DNS lookup included) and an answer's, is bounded by
its timeout and ends at once when the call is cancelled. Of what a relay sends, a read keeps only
what opens with its keys and, for a signed record, whose signature checks out, once per event and
without the members a relay may add to it (of a lease, only its id, author, date, signature and
end; of any other event, its id and its date): a relay cannot make the server's memory grow. A record of a newer format than this version reads is never used
nor written over, and logged as a warning: update the package.

## Outbound requests

Besides fixed hosts (the rate sources, mempool.space, BlockCypher), your server asks hosts that
others choose: the merchant's Lightning address and its provider (breez.tips), the merchant's
relays and [its Bitcoin node](#the-merchants-bitcoin-node). Their URLs are checked first: `https`
or `wss` only, no IP address, no `localhost` nor a name under it, no redirect followed; but the
node's, whose URL has a rule of its own (below). A name may still lead into your own network (an internal name, a public
name resolving to a private address), and the package does not resolve names itself (a name that
resolves again would bypass such a check). If your server holds the keys of several merchants, or
sits in a network with services of its own, restrict what it can reach to public addresses: in
your network (an egress proxy, a firewall), or in the `HttpHandler` (a `SocketsHttpHandler` with
a `ConnectCallback` that refuses private addresses) and the `WebSocketFactory` you give the
client.

**The merchant's Bitcoin node** (SPEC.md §27): its URL is the merchant's choice, and the package
always follows a valid one. Your server sends it GET requests, in clear HTTP too to an address of
a private network, and to an IPv4 address: at a shared host, whose servers hold the keys of
several merchants, one merchant could so have the server ask machines of that host's own network.
What bounds it: GET only, at mempool.space's paths under `/api`, and the WebSocket's two messages;
no redirect followed, an answer bounded and checked, any answer out of format a failure; nothing
sent but deposit addresses; never `localhost`, 127.0.0.0/8 nor 0.0.0.0/8; `http` only for the
local ranges. A server restricted to public addresses (above) leaves such a node unreachable: its
`fallback` then applies. The node is trusted as mempool.space is: what it shows decides whether an
order is paid. Over `http`, whoever is on the network between your server and the node can change
its answers: `http` is allowed for the local ranges only, and is as safe as that network.

## Security

- **secp256k1** is `NBitcoin.Secp256k1` 4.0.4, a C# port of libsecp256k1
  ([its source](https://github.com/MetacoSA/NBitcoin/tree/NBitcoin.Secp256k1/v4.0.4/NBitcoin/Secp256k1)),
  which ports libsecp256k1's constant-time algorithms. A BIP-340 signature multiplies the generator
  by its nonce with `ecmult_gen` (`ECMultGenContext.MultGen`): the scalar blinded, each entry of its
  table taken by a conditional move over the whole table, never by an index or a branch on the
  secret. NIP-44's ECDH multiplies the registry reader's point by the sale's secret with
  `ecmult_const` (`GE.MultConst`): a fixed window of signed digits, each odd multiple taken by
  conditional moves over the whole table, the constant-time point addition, and an inversion by
  exponentiation (no variable-time inversion). Their checks (a signature's verification, `lift_x`)
  handle public data and are variable-time, as libsecp256k1's. Unlike libsecp256k1's C, whose build
  is checked for constant time (its `ctime_tests`, under Valgrind), this is managed code compiled
  by the JIT: nothing guarantees that the machine code keeps these properties, and they were not
  measured here. A server that cannot accept that risk (others on the same machine measuring its
  timing) runs where no one else does.
- MACs (NIP-44) are compared in constant time (`CryptographicOperations.FixedTimeEquals`);
  ChaCha20 (the package's own) has no table nor branch on its key; every random byte (NIP-44 nonces,
  BIP-340 auxiliary data, the keys of a read, subscription ids) comes from .NET's CSPRNG
  (`RandomNumberGenerator`).
- Report a vulnerability privately: [SECURITY.md](SECURITY.md).

## Limits

- One hour at most per invoice; on-chain, about 30 minutes more to be `Paid` (3 confirmations).
- Not measured: the limits of breez.tips, of mempool.space and of the relays for a shop that makes
  many invoices from one IP address. BlockCypher takes 100 requests an hour from an IP address
  without a token: it is asked only about a transaction no check saw while its lease ran.

Not in this version: refunds, webhooks, and showing open (unpaid) invoices in the merchant's app.

## Options

```csharp
new SattlemeClient(new SattlemeClientOptions
{
    Key = key, // required
    RateSources = null, // a list of sources (names or your own); null: RateSource.Defaults
    CoinGeckoApiKey = null, // CoinGecko's Demo key (visible ASCII), sent to CoinGecko only
    HttpHandler = null, // the HTTP of every request; null: a SocketsHttpHandler without redirects
    WebSocketFactory = null, // the relays' and the explorer's WebSockets (the node's too); null: ClientWebSocketFactory
    RelayTimeoutMs = 10_000, // per relay request, connection included: 100 to 60 000
    HttpTimeoutMs = 10_000, // per HTTP request, body included: 100 to 60 000
    Logger = logger, // an ILogger: warnings only, never a secret
});
```

`TimeProvider` and `Random` exist for tests only (a fake clock, reproducible random bytes); leave
them null. Every method takes a `CancellationToken`. An invalid option throws
`InvalidOptionException` at once, from the constructor.

The client's calls run concurrently, each on several tasks: what you give it (the `HttpHandler`,
the `WebSocketFactory` and its sockets, the `Logger`, a rate source of your own, and the
`TimeProvider` and `Random` of tests) is called from several of them at once within one call, and
the relays of a write past its majority may still use the WebSocket factory's sockets, the clock
and the random bytes after the call returned (`RelayTimeoutMs` at most). They must be safe to call
so; .NET's own are. A rate source of your own gets the call's `CancellationToken`, and a call that
ends waits for it: honour it. A handler or a factory of yours must
follow no redirect, ask for no compression (a redirect it follows is refused all the same) and bound
an answer's head (a `SocketsHttpHandler` bounds its headers, 64 KiB by default).

With ASP.NET Core:

```csharp
builder.Services.AddSingleton(services => new SattlemeClient(new SattlemeClientOptions
{
    Key = builder.Configuration["Sattleme:IntegrationKey"]!, // from user secrets or a vault
    Logger = services.GetRequiredService<ILogger<SattlemeClient>>(),
}));
```

## Dependencies

| Package                                     | Version | Why                                                                                                                                                                                             |
| ------------------------------------------- | ------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `NBitcoin.Secp256k1`                        | 4.0.4   | secp256k1: BIP-340 Schnorr signatures (with 32 fresh random bytes of auxiliary data) and their verification, x-only keys, `lift_x`, ECDH for NIP-44. A C# port of libsecp256k1, maintained. |
| `Microsoft.Extensions.Logging.Abstractions` | 8.0.3   | `ILogger` for the warnings. The lowest version the library's `net8.0` target takes: a host brings its own, later one. It brings `Microsoft.Extensions.DependencyInjection.Abstractions` 8.0.2. |

Everything else is .NET's own: HTTP (`SocketsHttpHandler`), WebSockets (`ClientWebSocket`),
SHA-256, HMAC, HKDF and the CSPRNG (`System.Security.Cryptography`), `BigInteger`, strict UTF-8.
ChaCha20 (RFC 8439, for NIP-44 v2) is the package's own, checked against RFC 8439's and NIP-44's
official vectors: .NET offers it only with Poly1305, and not on every platform. Every JSON the
package reads (an HTTP answer, a relay's message, a record, a stored invoice, the explorer's
WebSocket) goes through its own reader, as JavaScript's `JSON.parse` reads it (SPEC.md §2: the last
member of a name counts, a number of any length is a double, a lone surrogate is kept, nothing but
RFC 8259) and no more than 64 arrays and objects nested, which `System.Text.Json` does not (its
`GetString` refuses a lone surrogate). The tests use `xunit.v3` 4.0.3 (Microsoft.Testing.Platform), and `Newtonsoft.Json` 13.0.4 to
check that Json.NET writes nothing of a key.
Every version is pinned in `Directory.Packages.props`, and every restore follows the lockfiles
(`packages.lock.json`).

## In a platform's plugin

A payment plugin of an e-commerce platform (nopCommerce, GrandNode, Smartstore…) calls this
package and never implements the protocol itself.

- **Runtimes**: the package targets `net8.0`: it loads in hosts of .NET 8, 9, 10 and 11.
- **What it brings**: `Sattleme.Checkout.dll` and `NBitcoin.Secp256k1.dll`, a managed assembly
  without dependencies of its own. `Microsoft.Extensions.Logging.Abstractions` (and its own
  dependency, `Microsoft.Extensions.DependencyInjection.Abstractions` 8.0.2) are part of ASP.NET
  Core's shared framework, which such a host loads in its own, later version.
- **Embedded in a plugin's folder**: two plugins carrying two versions of one assembly collide in
  a host that loads its plugins into one `AssemblyLoadContext`. A plugin that ships this package
  in its own archive embeds it under names of its own, its namespaces prefixed (checkout-spec's
  `PORTING.md`, "Packages that depend on an SDK"): the package reads nothing of its own assembly
  name or namespace at run time (no reflection, no embedded resource), so it builds and runs
  renamed as it is.
- **In the host**: one `SattlemeClient` per integration key, a singleton (thread-safe, it holds no
  connection between calls), its `Logger` the host's. Leave `HttpHandler` and `WebSocketFactory`
  null unless the host must route the requests (a proxy, an egress filter): the package's own
  follow no redirect and offer no compression. Store each order's invoice with `invoice.ToJson()`
  in the host's database, read it back with `InvoiceToCheck.FromJson`, and lock the order around
  `CreateInvoiceAsync`.
- **Its currencies**: whether the shop's currency can price an order, and in how many decimals,
  is `Currencies.DigitsOf(code)` (null: it cannot), public as everything a plugin calls.

## Other languages

The package's public API is the namespace `Sattleme.Checkout`: `SattlemeClient`, its options, the
data model, the exceptions, `Currencies` and `Prices`; everything else is internal. Implementations in other languages
follow [`SPEC.md`](https://github.com/sattleme/checkout-spec/blob/main/SPEC.md) and pass its
conformance vectors; the invoices they store are the same JSON.

## Development

```sh
./check.sh # the pinned specification, format, build (analyzers, warnings as errors), tests on .NET 8, 9, 10 and 11, package
```

It needs the .NET SDK of `global.json` (11.0.100 or a later feature band) and the .NET 8, 9, 10 and 11
runtimes. The tests run local Nostr relays and fakes of breez.tips, of the rate sources, of the
explorers (mempool.space and a merchant's Bitcoin node, REST and WebSocket) and of BlockCypher: no
network. One test class or method:

```sh
dotnet build tests/Sattleme.Checkout.Tests -c Release -f net8.0
dotnet test --project tests/Sattleme.Checkout.Tests -c Release -f net8.0 --no-build --filter-class "*RestTests"
```

`spec/` is a copy of [sattleme/checkout-spec](https://github.com/sattleme/checkout-spec)
pinned to one commit (`spec/PIN.json`, checked file by file by
`dotnet run --project tools/Sattleme.Checkout.Spec -- check` and by the tests), never edited here.
Its vectors are what the tests check the package against: the conformance vectors, the merchant's
app's vectors (`spec/vectors/app/app.json`) and the vectors an implementation makes for the app to
read (`spec/vectors/app/implementation-declaration.json`, `spec/vectors/app/checkout-link.json`,
`spec/vectors/app/checkout-rest.json`). To take a new version of the specification: clone
checkout-spec, check out that version's commit, run
`dotnet run --project tools/Sattleme.Checkout.Spec -- import <clone>` (it replaces `spec/` and
writes `PIN.json`), then make the code pass the new vectors, in one pull request.

Packages come from the lockfiles (`dotnet restore --locked-mode` in CI and `./check.sh`); to
change a version, edit `Directory.Packages.props` and run `dotnet restore --force-evaluate`.

## Releasing

Only a maintainer releases. A release is a tag `vX.Y.Z` on a commit of `main`, equal to the
`<Version>` of `src/Sattleme.Checkout/Sattleme.Checkout.csproj`. On that tag,
`.github/workflows/release.yml` runs the CI again, checks the tag against the version and against
`main`, packs `Sattleme.Checkout.X.Y.Z.nupkg` (with its symbols), then pushes it to nuget.org in a
job bound to the protected environment `nuget` (a maintainer approves each release), with NuGet's
trusted publishing: a short-lived key from the workflow's OIDC token, no stored key. Once, on
nuget.org: a trusted publishing policy for this repository, its workflow `release.yml` and its
environment `nuget`, and the environment's variable `NUGET_USER` (the nuget.org account that owns
the package).

## License

[MIT](LICENSE)
