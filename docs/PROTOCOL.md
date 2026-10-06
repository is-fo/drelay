# drelay — protocol and interception notes

Everything here was established from the **shipped client build 5.8.2** (decompiled with
ILSpy) and from live traffic checks on 2026-10-01. Where the original README disagrees with
this document, this document is correct.

## 1. Client shape

| Property | Value |
|---|---|
| Runtime | .NET 9 (`DarzasDominion.runtimeconfig.json`, `Microsoft.NETCore.App 9.0.x`) |
| Framework | MonoGame + SharpDX/SkiaSharp, no IL2CPP, no packing |
| Main assembly | `DarzasDominion.dll`, 50 MB, obfuscated (`hehe_*` identifiers, encrypted string literals) |
| Web API | `https://playdarzas.com/api/v1` |
| Log file | `%LOCALAPPDATA%\RippleStudio\Darza\logs\darza.log` |
| Settings | `%LOCALAPPDATA%\RippleStudio\Darza\settings.dat` (+ `.bak`) |
| Crash reports | `%LOCALAPPDATA%\RippleStudio\Darza\crashes\` |

The shipped DLL embeds its own dependencies' sources — including the full **Telepathy**
networking library and the `isrg_root_x1/x2.pem` trust anchors — so `ilspycmd -p` output is
effectively partial source, not just decompiled IL.

## 2. Service ports

From `DarzaCore.Networking.Ports`:

```csharp
public const int Game = 6410;
public const int Game_Slave = 6411;
public const int Queue = 6412;
public const int WebServer = 6406;
```

## 3. How the client learns each destination

This is the crux of why the old approach could only ever proxy Queue.

```
GET https://playdarzas.com/api/v1/checkversion   ->  {"ip":"playdarzas.com",
                                                      "url":"https://playdarzas.com",
                                                      "queueIp":"queue.playdarzas.com"}
GET https://playdarzas.com/api/v1/serverlist     ->  {"servers":[{"host":"18.145.161.25",
                                                                    "serverName":"Zenith",
                                                                    "region":"USW","time":0}]}
```

* **Queue** is addressed by the *hostname* `queue.playdarzas.com` (from `queueIp`), stored in
  `Queue.Ip` (`DarzasDominion.Darza.Networking.Queue`). A name can be shadowed.
* **Game** is addressed by the **bare IP** in `servers[].host` (`ServerInfo.Host`,
  `DarzaGameNet.Structures.ServerInfo`), consumed at `MainMenuScene` → `Client.CurrentHost` →
  `Client.Connect(host, callback)`. A hosts entry cannot express an IP.
* **Every port is an obfuscated constant.** `Client.Connect(host, callback)` passes
  `_E311_EE2E_F8F8_E14F_E1D6.hehe_F244_F670_F803_F5DD_EB9E(842)` as the port, and `Queue.Port` passes
  id 843. `DarzaCore.Networking.Ports` (`Game`/`Game_Slave`/`Queue`) is therefore dead code in this
  build: nothing references it, and no port literal appears in decompiled code at all, which is why
  searching for `6411` finds only its declaration. Two ids are pinned by observation: the queue
  dialled **6412** and the game dialled **6410**.
* **`Game_Slave` (6411) is never dialled by this client.** `Ports.Game_Slave` and the literal `6411`
  each occur exactly once in the whole decompiled assembly - the declaration itself. It is a
  *server-side* port; the only way the client could reach it is the next bullet.
* **`GmReconnect` is the third way a destination is learned, and the only dynamic one.** The server
  sends `GmReconnect` (game id 36) carrying `Host` (string32), `Port` (varint), `ToBeyond` (bool) and
  an optional `CharacterId` (int64); its handler calls
  `Client.Connect(gmReconnect.Host, gmReconnect.Port, …)`. The client dials whatever the server
  names, so no route table can pin that address in advance - which is exactly what `Game_Slave` is:
  not a host the client chooses, but a port the *server* would name. `networking.Relay` decodes this
  packet and logs the target (§6).

That single asymmetry is the whole "Game and Game_Slave could not be redirected" mystery - and 6411
turns out never to have been a destination at all.

### Confirmed dead ends

* **Editing the stored address.** `Prefs.preferred_server_host` in `settings.dat` *can* be
  changed (see §6), but the client cross-checks it against the live `/serverlist` response and
  clears it when absent (`MainMenuScene` ~line 1079). Verified live: with
  `preferred_server_host = 127.0.0.1` the client still dialled `18.145.161.25:6410`.
* **hosts + custom CA.** The API client installs a
  `ServerCertificateCustomValidationCallback` (`DarzasDominion.Darza.Networking.Api`) that
  builds an `X509Chain` with

  ```csharp
  chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
  chain.ChainPolicy.CustomTrustStore.AddRange(<isrg_root_x1/x2 read from embedded PEM>);
  ```

  so only certificates chaining to the Let's Encrypt ISRG roots are accepted. It also rejects
  anything except a pure name mismatch before that check, which rules out self-signed certs.
  Without a real ISRG-chained certificate there is no way to terminate this TLS.
* **Frida / DLL injection.** Hooking `connect`/`WSASocketW`/`ConnectEx` in-process makes the
  client die within seconds with

  ```
  System.InvalidOperationException: Handle is not initialized.
     at System.Threading.Overlapped.GetOverlappedFromNative(NativeOverlapped*)
     at System.Threading.IOCompletionCallbackHelper.PerformSingleIOCompletionCallback(...)
  ```

  (reproduced 3x; crash reports under `%LOCALAPPDATA%\RippleStudio\Darza\crashes`). The crashes
  tracked *which* hook was installed rather than Frida's mere presence, so the channel was
  narrowed to `ConnectEx` only — and that is where it stops being viable at all.

### Why ConnectEx-only redirection was abandoned

Probed with a small .NET 8 `Socket.ConnectAsync` client, i.e. the same runtime path the game uses,
instrumenting the Winsock and `mswsock` exports from the process. The scripts that did it are not
kept in the repository (the approach is closed off, so they would only rot), but the measurements
are:

| Finding | Evidence |
|---|---|
| `mswsock.dll` does **not** export `ConnectEx` | 63 exports enumerated; none match |
| `WSAIoctl(SIO_GET_EXTENSION_FUNCTION_POINTER)` from our own thread fails | `rc = -1`, `WSAGetLastError = 0` |
| The runtime fetches **only the IPv6** extension GUID | logged GUID `b907a225…` = `{25a207b9-…}` (v6); v4 GUID `d5a34b2f…` never requested |
| Yet the IPv4 connect succeeds | probe reaches `18.145.161.25:6410` and reads the 14-byte opening frame |
| `ws2_32!connect` and `WSAConnect` are **never** called | both hooked at their exports, zero hits |
| All 62 hookable `mswsock` exports were instrumented | only provider-internal `Tcpip4/6_WSHOpenSocket2` and `Tcpip4_WSHGetSockaddrType` fired; no connect entry point |

So the connect entry point is not reachable by name, by GUID lookup, or by hooking the socket
exports, while `ConnectEx` (the one set that could be replaced without the older crash) is
never the function performing the connect. In-process redirection is therefore closed off, and
the address claim is the only remaining hook that needs no binary modification.

Two useful side results from the same probe: `ConnectEx`'s address is `mswsock+0x24a0`, and the
real server's opening frame is stable — a 10-byte payload `5C 00 …` prefixed by `0000000A`, which
appears on every live connect and is a reliable "the session reached a server" marker.

## 4. Wire format — per-service byte order and type width

```
[4 bytes] payload length   (order differs by service, see below)
[N bytes] payload; the payload starts with the packet type, whose width ALSO differs by service
```

| Service | Length prefix | Type field | Written by |
|---|---|---|---|
| Game / Game_Slave | 4 bytes **big-endian** | **2 bytes, little-endian ushort** | `GmPacket.GetData()` writes `TypeId`; Telepathy frames it |
| Queue | 4 bytes **little-endian** | **1 byte** | `QPacket.GetData()` writes `Id` (a byte) and its own length |

The game's two-byte type is the subtle one. Reading `payload[0]` alone is correct for every game id
below 256 — which is all of them except `JumpScare` (320) — so the mistake stays invisible until a
payload is decoded by hand. It was caught on 2026-10-02, when a live `GmReconnect` beginning
`24 00 0E 00 00 00 …` was read as a host of length 3584: with a one-byte type the string length
starts one byte early. `GmPing` is the clean proof — it is a single `DateTime`, and its live payload
is 10 bytes, exactly 2 + 8. `Relay.typeId`/`Relay.bodyOffset` now read the type per service.

The byte order is **not** uniform, and this was settled from live traffic after a static reading
of the decompiled client gave the wrong answer:

| Service | Order | Evidence |
|---|---|---|
| Game / Game_Slave | big-endian | live server sends `00 00 00 0A` before its 10-byte opening Ping (`tools/tests/test_relay_live.py`) |
| Queue | little-endian | live queue session sent `08 00 00 00` (little-endian 8); read big-endian that is 0x08000000 = 134217728 |

The relay originally assumed big-endian everywhere, reasoning from
`Telepathy.ThreadFunctions.ReadMessageBlocking` using `Utils.BytesToIntBigEndian`. That holds for
Telepathy (the game link) but not for the queue link, which the first live queue capture proved by
failing with `implausible payload length 134217728`. `Relay.SessionState` now detects the order
from the first header of a session and reuses it for both directions; `networking.FrameTests`
pins both observed headers.

Details worth keeping:

* The length counts the type field plus the body, and the two services express that differently:
  the queue writes its own prefix and excludes those 4 bytes
  (`BitConverter.GetBytes(array.Length - 4)`), while Telepathy writes the game's and covers
  everything after it.
* `HelloId` (the low byte of the type in the client's opening packet) gates verification
  (`if (Verified || b == HelloId)`); the rest of the handshake is not yet decoded.
* A fresh game-server connection's first server→client packet is a **10-byte payload** whose
  header reads `0000000A` (length 10) and whose type is `0x005C`:

  ```
  0000000A 5C 00 D5 32 F1 92 E7 1F DF 08
           ^^^^^ type 0x005C = 92 (GmPing), then 8 bytes of DateTime
  ```

  `GmPacketType[92]` is `Ping`, and the 9 bytes after the id match a single 64-bit timestamp
  (`GmPing` writes one `DateTime`), so this is the game server's opening ping. Observed on every
  live connect, which makes it a reliable "the session really reached a server" marker.
  (An earlier note here described it as 14 bytes; 14 was the recv() return including the 4-byte
  header. The payload is 10.)
* The original Java proxy wrote **little-endian** lengths while reading with a
  `> 1_000_000 → reverseBytes` heuristic. It happened to read correctly, but re-encoding to LE
  would desync every response. `networking.Relay` uses big-endian on both sides.

## 5. Interception designs (no binary modification)

### Current: claim the server address on the loopback interface

```
   game process
        |
        | dials 18.145.161.25:6410 (a bare IP from /serverlist; nothing is rewritten)
        v
   [that address is claimed on the loopback pseudo-interface]      tools/Set-AddressClaim.ps1
        |                                                         => the dial is delivered locally
        v
   [networking.Relay listens on 0.0.0.0:6410/6411/6412]   and dials the real server from upstreamHost
        |
        v
      real Game / Game_Slave / Queue
```

Run it with `.\drelay.ps1`: it claims each route's address, starts the relay with
`-Ddrelay.upstreamHost=<lan-address>`, follows the relay log, and releases the claims on exit.
Verified 2026-10-01 - the relay's startup dial to `18.145.161.25:6410` left the host through the LAN
address and the real server answered, with no local listener running yet.

Queue is the one service the client addresses by *name*. With the claim it needs no hosts entry,
provided the claimed address is the one DNS returns for `queue.playdarzas.com` (`44.242.119.50`,
measured with `nslookup queue.playdarzas.com 1.1.1.1` - nslookup ignores the hosts file, the Windows
resolver does not). The old `<lan-ip> queue.playdarzas.com` line does still put Queue in the relay's
path while the relay is running, but it must not be left in place: the launcher reports it and leaves
the file alone, and while the relay is stopped that name resolves to a local address nothing is
listening on, so the client's queue connection is refused and it drops back to the start menu instead
of reaching the real queue server. That stale entry was exactly the symptom observed on 2026-10-01.

### Superseded: the WinDivert packet redirect

An earlier version of drelay carried a packet-level redirector (a .NET program using WinDivert) that
rewrote the client's outbound `:6410/:6411/:6412` packets to a local address. Its code is no longer
in the repository, because the address claim replaced it, but the reasons it failed are worth keeping:
they are why the claim is the design it is.

* The redirect is machine-wide on the destination ports, so it also catches the relay's own upstream
  dials, and the relay then dials itself forever. A source-port *range* guard does not separate them,
  because the OS hands the client and the relay ephemeral ports from the same range; the fix was to
  pin the relay's dials to one knowable source port (`upstreamBindPort`) and exclude exactly that
  port from the filter. Windows permits one connection per local port, so this also serialized
  upstream dials and failed a second concurrent session - the queue and game overlapping.
* Rewriting a packet to a local address and re-injecting it did **not** deliver it to a local
  listener (`seen=21 redirected=21` and zero relay sessions; the client timed out).
* Dropping the first SYN and letting the retransmit through made the client connect **directly** to
  the real server: an untouched retransmit still goes to the public address, so the relay became a
  no-op.
* A packet marked with the loopback flag while its peer is a public address is dropped by Windows;
  forcing that flag made the client's own dial time out with the relay logging nothing at all.

The decisive measurement is that the relay must be listening on **the same address the client
dials**, which is what claiming that address achieves.

### A hosts-file trap worth knowing

The hosts line that makes the client's queue connection catchable is
`<lan-ip> queue.playdarzas.com`. That same line makes the **relay** resolve
`queue.playdarzas.com` to its own listen address, so a route table naming that host makes the
relay dial itself - and a reachability check reports "OK", because something is listening: the
relay. Route hosts must therefore be concrete addresses. The relay detects this case explicitly and
says `destination ... resolves to THIS MACHINE`.

### Verification status

| Layer | Verified? | How |
|---|---|---|
| Framing + byte-exact forwarding | yes | `tools/tests/test_relay.py` — 3 packets round-tripped, incl. all 256 byte values |
| Per-service byte order | yes | `networking.FrameTests` (both live headers) + `tools/tests/test_relay_little_endian.py` (both orders end to end) |
| Relay against the **real server** | yes | `tools/tests/test_relay_live.py` — relay carried the genuine `len=10 id=0x5C Ping` handshake both ways |
| Packet primitives and codecs | yes | `networking.PrimitiveTests` |
| Packet-id tables | yes | `tools/verify_packet_ids.py`, name-by-name against the client enums (needs a decompiled client; see the script) |
| Live Queue / Game / Game_Slave capture | **yes** for Queue + Game, Slave idle | the launcher, 2026-10-01: `[Queue#1]` Hello/Join, then a playable `[Game#2]` session (~400 named packets); no `[Game_Slave#N]` at all |
| Realm transition (`GmReconnect` retarget) | yes | 2026-10-02, and `tools/tests/test_reconnect_log.py` offline |

### Every non-elevated redirect route is closed

Measured on this machine, so nobody has to rediscover them:

| Mechanism | Result |
|---|---|
| `hosts` edit | denied (needs admin) |
| `netsh interface portproxy` | "The requested operation requires elevation" |
| `route add` | "requires elevation" |
| `New-NetIPAddress` (loopback alias) | access denied |
| raw socket (ARP/DNS spoofing) | `WinError 10013`, forbidden by access permissions |
| WinDivert without elevation | `Win32Exception: Access is denied` |
| in-process hooking | crashes the client (§3) |

Every one of those routes is closed without elevation, so interception needs exactly one elevated
step: the **address claim** (`tools/Set-AddressClaim.ps1`), which loads no driver and rewrites no
packets.

### Packet-level redirect: three mechanisms, all measured against the real client

The client dials a **bare IP** from `/api/v1/serverlist`, so a hosts entry cannot shadow it, and
rewriting that packet is the obvious approach. It does not work, and the reasons are worth
recording so nobody repeats them.

| Mechanism | Result |
|---|---|
| rewrite destination + re-inject | `[stats] seen=21 redirected=21` and **zero** relay sessions; the client timed out. Rewritten packets are not delivered locally |
| drop the first SYN, forward the retransmit untouched | the client **connected directly to the real server** (its own log shows `[Client] Connected` while the relay logged no game session); an untouched retransmit still goes to the public address, so the relay becomes a no-op |
| rely on local routing | measured: a socket bound to the LAN address dialling the public address on the same port **times out**, and no local listener sees it - Windows sends it out to the real server |

The decisive property: the relay must be listening on **the same address the client dials**.
Nothing short of claiming that address puts the relay in the path.

### The viable mechanism: claim the server address

Assign the game server's address to a local interface so the kernel routes the client's
connection into the local stack, then bind the relay to that address. No packet rewriting, no
injection, no hosts trickery.

`tools/Test-AddressClaim.ps1` probes **both halves** in one elevated run: a listener on the claimed
address plus an *unbound* client (the game client's shape) must meet, and the same listener plus a
client *bound to the LAN address* (the relay's upstream shape) must not meet. It claims on the
loopback pseudo-interface by default, which touches no hardware configuration, no DHCP setting and
no route.

Measured 2026-10-01, elevated, claiming on `Loopback Pseudo-Interface 1` with LAN address
`192.168.0.39`: probe 1 **accepted** the client's dial to `18.145.161.25:6410` (arriving from
`18.145.161.25:55187`), and probe 2's source-bound dial to `203.0.113.7:6410` left the host without
reaching the local listener. The mechanism is confirmed: the relay binds the claimed address
directly and needs no packet rewriting.

#### Two documented Windows behaviours that made the first revision fail and damage the NIC

Measured here on 2026-10-01, on `Ethernet 2`:

* `New-NetIPAddress` **switches DHCP off** on a DHCP-enabled interface
  ([docs](https://learn.microsoft.com/en-us/powershell/module/nettcpip/new-netipaddress): "If you
  run this cmdlet to add an IP address to an interface on which DHCP is already enabled, then DHCP
  is automatically disabled"). The lease goes with it, so a cleanup that removes only the claimed
  address leaves the NIC static with no address at all: APIPA `169.254.x.x`, no default route, no
  DNS, adapter still "Up". Repair with `tools/Restore-DhcpAddressing.ps1`, or by hand:
  `netsh interface ipv4 set address name="<nic>" source=dhcp` then `ipconfig /renew "<nic>"`.
* The same page: "the new IP address is not usable until DAD successfully finishes". Binding
  straight after the add races duplicate address detection, and `Start()` fails with
  `WSAEADDRNOTAVAIL (10049) "The requested address is not valid in its context"` - exactly what the
  first run reported. Wait for `AddressState -eq 'Preferred'`.

#### The claim must live on the loopback interface

The boundary Windows enforces is the **loopback** one, not "any other interface". Measured:

| listener (a local address) | client source | result |
|---|---|---|
| `100.90.214.22` (Tailscale) | bound to `192.168.0.40` (Wi-Fi) | **not** delivered locally |
| `100.90.214.22` (Tailscale) | unbound | delivered locally |
| `192.168.0.40` (Wi-Fi) | bound to `192.168.0.40` | delivered locally |
| `192.168.0.39` (Ethernet) | bound to `192.168.0.40` (Wi-Fi), same subnet | **delivered locally** |
| `127.0.0.1` (loopback) | bound to `192.168.0.39` (Ethernet) | not delivered (`WSAEADDRNOTAVAIL`) |
| claimed `203.0.113.7/32` on loopback | bound to `192.168.0.39` (Ethernet) | not delivered - the dial left the host |

The fourth row is the trap, and it caught this project while the pieces were being wired together: a
claim on a **physical** NIC is not safe, because a dial from another NIC - even one on the same
subnet - can still be delivered locally. `networking.Relay` demonstrated it directly: with the
destination assigned to the Ethernet NIC and its upstream source bound to the Wi-Fi NIC it accepted
its own dials, hundreds of sessions per second, which is the loop signature described below.
Therefore `validateUpstream` refuses to start when a claimed destination sits on a physical
interface, `tools/Set-AddressClaim.ps1` only ever claims on loopback, and the loopback boundary
holds in both directions: the game client (which binds no source) lands in the local stack, while
the relay's source-bound upstream dial leaves the host.

The relay does not merely trust that measurement - `preflightClaimed` proves the escape at startup.
It binds a probe listener on the claimed address first and then dials through `upstreamHost`; if the
dial arrives at that probe, the relay exits (code 3) rather than starting a proxy that would accept
its own dials forever. The check runs **before** any listener starts, so a captured dial cannot be
mistaken for the real server answering.

Configuration consequences:

* `upstreamHost` - the LAN address - is **required** as soon as a route destination is claimed
  (`-Ddrelay.upstreamHost=...` overrides the file), because an unbound dial to a claimed address is
  delivered locally. The relay refuses to start without it, and also if it names a loopback address.
  The launcher works this out and writes it into the run's route table, so it only has to be set by
  hand when the relay is started directly.
* `upstreamBindPort` is unused by the claim path and should stay 0. It existed only to give the
  retired packet filter a source port to exclude, and a pinned port has a real cost: Windows allows
  one connection per local port, so it serializes upstream dials and fails a second concurrent
  session (a queue and a game session overlapping, for instance).
* A server address that is *not* claimed silently bypasses the proxy - the client reaches the real
  server directly - which is why the launcher claims before starting the relay and reports what the
  claim script verified. A realm address is claimed at runtime instead: when a `GmReconnect` names a
  host and port, the relay claims that address and learns the destination before forwarding the
  packet, so the client's follow-up dial cannot escape.
* `tools/Set-AddressClaim.ps1 -Status` reports what is claimed right now, and every runtime claim is
  recorded in `work/learned-claims.txt` so one that was killed can still be released.

### Why the packet redirect was abandoned

The redirector is gone from this repository; the address claim is the interception path. Its
diagnostics were what identified the failures recorded above, and the same questions apply to any
future attempt at a packet-level redirect:

| Observation | Meaning |
|---|---|
| the redirect counters rise, the relay shows no session | the rewrite happens but the packet never reaches a listener (a re-injection problem) |
| the counters stay at zero | the filter is not matching; check the port list and `DstPort` |
| a `notTcp` counter rises while `redirected` stays 0 | non-TCP/IPv4 traffic is matching; the client may be dialling over IPv6 |
| the relay logs `destination UNREACHABLE` | the route table address is stale |

## 6. Packet layer

`src/main/java/networking/packets/` implements the client's serialization model.

> **Writing packets, not just reading them.** The relay also injects now (auto-nexus), which adds two
> orderings this document does not cover: the frame order (a length that *includes* the 2-byte type id)
> and the session-state order (nothing may be written before the client's `Hello` and the server's
> `MapInfo`). Both are in [INJECTION.md](INJECTION.md) §1, along with the event log and the dashboard.

**Primitives** (`GameReader` / `GameWriter`, mirroring `DarzaCore.Tools.ByteReader/ByteWriter`):

| Client call | Wire format | Java |
|---|---|---|
| `Write(int/uint/short/ushort/long/float/double)` | little-endian (.NET `BinaryWriter`) | `writeInt32`, `writeUInt16`, `writeFloatLE`, ... |
| `WriteString8/16/32` | 1/2/4-byte length prefix + UTF-8 bytes | `writeString8/16/32` |
| `Write(bool)` | one byte, 1 or 0 | `writeBool` |
| `Write(GamePoint)` | two little-endian floats | `writePoint` |
| `Write(IntPoint)` | two varints | `writeIntPoint` |
| `WriteVarint(int)` | 6 value bits + sign bit (bit 6) + continuation bit (bit 7), then 7-bit groups starting at bit 6: the groups are **6, 6, 7, 7, …** | `writeVarint` |

The varint grouping is the one place a static reading of the client gave a wrong answer, because the
bit masks live in the encrypted constant pool. It was settled by measurement: `83 0B` is **707**, not
the 1411 that a 6, 7, 7 grouping produces, and the fixed-width `StatsType.Hp` int16 in the matching
`GmUpdate` is what proves it. See [INJECTION.md](INJECTION.md) §2.
`PrimitiveTests` pins the captured bytes.

The little-endian detail is easy to get wrong: the **framing header is big-endian** (4-byte
length) while **every payload field is little-endian**. The legacy proxy wrote little-endian
lengths; `Relay` writes big-endian.

**Ids.** `GmPacketType` (321 entries) and `QPacketType` (5 entries) are generated from the client
enums and verified name-by-name against the decompiled source by
`tools/verify_packet_ids.py`. Neither enum declares explicit values, so the id is the ordinal.

**Two id spaces.** Queue and Game do *not* share ids — queue `1` is `Hello` while game `1` is
`Update`, queue `4` is `Error` while game `4` is `RegisterResp`. Both services use identical
framing, so `PacketRegistry` keeps the registries separate and the relay picks one per route
(`Route.isQueue()`).

**Verified codecs:** on the game side `HealthUpdate` (70), `Escape` (66), `Reconnect` (36),
`EscapeAck` (158), `ForcedEscape` (184), `Kicked` (185), `EscapeCastState` (290) and `SafeAreaState`
(291); on the queue side `Hello` (1), `Position` (2), `Join` (3) and `Error` (4). Everything without a
codec is forwarded byte-for-byte, which is deliberate: re-encoding through a mis-modelled packet
silently corrupts a session.

`Kicked` is decoded for the log rather than for a rule: it carries the server's own string16 reason
for ending the session, which the relay records as `data.reason` on the packet event and as
`kickedReason` on the session-close event. It is the fastest explanation of a failed server→client
rewrite ([INJECTION.md](INJECTION.md) §5.5).

`networking.PrimitiveTests` is the gate:

```powershell
java -cp target/classes;target/test-classes networking.PrimitiveTests
```

It checks varint boundaries and round-trips (including exact byte layouts such as
`varint(250) = BA 03`), little-endian layout of ints/floats, string length prefixes under UTF-8,
`HealthUpdate`'s byte layout, all four queue packets, and the separation of the two id spaces.

### The queue → game handshake (captured 2026-10-01)

The first live capture through the claim (client build 5.8.2) shows the whole admission flow. Queue
is little-endian, game is big-endian, and **Queue hands the client the token that admits it to the
game**:

```
[Queue#1] /44.242.119.50:61652 -> 44.242.119.50:6412
[Queue#1 C->S] first header raw=08000000 -> LITTLE length 8
[Queue#1 C->S] len=8  id=0x01 Hello   01065A656E697468
                       01 Hello, 06 = string8 length, "Zenith"          <- servers[].serverName
[Queue#1 S->C] first header raw=22000000 -> LITTLE length 34
[Queue#1 S->C] len=34 id=0x03 Join    032057767A68517A746B796F6845454E527A50335564536871493942645A316B4F4D
                       03 Join, 20 = 32 bytes, "WvzhQztkyohEENRzP3UdShqI9BdZ1kOM"
[Queue#1 C->S] closed

[Game#2 C->S] first header raw=00000079 -> BIG length 121
[Game#2 C->S] len=121 id=0x37 Hello  3700 28 <24 bytes> 05 "5.8.2" 00 02 7F9739…9E01
                                     20 "WvzhQztkyohEENRzP3UdShqI9BdZ1kOM"      <- the same token
                                     14 "mMYVSvPiVzDT2YcEuqfm" 00 00 …
[Game#2 S->C] len=604 id=0x33 DailyGold    <- admission accepted
[Game#2 S->C] len=163 id=0x05 MapInfo      "The Tavern" / "TheTavernRemade_Halloween.bmap"
[Game#2 S->C] len=8   id=0x46 HealthUpdate
```

So the client asks Queue for the server **by name**, Queue answers `Join` with a 32-character token,
and the client presents that same token inside its game `Hello`, together with the version string
`5.8.2` and a 20-character account token. The session then streams `Update`/`Move`/`Projectiles`/
`Ping` and is fully playable through the relay.

`Game_Slave` (6411) was **never dialled** in this session, which is consistent with §3: the client
reaches it on the same host and that port is closed. That also shows why the two id spaces must stay
separate in `PacketRegistry`: `0x01` is Queue's `Hello` above, and `0x01` is the game's `Update` in
the live stream.

### The one packet that moves a session: `GmReconnect` (36)

Server to client, payload `24 00` onward. Fields in order: `Host` (string32), `Port` (varint),
`ToBeyond` (bool), `CharacterId` (int64, read only when bytes remain, otherwise `-1`). The client's
handler calls `Client.Connect(gmReconnect.Host, gmReconnect.Port, callback)` and *retries* that same
call on failure, so this packet decides where the rest of the session lives.

It is the whole of `Game_Slave`: 6411 is a port the **server** names, not one the client chooses, and
the address rotates per transition. Live capture on entering a realm, 2026-10-02:

```
[Game#2 S->C] len=31 id=0x24 Reconnect (codec)  24000E00000031382E3134352E3231352E3231338B64007F97390000000000
[Game#2 S->C]   RETARGET -> 18.145.215.213:6411  characterId=3774335  toBeyond=false

   24 00              type 36 (two little-endian bytes, see §4)
   0E 00 00 00        host length 14
   "18.145.215.213"   host
   8B 64              varint 6411
   00                 toBeyond
   7F 97 39 00 00 00 00 00    characterId = 3774335
```

The client's own log agrees: `[Client] Reconnecting to 18.145.215.213:6411...` then
`[Client] Reconnect connected, sending hello.` — that time straight to the real server, because the
address was not claimed. The next transition in the same session named `3.71.176.153:6411`, which is
the point: no static route can cover a destination the server invents per transition.

Because this is the one place a claimed-address relay loses a session, the relay acts on the packet
instead of only logging it. **Before** forwarding it (`networking.Relay.onRetarget`):

1. it claims the named address on the loopback pseudo-interface with
   `netsh interface ipv4 add address <loopback index> <ip> 255.255.255.255 store=active` — the same
   session-only store as `Set-AddressClaim.ps1 -SessionOnly` — refusing any address a real interface
   owns;
2. it records `host:port` in `Config.learned` for that port, which `handleSession` consults before
   the route table.

The write happens after both, so the client cannot dial the new address before the claim exists: the
transition pauses for the claim instead of escaping. Both steps are best effort — an unelevated relay
cannot claim, says so, and forwards anyway. Runtime claims are released by a shutdown hook and listed
in `work/learned-claims.txt`, so `Set-AddressClaim.ps1 -Release` (and `drelay.ps1 -Stop`) can clean
up after a relay that was killed.

Decoding never alters forwarding: the payload is still written on byte for byte, so a mis-read can
cost a log line but never a byte of the session.

## 7. settings.dat format

A flat key/value blob obfuscated with a **+1 byte shift on printable bytes** (`https` is stored
as `httpu`). `tools/decode_settings.py` reads it and can rewrite it:

```powershell
python tools/decode_settings.py                                   # dump keys/values
python tools/decode_settings.py --write out.dat --replace OLD NEW  # re-encode with an edit
```

Keys seen: `last_web_server_ip`, `preferred_server_host`, `preferred_server_name`,
`access_token`, `auto_activate`, `camera_offset`, plus UI/audio settings. Note that
`preferred_server_host` is validated against the server list at startup (§3), so editing it is
useful for inspection but cannot retarget the client.

## 8. Tooling used

| Tool | Purpose |
|---|---|
| `ilspycmd` | decompile the shipped assembly for analysis; primary source of truth |
| `networking.Relay` (`src/main/java`) | the relay: per-route listeners, byte-exact forwarding |
| `networking.Launcher` | the packaged entry point: elevation, updates, claims, child relay |
| `tools/Set-AddressClaim.ps1` | claims and releases the server addresses on loopback |
| Java 25+ (`javac`, Maven or `build.ps1`) | the relay build; no third-party runtime dependencies |
| `tools/tests/test_relay.py`, `tools/tests/test_relay_little_endian.py`, `tools/tests/test_relay_stress.py` | framing, byte order and load, without the game |
| `tools/tests/test_relay_live.py` | the relay in front of the real server |
| `tools/decode_settings.py` | `settings.dat` codec |

Earlier versions of this project also used a WinDivert packet redirector, a Frida-based in-process
hook, and ConnectEx interception. All three are recorded above as failures, their code is not part of
the repository, and `tools/` no longer ships any of it: the address claim replaced them.

## 9. Still unknown

1. The queue handshake and match payloads — **captured 2026-10-01** through the claim, without any
   hosts entry: `[Queue#1]` `Hello`/`Join` and a full `[Game#2]` session; see §6.
2. **When** `GmReconnect` fires — **resolved 2026-10-02**: it fires when the client changes server.
   The first observed cases are entering a realm on `Game_Slave` (port 6411) and returning to the
   lobby (port 6410). The relay now claims and learns the named destination before forwarding the
   packet, so the session stays in the proxy. There is nothing to pre-claim, because the address
   only exists once the server names it.
3. Game_Slave — **resolved: it is not a client destination at all.** §3: `Ports.Game_Slave` and the
   literal `6411` occur exactly once each in the decompiled client (the declaration), the ports the
   client actually dials come from the obfuscated constant table (6412 queue, 6410 game, both
   observed live), and one full login → queue → character → game session produced no dial to 6411 at
   all — the route's listener saw nothing. The README-era `44.222.253.93` was a hard-coded line in
   the legacy proxy, never an observation. `6411` is a *server-side* port: the client would
   only dial it if the server sent a `GmReconnect` naming it. `relay-routes.json` keeps the route,
   marked `optional`, so such a dial is captured rather than bypassing the proxy, while its closed
   port is reported as a note instead of a warning.
