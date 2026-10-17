# drelay-redirect

The packet redirector that puts the relay in the client's path **without claiming the game server's
address**.

It exists because of a measured Windows 11 failure: with the server's address claimed on the loopback
interface, the relay's own upstream connection completes its handshake and then has its first data
segment delivered back to the local stack and dropped, while the stack acknowledges it. Nothing
reports an error and the server never sees the client's `Hello`. The whole measurement is in
[`docs/proxy/HANDOFF-WINDIVERT.md`](../../docs/proxy/HANDOFF-WINDIVERT.md) §1.

---

## What it does

It is a WinDivert 2.2 program. It captures the game's outbound TCP on the game's ports and hands each
packet to the relay's listener **as though the game server had sent it**:

```
client                                   relay's listener
192.168.0.11:53749 -> 18.145.161.25:6410     0.0.0.0:6410

        swap the two addresses, clear the outbound flag, recompute the checksums

18.145.161.25:53749 -> 192.168.0.11:6410     -> accepted
```

The ports are never changed, so the relay's own answer comes back the same way: its packet
(`192.168.0.11:6410 -> 18.145.161.25:53749`) is swapped and re-injected, and the client sees the reply
it expects from the server. Neither end knows the relay is between them, and the server's address is
never made local, so the relay's upstream dial is an ordinary connection.

**Why a reflection and not a destination rewrite.** An outbound packet that WinDivert has already
captured has already been routed. Cleaning up its destination address and re-injecting it outward
therefore sends it out of the network card - it is not re-routed and it never reaches a local listener.
That is exactly what the previous redirector did (`seen=21 redirected=21`, zero relay sessions,
recorded in `docs/proxy/PROTOCOL.md` §5), and it is why the claim replaced it. Re-injecting inward
instead puts the packet back into the receive path, where delivery follows from the destination
address being local - which for the relay's `0.0.0.0` listener it is. WinDivert's own `streamdump`
sample uses the same trick to divert a stream into a local proxy.

---

## The loop guard, which is the part that must be right

The relay's own upstream dials are outbound TCP to the same two ports, so they match the filter too.
Reflecting one of them makes the relay a client of itself; each reflected connection makes another
dial. The previous design pinned the relay's upstream dials to one source port and excluded that port,
which both serialised upstream dials (Windows allows one connection per local port) and broke as soon
as two sessions overlapped.

This one asks Windows who owns the socket - the IPv4 TCP table that `netstat -o` prints - and never
reflects a packet owned by:

* this process,
* a pid given with `--exclude-pid`, or
* an executable named with `--exclude-image` (the default is `java.exe` / `javaw.exe`, which is the
  relay and the launcher).

The owner is read when a connection is first seen and cached for two minutes, so steady-state traffic
is a dictionary lookup. A packet whose owner cannot be determined is **passed through untouched** -
the client then reaches the real server directly, which is a visible loss of proxying rather than a
self-connection storm. A rate fuse (20 new reflected connections per second, `--fuse`) is the belt to
that brace: a burst beyond it is a fault, not load.

---

## Build

```powershell
.\proxy\redirector\build.ps1
```

It publishes `drelay-redirect.exe` into `proxy\target\redirector\` and stages `WinDivert.dll`,
`WinDivert64.sys` and `NOTICE.txt` beside it. Needs the .NET 8 SDK.

The WinDivert binaries are **not in this repository** (`*.dll` and `*.exe` are ignored: third-party
binaries are not ours to publish). The build takes them, in order, from `-WinDivertDir`, from an
existing WindivertDotnet extraction under `%APPDATA%\WindivertDotnet\v222\x64`, or from the official
WinDivert 2.2 A release, which it downloads into `redirector\.cache\`.

---

## Run

Normally the launcher starts and stops it, which is what keeps the lifetimes tied together:

```powershell
.\proxy\drelay.ps1 -Redirect
```

By hand, elevated:

```powershell
.\proxy\target\redirector\drelay-redirect.exe --ports 6410,6411 --stats-interval 5
```

| Option | Effect |
|---|---|
| `--ports <a,b,...>` | ports to intercept (default `6410,6411`) |
| `--exclude-pid <n>` | never reflect this process's packets (repeatable) |
| `--exclude-image <name>` | never reflect this executable's packets (default `java.exe`, `javaw.exe`) |
| `--no-default-excludes` | do not exclude the java names |
| `--fuse <n>` | pass through after `n` new connections in one second (default 20; 0 disables) |
| `--stats-interval <s>` | seconds between `[stats]` lines (default 5; 0 prints only at exit) |
| `--samples <n>` | describe the first `n` reflected packets (default 10) |
| `--dry-run` | log the decision for every packet, reflect nothing |
| `--ready <path>` | write the driver's verdict to a file (how the launcher knows it came up) |
| `--selftest` | run the offline checks and exit; no driver, no elevation |

Exit codes: `0` ran and was asked to stop, `2` usage, `3` the driver could not be opened (access
denied, or a missing/stale WinDivert pair beside the exe).

---

## Verifying it

`--selftest` covers everything that does not need the driver - the packet transform, both checksums
including odd payload lengths and TCP options, the filter, the TCP-table ownership lookup against a
live socket, the image-name lookup, the fuse and the command line. It runs unelevated:

```powershell
.\proxy\target\redirector\drelay-redirect.exe --selftest
```

The property that does need the driver - *a reflected packet reaches a local listener, and the answer
gets back* - is measured by a probe that needs no game, no server and no relay:

```powershell
.\proxy\tools\Test-RedirectDataPath.ps1        # elevated
```

It listens on port 16410, points the redirector at that port only, dials `192.0.2.1:16410` (TEST-NET-1,
so nothing real can answer), and checks that the listener accepts a connection from that address and
that bytes survive the round trip in both directions. It also adds and removes the inbound firewall
rule the redirected connection needs.

---

## Reading the counters

| Counter | Meaning |
|---|---|
| `seen` | packets the filter matched. Zero means the filter is wrong, not the transform. |
| `reflected` | packets handed to the relay's listener (client -> relay). |
| `returned` | packets handed back to the client (relay -> client). `reflected` without `returned` is a one-way proxy. |
| `selfMatch` | the loop guard refused a packet owned by an excluded process. Rising here is the guard working. |
| `unknownOwner` | Windows would not name the owning process, so the packet was passed through. Rising here means the proxy is not in the path for those connections. |
| `fuseTripped` | a new-connection burst exceeded `--fuse` and was passed through. |
| `reinjectFailed` | WinDivert refused to take a packet back. Traffic is being lost. |
| `notTcp`, `fragmented`, `malformed` | traffic the filter matched but the transform cannot use; always passed through untouched. |
