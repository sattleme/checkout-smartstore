#!/usr/bin/env bash
# Every check of the repository, as the CI runs them (README.md, "Development"): Smartstore 6.4.0's
# assemblies, the pinned copies, the packages from the lockfiles, the build (analyzers, warnings as
# errors) and the module's archive, the coding standard, the tests, what the archive holds, and the
# brand. The end-to-end test needs Docker: e2e/run.sh.
#
#   scripts/check.sh
#
# Smartstore's assemblies go to $SMARTSTORE_DIR, or .smartstore/ in this repository.
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root"
export CI=true DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export SMARTSTORE_DIR=${SMARTSTORE_DIR:-$root/.smartstore}

echo "== Smartstore 6.4.0's assemblies"
scripts/smartstore.sh "$SMARTSTORE_DIR"

echo "== The pinned copies (sdk/, vendor/)"
dotnet run --project tools/Sattleme.Smartstore.Pins -c Release -- check

echo "== Packages, from the lockfiles"
dotnet restore Sattleme.Bitcoin.sln --locked-mode

echo "== Build (analyzers, warnings as errors), and the module's archive"
dotnet build Sattleme.Bitcoin.sln -c Release --no-restore

# After the build: the embedded projects compile copies the build writes (third-party code, not
# formatted here).
echo "== Coding standard"
dotnet format Sattleme.Bitcoin.sln --verify-no-changes --no-restore --exclude src/Embedded

echo "== Tests"
dotnet test tests/Sattleme.Bitcoin.Tests -c Release --no-build

echo "== The archive (the tests check it file by file)"
archive=$(ls artifacts/Release/Smartstore.Module.Sattleme.Bitcoin.*.zip)
python3 -I -m zipfile -l "$archive"
if python3 -I -m zipfile -l "$archive" | grep -E '(Sattleme\.Checkout|NBitcoin\.Secp256k1|Smartstore[^ ]*)\.dll'; then
  echo "the archive holds an assembly of the original names, or Smartstore's" >&2
  exit 1
fi

echo "== Brand"
scripts/brand.sh

echo "All checks passed."
