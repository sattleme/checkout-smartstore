# sattle.me for Smartstore

A payment module for [Smartstore](https://github.com/smartstore/Smartstore): customers pay their
orders in bitcoin, in Lightning or on-chain, and the bitcoin goes straight to the merchant's
[sattle.me](https://sattle.me) wallet. It is built on the sattle.me checkout SDK for .NET,
`Sattleme.Checkout` ([source](https://github.com/sattleme/checkout-dotnet)), which makes and checks
the invoices: no sattle.me server, no API key of a third party, and the integration key can make
invoices that pay the merchant, never spend the funds.

- [How it works](#how-it-works)
- [Requirements](#requirements)
- [Installation](#installation)
- [Configuration](#configuration)
- [Payments and orders](#payments-and-orders)
- [Background checks](#background-checks)
- [Security](#security)
- [The SDK in the module, isolated](#the-sdk-in-the-module-isolated)
- [Privacy](#privacy)
- [Known limits](#known-limits)
- [Troubleshooting](#troubleshooting)
- [Development](#development)
- [Releases](#releases)
- [License](#license)

## How it works

1. At checkout, the customer chooses **Bitcoin** (or the name the merchant gives the method) and
   confirms the order. Smartstore places it, its payment **pending**.
2. Once Smartstore has stored the order, the module asks the SDK for its invoice: a Lightning
   invoice, a deposit address of the merchant's wallet, or both, depending on the amount (see
   [the SDK](https://github.com/sattleme/checkout-dotnet#on-chain-payments)). Smartstore then
   sends the customer to the module's payment page, in the shop's design and language (a
   redirection payment method: `PostProcessPaymentAsync` gives the page's address, as Smartstore's
   documentation describes them). It shows one QR code of the invoice (BIP-21 with `lightning=`,
   or `lightning:`), a button that opens the customer's wallet with it, the Lightning invoice and
   the address to copy, the amount (in bitcoin, in sats and in the order's currency), and the time
   left to pay; the rest of an underpayment, and "being confirmed" while an on-chain payment waits
   for its confirmations. When no invoice could be made, the page says why and offers to try
   again.
3. The page follows the payment on its own. Once the SDK reports it **paid**, the module marks the
   order paid through Smartstore (`MarkOrderAsPaidAsync`), which then goes on as for any paid order
   (its status, its emails, its downloads).

The page is rendered on the server (the QR code is an inline SVG drawn from numbers); its small
script, a file of the module, only counts down, copies, and reloads the page when the payment
moved on. Without JavaScript, the page offers a button to check the payment. The customer's page
of the order (My account → Orders) says where its payment is (of an underpayment that ended, what
came against the amount, as the payment page does), and links to the payment page, as does its
**Complete payment** button, only where a payment can be made there: an invoice that can be paid
(or the rest of an underpayment), or a new invoice once the last one ended with nothing received;
never while a payment is being confirmed or once one came, nor after an invoice ended underpaid.
In the admin area, the order's page has a tab **Bitcoin payment (sattle.me)** with its invoices.

Unlike the sattle.me plugin for nopCommerce, which makes the invoice while the order is placed,
the module makes it once the order is stored: Smartstore calls a payment method's
`ProcessPaymentAsync` inside the database transaction that places the order
(`OrderProcessingService.PlaceOrder.cs:63-73` at 6.4.0), which a call to the merchant's relays
must never hold open, and calls `PostProcessPaymentAsync` once it is committed
(`CheckoutWorkflow.cs:482-494`). `ProcessPaymentAsync` only refuses the order, with a message, when
the store cannot take Bitcoin payments at all (no readable key, or a primary currency the SDK does
not convert).

## Requirements

- **Smartstore 6.4** (`module.json`: `MinAppVersion` `6.4.0`). 6.4.0 is Smartstore's latest
  release (tag `6.4.0`, commit `fc75d48139743a2502ec654e35f42f1e3d0ac0ba`, 2026-05-29), still
  maintained: its branch `6.4.0.x` takes its fixes (its last commit 2026-11-20). Smartstore's
  `main` is 7.0.0, on .NET 11, not released; its `SECURITY.md` gives security updates to its
  development branch, and a new release when required. The module targets Smartstore 6.4's
  framework, **.NET 10** (`net10.0`, a long-term support release supported until 2028-11-14),
  and is built and tested against the assemblies of Smartstore's release 6.4.0
  (`Smartstore.Community.6.4.0.linux-x64.zip`, SHA-256
  `3f822a9d65a6255afe03dabefd1e567ed1e9abe68c63f66cbb41a6f03e7ae6ae`). Smartstore installs a module
  whose `MinAppVersion` is its own version, or a lower one when no breaking change is recorded
  since (`SmartstoreVersion.IsAssumedCompatible`).
- Smartstore's database, any it supports (SQL Server, MySQL, PostgreSQL, SQLite): the module's two
  tables are made by its migration (FluentMigrator, as Smartstore's own modules), the conditional
  updates are EF Core's. The tests and the end-to-end test run on SQLite.
- Smartstore's **scheduler** running, as it does in any installation: it calls its own URL every
  minute to run the scheduled tasks ([Background checks](#background-checks)).
- A merchant account of sattle.me, and an **integration** created in its app.
- **Outbound connections** from the Smartstore server (never from the customer's browser):
  - the merchant's **Nostr relays**, over WebSocket (TLS): those the integration key names, or the
    merchant's app;
  - the merchant's **Lightning address** (breez.tips for a sattle.me account) and the URLs its
    provider gives (its callback, its verify URLs), over HTTPS;
  - the **rate sources**, one after the other until one answers: CoinGecko, mempool.space,
    Coinbase, Kraken, OpenNode;
  - for an invoice with a deposit address, one block explorer: **mempool.space**, or the
    merchant's own **Bitcoin node** when the merchant names one in the sattle.me app (the server
    then reads the chain from it, mempool.space as its fallback unless the merchant chose none);
    and **api.blockcypher.com** for a transaction no check saw in time.

  Every request, and the handshake of each WebSocket, says `User-Agent: sattleme-Checkout/1`, and
  goes through the server's system proxy when it has one (.NET's default). See the SDK's
  [Outbound requests](https://github.com/sattleme/checkout-dotnet#outbound-requests).

### Smartstore's APIs the module uses

Each checked in Smartstore's source at the tag `6.4.0`, and run by the tests (against the release's
assemblies) or the end-to-end test (in the release's official image):

| API                                                                   | For                                                        |
| --------------------------------------------------------------------- | ---------------------------------------------------------- |
| `PaymentMethodBase`, `[PaymentMethod(PaymentMethodType.Redirection)]` | the payment method                                         |
| `PostProcessPaymentRequest.RedirectUrl`, `CanRePostProcessPaymentAsync` | the payment page, the order's **Complete payment**        |
| `IPaymentMethodFilter`                                                | the method hidden without a key or a convertible currency |
| `Order.CanMarkOrderAsPaid`, `IOrderProcessingService.MarkOrderAsPaidAsync` | the completion                                        |
| `Order.HasNewPaymentNotification`                                     | the merchant told what to settle                           |
| `ModuleBase`, `IConfigurable`, `ITaskStore.GetOrAddTaskAsync`          | the installation, the scheduled task                       |
| `ITask`, `[TaskName]`                                                 | the background checks                                      |
| `SmartDbContext`, `IDbContextConfigurationSource`, FluentMigrator `Migration` with `[MigrationVersion]` | the module's tables  |
| `ISettingService`, `ISettingFactory`, `[LoadSetting]`/`[SaveSetting]`, `IEncryptor` | the settings and the encrypted key          |
| `AdminController`, `PublicController`, `[Permission]`                  | the configuration, the order's tab, the payment page       |
| `TabStripCreated`, `IWidgetProvider`, endpoint filters                | the order's tab, the customer's order page                 |
| `GdprCustomerDataExportedEvent`, `CustomerAnonymizedEvent`, `AsyncDbSaveHook<Customer>` | privacy                                   |
| `IXmlResourceManager` (resource files `Localization/resources.*.xml`)  | the texts                                                  |
| `ICurrencyService.PrimaryCurrency`, `IStoreContext`, `IDateTimeHelper`  | prices, the note of an invoice, times                      |

## Installation

The module is distributed as a GitHub release of this repository: a package
`Smartstore.Module.Sattleme.Bitcoin.<version>.zip` (`manifest.json` and the folder
`Modules/Sattleme.Bitcoin/`, named as Smartstore's packager names a module's package), and its
`SHA256SUMS`.

1. Download the package of the latest [release](https://github.com/sattleme/checkout-smartstore/releases),
   and check it: `sha256sum --check SHA256SUMS`.
2. In Smartstore, **Admin → Plugins → Manage plugins → Upload plugin**, and choose the package
   (who may upload plugins). Smartstore extracts it into `Modules/Sattleme.Bitcoin/`.
3. **Restart application** (the store's menu at the top right): Smartstore lists the module
   **Bitcoin (sattle.me)** (group Payment).
4. **Install** it; Smartstore restarts and installs it as it starts again: its tables (its
   migration), its settings, its texts in each of the shop's languages, and its scheduled task.
5. Configure it ([Configuration](#configuration)).

Or copy the package's folder `Modules/Sattleme.Bitcoin/` into Smartstore's `Modules/` directory,
restart, and install it as above. To update the module, upload the new package and restart.

**Uninstalling** the module removes its settings (the integration key included), its texts and its
scheduled task, and keeps its tables, that is its invoices, as Smartstore's guidelines ask: a
reinstalled module finds them again. It refuses while a payment may still come (an invoice that can
still be paid, or a payment being confirmed), and Smartstore says how many.

## Configuration

1. In the sattle.me app, create an integration for your shop and copy its secret key
   (`sattleme_ck_…`).
2. In Smartstore, **Admin → Configuration → Payment methods → Bitcoin → Configure** (who may read
   payment methods sees the page, who may update them saves it), or the module's **Configure** in
   Plugins → Manage plugins.
3. Enter the **integration key**. It is checked when you save it: the SDK refuses what is not an
   integration key, then reads the merchant of the integration on its relays, so that the key of an
   integration removed in the app, or of one the relays do not know, is refused. Once saved, it is
   never shown again: the page says whether a key is saved, and a new one replaces it. In a store's
   own configuration (Smartstore's multi-store scope), a store may have a key of its own, which
   takes over every store's; **Remove the key** removes the key of the scope shown.
4. **Time to pay**: how long the customer has to pay an invoice, 1 to 60 minutes (15 by default).
5. Optionally, an **additional fee**, fixed or a percentage, as Smartstore's own payment methods
   charge it.
6. **Time zone.** Set the store's time zone: Admin → Configuration → Settings → General
   settings, **Default store time zone** (`DateTimeSettings.DefaultStoreTimeZoneId`). The payment
   page, the customer's order page and the order's Bitcoin tab show their times as Smartstore
   shows its own, in that time zone (or the customer's, when **Allow customers to select time
   zone** is on and they chose one). Without one, Smartstore uses the server's time zone
   (`DateTimeHelper.DefaultStoreTimeZone`: `TimeZoneInfo.Local`), which is UTC in Smartstore's
   Docker image: the customer then reads the time to pay in UTC.
7. **Activate** the method: **Configuration → Payment methods**, **Activate** on Bitcoin. There
   too (**Edit**), its name and description at checkout in each language, its icon, the stores and
   the rules it is limited to, as for any payment method of Smartstore.

The method is offered only with a key the store can read and when the store's primary currency is
one the SDK converts (its table of currencies); the configuration page warns otherwise. Prices are
charged in that currency, as Smartstore's own payment methods charge them.

Change the key only while no Bitcoin payment is pending: each check uses the key set for the
order's store at its time, and an invoice with a deposit address made under another integration's
key would be judged without its records in the merchant's registry (its lease, its tolerance, the
invoices of its rest).

**Test mode.** sattle.me has no test network: payments are real bitcoin payments, to the
merchant's wallet.

## Payments and orders

| What the SDK reports                     | What the module does                                                                   |
| ---------------------------------------- | -------------------------------------------------------------------------------------- |
| An invoice was made                      | The order's payment stays **pending**; the customer is on the payment page.            |
| No invoice could be made                 | The page says why (the SDK's error, in plain words) and offers to try again; a note.   |
| `open`                                   | Waits. A new invoice only once a check after its end finds it expired.                 |
| `open` with `received` (on-chain, short) | The page shows what came and a QR code for the rest (the SDK asks for it).             |
| `unconfirmed`                            | Waits for 3 confirmations (about 30 minutes); a note on the order.                     |
| `paid` (Lightning, on-chain, or both)    | Marks the order **paid**, its capture transaction the invoice's reference; a note.     |
| `paid`, the order owing another amount   | Leaves the payment **pending**, with a note and a new payment notification.            |
| `paid` with `received` other than `sats` | Notes both on the order: the merchant's tolerance or acceptance, or an overpayment.    |
| `paid`, the order cancelled or deleted   | Leaves the order **cancelled**, with a note and a new payment notification.            |
| `paid`, the order paid otherwise         | Notes it, with a new payment notification: the merchant refunds one of the two.        |
| `expired`                                | Notes it: the customer can get a new invoice on the payment page.                      |
| `expired` with `received` above 0        | Notes what reached the wallet, with a new payment notification; no new invoice.        |

`expired` is final as soon as the SDK reports it (see [Known limits](#known-limits), "An
underpayment accepted, read late").

- **Only a `paid` status completes an order**, from the SDK's own check of the invoice the order
  stored, on the server: never because the customer's browser says so. The order is marked paid
  once, through Smartstore's `Order.CanMarkOrderAsPaid` and `MarkOrderAsPaidAsync`
  (`OrderProcessingService.Payment.cs:21-42`), whichever check sees the payment first (the page's
  or the background's): each check holds the invoice alone (a conditional update of its record),
  and so does the completion. Smartstore then completes an order with nothing to ship, or moves it
  to Processing.
- **What the merchant must settle** is never left to the order's notes alone: the module sets the
  order's **new payment notification** (`Order.HasNewPaymentNotification`, the badge Smartstore's
  admin shows in the list of orders until the order is opened: `OrderController.cs:1470-1472`, as
  Smartstore's guide "Creating a payment provider" asks for any payment message), and logs a
  warning (Admin → System → Log).
- **One try, one reference.** Each invoice is asked for a random reference (`ss-` and 128 bits),
  stored before the SDK is asked, never used for another try nor taken from the order's number: no
  deleted order, restored backup or other installation can share it. A try that follows a failed
  one (a timeout) asks for the same reference: the SDK then hands out the invoice it may have made
  meanwhile (`InvoicePendingException`), never a second one, or reports it paid
  (`OrderPaidException`), and the order is completed by the next check.
- **The order locked.** The invoice is made with the order locked in the database (a conditional
  update of the module's record of the order, on every database Smartstore runs on): two requests
  at once (a double click, two servers) get one invoice.
- **One payable invoice per order.** A Lightning invoice cannot be revoked: until the order's
  invoice is known to have ended unpaid, the customer cannot get another one (the page says until
  when), so that the order is never paid twice. Known from its final status only, `expired` with
  nothing received, which a check after its end (its 120 seconds of grace included) gives.
  Smartstore does not let the customer change the payment method of a placed order: once the
  invoice expired with nothing received, the page offers a new one (the order's **Complete
  payment** leads to it too: `PaymentService.CanRePostProcessPaymentAsync`,
  `PaymentService.cs:449-477`, allows it while the order is pending payment, not cancelled nor
  deleted, and the shop allows re-posting payments, then asks the module, which allows it only
  where a payment can be made).
- **Cancelled, then paid.** Smartstore has no "on hold" status (its order statuses are Pending,
  Processing, Complete and Cancelled: `OrderEnums.cs:23-44`), and nothing takes an order out of
  Cancelled (`OrderExtensions.CanMarkOrderAsPaid` and `CanCompleteOrder` refuse it,
  `OrderExtensions.cs:24-29` and `82-94`, and its admin has no other way). A payment that comes for
  a cancelled (or deleted) order is never refused: the order stays cancelled, a note says what came
  and that the merchant refunds or settles it, the order gets a new payment notification, and the
  log a warning. Smartstore's customers cannot cancel an order themselves (its storefront has no
  such action: `Controllers/OrderController.cs` at 6.4.0), and none of its scheduled tasks
  cancels an unpaid order: only the shop's staff does (Admin → Orders, Cancel order; or the Web
  API's `OrdersController.Cancel`, `IOrderProcessingService.CancelOrderAsync`), and the order's
  Bitcoin tab warns while a payment may still come, and once an invoice ended underpaid, what
  reached the wallet, to settle first.
- **An order changed after its invoice.** Smartstore's admin can edit an order's totals in any
  state (Admin → Orders → Edit: `OrderController.EditOrderTotals`, `OrderController.cs:1003`). When
  an invoice is found paid, the module compares its price with what the order owes then, in the
  store's primary currency: its total, which Smartstore computes net of its gift cards, reward
  points and credit balance (`OrderCalculationService.cs:144-265`), less what was refunded. When
  they differ, the order is **never completed automatically**: its payment stays **pending**
  (Smartstore's unpaid orders), with a note of what came and what the order owes now, a new
  payment notification and a warning in the log; the merchant settles the difference with the
  customer, then marks the order paid by hand.
- **Deadlines.** Smartstore cancels no unpaid order on its own (none of its scheduled tasks does).
- **Underpayments.** When less than the amount comes on-chain in time, the page shows the rest, as
  a Lightning invoice the SDK asks for. The merchant may accept an on-chain payment a little short
  of the amount (its tolerance, set in the sattle.me app): the SDK then reports it `paid`, and the
  order's notes say how much came. So it does when the merchant accepts by hand, in the sattle.me
  app, an order underpaid on-chain at what it received (the merchant's acceptance): being confirmed,
  then `paid` with `received` below `sats`, and the order is marked paid. The SDK's status does not
  say which of the two applied: the note names both ("by your tolerance or your acceptance in the
  sattle.me app").
- **Refunds** are not automatic: a Lightning payment can only be refunded to an invoice of the
  customer's, an on-chain one to an address of theirs, which Smartstore never asks. Refund the
  customer from the wallet, then record it in Smartstore (the order's offline refund); the order's
  Bitcoin tab says so.
- The order's Bitcoin tab shows, for each invoice of the order: its reference, its state ("Ended
  underpaid" when something came, never "unpaid"; a first check that failed as such, not "waiting
  for its first check"), the amount in sats and BTC and the price it was converted from, the
  exchange rate (its source and time), what came (of a Lightning payment, its invoice's amount),
  the confirmations, the deposit address and the transactions (linked to mempool.space; those of
  an underpayment, whose status lists none, on its deposit address's page), each proof of payment
  (preimage) with the payment hash it proves (its SHA-256), when it was made, ends, was last
  checked and is checked next, and its last error in plain words. Once an invoice ended
  underpaid, the tab says what reached the wallet, to settle before cancelling the order. The order's notes (admin only) log the invoices
  made, the payments being confirmed and paid, the underpayments, the payments left to the
  merchant, those of another amount than the invoice's, and why an invoice could not be made.
  Each note of a payment says what came against what the invoice asked, and says how only when the
  SDK's status tells it: a `paid` status gives its method, so its note says Lightning (its
  invoice's own amount), on-chain (how many transactions paid it, never which: a note holds no
  address nor transaction), or both (the on-chain transactions and the Lightning invoices of the
  rest, by count); `unconfirmed` and an `expired` with `received` give none, so their notes say
  what came, against what was asked, and where it went, never how.
- **Times** (when an invoice ends, on the payment page, the customer's order page and in the
  messages; when it was made, checked and ends, in the admin's tab) are in the reader's time zone
  as Smartstore gives it (`IDateTimeHelper.ConvertToUserTime`, `DateTimeHelper.cs:67-125`): the
  customer's own when the shop lets customers set theirs
  (`DateTimeSettings.AllowCustomersToSetTimeZone`), else the store's default time zone
  (`DateTimeSettings.DefaultStoreTimeZoneId`; both under Admin → Configuration → Settings →
  General settings), else the server's. The order's notes hold no time.

## Background checks

The SDK has no webhooks: payments are checked, by the module, on the Smartstore server.

- While the customer's payment page is open: the page asks every few seconds, and the server checks
  the invoice at most every 5 seconds, whoever asks.
- In the background, Smartstore's scheduler (it polls every minute: `DefaultTaskScheduler.cs:36`)
  runs the module's task, **Check the Bitcoin payments (sattle.me)**, every minute (cron
  `* * * * *`; Smartstore's list of scheduled tasks, Admin → System); its executor never starts a
  run while the last one is still running (`TaskExecutor.cs:53`). A run checks the invoices due
  when it starts. Each invoice is checked at every run while it can be paid, once 10 seconds
  before the end of its grace period (an on-chain payment a check lists by then came in time) and
  once 10 seconds after (its final status), then at every fifth run, about every five minutes,
  until its final status is known (an on-chain payment being confirmed). After failed checks: one
  minute later, then twice as long each time, up to every 30 minutes. Those waits count from the
  start of the run that made the check, less 5 seconds, so that the run a minute (or five) later
  takes it: counted from the check's end, which comes seconds after its run started, they would
  miss the run a minute after (every two minutes, every six). A run checks the invoices that are
  due, the longest due first, 20 at most and for about 45 seconds (each check is bounded at 20
  seconds); one whose payment page is open is checked by the page, and a run skips it when the
  page's check holds it or just ended.

An invoice whose final status is still unknown after 90 days (an on-chain payment made in time,
still unconfirmed on a congested network) is no longer checked; a note says so on its order, whose
payment stays pending: check it in the sattle.me app and settle it by hand.

If the module finds a payment but Smartstore fails to mark its order paid (its database, a module
reacting to the payment), the invoice stays unsettled and a later check marks the order paid;
what Smartstore's failed call left unsaved in the request is dropped, never saved by a later one
(the scheduled task checks several invoices in one request). If Smartstore stops half-way
(`MarkOrderAsPaidAsync` saves the order paid, with the invoice's reference as its capture
transaction, then completes an order with nothing to ship, its e-mails, and publishes its
`OrderPaidEvent`), a later check finishes it: an order already paid by this invoice gets the
module's notes then, each written once, never a "paid twice" alert. What Smartstore does after a
payment is not run again: its log says what failed.

## Security

- **The integration key** is stored in Smartstore's settings (`SattlemeSettings.EncryptedIntegrationKey`,
  one per store scope), **encrypted** with Smartstore's encryptor (`IEncryptor`: TripleDES,
  `Encryptor.cs:66`, keyed by the store's encryption key, Admin → Configuration → Settings →
  General settings → Security), as Smartstore encrypts its own secrets. That key is a setting of
  the same database: the encryption keeps the integration key out of plain sight (the list of all
  settings, an export of them), not from whoever reads the whole database. Smartstore encrypts
  nothing again when its encryption key is changed, and publishes no event for it: the saved key
  can then no longer be read, the method is no longer offered, and the configuration page says
  so: enter it again. The key never goes back to the browser (the field
  is a password field without a value; a key refused is not sent back either), and never goes in a
  log, an exception, a page or a note of the module; the SDK never puts it, or a key derived from
  it, in a message or a representation.
- **One key, one shop.** Create an integration for your Smartstore shop, and use its key nowhere
  else. If it leaks, remove the integration in the sattle.me app and create a new one: the key can
  make invoices that pay you, never spend your funds.
- **Payment links** carry the order's GUID and a secret of the order (32 random bytes), compared in
  constant time: an order's number never opens them. Like the order's own page, a payment link is
  the customer's. The pages are never cached (`Cache-Control: no-store`), never indexed (`noindex`
  in the page and `X-Robots-Tag`), send no referrer, and use no inline script of the module's; the
  shop's own checks of its public pages apply (Smartstore's `PublicController`: a closed store,
  access to the shop). Nothing the browser sends decides an amount, a currency, a reference or a
  status: the page and its status show what the server's own checks found.
- **Forms.** Every change posts Smartstore's antiforgery token (the configuration, which
  Smartstore's admin area validates; the new invoice, which the module validates); the
  configuration page is the admin area's, for who may read or update payment methods, the order's
  tab for who may read orders. What the pages show is encoded (Smartstore's `T()` writes its texts
  encoded; the only raw HTML is the QR code's SVG, made of numbers).
- **Outbound requests.** The SDK connects to hosts the merchant chooses (its relays, its Lightning
  address, its Bitcoin node). It refuses IP addresses and `localhost`, follows no redirects and
  checks TLS, but a name may still lead to an address of your own network: if your Smartstore
  server sits in a network with services of its own, restrict what it can reach to public
  addresses (see the SDK's
  [Outbound requests](https://github.com/sattleme/checkout-dotnet#outbound-requests)). The
  merchant's Bitcoin node has a rule of its own: `https`, or plain `http` to a host of a local
  network (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 100.64.0.0/10, a name under `.local`), an
  IPv4 address allowed, never `localhost` nor 127.0.0.0/8; GET requests only, at mempool.space's
  paths. What it shows decides whether an order is paid, as mempool.space's does: a node on a
  local network is reachable only by a shop on that network, and over `http` it is as safe as
  that network (the SDK's
  [The merchant's Bitcoin node](https://github.com/sattleme/checkout-dotnet#the-merchants-bitcoin-node)).
- Report a vulnerability privately: see [SECURITY.md](SECURITY.md).

## The SDK in the module, isolated

Smartstore loads a module's main assembly into the application's load context, and the module's
other assemblies (its private references) into a load context of the module's own
(`ModuleAssemblyInfo.cs:17-23`, `ModuleAssemblyLoadContext.cs`: an `AssemblyDependencyResolver` on
the module's `.deps.json`), which an assembly resolver of the engine asks when the main assembly
needs one (`SmartEngine.cs:48-56`, `ModuleReferenceResolver.cs`). The isolation is partial: an
assembly that the application, or another module's main assembly, already loaded by name serves a
module's request first. So the module ships neither `Sattleme.Checkout.dll` nor
`NBitcoin.Secp256k1.dll`, nor a QR code library of a common name, which another module of the same
shop could carry at another version: the SDK's sources (`sdk/`), `NBitcoin.Secp256k1`'s at the
exact version the SDK pins (4.0.4, its repository's tag `NBitcoin.Secp256k1/v4.0.4`) and the QR
code generator's (`Net.Codecrete.QrCodeGenerator` 3.2.1, tag `v3.2.1`), all MIT and pinned in
`sdk/` and `vendor/` unchanged, are compiled into three assemblies of the module's own names
(`Sattleme.Bitcoin.Sdk`, `.Secp256k1`, `.QrCode`), their namespaces prefixed with
`SmartstoreSattleme.` (`src/Embedded/Embedded.targets`). It is the simplest reliable way: the SDK
reads nothing of its own name at run time (no reflection, no embedded resource: its README, "In a
platform's plugin"), so it builds and runs renamed as it is, with no tool to maintain beside the
compiler. The tests check the package's assemblies (their names, their references, every type under
the prefix, nothing Smartstore does not ship), that they are byte for byte the ones tested, and run
the SDK's own vectors for integration keys, BIP-340 signatures and NIP-44 against them; the
end-to-end test runs them in Smartstore, where the SDK is loaded into the module's own context.

## Privacy

- **What the module stores**, in Smartstore's database (its tables `SattlemeOrder` and
  `SattlemeInvoice`): for each order paid with Bitcoin, the secret of its payment page; for each
  invoice, its reference, the price it was asked for, the invoice as the SDK made it (amounts in
  sats and in the store's currency, exchange rate, Lightning invoice and, from 1,000 sats, a deposit
  address of the merchant's wallet) and what its checks found (its state, what came, the
  confirmations, the transactions, the proof of payment). No name, no email, no IP address: the
  customer's are Smartstore's own.
- **What the Smartstore server sends**, and to whom (the SDK's
  [What `CreateInvoiceAsync` does](https://github.com/sattleme/checkout-dotnet#what-createinvoiceasync-does)):
  - the merchant's **Nostr relays**, encrypted for the merchant (NIP-44): each invoice, with its
    price and its note (the store's name and the order's number), which the merchant's app shows;
  - the merchant's **Lightning address** (breez.tips for a sattle.me account): asked for each
    invoice, its amount and, when the address accepts comments, its note, then for its payment;
  - the **rate sources**, one at a time until one answers: asked for the price of bitcoin;
  - the block explorer, for an invoice with an address: **mempool.space**, or the merchant's own
    **Bitcoin node** when the merchant names one in the sattle.me app (mempool.space as its
    fallback unless the merchant chose none): the address and its transactions;
    **api.blockcypher.com**: a transaction, only for one no check saw in time.

  Each of them sees the Smartstore server's IP address, never the customer's: the customer's
  browser talks to the shop only (the payment page loads nothing from elsewhere).
- **Removing personal data.** When Smartstore deletes a customer (Admin → Customers → Delete:
  `CustomerService.DeleteCustomersAsync`, a soft deletion that keeps their orders,
  `CustomerService.cs:183-186`; a guest with orders is never deleted otherwise, line 260), or its
  GDPR tool anonymizes one (`IGdprTool.AnonymizeCustomerAsync`, which publishes
  `CustomerAnonymizedEvent`), the module removes what leads to the customer's coins on the
  blockchain from the invoices of their orders: the deposit address (and the payment URI that holds
  it), the transactions, and the rest's QR code. Their references, amounts, exchange rates, dates
  and states stay, for the merchant's accounting; so do their Lightning invoices, which name the
  merchant's node, never the customer's, and the proofs of payment. An invoice still being checked
  then (an on-chain payment being confirmed) loses them once final. The order's notes never hold an
  address nor a transaction. Smartstore's GDPR export of a customer (Admin → Customers → Edit,
  Export: `GdprTool.ExportCustomerAsync`, which publishes `GdprCustomerDataExportedEvent`) includes
  their Bitcoin payments. What the merchant's relays received stays theirs: the merchant's app
  keeps its sales.

## Known limits

- **Smartstore 6.4**: a later version is accepted as long as Smartstore records no breaking change
  since 6.4.0; 7.0.0 (on .NET 11, with breaking changes) is not released, and the module is not
  tested on it: it will need a new version of the module.
- **Currencies.** The store's primary currency must be one the SDK knows (its table of currencies)
  and one its rate sources price. An amount it cannot convert, or out of the merchant's limits, is
  refused when the invoice is made: the payment page says so, and the order stays pending.
- **One method per order.** Smartstore does not change the payment method of a placed order: a
  customer whose invoice expired gets a new one, or the merchant cancels the order.
- **Confirmations.** An on-chain payment is paid at 3 confirmations on the block explorer read
  (mempool.space, or the merchant's Bitcoin node), about 30 minutes: the order stays pending until
  then. What that one explorer shows decides whether the order is paid.
- **The merchant's Bitcoin node.** With its fallback set to none, while the node does not answer
  no deposit address is leased (an order payable on-chain only gets no invoice, the others a
  Lightning invoice only) and no on-chain payment is judged: the invoice stays unsettled, its
  order pending, until the node answers again (90 days at most, as for any invoice). A node on
  the merchant's own network is reachable only by a Smartstore server on that network.
- **An underpayment accepted, read late.** `expired` is final for the module as soon as the SDK
  reports it. In one rare case the specification names
  ([SPEC.md §3](https://github.com/sattleme/checkout-spec/blob/1bd7966d0cff77c2538c00e8929b65f0796d9220/SPEC.md#3-data-model)),
  the merchant accepted an underpaid order in the sattle.me app, but a relay holding that
  acceptance failed at the check after the invoice's end: the SDK then reports `expired`, and the
  shop shows the order as ended underpaid, its payment pending (or the order cancelled), although
  the merchant accepted it. The module does not check that invoice again: the merchant settles the
  order by hand (marks it paid).
- **Time.** Making an invoice runs in the request that follows the order's placement, bounded at
  25 seconds; a check, in the request of the payment page or in the scheduled task, at 20 seconds.
- **The scheduler** must reach the shop from the server (Smartstore's scheduler calls its own URL):
  when it cannot, the background checks do not run (Smartstore's log says so); the page's checks
  still do, while it is open.
- **Deposit addresses.** Each invoice of 1,000 sats or more may lease an address of the
  integration's lot for its time: many orders left unpaid at once can empty it, the next ones are
  then payable in Lightning only until the merchant opens the app (see the SDK).

## Troubleshooting

The order's Bitcoin tab and the order's notes say what went wrong, in plain words; Smartstore's log
(Admin → System → Log, messages starting with `sattle.me:`) says it too, without the key.

- **The key is refused when saving the settings.**
  - "This is not the secret key of a sattle.me integration": copy the whole key from the app,
    `sattleme_ck_` included.
  - "This integration was removed in the sattle.me app": create a new integration, and enter its
    key.
  - "This integration could not be found": the merchant's relays do not hold it (yet): open the
    sattle.me app, check the key, and try again.
  - "This integration needs a newer version of the sattle.me module": update the module.
  - "This key could not be verified": the shop's server could not reach the sattle.me service.
    Check its outbound connections ([Requirements](#requirements)).
- **The method is not offered at checkout**: the method not activated (Configuration → Payment
  methods), no key saved for the store or one that cannot be read (the store's encryption key
  changed: "A key is saved, and it cannot be read": enter the key again), or a primary currency the SDK does not convert (the configuration
  page warns).
- **"Bitcoin payments are not available right now"**: Smartstore's log says why: an integration key
  no longer valid or removed, the integration not found on its relays, the merchant's Lightning
  address answering wrongly, or a clock that is off (keep the server's synchronized).
- **"The Bitcoin invoice could not be made right now"**: the relays, the Lightning address or the
  rate sources did not answer in time, or the amount can only be paid on-chain and no deposit
  address of the integration was free (the merchant opens the app to give it more). The customer
  tries again from the payment page; if it goes on, check the server's outbound connections.
- **"This order cannot be paid with Bitcoin"**: the SDK refused the amount or the currency
  ([Known limits](#known-limits)).
- **A payment stays pending.** The order's Bitcoin tab shows its last check and its next one. No
  check at all: the scheduled task **Check the Bitcoin payments (sattle.me)** must be enabled and
  Smartstore's scheduler must run. An on-chain payment waits for 3 confirmations, about 30 minutes,
  longer with a low fee. "no final status after 90 days, its checks stopped": check the payment in
  the sattle.me app, and settle the order by hand.
- **An order with a new payment notification**: its notes say what to settle (an underpayment, a
  payment of a cancelled order, of an order whose total changed, or of one paid otherwise).
- **The module cannot be uninstalled**: a payment may still come for some orders; wait until their
  invoices are settled (or check them in the sattle.me app).

## Development

The checks need the .NET SDK of `global.json` (10.0.404), git, curl and Python 3; the end-to-end
test, Docker.

```shell
scripts/check.sh   # every check, as the CI runs them
e2e/run.sh         # the end-to-end test, with Docker
```

- **Smartstore's assemblies.** The module is built as Smartstore's documentation shows for a
  module ("Getting started with modules", `Smartstore.Build/Smartstore.Module.props` at 6.4.0: a
  Razor class library of `net10.0`, its views compiled into its assembly, with Smartstore's
  application part factory), referencing Smartstore's assemblies without copying them. Smartstore
  publishes no NuGet package of them, and its source builds only as the whole application (with its
  native libraries and every module, several gigabytes); its release archive holds the very
  assemblies a shop runs. `scripts/smartstore.sh [<dir>]` downloads
  `Smartstore.Community.6.4.0.linux-x64.zip` from the release `6.4.0`, checks its SHA-256, and keeps
  the application's assemblies and its packages' (never .NET's runtime) and SQLite's native library
  (about 110 MB) in `$SMARTSTORE_DIR` or the ignored `.smartstore/`; nothing of Smartstore is kept
  in this repository.
- **The checks**, in `scripts/check.sh`: Smartstore's assemblies, the pinned copies (`dotnet run
  --project tools/Sattleme.Smartstore.Pins -c Release -- check`), the packages from the lockfiles
  (`dotnet restore Sattleme.Bitcoin.sln --locked-mode`), the build in Release (Smartstore's coding
  standard and .NET's analyzers at `latest-recommended`, nullable references, warnings as errors),
  which also makes the package (`artifacts/Release/Smartstore.Module.Sattleme.Bitcoin.1.0.0.zip`),
  the coding standard (`dotnet format --verify-no-changes`, Smartstore's `.editorconfig` rules), the
  tests, what the package holds, and the brand (`scripts/brand.sh`).
- **The tests** (NUnit and Moq, as Smartstore's own; `tests/Sattleme.Bitcoin.Tests`) run against
  Smartstore's assemblies. The shop is Smartstore's own `SmartDbContext` on SQLite, through
  Smartstore's SQLite provider, with Smartstore's tables and the module's tables made by the
  module's migration, each request with a context of its own; Smartstore's settings services,
  encryptor, package and manifest readers are its own, the rest of Smartstore is faked where the
  module calls it. They never reach the network: the module makes every client of the SDK in one
  place, `ISattlemeGateway`, which the tests replace with a fake once the real SDK has checked the
  key. They cover every status the SDK reports and its errors, the lock, the completion done once,
  the orders cancelled or changed, the schedule, the pages, the settings, the installation, privacy,
  the translations, the package, and the SDK's own vectors against the package's assemblies.
- **The end-to-end test**, with Docker: `e2e/run.sh` builds the packages, runs Smartstore's
  official image (`ghcr.io/smartstore/smartstore-linux:6.4.0`, pinned by its digest) on SQLite, in a
  container of its own, removed afterwards; `e2e/drive.py` then installs Smartstore with its sample
  data, uploads and installs the module from its package in the admin area, enters a key, activates
  the method, orders a product with it through Smartstore's checkout, checks the payment page (its
  QR code, wallet link, invoice, address, headers, script, its secret) and the customer's order
  page, has the invoice paid, and sees the order paid: once through the page's status, once through
  the scheduled task alone, for an order of a download that Smartstore then completes (its "order
  completed" e-mail queued from the task's request); gets a new invoice for an order whose invoice
  ended unpaid (refused
  without the form's antiforgery token); and uninstalls the module, refused while a payment may
  still come; `e2e/database.py` then reads the shop's database: no error of the module in
  Smartstore's log, each paid order's payment noted once (never "paid twice"), the order with
  nothing to ship complete, none of its texts, settings or task left, its invoices kept. The SDK is replaced
  there by a fake (`e2e/FakeSdk`, a test-only module installed beside it, never shipped: the module
  has no switch for it). `E2E_KEEP=1 e2e/run.sh` keeps the container of a failed run.
- **The CI** (`.github/workflows/ci.yml`) runs `scripts/check.sh`'s steps, and the end-to-end
  test, on GitHub-hosted runners.
- **The pinned copies.** `sdk/` holds the SDK's own files at a commit of
  [checkout-dotnet](https://github.com/sattleme/checkout-dotnet) (the project, its sources, and the
  vectors the tests run), unchanged, with `sdk/PIN.json` (the repository, the commit, the version,
  each file's SHA-256); `vendor/` holds NBitcoin.Secp256k1's and the QR code generator's the same
  way. To take a new version of the SDK, from a clone of checkout-dotnet:

  ```shell
  dotnet run --project tools/Sattleme.Smartstore.Pins -c Release -- import sdk <clone> [<commit>]
  ```

  (the clone's HEAD by default), which copies the files of that commit from git's objects (never the
  working tree), writes `sdk/PIN.json`, and checks them; then `scripts/check.sh`. A new SDK that
  pins another `NBitcoin.Secp256k1` takes it the same way (`import secp256k1 <clone> <tag>`).
- **Dependencies.** The module references no package: Smartstore brings its framework, and the
  SDK, NBitcoin.Secp256k1 and the QR code generator are compiled from their pinned sources. The
  tests use NUnit 5.0.0, NUnit3TestAdapter 6.3.1, Microsoft.NET.Test.Sdk 18.12.0 and Moq 4.21.0,
  and Smartstore 6.4.0's own Microsoft.Extensions.DependencyModel 10.0.8, pinned in
  `Directory.Packages.props` and the lockfiles. Dependabot proposes the updates of the actions, of
  the tests' packages and of the .NET SDK, a week after each release, one pull request per
  ecosystem (`.github/dependabot.yml`); not of the pinned copies, nor of
  Microsoft.Extensions.DependencyModel, nor a new major of the .NET SDK: those follow Smartstore's
  version, by hand (its tag and checksum in `scripts/smartstore.sh`, its image's digest in
  `e2e/run.sh`, then `Directory.Packages.props`, `global.json` and the lockfiles, `dotnet restore
  --force-evaluate`).
- **The translations** are Smartstore resource files, `Localization/resources.en-us.xml` (the
  source), `resources.fr-fr.xml`, `resources.de-de.xml` (written with ss, never ß) and
  `resources.it-it.xml`: Smartstore imports, for each language of the shop, the file of its culture
  or of its neutral language when the module is installed or a language added, and English for the
  shop's master language when none matches it (`XmlResourceManager.ImportModuleResourcesFromXmlAsync`).
  The tests read them with Smartstore's own code, and check that the four have the same resources
  and placeholders, that every resource the code and the views use is there, and every one there is
  used.

## Releases

A maintainer pushes a tag `vX.Y.Z` (the version of `Directory.Build.props` and `module.json`, with
its entry in `CHANGELOG.md`) on a commit of `main`; [`release.yml`](.github/workflows/release.yml)
runs the CI again, checks the tag, builds and tests the package at that commit, then creates the
GitHub release of the tag with the package and its `SHA256SUMS`, from its last job only (the one
with `contents: write`, with the workflow's own `GITHUB_TOKEN`; no other secret). That release is
what the sattle.me app offers to download for Smartstore.

The package is reproducible: built from the same commit, with the SDK of `global.json`, in the
same directory (a GitHub-hosted runner's `/home/runner/work/checkout-smartstore/checkout-smartstore`),
it is the same file, byte for byte. Its assemblies are deterministic (`Deterministic`, and
`ContinuousIntegrationBuild` in CI, which maps the source paths), and the archive is written in
the ordinal order of its paths, each entry dated 2026-01-01 (`SattlemeReproducibleZip` in
`src/Sattleme.Bitcoin/Sattleme.Bitcoin.csproj`). The directory, the repository's URL and the
commit count: the identifiers Razor's compiler gives the views' tag helpers, and the paths of the
views' generated sources in the debugging information (which sets the module assembly's
identifiers), depend on the directory; the repository's URL is in the assemblies' source link; the
commit is in their informational version and their source link. A build of the public repository on
a GitHub-hosted runner is therefore the same file at each run.

## License

MIT, see [LICENSE](LICENSE). The SDK, NBitcoin.Secp256k1 and the QR code generator are MIT too
(their licenses are in the package, `licenses/`). The sattle.me name and logo (`wwwroot/icon.png`,
`wwwroot/branding.png` and `wwwroot/brands/`, shown in Smartstore's lists of plugins and payment
methods) are not under that license.
