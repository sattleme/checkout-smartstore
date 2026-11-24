# Changelog

All notable changes to `Sattleme.Checkout`. Versions follow
[Semantic Versioning](https://semver.org/); tags are `vX.Y.Z`.

Released versions are pushed to nuget.org by CI, from a job a maintainer approves, with NuGet's
trusted publishing (README.md, "Releasing").

## 1.0.0 - 2026-11-19

First version. Follows version 1.0.0 of the specification
([sattleme/checkout-spec](https://github.com/sattleme/checkout-spec)) and passes its
conformance vectors. For .NET 8 or later (`net8.0`), tested on .NET 8, 9, 10 and 11.

- **The client**, `SattlemeClient`: reads the integration key and the merchant
  (`GetMerchantAsync`), makes the invoice of an order and declares it in the merchant's registry
  (`CreateInvoiceAsync`), checks its payment (`GetStatusAsync`, `WaitForPaymentAsync`), finds the
  invoices of an order again (`FindInvoicesAsync`). Thread-safe, made for a server: nothing is
  kept between calls, and once a call returned nothing it started runs on, but a write's relays
  past the majority (`RelayTimeoutMs` at most); what is injected is called from several tasks at
  once. Every method takes a `CancellationToken`: a cancelled call throws
  `OperationCanceledException`.
- **One order, one invoice at a time**, checked against the registry (`InvoicePendingException`,
  `OrderPaidException`): the payment of an invoice that may still be paid is checked first, even
  when it is the only one, so that an invoice already paid is never handed out again; of several,
  the one a majority of the relays holds is handed out.
- **Orders in sats or in a currency** (the ISO 4217 currencies), converted exactly
  (`BigInteger`) and rounded up to the sat, at the price of a list of rate sources asked in turn:
  CoinGecko, mempool.space, Coinbase, Kraken, OpenNode, or your own (`IRateSource`).
  `Currencies.DigitsOf(code)`, the specification's `currencyDigits`, gives the decimals of a
  currency whose code is exactly `code`, null for any other text: whether a shop may price its
  orders in its currency, without a client.
- **Lightning payments**: an invoice from the merchant's Lightning address (LUD-16, LUD-21 verify
  URL).
- **On-chain payments**: from 1 000 sats, a taproot deposit address of the merchant's wallet leased
  for the order, with a single `bitcoin:` QR code (`PaymentUri`). A lease is published
  unconfirmed, read again on every relay of the merchant, then marked confirmed: only a confirmed
  lease gives the order its address, and no order leases an address another order holds or a
  witness names. Payment checked on one explorer, mempool.space or the merchant's Bitcoin node
  (3 confirmations on the explorer read), with fee bumps (RBF) and the `Unconfirmed` state;
  `WaitForPaymentAsync` watches the address on the explorer's WebSocket.
- **The merchant's Bitcoin node**, read in the integration's channel (`bitcoinNode`): the explorer
  of every read of an address and of the chain's height (`<url>/api`) and of the WebSocket
  (`ws://` or `wss://` `<url>/api/v1/ws`), its URL checked by a rule of its own (`http` only for
  the local ranges, an IPv4 address allowed); with `fallback` `"mempool"`, mempool.space when the
  node does not answer (a node that failed less than 60 s ago in the same call asked after it);
  with `"none"`, never mempool.space: no address leased while the node is down, and
  `NetworkException`, never `Expired`, for a check that cannot read it. With `payers` true, the
  node's URL and fallback are written in the checkout links' records, for their pages.
- **Witnesses** of the transactions each check sees, which name the invoice they are of: a
  transaction a witness of another invoice names counts for nothing.
- **The rest of an on-chain underpayment**, payable in Lightning (and on-chain from 1 000 sats),
  shown on the order's checkout links; an order paid both ways is `Paid` with `Method` `Mixed`.
- **The merchant's tolerance of on-chain underpayments** (0 to 10 %), read in the integration's
  channel and kept in the lease of each order's address; acceptance records tell the checkout
  links' pages that a payment counts, never the tolerance.
- **The merchant's acceptance** of an order underpaid on-chain at what it received, made by the
  merchant in its app: read by every check on every relay of the merchant, and counted only when a
  majority of them hold it; the order is then `Paid` with `Received` below its amount.
- **Checkout links**: `new Order { …, Checkout = true, RedirectUrl = … }` gives a `CheckoutUrl`,
  a page of the merchant's web app where the customer pays, then is sent to `RedirectUrl`.
- **The data model as the specification's JSON** (`ToJson`, `FromJson`, and `System.Text.Json`
  converters): an invoice stored by another implementation is checked here, and the other way
  round.
- **JSON read alike everywhere** (SPEC.md §2): HTTP answers, relays' messages, records, stored
  invoices and the explorer's WebSocket, by one reader, as JavaScript's `JSON.parse` reads them,
  and no more than 64 arrays and objects nested.
- **Outbound requests**: https URLs only (a lowercase host name, no IP address, no host under
  `localhost`), no redirect followed, at most 1 MiB read of an answer's body and 10 MiB of all of
  it, its head included, and `User-Agent: sattleme-Checkout/1`. The relays' and the explorer's WebSocket handshakes follow no
  redirect, carry the same User-Agent and offer no compression; every wait (a connection, its DNS
  lookup included) is bounded by its timeout and ends at once on a cancellation; a read keeps only
  what opens with its keys, once per event, so a relay cannot make the server's memory grow.
- **Typed exceptions**, `SattlemeException` and one subclass per code, with a stable `Code` and
  whether they are `Retryable`. No key, nor any key derived from it, in an exception, a log, a
  string or a serialization.
