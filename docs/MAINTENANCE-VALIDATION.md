# Maintenance Mode validation — 2026-11-04

Overall: **PARTIAL**. Implementation and automated fixture checks PASS; deployment
and real streaming acceptance are BLOCKED. No installed OpenStreamMS service was
stopped, started, updated or reconfigured. No installer was executed. RYZEN-PC's
installed immutable direct-guard remains outside this change.

Base: upstream `e3392c0` (merged PRs #17, #19 and #18). Branch:
`feature/safe-maintenance-mode`. No updater module is introduced.

## Evidence

| Check | Result | Scope |
|---|---|---|
| Release build | PASS | .NET SDK 10.0.403; no errors |
| win-x64 self-contained publish | PASS | Application, Dashboard, 22 dictionaries and native distribution assets |
| Policy/backend tests | PASS, 91 checks | Real policy persistence, concurrent CAS, corrupt configuration, atomic failure, cold child processes, real AuthService and middleware; inert native adapters |
| RemoteIdd script fixtures | PASS, 5 checks | Mocked process/WMI capabilities; no helper/driver operation |
| Existing localization unit tests | PASS, 18 tests | Web, Tray and installer keys/formatting, loading and fallback |
| Existing browser regression suite | PASS, 142 checks on source and 142 on published assets | All 22 languages, Setup/Login/Dashboard, persistence, fallback and translated driver logs; API calls intercepted |
| New mode browser suite | PASS, 268 checks | Both modes in 22 languages, mobile layouts, disabled mutations, explicit Normal confirmation cancellation and live RU/EN switching |
| Published HTTP assets | PASS | All 22 dictionary URLs, public authentication assets and protected paths in a separate static validation host |
| Tray text harness | PASS | All dictionaries/format arguments; Russian and Japanese culture selection; no native notification icon |
| Legacy immutable direct-guard reference | PASS, 12 checks | Old reference binary only; not evidence that this configurable build is deployed |
| Package contents and Inno compilation | PASS | Compiled test package only; never executed |
| New build under real SCM/SYSTEM, restart/reboot persistence and ordinary-user ACL integration | BLOCKED | No authorized deployment environment for this task |
| Actual Tray, Safe-to-Normal restart and real Sessions/Seats | BLOCKED | Require separately authorized acceptance |
| Moonlight image/audio/keyboard/mouse/controller and physical MultiSeat | BLOCKED | No new real streaming test performed |
| Earlier unexplained service stop | UNRESOLVED | Monitor exception logging is improved; original cause remains unknown |

Local evidence is retained outside Git in sibling workspace directories:
`maintenance-tests.log`, `maintenance-published-http.log`,
`maintenance-published-browser/`, `maintenance-mode-browser/`,
`safe-maintenance-browser/`, `maintenance-package-verification.json`,
`maintenance-inno-build.log` and `legacy-directguard-checks.log`.
Screenshots are browser fixtures, not screenshots of the installed RYZEN-PC build.

The test package is `OpenStreamMS-Setup-1.5.1-maintenance-test.exe`. Its SHA-256 and
the exact changed-file manifest are recorded in the external final report, avoiding
a self-referential package hash in source. No binaries, runtime configuration,
certificates, backups or user data are committed.

## Remaining limits and risks

- Normal retains upstream implementation branches and passes pure adapter tests;
  physical MultiSeat compatibility still requires real hardware. Normal may run
  existing repair, driver and device actions. It must not be enabled over the sole
  remote access path without recovery arrangements.
- A saved mode activates at the next actual SCM startup, not immediately. Finish
  streaming first. There is no in-flight policy flip and no automatic service restart.
- Safe blocks manual lifecycle actions too. It does not stop streams left by another
  process, enumerate live console monitors, pair Sunshine clients or meter devices.
- Runtime ACL behavior is implemented with protected Windows ACLs but fixture tests
  substitute the privileged ACL adapter. SYSTEM/ordinary-user integration is BLOCKED.
- Safe is application policy, not a Windows sandbox. Another binary, old installer,
  edited script or local administrator can act outside its scope. Back up the policy
  and anchor together; explicitly prepare Safe before replacing old direct-guard.
- The initial unexplained RYZEN-PC service stop is not diagnosed or claimed fixed.
- A clean upstream baseline build reproduces the existing NU1903 Microsoft.OpenApi
  2.0.0 advisory, WindowsBase/WebView2 resolution warning and ASP.NET analyzer exception.
  These dependency/toolchain warnings were not introduced or hidden by this change.
  Review the linked advisory in MAINTENANCE-MODE.md before production deployment.
- New translations are machine assisted and require native-speaker review.

## Deployment and manual acceptance

Do not run the test installer against RYZEN-PC. Follow the explicit offline
direct-guard migration and immutable-guard rollback instructions in
[MAINTENANCE-MODE.md](MAINTENANCE-MODE.md) only after separate owner approval.

Before approval, review the proposed file manifest, hashes, independent Windows
access, active streams and a readable backup of application/service configuration,
users, sessions, certificates and pairings. Proposed changes are application file
replacement plus protected policy/anchor/runtime files; no RDP, driver, firewall,
account or service-registration change is part of Safe deployment.

After approval: finish streams, stop only OpenStreamMS, prepare and verify committed
Safe policy before replacement, copy reviewed files preserving data, then start only
the existing service. Compare configuration hashes and RDP/driver/firewall snapshots;
verify no Sessions/Seats autostart, Dashboard authentication, 22 languages, RU/EN and
Tray. Observe for at least 15 minutes and test a controlled service restart in Safe.
Rollback to verified immutable direct-guard files if acceptance fails, preserving
configuration and policy. If restoration is uncertain, stop and ask the owner.

A subsequent separately authorized Normal test must cover an explicit confirmation,
controlled restart, one session, one Seat and Moonlight video/audio/input/controller,
normal shutdown, service errors and independent Windows access. Record PASS/FAIL
for each observation; do not infer streaming success from Dashboard or localization.
