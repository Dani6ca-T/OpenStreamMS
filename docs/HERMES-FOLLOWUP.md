# Hermes follow-up — 2026-11-04

Verdict: **READY FOR DRAFT PR**, with the explicit integration limits below.
This is not approval to merge, deploy to RYZEN-PC or claim successful streaming.

Reviewed baseline: `810359b`, `0db7246`, `2fc5808`, upstream `e3392c0`.
The supplied Hermes review is static analysis, not Windows runtime evidence.
Its source text SHA-256 is
`84432142f7fb27f23c06c1bd53fc1c6a0d5f0321e8e349add0c68c9571c58443`.
Every H/M/L finding is accounted for; suggested changes were not applied blindly.

## Disposition of all 17 findings

| ID | Classification | Code evidence and disposition |
|---|---|---|
| H1 | FALSE POSITIVE | `StartupSafetyPolicy.Read()` calls `ReadClient()` on every client call; `_clientRuntime` selects a branch, not cached data. Same-process tests change committed policy Normal/Safe and observe both. The SCM snapshot is deliberately immutable until restart; documentation now makes both semantics explicit. No Refresh API is needed. |
| H2 | BY DESIGN | Runtime status is written under SYSTEM/Admin ACL; Users may read it but cannot forge it under the stated threat model. PID and creation time reject stale status, not a hostile local administrator. Private policy/runtime are not an OS sandbox. Real NTFS tests demonstrate denial of policy writes and ACL changes to the unelevated token. No speculative PID-collision redesign was added. |
| H3 | CONFIRMED | The Safe `EnsureInstalled()` skip lacked a diagnostic. Added a warning before returning, without probing/installing a missing driver. The review's claim that `--install` can proceed in Safe is false: the CLI guard rejects it before dispatch. Existing absent-driver tests verify zero probes. Warnings still respect the configured log threshold. |
| M1 | FALSE POSITIVE | `SeatHostControl.StartClient()` already starts with `RequireNormal`, before CTS/Task.Run. Added a direct entry-point regression; the Tray condition is not the sole protection. |
| M2 | FALSE POSITIVE | `diagnostic` is an `Action`; `GetStatus()` returns synchronously. Discarding its result with `_ =` does not make it asynchronous. A fresh-process blocking diagnostic test proves initialization is not signalled before return. The existing failure test proves the finally block releases waiters. No async architecture change was made. |
| M3 | FALSE POSITIVE | Actual Windows/NTFS tests replace a deliberately permissive destination with the protected temporary file and inspect the resulting DACL. SYSTEM/Admin-only ACLs remain after the overwrite and repeated writes. Adding post-publication protection is unnecessary and would not improve pre-publication guarantees. Sharing failures are tested as failures, not successful commits. |
| M4 | BY DESIGN | Revision zero denotes no readable valid revision; it is not an authorization token. Anchor-only state is Safe, startup cannot activate Normal, unconfirmed Normal is rejected, and even explicitly confirmed recovery stays pending until startup. Added tests and documentation rather than an arbitrary sentinel that would not add authorization. |
| M5 | BY DESIGN | An allowed CLI verb still has preconditions: offline preparation requires an administrator, stopped service and existing configuration. Allowing it in Safe enables re-preparation of a stopped guarded installation. Removing it would break that procedure. Running it against an active service intentionally fails. |
| M6 | CONFIRMED | Leftover journals were not surfaced in Safe. Added bounded read-only diagnostics, startup warning and localized Dashboard notice. The suggested automatic `RestoreAll`/`Release` would violate Safe's guarantees and was rejected. Journal values describe pending/unknown work, not proven current device state. Recovery requires separate approval, backups and a specific operator plan. No recovery action or journal deletion is performed. |
| M7 | BY DESIGN | Safe still needs RDP status and completion signalling. The worker returning is not the service/Dashboard exiting. `EnsureMultiRdp` selects only synchronous diagnostics in Safe; moving the check earlier would remove useful diagnostics/completion. No change needed. |
| M8 | CONFIRMED | `--policy-directory` used case-sensitive search while the main verb was case-insensitive. Matching now uses OrdinalIgnoreCase; the directory value is preserved, not lowercased. No live migration command was executed. |
| L1 | CONFIRMED | Fail-closed status is correct but the Tray label could misleadingly imply an actual Safe service when runtime status was unavailable. Added a distinct localized unavailable-status notice; diagnostic state participates in the menu refresh signature. There is no retry loop or authority weakening. Native Tray integration remains separately untested. |
| L2 | BY DESIGN | Authenticated logging preferences are an explicitly documented Safe exception, alongside application-owned logs/status. The narrow PUT route is intentionally permitted; arbitrary methods/system mutations are not. GET is not allowed by that special case, contrary to the finding's heading. Added regression for the exact exception; it does not grant RDP/device rights. |
| L3 | FALSE POSITIVE | `ConsoleDisplay.List()` already returns an empty list in Safe before launching a helper. The direct test verifies it. `SeatAudio.Inputs/Outputs` enumerate endpoint IDs/names only; recording/identify routes are separately blocked. Removing read-only inventory would unnecessarily reduce diagnostics. |
| L4 | CONFIRMED | Upstream resolved vulnerable Microsoft.OpenApi 2.0.0. Added an explicit patched 2.7.5 dependency in the same 2.x line. Actual HTTP OpenAPI generation, request schemas and the existing document transformer are regression tested; runtime version is checked. NuGet's transitive vulnerability scan reports no affected packages. |
| L5 | CONFIRMED | WindowsBase/WebView2 conflict and ASP.NET analyzer exceptions remain visible in the Release logs and reproduce on the original baseline. Not suppressed or claimed fixed. These tooling/dependency compatibility warnings are tracked; they do not constitute evidence of a streaming failure or fix. |
| L6 | NEEDS TEST | All 22 catalogs have matching keys/placeholders, browser and fallback checks pass. Native-speaker correctness of the new machine-assisted translations is not established by automated tests. |

## Additional confirmed packaging issue

During the required publish check, the Web SDK picked up JSON/configuration from
the ignored `Core/Installer/publish` staging tree. This was observed in the actual
published output, including stale dependency metadata. `.gitignore` does not affect
SDK Content discovery. The narrow `DefaultItemExcludes` extension excludes that
tree; default content inclusion is otherwise unchanged. A clean publish and package
regression verify no nested staging tree or per-machine mode files are distributed,
and compare all relevant web asset bytes plus all 22 dictionaries against source.

## Actual Windows/NTFS evidence

`tests/PolicyNtfsTests.csproj` references the real product implementation.
Final run: **33 elevated + 17 unelevated checks PASS** on NTFS. ACL adapters were
not mocked. Only a fresh synthetic workspace application directory was modified.
The elevated run uses an owner-approved UAC process; the ordinary run uses the
owner's unelevated UAC token, with Administrators disabled. No Windows account was
created, no separate standard-account login was used, and this is not a SYSTEM
service integration test.

Covered: first write, protected anchor/lock/policy, eight repeated saves, replacing
a permissive destination, real sharing refusal, 100 concurrent write attempts with
JSON readers, successful write after readers close, failure after real ACL setup
before the first policy publication, corrupt/missing policy with an existing anchor,
and actual denial of reads/writes/deletes/DACL changes to the unelevated process.
The latter also fails to replace policy or change mode and reads Safe/access-denied.

The concurrent run observed **58 commits and 42 explicit sharing refusals**; readers
observed only complete JSON. No partial policy or temporary-file leak was accepted.
Windows can report a sharing refusal as UnauthorizedAccessException as well as
IOException. The API now returns an explicit 503 `policy_write_denied` for access
refusal, retaining policy/revision, rather than an unhandled endpoint error.
There is no automatic retry that could hide a permission problem.

Preliminary test-development runs aborted on Windows' access-denied exception
classification and on an expected concurrent sharing refusal; their logs were
retained. The final harness treats those as explicit refused writes and verifies
preservation and later success, rather than treating them as successful writes.
No installed application was involved in any run.

## Final automated results

| Suite | Result |
|---|---|
| Release rebuild, win-x64 self-contained publish | PASS, zero errors |
| Backend/policy/authentication/real OpenAPI HTTP | PASS, 109 checks |
| Actual Windows/NTFS policy tests | PASS, 33 elevated + 17 unelevated |
| RemoteIdd inert script fixtures | PASS, 5 checks |
| Existing localization unit suite | PASS, 18 tests |
| Safe/Normal browser suite | PASS, 334 checks, 22 locales; recovery warning and mobile wrapping included |
| Existing browser regression suite | PASS, 142 source checks and 142 published checks |
| Published HTTP/public-path validation | PASS, all 22 dictionaries |
| Tray text harness | PASS, 27 keys in all catalogs, RU/JA selection and formatting |
| Package byte/content regression | PASS, web assets and 22 dictionaries; no nested staging or machine policy |
| NuGet vulnerability scan, including transitive packages | PASS, none reported by configured NuGet advisory source |
| Actual SCM/SYSTEM and ordinary-account login, new-build Tray, Normal session/Seat, Moonlight | BLOCKED, no deployment authorized |

The Release rebuild retains five warnings in the MSB3277/AD0001 categories. NU1903
was eliminated by fixing the dependency, not by suppressing it. The official
[advisory](https://github.com/advisories/GHSA-v5pm-xwqc-g5wc) lists 2.7.5 as patched
in the compatible 2.x line. This review does not assert that OpenStreamMS accepts
untrusted OpenAPI documents or that the earlier service stop was caused by it.

Browser fixtures intercept API calls and cannot prove physical streaming. One
published browser run timed out during initialization; a diagnostic rerun completed
all 334 checks. No product cause was established; the harness now reports language,
mode and JavaScript errors if initialization times out again. This is not claimed
as an installed-product reliability fix.

Logs and screenshots are retained outside Git under workspace `work/hermes-*`,
including `hermes-backend.log`, `hermes-release.log`, `hermes-publish.log`,
`hermes-vulnerabilities.json`, `hermes-ntfs-20261104-112951/`,
`hermes-maintenance-browser/`, `hermes-published-browser/`,
`hermes-http.log` and `hermes-package-verification.json`.
Screenshots are inert browser fixtures, not the installed RYZEN-PC application.

## Remaining boundaries

Installed RYZEN-PC OpenStreamMS, SCM state, drivers, RDP, TermService, firewall and
Windows accounts were not changed. No installer was executed, no reboot occurred,
and no streaming session was started/stopped. Existing integration limitations and
the original unexplained service stop remain. The previous test installer is a
pre-follow-up artifact and must not be presented as this build; no new deployment
is authorized by these checks.

See [MAINTENANCE-MODE.md](MAINTENANCE-MODE.md) for journal recovery, Safe migration
and rollback. Native-speaker review, visible toolchain warnings and physical
SCM/Tray/MultiSeat acceptance remain explicit review items. This work is suitable
for a **draft** upstream review, not an unsupported production-readiness claim.
