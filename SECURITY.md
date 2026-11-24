# Security

This module lets a Smartstore shop take Lightning and on-chain payments for a sattle.me merchant.
A flaw here could let an attacker be paid instead of the merchant, mark an order paid that is not,
or learn a key: please report it **privately**, never in a public issue, pull request or
discussion.

## How to report

- On GitHub: **Security** tab of this repository → **Report a vulnerability** (private
  vulnerability reporting).
- Or by email: security@sattle.me, subject starting with `[security] checkout-smartstore`.

Please include what you found, the version of the module (`module.json`), of Smartstore and of
.NET, and, when you can, the steps or the input that show it. Do not test against real merchants'
funds or keys.

## What is in scope

- This repository's code: a way to make the module complete an order that is not paid, complete
  one twice, show or accept a wrong invoice, open a payment page without its secret, leak the
  integration key, or have the shop's server reach another host than those the SDK names.
- A flaw of the SDK it embeds goes to
  [checkout-dotnet](https://github.com/sattleme/checkout-dotnet/security), and one of the protocol
  itself (a rule of `SPEC.md` that, followed as written, is unsafe) to
  [checkout-spec](https://github.com/sattleme/checkout-spec/security), through the same channels.

We will acknowledge your report, keep you informed, and credit you when the fix is published,
unless you prefer otherwise.
