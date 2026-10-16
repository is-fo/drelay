# Handoff — launching the hook without a command line

**Status: BUILT.** §15 is the as-built record: the files, the two places the implementation reads
this document differently than §4.1 and §11 state, and the check that covers each decision. §16
records the two deliberate departures that the first live no-argument runs caused — the template is
written on any first no-argument run (§6) and it ships with auto-aim and the live dodge on (§9.3).
§1–§14 stand as the specification and are what the implementation is measured against.

**Original status line, kept because it is what the round was written against: designed, not
built.** This is the specification for the release-facing launcher: a
configuration file, a precedence rule, zero-argument launching, and game-path auto-discovery. It is
written so that a session with none of this context can implement it, and so that every decision in
it can be argued with before a line is written.

It answers the question "how does a user who downloaded `drelay-hook-<version>.zip` actually start
this thing", which is currently answered by *"go and find the arguments in the README"*.

Input to this document: a proposal from an outside model ("layer your configuration, bind JSON with
`System.Text.Json`, add a first-run experience, and prepare a named-pipe bridge"). §9 records what
was taken, what was rejected and why. The operator's verdicts on the nine open questions are in §1.

Related: [`HOOK-BUILD-MATRIX.md`](HOOK-BUILD-MATRIX.md) (the gate and what a maintainer must not
simplify), [`JAVA-BRIDGE.md`](../bridge/JAVA-BRIDGE.md) (the transport that was chosen and why),
[`PHASE1-HANDOFF.md`](PHASE1-HANDOFF.md) §4 trap 1 (the environment-variable filter).

---

## 1. The decisions, as taken

| # | question | verdict |
|---|---|---|
| 1 | Which fixed startup waits go? | **All four**: collect delay → 0, discovery window → 10 s (and early close, which already existed), 500 ms resolve sleep → 100 ms + a knob, and no more pausing the frame read after the evidence window. **Implemented** — see §2. |
| 2 | Per-build cache of "the collection", invalidated on a version bump? | **Not built.** It cannot shorten the wait it was proposed for; the wait is for the game's own JIT. The version-keyed artefact already exists as `known-build.json`. §3 has the argument. |
| 3 | Configuration precedence | **CLI > config file > auto-discovery > defaults. No environment-variable layer.** §4. |
| 4 | Where the config file lives | **`%LOCALAPPDATA%\drelay\hook\drelay.config.json`.** §5. |
| 5 | Double-click behaviour | **Discover; if discovery works, just run. If it fails, write a default config, explain, and pause.** §6. |
| 6 | Game-path auto-discovery | **Steam registry + `libraryfolders.vdf` (appid 1895130) + the pinned `recordedFrom` + standard paths, then the fingerprint gate.** §7. |
| 7 | Gemini's named-pipe recommendation | **Rejected**, on the repo's existing measurement. §9.2. |
| 8 | Scope of this handoff | **The launcher layer only.** The bridge stays a separate document. §8. |
| 9 | Does the proxy launch the game? | **Not in this release.** It gains a launch button later, and §10.2 is the ordering problem that has to be solved first. |

---

## 2. The startup waits, as they now stand (implemented)

These were separate decisions from the flags work and are already in the tree. They are recorded
here because the launcher's documentation has to describe the same numbers.

| wait | was | now | where |
|---|---|---|---|
| operator head start before the discovery window opens | 30 000 ms | **0** (`--collect-delay`) | `DrelayInstall.cpp` `kDefaultCollectDelayMs` |
| discovery window | 30 000 ms, closed early when all targets matched | **10 000 ms**, still closes early (`--discovery-ms`) | `DrelayInstall.cpp` `kDefaultDiscoveryWindowMs` |
| resolve-callback margin after the bootstrap signals | hardcoded 500 ms | **100 ms** (`--resolve-delay`) | `DrelayInstall.cpp` `kDefaultResolveDelayMs` |
| frame read after the evidence window | paused | **stays open**; `--pause-after-run` / `DRELAY_KEEP_READING=0` restores the pause | `DrelayInstall.cpp` `ReadKeepReadingAfterRun` |
| when a matched target is patched | at the discovery window's close | **500–750 ms after the JIT reports it** (`kTargetSettleMs` = 500, observed on the watcher's 250 ms cadence) | `DrelayFrame.cpp` `DrelayPatchTargetsDuringDiscovery` |

Two properties of the old design were load-bearing and are preserved deliberately:

- **The settle interval is not optional.** `PublishMatch` runs *inside* `compileMethod`, and the
  execution engine copies the finished code into the executable address *after* that call returns
  ([`HOOK-ARCHITECTURE.md`](HOOK-ARCHITECTURE.md) §4.4). A `jmp` written before the copy is
  overwritten and the hook silently never fires — the worst failure available, because the run then
  looks exactly like one where the game never called the method. The old design covered this by
  accident (nothing was patched for at least thirty seconds); the new one states it.
- **Nothing functional waits on the discovery window.** It now bounds only the per-target verdicts
  an operator reads. A target compiled after it closes is still matched, still reported, and still
  patched by the late pass.

**The "real signal instead of the 500 ms sleep" half of the verdict** was implemented as a **100 ms
margin with a knob**, and the code says why a signal is impossible: a startup hook cannot
JIT-compile anything ([`PHASE1-SCOPE.md`](PHASE1-SCOPE.md) §6.1 — every attempt faulted the process),
so it cannot execute a single statement after it returns, so nothing it writes can prove that it has
returned. The one event that does follow the return — the game's entry point being compiled —
happens microseconds after the bootstrap signals and therefore **before the JIT detour is even
installed**, so it cannot be observed either. This is the one point on which the implementation is
not literally the option that was chosen, and it is flagged here rather than buried.

**One consequence worth stating, because it is a release question and not a test question.** The
evidence window (`--live-seconds`, default 5) is measured **from the patch**. With the patch now
landing about a second after launch, the evidence files are written around t+6 s — on the loading
screen, for a real session. The hook keeps working (the frame read stays open and the dodge and
auto-aim are unaffected), so this costs diagnostics rather than function, but it does mean
`frames.csv` and `summary.txt` from a default release run describe the title screen. The options, in
order of preference, are for the release decision rather than this handoff:

1. start the evidence window when the arm probe first reports `ARMED` (a world exists), which is the
   first moment the read path has anything worth recording;
2. leave it as it is, and document that a diagnostic run passes `--live-seconds`;
3. start it at the frame tick's first entry — indistinguishable from today, since the tick runs from
   the title screen.

The two game-pinned runs that compare a sampler snapshot against a churning fixture had to be given
an explicit `--collect-delay 30000` for the same reason, and their comments say so; that is a
fixture-timeline change, not a change to what they test.

---

## 3. Why there is no "collect once, cache to file, invalidate on a version bump"

The proposal was: the hook re-does its collection on every start, so cache the result in a file and
reuse it until the game's version changes.

**It cannot work, and the reason is what is being collected.** The hook matches its detour targets by
the SHA-256 of their IL bytes, and the JIT reports a method only when the game first *compiles* it.
Two consequences:

1. **The thing being waited for is the game, not the hook.** `Projectile.HitCharacter` is compiled
   the first time a projectile is fired at the player. No file the hook can write changes when that
   happens, so a cache cannot make it happen earlier.
2. **The output is per-process.** A match yields a native entry address, which is a function of ASLR
   and of the order the runtime compiled things in. It cannot be replayed into a patch on the next
   run, and storing it would be storing a value that is wrong by construction.

What *is* stable per game build is already cached, and has been since Phase 0:

| artefact | keyed by | invalidated by |
|---|---|---|
| `hook/known-build.json` → `GeneratedBuildTable.cs` (six target IL hashes, sizes, tokens, the assembly MVID and size) | one game build | a rebuild of the table (`il_index.py`) when the game patches |
| the live identity check (self-check 5/5a) | the loaded assembly's MVID + size vs the pinned ones | a mismatch refuses to install |

A second file cache would be a second source of truth for the same question, which is what
`IMPLEMENTATION-PLAN.md` Phase 0 item 3 rejected for the constants. The accepted alternative — make
the wait not exist — is §2.

**If a cache is wanted anyway** (a per-build record of which targets this build has ever produced,
useful for spotting a cold first run), it belongs in the run directory as *evidence*, never on the
patch path. It is not in scope here.

---

## 4. Precedence

```
1. command line             --game, --autoaim, ...        (highest)
2. config file              drelay.config.json            (§5)
3. auto-discovery           Steam / recordedFrom / cwd    (§7)
4. compiled defaults        the numbers in Program.cs     (lowest)
```

**There is deliberately no environment-variable layer**, and this is the one place the outside
proposal would have introduced a real defect. The launcher builds the child's environment rather than
inheriting it, and it *deletes every variable whose name begins with `DRELAY_HOOK_` or `DOTNET_`*
before setting its own (`Program.cs` → `BuildEnvironment`; the reason is the `DOTNET_TieredCompilation`
hazard in [`HOOK-ARCHITECTURE.md`](HOOK-ARCHITECTURE.md) §1). A `DRELAY_*` configuration layer would
either

- be read by the launcher from its own inherited shell, which reintroduces exactly the
  "stale variable in the operator's shell silently changes what gets patched" failure that the filter
  exists to prevent, or
- be read by the hook from a namespace the launcher is about to overwrite, which is
  [`PHASE1-HANDOFF.md`](PHASE1-HANDOFF.md) §4 trap 1 for the fourth time.

Environment variables that a *test* needs have their own escape hatch already: the launcher owns the
`DRELAY_HOOK_*` namespace and writes those values itself, so a script drives the hook through the
launcher's flags. That is enough.

### 4.1 Per-field precedence, not per-source

The four sources are merged **field by field**, not chosen wholesale. `--autoaim highesthp` on the
command line must not discard a `GamePath` that only the config file knows, and a config file that
sets two fields must not discard the compiled defaults for the rest. Implementation shape:

```
Options defaults  ->  overlay config file  ->  overlay auto-discovery (GamePath only)  ->  overlay argv
```

Only fields the user actually supplied are overlaid. This means the parser cannot be "fill the struct
in argv order"; it needs a second pass that records *which* fields were set (a nullable-backed
settings record, or an explicitly tracked bit set). This is the single most error-prone part of the
implementation and deserves its own test (§13).

---

## 5. The configuration file

**Location: `%LOCALAPPDATA%\drelay\hook\drelay.config.json`** — the same root the run directories are
already created under (`--runroot`, `Program.cs`). Not beside the executable, because the zip may be
unpacked somewhere read-only (Program Files, a network share), and a first-run experience that
cannot write its own config is not a first-run experience.

**Format: JSON, read with `System.Text.Json` into a record.** The launcher already targets a modern
runtime and `System.Text.Json` is in the shared framework; no new dependency. `JsonSerializerOptions`
should be `PropertyNameCaseInsensitive = true` and `ReadCommentHandling = Skip` so a user's stray
comment is not a startup failure.

```jsonc
{
  // Required unless the game is discovered (§7) or --game is given.
  "GamePath": "E:\\SteamLibrary\\steamapps\\common\\Darza's Dominion",

  // What the hook does. All three are the release-relevant ones.
  "AutoAim": "closest",          // "closest" | "highesthp" | null (off)
  "Dodge": {
    "Enabled": true,             // run the solver and log decisions
    "Act": true,                 // ...and let the decision steer the player
    "DecisionDisc": 2.0,         // --dodge-disc, multiples of the disc-crossing time
    "WindowSeconds": 0.0         // --dodge-window, absolute floor in seconds
  },

  // Timing. The first two are reporting bounds after §2; they are here because a
  // slower machine may want them, not because they gate anything.
  "CollectDelayMs": 0,
  "DiscoveryWindowMs": 10000,
  "LiveSeconds": 5,
  "PauseAfterRun": false        // true restores the pre-§2 behaviour
}
```

`RunRoot` is deliberately not in the template: its default is already
`%LOCALAPPDATA%\drelay\hook` (`Program.cs`), and a JSON string that looks like an environment
variable but is not expanded by anything is a trap. If it is ever exposed, it is exposed as an
absolute path.

Three rules about the schema:

1. **A missing file is not an error.** It means "defaults", and the first-run path (§6) is the only
   thing that creates one.
2. **An unparseable *field* is a refusal with a named field, not a silent default.** A typo in
   `"AutoAim": "closests"` must fail at the command line the way `--autoaim closests` already does
   (`TryParseAutoAim`). A typo that silently resolves to "off" costs a session spent watching the
   game not aim.
3. **Test-harness flags are not in the template and not in the schema.** `--test-target`,
   `--frame-helper`, `--frame-replacement`, `--assembly`, `--exe`, `--off`, `--full-trace` and the
   diagnostic `DRELAY_*` switches stay command-line-only. A user-facing config that offers
   `FrameHelper` is offering a switch that allocates 64 KB per frame on purpose.

### 5.1 The full flag inventory the config maps onto

| flag | in the config? | notes |
|---|---|---|
| `--game PATH` | **yes** (`GamePath`) | required unless discovered |
| `--autoaim MODE` | **yes** (`AutoAim`) | implies the frame replacement, in the launcher |
| `--dodge` / `--dodge-act` | **yes** (`Dodge.Enabled` / `Dodge.Act`) | `Act` implies `Enabled`, as the flag does |
| `--dodge-disc` / `--dodge-window` | **yes** | `-1` in the launcher means "not set"; the config uses `null` |
| `--collect-delay` / `--discovery-ms` / `--resolve-delay` | **yes** | §2's knobs |
| `--live-seconds` / `--pause-after-run` | **yes** | |
| `--runroot` | **in the schema, not in the template** | the default is already the right place; §5's note |
| `--dbg-dump` | **yes** | crash dumps, off by default |
| `--verbose` / `--no-wait` | **yes** | `NoWait` matters for a future proxy launch, §8 |
| `--timeout` | **yes** | |
| `--hook` / `--bootstrap` | optional | default to beside the launcher; a config entry only helps a non-standard layout |
| `--act`, `--off`, `--full-trace`, `--frame-helper`, `--frame-replacement`, `--trace`, `--escape-test`, `--move-steer`, `--test-target`, `--exe`, `--assembly` | **no** | diagnostics and mechanism tests; CLI only |

---

## 6. Double-click and first run

`Drelay.Launcher.exe` with `args.Length == 0`:

```
1. load %LOCALAPPDATA%\drelay\hook\drelay.config.json      (missing => defaults)
2. if GamePath is still unknown:
       auto-discover (§7)
   if discovery succeeded:
       just run.  No prompt, no pause, no console output the user has to dismiss.
   if discovery failed:
       write a default drelay.config.json with GamePath empty and every other
       field at its default, print the exact path and the field to fill in,
       then Console.ReadKey() before exiting 2.
```

The "just run" branch is the verdict, and it is worth stating why it is not
"[always] write a config and pause": the common case on a Steam machine is that discovery works, and
a first-run experience that stops a working install to say "here is a file you do not need to edit"
is a worse experience than no first-run experience at all. The pause exists for one failure mode —
the console window vanishing before a human can read why — and it is applied to exactly that.

Two supporting rules:

- **Pause only when there is nobody to read the output otherwise.** A first run from a script or from
  the future proxy must not block on a keypress. Detection: `args.Length == 0` **and**
  `!Console.IsInputRedirected` **and** the failure is a user-fixable one. Scripted runs always pass
  at least one argument, so this only affects the double-click path.
- **Never pause on a refusal the user cannot fix by editing the file** (missing DLLs beside the
  launcher, a game build whose fingerprint does not match). Print and exit; the hook's own log has
  the detail, and the README says where.

---

## 7. Game-path auto-discovery

Probe order, first hit wins, each candidate validated the same way:

1. `HKCU\Software\Valve\Steam` → `SteamPath` (fall back to `HKLM\SOFTWARE\WOW6432Node\Valve\Steam` →
   `InstallPath`) → read `<steam>\steamapps\libraryfolders.vdf` → for each `path` → test
   `<path>\steamapps\common\Darza's Dominion`. The app id is **1895130**, already recorded in
   `known-build.json` (`game.steamAppId`); the library list is where a game installed on a second
   drive lives.
2. The pinned record: `GeneratedBuildTable.RecordedFrom` (currently
   `E:\SteamLibrary\steamapps\common\Darza's Dominion`). It is the machine the build was verified on,
   which is exactly the case where it is right, and one absent path when it is wrong.
3. `<launcher dir>` and its parent — covers "unpacked into the game folder", which is what the
   release notes tell the user to do anyway.
4. Standard standalone locations, if any are ever confirmed. Nothing else is guessed at.

**Discovery produces a candidate; the gate decides.** Finding a directory named
`Darza's Dominion` does not make it the right build, and the two apphosts that ship together carry
*different* assemblies (`known-build.json`: `assembly.size` 50 216 960 vs
`assembly.sizeOpenGlVariant` 50 831 360 — the root `DarzasDominion.exe` is the pinned one, the one
under `OpenGL\` is not). So:

- the launcher resolves `<dir>\DarzasDominion.exe` (`GeneratedBuildTable.ExecutableFileName`), never
  a path under `OpenGL\`;
- the hook's own identity checks (MVID + assembly size, self-check 5/5a) refuse to install if the
  build is not the pinned one, which is the same failure the `Fingerprint.ps1` gate reports before
  anything runs;
- **discovery must never report success on a directory whose assembly is not the pinned build.** It
  should name the mismatch, not silently retry the next candidate — a user with the OpenGL variant
  needs to know that is the problem.

### 7.1 A release-packaging gap this feature runs into

The release notes tell the user to run `.\hook\tools\Fingerprint.ps1` before installing, but the zip
is built with `Compress-Archive -Path 'out\*'` and `out\` contains only the DLLs, the launcher and
`VERSION.txt` — there is no `tools\` and no `known-build.json` in it. So that instruction has never
been actionable for a user of the zip. Two ways out, and the second is the one this feature assumes:

1. ship `Fingerprint.ps1` + `known-build.json` in the zip and keep the instruction, or
2. drop the instruction and rely on the in-process gate, which already refuses an unknown build with
   one line in `hook.log` and leaves the game running.

The launcher should prefer (2) and, when discovery succeeds but the hook later refuses, point at the
run directory's `selfcheck.txt`. That is a documentation change to the release workflow and the
README, not code.

---

## 8. Where this stops, and what it leaves for the bridge

**In scope:** the config file, precedence, discovery, first-run, and the flag inventory above.

**Out of scope, deliberately:** anything that connects to the running hook. The transport question is
already decided in [`JAVA-BRIDGE.md`](../bridge/JAVA-BRIDGE.md) §3 and this feature does not reopen
it. Two hook points are left deliberately open, because they cost nothing now and are expensive to
retrofit:

- **`--no-wait` already exists** and is what a supervising process uses: it returns as soon as the
  hook has answered, and the game keeps running. The proxy's launch button (later) needs exactly the
  process-handle behaviour described there.
- **A stateless config path** (the proposal's `--config-stdin`) is *not* adopted now. If the proxy
  later wants to launch a run without overwriting the user's saved file, the cheapest shape that fits
  this design is `--config <path>` pointing at a generated file in the run root, not stdin: the
  launcher already has a place to put per-run files, and a generated config is inspectable after the
  fact, which stdin is not. Recorded here so the later decision starts from the existing structure.

---

## 9. The outside proposal: what was taken, what was corrected

### 9.1 Taken

- the layered precedence idea (§4), with the env layer removed;
- JSON bound to a record, test flags kept out of the user-facing template (§5);
- the first-run experience, with the "just run when discovery succeeds" refinement (§6);
- the observation that `KeepReading` should default on for a packaged release — that part was right
  and is implemented (§2), though it is now expressed as "the read stays open unless asked to stop",
  because the switch's old polarity would have made a release remember to set something.

### 9.2 Corrected — named pipes

The proposal recommends **named pipes** for live control. This repository already rejected them, with
reasons that have not changed ([`JAVA-BRIDGE.md`](../bridge/JAVA-BRIDGE.md) §3.1,
[`BRIDGE-RESEARCH.md`](../bridge/BRIDGE-RESEARCH.md) §2.1):

- pure Java **cannot** put a pipe in message mode — a `CreateFile` pipe client starts in byte-read
  mode and `SetNamedPipeHandleState` is not reachable without JNA;
- Java's `FileChannel` cannot do overlapped I/O on a pipe, so a blocking read is uninterruptible
  except by `close()`;
- the chosen transport is a **pagefile-backed named section with sequence-stamped rings**
  (`Local\DrelayHookShm`), which needs no syscall on the data path, no elevation, and no JNA on the
  hot path — JNA is used only at attach (`OpenFileMapping`/`MapViewOfFile`/`CloseHandle`).

The proposal's own third option — a `FileSystemWatcher` on the config file — is the *simplest*
implementation and the one this design is structurally compatible with (a config file already
exists), but it is not adopted as the control channel either: file locking plus a watcher's
coalescing makes "did the hook apply my change" unanswerable without an acknowledgement channel
anyway. It remains a legitimate fallback if the bridge is never built, and §8 records that the
config file is the reason it is cheap.

### 9.3 Corrected — the rest

| proposal | correction |
|---|---|
| "auto-discovery … via the Registry" as the lowest-priority fallback | it is §7's *first* probe, because on a Steam machine it is the only one that is right; the pinned `recordedFrom` is the fallback, not the other way round |
| "Environment variables (`DRELAY_*`)" as layer 2 | removed — §4 |
| a config default of `AutoAim: "closest"` | not a default. Aiming is an action with a real side effect (`Prefs.enable_aimbot` is set and persisted); the written template leaves it `null` |
| "default `KeepReading` to true" | adopted, and the switch's polarity inverted so the *default* is the safe release behaviour |
| `--test-target`/`--frame-helper` out of the template | adopted, and extended: they are out of the schema as well as the template |
| write config then start the launcher, from the proxy | deferred; §10.2 names the ordering problem that has to be solved first |

---

## 10. Traps

### 10.1 The environment filter (fourth reminder)

Anything the operator sets in their own shell must be `DRELAY_*`; anything the launcher needs to reach
the hook must be `DRELAY_HOOK_*` **and be written by the launcher**. A config field that ends up as a
`DRELAY_HOOK_*` variable in the parent environment is silently deleted. This is why §4 has no
environment layer at all.

### 10.2 Elevation and integrity levels

The proxy elevates (it claims server addresses on the loopback interface, `Launcher.java`), and a
process cannot inject into one at a higher integrity level. If the proxy launches the game, then
either the game is elevated (because the proxy is), or the launcher must elevate itself, or the
launcher must be launched by the proxy before it elevates. This is an ordering question with no
answer in the code today, and it is the reason §1's verdict defers the proxy button.

### 10.3 Two apphosts, one pinned

`OpenGL\DarzasDominion.exe` carries a **different** assembly. Discovery must resolve the root apphost
and the gate must name the mismatch rather than retry. §7.

### 10.4 A read-only install directory

The zip may be unpacked anywhere. Nothing the launcher writes may be assumed to live beside the
executable — which is why the config is under `%LOCALAPPDATA%` and why the run root is a separate
knob. The one thing that *is* beside the executable is the two DLLs and the launcher itself, and a
missing one of those is a non-pausable refusal (§6).

### 10.5 Defaults that must not be duplicated

`DodgeConfig.Default()` on the managed side owns the dodge's defaults, and the launcher's `-1.0`
"not set" convention exists so that a default is never written down twice
(`Options.DodgeDisc`). The config file must follow the same rule: `null` means "not set", and the
launcher must not carry its own copy of the number.

### 10.6 The flags that imply others

`--autoaim` and `--escape-test` both set `--frame-replacement`, and `--dodge-act` puts the pilot in
`dodge` mode. A config file that sets `AutoAim` without the frame replacement produces a module that
is published, verified, and never entered. Whatever applies the config must apply those implications
in the same code path the flags use, not re-derive them.

---

## 11. Implementation order

Each step is independently testable and none of them needs the game.

1. **A settings record with per-field provenance.** `Options` today is a bag of fields with defaults
   in the initialisers; the merge in §4.1 needs to know which fields the user supplied. Do this
   first and test it alone: config-only, CLI-only, both, neither, and a CLI value overriding exactly
   one field of a config that sets several.
2. **The config reader.** `System.Text.Json`, case-insensitive, comments skipped, missing file =
   defaults, bad *field* = named error. Test with: missing file, empty file, unknown key (ignore),
   malformed JSON (refuse, name the file), each field at each type boundary.
3. **Auto-discovery**, as a pure function of an injectable environment (registry + filesystem
   probes behind an interface) so the Steam-library walk is testable with a fake tree. Test: no
   Steam, Steam with one library, Steam with the game on a second library, `recordedFrom` only,
   launcher-directory only, and a directory whose assembly is the OpenGL variant.
4. **First-run behaviour**, with the pause suppressed when input is redirected. Test the three
   branches of §6 and assert that a redirected run never blocks.
5. **Wire the config into `BuildEnvironment`** and assert that every field reaches the environment
   variable it claims to, and that an unset field reaches *nothing* (so the hook's own default
   applies).
6. **Update `Usage()` and the README**, and add the config file to the release notes' "what to do
   next".

## 12. What this does not do

- It does not add a runtime control channel (§8).
- It does not change what the hook patches, or how it decides what to patch.
- It does not add a second version of any default (§10.5).
- It does not make the launcher a service, a tray icon, or a GUI. It is a console program that a
  user double-clicks, and the config file is what stands in for a GUI until the dashboard exists.

## 13. The invariants worth a test

| # | invariant | how it is checked |
|---|---|---|
| C1 | A config field never overrides a value the user typed on the command line | §11 step 1 |
| C2 | An unset field is absent from the child's environment, not set to the default | §11 step 5 |
| C3 | A typo in `AutoAim` refuses at the command line and names the field | §11 steps 1–2 |
| C4 | Discovery never returns a directory containing the OpenGL assembly | §11 step 3 |
| C5 | A zero-argument run with redirected input never pauses | §11 step 4 |
| C6 | The written template contains no test-harness key | a string search over the template in the test |
| C7 | Every field of the config has a corresponding flag, and every user-facing flag a field | one table, asserted in both directions |

---

## 14. Appendix — what this round found that is not this feature's

Recorded so a release review has them in one place, and so nobody spends a session
re-discovering them while implementing §11.

1. **Two injection checks were already red, and this change did not cause them.** After the
   §2 work the end-to-end suite reports **138 of 140**, and the two failures are both in
   run 10: `the sampler classified every live bullet` (12 vs 20) and `the measured straight
   fraction agrees with the host's own mix` (0.34 vs 0.60). They are **structural, not
   temporal**: `TrajectorySampler.SampleOne` returns before taking a single sample for a
   projectile the client will never test against the player (`HitsPlayer == false`,
   `LocalSeedDerived == true`, added by [`SPIKE3.2-OFFLINE-SOLVER.md`](SPIKE3.2-OFFLINE-SOLVER.md)
   §12), while the check compares the sampler's post-filter count and cumulative fraction
   against the sensor's **unfiltered** `live` count and the host's unfiltered snapshot. Both
   halves are present at `HEAD` (`autododgeresearch/hook/managed/Drelay.Bootstrap/TrajectorySampler.cs`,
   `autododgeresearch/hook/tools/Test-Injection.ps1`), and the numbers did not move when the
   evidence window was moved from +1 s to +31 s. Resolving them needs the *fixture* to
   publish its eligible count and eligible mix (a `SyntheticHost` change), not a timeline.

2. **The release zip does not contain the gate the release notes tell users to run.** §7.1.

3. **The evidence window now lands on the loading screen.** §2.

4. **`Drelay.Launcher.exe` remains a console program with a required argument**, so a
   double-click today prints `--game is required` and the window closes before it can be
   read. That is the user-visible half of this feature, and §6 is its fix.

---

## 15. As built — the files, the two readings that changed, and what covers each decision

**Files.** The launcher's `Program.cs` lost the `Options` class and `BuildEnvironment`, and gained
nothing but the flow:

| file | what it owns |
|---|---|
| `Program.cs` | `Main` (the flow and the refusals), `Run`, `Usage`, the log tail |
| `Settings.cs` | `Settings` (the nullable, per-source record) and `Options` (the resolved result, its defaults, the field-by-field `Apply`, the implications, validation, path resolution) |
| `CommandLine.cs` | argv → `Settings`, and the two spellings of every flag |
| `ConfigFile.cs` | the schema table, the JSON reader, the template writer |
| `Configuration.cs` | **the merge itself** — the one place §4's precedence exists |
| `Discovery.cs` | the probe order, the Steam library walk, the KeyValues scanner, the build gate |
| `DiscoveryWindows.cs` | the registry and filesystem behind `IDiscoveryEnv`; the one file the harness does not compile |
| `EnvironmentBlock.cs` | the child's environment, and which of its names are written only when supplied |
| `FirstRun.cs` | `ShouldPause`, the C5 rule, as a function rather than an `if` |
| `tools/LauncherHarness/` | C1–C8, run by `tools/Test-HookOffline.ps1` and built by `build.ps1` |

**Reading 1 — §4.1's diagram, and where discovery sits.** §4.1 draws discovery between the config
file and the command line, which would let discovery overwrite a `GamePath` the config file set;
§4's table says the config file beats auto-discovery, and §6 says discovery runs "if GamePath is
still unknown". The three agree on one behaviour and that is what was built: **discovery may fill a
`GamePath` that neither the config file nor the command line supplied, and may never overwrite
one.** With that rule the position of the discovery step in the chain is unobservable, and
`Configuration.Resolve` documents the conflict where the code is.

**Reading 2 — §11 step 1's "settings record with per-field provenance" is two types, not one.**
`Settings` is the nullable source record; `Options` is the resolved result and keeps its old shape
and its old defaults, so `EnvironmentBlock` and `Run` were not rewritten to a new vocabulary. The
provenance C2 needs is five `Set*` flags on `Options`, for exactly the five timing knobs whose
launcher default and hook default are the same number — the five that are therefore safe to *not*
write. Every other launcher-owned name (`DRELAY_HOOK_SESSION`, `_RUNDIR`, `_GAMEDIR`, `_BLOCK`,
`_DRYRUN`, `DOTNET_STARTUP_HOOKS`) is still written on every run, because its absence is not a
neutral state: an absent `DRELAY_HOOK_DRYRUN` means **act**.

**Two consequences of that choice, stated rather than buried.**

- `--timeout` is now written to the child only when it was supplied. The child's own default is the
  same 60000 ms (`DrelayMain.cpp`), so nothing changes; note that
  `Test-Injection.ps1`'s `$env:DRELAY_HOOK_TIMEOUT_MS = '15000'` never reached the child anyway,
  because the launcher deletes the whole `DRELAY_HOOK_*` namespace from it.
- `--collect-delay`, `--discovery-ms`, `--resolve-delay` and `--live-seconds` are unchanged in
  effect for the same reason, and every script in `tools/` passes them explicitly anyway.

**Two additions to the pinned record**, both generated from `known-build.json` rather than written
down again: `GeneratedBuildTable.AssemblySizeOpenGlVariant` (so discovery can name the OpenGL
install instead of reporting "not the pinned build", §7) and `GeneratedBuildTable.SteamAppId` (so
discovery can read `appmanifest_<id>.acf` and get the directory the user actually installed into).
C8 fails if the record stops carrying either.

**What covers each decision.**

| decision | covered by |
|---|---|
| §4 precedence, per field | C1 |
| §13 C2, and §10.5's "no second copy of a default" | C2 |
| §13 C3 (both sources), §5 rule 2 | C3, R1 |
| §7 probe order, the app-manifest path, the OpenGL gate | C4 |
| §13 C5 and §6's two pause rules | C5 (the truth table, plus a source check that `Main`'s single `Console.ReadKey` is behind `FirstRun.ShouldPause`) |
| §6 read through §5 rule 1 (the template exists before anything is resolved) | C5 (`FirstRun.ShouldWriteTemplate`), R1 (the writer never overwrites) |
| §13 C6, and that the written template is the release behaviour | C6 (§16.2) |
| §13 C7, in both directions | C7 |
| §7's two apphosts, at the record | C8 |
| §11 step 2's reader boundaries | R1 |
| §11 step 1's "the parser cannot be fill-the-struct-in-argv-order", plus §10.6 | R2, C1 |

**What is deliberately still not built**, and is unchanged by this round: the proxy's launch button
(§1 verdict 9), a runtime control channel (§8), `--config <path>` for a per-run file (§8), and the
per-build evidence cache of §3. §14's four findings are also unchanged — in particular item 1, the
two structural injection-check failures, which are a fixture problem and not this feature's.

---

## 16. The first live no-argument runs, and the two departures they caused

Two runs on 2026-10-16 (`20261016-041022-p2304` and `20261016-041524-p14372`) were the first launch
of this feature against the game. **The hook did its job and the §2 work is confirmed live**: with no
flags and no delay, `frameTick` waited out its 500 ms settle interval and was **PATCHED** about a
second after launch, the other five targets were found *after* the window closed by the late pass and
were waiting out their own settle intervals when the game was closed, and 655 frames were read. The
run still reported **no dodge**, and that is the interesting part: nothing had asked for one.

```
boot: no frame entry point to publish; the frame read is native
boot: dodge idle: DRELAY_DODGE is not set
boot: auto-aim plan: auto-aim: off (DRELAY_AIM_MODE unset)
```

### 16.1 Departure one: the template is written on any first no-argument run (§6)

§6 writes the template **only when discovery fails**, which inverts who needs it. Discovery succeeding
is the normal case and the whole point of §7 — so the one operator who never receives the file is the
one whose game was found, and the only way to change what the hook does is to know the schema by
heart. That is what the run above did: no file, nothing enabled, and no way to notice except by
reading `bootstrap.log`.

`FirstRun.ShouldWriteTemplate(noArguments, configExists)` now decides it, and `Main` writes the file
**before the configuration is resolved**, so the file describes the run that is happening rather than
the one after it. A scripted run still creates nothing, and an existing file is never overwritten.
Section 6's other rules are untouched: no prompt, no pause, no blocking.

### 16.2 Departure two: the template is the release behaviour, not the compiled defaults (§9.3)

§9.3's verdict — *"a config default of `AutoAim: "closest"` | not a default"* — was about a template
whose values are the compiled defaults. With the file now written by a first run and used by it, the
template **is** the release behaviour, and an all-off template means the headline feature
("double-click it") attaches a hook that does nothing. **The operator's decision, taken
2026-10-16, is that the written file turns both on:**

| key | written value | why |
|---|---|---|
| `AutoAim` | `"closest"` | the session this project exists for; §9.3's counter-argument stands and is kept in `ConfigFile.Template`'s comment |
| `Dodge.Enabled` | `true` | the solver runs every frame and logs its decision |
| `Dodge.Act` | `true` | and the decision steers the player |
| `Dodge.DecisionDisc` / `WindowSeconds` | `null` | §10.5: the solver owns those defaults |

`CompiledDefaults` and `ReleaseDefaults` are therefore two different things, and `C6` now asserts the
release ones, plus that resolving the written file produces `DRELAY_AIM_MODE=closest`,
`DRELAY_DODGE=1`, `DRELAY_DODGE_ACT=1`, `DRELAY_MOVE_STEER=dodge`, `DRELAY_FRAME_SENSOR=1` and the
managed frame replacement — i.e. that the file, merged through the shipped code, asks the hook for
both modules.

**One consequence, and it is a real interaction rather than an edge case.** `Dodge.Act` and
`--move-steer` both say *this is what steers the player*, so with the shipped template a steering
experiment collides with the file's default. §10.6 refuses that pair when both are typed on the
command line, and that refusal stands: two hand-typed contradictory flags are a mistake. But the
same pair where `Act` came from the **file** is a run, because the command line is the more specific
statement and a file cannot be told "not this time":

- `"Act": true` in the file + `--move-steer hold:90:8000` on the command line resolves, with `Act`
  cleared for that run. Without this rule the shipped default breaks
  `Run-Phase3Session.ps1 -Experiment Sweep|Hold`, which passes `--move-steer` and has no way to turn a
  config field off;
- the file's `Dodge.Enabled` is **not** cleared — solving and logging do not contradict somebody else
  steering — so the solver still runs and logs. `"Enabled": false` in the file is how a calibration
  run gets the old, solver-free frame;
- and the launcher **prints a notice** naming both, because a merge that silently discards a field
  the operator set is the class of surprise this whole section exists to prevent. `R2` asserts the
  notice, the cleared field and the pilot's own value.

### 16.3 Two things the runs showed that are not this feature's

1. **The `crash.txt` those runs left is the known close-time CLR fault, not a regression.** It is
   byte-for-byte the same stack as the runs of 2026-10-07…10-14: `coreclr.dll+0x60BCC`,
   `VirtualCallStubManager::ResolveWorker`, reading `[this+8]` with `this == 0`, with no
   `Drelay.Hook.dll` frame on the faulting stack. `PHASE3-SCOPE.md` §5.1 and
   `PHASE1-VERIFICATION.md` §5 already record it, and the rule there still holds: a `crash.txt` says
   nothing about whether the run succeeded.
2. **A stale `DRELAY_TRACE=1` in the operator's shell reached the hook and armed the recorder with no
   managed frame replacement installed**, so `trace.jsonl` was a 585-byte header in the first run and
   0 bytes in the second — the "valid, empty file" `Options.Trace` exists to warn about. The launcher
   deliberately does not filter plain `DRELAY_*` names (`Run-Phase2Session.ps1` and
   `Test-Injection.ps1` depend on that passthrough, and their comments say so), so the honest fix is
   in the hook rather than the launcher: **arm the recorder only when there is a frame entry point to
   call it from, or say loudly that it is armed and will never be called.** Not made in this round;
   recorded here so it is not re-discovered.


