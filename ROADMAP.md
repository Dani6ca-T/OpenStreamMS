# Roadmap

Planned work for OpenStreamMS. Items move to the README once they ship.

## Planned

Grouped by priority, highest first.

### 1. Polish local seats

- **Hardware check of untested seat features.** Seat start on the lock screen, audio capture
  when the seat agent runs as SYSTEM, the console lock guard (<kbd>Win</kbd>+<kbd>L</kbd>), removing
  the shutdown right and disconnecting from inside a seat are implemented but not yet verified on
  real hardware. Add a checklist, or a self-test in the dashboard.
- **Ctrl+Alt+Del on password seats.** If Windows asks for Ctrl+Alt+Del before the password, the seat
  can't get past the lock screen, because that combination can't be injected as keys. The service can
  raise it with `SendSAS`.
- **Keyboard LEDs on seats.** Caps Lock, Num Lock and Scroll Lock LEDs stay off on keyboards captured
  with WinUSB. Send the keyboard its LED output report over the same WinUSB connection.
- **Hot-plug into a running seat.** A keyboard or mouse plugged into one of a running seat's USB
  ports should be captured and routed to the seat automatically, without turning the seat off and on.
  (Other USB devices and gamepads without HidHide's session jail are already reserved on arrival.)

### 2. More useful seats

- **Assign a whole USB hub to a seat.** Everything plugged into that hub goes to the seat, with no
  per-device setup.
- **Bluetooth gamepads and shared receivers.** Bluetooth gamepads inherit the port of the Bluetooth
  radio, and every pad on a wireless receiver (Xbox, Unifying) shares the receiver's port: assign them
  by Bluetooth address or by child device instead.
- **USB storage, cameras and audio on a seat.** The device ACL doesn't isolate what a Windows service
  opens on the session's behalf: give removable drives a per-session drive letter, and evaluate RDP
  USB redirection (URBDRC) for cameras and other devices that need their driver inside the session.
- **Schedules and time limits.** Per seat or session: turn off at a set time (e.g. a kids' seat at
  21:00), daily time allowance, and turn off after a period without input.

### 3. Maintenance and trust

- **Diagnostics page.** Hardware errors from the Windows event log (WHEA machine checks, unexpected
  restarts), devices left on the WinUSB driver, and per-seat stats (audio underruns, dropped frames).
- **Update from the dashboard.** The panel already shows the installed version and notices a newer
  GitHub release (About, #96). Still to do: download and install it in one click, and a beta/nightly
  channel to opt into.
- **Automatic release notes.** Have the release workflow build the notes from the commits since the
  previous tag; GitHub's generated notes are empty when changes don't come from pull requests.
- **Automated tests.** Unit tests for the HID report descriptor parser and the seat rules (conflicts,
  password, auto-start, shutdown right), run in CI.

### 4. Website

- **Screenshots and a short clip of local seats in use** on the project site and the README.

### 5. Per-session LAN IP

**Goal:** optionally give a session its own IP on the local network (e.g. `192.168.1.201`) so that
every program in that session uses it. Classic LAN games (fixed ports, broadcast discovery) can then
run twice on the same PC — one copy per session or seat — and see each other and other PCs on the
network as separate hosts.

**Why it needs more than an extra IP:** Windows has a single network stack for the whole machine.
Programs usually bind to "any address" (`0.0.0.0`) and Windows picks the source IP from the routing
table, never by session. Two copies of a game then collide on the same port and announce the same IP.

**Chosen direction (2026-11-11): one virtual network adapter per session.** Each session gets its own
Hyper-V host virtual NIC (`Add-VMNetworkAdapter -ManagementOS`, signed Microsoft adapters, no driver of
our own), connected to the outside through an interface picked in the dashboard. Two modes, chosen per
installation in the dashboard:

- *Bridge*: an external switch bound to the chosen interface; every session gets an IP from the router
  and is a separate host on the LAN (LAN games work with other PCs). The PC itself moves to `vEthernet`.
- *NAT*: an internal switch plus `New-NetNat`; sessions live in a private subnet and go out through the
  chosen interface. Does not touch the PC's own networking, but other PCs do not see the sessions.

Sessions should not see the main network card. Windows has one network stack, so this is done in user
mode by the hook in point 2: it lists only the session's adapter and binds sockets to it. Kernel-level
isolation is out of scope (see *Ruled out*). Requires Windows Pro, Enterprise or Server with Hyper-V;
Windows Home keeps the PC's IP. The secondary-IP option below stays as the fallback.

**Design (research done, not started):**

1. **The address.** Two ways, chosen per installation:
   - *Secondary IP on the existing network adapter* (default): added with `SkipAsSource` so the rest
     of the PC keeps using its main IP. Windows only, applied instantly. Requires the adapter to have
     a static IP, and the session IPs must be outside the router's DHCP pool.
   - *Bridged virtual adapter*: a Hyper-V external switch with one host virtual NIC per session, each
     with its own MAC and a DHCP lease from the router. Heavier: it enables Hyper-V and changes the
     networking of the whole PC.
2. **Making programs use it.** A user-mode DLL (same mechanism as the Steam isolation hook; no
   drivers) is loaded into the session's Explorer and passed on to every child process:
   - `bind`, `connect`, `sendto`, `WSAConnect` and `ConnectEx` bind wildcard sockets to the session IP.
   - `gethostname`, `getaddrinfo` and `GetAdaptersAddresses` report only the session IP, so games
     advertise the right address.
   - Sunshine and OpenStreamMS processes are excluded.
3. **Dashboard.** A per-session setting "Network: own IP [x.x.x.x] / PC's IP" and a per-session list of
   excluded games. The service adds and removes the IP when the session starts and stops.

**Verified so far:** a UDP socket bound to a specific IP still receives broadcasts sent to
`255.255.255.255` and to the subnet broadcast from that adapter, including ones sent from the same PC,
so LAN game discovery keeps working.

**Next steps:**

- Prototype outside OpenStreamMS: add a temporary secondary IP, run two copies of a LAN game each bound
  to its own IP, and check they find each other. Confirm that a broadcast sent from one secondary IP
  reaches a socket bound to another IP on the same adapter.
- Integrate the setting, the IP lifecycle and the hook into sessions and seats.

**Known limits:**

- Games with kernel anti-cheat (Easy Anti-Cheat, BattlEye, Vanguard) may refuse to start or flag an
  injected DLL; they go on the exclusion list and use the PC's IP.
- Programs not started from the session's desktop (elevated through UAC, scheduled tasks, COM
  servers) keep the PC's IP.
- IPv6 is not covered for the redirected sockets; both 32- and 64-bit builds of the DLL are needed.
- Most modern online games and Steam networking don't need this; it targets LAN games.

**Ruled out:** WFP redirection (needs a kernel callout driver), Winsock LSPs (removed from current
Windows) and network compartments (undocumented, container-only).

## Experimental

### Remote-session IddCx display (clean alternative to the DuoStream hook)

**Idea:** give each seat its own display through a Windows-documented *remote-session* IddCx
driver instead of hooking Microsoft's `RdpIdd.dll` the way DuoStream does. Frames leave the
driver's swapchain to Sunshine over shared memory, bypassing RDP's own video path.

**Status: working on a real seat and offered as an optional installer task.** The first real-seat
test (1920×1080, Moonlight at a steady 60 Hz, no dropped frames) passed; the UAC secure desktop no
longer turns the stream black, and auto capture picks `remoteidd` whenever the provider is active.
It still uses a test certificate, needs a PC that accepts its protocol provider (RemoteIdd is taken out of
RDP-Tcp when RDP-Tcp does not listen with it) and takes effect after a restart. Pending: attestation signing for real
installs. Everything lives in
`Native/RemoteIdd/` — start with `GUIA.md`. `build.cmd` builds against the official WDK NuGet
package (no WDK install) and runs the tests: frame hand-off contract, cross-process GPU
performance (4K240 present→pixel p99 ≈ 0.6 ms) and the protocol provider with fakes.

**How the blocker was solved, with documented APIs only:** an RDS protocol provider
(`OsmsRdsProvider.dll`) wraps Microsoft's own RDP protocol (`rdpcorets`) and answers only
`IWRdsWddmIddProps`: Windows then loads our IddCx for the session, and the provider hands the
session id and client resolution to the driver by IOCTL. It sits on RDP-Tcp (Windows client
editions start no other listener, as DuoStream also assumes) and acts only on connections
from the machine itself (127.x or ::1); every other RDP connection keeps Microsoft's protocol and RdpIdd.
Sunshine reads frames with `capture = remoteidd`.

**Answered by the real-seat test:** `rdpcorets` accepts a session whose display is our driver
instead of its own `RdpIdd`.

**Ruled out for this item:** injecting a DLL into Microsoft's `RdpIdd` and hooking its internal
functions by offset (DuoStream's actual mechanism) — fragile across Windows updates, exactly the
third-party/hack dependency the project avoids.
