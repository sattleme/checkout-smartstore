# Changelog

## 1.0.0 - 2026-11-24

First version: Bitcoin payments, in Lightning or on-chain, to the merchant's sattle.me wallet,
for Smartstore 6.4 (on .NET 10; built and tested against 6.4.0), on the sattle.me checkout SDK
for .NET (`Sattleme.Checkout` 1.0.0, commit `946eb62` of
[checkout-dotnet](https://github.com/sattleme/checkout-dotnet), compiled in under the module's
own names).

- A redirection payment method: once Smartstore has stored the order, the module makes its invoice
  and the customer pays it on the module's page in the shop (one QR code, the wallet link, the
  Lightning invoice and the address to copy, the amounts, the time left, the rest of an
  underpayment, the confirmations), which works without JavaScript and is never cached nor
  indexed; the order's page and its Complete payment lead back to it only while a payment can be
  made there, and say what came of an underpayment.
- A random reference per invoice, stored before the SDK is asked; the order locked in the database
  around the invoice's making; one invoice that may be paid per order; only the SDK's `paid` status
  of the server's own check completes the order, once, through Smartstore's `MarkOrderAsPaidAsync`,
  and a completion Smartstore stopped half-way is finished by a later check, its notes written
  once. An order cancelled then paid stays cancelled, and one that owes another amount by then
  stays pending: both with a note and Smartstore's new payment notification. Each note says what
  came against what the invoice asked, and how it came (Lightning, on-chain, or both) when the
  SDK's status tells it (a paid invoice).
- Background checks by Smartstore's scheduler, at every run while an invoice can be paid, around
  the end of its grace period, then about every five minutes until its final status is known, for
  90 days at most, the waits counted from each run's start; `expired` is final as soon as the SDK
  reports it.
- On-chain payments read on one block explorer: mempool.space, or the merchant's own Bitcoin node
  when the merchant names one in the sattle.me app (mempool.space as its fallback unless the
  merchant chose none).
- The integration key verified when saved, stored encrypted per store scope, never shown again; the
  order's Bitcoin tab in the admin area (each preimage with the payment hash it proves); a
  customer's deletion or anonymization removing what leads to their coins, and their GDPR export
  including their Bitcoin payments; no uninstallation while a payment may still come.
- English, French, German and Italian.
- Distributed as a GitHub release of this repository (the package Smartstore's admin uploads, and
  its SHA256SUMS), made by the release workflow.
