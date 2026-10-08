#!/usr/bin/env python3
"""Check the compiled strategy translation against reviewed source bytes; never execute BAT."""
import hashlib
import json
import shlex


def verify_catalog(archive, prefix, root):
    document = json.loads((root / 'engine/flowseal/strategies.json').read_text())
    manifest = json.loads((root / 'engine/catalog/flowseal.json').read_text())
    if document['bundleRevision'] != manifest['revision'] or document['bundleVersion'] != '1.10.3':
        raise RuntimeError('Strategy catalog revision mismatch')
    selected = {c['sourcePath'] for c in manifest['components']}
    for strategy in document['strategies']:
        data = archive.read(prefix + strategy['sourceFile'])
        if hashlib.sha256(data).hexdigest() != strategy['sourceSha256']:
            raise RuntimeError('Reviewed strategy source was changed: ' + strategy['sourceFile'])
        text = data.decode('utf-8-sig')
        marker = '"%BIN%winws.exe"'
        if text.count(marker) != 1:
            raise RuntimeError('Strategy launch definition is ambiguous')
        command = text.split(marker, 1)[1].replace('^\r\n', ' ').replace('^\n', ' ').replace('^!', '!').strip()
        original = shlex.split(command, posix=True)
        user = {'general': 'list-general-user.txt', 'excluded-hosts': 'list-exclude-user.txt', 'excluded-ips': 'ipset-exclude-user.txt'}
        def render(option):
            value, kind = option['value'], option['kind']
            if kind == 'BinAsset':
                if 'bin/' + value not in selected: raise RuntimeError('Unbundled fake payload')
                value = '%BIN%' + value
            elif kind == 'BundleList':
                if 'lists/' + value not in selected: raise RuntimeError('Unbundled hostlist/ipset')
                value = '%LISTS%' + value
            elif kind == 'UserList': value = '%LISTS%' + user[value]
            elif kind == 'Ports': value = value.replace('{GAME_TCP}', '%GameFilterTCP%').replace('{GAME_UDP}', '%GameFilterUDP%')
            elif kind != 'Literal': raise RuntimeError('Unknown typed option kind')
            return option['name'] + '=' + value
        translated = [render(o) for o in strategy['global']]
        for index, rule in enumerate(strategy['rules']):
            if index: translated.append('--new')
            translated.extend(render(o) for o in rule)
        if translated != original:
            raise RuntimeError('Ordering, repetition or typed translation differs: ' + strategy['sourceFile'])
    print('Verified five typed strategies against pinned BAT bytes; no script was executed.')
