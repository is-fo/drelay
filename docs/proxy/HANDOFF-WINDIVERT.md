# HANDOFF: a redirect that does not claim the address (WinDivert)

**Status (2026-10-17): implemented.** The redirector is back, in `proxy/redirector/`, and the launcher
owns its lifetime via `--redirect`. It is **not** a reimplementation of the program this document's
§3 describes: the design there - rewrite `ip.DstAddr` and re-inject - is the one that already failed
here (§1's evidence table and `PROTOCOL.md` §5), and it failed because an outbound packet has already
been routed when WinDivert sees it. The working mechanism is a **reflection** (§3 below). §1, the
finding that made this necessary, is unchanged and is still the reason the work was done.

What is verified, and what is not, is in §7.

**Where the old redirector lived:** the author kept it gitignored on the Windows 10 machine. It was
never in `src/` and never in the repository's history (checked: no path matching `redirect` was ever
added or deleted), so it was **not** recoverable. It was rewritten from the measurements recorded here
plus the WinDivert API. Its `WindivertDotnet` package and the WinDivert 2.2 binaries it downloaded
were still on this machine, which is what `redirector\build.ps1` now uses as a source.

---

## 1. The finding this document exists for

The proxy "works on the Windows 10 main machine, not on the Windows 11 laptop". The difference is
**not** the relay, the launcher, the hooks or the game. It is the operating system.

A claim assigns the game server's address (`18.145.161.25`) to the loopback interface, so the
client's dial to that address is delivered locally where the relay listens. The relay then reaches
the real server from a socket bound to the LAN address. On the laptop (Windows 11, build
`10.0.26200`) that upstream connection:

* completes the TCP handshake (SYN, SYN-ACK, ACK all appear on the wire),
* sends its first data segment,
* and that segment is **delivered back to the local stack and dropped**, while the stack ACKs it.

The server therefore never receives the client's `Hello`. It keeps the socket alive with its
keepalive `GmPing` (id `0x5C`) and says nothing else. The relay logs a flawless handshake and then a
quiet session; the client sits on its loading screen for ~20 s and is dropped. Nothing reports an
error at any layer, because from the sender's point of view the data was acknowledged.

### Evidence

Captured with `pktmon` while a **plain Python socket** (no relay, no game) bound to `192.168.0.11`
connected to the claimed `18.145.161.25:6410` and sent 135 bytes:

```
pktmon start --capture --type drop --pkt-size 0 --file drops.etl
pktmon etl2txt drops.etl --out drops.txt
```

```
Drop: ip: 192.168.0.11.53749 > 18.145.161.25.6410: Flags [P.], length 135
Drop: ip: 18.145.161.25.6410 > 192.168.0.11.53749: Flags [.], ack ..., length 0
```

The machine's **own outbound** segment, listed as a drop in the **Rx** direction - i.e. Windows
delivered the packet to itself and discarded it, then acknowledged it locally. The same test with
the claim released put the identical 135 bytes on the wire (four capture copies, none dropped).

Other things ruled out on the laptop:

| Hypothesis | Result |
|---|---|
| the relay (framing, ordering, buffering) | not involved: a plain socket reproduces it |
| the relay split a frame across segments | fixed this session (`writeFrame`, one write per frame) and verified; this drop is a separate cause |
| `WeakHostSend` on the loopback interface | set to `Disabled` and re-tested: no change (restored afterwards) |
| Tailscale WFP filters | service stopped and re-tested: no change |
| the server rejecting a stale token | a synthetic client with a fresh token *from `127.0.0.1`* is admitted normally through the same relay |

The last row matters: with the same relay and the same token flow, a client on `127.0.0.1` gets the
full admission (`GmDailyGold` 698 B, `GmMapInfo` "The Tavern", `GmHealthUpdate`, ...). A client whose
local endpoint is the claimed address gets keepalives only. The relay code path is identical; only
the *destination being a local address at send time* differs.

### The pass/fail probe

`proxy/tools/Test-ClaimDataPath.ps1` (elevated) reproduces the verdict for one route in seconds,
without the game and without a relay:

* it claims the route's address on loopback,
* connects from the LAN address to the real server,
* sends a real 108-byte game `Hello` carrying an **invalid** queue token,
* expects `GmFailure` (id `0x68`) "Invalid queue token." - which any server sends when it **parses**
  a Hello,
* and reports `FAIL` if the only thing that comes back is `GmPing`.

On the laptop it reports `FAIL: only keepalive Pings came back`. **Run it on the Windows 10 machine
first**: if it passes there, that is the confirmation that the two machines differ at the OS layer and
that the claim design is what has to change here.

---

## 2. What the replacement must do

The client must reach the relay **without the server's address being local**, so that the relay's own
dial to the real server is an ordinary, routable connection.

1. The game's outbound TCP to the game service must be diverted to the relay's listener on
   `127.0.0.1:6410` / `127.0.0.1:6411` (the relay already listens on `0.0.0.0`, so loopback works).
2. The destination address must **not** be claimed, and `Set-AddressClaim` must not be used for the
   `Game` / `Game_Slave` routes while the redirector runs.
3. The queue keeps the **hosts** capture (`Set-HostsEntry.ps1`). It is name-addressed, it already
   works on both machines, and it is unaffected by any of this.
4. The relay's upstream dial can stay bound to `upstreamHost` (harmless) or be left unbound; either
   way it is now a normal connection. `-Ddrelay.upstreamHost` and the "claims" checks become
   irrelevant for a redirect-only run.

The relay itself needs no protocol change. Two pieces of its behaviour do become wrong and must be
guarded:

* `Relay.onRetarget` currently **claims** the realm address named by a `GmReconnect` before forwarding
  it. With a redirector, claiming is exactly what breaks the data path, so this must be a no-op (or
  a config switch) while the redirector is active. The *learned destination* logic
  (`cfg.learned`, `Config.learned`) must stay: the relay still has to forward port 6411 to the realm
  address the server named.
* The launcher's `claimAddresses` / `releaseClaims` steps, and the claim half of `--stop` and
  `--release-claims`, should be skipped or made explicit when a redirector is in use.

---

## 3. Redirect design (WinDivert 2.x), as built

WinDivert is a WFP callout driver plus a user-mode library. It needs administrator rights (the
launcher already elevates) and needs `WinDivert.dll` + `WinDivert64.sys` beside the executable.
Implementation: `proxy/redirector/` (C#, .NET 8, direct P/Invoke, no NuGet dependency).

**Filter.** Match the two game ports in both directions, and never an address:

```
outbound and ip and tcp
  and (tcp.DstPort == 6410 or tcp.SrcPort == 6410
       or tcp.DstPort == 6411 or tcp.SrcPort == 6411)
  and not loopback
```

`6411` is matched **without** pinning the destination: the realm address is invented by the server per
transition (`GmReconnect`), so a filter that names `18.145.161.25` loses the session at the first realm
change. The source-port clauses are how the relay's answer finds the client. `not loopback` leaves the
hosts-captured queue route alone, since those packets are already going to a local listener.

**Reflection, not a destination rewrite.** THE ONE PLACE THIS DOCUMENT WAS WRONG. An outbound packet
has already been routed when WinDivert sees it, so rewriting `ip.DstAddr` and re-injecting it outward
cannot reach a local listener - that is the `seen=21 redirected=21` measurement above, not a checksum
problem. What works is the transformation WinDivert's own `streamdump` sample uses for exactly this
purpose:

1. swap `ip.SrcAddr` and `ip.DstAddr`;
2. clear the address's `Outbound` flag, so the packet re-enters the **receive** path;
3. recompute the IPv4 and TCP checksums (the TCP one covers the pseudo header, so it cannot be done
   incrementally);
4. `WinDivertSend` it back, reusing the captured `IfIdx`.

The ports are never changed, so the relay's reply is reflected by the same swap and the client sees a
reply from the server's address. Delivery then follows from the destination address being local, which
for the relay's `0.0.0.0:6410/6411` listener it is. `handleSession` forwards to the route's / learned
destination - unchanged code.

Two consequences, both handled:

* the relay's listener is now an **inbound** endpoint to Windows Firewall (the reflected packet comes
  from a public address), so the launcher adds one named inbound rule for the redirected ports while
  it runs and removes it on the way out;
* the relay must stop claiming a `GmReconnect` destination (`-Ddrelay.redirect=true`), because a claim
  is what makes that destination local again. The learned destination is still recorded.

**Loop prevention - the part that must not be skipped.** The relay's own upstream dials are also
outbound TCP to port 6410/6411. If the filter matches them, the relay dials itself and the cascade
described in `PROTOCOL.md` §5 returns (16 000 sessions in five minutes). Built: the owning process is
read from the IPv4 TCP table (`GetExtendedTcpTable`, i.e. `netstat -o`) the first time a connection is
seen and cached; packets owned by this process, by `--exclude-pid`, or by an executable named
`--exclude-image` (default `java.exe`, `javaw.exe` - the relay and the launcher) are re-injected
untouched. **Not** the pinned source port: Windows allows one connection per local port, so
`upstreamBindPort` serialised upstream dials and broke as soon as the queue and game overlapped. An
unattributable packet is passed through rather than reflected (fail closed), and a rate fuse (20 new
reflected connections/s) covers the case where that attribution is wrong.

Counters, as requested: `seen`, `reflected`, `returned`, `notTcp`, `fragmented`, `malformed`,
`reinjectFailed`, `selfMatch`, `unknownOwner`, `noMatch`, `passThrough`, `fuseTripped`, `recvError`.

**Where it runs.** In the launcher, started before the relay and stopped by the same disposal path as
the hosts block and the claims - `Launcher.startWithCapture` and `releaseCapture`
(`startWithClaims` before this change). A redirector left running after the relay exits points the
client at a dead listener, which is the same class of failure the hosts block caused; a launcher that
finds one from an earlier run stops it first (`Launcher.stopLeftoverRedirector`), and `--stop` does
the same. The launcher also watches it: if the redirector dies mid-session it says so, because with no
redirector in the path the game keeps working and only the proxying stops.

**Packaging.** `drelay-redirect.exe` (framework-dependent, single file, ~180 KB) is published beside
the jar into `proxy\target\redirector\` by `redirector\build.ps1`, together with the two WinDivert
files and a NOTICE. It is deliberately **not** inside the jar: the JVM cannot call `WinDivert.dll`,
and the binaries are third-party (`*.dll`/`*.exe` are ignored repository-wide). The launcher searches
`-Ddrelay.redirector`, `redirector\`, `target\redirector\` and `work\redirector\` beside the jar.

> **Release packaging is still a gap.** `proxy-release.yml` uploads `drelay.jar` only, so a downloaded
> release has no `redirector\` directory and `--redirect` stops with "the packet redirector is not
> installed" plus the build command. Building it in CI needs `actions/setup-dotnet` and a second asset
> (or a zip); that was not attempted here because it cannot be verified from this machine.

---

## 4. Test plan

Everything below runs without the game, and all but the last two run without elevation.

1. **OS probe (must fail on the laptop):**
   `powershell -ExecutionPolicy Bypass -File proxy\tools\Test-ClaimDataPath.ps1`
   Expect `FAIL ... only keepalive Pings` on Windows 11, `PASS ... parsed the Hello` on Windows 10.
2. **Relay forwarded a frame as one segment:** `python proxy\tools\tests\test_frame_chunking.py`
   (asserts 135 and 608 byte frames arrive upstream as one read each).
3. **Relay byte-exactness and framing:** `test_relay.py`, `test_relay_little_endian.py`,
   `test_relay_stress.py`.
4. **The redirector's own delivery property** (new, elevated, ~15 s, no game and no server):
   `powershell -ExecutionPolicy Bypass -File proxy\tools\Test-RedirectDataPath.ps1`
   It listens on 16410, points the redirector at that port, dials `192.0.2.1:16410` and requires the
   listener to accept a connection from that address and a byte to survive in both directions. This
   is the half that cannot be unit-tested, so run it before trusting a session.
5. **Redirector offline logic:** `proxy\target\redirector\drelay-redirect.exe --selftest` (49 checks:
   the transform, both checksums incl. odd lengths and TCP options, the filter, the TCP-table
   ownership lookup against a live socket, the image-name lookup, the fuse, the command line).
6. **Admission through the relay, no game.** The end-to-end check:
   * mint a token: connect to `127.0.0.1:6412`, send the queue Hello
     `01 06 "Zenith"` framed little-endian (`struct.pack("<i", 8)`), read `0x03 Join` and take its
     32-byte token;
   * send the client's 131-byte game Hello (`0x37`) through `127.0.0.1:6410` with that token
     substituted at the `0x20`-prefixed field;
   * the server answers `GmDailyGold` (`0x33`, 698 B), `GmMapInfo` (`0x05`, "The Tavern"),
     `GmHealthUpdate` (`0x46`).

   With the redirector running and no claim, run the same flow against the **real destination**
   instead of `127.0.0.1:6410`: the redirector should divert it, and the same admission should come
   back through the relay.
7. **No self-redirect:** after a run, the relay's session list must show exactly one session per
   client connection, and `selfMatch` in the redirector's stats must be non-zero while `reflected`
   tracks only real client dials.
8. **Live game:** the winner is `[Game#2 S->C] ... id=0x33 DailyGold` in
   `work/logs/relay-console.out` and `[Client] Reconnecting to <realm>:6411...` in
   `%LOCALAPPDATA%\RippleStudio\Darza\logs\darza.log`.

---

## 5. Things earlier sessions changed (do not redo)

* `Launcher.java`: captures are released in a `finally` **and** by a shutdown hook (`drelay-release`),
  once, via `releaseCapture`; the claim-refused path releases too; `--release-claims` clears the hosts
  block as well as the claims. Ctrl+C now leaves nothing behind (verified on the laptop).
* `Relay.java`: one write per frame (`writeFrame`) and `TCP_NODELAY` on both sockets. This was a real
  bug - the old code wrote the length and payload as two writes, which the game server's reader
  mishandled - and it is verified against the real server.
* `Sync.java`: a file byte-identical to the bundled copy is adopted as "ours", so helpers from an
  older jar are not frozen as user-owned forever.
* `Restore-HostsFile.ps1`: `File.Replace` with `[NullString]::Value` (a bare `$null` binds to `""` and
  fails with "the path is not of a legal form"), whole-block removal, `-HostsPath`.
* `Set-HostsEntry.ps1`: `-Status`/`-Release` no longer need a route table.
* `Test-ClaimDataPath.ps1`: the OS probe above.

None of these were wasted by the redirector work: the disposal path, the framing and the hosts
handling stay exactly as they are.

---

## 6. Open questions

1. Does `Test-ClaimDataPath.ps1` pass on the Windows 10 machine? (Confirms the OS difference and that
   the desktop does not need the redirector. Still unanswered - this session ran on the laptop.)
2. Which lifecycle wins: redirector in the launcher (one process tree, one cleanup) or a standalone
   service? **Answered: the launcher.** It is started before the relay and stopped by `releaseCapture`,
   which is the same idempotent path the claims and the hosts block use.
3. Is the old filter process-aware already, or does it rely on port-only matching? **Answered: it was
   port-only plus `upstreamBindPort`.** The new one is process-aware via `GetExtendedTcpTable`; the
   pinned port is gone.
4. Does the client ever dial the game address over IPv6? The filter is IPv4-only and logs nothing for
   IPv6; the address is an IPv4 literal, and `notTcp` plus `malformed` cover the rest. Still worth a
   `--dry-run` minute on a live client before trusting it.

---

## 7. What this session verified, and what it did not

Verified on the Windows 11 laptop, unelevated:

* `drelay-redirect.exe --selftest` - 49 checks, including the TCP-table ownership lookup, which is the
  loop guard's whole basis, and the `QueryFullProcessImageNameW` lookup.
* `proxy\build.ps1` - compiles, packages, and runs all the offline checks (`RoutesTests` gained the
  claim/hosts port-division test).
* `proxy\tools\Test-RedirectDataPath.ps1` parses and refuses to run unelevated, as intended.

Verified on the same machine, in an elevated run that happened during this session with the **old**
build (it crashed on `QueryFullProcessImageName`, since fixed):

* WinDivert 2.2 installed and loaded: the banner printed `winDivert: 2.2` and the filter was accepted.
* The filter matched real traffic: `seen=1` on the first packet, which was the relay's own preflight
  dial - i.e. exactly the packet the loop guard exists for.
* The launcher started the redirector before the relay, read its verdict from `work\redirector.ready`,
  and started the relay only after the driver was open.

**Not verified, and the reason this is still a handoff:** the reflected packet actually arriving at the
relay's listener, and a live game session through it. Both need the elevated probe (§4.4) and a game
run (§4.8); neither was done here because this session could not elevate without a prompt. The
mechanism is WinDivert's own documented `streamdump` technique rather than an invention, but "documented
elsewhere" is not the same as "measured here", and the previous design was also believed to work.

Two bugs were found by running it rather than by reading it, and are the argument for running §4.4
before a game session:

* `QueryFullProcessImageName` has no entry point - it is `...W` - so the redirector crashed on the
  first packet it had to attribute. The selftest now calls that lookup directly.
* `drelay.ps1` swallowed everything the jar printed, because `$code = Invoke-Jar ...` captures the
  function's whole output stream. Fixed; the redirector's log and the relay's console now reach the
  terminal through `drelay.ps1`.
