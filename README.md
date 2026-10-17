# drelay

Tools for observing and steering a **Darza's Dominion** session. The repository holds two separate
products that happen to share a repository, a game build, and a research history:

| Component | What it is | Where |
|---|---|---|
| **proxy** | A local TCP relay in Java. It captures the client's own connection - with a packet redirector when installed, otherwise by claiming the game server addresses on the loopback interface - so a local listener can decode, log and write packets back before forwarding the session to the real server. It ships as a Windows zip containing `drelay.jar` and the redirector, and its jar updates itself. | [`proxy/`](proxy/) |
| **hook** | An in-process C++ / .NET hook. It is injected into the game process before `Main`, matches its detour targets by the SHA-256 of their IL bytes, and works inside the game's own frame — where the latency budget makes a decision impossible to send out to another process. | [`hook/`](hook/) |

They are deliberately **not** one program. The proxy is a network tool that neither reads nor writes
the game's memory; the hook is a memory tool that never touches the wire. Where they meet — a
shared-memory bridge fast enough to be usable, and the proof that the decision cannot cross it — is
specified in [`docs/bridge/`](docs/bridge/), and is not built yet.

Neither component is a game client. Nothing here modifies any file in the game's installation.

---

## Layout

```
proxy/                    the Java relay — its own Maven project, its own release
  src/main/java/            the relay, the launcher and the dashboard
  src/main/resources/       the route-table template and helpers, shipped inside the jar
  src/test/java/            the offline checks (standalone main classes, no test framework)
  tools/                    operator and recovery scripts
  tools/tests/              the end-to-end checks that drive a real relay subprocess
  redirector/               drelay-redirect.exe: the WinDivert capture used by --redirect
  work/                     the relay's runtime state: event logs, generated route tables (ignored)

hook/                     the native + managed hook — its own build, its own release
  native/                   Drelay.Hook.dll: the injected side
  managed/                  Drelay.Bootstrap.dll and Drelay.Launcher.exe
  tests/                    SyntheticHost and MapProbe, the fixtures the offline checks run against
  thirdparty/MinHook/       the vendored detour library
  tools/                    the build/verify instruments and the trace-analysis reports
  known-build.json          the pinned game build every target offset is relative to
  build.ps1                 builds all three artefacts into out/
  work/  il/  .tools/       scratch, the IL dump and local tool installs (all ignored)

docs/
  proxy/                    the relay's documentation
  hook/                     the hook's research and verification record
  bridge/                   the Java ⇄ hook bridge
```

Each component's README is the manual for that component, and every path inside it is relative to
that component's directory:

- [`proxy/README.md`](proxy/README.md) — requirements, running it, the command line, the dashboard,
  the update mechanism, the release procedure.
- [`hook/README.md`](hook/README.md) — the phases, what is built, how to build it, what the checks do,
  and what the gate cannot check.

---

## Building and running

Both components need their own toolchain and neither needs the other.

```powershell
# the relay: a JDK 25 or newer, nothing else
cd proxy
.\build.ps1            # build target\drelay.jar, then run the offline checks
.\drelay.ps1           # build if needed, then run (asks for administrator rights)

# the hook: Windows, MSVC, the .NET 8 SDK and Python
cd hook
.\build.ps1                    # native DLL, bootstrap, launcher -> out\
.\tools\Test-HookOffline.ps1   # every check that does not need the game installed
```

The downloaded relay is a Windows zip containing the jar and the packet redirector; extract it and
run the jar, and the jar updates itself. The hook is shipped as a zip that is unpacked next to the
game. See the two READMEs for the details, including why the hook will refuse to install against a
game build whose IL hashes do not match the ones it was built for.

---

## Documentation

The two document sets are genuinely different in kind, which is why they are separate directories.

| Directory | What is in it | Start here |
|---|---|---|
| [`docs/proxy/`](docs/proxy/) | The wire: framing, the verified packet layouts, injection, the auto-nexus rule and its operator's manual, the interception design and the dead ends. | [`PROTOCOL.md`](docs/proxy/PROTOCOL.md) |
| [`docs/hook/`](docs/hook/) | The game binary: the pinned build and how to re-verify it after a patch, the client's structure, the hook's architecture and threat model, and the phase-by-phase record of what was built and measured. | [`HOOK-BUILD-MATRIX.md`](docs/hook/HOOK-BUILD-MATRIX.md) |
| [`docs/bridge/`](docs/bridge/) | The Java ⇄ hook bridge: transport, binding, the latency argument that decides where the decision has to live, and the options that were rejected. | [`JAVA-BRIDGE.md`](docs/bridge/JAVA-BRIDGE.md) |

---

## Releases

Both components release from this repository, into one shared release list, and the two streams are
kept apart by their **tag namespaces** rather than by a path filter:

| | Tag | Asset | Pipeline |
|---|---|---|---|
| proxy | `v<version>` | `drelay-<version>-win64.zip` (jar + redirector) + `drelay.jar` + `SHA256SUMS.txt` | [`.github/workflows/proxy-release.yml`](.github/workflows/proxy-release.yml) |
| hook | `hook-v<version>` | `drelay-hook-<version>.zip` + `SHA256SUMS.txt` | [`.github/workflows/hook-release.yml`](.github/workflows/hook-release.yml) |

In both cases a release is a **version bump merged to master**: the workflow tags the commit it built
and publishes only if that tag does not already exist, so a push that bumped nothing is a no-op.

Two details are load-bearing and easy to break:

- **GitHub Actions does not evaluate path filters for tag pushes.** The `paths` filters in these
  workflows trim branch pushes only; a pushed tag starts its workflow regardless. The tag prefix is
  what keeps `v0.0.3` out of the hook's pipeline and `hook-v1.0.0` out of the relay's.
- **The relay's auto-updater reads the same release list**, so it decides what is installable from
  the tag's shape and from the presence of an asset named `drelay.jar`
  (`Updater.isProxyTag`). A hook release has neither and is never offered as a relay update.
  `networking.UpdaterTests` pins both halves of that.

### What CI does and does not verify

| Pipeline | Runs |
|---|---|
| [`proxy-build.yml`](.github/workflows/proxy-build.yml) | Maven build on JDK 25, the offline checks, the packaged jar's manifest and contents, and that no test class is inside it. |
| [`hook-build.yml`](.github/workflows/hook-build.yml) | The full native + managed build, then every check that depends only on the sources. |

The hook's most important checks are **pinned to one installed build of one game** — the fingerprint
gate, the re-derivation of the target table from the real assembly, and the end-to-end injection
test — and a CI runner has no copy of that game. They are not silently skipped: they run locally,
[`docs/hook/HOOK-BUILD-MATRIX.md`](docs/hook/HOOK-BUILD-MATRIX.md) is the procedure and the order, and
[`hook/tools/Test-HookOffline.ps1`](hook/tools/Test-HookOffline.ps1) names every one it did not run
on each invocation. A green hook build is never a claim that the hook was verified against the game.

---

## Disclaimer

This project was created for educational and research purposes only. It explores network protocols,
packet serialization and reverse engineering techniques in a controlled environment, and it is not
affiliated with or endorsed by the developers of Darza's Dominion.

Use it on your own account and at your own risk.
