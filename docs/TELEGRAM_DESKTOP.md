# Telegram Desktop: direct MTProto bootstrap support

## Evidence and demonstrated gap

The affected Russian ISP tester reports PR #16 UAC/engine startup succeeds and
some reviewed strategies improve YouTube/Discord, but Telegram Desktop remains
Connecting with every strategy. No affected-PC transport trace or MTProto probe
result has been supplied. Routing failure, proxy misconfiguration, IPv6 failure,
endpoint churn and provider-specific DPI behavior remain possible causes. This
change corrects a demonstrated **matching gap**, not a proven complete ISP fix.

Official source inspected at telegramdesktop/tdesktop commit
`86262333a457f62709726ee4ab9c48fa58824da4`:
- `Telegram/SourceFiles/mtproto/mtproto_dc_options.cpp` supplies six production
  IPv4 and five IPv6 bootstrap addresses on 443; separate test-DC entries are
  explicitly excluded. Configuration subsequently supplies updated `dcOption`
  endpoints, including media/CDN, secrets and TCP-only options.
- `connection_tcp.cpp` uses direct-IP TCP, abridged framing and an obfuscated
  connection prefix with AES-CTR; proxy/secret transports choose other paths.
- `connection_http.cpp` uses an IP URL `/api` and forced port 80 for HTTP fallback.
- `mtproto_dc_options.cpp::lookup` and `session_private.cpp` choose address-family,
  media/static/TCP-only/proxy options. A configured SOCKS/MTProxy may target a
  completely different destination; Northpass must not read or overwrite secrets.

[Official transport documentation](https://core.telegram.org/mtproto/transports)
and [DC mechanism](https://core.telegram.org/api/datacenter) are reference URLs;
the documentation website returned HTTP403 in this cloud environment, so the
source findings above were verified directly from the official pinned Git tree.
No claim of having read inaccessible pages. `engine/telegram/bootstrap.json`
records exact factual endpoints, source path, revision, size and SHA-256;
`verify-telegram-catalog.py` checks them against the pinned official source in CI.

Before this change, all five global filters already captured public TCP80/443,
including direct MTProto traffic to those ports. That does NOT mean matching a
transformation: opaque MTProto supplies no HTTP Host/TLS SNI, while preceding
rules select YouTube/Discord hostlists or special voice ports/protocols.
`ipset-all.txt` is exactly TEST-NET `203.0.113.113/32`, not Telegram coverage.
An HTTPS check of web.telegram.org/telegram.org cannot validate Desktop messaging.
DNS is not involved in these compiled direct-IP bootstrap connections, though
proxy hostname resolution and HTTPS fallback/config acquisition can involve DNS.

## Isolated production correction

The original five global capture options and every original ordered rule remain
unchanged. An additional compiled first rule matches ONLY the eleven exact
reviewed production bootstrap destinations on TCP80/443. Both reviewed and custom
IP exclusions remain applied. First placement prevents a custom broad game/IP
rule from consuming opaque MTProto before this rule. It uses the existing reviewed
engine's `multisplit`, split position1, any-protocol=1, cutoff=d2 (only the first
data packet). There are no fake TLS/UDP contents, sequence overlaps, raw capture
arguments, BAT execution or arbitrary user configurations. Later payloads and
all other destinations retain their original behavior. UDP/calls and guessed
subnets/5222 are not added without evidence from official endpoint options.

Legacy saved profiles still validate against the original immutable templates;
the trusted runtime compiler inserts the extension. No migration discards users'
working YouTube/Discord strategy choices. Protected components, snapshot leases,
UAC/linked-token authorization, challenge/ACL/replay security, installer repair,
engine verification and Native0.4 remain intact.

Coverage is deliberately incomplete: Desktop may already have cached different
dynamic DC/media/CDN addresses. Neighboring addresses are NOT inferred as trusted.
Unknown endpoints are not covered; bootstrap success does not prove media or full
login. Updates require a new reviewed official source revision, exact endpoint
translation, source SHA-256, CI and a signed-off offline application build.
There is no automatic remote IP-set refresh, DNS expansion or permissive CIDR.

## Privacy-preserving opt-in diagnostics

Diagnostics -> Telegram Desktop -> Check direct connection requests consent before
sending eight bounded probes: DC1/DC2 sample, IPv4/IPv6, plain abridged and
obfuscated abridged TCP443. Each has a four-second deadline, cancellation and
closed owned socket. No automatic startup probes or cloud live Telegram test.
The request is an initial unencrypted `req_pq` with a random nonce, no account,
phone number, auth key, message or browsing contents. The reply parser checks
bounded canonical framing, constructor, nonce, pq/vector lengths. Buffers and
transport key material are disposed/zeroed. Obfuscation is NOT authentication.

A valid initial resPQ is an **unauthenticated initial protocol response**, not
proof of server authenticity, account login, messages, media or Russian DPI
bypass. Plain transport may fail while Desktop's obfuscated transport works, or
vice versa. HTTP fallback is included in matching but not probed by this button.
The diagnostic does not use/change Telegram's proxy or cached config. Results
retain only catalog DC ID, family, transport, stage, safe socket code, elapsed
time and catalog/configured-rule match. They never infer actual packet interception
or transformation from that configured predicate; those remain unknown. Capture
initialization is a separate historical status. Cancel, Disconnect and shutdown
cancel the probe worker; completion never depends on a blocked WPF dispatcher.

## Tests and evidence boundaries

Portable tests validate exact endpoint scope, rejection of neighboring/test DCs/
unreviewed ports, preservation of all five original strategy prefixes/order and
profile templates, exclusions, bounded nonce-bound response rejection, cancellation
and a synthetic protocol fixture. Such fixtures are NOT real Telegram servers.

`TelegramWindowsTests` loads the actual verified DLL and protected winws, then
uses a test-only filter confined to generated TCP source198.51.100.1:55002 and
one known bootstrap destination/control on443. A DROP guard is opened before
process startup/injection, so no synthetic packet can reach the NIC/public DC.
The real driver/engine must split the opaque first data packet into two ordered,
nonoverlapping segments, reconstruct fixed synthetic bytes exactly, and pass a
non-Telegram control unchanged. Only aggregate outcome evidence is written;
packet buffers are not persisted. Owned process and receive handles are closed.
This proves isolated engine rule application, not actual server/ISP connectivity.
Existing YouTube/Discord parser, true-capture UDP/loopback, broker and offline
upgrade/repair/uninstall regressions remain required. Windows Server CI is not
interactive desktop Windows10/11 acceptance.

## Manual affected-network acceptance: Windows10/11

1. Install this PR's new offline artifact. Keep all Windows protections enabled.
   Record only Windows/app version and reviewed strategy ID. Confirm working
   YouTube playback/seeking and Discord gateway/text/media/two-way voice before
   and after; stop if those regress. Do not overwrite working configurations.
2. In Telegram Desktop Settings -> Advanced -> Connection type record ONLY
   whether Default/System/SOCKS5/MTProxy is selected. Never export proxy secrets,
   login data or tdata. A direct bootstrap fix does not repair an unrelated proxy.
   With explicit tester consent compare the app's default/direct connection and
   its current proxy, restoring its preference afterward. Northpass changes none.
3. On the affected ISP compare Disconnect and Connect, closing/reopening Telegram
   to avoid stale established sessions. Approve genuine same-account UAC. Record
   startup/capture status separately. Run the new Diagnostics check only after
   consent; record DC IDs/families/transports/stages/codes/times, not raw traffic.
4. Compare IPv4/IPv6 results without changing global network settings. TCP failure
   on both runs may indicate routing/endpoint/proxy problems; timeout does not
   identify DPI by itself. Initial protocol success while Desktop fails suggests
   cached/dynamic endpoints, another transport/proxy or later account steps.
5. Success requires Telegram Desktop actually leaving Connecting, exchanging a
   tester-approved message in BOTH directions and loading a small test media
   object. Record those observations separately from automatic probes. Verify
   Disconnect/reconnect and repeated app restarts. No private content/screenshots
   of chats, packet dumps or account identifiers should be shared.
6. If still stuck, return bounded app diagnostics and this task matrix. Do not
   blindly expand IP ranges or alter all working strategies. Investigate selected
   DC/proxy/transport with explicitly authorized, privacy-reviewed tooling before
   extending coverage. No independent Russian ISP bypass claim until actual
   affected-connection send/receive has been demonstrated.
