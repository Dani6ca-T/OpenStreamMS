# Fork-only direct-guard profile for 1.5.1

This branch preserves the source changes used for the installed
1.5.1+f8a595c.direct-guard build. It starts from localization commit f8a595c.
It is separate from the upstream localization PR and contains no Safe Update
engine, Inno Update Only entry point, host configuration, backups, certificates,
credentials, or newly committed runtime binaries.

## Runtime behavior

StartupSafetyPolicy is immutable: RestrictedStartup=true and
AutomaticRdpRepairEnabled=false. Every process reads those constants; no writable
JSON switch can re-enable automatic repair. RDP status still reports diagnostics,
and initialization waiters are released even for a broken wrapper.

Automatic session recovery/startup, seat startup/USB recovery, and background
gamepad/HidHide changes are paused. Driver install/uninstall methods reject calls
before native detection; RDP mutation endpoints reject requests. The Dashboard
shows a translated notice and disables driver/RDP mutation buttons. Startup
session migration stays in memory instead of rewriting saved sessions.

The updateOnlyPolicyActive DTO field and updateOnly.notice translation key are
compatibility names for this UI notice. They do not load an update module.

This is a restricted runtime profile, not a general administrative sandbox.
Ordinary administrative CLI commands and installer code remain in the inherited
repository and must not be used for the direct deployment. Do not run --install,
--uninstall, --remove-deps, Inno Setup, or RemoteIdd scripts to deploy this profile.
Streaming/automatic seats are intentionally limited; this branch is not evidence
that Moonlight streaming works.

## Build and tests

Use Windows and the .NET 10 SDK. The reference deployment used SDK 10.0.403,
self-contained win-x64 publication, and ReadyToRun. For source verification:

~~~powershell
dotnet build OpenStreamMS.csproj -c Release -p:InformationalVersion=1.5.1+f8a595c.direct-guard -p:IncludeSourceRevisionInInformationalVersion=false
dotnet run --project tests/DirectGuardTests.csproj -c Release -- bin/Release/net10.0-windows/OpenStreamMS.dll
node --test tests/i18n.test.cjs
~~~

DirectGuardTests links the actual policy, driver/RDP manager and background
service classes with capability stubs. It checks absent ViGEmBus/HidHide, broken
TermWrap, startup completion, paused sessions/timer, fresh-process persistence,
and the supplied product DLL's metadata/IL. It does not register or stop/start
an SCM service, install/remove drivers, or mutate RDP. The default diagnostic
probe can read Windows wrapper status; it does not repair it.

The original source-to-payload packaging excluded configuration/state, driver
packages, and installation scripts, and supplied previously built native Steam
components. A raw dotnet publish output is therefore not an approved live update
package. The existing installation is updated only by a separately reviewed file
manifest and verified backups; no deployment executable is supplied by this branch.
Native dependency builds follow the existing project/release workflow. No new
native binaries or historical user data are added to Git.

docs/direct-guard-source-manifest.json records normalized source hashes and the
reference deployed EXE/DLL hashes. It is provenance, not a claim that a different
checkout path, SDK or build environment reproduces byte-identical PE files.

## Validation boundary

Reference live update: 599 files replaced; 705 files backed up; 103 preserved
files matched before/after startup. The service and Dashboard worked; all 22
Dashboard languages, RU/EN reload persistence, and read-only driver dialogs were
checked. Driver/RDP/firewall, service registration and autorun snapshots matched.
No driver or RDP changes, reboot, or automatic session/seat launch occurred.

Tray process/autorun were checked, but native tray-menu/notification visual
acceptance was blocked by unavailable Computer Use native surfaces. Driver logs
were empty after service restart; operation translations were verified with
fixtures, without triggering driver operations. Actual Moonlight pairing,
streaming, first-run production setup, reboot/logon behavior and production
rollback were not accepted. Earlier fixture tests do not replace those checks.

Keep all local deployment reports, full snapshots, configurations, certificates,
private backups and installers outside Git. The live installation must not be
changed merely to validate or publish this branch.
