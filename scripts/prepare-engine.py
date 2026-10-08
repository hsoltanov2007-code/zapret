#!/usr/bin/env python3
"""Acquire only pinned reviewed bytes. Does not refresh hashes or execute upstream scripts."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import ssl
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent.parent


def checked(path, size, digest):
    if path.stat().st_size != size or hashlib.sha256(path.read_bytes()).hexdigest() != digest:
        raise RuntimeError(f"Trusted integrity check failed: {path.name}")


def acquire(url, size, digest, cache, supplied=None):
    if not url.startswith("https://codeload.github.com/"):
        raise RuntimeError("Only reviewed HTTPS Git source archives are accepted")
    path = Path(supplied) if supplied else cache / (digest + ".zip")
    if not path.exists():
        path.parent.mkdir(parents=True, exist_ok=True)
        partial = path.with_suffix(".partial")
        try:
            with urllib.request.urlopen(url, context=ssl.create_default_context(), timeout=90) as response, partial.open("wb") as output:
                if response.url != url:
                    raise RuntimeError("Pinned archive redirected to another URL")
                total = 0
                while block := response.read(65536):
                    total += len(block)
                    if total > size:
                        raise RuntimeError("Archive exceeds its reviewed size")
                    output.write(block)
            checked(partial, size, digest)
            partial.replace(path)
        finally:
            partial.unlink(missing_ok=True)
    checked(path, size, digest)
    return path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "dist/Northpass")
    parser.add_argument("--cache", type=Path, default=ROOT / "dist/acquisition-cache")
    parser.add_argument("--bundle-archive", type=Path)
    args = parser.parse_args()
    manifest = json.loads((ROOT / "engine/catalog/zapret2.json").read_text())
    bundle = acquire(manifest["archiveUrl"], manifest["archiveSize"], manifest["archiveSha256"], args.cache, args.bundle_archive)
    payload_dir = args.output / "engine-payload"
    payload_dir.mkdir(parents=True, exist_ok=True)
    payload = payload_dir / "zapret2-offline.zip"
    temporary = payload.with_suffix(".partial")
    try:
        with zipfile.ZipFile(bundle) as source, zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_STORED) as target:
            names = source.namelist()
            if len(names) != len(set(n.casefold() for n in names)):
                raise RuntimeError("Duplicate archive paths")
            for component in manifest["components"]:
                data = source.read(manifest["archivePrefix"] + component["sourcePath"])
                if len(data) != component["size"] or hashlib.sha256(data).hexdigest() != component["sha256"]:
                    raise RuntimeError(f"Untrusted component: {component['path']}")
                entry = zipfile.ZipInfo(component["sourcePath"], (2026, 9, 18, 0, 0, 0))
                entry.create_system = 3
                entry.external_attr = 0o100644 << 16
                target.writestr(entry, data)
        checked(temporary, manifest["offlineSize"], manifest["offlineSha256"])
        temporary.replace(payload)
    finally:
        temporary.unlink(missing_ok=True)
    # Supply corresponding source with the offline binary distribution, not a written offer.
    sources = json.loads((ROOT / "engine/catalog/redistribution-sources.json").read_text())
    source_dir = args.output / "docs/third-party-source"
    source_dir.mkdir(parents=True, exist_ok=True)
    for source in sources:
        archive = acquire(source["url"], source["size"], source["sha256"], args.cache)
        shutil.copyfile(archive, source_dir / source["name"])
    shutil.copytree(ROOT / "docs/licenses", args.output / "docs/licenses", dirs_exist_ok=True)
    shutil.copyfile(ROOT / "THIRD_PARTY_NOTICES.md", args.output / "THIRD_PARTY_NOTICES.md")
    print(f"Verified offline payload: {len(manifest['components'])} x64 components; pinned revision {manifest['revision']}")
    print("Corresponding third-party source archives and full notices are included.")


if __name__ == "__main__":
    main()
