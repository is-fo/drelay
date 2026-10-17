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

## 1. The game closes a few seconds after launch, and `hook.log` stops right after `PATCHED 'frameTick'`

**Symptom.** The launcher shows the hook installing, `PATCHED 'frameTick'`, then the game is
gone. A `crash.txt` appears in the run folder.

**Read the launcher's console, not `crash.txt`.** The end of the console says:

```
CLR: Assert failure(PID 13440 [0x00003480], Thread: 9864 [0x2688]):
     pThread->HasThreadState(Thread::TS_Hijacked)
    File: ...\src\coreclr\vm\excep.cpp
```

and Event Viewer → **Windows Logs → Application** has an `Application Error` naming
**`coreclr.dll`** with exception code **`0xC0000602`**.

**What it is.** The .NET runtime hits one of its own internal assertions and fail-fasts the
process. This is a race inside the runtime's thread suspension (a thread being stopped by the
garbage collector), not a bug in the target patch and not a bad offset. It only happens while
the hook's managed half is loaded; the game on its own does not do it.

**What actually helps**

1. **Confirm the boundary.** Run the game with the hook's native half only:

   ```powershell
   .\Drelay.Launcher.exe --off
   ```

   The game then runs normally. That tells you the crash belongs to the hook's managed half,
   not to the game, the relay or the install. It does **not** give you a working hook —
   `--off` injects nothing and patches nothing.

2. **Relaunch.** It is a race, not a fixed point: on the machine this was measured on
   roughly half of launches died at 4–15 s and the rest ran on. A launch that survives the
   loading screen usually stays up.

3. **Do not chase these — they were each measured and each ruled out** (25–30 s per trial,
   same machine, same game build):

   | changed | result |
   |---|---|
   | game alone, no hook | 7/7 survived |
   | `--off` (native only, nothing managed) | 3/3 survived |
   | `DRELAY_NO_PATCH=1` (nothing patched) | still died |
   | `DRELAY_FORWARD_ONLY=1` (JIT detour reduced to a forwarder) | still died |
   | `DRELAY_SKIP_JIT_DETOUR=1` (no JIT detour at all) | still died |
   | no arming probe / no resolve callback / auto-aim and dodge off | still died |
   | `DOTNET_gcConcurrent=0` | still died |

   So this is **not** the patch, **not** the JIT detour, **not** the config, **not** the
   environment. It is the managed bootstrap's presence in the process, and the fix has to be
   a code change there.

   **The work to close it is specified in
   [`docs/hook/HANDOFF-TS-HIJACK-CRASH.md`](docs/hook/HANDOFF-TS-HIJACK-CRASH.md)** — the
   experiment to run, the acceptance criteria, and the traps that make a single clean launch
   look like a fix when it is not.

---

## 2. The `crash.txt` trap

`crash.txt` is written by a vectored exception handler, so it records exceptions that reach
it — not the reason the process died. Two things it will show you that are **not** crashes:

| in `crash.txt` | what it really is |
|---|---|
| `exception 0x80000033  unclassified` | the runtime's own *return-address hijack attempt* — a normal step in stopping a thread. Seen by the handler and recorded; harmless in itself |
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

## 7. Collecting something worth reporting

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
