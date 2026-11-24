#!/usr/bin/env bash
# The brand (CONVENTIONS): sattle.me everywhere, authors and company included; the merchant's
# legal entity only in a LICENSE's copyright line (this repository's, the pinned SDK's) and in a
# .NET Copyright field. Every other
# mention of the former brand fails. The pinned specification's copy is left out: its base64 may
# hold those letters by chance.
set -euo pipefail
cd "$(dirname "$0")/.."
if git grep -I -i -n -E 's[w]iss|s[b]p|new[-]app' -- . ':!spec/' ':!sdk/spec/' \
  | grep -v -E '^((sdk/)?LICENSE:[0-9]+:Copyright \(c\) 2026 S[w]iss Bitcoin Pay Sàrl|[^:]+\.(props|csproj):[0-9]+: *<Copyright>Copyright \(c\) 2026 S[w]iss Bitcoin Pay Sàrl</Copyright>)$'; then
  echo "a mention of the former brand (above)" >&2
  exit 1
fi
echo "Brand: sattle.me only."
