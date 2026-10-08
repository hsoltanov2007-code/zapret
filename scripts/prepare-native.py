#!/usr/bin/env python3
"""Acquire the fixed official WinDivert SDK; never discover latest assets or execute upstream code."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path, PurePosixPath
import shutil
import ssl
import stat
import urllib.parse
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent.parent


def checked(data, size, digest):
    if len(data) != size or hashlib.sha256(data).hexdigest() != digest:
        raise RuntimeError("WinDivert trusted integrity check failed")


class PinnedRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        uri = urllib.parse.urlsplit(newurl)
        if uri.scheme != "https" or uri.hostname not in ("github.com", "release-assets.githubusercontent.com") or uri.username:
            raise RuntimeError("WinDivert acquisition redirected outside official HTTPS asset hosts")
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def dependency(output, cache, supplied=None):
    manifest = json.loads((ROOT / "native/windivert.json").read_text())
    archive = Path(supplied) if supplied else cache / (manifest["archiveSha256"] + ".zip")
    if not archive.exists():
        archive.parent.mkdir(parents=True, exist_ok=True)
        partial = archive.with_suffix(".partial")
        try:
            opener = urllib.request.build_opener(PinnedRedirect(), urllib.request.HTTPSHandler(context=ssl.create_default_context()))
            with opener.open(manifest["archiveUrl"], timeout=60) as response, partial.open("wb") as stream:
                total = 0
                while block := response.read(65536):
                    total += len(block)
                    if total > manifest["archiveSize"]:
                        raise RuntimeError("WinDivert SDK exceeds reviewed archive size")
                    stream.write(block)
            checked(partial.read_bytes(), manifest["archiveSize"], manifest["archiveSha256"])
            partial.replace(archive)
        finally:
            partial.unlink(missing_ok=True)
    checked(archive.read_bytes(), manifest["archiveSize"], manifest["archiveSha256"])
    with zipfile.ZipFile(archive) as source:
        seen = set()
        for entry in source.infolist():
            path = PurePosixPath(entry.filename)
            if entry.filename.casefold() in seen or path.is_absolute() or ".." in path.parts or "\\" in entry.filename or ":" in entry.filename or stat.S_ISLNK(entry.external_attr >> 16):
                raise RuntimeError("Unsafe WinDivert archive path")
            seen.add(entry.filename.casefold())
        for component in manifest["components"]:
            data = source.read(manifest["archivePrefix"] + component["sourcePath"])
            checked(data, component["size"], component["sha256"])
            destination = output / component["path"]
            destination.parent.mkdir(parents=True, exist_ok=True)
            if destination.is_symlink():
                raise RuntimeError("Untrusted SDK output symlink")
            destination.write_bytes(data)
    # Full corresponding source accompanies every native binary distribution.
    spec = importlib.util.spec_from_file_location("reviewed_acquisition", ROOT / "scripts/prepare-engine.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    reference = next(s for s in json.loads((ROOT / "engine/catalog/redistribution-sources.json").read_text()) if s["name"] == "windivert-2.2.2-source.zip")
    archive = module.acquire(reference["url"], reference["size"], reference["sha256"], cache)
    (output / "source").mkdir(parents=True, exist_ok=True)
    shutil.copyfile(archive, output / "source" / reference["name"])
    print("Verified fixed WinDivert 2.2.2 SDK, x64 runtime, licences and corresponding source.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dependency-only", action="store_true", required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "dist/native-sdk")
    parser.add_argument("--cache", type=Path, default=ROOT / "dist/acquisition-cache")
    parser.add_argument("--archive", type=Path)
    args = parser.parse_args()
    dependency(args.output, args.cache, args.archive)
