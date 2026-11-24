"""Extracts, from Smartstore's release archive, the assemblies the module is built and tested
against (scripts/smartstore.sh): every assembly at the archive's root that is not a file of .NET's
runtime (the runtime packs Smartstore.Web.deps.json names), and SQLite's native library, which the
tests' database uses. The archive is data: nothing in it is run, and no path of it is trusted.

    python3 -I scripts/smartstore_extract.py <archive> <directory>
"""

import json
import os
import sys
import zipfile


def main(archive_path, target):
    with zipfile.ZipFile(archive_path) as archive:
        deps = json.loads(archive.read("Smartstore.Web.deps.json").decode("utf-8-sig"))
        libraries = deps["libraries"]
        runtime = set()
        for key, library in deps["targets"][deps["runtimeTarget"]["name"]].items():
            if libraries.get(key, {}).get("type") == "runtimepack":
                for section in ("runtime", "native"):
                    runtime.update(path.rsplit("/", 1)[-1] for path in library.get(section, {}))

        count = 0
        for entry in archive.infolist():
            name = entry.filename
            if "/" in name or "\\" in name or name.startswith(".") or entry.is_dir():
                continue
            wanted = (name.endswith(".dll") and name not in runtime) or name == "libe_sqlite3.so"
            if not wanted:
                continue
            with archive.open(entry) as source, open(os.path.join(target, name), "xb") as copy:
                while True:
                    chunk = source.read(1 << 20)
                    if not chunk:
                        break
                    copy.write(chunk)
            count += 1
        if count == 0 or not os.path.exists(os.path.join(target, "Smartstore.Core.dll")):
            raise SystemExit("not Smartstore's release archive")
        print(f"{count} assemblies of Smartstore's release")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    main(sys.argv[1], sys.argv[2])
