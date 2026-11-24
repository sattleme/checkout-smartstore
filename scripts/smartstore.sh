#!/usr/bin/env bash
# Smartstore's assemblies the module is built and tested against: those of its release 6.4.0
# (github.com/smartstore/Smartstore, tag 6.4.0, commit fc75d48139743a2502ec654e35f42f1e3d0ac0ba),
# the archive its vendor publishes for Linux (Smartstore.Community.6.4.0.linux-x64.zip), checked
# against its SHA-256. Only the application's own assemblies and its packages' are kept (never
# .NET's runtime, nor the application's files, views or modules): the module references them,
# never ships them. Nothing of Smartstore is kept in this repository.
#
#   scripts/smartstore.sh [<dir>]
#
# The assemblies go to <dir>, $SMARTSTORE_DIR, or .smartstore/ in this repository.
set -euo pipefail

version=6.4.0
url="https://github.com/smartstore/Smartstore/releases/download/$version/Smartstore.Community.$version.linux-x64.zip"
sha256=3f822a9d65a6255afe03dabefd1e567ed1e9abe68c63f66cbb41a6f03e7ae6ae

root=$(cd "$(dirname "$0")/.." && pwd)
dir=${1:-${SMARTSTORE_DIR:-$root/.smartstore}}
mkdir -p "$dir"
dir=$(cd "$dir" && pwd)

if [ -f "$dir/.complete" ] && [ "$(cat "$dir/.complete")" = "$sha256" ]; then
  echo "Smartstore $version: $dir"
  exit 0
fi

# The archive is downloaded data: into a new empty directory of its own, read by a script kept in
# this repository, never run.
download=$(mktemp -d "${TMPDIR:-/tmp}/smartstore-download.XXXXXX")
trap 'rm -rf "$download"' EXIT
curl --proto '=https' --tlsv1.2 --fail --silent --show-error --location --retry 3 \
  --output "$download/smartstore.zip" "$url"
echo "$sha256  $download/smartstore.zip" | sha256sum --check --strict --quiet

find "$dir" -mindepth 1 -delete
python3 -I "$root/scripts/smartstore_extract.py" "$download/smartstore.zip" "$dir"
echo "$sha256" > "$dir/.complete"
echo "Smartstore $version: $dir"
