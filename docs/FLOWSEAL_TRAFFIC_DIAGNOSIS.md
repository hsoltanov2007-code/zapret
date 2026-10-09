# Production capture and service-access diagnosis

The affected PR #15 desktop successfully passed genuine same-account interactive
UAC. Its engine runs, but all five reviewed strategies failed to restore service
access. That report proves startup is fixed on that PC, **not** packet interception
or DPI bypass. No packet evidence from the affected connection is available yet.
The ISP-specific cause remains undetermined.

## Audited production path

Home Connect -> BrokerEngine -> authenticated broker START -> EngineController ->
Zapret1Engine -> FlowsealCatalog.Compile -> protected/hash-verified winws v72.9 ->
WinDivert. START accepts only five catalog strategy IDs and bounded typed ports /
data lists. No BAT executes and arbitrary filters/debug arguments cannot cross IPC.
The production bootstrap never adds `--test-no-traffic`; only explicit acceptance
tests use that option. Production Compile emits real `--wf-tcp` / `--wf-udp`.
`--wf-raw=false` is confined to parser/ownership acceptance tests, not consumers.

Catalog bundle remains Flowseal 1.10.3 / 865da4f, engine c849e55 / v72.9. The
build-time verifier compares all ordered typed definitions to their pinned BAT
sources. Installation checks every bundled list and binary; immutable protected
session snapshots are leased for the child lifetime. No upstream refresh or
arbitrary strategy replacement is made in this investigation.

All five strategies have nine ordered rules. Global TCP covers 80,443,
2053,2083,2087,2096,8443 plus configured game ports; UDP covers 443,
19294-19344,50000-50100 plus configured game ports. Upstream defaults to both IPv4
and IPv6. The ordinary capture filter excludes loopback, impostors and local
ranges. Early UDP443 hostlist rule, Discord/STUN voice rule, discord.media
alternate TCP ports, Google TCP443, general TCP80/443, then IP-set/game rules
remain in upstream order. Ordinary game-port default is 12 (upstream disabled
convention). No rule selection is inferred merely from process liveness.

| Traffic | Reviewed coverage | Limits |
| --- | --- | --- |
| YouTube | list-google.txt includes youtube.com, googlevideo.com, ytimg.com, youtubei.googleapis.com and related endpoints; TCP443 and UDP443 QUIC rules | HTTPS homepage probe doesn't test playback/CDN/QUIC; ECH, address-family or provider changes require affected-PC checks |
| Discord web/gateway/media | list-general.txt includes discord.com, discord.gg, discordapp.com/net, discord.media and related domains (subdomains match upstream hostlist semantics); TLS443 plus media ports | gateway/app traffic is not proven by homepage HTTPS |
| Discord voice | UDP19294-19344/50000-50100, `discord,stun` L7 and reviewed fake assets | actual voice endpoints/protocol/ports must match; a call is the acceptance check |
| Telegram | no Telegram domains in the reviewed lists; bundled ipset-all.txt is the TEST-NET placeholder 203.0.113.113/32 | no demonstrated Telegram MTProto/media coverage. Telegram HTTPS card is a reachability check, never a support guarantee |

Bundled exclusions include private/link-local/loopback/multicast/CGNAT IPv4 and
local IPv6 ranges, and a small unrelated domain list. Inbound default capture is
selective TCP, not all inbound UDP. This preserves upstream behavior. The bundled
IP-set is deliberately **not** a worldwide/Telegram IP range. Custom all-ips
replaces only the designated IP-set; exclusions are combined with reviewed ones.
There is no evidence justifying arbitrary broad interception or new Telegram
modification rules here.

## Confirmed integration defects corrected

1. Active previously followed a 300ms process-survival delay. Flowseal now waits
   at most 15s for the pinned executable's exact stdout capture-initialization
   message. Missing readiness, early exit and cancellation clean up the owned
   child. This confirms an opened filter at startup, not a received packet.
2. Reported logical-network loss, receive failure or reinjection failure revoke
   Active in polled status while retaining process ownership for Disconnect.
   A later genuine reinitialization can restore startup evidence. Upstream
   without-debug logging cannot report every drop, too-large packet or kernel
   loss; silence never proves lossless capture or continued functional capture.
3. Diagnostics now separate process state, capture initialization, packet
   interception, matching, transformation and user-observed access. The latter
   packet stages are explicitly unconfirmed in consumer sessions because this
   reviewed executable exposes no privacy-safe aggregate counters. No debug
   packet logging is enabled, no counters are fabricated. Export includes only
   strategy ID, capture state and configured port sets for this new evidence.
4. Upstream's error-577 advice to disable Secure Boot is filtered from live logs;
   the original driver failure remains visible. Windows protections stay enabled.

## Windows regression laboratory

`FlowsealCaptureWindowsTests` loads only the verified, leased absolute WinDivert
DLL. The executable receives a **test-only** exact filter for dedicated loopback
TCP/UDP ports and one documentation-address synthetic UDP scope. This override
cannot be requested through broker IPC. All nine production reviewed rules and
protected lists remain compiled. A REFLECT handle checks the actual owned winws
NETWORK handle. A lower-priority sniff counts only protocol packets and discards
buffers. Real socket exchanges check bidirectional fixed-pattern reconstruction.
Upstream intentionally passes loopback unchanged, so this part tests capture and
reinjection, not DPI transformations.

A second test injects one synthetic documentation-address UDP packet through the
actual driver and activates the existing reviewed final any-protocol UDP rule
using a validated dedicated IP-set. A lower-priority DROP guard is established
**before** starting winws or injecting anything; a sniff before that guard counts
extra original/fake packets. The scope cannot reach the NIC. No raw traces,
private traffic, credentials, domains visited or packet payloads are persisted.
Counts are only laboratory measurements. A 30s overall deadline, child-tree
termination and handle shutdown bound the test. Driver policy blocks fail CI;
the test never silently substitutes `false` or mocks packet interception.

This tests actual driver delivery/reinjection and one reviewed UDP rule. It does
not prove that production public-internet filters match a particular ISP's
TLS/QUIC/STUN flows. Windows Server CI is not a Windows 10/11 consumer desktop.
Native TLS laboratory results remain separate and do not validate Flowseal TLS.
See the current PR's CI and `engine-flowseal-real-capture.txt` for executed results.

## Manual Russian ISP acceptance (Windows 10 / 11)

1. Install the current offline artifact, retaining Defender/Firewall/Secure Boot/
   UAC. Launch as a normal desktop user. Record app version, Windows version,
   strategy ID, IPv4/IPv6 availability and whether another packet tool is running.
   Do not send account identifiers, IP addresses or browsing logs.
2. Before Connect check the same fixed tasks: YouTube video starts and seeks;
   Discord app gateway connects, text/media loads and an authorized voice call
   carries audio both ways; Telegram app messages and a public test media file
   load. Record only task results and elapsed times. Web cards are separate HTTPS
   results, not these application checks.
3. Click Connect; approve standard same-account UAC. Diagnostics should show
   startup initialization confirmed and packet/match/transformation unknown.
   Export bounded app diagnostics and verify `NORTHPASS_BROKER_START engine=zapret1`
   plus `FLOWSEAL_CONFIG` shows production ports, not test-disabled. Driver errors
   or lost capture must result in attention/error, not an unconditional Active.
4. Repeat the same tasks after fully closing/reopening target apps to avoid
   already-established connections. Record each strategy independently; stop
   before changing it. Compare IPv4/IPv6 and QUIC/TCP behavior in an authorized
   controlled endpoint test, rather than changing global Windows network settings.
5. Disconnect, verify owned child ends, reconnect, and repeat one task. Confirm
   no orphaned children or repeated unchanged error floods. Preserve UAC denial /
   cleanup regressions from the prior acceptance document.
6. If every strategy still fails, retain only safe diagnostics and this task
   matrix. Public-service packet matching/transformation needs a separately
   reviewed privacy-safe upstream metric interface or an explicitly authorized
   isolated endpoint laboratory. Do not enable verbose packet dumps on private
   internet traffic. Do not report Russian DPI bypass until real affected-network
   access has been demonstrated independently.
