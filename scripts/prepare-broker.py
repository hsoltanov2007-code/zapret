#!/usr/bin/env python3
"""Pin the exact managed worker into a native bootstrap, then pin the whole helper into the UI."""
import argparse, hashlib, json
from pathlib import Path
parser=argparse.ArgumentParser();parser.add_argument('--worker',type=Path);parser.add_argument('--catalog',type=Path);args=parser.parse_args()
if args.worker:
    files=sorted(p for p in args.worker.iterdir() if p.is_file() and p.suffix!='.hpp')
    if not files or not (args.worker/'Northpass.Broker.Worker.exe').is_file():raise SystemExit('A published Windows worker is required')
    entries=[]
    for p in files:
        if any(c not in 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-' for c in p.name):raise SystemExit('Unsafe worker component name')
        entries.append(f'{{L"{p.name}","{hashlib.sha256(p.read_bytes()).hexdigest()}",{p.stat().st_size}}}')
    header=args.worker/'broker_components.hpp'
    header.write_text('#pragma once\nstruct BrokerComponent { const wchar_t* name; const char* sha256; unsigned long size; };\ninline constexpr BrokerComponent broker_components[] = {\n'+',\n'.join(entries)+'\n};\n')
if args.catalog:
    root=args.catalog
    files={p.relative_to(root.parent).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(root.rglob('*')) if p.is_file()}
    if 'broker/Northpass.Broker.exe' not in files:raise SystemExit('Native broker bootstrap is required')
    (root.parent.parent/'broker-manifest.json').write_text(json.dumps(files,indent=2)+'\n')
