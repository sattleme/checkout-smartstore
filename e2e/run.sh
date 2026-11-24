#!/usr/bin/env bash
# The end-to-end test (README.md, "Development"): Smartstore's official image at a pinned digest,
# on SQLite (the lightest database Smartstore installs on), in a container of its own; the module
# installed from its package, with the test-only fake of the SDK (e2e/FakeSdk, never shipped); then
# e2e/drive.py does what a merchant and a customer do, and e2e/database.py reads the shop's database.
# Needs Docker, .NET and python3; Smartstore's assemblies for the build (scripts/smartstore.sh, or
# $SMARTSTORE_DIR).
#
#   e2e/run.sh                builds the packages (Release), runs, removes its container
#   SKIP_BUILD=1 e2e/run.sh   uses the packages already in artifacts/Release/
#   E2E_KEEP=1 e2e/run.sh     keeps the container of a failed run, to look at it
#
# It never touches containers, volumes or images that are not its own; the image it pulls is left
# for the next run (removed by the caller when done: docker image rm with the name below).
set -euo pipefail

SMARTSTORE_IMAGE=ghcr.io/smartstore/smartstore-linux:6.4.0@sha256:fc91046eacc2371c109023447dea76f6d6639214982f2e4164106e9ed53cc350

root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root"
export SMARTSTORE_DIR=${SMARTSTORE_DIR:-$root/.smartstore}

if [ "${SKIP_BUILD:-}" != "1" ]; then
  scripts/smartstore.sh "$SMARTSTORE_DIR"
  dotnet build src/Sattleme.Bitcoin -c Release
  dotnet build e2e/FakeSdk -c Release
fi
module=$(ls artifacts/Release/Smartstore.Module.Sattleme.Bitcoin.*.zip)
fake=artifacts/Release/Smartstore.Module.Sattleme.E2e.1.0.0.zip
test -f "$module" && test -f "$fake"

shop="sattleme-smartstore-e2e-$(date +%s)-$$"
work=$(mktemp -d "${TMPDIR:-/tmp}/sattleme-smartstore-e2e.XXXXXX")

cleanup() {
  status=$?
  if [ "$status" -ne 0 ]; then
    echo "[e2e] Smartstore's output (last lines):" >&2
    docker logs --tail 100 "$shop" >&2 2>&1 || true
  fi
  if [ "$status" -ne 0 ] && [ "${E2E_KEEP:-}" = "1" ]; then
    echo "[e2e] kept: the shop $shop at http://127.0.0.1:${port:-?}/; remove: docker rm -f -v $shop" >&2
  else
    docker rm -f -v "$shop" >/dev/null 2>&1 || true
  fi
  rm -rf "$work"
  exit "$status"
}
trap cleanup EXIT

# Smartstore stops itself to restart (after its installation, after modules are uploaded or
# installed): Docker starts it again, on the same port (a port Docker chose would change).
port=$(python3 -I -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()')
docker run -d --name "$shop" --restart unless-stopped -p "127.0.0.1:$port:80" "$SMARTSTORE_IMAGE" >/dev/null

python3 -I e2e/drive.py --base "http://127.0.0.1:$port" --host localhost --module "$module" --fake "$fake"

# The shop's database, as Smartstore left it (SQLite, with its write-ahead log).
docker stop "$shop" >/dev/null
for file in Smartstore.db Smartstore.db-wal Smartstore.db-shm; do
  docker cp "$shop:/app/App_Data/Tenants/Default/$file" "$work/" 2>/dev/null || true
done
python3 -I e2e/database.py "$work/Smartstore.db"
echo "[e2e] all good"
