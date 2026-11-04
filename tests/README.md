# Localization validation

## Safe / Maintenance Mode

Run only the fixture tests below, not the real product `--run` or installer on a
working machine. These checks do not start/stop SCM services or change Windows:

```powershell
dotnet run --project tests/MaintenanceTests.csproj -c Release
./tests/remoteidd-maintenance.test.ps1 -FixtureDirectory C:/path/to/workspace-fixtures
node tests/maintenance-browser.cjs
```

The C# suite references the actual policy, guarded backend methods, AuthService
and HTTP middleware. Native repair in the Normal branch uses an injected callback;
Safe checks reject before native adapters, including absent-driver fixtures.
Persistence tests include cold processes, corrupt policies, concurrent requests,
atomic failure, pending Normal and preserved session data. The PowerShell suite
replaces WMI/process capabilities and never executes a driver/helper operation.
The browser suite intercepts every API request and checks both modes in 22 locales,
confirmation, disabled actions and responsive layouts. Set `NODE_PATH` to an
existing Playwright installation and `MAINTENANCE_TEST_OUTPUT` for screenshots.

The immutable legacy `DirectGuardTests` on `feature/direct-guard-1.5.1` remain a
reference for that old binary; constant getter assertions do not apply to the new
configurable policy. The maintenance suite replaces those assertions for this branch.

Real service restart, permissions under SYSTEM and an ordinary Windows user, Tray,
Normal streaming, Seats, and Moonlight video/audio/input are NOT fixture passes.
They require separate authorized acceptance per `docs/MAINTENANCE-MODE.md`.

### Real Windows/NTFS policy tests

`PolicyNtfsTests.csproj` is separate from the fixture suite. It performs actual
Windows ACL and replacement operations only in a **fresh synthetic workspace**
application directory, with SYSTEM/Admin writes and Users read/traverse on the
directory. It never uses SCM, account management, registry, drivers or firewall.

Build with `dotnet build tests/PolicyNtfsTests.csproj -c Release`. Create a new
evidence directory. From an explicitly elevated terminal run
`dotnet tests/bin/Release/net10.0-windows/PolicyNtfsTests.dll --elevated <evidence>`.
Then from a non-elevated terminal run the same DLL with `--ordinary <evidence>`.
The second phase requires an existing successful elevated fixture and verifies
real denial of reading, writing, deleting and changing private file ACLs. No Windows
account is created. The executed acceptance used the owner's unelevated UAC token
with Administrators disabled, not a separate standard-account login or SYSTEM.

The elevated phase checks repeated writes, permissive-destination replacement,
real sharing failure, concurrent readers, interrupted first publication and an
anchored missing/corrupt policy. Windows may explicitly refuse a replacement while
a reader holds it; the test requires complete old/new JSON, retained ACLs, no leaked
temporary files and successful replacement after readers close. API access errors
are returned explicitly, never reported as successful saves. Results/logs go in
the evidence directory. Fixtures contain only synthetic data and may be retained
for review; any later cleanup requires access to that exact workspace directory.

The tests do not install or start OpenStreamMS, drivers, Windows services, or
streaming sessions. Run from the repository root.

```powershell
node --test tests/i18n.test.cjs
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -o Core/Installer/publish
node tests/verify-package.cjs
```

`i18n.test.cjs` checks every dictionary, the effective original values at source
commit `79a032e5d00efb37bb53a71991408acbd94c3a11`, placeholders, safe HTML,
asynchronous startup, storage, races, English fallback and error recovery.

Browser tests require the `playwright` Node package and an installed Microsoft
Edge. Use an existing package installation via `NODE_PATH` if appropriate.

```powershell
node tests/i18n-browser.cjs
$env:I18N_WEB_ROOT = (Resolve-Path Core/Installer/publish/wwwroot).Path
$env:I18N_TEST_OUTPUT = 'C:/path/to/test-results'
node tests/i18n-browser.cjs
```

All API requests in this suite are intercepted. The suite checks all 22
languages on Login, Setup and Dashboard, language persistence, open session/seat
forms, preservation of unsaved fields, errors, fallback and Russian action labels
at 375/720/1280 pixels in light/dark themes. Driver dialogs also check localized
progress/success/failure log rendering, live language changes, preserved technical
details and safe text rendering. Driver API calls must remain GET-only; no driver
is installed or removed. Screenshots and JSON results are saved.

For real ASP.NET static-file and public-path checks, generate a separate host
outside the product source tree:

```powershell
./tests/prepare-validation-host.ps1 -OutputDirectory C:/path/to/validation-host
dotnet build C:/path/to/validation-host/ValidationHost.csproj -c Release
dotnet C:/path/to/validation-host/bin/Release/net10.0/ValidationHost.dll C:/path/to/publish/wwwroot http://127.0.0.1:51972
```

This host compiles the current `IsPublicPath` method extracted from `Program.cs`
and the actual `TrayText.cs`, but links no streaming/system code. In another shell:

```powershell
node tests/verify-http.cjs http://127.0.0.1:51972
$env:I18N_BASE_URL = 'http://127.0.0.1:51972'
node tests/i18n-browser.cjs
dotnet C:/path/to/validation-host/bin/Release/net10.0/ValidationHost.dll --tray-test ru-RU
```

The validation cookie is test-only. This host does not validate real accounts,
register services, or reproduce the complete application's authentication flow.
`--tray-test` checks all tray dictionaries, format arguments, Windows language
detection and fallback; it does not test the real notification icon or service.

`preview-server.cjs <published-wwwroot> [port]` is an optional visual preview with
inert session and device fixtures. All write API calls fail without side effects.
It is not an integration test of Windows, RDP, devices or streaming.

Full installation, uninstallation, tray integration and driver behavior must be
tested separately in an explicitly permitted disposable Windows VM.
