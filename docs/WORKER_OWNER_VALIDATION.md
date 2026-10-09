# WorkerOwner authentication evidence (PR #12 follow-up)

This records the PR #13 diagnostic release. The affected PC subsequently confirmed
`AuthenticationId / IdentityMismatch / linked=MatchesPeer`. See the current
[UAC linked-token correction](UAC_LINKED_TOKEN_AUTH.md) for the authorization fix;
the diagnostic-only equality policy described below is historical.

## Report and conclusion

The independent desktop report proves that standard Yes/No UAC consent completed,
the bootstrap passed ownership/integrity checks, and the managed worker started.
The worker rejected `WorkerOwner` after approximately 2.4 seconds with
`0x80070005` and owned exit `0x4e500602`; Flowseal START was never reached.
This is an early authorization rejection, not evidence of a 15-second timeout,
a strategy failure, a blocked website, or UAC denial.

The **exact rejection is not yet demonstrated on the affected PC**. The old
implementation manufactured `UnauthorizedAccessException`/`0x80070005` for
image mismatches, unavailable token queries, SID/logon/session mismatches and
other checks. That HRESULT did not identify the failing Windows API.

This change is a **fail-closed diagnostic release, not a claimed connection
fix**. Do not mark the real issue resolved without a new affected-PC result.
In particular, the prior comment that AuthenticationId is invariant across UAC
split tokens was unsupported. The policy still requires the same SID, session
and AuthenticationId. If the latter differs, the worker now queries the current
token's actual Windows `TokenLinkedToken` and reports whether its SID,
AuthenticationId and session match the peer. Even `linked=MatchesPeer` still
rejects the connection in this release. It is evidence for a separately reviewed
linked-logon authorization correction, not permission to ignore any mismatch.

## Exact checks and safe output

`BrokerSecurityException` carries a fixed check/outcome vocabulary and a numeric
code. All owner creation/image/token checks, peer elevation/groups and worker
parent/snapshot checks distinguish failed API calls from logical rejection.
The worker emits this before exiting and the desktop preserves it in the bounded
local failure journal and final diagnostic. An exited helper gets at most
500 ms of evidence draining before cleanup cancels the readers; cleanup ownership,
file leases and the eight-second termination bound remain intact.

Examples (illustrative, **not captured affected-PC results**):

```
BROKER_FAILURE kind=AuthenticationRejected stage=WorkerOwner ... check=PeerTokenOpen outcome=ApiFailure code=5 linked=NotChecked linked_code=0
BROKER_FAILURE kind=AuthenticationRejected stage=WorkerOwner ... check=AuthenticationId outcome=IdentityMismatch code=-2147024891 linked=MatchesPeer linked_code=0
```

- `ApiFailure`: `code` is the immediately captured Win32 error, e.g. 5.
- `IdentityMismatch`: an actual value comparison rejected the peer. The code is
  a synthetic managed authorization HRESULT, not a Windows API error.
- `InvalidData`: an API result failed structural/length validation.
- `ProcessExited`: the held process was already signaled.
- `linked=QueryFailed`: the optional diagnostic query failed; `linked_code` is
  separate from the original rejection. No authorization is granted.
- `linked=NotSplit`: the current token is `TokenElevationTypeDefault`.

No SID, LUID, session number, account name, executable path, nonce, token handle,
packet payload or raw exception text is logged. `NPB2` extends the numeric
advisory evidence schema; existing native bootstrap `NPB1` remains accepted.
Unknown fields/enums are rejected. The 32-record/4096-byte per-channel,
128-character line and 64-record/16-KiB local journal limits are unchanged.
Evidence may be unavailable after a crash; a generic stage/exit is then reported
without inventing a precise check. Evidence never authorizes the control pipe.

## API and structure review

- Open the peer once with `PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE`;
  creation, image and token queries use that held handle.
- `OpenProcessToken` uses only `TOKEN_QUERY` (0x8), including cross-integrity
  inspection. Never request all access or weaken a DACL to inspect a token.
- Query `TokenUser` directly, avoiding managed exceptions that lose the original
  query error. The old `WindowsIdentity(IntPtr)` uses `DuplicateHandle`, not
  `DuplicateTokenEx`; it was **not established as the desktop cause**.
- `TOKEN_STATISTICS` is 56 bytes; `AuthenticationId` is the second 8-byte LUID.
  Check returned lengths explicitly. Session and elevation queries are 4 bytes.
- Variable token buffers are capped at 64 KiB; group entries account for native
  alignment and each SID pointer/length is checked within the returned buffer.
- Linked-token handles are closed. Linked identity is diagnostic only.

References reviewed:
[OpenProcessToken](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-openprocesstoken),
[GetTokenInformation](https://learn.microsoft.com/windows/win32/api/securitybaseapi/nf-securitybaseapi-gettokeninformation),
[TOKEN_STATISTICS](https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-token_statistics),
[TOKEN_LINKED_TOKEN](https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-token_linked_token),
[.NET 8 WindowsIdentity implementation](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.Security.Principal.Windows/src/System/Security/Principal/WindowsIdentity.cs).

## Automated coverage and its limits

Portable tests exercise the actual identity comparison policy with synthetic
identities, including different user, logon and session, and prove a linked match
cannot bypass rejection. They also validate the closed evidence vocabulary.
Windows tests query real process/token objects, validate correct identity, reject
wrong creation/image values, and deliberately remove TOKEN_QUERY on a real token
handle to verify `PeerUserQuery / ApiFailure / 5` and successful retry.

The Windows linked-token test records whether the runner actually supplies a
split-token pair in `engine-broker-token-coverage.txt`. A default-token runner
records **NOT EXERCISED**, not interactive UAC success. The existing medium
launcher creates a restricted medium token; this is not a genuine UAC limited
linked token. Published-package tests retain actual medium/elevated handoff,
pre-authentication image rejection with the precise detail asserted, startup
cancellation/reconnect, worker crash, parent death, replay, driver and installer
coverage. Windows Server 2022 and the windows-latest runner run the new security
tests. Neither certifies Windows 10/11 secure-desktop behavior.

## Required manual Windows 10/11 acceptance

Keep UAC, Defender, Firewall, Secure Boot and protected installation ACLs enabled.
Use the new offline installer in Program Files, with an ordinary same-account
split-token administrator launching Northpass from its normal shortcut.

1. Record OS build and installer SourceRevision. Confirm the UI is medium
   integrity. Click Connect and approve **Yes** on real UAC (not Run as
   administrator before starting the UI and not the CI handoff).
2. Export the single failed attempt from Diagnostics, or the bounded
   `%LOCALAPPDATA%/Northpass/diagnostics/broker-startup.json`. Capture stage,
   check, outcome, code, linked status/code, elapsed time, owned helper exit and
   cleanup. Do not send tokens, credentials, account identifiers or packet dumps.
3. If `AuthenticationId / IdentityMismatch / MatchesPeer` is recorded, the
   strict equality policy has rejected a Windows-linked logon. Keep the failure
   fail-closed; request review of explicit linked-logon authorization. If an API
   failure is recorded instead, investigate that specific query/access error.
4. Leave Diagnostics open 30 seconds: unchanged polling must not repeat the
   failure. Confirm cleanup completes and no owned elevated worker survives;
   retry must launch a fresh attempt and preserve equally precise evidence.
5. On separate attempts deny UAC, approve after a delay, close during startup,
   and terminate only the owned worker in a test VM. Expect distinct denial,
   cancellation/termination, bounded cleanup and a recoverable next attempt.
6. Test a different-account credential elevation in a dedicated VM; it must
   reject the user identity. Wrong image/time are covered automatically; do not
   change protected files or ACLs on a production installation to force access.
7. If connection succeeds, verify ordered authentication/START and owned child
   lifecycle, then Disconnect/reconnect and installer repair/uninstall. Website
   probe success is separate from authentication and does not establish bypass.

Real Windows 10/11 approval/denial, genuine linked-token behavior where CI lacks
it, different-account interactive elevation and affected-PC resolution remain
manual requirements. No independent security review or ISP bypass is claimed.
