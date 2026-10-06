# drelay

A local TCP relay for **Darza's Dominion** that sits between the game client and the official
servers. Packets can be inspected, logged, decoded into structured objects, and written back.

It is a research tool, not a game client. It changes nothing on disk in the game's installation and
does not modify the game binary: it claims the server addresses on the loopback interface so the
client's own connection is delivered into the local stack, then forwards each session to the real
server.

---

## Requirements

| | |
|---|---|
| **Java** | 25 or newer (Temurin, Microsoft OpenJDK, Oracle JDK — any build) |
| **OS** | Windows 10/11. Claiming an address and the setup scripts are Windows-specific; the relay itself runs anywhere the JVM does, but without claims it only forwards a route whose destination is not a real server address |
| **Rights** | Administrator, only to claim the server addresses (the launcher asks for it) |
| **Game** | Darza's Dominion, installed and running normally |

Nothing else: the released jar has no dependencies.

---

## Run it

### From a release (no build required)

1. Download **`drelay.jar`** from the [latest release](https://github.com/is-fo/drelay/releases/latest).
2. Put it in a directory of its own, for example `C:\drelay`.
3. Double-click it, or run:

   ```powershell
   java -jar drelay.jar
   ```

4. Accept the Windows prompt for administrator rights, then start Darza's Dominion.

On the first run the launcher writes the files it ships (a `relay-routes.json` and two PowerShell
helpers in `tools\`) next to the jar, refreshes the game server addresses, claims them, starts the
relay and opens the dashboard at <http://127.0.0.1:8765/>.

Press **Ctrl+C** in the console window to stop: the claims are released automatically. Closing the
window can leave an address claimed, which stops the game from reaching its real server; repair that
with

```powershell
java -jar drelay.jar --release-claims      # from an elevated prompt
```

### From source

```powershell
git clone https://github.com/is-fo/drelay.git
cd drelay
.\build.ps1                 # javac + jar, needs only a JDK; produces target\drelay.jar
.\drelay.ps1        # builds if needed, then runs the jar
```

`build.ps1` runs the offline checks after packaging. Maven works too, and is what CI uses:

```powershell
mvn clean package           # produces target\drelay.jar
java -jar target\drelay.jar
```

`drelay.ps1` is the one script to remember: it finds the newest jar (building it first if there is
none) and then does exactly what double-clicking the jar does. It can also do the few jobs that are
about a *previous* run rather than this one:

```powershell
.\drelay.ps1                 # build if needed, then run
.\drelay.ps1 -Rebuild        # clean build, then run
.\drelay.ps1 -Stop           # stop a running relay and release the claimed addresses
.\drelay.ps1 -Status         # what is running and what is claimed; change nothing
.\drelay.ps1 -Claim          # claim the addresses without starting a relay
.\drelay.ps1 -Release        # release the claimed addresses, leave the relay alone
.\drelay.ps1 -CheckUpdate    # is there a newer release?
.\drelay.ps1 -Path           # which jar would it run?
.\drelay.ps1 -JarArg -Ddrelay.nexus.enabled=true
```

Building puts the jar in `target\`, and `drelay.ps1` moves it up beside the script before running:
the launcher treats the jar's own directory as its working directory, so one jar in one directory
keeps the route table, the extracted helpers and `work\logs` in a single predictable place — the same
layout a downloaded release has.

### Command line

Everything below is what the jar itself accepts, so it works identically for a release download:

```
java -jar drelay.jar [options] [commands] [-Ddrelay.*=...]
```

| Option | Effect |
|---|---|
| `--no-update` | do not check GitHub for a newer release |
| `--no-claim` | do not claim any address and do not elevate — nothing is captured |
| `--no-elevate` | never re-run the jar to obtain administrator rights |
| `--no-browser` | do not open the dashboard automatically |
| `--config <path>` | use a different route table (default `work/relay-routes.json`) |

| Command | Effect |
|---|---|
| `--check-update` | report whether a newer release exists |
| `--update` | install the newest release and restart |
| `--stop` | stop a running relay and give the claimed addresses back |
| `--relay` | run the relay alone in this JVM, no update and no claim |
| `--release-claims` | give every claimed address back |
| `--install` | extract or upgrade the bundled files, then stop |
| `--version`, `--help` | |

Any `-Ddrelay.*` property reaches the relay, so settings can be changed for one run:

```powershell
java -jar drelay.jar -Ddrelay.web.port=0                  # no dashboard
java -jar drelay.jar -Ddrelay.log.dir=D:\drelay-logs      # logs elsewhere
java -jar drelay.jar -Ddrelay.nexus.enabled=true -Ddrelay.nexus.dryRun=true
java -jar drelay.jar -Ddrelay.strip.enabled=false          # forward server->client bytes verbatim
java -jar drelay.jar -Ddrelay.strip.effects=11,16          # the shipped default, spelled out
java -jar drelay.jar -Ddrelay.strip.effects=11,16,7        # add Slowed - it is server-validated
java -jar drelay.jar -Ddrelay.strip.hallucinating=false     # disarm the cosmetic one only
```

### Updates

The launcher checks GitHub for a newer release on every start and installs it before claiming
anything, verifying the jar against the published `SHA256SUMS.txt` first. `--no-update` skips the
check. `--check-update` reports without installing.

### Configuration

`relay-routes.json` holds the routes, the log and dashboard settings, the auto-nexus rule, and the
server→client `strip` block. Its `_comment` block documents every key in place. The addresses for the
`Game` and `Queue` routes are refreshed from the game's own API and from DNS on every start; an
address may be pinned by removing its `refresh` key.

The launcher keeps three files straight, and only the middle one is what the relay actually reads:

| File | Written by | Purpose |
|---|---|---|
| `relay-routes.json` | the launcher, on first run only | the template — put your own here before the first run and it is used instead |
| `work/relay-routes.json` | the launcher, every run | **the file the relay reads**: your settings plus the refreshed addresses and the LAN address the launcher resolved |
| `work/install-manifest.txt` | the launcher | which bundled files it wrote, and whose they are |

The first two are not interchangeable, and the difference is not cosmetic: `upstreamHost` is only in
the generated copy, and without it the relay refuses to start as soon as an address is claimed — every
upstream dial would be delivered back into its own listener. A file you edit is never overwritten, by
the launcher or by an update: its contents decide, and the moment they differ from what was installed,
the file is yours. To hand it back to the launcher, delete its line from `work/install-manifest.txt`
(or delete the file).

**Settings changed in the dashboard are saved.** The auto-nexus rule and the `strip` module are the
two things the dashboard owns, and pressing **apply** writes them back into the file the relay was
started with — `work/relay-routes.json` under the launcher — telling you in the panel whether the write
succeeded. The launcher then carries those two blocks forward when it regenerates that file on the next
run, so a threshold or an armed effect survives a restart instead of quietly reverting to the default.
Change them from the dashboard, or by editing `work/relay-routes.json`; the template's copy of those
two keys is only the first-run default.

---

## Watching a session

| | |
|---|---|
| **Dashboard** | <http://127.0.0.1:8765/> — live HP, the auto-nexus and status-effect-strip panels, both saved back to the route table, and a filtered packet stream |
| **Event log** | `work/logs/events-<run>.jsonl` — every event, with raw payload and decoded fields |
| **Nexus log** | `work/logs/nexus-<run>.jsonl` — the escape story only |
| **Relay console** | `work/logs/relay-console.out` — the relay's own output, mirrored from the launcher's window |

Auto-nexus injects `GmEscape` when HP falls below a threshold. It is off by default and starts in dry
run when switched on: [docs/AUTONEXUS-GUIDE.md](docs/AUTONEXUS-GUIDE.md) is the operator's manual,
[docs/AUTONEXUS.md](docs/AUTONEXUS.md) is how it works. It is reactive — it reads the server's own
health packet, it does not predict damage.

The `strip` block is the one thing that changes bytes the server sent. It is **on by default**, with
`Confused` (11) and `Hallucinating` (16) armed: it removes those entries from this client's own status
lists, because both effects are applied client-side and never expire on a timer. The dashboard's
**status effect strip** panel also offers `Paralyzed` (6) and `Slowed` (7), both off by default — in the
2026-10-05 capture Slowed landed on the local player 69 times and Confused 5, while Paralyzed managed 4,
so arming them is a real choice rather than a free one. They are a trap as well as a choice: `Slowed`,
`Paralyzed`, `Cutscene`, `Grounded` and `FearOfTheBull` all sit in the client's movement law, so the
server re-simulates them and a stripped entry is a speed-check kick. `Hallucinating` is the exception and
the reason it is on: its whole client-side consequence is a sprite swap, nothing derived from it reaches
the wire, and that has been confirmed — so it is a free cosmetic gain rather than a gameplay change.
[docs/INJECTION.md](docs/INJECTION.md) §5 has the wire format, the reason a shortened duration does
nothing, the effect-by-effect verdict, and what it refuses to rewrite.

When a session does end badly, `GmKicked` (id 185) is decoded into the event log: the server's own
reason text lands in `data.reason`, and again on the session-close event as `kickedReason`, so a failed
rewrite names its cause instead of showing up as a bare disconnect.

---

## How it works

The client learns its destinations at runtime: the queue by host name, the game by a bare IP from
`https://playdarzas.com/api/v1/serverlist`, and a realm by a `GmReconnect` packet sent mid-session.
Patching the client is not an option and a hosts entry cannot cover a bare IP, so the relay claims
each destination address **on the loopback pseudo-interface**. Windows then delivers the client's own
connection to the local stack, where a per-route listener accepts it and forwards the session to the
real server from a socket bound to this machine's LAN address.

```
Client -> (claimed address, delivered locally) -> drelay listener -> real server
```

The relay forwards bytes verbatim and only rewrites when a rule injects; framing is
`[4-byte length][payload]`, big-endian on the game service and little-endian on the queue. The
protocol, the verified packet layouts, the interception design and the dead ends are documented in
[docs/PROTOCOL.md](docs/PROTOCOL.md).

Modules:

| Package | Role |
|---|---|
| `networking` | `Launcher` (entry point), `Relay` (listeners and forwarding), `Session`, `AutoNexus`, `Strip`, `PlayerLocator`, `ConfigWriter` (settings persistence) |
| `networking.packets` | packet codecs and the registry, the injection primitive, `UpdateScan` (the `GmUpdate` walk), `StatusStrip` (the effects it removes) |
| `networking.log` | the structured event log |
| `networking.web` | the dashboard |
| `networking.util` | JSON writing, preferences, field decoding |

---

## Development

Source and tests are in separate trees, and only the sources are packaged — the jar cannot contain a
test class by construction, and it is 30 KB smaller for it.

```
src/main/java/        the relay and its launcher
src/main/resources/   the route table template and the PowerShell helpers, shipped inside the jar
src/test/java/        the offline checks (standalone main classes, no test framework)
tools/                operator and recovery scripts
tools/tests/          the end-to-end checks that drive a real relay subprocess
```

```powershell
.\build.ps1 -Clean            # compile, package, run the offline checks
mvn clean package             # the same artifact, through Maven

java -cp "target\classes;target\test-classes" networking.PrimitiveTests      # primitives, codecs, ids, varints
java -cp "target\classes;target\test-classes" networking.FrameTests          # framing, both byte orders
java -cp "target\classes;target\test-classes" networking.ClientPacketsTests  # the client clock field
java -cp "target\classes;target\test-classes" networking.InjectionTests      # injected bytes, gates, write lock
java -cp "target\classes;target\test-classes" networking.UpdateScanTests     # the GmUpdate walk, the strip, the player locator
java -cp "target\classes;target\test-classes" networking.SettingsTests       # settings parsing, the route-table write, the filter round trip

python tools\tests\test_relay.py                   # framing, byte-exact both directions
python tools\tests\test_relay_little_endian.py     # both length orders end to end
python tools\tests\test_relay_stress.py            # both directions under concurrent load
python tools\tests\test_reconnect_log.py           # a GmReconnect retarget and a GmKicked reason are decoded and logged
python tools\tests\test_auto_nexus.py              # the auto-nexus chain against a fake game server
python tools\tests\test_status_strip.py            # a server->client rewrite, against a fake game server
python tools\tests\test_dashboard.py               # the page parses; the endpoints have the right shape
python tools\tests\test_relay_live.py              # the relay in front of the real server
python tools\verify_packet_ids.py                  # packet ids against the client's own enums

.\tools\Set-AddressClaim.ps1 -Status               # what is claimed on loopback now (no elevation)
```

The Python checks drive the relay from `target\classes`, so build first. They need no game, no
network and no elevation, except `test_relay_live.py` which connects to a real server.
`verify_packet_ids.py` needs a decompiled client; point it at one with `DRELAY_DECOMP=<dir>`, or it
skips.

### Releasing

A release is a **version bump merged to master**. The version lives in `src/main/resources/drelay.properties`
(what the jar reports and the updater compares) and in `pom.xml`, and the workflow refuses to publish
while they disagree:

```powershell
.\tools\Bump-Version.ps1 -Bump patch      # 0.0.2-alpha -> 0.0.3-alpha, both files
git commit -am "chore(release): 0.0.3-alpha"
git push origin master
```

The `release` workflow then reads the version, and if tag `v<version>` does not exist yet it builds the
jar on Java 25, runs the offline checks, verifies that the jar is runnable and carries its bundled
files, tags exactly the commit it built, and attaches `drelay.jar` and `SHA256SUMS.txt` to a GitHub
release with a build provenance attestation. A push to master that did **not** bump the version is a
no-op. Bumping the version without wanting a release is therefore safe; tag a commit by hand, or push
a `v*` tag, to publish an existing version.

A version with a prerelease suffix (`0.0.2-alpha`) is published as a GitHub prerelease, which keeps it
off the *Latest* badge on the releases page. It is **not** held back from the updater: the updater
reads the release list and does not look at that flag, so an alpha reaches every install. The
releases are version 0.0.x to date; the updater only ever offers a *newer* version than the one
running, so the numbering has to keep increasing to be delivered.

The same workflow can be run from the Actions tab instead: `bump` commits a `patch`/`minor`/`major`
bump and releases it in one step, and with `publish` off it only builds and uploads the jar as a
workflow artifact, which is how to check a release candidate.

The former manual release was tagged `50` (its *release name* was `v0.0.1-alpha`) and carried no
`SHA256SUMS.txt`, and the updater reads the tag rather than the release name — so no install could
update from it. It has been retagged `v0.0.1-alpha`; its jar still reports `1.0.0`, which is why the
numbering restarted at `0.0.2-alpha` and why an install of that build needs one manual download.

---

## Docs

| Document | Contents |
|---|---|
| [docs/PROTOCOL.md](docs/PROTOCOL.md) | the protocol, the verified framing, the interception design, the dead ends, the unknowns |
| [docs/INJECTION.md](docs/INJECTION.md) | writing packets: the three orderings, the observability model, the dashboard API |
| [docs/AUTONEXUS.md](docs/AUTONEXUS.md) | the auto-nexus rule: the signal, every gate, every dashboard control |
| [docs/AUTONEXUS-GUIDE.md](docs/AUTONEXUS-GUIDE.md) | the operator's manual for auto-nexus: dry run, thresholds, troubleshooting |

---

## Disclaimer

This project was created for educational and research purposes only. It explores network protocols,
packet serialization and reverse engineering techniques in a controlled environment, and it is not
affiliated with or endorsed by the developers of Darza's Dominion.

Use it on your own account and at your own risk.
