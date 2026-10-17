# HANDOFF: a redirect that does not claim the address (WinDivert)

**Status:** the claim-based capture cannot forward traffic on this Windows 11 machine. The redirector
work is not in this repository - the previous one was removed (see §7 of
[PROTOCOL.md](../../docs/proxy/PROTOCOL.md)). This document is the starting point for bringing it
back, and it records why it is needed now.

**Where the old redirector lives:** the author keeps it **gitignored on the Windows 10 machine**. It
is not in `src/`, so it has to be recovered from there before this can be built.

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

## 3. Redirect design (WinDivert 2.x)

WinDivert is a WFP callout driver plus a user-mode library; it is what the previous redirector used.
It needs administrator rights (the launcher already elevates) and ships `WinDivert.dll` +
`WinDivert64.sys` beside the jar.

**Filter.** Match only what the game sends to the game service:

```
outbound and ip and tcp
  and (tcp.DstPort == 6410 or tcp.DstPort == 6411)
  and not ip.DstAddr == 127.0.0.1
```

`6411` must be matched **without** pinning the destination: the realm address is invented by the
server per transition (`GmReconnect`), so a filter that names `18.145.161.25` will lose the session at
the first realm change. Port-based matching plus a process check is the right shape.

**Rewrite.** Set `ip.DstAddr = 127.0.0.1`, keep the destination port, recompute the checksums
(`WinDivertHelperCalcChecksums`), and re-inject with `WinDivertSend`. The relay's listener on
`0.0.0.0:6410/6411` accepts it and `handleSession` forwards to the route's / learned destination -
unchanged code.

**Loop prevention - the part that must not be skipped.** The relay's own upstream dials are also
outbound TCP to port 6410/6411. If the filter matches them, the relay dials itself and the cascade
described in `PROTOCOL.md` §5 returns (16 000 sessions in five minutes). Options:

* match by **process**: `WinDivertOpen` at `WINDIVERT_LAYER_FLOW` reports the flow's process id
  (`WINDIVERT_FLOW_DATA.processId`); keep a set of the game's pids and redirect only those, or
  conversely never redirect a pid that belongs to the relay/launcher (java.exe).
* or exclude the relay's source port range / use `WinDivertOpen` with a filter that excludes
  `ip.SrcAddr == <LAN address> and tcp.SrcPort in <the relay's ephemeral range>` - weaker, because
  the range is not fixed.

The previous redirector's counters are worth reproducing verbatim: they are what identified its
failures (`PROTOCOL.md` §5, "Why the packet redirect was abandoned"). At minimum:
`seen`, `redirected`, `notTcp`, `reinjectFailed`, and a `selfMatch` counter for the loop guard.

**Where to run it.** In the launcher, before the relay starts, as a thread/child with a clean
shutdown, and make its lifecycle part of the same disposal path as the hosts block today (the
launcher already has a shutdown hook for that - see `Launcher.startWithClaims` and
`releaseCapture`). A redirector left running after the relay exits would point the client at a dead
listener, which is the same class of failure the hosts block caused.

**Packaging.** The jar is dependency-free by design (`build.ps1` uses `javac` + `jar` only), so the
redirector is either:

* a small native helper (`windivert-redirect.exe`) written in C/C++, shipped beside the jar and
  started by the launcher, or
* a JNI/JNA-free Java implementation is not realistic: `WinDivert.dll` needs a native call layer.

The first is the pragmatic choice and matches how the previous redirector worked.

---

## 4. Test plan

Everything below runs without the game, and the first three already exist.

1. **OS probe (must fail on the laptop):**
   `powershell -ExecutionPolicy Bypass -File proxy\tools\Test-ClaimDataPath.ps1`
   Expect `FAIL ... only keepalive Pings` on Windows 11, `PASS ... parsed the Hello` on Windows 10.
2. **Relay forwarded a frame as one segment:** `python proxy\tools\tests\test_frame_chunking.py`
   (added this session; asserts 135 and 608 byte frames arrive upstream as one read each).
3. **Relay byte-exactness and framing:** `test_relay.py`, `test_relay_little_endian.py`,
   `test_relay_stress.py`.
4. **Admission through the relay, no game.** This is the check that proves the redirector end to end:
   * mint a token: connect to `127.0.0.1:6412`, send the queue Hello
     `01 06 "Zenith"` framed little-endian (`struct.pack("<i", 8)`), read `0x03 Join` and take its
     32-byte token;
   * send the client's 131-byte game Hello (`0x37`) through `127.0.0.1:6410` with that token
     substituted at the `0x20`-prefixed field;
   * the server answers `GmDailyGold` (`0x33`, 698 B), `GmMapInfo` (`0x05`, "The Tavern"),
     `GmHealthUpdate` (`0x46`).

   With a redirector in place and no claim, run the same flow against the **real destination**
   instead of `127.0.0.1:6410`: the redirector should divert it, and the same admission should come
   back through the relay.
5. **No self-redirect:** after a run, the relay's session list must show exactly one session per
   client connection, and the dashboard's counters must not be growing on their own.
6. **Live game:** the winner is `[Game#2 S->C] ... id=0x33 DailyGold` in
   `work/logs/relay-console.out` and `[Client] Reconnecting to <realm>:6411...` in
   `%LOCALAPPDATA%\RippleStudio\Darza\logs\darza.log`.

---

## 5. Things this session already changed (do not redo)

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

None of these are wasted by the redirector work: the disposal path, the framing and the hosts
handling stay exactly as they are.

---

## 6. Open questions for the next session

1. Does `Test-ClaimDataPath.ps1` pass on the Windows 10 machine? (Confirms the OS difference and that
   the desktop does not need the redirector.)
2. Which lifecycle wins: redirector in the launcher (one process tree, one cleanup) or a standalone
   service? The launcher's shutdown hook is the natural owner.
3. Is the old filter process-aware already, or does it rely on port-only matching? If port-only, the
   loop guard has to be designed before it is trusted.
4. Does the client ever dial the game address over IPv6? (The address is an IPv4 literal, so no, but
   the redirector should log any non-matching TCP it sees for a minute before it is trusted.)
