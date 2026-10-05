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
java -jar drelay.jar -Ddrelay.strip.confused=true         # remove the Confused debuff from this client
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

---

## Watching a session

| | |
|---|---|
| **Dashboard** | <http://127.0.0.1:8765/> — live HP, the auto-nexus state, and a filtered packet stream |
| **Event log** | `work/logs/events-<run>.jsonl` — every event, with raw payload and decoded fields |
| **Nexus log** | `work/logs/nexus-<run>.jsonl` — the escape story only |
| **Relay console** | `work/logs/relay-console.out` — the relay's own output, mirrored from the launcher's window |

Auto-nexus injects `GmEscape` when HP falls below a threshold. It is off by default and starts in dry
run when switched on: [docs/AUTONEXUS-GUIDE.md](docs/AUTONEXUS-GUIDE.md) is the operator's manual,
[docs/AUTONEXUS.md](docs/AUTONEXUS.md) is how it works. It is reactive — it reads the server's own
health packet, it does not predict damage.

The `strip` block is the one setting that changes bytes the server sent: it removes a status effect
(`Confused` by default) from this client's status lists, because that effect is applied client-side
and never expires on a timer. Off by default; [docs/INJECTION.md](docs/INJECTION.md) §5 has the wire
format, the reason a shortened duration does nothing, and what it refuses to rewrite.

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
| `networking` | `Launcher` (entry point), `Relay` (listeners and forwarding), `Session`, `AutoNexus`, `PlayerLocator` |
| `networking.packets` | packet codecs and the registry, the injection primitive, `UpdateScan` (the `GmUpdate` walk), `ConfusedStrip` |
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

python tools\tests\test_relay.py                   # framing, byte-exact both directions
python tools\tests\test_relay_little_endian.py     # both length orders end to end
python tools\tests\test_relay_stress.py            # both directions under concurrent load
python tools\tests\test_reconnect_log.py           # a GmReconnect retarget is decoded and learned
python tools\tests\test_auto_nexus.py              # the auto-nexus chain against a fake game server
python tools\tests\test_strip_confused.py          # a server->client rewrite, against a fake game server
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

Bump the version in **both** places — `src/main/resources/drelay.properties` (what the jar reports
and the updater compares) and `pom.xml` — commit, then tag the commit:

```powershell
git tag v1.0.1
git push origin v1.0.1
```

The release workflow fails before publishing if the tag, `drelay.properties` and `pom.xml` disagree.
It then builds the jar on Java 25, runs the offline checks, verifies that the jar is runnable and
carries its bundled files, attaches `drelay.jar` and `SHA256SUMS.txt` to a GitHub release, and attests
the build provenance. The workflow can also be run manually from the Actions tab: it builds and
uploads the jar as a workflow artifact, and publishes only if the `publish` input is set.

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
