#!/usr/bin/env python3
"""Verify factual bootstrap addresses against pinned official source. No runtime update."""
import argparse, hashlib, ipaddress, json, re, urllib.request
from pathlib import Path
ROOT = Path(__file__).resolve().parent.parent

def verify(data):
    manifest = json.loads((ROOT/'engine/telegram/bootstrap.json').read_text())
    if len(data) != manifest['sourceSize'] or hashlib.sha256(data).hexdigest() != manifest['sourceSha256']:
        raise RuntimeError('Official source pin/hash failed')
    text=data.decode('utf8'); entries=[]
    for name in ('kBuiltInDcs','kBuiltInDcsIPv6'):
        block=re.search(r'const BuiltInDc '+name+r'\[\] = \{(.*?)\n\};',text,re.S).group(1)
        entries += [dict(dc=int(dc),address=ip,port=int(port)) for dc,ip,port in re.findall(r'\{\s*(\d+),\s*"([^"]+)"\s*,\s*(\d+)\s*\}',block)]
    if entries != manifest['endpoints'] or len(entries)!=11 or any(e['port']!=443 for e in entries):
        raise RuntimeError('Bootstrap translation differs from reviewed official facts')
    for e in entries: ipaddress.ip_address(e['address'])
    print('Verified 11 exact production bootstrap addresses against official Telegram Desktop source; no ranges/test DCs/remote runtime updates.')

if __name__ == '__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path);args=parser.parse_args()
    m=json.loads((ROOT/'engine/telegram/bootstrap.json').read_text())
    if args.source: data=args.source.read_bytes()
    else:
        url=f"https://raw.githubusercontent.com/telegramdesktop/tdesktop/{m['sourceRevision']}/{m['sourcePath']}"
        with urllib.request.urlopen(url,timeout=30) as response:
            if response.url!=url: raise RuntimeError('Unexpected source redirect')
            data=response.read(m['sourceSize']+1)
    verify(data)
