# Troubleshooting

Two halves, and they fail very differently:

| piece | what it is | where it reports |
|---|---|---|
| **relay** — `drelay.jar` | the proxy, between the client and the game servers | the console it was started from |
| **hook** — `Drelay.Launcher.exe` | injects into `DarzasDominion.exe` | the launcher's console (live), plus a run folder per launch: `%LOCALAPPDATA%\drelay\hook\<date>-<time>-p<pid>\` |

The single most useful habit: **read the launcher's console first.** It echoes the hook log
live and prints the CLR's own fatal message, which is the only place the real reason for an
early death appears.

---

## 1. The game closes a few seconds after launch (fixed in hook 0.1.2)

**Symptom.** The launcher shows the hook installing, `PATCHED 'frameTick'`, then the game is
gone. In 0.1.1 a `crash.txt` appeared in the run folder.

**Read the launcher's console, not `crash.txt`.** The end of the console says:

```
CLR: Assert failure(PID 13440 [0x00003480], Thread: 9864 [0x2688]):
     pThread->HasThreadState(Thread::TS_Hijacked)
    File: ...\src\coreclr\vm\excep.cpp
```

and Event Viewer → **Windows Logs → Application** has an `Application Error` naming
**`coreclr.dll`** with exception code **`0xC0000602`**.

**What it was.** The .NET runtime hits one of its own internal assertions and fail-fasts the
process. The assertion is on the runtime's *return-address hijack* path — the mechanism it uses to
stop a thread that is in cooperative GC mode. It fires when a thread receives the runtime's own
hijack signal (`STATUS_RETURN_ADDRESS_HIJACK_ATTEMPT`, `0x80000033`) while the runtime's
`TS_Hijacked` flag is not set, which is how the runtime proves "this hijack was mine".

The hook was provoking it. Its crash reporter installs a vectored exception handler at the front of
the chain, and that handler treated `0x80000033` as a fault worth recording. The exception is raised
on a thread the GC has *just* hijacked, while the runtime is waiting for that thread to reach a safe
point. Recording it meant a stack walk and up to four file writes on exactly that thread — the kind
of delay a thread-suspension race does not tolerate — and the report it wrote was the misleading
`0x80000033` record that §2 has been telling operators to ignore.

**The fix (hook 0.1.2).** `DrelayCrash.cpp` now returns `EXCEPTION_CONTINUE_SEARCH` for
`0x80000033` without doing any work. Measured on the machine that failed: **5 of 5 launches
survived 60 s** with the default configuration, where 0.1.1 died in roughly one launch in three and
in 5/5 with the managed frame replacement switched off. The five trials reached `phase 1 complete`
and `PATCHED 'frameTick'`; `crash.txt` is no longer written for a hijack signal.

**If it still happens.** A relaunch is still worth one try (a race is a race), but do not stretch it
to five and do not chase the old list below. Collect the launcher console, `hook.log`,
`bootstrap.log` and the event-log entry as in §9, and check that `VERSION.txt` says 0.1.2 or later —
the 0.1.1 package keeps failing by design.

**What was ruled out under 0.1.1** (each measured on the same machine and build, 25–30 s per
trial). They are recorded so a future session does not spend the same rounds on them:

| changed | result |
|---|---|
| game alone, no hook | 7/7 survived |
| `--off` (native only, nothing managed) | 3/3 survived |
| `DRELAY_NO_PATCH=1` (nothing patched) | still died |
| `DRELAY_FORWARD_ONLY=1` (JIT detour reduced to a forwarder) | still died |
| `DRELAY_SKIP_JIT_DETOUR=1` (no JIT detour at all) | still died (1/5 in the confirming run) |
| `DRELAY_SKIP_RESOLVE_CALLBACK=1` (no managed callback) | still died (2/5) |
| auto-aim and dodge off (no managed frame replacement) | still died (1/5) |
| `DOTNET_gcConcurrent=0` | pooled 5/7, inside the noise |
| `--cores 4` (`DOTNET_PROCESSOR_COUNT=4`) | still died (1/5) |

So it was **not** the patch, **not** the JIT detour, **not** the config, **not** the thread count and
**not** the environment. It was the crash reporter's handler on the hijack path — a code change in
`DrelayCrash.cpp`, which is where 0.1.2 makes it.


---

## 2. The `crash.txt` trap

`crash.txt` is written by a vectored exception handler, so it records exceptions that reach
it — not the reason the process died. As of **0.1.2 the handler ignores `0x80000033` outright**
(§1), so that line no longer appears at all; a file that still contains it is from an older build.
Two things it will show you that are **not** crashes:

| in `crash.txt` | what it really is |
|---|---|
| `exception 0x80000033  unclassified` | the runtime's own *return-address hijack attempt* — a normal step in stopping a thread. **0.1.2 and later do not record it**; on 0.1.1 it is harmless in itself, and recording it was the crash |
| `jit-internal.txt` with `0x02345678` | the JIT's private "give up on this method, retry" channel, which the JIT catches itself |
| `0xC0000005` in `Drelay.Hook.dll` frames | often a fault the handler saw *inside the handler's own stack walk* |

The authoritative record of a fatal .NET error is the **event log** (`0xC0000602` or
`0x80131506` naming `coreclr.dll`), plus the launcher console.

**Fix:** stop reading `crash.txt` as the cause. If `hook.log` has written its final lines
(`phase 1 complete`, `N target(s) patched`) then the run worked and the fault is elsewhere.

---

## 3. "0 of 6 target(s) patched" / every target `MISSING`

**Symptom.** The hook installs and the run completes, but no target is found.

**What it is.** Almost always: the character never got past the loading screen or the menu.
`spawn`, `collision`, `escape`, `movementIntegrator` and `aimTarget` are compiled on **first
use in gameplay**; sitting in a menu compiles none of them.

**Fix.** Get the character into a zone, then re-run. To tell "never compiled" from "compiled
but the IL differs", read the `target-size` lines in `hook.log`:

| line | meaning |
|---|---|
| `ZERO compiled` | the game never ran that code in this run — not a hook problem |
| `compiled N time(s) AND MATCHED` | the target is this build's method |
| `IL size 335 matches target 'escape' but the hash does not` | a method of the same size exists; this game build is not the pinned one — re-verify with `tools\Fingerprint.ps1` |

---

## 4. The launcher prints `FAILED:` and exits before the game starts

**Symptom.** One line, no game, exit code non-zero.

**Causes and fixes**

| message mentions | fix |
|---|---|
| the run directory could not be created | pass `--runroot <writable folder>`, e.g. `--runroot C:\drelay-runs` |
| the game could not be found | pass `--game "C:\Program Files (x86)\Steam\steamapps\common\Darza's Dominion"` |
| the discovered install was refused | discovery found the `OpenGL\` apphost; pass `--game` explicitly |
| a configuration field was refused | fix the value in `%LOCALAPPDATA%\drelay\hook\drelay.config.json` — a misspelled `AutoAim` is refused on purpose |

---

## 5. The launcher waits forever at "native ready"

**What it is.** The hook DLL did not get to run inside the game. In practice this is an
**elevation mismatch**: a launcher at a lower integrity level cannot inject into an elevated
game.

**Fix.** Start Steam/the game and the launcher at the same elevation. If it persists, check
that `Drelay.Hook.dll`, `Drelay.Bootstrap.dll` and `Drelay.Launcher.exe` are all in the same
folder (the launcher defaults to the DLLs beside itself).

---

## 6. The configuration file

`%LOCALAPPDATA%\drelay\hook\drelay.config.json` is written by the first no-argument run and
**never overwritten** — editing it is how a session is described from then on.

| want | set |
|---|---|
| auto-aim off | `"AutoAim": null` |
| dodge decides but never moves the player | `"Dodge": { "Act": false }` |
| dodge off entirely (cheapest run) | `"Dodge": { "Enabled": false }` |

Leave `"GamePath": ""` to let discovery find the install. A value that is wrong is refused at
startup, naming the field — that is deliberate.

---

## 8. The relay runs, the hook runs, but the game never gets past the loading screen

**Symptom.** The game stays on the loading/login screen and eventually has to be closed. The
hook is happy — `hook.log` ends with `phase 1 complete`, `frameTick` is found and patched —
but `summary.txt` says `arm state : WAITING (no world has existed yet)`. The relay console
shows a `Queue` session that **connects, receives one packet, and then dies**; the client
never dials 6410 at all.

**The relay's event log says which half is broken** (`work/logs/events-<run>.jsonl`):

```
"session"  "Queue#N"  connected ...
"packet"   "dir":"C->S"  "pkt":"Hello"  "len":8  "hex":"01065A656E697468"
"error"    "S->C stream error: Connection reset"
```

`packetsToServer: 1, packetsToClient: 0` on the close is the finding: the login packet went
upstream and nothing came back. The client is waiting for the server's `QJoin`.

**The cause is the claimed address, not the queue service.** Verified on this machine:

| setup | result |
|---|---|
| nothing claimed, relay running, login packet sent to `127.0.0.1:6412` | **server answers** `QJoin` (id 3, 34 bytes) — the relay forwards the frame byte-for-byte |
| `44.242.119.50` claimed, relay started with `upstreamHost` = LAN address | session opens, hello forwarded, **nothing ever comes back** — no reset, no reply |
| `44.242.119.50` claimed, relay started **without** `upstreamHost` | the relay's own dials are delivered back into its own listener: an **exponential cascade** (16 000+ sessions in five minutes) and the real client gets nothing |

A claimed address is a **local** address, so the relay's own upstream dial to that address
never reaches the internet — it is either delivered back into the relay's listener or its
replies never arrive. `upstreamHost` stops the cascade (the bind keeps the relay out of its
own listener) but it does not deliver the session: `Get-NetTCPConnection` shows the upstream
socket correctly bound (`192.168.0.11:xxxx -> 44.242.119.50:6412`, `Established`) and still
nothing comes back.

**The relay's startup probe does not catch this.** It prints

```
[Queue] destination OK: 44.242.119.50:6412 - the dial left the host and the real server answered
```

which only proves a TCP connect succeeded — not that the connection reached the server. Read
it as "the port is up", never as "this route works". The relay *does* refuse a bad layout at
startup when `upstreamHost` is missing (`Refusing to start: 1 route destination(s) are
claimed by this machine, and no upstreamHost is set`), so a relay that starts is not proof of
anything either.

**Fix — capture the name, not the address.** A route the client reaches by a *name* can be
redirected without touching the server's address, which is the conflict above. The launcher
now does that on start and undoes it on stop; the tool on its own is:

```powershell
# elevated (it writes the hosts file); -Status and -WhatIfOnly need no elevation
.\tools\Set-HostsEntry.ps1 -Config .\relay-routes.json     # write the block
.\tools\Set-HostsEntry.ps1 -Status                          # what is captured now
.\tools\Set-HostsEntry.ps1 -Release                         # give it back
```

It writes ONE marked block (`# >>> drelay (hosts capture) >>>`) mapping
`queue.playdarzas.com` to `127.0.0.1`, keeps a `<hosts>.drelay-backup`, replaces the file
atomically, and reads it back to verify. `tools\Restore-HostsFile.ps1` removes the same block
if anything goes wrong.

`-Status` and `-Release` work **without** a route table: the block is self-describing, so removing
it is a pure hosts-file edit. That is deliberate - running `-Release` from a directory that has no
`relay-routes.json` used to exit 2 with "route table not found" and leave the block behind, which is
exactly the state where the queue name still points at a dead `127.0.0.1`. `drelay.jar
--release-claims` now clears the block as well as the addresses, and the launcher gives both back on
a normal stop **and** from a shutdown hook, so Ctrl+C or a closed console window no longer leaks
them.

`Set-AddressClaim.ps1` now **skips every route the hosts block covers** — the two mechanisms
cannot fight over a route. A route the client reaches by a bare IP (`refresh: "serverlist"`,
which is `Game` and `Game_Slave`) cannot be captured this way at all: a hosts file cannot
redirect an IP. Those routes still take the claim, and the claim still has the conflict
above, so **expect game traffic to bypass the relay** until a capture that does not shadow
the destination exists. The login path — which is what stopped the game entering — is fixed
by the hosts block alone.

**Second cause, and the one that looks like a server rejection: a frame split across two TCP
segments.** If the relay's log shows a clean `Queue` Hello/Join and then a `Game` session whose only
traffic is `Ping`/`Pong` — no `DailyGold`, no `MapInfo`, no `Reconnect` — the server is not refusing
the client, it is **desynced**. It read the relay's 4-byte length prefix as a whole frame and then
parsed the payload (`37 00 41 …`, i.e. a length of ~920 million) as the next header, and it waits for
that payload while its keepalives keep the socket healthy. The client sits on the loading screen and
is dropped ~20 s later.

The client's own log is the fastest confirmation; it is
`%LOCALAPPDATA%\RippleStudio\Darza\logs\darza.log`:

```
[Telepathy] Connecting to Server 18.145.161.25:6410
[Client] Connected
...                                    <- 20 s of nothing
[Client] Disconnected from server @ 18.145.161.25.
```

Against a healthy direct connection the same log continues within a second or two with
`[Client] Reconnecting to <realm>:6411...` and `[Client] Reconnect connected, sending hello.` — that
realm transition is the proof the session was admitted.

The fix is in the relay, not in the game or the network: `Relay.pump` writes each frame with a
single `write()` and both sockets run `TCP_NODELAY`, so one frame is one segment exactly as the
client sends it. Before that fix the length and payload were two writes, which is a *race* — it
coalesced on some hosts and split on others, which is why the same build appeared to work on one
machine and fail on another. `proxy\tools\tests\test_frame_chunking.py` now pins it.

**Third cause, machine-specific: the OS discards data sent to a claimed address.** If the symptom is
"relay connects, the server answers, but only `Ping`s ever come back", the capture may be fine while
the *upstream* is impossible on that machine. A claim assigns the server's address to the loopback
interface; the relay then dials the server from the LAN address. On Windows 11 build 10.0.26200 that
dial's handshake completes and every data segment is then delivered back to the local stack and
dropped — while the server's keepalive `Ping` keeps both ends looking healthy. `pktmon
start --capture --type drop` shows it directly:

```
Drop: ip: 192.168.0.11.53749 > 18.145.161.25.6410: Flags [P.], length 135
```

(the machine's *own outbound* segment, listed as an Rx drop). It reproduces with a plain socket and
no relay at all, and releasing the claim before the send makes the same bytes appear on the wire, so
it is the OS, not the relay. It is also independent of `WeakHostSend`, Tailscale and the firewall.

`tools\Test-ClaimDataPath.ps1` (elevated) answers the question for one route in a few seconds: it
claims the address, sends a real game `Hello` with an invalid token from the LAN source, and expects
the server's `GmFailure` "Invalid queue token." — a reply proves the payload travelled, only `Ping`
proves it did not. Run it on two machines to compare: a machine where it passes can run the claim
design, one where it fails cannot, and the fix there is a capture that does not claim the
destination (a packet-layer redirect or an in-process `Connect` redirect), not a relay change.

**If the game still does not enter**, check the two things in order: `Set-HostsEntry.ps1
-Status` shows the block, and the relay's event log shows a `Queue` session with
`packetsToClient: 1` (the server's `QJoin`) instead of `0`.

**Before you test the game without the proxy**, remember to release the block: with the name
pointed at `127.0.0.1` and no relay listening, the client is refused. That looks like a
completely different bug.

---

## 9. Collecting something worth reporting

From a single launch, in the run folder, in this order:

1. the launcher's **console output** (copy the last ~40 lines)
2. `hook.log` — ends with `phase 1 complete` on a good run
3. `bootstrap.log` — the managed side; its **first line must predate the game's first log line**
4. `summary.txt`, `selfcheck.txt`, `offsets.csv` — present only if the evidence window closed
5. the **event log** entry for the death, if any
6. `crash.txt` / `jit-internal.txt` — last, and with §2 in mind

---

## 8. The relay: "Proxy seems to work fine"

If the relay is the suspect, its console is the whole evidence: it prints each route, the
one-way dial probe per destination (`destination OK`), and then every accepted connection as
`[Game#1]`, `[Queue#1]`, … A missing `destination OK` means the machine cannot reach the game
server, which is a network/hosts problem, not a relay bug. `tools\Restore-HostsFile.ps1`
undoes the hosts file edit if a previous run left one behind.
