# Add configurable Safe Startup / Maintenance Mode

Remote administrators currently cannot start OpenStreamMS for diagnostics without
also permitting normal startup recovery and session/device operations. A startup
failure or an experimental deployment can therefore put the only Windows access
path at risk. This change introduces a configurable Safe maintenance mode while
keeping Normal behavior based on the current upstream master.

Safe keeps Dashboard, Tray, all 22 existing languages and component diagnostics
available. Backend and CLI guards reject session/seat lifecycle, RDP/driver/device
and power mutations before side effects. Startup workers pause recovery, autostart,
HidHide, USB and user-rights operations. The installer and RemoteIdd scripts check
the policy before their existing privileged actions.

The protected, atomically saved policy has Normal/Safe and a pending mode. Only SCM
startup applies a saved pending choice; selecting Normal requires explicit admin
confirmation. Corrupt/unknown policy fails closed, and a persistent anchor protects
against accidental policy loss. Unprivileged Tray/seat processes consume a
read-only, PID/creation-time-bound service status rather than private configuration.
An explicit offline migration procedure preserves Safe for existing direct-guard
deployments; no updater or automatic host migration is introduced.

The implementation starts from e3392c0, retaining PRs #17, #19 and #18. Diagnostics
and localization tests do not constitute streaming certification. Policy/API tests
use real handlers with inert capability adapters; browser tests intercept all API
calls. Actual SCM restart, Tray, Safe→Normal, Seats and Moonlight image/audio/input
acceptance must still be performed on separately authorized hardware. The previous
unexplained service stop is not claimed to be fixed.

Some new translations are machine assisted and need review by native speakers.
Safe is an application policy, not a Windows sandbox or protection against a local
administrator running other binaries/tools. See MAINTENANCE-MODE.md for boundaries,
migration, rollback and dependency warnings. No upstream PR or release is created
automatically by this work.
