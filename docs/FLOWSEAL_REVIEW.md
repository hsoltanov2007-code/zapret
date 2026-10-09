# Northpass Flowseal review (retained in v0.6)

Review date: 2026-10-08. This records provenance and selected strategy translation, not a complete security audit of upstream native code or proof of effectiveness on an ISP.

## Immutable input

Flowseal/zapret-discord-youtube **1.10.3**, Git commit `865da4f4c3659523bf79bc6edf0446e7d7969614`, is the only accepted Flowseal input. Its pinned codeload ZIP is 1,641,392 bytes, SHA-256 `972926e48df550da238981aa6a57ba260208acb92a96c9c9d796ea03e0ef86e6`. All 64 Git-tracked files were compared with that archive. The selected 18 component hashes/sizes and the deterministic 3,310,911-byte offline ZIP hash are committed in `engine/catalog/flowseal.json`. Hashes are not refreshed automatically.

`bin/winws.exe` matches byte-for-byte the x64 binary in bol-van/zapret's official **v72.9** release ZIP (published SHA-256 `2d8eef31e4966f32cd25b1ca361b0faa8f3c10cbaa514b8d0b6d82986784dfe7`). Its embedded version identifies source `c849e55ef0f1c244206f5a05ff7b1ab41a3824ee`, matching that tag. The reviewed corresponding source is separately pinned and bundled. The PE is x64 and imports Cygwin, WinDivert and Windows system libraries; its Cygwin Makefile statically links zlib 1.3.1 and does not use Zapret2 Lua options.

Flowseal's WinDivert.dll and signed WinDivert64.sys match the official **2.2.2-A x64** files byte-for-byte. Its cygwin1.dll matches the independently reviewed 3.4.10-1 runtime already used by the optional Zapret2 adapter. Full original terms, notices and corresponding Cygwin/WinDivert/Zapret/Flowseal sources accompany the installer. See THIRD_PARTY_NOTICES.md.

## Reviewed strategies

| Typed ID | Upstream input | Included rules |
| --- | --- | --- |
| general | general.bat | Starter YouTube/Discord TCP/QUIC and Discord/STUN UDP |
| alt | general (ALT).bat | Alternate split/fake rules, including repeated TLS fake inputs |
| alt2 | general (ALT2).bat | Alternate split placement and fake composition |
| simple-fake | general (SIMPLE FAKE).bat | Simpler fake strategy |
| fake-tls-auto | general (FAKE TLS AUTO).bat | Automatic TLS fake modification, preserving literal `!` |

Each has nine ordered rule blocks and eight `--new` separators. Reviewed UDP 443 QUIC rules, UDP 19294–19344 / 50000–50100 `discord,stun` rules with ACTIVE_DISCORD_UDP, Discord media TCP ports, google hostlists, repeated include/exclude hostlists/ipsets and fake asset paths remain in their original order. Metadata records each original BAT SHA-256. `scripts/verify-flowseal-catalog.py` statically compares every typed argument with those pinned bytes during packaging. It never executes the scripts.

The app executes only a fully verified PE using ArgumentList. `%BIN%`, `%LISTS%` and game variables become controlled typed paths/port inputs. No environment/shell expansion, BAT execution, service.bat or Lua loading occurs in this adapter. Profile JSON must match a compiled strategy's canonical arguments exactly. Customization is limited to validated port lists and data-only host/IP lists.

The upstream disabled game-filter setting is **12** for TCP and UDP, preserved as the default. Enabling broad ranges is a deliberate advanced input, not a hidden automatic change. Upstream placeholder userlists contain `domain.example.abc` and `203.0.113.113/32`; these nonempty defaults are needed for parser compatibility and are prepared internally. `ipset-all.txt` is also a placeholder in this pinned bundle; Northpass does not silently download a changing IP set. Advanced users can supply validated IP/CIDR data.

`service.bat` and its global mutations are deliberately excluded: no global TCP timestamp change, Windows service install/delete, external process termination, shared driver removal, hosts/DNS/Winsock change or security disablement. Strategies using timestamp fooling therefore do not inherit service.bat's global timestamp prerequisite; their provider effectiveness needs explicit testing. No upstream claim of universal coverage is adopted.

## Installation and lifecycle

A separate Zapret1 adapter implements IDpiEngine; the engine-neutral controller, installer contracts and future native adapter remain intact. New installs choose `flowseal-general`; v0.6 retains valid Flowseal profiles, and migrates unsupported old selections while preserving their files/preferences. Only the Flowseal payload is bundled; the old adapter/catalog/payload is removed. The module has a protected Program Files root and embedded catalog. Missing bundled bytes fail closed without a download fallback. Optional updates/rollback accept only reviewed embedded manifests; no previous Flowseal revision is invented.

Before capture, detect any external winws/winws2 process and instruct the user to close it through its own controls. No collision process is killed. Recheck immediately before launch; a cross-product process can still race this check, so the native driver's duplicate-filter checks stay enabled. Only Northpass's owned child is stopped.

User text lists are UTF-8 .txt only, bounded to 1 MiB/20,000 entries, with domain or IP/CIDR grammar and no commands, URLs, script files or executable imports. Engine snapshots are copied into ACL-protected per-session directories, opened with deny-write/delete handles until the child stops, and cleaned up afterwards. Bundled lists and hashes never change. This protects against ordinary-user modification, not administrators/kernel compromise. Abnormal app termination may leave inert protected session data; normal disconnect and exit remove owned snapshots, and no global driver is deleted.

## Evidence and limits

Portable tests use explicit fixtures for website outcomes and controller timing. Windows CI uses the actual reviewed PE for version, all five parser/filter-rendering probes and the WinDivert lifecycle with `filter=false` for ownership-only acceptance. A separate real-capture laboratory now checks dedicated loopback TCP/UDP delivery and one contained synthetic UDP reviewed rule behind a DROP guard. These are distinct scopes; only the latter captures packets. See [traffic-path audit](FLOWSEAL_TRAFFIC_DIAGNOSIS.md). The installed published app is checked for offline preparation, all 18 Flowseal components, reuse, missing-component rejection, restoration and actual silent uninstall. WPF checks render EN/RU/AZ and verify the main flow has no engine terminology. CI output must distinguish driver initialization from a security-policy block.

Guided testing stops the prior session, tests one manually selected configuration without recovery/automatic strategy swapping, records provider label, reviewed strategy/revision, engine state, stage results and bounded logs, then disconnects even on error/cancellation. Records are local, can contain hosts/paths/provider labels, and must be reviewed before sharing. A user can separately record their own playback/voice observation; those fields are never automatic proof.

YouTube/Discord/Telegram web checks use DNS, TCP 443, authenticated TLS and HTTP status on one resolved address; other IP families, streaming/login/WebSocket gateway, QUIC, STUN and two-way voice remain Unknown. A successful website probe or running engine does not imply a successful DPI bypass. See WINDOWS_ACCEPTANCE.md for real Russian ISP checks. Signing, real normal-user UAC, clean-machine behavior and ISP verification are release acceptance work, not implied by portable tests.
