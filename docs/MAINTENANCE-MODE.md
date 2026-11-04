# Safe Startup / Maintenance Mode

This feature is intended for machines maintained remotely. It is based on upstream
`e3392c0` and preserves the localization, isolated-user/ViGEmBus and Guide button
changes from PRs #17, #19 and #18. It is not an updater or a Windows sandbox.

## Modes and configuration

Normal preserves upstream session, seat, RDP repair, driver and device behavior.
The feature adds no new automatic installation or repair to Normal.

Safe leaves Dashboard, authentication, Tray, language selection, logs, component
status and configuration reads available. It blocks session/seat creation,
editing, deletion, start/stop/restart, pairing, Sunshine proxy calls, power actions,
RDP changes, driver installation/removal, USB recovery and device isolation.
Stopping an existing streaming session is also a mutation: do it before entering
Safe. Safe does not silently kill or adopt a Windows session left by another process.

`operation-mode.json` is a separate protected part of the service configuration:

```json
{
  "Version": 1,
  "OperationMode": "Safe",
  "PendingMode": null,
  "Revision": 1
}
```

Keeping policy separate from `service.config.json` prevents an old settings writer
from erasing the restriction. Both policy writes and service configuration saves
use a temporary file, apply SYSTEM/Administrators-only ACLs **before writing**, flush
to disk, and atomically replace the destination. An exclusive file lock and revision
comparison reject concurrent/stale changes. No permissions failure is ignored.

`operation-mode.required` is written first on the first explicit policy save.
A missing policy with this anchor, unsupported schema/version/enum, duplicate keys,
malformed JSON or an inaccessible policy results in Safe. Existing upstream
installations with neither file retain their legacy Normal default. These files
must be included in configuration backups; they are not distributed as defaults.
Deleting both files as an administrator intentionally resets this installation's
configuration: this feature cannot defend against a hostile local administrator.

## Switching, authority and restart

The authenticated Dashboard administrator uses
`GET/PUT /api/settings/operation-mode`. A PUT includes `operationMode`, `revision`
and `confirmNormal`. Normal requires explicit confirmation. Tray's local API bypass
does not include this endpoint. Requests without normal application authentication
are rejected by the existing authentication middleware.

Saving sets **PendingMode**, leaving the committed OperationMode unchanged. It does
not install drivers, repair Windows, restart the service or launch sessions. The
current service process keeps its effective policy. Dashboard shows current and
next modes and explains that a restart is required. In particular, saving Safe
while Normal is active does **not** promise immediate protection; finish active
operations and arrange a controlled service restart. A saved Normal choice can be
applied by the next SCM startup, including a reboot or recovery start.

Only an actual SCM service startup activates PendingMode. Ordinary CLI processes
cannot commit a pending Normal choice. Failure to activate causes restricted
startup; RDP diagnostic completion is signalled even when its probe fails, so
waiting components are not left hanging. There is no hot privilege flip while a
native operation is in flight. A service restart still needs the normal assessment
of active streams and independent Windows access.

Tray and seat subprocesses cannot read the private configuration. The service
publishes only active mode, PID and process creation ticks in
`operation-mode.runtime.json`, writable by SYSTEM/Administrators and readable by
Users. Clients reject stale/reused PIDs and re-read status. A live Safe service
also binds privileged CLI clients. Missing status for a running installation fails
closed. This public status is never used to select the service's own mode.

## Enforcement and audit scope

| Path | Enforcement before side effects |
|---|---|
| API | Middleware after authentication; 409 JSON `code: maintenance_mode` before mutation handlers/Task.Run |
| Session lifecycle | Guards before state changes, user provisioning, FreeRDP, Sunshine or firewall rules; migration stays in memory in Safe |
| Seats | Hosted startup returns before USB restore, rights recovery or autostart; direct CRUD/lifecycle/presenter/agent calls guarded |
| RDP | Safe only probes status, signals initialization completion, never calls repair; direct registry/session/display helpers guarded |
| Drivers | EnsureInstalled returns before detection in Safe; install/remove reject before download, registry or installer calls |
| Devices | Gamepad hosted startup returns before Guide mapping/HidHide enumeration; USB, HidHide setters, display/rights/profile helpers guarded |
| Tray | Polls diagnostics; disables lifecycle menu actions and native presenter while Safe; backend remains authoritative |
| CLI | Safe permits run, tray/open, help, read-only check and explicit offline Safe preparation; install/uninstall/purge/remove-deps/display/identify/seat commands rejected |
| Installer | Policy preflight rejects before StopAppProcesses, file replacement or uninstall; ordinary installs without policy retain upstream flow |
| RemoteIdd | Packaged install/uninstall check the same executable policy parser and terminate before privileged helper actions |

Normal runtime behavior is preserved inside existing implementation branches.
The synchronous session-monitor timer now logs exceptions instead of leaving them
unobserved; this is **not** evidence that the earlier unexplained host stop is fixed.
There is no added crash restart loop or SCM recovery configuration change.

Safe permits application-owned log/cache/status publication, authentication and
logging preferences; it does not make every file or registry read/write impossible.
Live console-monitor enumeration is unavailable in Safe because it would launch a
console helper; saved seat configuration remains readable. Device identification,
microphone metering, Sunshine pairing and stopping a leftover stream are also
unavailable until an explicitly approved Normal restart.
It restricts the current installation's managed runtime, API, CLI and packaged
installer/RemoteIdd entry points. Other applications, external administrative tools,
arbitrary edited scripts, old installers, and another executable copy outside this
installation are outside this policy. No promise of full Windows isolation is made.

## Direct-guard migration — OFFLINE, explicit approval required

No migration is applied automatically based on a guessed version or configuration.
An old direct-guard file alone does not reliably identify the old installation once
its executable is replaced. Do not copy a new ordinary-default binary over it first.

1. Obtain approval for the exact deployment and assess active sessions and independent
   access. Finish streams normally. Back up all application files, service registration,
   configuration, policy files, sessions, certificates and pairing state. Verify hashes
   and restore readability. Keep the known immutable direct-guard EXE/DLL available.
2. Stop only OpenStreamMS through SCM. Do not change drivers, TermService or firewall.
3. From a verified **staging** copy of this build, as administrator, run
   `OpenStreamMS.exe --prepare-safe-mode --policy-directory "<existing app directory>"`.
   This command requires the existing service to be stopped and its configuration to
   exist. It writes only the protected policy/anchor in the explicitly supplied directory.
4. Verify `Version=1`, `OperationMode=Safe`, `PendingMode=null`, SYSTEM/Administrators ACLs,
   and backups **before replacing any installed binaries**. Do not proceed if verification fails.
5. Only after separate deployment approval, copy the reviewed application files without
   replacing user configuration or state. This is a manual migration, not an Update Only module.
   The ordinary installer intentionally refuses to mutate an installation in Safe.
6. Start only the existing service via SCM. Verify effective Safe through the authenticated
   API, startup logs, 22 dictionaries, Tray and system/configuration snapshot comparison.
   No Sessions/Seats should start. Observe service stability for at least 15 minutes.
7. Normal selection, service restart and a real streaming test are a later explicit choice.
   They can invoke upstream system operations; do not perform them over the sole remote
   connection without independent recovery access and an approved test plan.

Rollback: if validation fails, stop the application only when safe, restore the verified
**immutable direct-guard** application files and preserved configuration/policy, then verify
the guard before a controlled SCM start. Do not fall back to an unrestricted older binary,
remove drivers, repair RDP or reboot as an automatic recovery step.

On RYZEN-PC the installed `1.5.1+f8a595c.direct-guard` remains untouched by this work.
No installed-service test, reboot, driver change, installer execution or migration is authorized
by development tests. Actual Safe/Normal SCM restart, Tray integration, Seats and Moonlight
image/audio/input acceptance remain separate real-hardware checks.

## Validation

See `tests/README.md`. Automated tests use workspace fixtures, mocked capability
adapters, isolated HTTP endpoints on ephemeral loopback ports, and headless browser
API interception. They never run an actual installer or mutate RDP/drivers/firewall.
They exercise real policy persistence, auth, maintenance middleware and guarded
backend entry points; they do not certify physical MultiSeat or streaming.

The upstream dependency graph currently reports NU1903 for Microsoft.OpenApi 2.0.0
([CVE-2026-49451](https://github.com/advisories/GHSA-v5pm-xwqc-g5wc)); the advisory lists
2.7.5 as the patched 2.x version. This change does not silently upgrade unrelated
dependencies or establish exploitability in OpenStreamMS. Review it before production
deployment. WindowsBase/WebView2 resolution and ASP.NET analyzer warnings are recorded
in the validation report, not hidden or treated as proof of a runtime failure.
