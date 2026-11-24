# Extensions

OpenStreamMS can install extensions straight from GitHub. An extension can:

| Capability | What it does |
|---|---|
| `component` | Installs, detects and uninstalls something on Windows with PowerShell scripts (drivers, tools, services). |
| `hooks` | Runs scripts when a streaming session or a seat starts or stops. |
| `apps` | Adds entries to the Moonlight app list of every session (Sunshine `apps.json`). |
| `panel` | Adds its own tab to the dashboard: a static page, isolated from the dashboard. |

Users install an extension from a link (`owner/repo`, `https://github.com/owner/repo`,
`…/tree/<ref>/<folder>`, `…/releases/tag/<tag>`) or from a **catalog**. Before anything is
installed, OpenStreamMS downloads the package and shows what it does, who made it, the commit
and the SHA-256 of the package; the user has to confirm that exact version.

> **Extensions run as SYSTEM**, with full control of the PC. OpenStreamMS doesn't review them.
> Only install extensions from people you trust.

## Package

The package is the repository (or a folder of it) at a fixed commit:

- If the release has an asset called **`osms-extension.zip`**, that zip is the package (use it
  to ship compiled binaries). If everything in the zip hangs from one folder, that folder is the root.
- Otherwise, the source code of the release's commit, or of the latest commit of the default
  branch when the repository has no releases.
- A catalog or a link can point to a **folder** of the repository (`…/tree/main/extensions/foo`),
  so one repository can hold several extensions. Folders always use the source code.

The root of the package must contain `osms-extension.json`. The package is extracted to
`%ProgramData%\OpenStreamMS\extensions\<id>\pkg` (only SYSTEM and Administrators can write there).

Limits: 300 MB download, 1 GB extracted, 20 000 files. Paths can't leave the package.

## `osms-extension.json`

```jsonc
{
  "schema": 1,
  "id": "my-extension",          // lowercase letters, digits and hyphens, up to 48
  "name": "My extension",        // up to 80 characters
  "description": "What it does, in one or two sentences.",
  "author": "Your name",
  "homepage": "https://github.com/you/my-extension",   // https only
  "license": "MIT",
  "notice": "Shown in the install dialog: licenses, risks, requirements.",
  "minOsmsVersion": "1.7.0",     // optional
  "icon": "icon.png",            // optional: .png, .jpg, .webp, .gif or .svg in the package, up to 256 KB
  "categories": ["tools"],       // optional: up to 3, see Categories below

  "component": {
    "install":   "install.ps1",  // required; exit 0 = OK, 3010 = OK, restart Windows
    "uninstall": "uninstall.ps1",
    "detect":    "detect.ps1",   // exit 0 = installed
    "reboot":    false,          // ask to restart Windows after installing
    "timeoutSec": 300            // per script, 10–3600
  },

  "hooks": {
    "sessionStart": "hooks/session-start.ps1",
    "sessionStop":  "hooks/session-stop.ps1",
    "seatStart":    "hooks/seat-start.ps1",
    "seatStop":     "hooks/seat-stop.ps1",
    "runAs": "system",           // or "user": in the session's Windows session, as its user
    "timeoutSec": 60             // per script, 1–600
  },

  "apps": [
    {
      "name": "My app",          // unique, up to 60 characters
      "cmd": "{ext}\\bin\\my-app.exe",
      "workingDir": "{ext}\\bin",
      "imagePath": "{ext}\\cover.png",
      "detached": [],
      "prepCmd": [ { "do": "…", "undo": "…", "elevated": false } ],
      "autoDetach": true
    }
  ],

  "panel": {
    "title": "My tab",           // up to 30 characters
    "entry": "panel/index.html"
  }
}
```

At least one of `component`, `hooks`, `apps` or `panel` is required. All script paths are
relative to the package and must be `.ps1` files that exist in it.

### Icon and categories

`icon` is shown on the extension's card. It must be a PNG, JPEG, WebP, GIF or SVG file in the
package, up to 256 KB, and its content has to match its extension. A square image of 128×128 or
more looks best. Without one, the card shows the name's first letter.

`categories` let users filter the Extensions tab. Use up to three of:

| Category | For |
|---|---|
| `remote-desktop` | Several sessions at once, Remote Desktop patches and settings. |
| `streaming` | Streaming hosts, encoders and clients. |
| `display` | Virtual displays and display drivers. |
| `input` | Controllers, keyboards, mice and other input devices. |
| `audio` | Audio devices and routing. |
| `network` | Networking for sessions and seats. |
| `games` | Game launchers, stores and apps for Moonlight. |
| `monitoring` | Dashboards, metrics and status pages. |
| `tools` | Anything else. |

Unknown categories are accepted and ignored, so an extension written for a newer OpenStreamMS
still installs.

### Scripts

Scripts run with `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File`,
from the package folder. Output goes to `extensions\<id>\logs\<install|uninstall|detect|hooks>.log`,
which the dashboard shows. Every script gets:

| Variable | Value |
|---|---|
| `OSMS_EXTENSION_ID` | The extension's id. |
| `OSMS_EXTENSION_DIR` | The package folder. |
| `OSMS_ACTION` | `install`, `uninstall`, `detect` or the hook event. |
| `OSMS_PREVIOUS_VERSION` | On install: the version being replaced (empty on a first install). |
| `OSMS_VERSION` | OpenStreamMS version. |

`install.ps1` also runs on updates: make it idempotent. If it fails, the previous package stays
installed.

### Hooks

`sessionStart` runs once the session is up; `sessionStop` runs before the session's Windows
session is closed (stop hooks get at most 15 s in total, whatever `timeoutSec` says).
`seatStart`/`seatStop` work the same for seats. Extra variables:

| Variable | Value |
|---|---|
| `OSMS_EVENT` | `SessionStart`, `SessionStop`, `SeatStart` or `SeatStop`. |
| `OSMS_SESSION_ID`, `OSMS_SESSION_NAME` | The streaming session. |
| `OSMS_WINDOWS_SESSION_ID` | Its Windows session id. |
| `OSMS_WINDOWS_USER` | Its Windows user (`DOMAIN\user`). |
| `OSMS_LOCAL` | `1` for the local session (the PC's own desktop). |
| `OSMS_SEAT_ID`, `OSMS_SEAT_NAME` | The seat (seat events only). |

With `"runAs": "user"` the scripts run hidden in the session's Windows session as its user
(good for starting programs the user sees); their output isn't logged, only the exit code.

### Apps

Entries are written to each session's Sunshine `apps.json` the next time the session starts,
marked with `"osms-extension": "<id>"`. `{ext}` in any text is replaced with the package folder.
A user app with the same name is replaced. Disabling or uninstalling the extension removes them.

### Dashboard tab

The page is served from `/ext/<id>/<token>/<entry>` inside an `<iframe sandbox>` without
`allow-same-origin`, plus a `Content-Security-Policy: sandbox` header. It has an opaque origin:
it can't read the dashboard, its cookies or its API. Relative links to other files of the package
work. To get read-only context, ask the dashboard:

```js
window.parent.postMessage({ osms: 'context' }, '*');
window.addEventListener('message', ev => {
  if (ev.data?.osms !== 'context') return;
  // ev.data = { extension, lang, theme, sessions: [{ id, name, state, local }], seats: [{ id, name, state }] }
});
```

## Catalogs: `osms-catalog.json`

A catalog is a GitHub repository with `osms-catalog.json` at its root (or in the folder of a
`…/tree/<ref>/<folder>` link). OpenStreamMS ships with the official catalog,
`https://github.com/OpenstreamCo/osms-extensions`; users can add and remove others.

```jsonc
{
  "schema": 1,
  "name": "My catalog",
  "description": "Extensions for …",
  "extensions": [
    {
      "repo": "you/my-extension",       // or a github.com URL
      "path": "extensions/foo",         // optional folder
      "ref": "v1.2.0",                  // optional: pin a tag, branch or commit
      "sha256": "…64 hex…",             // optional: the package must have this hash
      "id": "my-extension",             // optional: the package must have this id
      "name": "My extension",
      "description": "…",
      "notice": "Shown in the install dialog next to the author's notice.",
      "tags": ["tools"],
      "icon": "icons/my-extension.png",   // optional: relative to the catalog's folder
      "categories": ["tools"]             // optional: overrides the extension's own
    }
  ]
}
```

The catalog's `icon` and `categories` are shown before anything is installed. Without them, the
dashboard uses the extension's own once it's installed.

With `ref` and `sha256`, the catalog decides exactly what gets installed, and updates follow the
catalog. Without them, extensions follow their latest release.

## Updates

“Check for updates” resolves the same source again (the latest release, the same branch, or what
the catalog pins now). If the package is different, the user sees the new version, commit and
hash and has to confirm again.
