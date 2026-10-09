# Same-account UAC linked-token authentication

## Demonstrated cause

After the PR #14 offline upgrade correction, the affected Windows desktop
installed and launched normally. Real same-account Yes/No UAC consent created
both the privileged bootstrap and worker. Worker evidence then recorded:

```
stage=WorkerOwner check=AuthenticationId outcome=IdentityMismatch
code=-2147024891 linked=MatchesPeer linked_code=0
kind=AuthenticationRejected elapsed_ms=2113 helper_exit=1313867266 cleanup=complete
```

The previous policy rejected every differing AuthenticationId even after Windows
confirmed a linked logon identity. This specific equality-only rejection is the
confirmed cause. It precedes IPC authentication and Flowseal START. It is not a
15-second timeout, UAC denial or DPI strategy failure.

## Authorization correction

The ordinary path still requires exact user SID, Windows session and equal
AuthenticationId. Elevated worker authorization still requires actual token
Elevation=1 and enabled, non-deny-only Administrators group membership.

If AuthenticationIds differ, authorization reads fresh Windows token information
from the **actual process tokens held during this validation**:

1. Verify the same SID and session before any linked-token fallback.
2. Read elevation type/flag, token statistics and enabled administrator membership.
   For worker validating desktop, current must be Full/elevated/admin and peer
   Limited/not elevated/not enabled-admin. For desktop validating worker, reverse
   this direction. Default, Full/Full, Limited/Limited and unexpected types fail.
3. Call `GetTokenInformation(TokenLinkedToken)` on **both** held tokens. Close
   both returned handles. Query errors or invalid/missing results fail closed.
4. Match the current token's OS-linked SID, AuthenticationId and session to the
   exact observed peer logon identity, and the peer's OS-linked identity back to
   current. Require corresponding opposite elevation types/flags and administrator
   capability on both returned linked identities. A one-sided link cannot pass.
5. Re-read both source snapshots after the link queries to detect identity or
   capability changes. The public process validation accepts primary tokens only,
   then reopens each process token and compares its TokenId to the held object;
   replacement fails. The peer must still be alive before validation returns.
6. Keep image, creation time, protected application/hash/ACL checks, bootstrap
   direct-child ownership, elevated administrator check, exclusive local IPC ACL,
   challenge, ordered commands/replay rejection and owned cleanup unchanged.

Windows can duplicate primary tokens for child processes: `TokenId` identifies a
specific token object, whereas `AuthenticationId` identifies a logon session.
We therefore check TokenId for stability of each actual process token, not for
equality between a process-primary clone and an OS-linked canonical token. The
relationship binds the full observed SID/session/AuthenticationId plus UAC
elevation/capability in **both directions**. Query-only linked handles may be
primary or impersonation tokens; they are never assigned, duplicated for execution
or impersonated. Actual authorizing process tokens must be primary.

`BrokerIdentityPolicy` consumes internal typed live snapshots, never
`BrokerLinkedTokenStatus`, a diagnostic string, a journal, cached proof or IPC
input. The `linked=MatchesPeer` advisory record is not an authorization credential.
No SID, LUID, token handle/ID, account name or browsing payload is logged or stored.
The typed success mode `UacLinkedLogon` may be logged without identity values.
Worker evidence `LinkedIdentity / Authorized` records completion of its live check;
this evidence still does not authorize the desktop's independent verification.

The installer remains offline with reviewed archive/component hashes. PR #14's
migration of unknown old selection references is preserved. UI/application version
remains 0.7.0; NorthpassCore remains 0.4.0. Native v0.5 is not introduced.

## Tests and limits

Portable tests use **simulated typed token snapshots** to cover both valid
Full/Limited directions, equal logon acceptance without link queries, mismatching
SID/session, unrelated AuthenticationIds, one-sided/wrong link, elevation/admin
capability mismatches, changed source identity and link query failure. These
fixtures are not Windows token issuance or interactive UAC validation.

Windows tests use actual process/token APIs for process identity, image/creation
rejection, token-query access denial, linked-query access denial, unrelated worker
parent rejection and repeat validation. Where the runner actually supplies a
split-token pair, it tests both token-validation directions with administrator
checks. A default-token runner explicitly records NOT EXERCISED for that pair in
`engine-broker-token-coverage.txt`. No Windows policy is modified to manufacture
a UAC pair in CI.

Published-package CI retains actual medium-integrity restricted desktop to
pre-elevated bootstrap/worker handoff, replay, crash, parent-death, cancellation,
cleanup and reconnect tests. The Flowseal scenario explicitly asserts **two
received authenticated START requests**, readiness, STOP and restart using
`filter=false` with no traffic interception. Actual installer upgrade/repair and
uninstall regressions remain. Pre-elevated handoff is not interactive UAC consent
and its restricted medium token is not a genuine linked UAC limited token.

## Manual Windows 10/11 acceptance

Use a normal same-account split-token administrator on Windows 10 x64 and Windows
11 x64, starting Northpass unelevated through the standard shortcut. Record OS
build and installer SourceRevision. Keep UAC, Defender, Firewall, Secure Boot and
protected paths/ACLs enabled.

1. Install the new offline EXE over the previous Northpass installation. Confirm
   preparation completes without editing selection.json or downloading engines.
2. Click Connect and approve **Yes** on the actual secure-desktop UAC prompt.
   Confirm successful WorkerOwner verification, desktop-to-worker identity mode,
   challenge/authentication, EngineStart and readiness. When linked fallback is
   needed, expect worker `LinkedIdentity / Authorized` and desktop
   `BROKER_IDENTITY direction=DesktopToWorker mode=UacLinkedLogon`.
3. Confirm Flowseal received START (authenticated diagnostic
   `NORTHPASS_BROKER_START engine=zapret1`) and the owned winws child initialized.
   Website/media/voice effectiveness must be evaluated separately; process state
   and successful web probes are not proof of DPI bypass.
4. Disconnect and confirm all owned worker/engine children stop. Reconnect twice,
   including another reviewed Home strategy; no stale authentication error,
   duplicate privileged helper or orphan may remain.
5. On a fresh attempt deny UAC. Expect UacDenied/code 1223 and completed cleanup;
   approve on the next attempt and reconnect. Leave Diagnostics open 30 seconds:
   an unchanged error must not be repeated on every poll.
6. In a dedicated VM test alternate-account credential elevation. Different SID
   or unrelated logon must fail closed. Never widen permissions to make it pass.
7. In a dedicated VM terminate only the identified owned worker during startup
   and active operation, and close the desktop during startup. Expect reported
   failure/cancellation, bounded owned cleanup and successful explicit retry.
8. Test offline reinstall/repair/uninstall, preserving the existing upgrade fix.

Until this procedure is executed on the affected PC, restored interactive
connection is unverified. CI is not Windows 10/11 secure-desktop certification,
independent security review or proof of Russian ISP bypass. A subsequent failure
must retain its exact check/outcome/code rather than be bypassed.

References: [GetTokenInformation](https://learn.microsoft.com/windows/win32/api/securitybaseapi/nf-securitybaseapi-gettokeninformation),
[TOKEN_LINKED_TOKEN](https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-token_linked_token),
[TOKEN_STATISTICS](https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-token_statistics),
[TOKEN_ELEVATION_TYPE](https://learn.microsoft.com/windows/win32/api/winnt/ne-winnt-token_elevation_type).
