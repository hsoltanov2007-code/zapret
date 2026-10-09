#!/usr/bin/env python3
"""Create a native offline payload and immutable manifest BEFORE managed compilation."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parent.parent


def package(executable, sdk, output):
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    if not executable.is_file() or executable.read_bytes()[:2] != b"MZ":
        raise RuntimeError("A built Windows PE is required; cannot package a Linux test binary")
    pinned = json.loads((ROOT / "native/windivert.json").read_text())
    files = [("bin/NorthpassCore.exe", executable)] + [(c["path"], sdk / c["path"]) for c in pinned["components"] if not c["path"].startswith("include/")]
    for component in pinned["components"]:
        path = sdk / component["path"]
        if path.stat().st_size != component["size"] or hashlib.sha256(path.read_bytes()).hexdigest() != component["sha256"]:
            raise RuntimeError("WinDivert SDK changed before packaging")
    output.mkdir(parents=True, exist_ok=True)
    payload = output / "native-offline.zip"
    components = []
    with zipfile.ZipFile(payload, "w", compression=zipfile.ZIP_STORED) as archive:
        for name, path in files:
            data = path.read_bytes()
            entry = zipfile.ZipInfo(name, (2026, 10, 8, 0, 0, 0)); entry.create_system = 3; entry.external_attr = 0o100644 << 16
            archive.writestr(entry, data)
            components.append(dict(sourcePath=name, path=name, size=len(data), sha256=hashlib.sha256(data).hexdigest()))
    digest = hashlib.sha256(payload.read_bytes()).hexdigest()
    manifest = dict(engineId="native", revision=digest[:40], version="0.3.0", executable="bin/NorthpassCore.exe",
                    acquisitionKind="OfflineBuild", sourceRevision=revision, archiveUrl="", archivePrefix="",
                    archiveSha256=digest, archiveSize=payload.stat().st_size, offlineSha256=digest,
                    offlineSize=payload.stat().st_size, components=components)
    (output / "native-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    shutil.copytree(sdk / "source", output / "source", dirs_exist_ok=True)
    # Original NorthpassCore source and build metadata are supplied for audit/rebuild.
    with zipfile.ZipFile(output / "source/NorthpassCore-source.zip", "w", compression=zipfile.ZIP_DEFLATED) as archive:
        tracked = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT).decode().split("\0")
        roots = ("native/", "src/", "tests/", "scripts/", "installer/", "profiles/", "engine/", "branding/", "docs/")
        metadata = {"Northpass.sln", "Directory.Build.props", "global.json", "NuGet.Config", "NuGet.config", "README.md", "THIRD_PARTY_NOTICES.md", "CHANGELOG.md"}
        for name in sorted(n for n in tracked if n and (n.startswith(roots) or n in metadata)):
            archive.write(ROOT / name, name)
        archive.writestr("BUILD-REVISION.txt", revision + "\n")
    print(f"Native 0.3.0 offline payload: {len(components)} components; immutable manifest ready for embedding.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path, required=True)
    parser.add_argument("--sdk", type=Path, default=ROOT / "dist/native-sdk")
    parser.add_argument("--output", type=Path, default=ROOT / "dist/native")
    args = parser.parse_args()
    package(args.executable, args.sdk, args.output)
