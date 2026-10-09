# Offline installation over a previous Northpass build

## Evidence and reproduced defect

The reported Windows 11 installer failed during bundled component preparation.
Running the installed bootstrap with `--install` returned `1313867267`, or
`0x4e500603`: managed worker, legacy stage 6, `InvalidDataException`. The old
install path left the stage at `WorkerOwner`, even though it never performed
desktop owner authentication. This is not the earlier interactive UAC rejection
`0x4e500602`. The old code does not identify which data check failed.

A regression test reproduced a concrete upgrade defect: an engine selection from
an older app build is absent from the new build's embedded catalog. Normal
installation correctly rejects it, but explicit repair calls the same strict
selection reader and rejects it again before preparing the new reviewed bytes.
Native payload revisions are content-derived and can change between builds.
The affected Windows 11 PC subsequently reported saved current revision
`105e3ecb65b9404224635284ca6bd0a5a39a1740` and bundled payload SHA-256
`90a495ced6348dd8e673178604c2fbf5665d7fe2c7d679e1bf2c759d8eee1ced`.
The latter's first 40 characters identify the new native revision. This confirms
that the affected installation contains the stale selection which deterministically
blocks native preparation on the old implementation. Successful repair on that
PC still requires testing the corrected installer.

## Correction and boundaries

Only explicit repair may discard an unknown *reference* from a valid, bounded,
protected selection file. It does not authorize, run, inspect, remove or use the
unknown revision as a path or a rollback target. Root/file ACL and link checks,
the exclusive installation lock, embedded manifest, archive and component hashes,
and the installed-version probe remain mandatory.

Repair prepares the current reviewed payload and atomically changes selection
only after verification succeeds. Unknown prior revisions are excluded from the
new rollback pointer. A failed repair leaves the original selection intact.
Detection, ordinary installation and launch still reject unknown selections;
installer preparation already invokes explicit repair on invalid data.
Old engine trees and user preferences are retained.

The installer now distinguishes process launch failure from a nonzero helper
exit, and records/shows the numeric result. Worker installation stages are
`InstallationProtection=20`, `InstallFlowseal=21`, `InstallNative=22`; the native
bootstrap also retains integrity/creation stages in installer mode. These codes
are diagnostic only. Authentication, packet engines and Windows security settings
are unchanged.

## Validation

The new upgrade test failed on the old implementation with `InvalidDataException`
from `ReadSelection` inside `RepairAsync`. Regression coverage also verifies no
old executable probe, no network fallback for the provided offline payload,
preserved selection after corrupt payload rejection, retained old files, and
rejection of unknown rollback/path references as authorization.

Windows CI runs the actual offline installer against a protected selection that
references an uncatalogued prior-build fixture. The old directory is inert;
the test checks normal detection rejection, successful installer migration,
current payload verification, excluded rollback reference, untouched old tree,
and recorded helper exit. This is a synthetic prior-build state exercised by
the real installer, not the affected user's historical install or interactive UAC.

On the affected Windows 11 PC, install the corrected package over the existing
application and confirm preparation finishes. Then separately test Connect with
real UAC approval. The WorkerOwner authorization issue remains subject to its own
per-check diagnostics; successful installation does not establish its resolution.
See [WorkerOwner validation](WORKER_OWNER_VALIDATION.md).
