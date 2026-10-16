# drelay ⇄ C++ hook bridge: implementation-oriented technical report

Scope: Java 25 ↔ an injected C++ DLL inside a .NET 9 x64 Windows game process. Budget: event → Java → decision back, target well under 16 ms; ~200 msg/s; ~150 B/msg; Windows 10/11 x64 only; keep `drelay`'s "no external dependencies, plain `javac`" property if possible.

Every claim below carries a source link. Where I could not verify something I say so explicitly. Numbers from blogs/StackOverflow are labelled **informal**.

---

## TL;DR recommendation

| Decision | Choice |
|---|---|
| Transport | **Pagefile-backed named shared-memory section** (`CreateFileMapping(INVALID_HANDLE_VALUE, …, "Local\\DrelayHookShm")`) containing a **sequence-stamped SPSC ring**; **loopback TCP** as the control/lifecycle channel (you already have socket code). |
| Binding | **JNA, `jna.jar` only (not `jna-platform`)** — used **at attach time only** (`OpenFileMapping`, `MapViewOfFile`, `CloseHandle`). Zero JNA calls on the hot path. |
| Zero-dependency fallback | **Pure Java `FileChannel.map(MapMode.READ_WRITE, 0, SIZE, Arena)` over a file both processes map** — no JNA, no FFM restricted methods, no dependency at all. |
| Latency result | Transport contributes **≲1 µs**; the **frame quantum dominates**. At 144 fps a round trip is ~7–8 ms; at 60 fps it is ~17 ms. Move the escape decision into C++. |
| Does JNA suffice? | **Yes, decisively.** Nothing else is *clearly* better for this use case. See §1.6. |

---

## 1. JNA vs JNI vs FFM

### 1.1 JNA facts

| Item | Value | Source |
|---|---|---|
| Latest version | **5.19.1** (`<release>5.19.1</release>`) | [jna maven-metadata.xml](https://repo1.maven.org/maven2/net/java/dev/jna/jna/maven-metadata.xml) |
| Coordinates | `net.java.dev.jna:jna:5.19.1`, `net.java.dev.jna:jna-platform:5.19.1` | [JNA README](https://raw.githubusercontent.com/java-native-access/jna/master/README.md) |
| JPMS variants | `jna-jpms`, `jna-platform-jpms` (since 5.8.0) | [JNA FAQ](https://raw.githubusercontent.com/java-native-access/jna/master/www/FrequentlyAskedQuestions.md) |
| `jna-5.19.1.jar` size | **1,950,853 bytes ≈ 1.86 MiB** | [Central listing](https://repo1.maven.org/maven2/net/java/dev/jna/jna/5.19.1/) |
| `jna-platform-5.19.1.jar` size | **1,416,385 bytes ≈ 1.35 MiB** | [Central listing](https://repo1.maven.org/maven2/net/java/dev/jna/jna-platform/5.19.1/) |
| License | **LGPL-2.1-or-later OR Apache-2.0** (dual, you choose) | [jna-5.19.1.pom](https://repo1.maven.org/maven2/net/java/dev/jna/jna/5.19.1/jna-5.19.1.pom), [README](https://raw.githubusercontent.com/java-native-access/jna/master/README.md) |
| Native engine | **libffi** (`ffi_prep_cif` / `ffi_call`) | [FunctionalDescription.md](https://raw.githubusercontent.com/java-native-access/jna/master/www/FunctionalDescription.md) |
| JDK 24+ requirement | Documented in 5.18.0: PR #1669 "Document requirement for running on JDK 24+" | [CHANGES.md L60](https://raw.githubusercontent.com/java-native-access/jna/master/CHANGES.md) |

**How JNA loads a native library** (verbatim from the `Native` class javadoc and `loadNativeDispatchLibrary()`):

> "An attempt is made to load it from the any paths defined in `jna.boot.library.path` (if defined), then the system library path using `System.loadLibrary`, unless `jna.nosys=true`. If not found, the appropriate library will be extracted from the class path (into a temporary directory if found within a jar file) and loaded from there, unless `jna.noclasspath=true`."

Sources: [Native.java L75–104 (5.19.1)](https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Native.java), `loadNativeDispatchLibrary()` at L1010, and the `jna.nosys` default of `"true"` at L1070 (verified in the released `jna-5.19.1-sources.jar`).

### 1.2 The four JNA call styles and their real costs

| Style | Mechanism | Cost |
|---|---|---|
| **Interface mapping** (`extends Library`, `Native.load`) | JDK dynamic proxy → `Library.Handler.invoke` → `Function.invoke` → runtime type mapping → `dispatch` (`native/dispatch.c`) → `ffi_call` | **1346 ± 72 ns/call** (measured) |
| **Direct mapping** (`Native.register`) | Reflection once at registration to build a libffi stub per method → `dispatch_direct` | **1373 ± 70 ns/call** (measured) — *not* faster for this shape |
| **`Structure`** | `autoWrite()` copies Java fields → native memory before the call, `autoRead()` copies back after | Dominates the full benchmark: 4037 ns vs 1346 ns call-only |
| **`Callback`** (upcall) | "implemented by wrapping a Java object method in a little bit of C glue code" | No published JNA upcall number found; docs warn the callback object **must be kept alive or "the native callback invocation will probably crash"** |

Sources: [FunctionalDescription.md](https://raw.githubusercontent.com/java-native-access/jna/master/www/FunctionalDescription.md), [DirectMapping.md](https://raw.githubusercontent.com/java-native-access/jna/master/www/DirectMapping.md), [CallbacksAndClosures.md](https://raw.githubusercontent.com/java-native-access/jna/master/www/CallbacksAndClosures.md).

JNA's own performance claims, **quoted verbatim** ([FAQ](https://raw.githubusercontent.com/java-native-access/jna/master/www/FrequentlyAskedQuestions.md)):

> "The calling overhead for a single native call using JNA interface mapping can be an order of magnitude (~10X) greater time than equivalent custom JNI … In raw terms, the calling overhead is on the order of **hundreds of microseconds** instead of tens of microseconds."

> "You might expect a speedup of about an order of magnitude moving to JNA direct mapping, and a factor of two or three moving from there to custom JNI."

⚠️ **The JNA FAQ's unit is wrong.** "Hundreds of microseconds" for JNA and "tens of microseconds" for JNI is off by ~1000×; both should read nanoseconds. Measured values are ~1.35 µs (JNA) vs ~57 ns (JNI) — a ~24× ratio, not 10×, and not microseconds. Report this discrepancy rather than repeating it.

### 1.3 Measured numbers (the two real benchmarks I found)

**A. `java-native-benchmark` (JMH; Intel i7-10610U, Windows 10, OpenJDK 19.0.1).** Calls `kernel32!GetSystemTime` with a `SYSTEMTIME` struct.
Source: [archenroot/java-native-benchmark-archenroot README](https://raw.githubusercontent.com/archenroot/java-native-benchmark-archenroot/master/README.md) (fork of zakgof's project; results dated 2023-08-09).

```
Native call only (average time, smaller is better)
JmhCallOnly.jna_direct     1373.435 ±  70.343  ns/op
JmhCallOnly.jna            1346.036 ±  72.239  ns/op
JmhCallOnly.bridj           383.992 ±  50.000  ns/op
JmhCallOnly.jnr             298.334 ±  48.785  ns/op
JmhCallOnly.jni_javacpp      56.605 ±   8.087  ns/op
JmhCallOnly.foreign          49.717 ±   6.667  ns/op

Full benchmark (struct alloc + call + field read)
JmhGetSystemTimeSeconds.jnaDirect   4517.766 ± 417.656  ns/op
JmhGetSystemTimeSeconds.jna         4037.103 ± 681.270  ns/op
JmhGetSystemTimeSeconds.jni_javacpp  259.521 ±   7.964  ns/op
JmhGetSystemTimeSeconds.foreign      237.920 ±  30.081  ns/op
```

**This is the single most directly relevant data point**: it is a Windows/x64 measurement of exactly the operation this project needs (open a Windows DLL, call a function, read fields). JNA ≈ 24× JNI/FFM per call.

**B. `Glavo/java-ffi-benchmark` (JMH; AMD Ryzen 7 5800X, Ubuntu 22.04, Java 21).**
Source: [Glavo/java-ffi-benchmark README](https://raw.githubusercontent.com/Glavo/java-ffi-benchmark/master/README.md). Linux, not Windows, but the *ratios* are informative:

- `NoopBenchmark` (empty C function): JNA = **6.7 %** of JNI throughput; JNA direct mapping = **7.2 %**; JNR with `@IgnoreError` ≈ 84 %; **FFM default = 113 % of JNI** (13 % faster); FFM with the trivial/leaf linker option = **160 % of JNI**.
- `QSortBenchmark` (Java method used as a C callback): **"the performance of Panama is about 3.5~4 times that of JNI."** ← the upcall answer.

⚠️ Note the API rename: Glavo's README (written against the Java 21 preview) says `Linker.Option.isTrivial()`. In the **final** API the option is `Linker.Option.critical(boolean allowHeapAccess)`, documented as "a function that has an extremely short running time in all cases … and does not call back into Java". Source: [Java 25 `Linker` javadoc](https://docs.oracle.com/en/java/javase/25/docs/api/java.base/java/lang/foreign/Linker.html), [Linker.java](https://raw.githubusercontent.com/openjdk/jdk/master/src/java.base/share/classes/java/lang/foreign/Linker.java).

**Verdict on JNA call overhead: ~1.3–1.4 µs per call, and ~4 µs if a `Structure` is marshalled.** Not microseconds.

### 1.4 JNI

- **Raw C++ surface**: a `native` method declaration, a generated header (`javac -h`), and a C/C++ implementation, kept in sync across two toolchains. JEP 454's motivation states it plainly: "JNI involves several tedious artifacts: a Java API (`native` methods), a C header file derived from the Java API, and a C implementation that calls the native library of interest. Java developers must work across multiple toolchains to keep platform-dependent artifacts in sync." It also notes "passing Java objects via JNI is slow" and that JNI "does not reconcile the Java type system with the C type system". Source: [JEP 454](https://openjdk.org/jeps/454).
- **Performance**: measured **56.6 ns/call** Windows/x64 (above); the fastest upcall path besides FFM.
- **Deployment complication added in JDK 24**: JNI loading/linking is now a *restricted* operation. [JEP 472](https://openjdk.org/jeps/472), Status Closed/Delivered, Release 24. Consequences on Java 25:
  - `System::loadLibrary`, `System::load`, `Runtime::loadLibrary`, `Runtime::load` and **declaring a `native` method** require native access.
  - Default behaviour is `--illegal-native-access=warn`: **at most one warning per module**, written to stderr. `deny` (throwing `IllegalCallerException`) "will become the default in a future release".
  - Opt out with `--enable-native-access=ALL-UNNAMED` or the JAR manifest attribute `Enable-Native-Access: ALL-UNNAMED` (only valid value; only for `java -jar`).
  - Context: "An analysis of artifacts on Maven Central found that about 7% of existing artifacts depend on native code. Of these, about 25% use JNI directly" ([JEP 472](https://openjdk.org/jeps/472)).

### 1.5 FFM (`java.lang.foreign`) status in Java 25

- **Final, not preview.** [JEP 454](https://openjdk.org/jeps/454): "Status: Closed / Delivered", "Release: 22". `Linker` is `@since 22`. **No `--enable-preview` is required on Java 25.**
- Classes confirmed present in the Java 25 javadoc: `Linker` (`nativeLinker()`, `downcallHandle(...)`, `upcallStub(...)`, `defaultLookup()`, `canonicalLayouts()`), `Arena` (`ofConfined`, `ofShared`, `ofAuto`, `global`), `MemorySegment`, `ValueLayout`, `Linker.Option.critical`, `Linker.Option.captureCallState`.
- **Upcalls: yes.** `Linker::upcallStub(MethodHandle target, FunctionDescriptor function, Arena arena, Linker.Option... options)` returns "a zero-length segment whose address is the address of the upcall stub". Hard caveat from the javadoc: **"The target method handle should not throw any exceptions. If the target method handle does throw an exception, the JVM will terminate abruptly."**
- **Restricted methods** (Java 25 javadoc marks each `[RESTRICTED]`): `Linker::downcallHandle` (both overloads), `Linker::upcallStub`, `MemorySegment::reinterpret`, `SymbolLookup::libraryLookup`.
- **Flags on Java 25**: restricted calls warn by default (once per module). `--enable-native-access=ALL-UNNAMED` (or per-module) removes the warning. `--illegal-native-access=deny` turns them into `IllegalCallerException` today if you want to test forward-compatibility. Warnings become errors "in a future release" ([JEP 454 §Safety](https://openjdk.org/jeps/454), [JEP 472](https://openjdk.org/jeps/472)).
- **Credible dependency-free replacement for JNA?** For *plain functions with primitives and pointers*: yes, and it is ~27× faster per call. It is **not** a drop-in replacement for JNA's pre-built Win32 `Structure` bindings or its `Callback`/`Structure` ergonomics, and you lose the ability to just `Native.load("kernel32", Kernel32.class)` — you write the `FunctionDescriptor`s and own the `Arena` lifetimes yourself.

### 1.6 JNI vs FFM vs JNA — latency and upcalls specifically

| | Downcall latency | Upcall (native → Java) | Build cost | Runtime opt-in on Java 25 |
|---|---|---|---|---|
| **JNI** | ~57 ns (Win/x64) | Fastest after FFM; requires `AttachCurrentThread` bookkeeping | **High**: header + C stub + two toolchains | `--enable-native-access` (JEP 472) |
| **FFM** | ~50 ns (Win/x64); 113–160 % of JNI throughput | **3.5–4× JNI throughput** — the best upcall path | **Low**: pure Java, no C stub for the *binding* | `--enable-native-access` for restricted subset only |
| **JNA interface** | ~1346 ns (Win/x64) | Works, but no published numbers; GC-safety footgun | **Lowest** | `--enable-native-access` (JNA calls `System.load` + declares `native` methods) |
| **JNA direct** | ~1373 ns (Win/x64) | n/a | Low | same |

**Does JNA suffice here, or is something else clearly better?**

**JNA suffices, and nothing is *clearly* better for this specific use case** — because in the recommended architecture the binding layer is **off the hot path entirely**. At 200 msg/s with ~10 JNA calls per session (attach, map, close), the total JNA cost is ~13 µs *per session*, i.e. ~0.0003 % of a 16 ms budget. The 24× JNA-vs-FFM per-call gap is real but irrelevant at this call volume.

Ranking if the binding *were* hot: FFM > JNI > JNA on latency; FFM > JNA > JNI on effort. JNI is **strictly dominated** — same latency class as FFM, far more build complexity, and equally restricted by JEP 472.

Honest caveat: if you later want per-frame *native reads that require a function call each* (e.g. calling into a DLL exported function 144×/s to pull state), prefer FFM or pure `MemorySegment` access. Do **not** use JNA interface mapping in that loop.

---

## 2. Inter-process transport

Note first: the parent's framing is important — **the hook is in a different process**, so in-process bindings (JNA/JNI/FFM) are only the *door*, not the corridor. Peer-reviewed context: Smith & Wells, *"Interprocess Communication with Java in a Microsoft Windows Environment"*, SACJ 29(3), 2017 — Java "currently lacks interprocess communication (IPC) facilities, other than slow socket-based communication mechanisms", and their JNI-based native-IPC implementation showed "the performance dichotomy between socket-based communication and native IPC facilities, with Windows' facilities providing significantly faster communication" ([abstract](https://sacj.cs.uct.ac.za/index.php/sacj/article/view/500), DOI [10.18489/sacj.v29i3.500](https://doi.org/10.18489/sacj.v29i3.500) — **I verified the abstract only; the PDF could not be fetched by my tools**).

### 2.1 Windows named pipes from Java

**What Java actually does.** Java's Windows file-open path is `sun.nio.fs.WindowsChannelFactory.open()`, which calls:

```c
CreateFile(pathForWindows,
           dwDesiredAccess,       // GENERIC_READ|GENERIC_WRITE for "rw"
           dwShareMode,           // FILE_SHARE_READ|WRITE|DELETE by default
           pSecurityDescriptor,
           dwCreationDisposition, // OPEN_EXISTING for an existing path
           dwFlagsAndAttributes); // FILE_ATTRIBUTE_NORMAL; FILE_FLAG_OVERLAPPED only for AsynchronousFileChannel
```
Source: [WindowsChannelFactory.java L222–334](https://raw.githubusercontent.com/openjdk/jdk/master/src/java.base/windows/classes/sun/nio/fs/WindowsChannelFactory.java) (verified verbatim).

⇒ `new RandomAccessFile("\\\\.\\pipe\\drelay-hook", "rw").getChannel()` produces a **valid, synchronous (blocking), non-overlapped handle to a named pipe**, with no way to request overlapped I/O.

**The three hard limitations:**

1. **Message mode is unreachable from Java's public API.** Microsoft: *"For a pipe client, a pipe handle returned by `CreateFile` is always in byte-read mode initially. Both pipe clients and pipe servers can use the `SetNamedPipeHandleState` function to change the read mode of a pipe handle. The pipe handle must have the `FILE_WRITE_ATTRIBUTES` access right."* ([Named Pipe Type, Read, and Wait Modes](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-type-read-and-wait-modes)); and *"The client side of a named pipe starts out in byte mode, even if the server side is in message mode. To avoid problems receiving data, set the client side to message mode as well."* ([CreateNamedPipe](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea)). `java.nio.channels.FileChannel` has **no** API that maps to `SetNamedPipeHandleState`. ⇒ **Java cannot read a message-mode named pipe in message mode.** The workaround is universally the same: **do your own length-prefix framing** over a byte stream (which you need anyway for resync after a dropped byte).
2. **No overlapped I/O.** Overlapped mode is only set when `flags.overlapped = true`, which `WindowsChannelFactory` sets *only* from `newAsynchronousFileChannel(...)` — i.e. only via `AsynchronousFileChannel`, which is not usable on a pipe path through the public API. ⇒ blocking `ReadFile` on a synchronous handle; `Thread.interrupt()` will not unblock it; only closing the channel will.
3. **`FileChannel.open(Path)` on a pipe path is unverified.** `FileChannel.open` goes through `WindowsFileSystemProvider` (path normalisation, `toWindowsPath`), and I could **not** find documentation or a test asserting it works on `\\.\pipe\…`. `RandomAccessFile` is the empirically-used route. I also could not fetch the canonical StackOverflow answer ([SO 634564](https://stackoverflow.com/questions/634564/how-to-open-a-windows-named-pipe-from-java)) — StackOverflow returned HTTP 403 to my fetcher. **Treat `FileChannel.open` on a pipe as UNVERIFIED and test it in the spike.**

**Evidence the JDK *does* support pipes on this path:** `FileDispatcherImpl.read0` explicitly special-cases the pipe error codes:

```c
if (error == ERROR_BROKEN_PIPE) return IOS_EOF;         // 109
if (error == ERROR_NO_DATA)    return IOS_UNAVAILABLE;  // 232
```
Source: [FileDispatcherImpl.c, `Java_sun_nio_ch_FileDispatcherImpl_read0`](https://raw.githubusercontent.com/openjdk/jdk/master/src/java.base/windows/native/libnio/ch/FileDispatcherImpl.c). Those are *named-pipe* error codes, so this branch only exists because pipes are expected to arrive here.

**JNA route (recommended if you want true message mode).** All of these exist in `com.sun.jna.platform.win32.Kernel32` **in the released 5.19.1 artifact** (verified by grepping `jna-platform-5.19.1-sources.jar`):

```java
public HANDLE   CreateNamedPipe(String lpName, int dwOpenMode, int dwPipeMode,
                                int nMaxInstances, int nOutBufferSize, int nInBufferSize,
                                int nDefaultTimeOut, SECURITY_ATTRIBUTES lpSecurityAttributes);
public boolean  ConnectNamedPipe(HANDLE hNamedPipe, OVERLAPPED lpOverlapped);
public boolean  DisconnectNamedPipe(HANDLE hNamedPipe);
public boolean  SetNamedPipeHandleState(HANDLE hNamedPipe, IntByReference lpMode,
                                        IntByReference lpMaxCollectionCount,
                                        IntByReference lpCollectDataTimeout);
public boolean  TransactNamedPipe(HANDLE hNamedPipe, byte[] lpInBuffer, int nInBufferSize,
                                  byte[] lpOutBuffer, int nOutBufferSize,
                                  IntByReference lpBytesRead, OVERLAPPED lpOverlapped);
public boolean  WaitNamedPipe(String lpNamedPipeName, int nTimeOut);
public boolean  CallNamedPipe(String lpNamedPipeName, byte[] lpInBuffer, int nInBufferSize,
                              byte[] lpOutBuffer, int nOutBufferSize, IntByReference lpBytesRead,
                              int nTimeOut);
public HANDLE   CreateFile(String lpFileName, int dwDesiredAccess, int dwShareMode,
                           SECURITY_ATTRIBUTES lpSecurityAttributes, int dwCreationDisposition,
                           int dwFlagsAndAttributes, HANDLE hTemplateFile);
```
Constants in `com.sun.jna.platform.win32.WinBase`: `PIPE_ACCESS_DUPLEX = 0x00000003` (L1725), `PIPE_TYPE_MESSAGE = 0x00000004` (L1731), `PIPE_READMODE_MESSAGE = 0x00000002` (L1735), `PIPE_WAIT = 0x00000000` (L1738), `PIPE_UNLIMITED_INSTANCES = 255` (L1744); in `WinNT`: `GENERIC_READ = 0x80000000`, `GENERIC_WRITE = 0x40000000`, `OPEN_EXISTING = 3`.
Sources: [Kernel32.java](https://raw.githubusercontent.com/java-native-access/jna/master/contrib/platform/src/com/sun/jna/platform/win32/Kernel32.java), [WinBase.java](https://raw.githubusercontent.com/java-native-access/jna/master/contrib/platform/src/com/sun/jna/platform/win32/WinBase.java), [WinNT.java](https://raw.githubusercontent.com/java-native-access/jna/master/contrib/platform/src/com/sun/jna/platform/win32/WinNT.java).

**JNA ships a working message-mode duplex reference implementation**: `contrib/platform/test/com/sun/jna/platform/win32/Kernel32NamedPipeTest.java`, whose server does `CreateNamedPipe(name, PIPE_ACCESS_DUPLEX, PIPE_TYPE_MESSAGE|PIPE_READMODE_MESSAGE|PIPE_WAIT, 1, 1024, 1024, 30000, null)` and whose client does `CreateFile(...)` then `SetNamedPipeHandleState(hPipe, new IntByReference(PIPE_READMODE_MESSAGE), null, null)`. Source: [Kernel32NamedPipeTest.java](https://raw.githubusercontent.com/java-native-access/jna/master/contrib/platform/test/com/sun/jna/platform/win32/Kernel32NamedPipeTest.java). Named-pipe support was added to JNA in 4.2.0 (`CHANGES.md` #431).

**Pipe naming and privileges.** Server form is `\\.\pipe\PipeName`; "Pipe names are not case sensitive"; total ≤ 256 chars ([Pipe Names](https://learn.microsoft.com/en-us/windows/win32/ipc/pipe-names)). The `Local\`/`Global\` prefixes apply to *file mappings and other kernel objects*, not to pipes. With `lpSecurityAttributes = NULL` the default descriptor "grant[s] full control to the LocalSystem account, administrators, and the creator owner. [It] also grant[s] read access to members of the Everyone group and the anonymous account" ⇒ **no elevation needed** ([CreateNamedPipe](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea)).

**Working around the limitations — the three real options:**
- **(a)** JNA + `CreateNamedPipe`/`CreateFile`/`SetNamedPipeHandleState` → true message mode, true framing, and you can add `FILE_FLAG_OVERLAPPED` if you want async I/O. Cost: `jna-platform.jar`.
- **(b)** `RandomAccessFile("\\\\.\\pipe\\…","rw")` + your own length-prefix framing on a byte stream. Zero dependencies. Accepts byte-mode coalescing. Dedicated blocking reader thread; to stop it you must `close()` the channel.
- **(c)** Don't use pipes at all — put the control channel on loopback TCP, which you already have.

### 2.2 Windows shared memory + SPSC ring buffer

**Creating a named *pagefile-backed* section (no file on disk):**
> "If `hFile` is `INVALID_HANDLE_VALUE`, the calling process must also specify a size … **`CreateFileMapping` creates a file mapping object of a specified size that is backed by the system paging file** instead of by a file in the file system." … "The name can have a `Global` or `Local` prefix … Creating a file mapping object in the global namespace from a session other than session zero requires the `SeCreateGlobalPrivilege` privilege."
> — [CreateFileMappingW](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-createfilemappingw)

⇒ Use **`Local\DrelayHookShm`** and **no administrator rights are required**. Initial page contents are zero.

**Coherency is guaranteed across processes:**
> "With one important exception, file views derived from any file mapping object that is backed by the same file are coherent or identical at a specific time. Coherency is guaranteed for views within a process **and for views that are mapped by different processes**. The exception is related to remote files."
> — [CreateFileMappingW](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-createfilemappingw)

**Mapping from Java, four ways:**

1. **JNA (recommended for the pagefile-backed case).** Exact signatures, all present in 5.19.1:
   ```java
   HANDLE  Kernel32.CreateFileMapping(HANDLE hFile, WinBase.SECURITY_ATTRIBUTES lpAttributes,
                                      int flProtect, int dwMaximumSizeHigh,
                                      int dwMaximumSizeLow, String lpName);
   HANDLE  Kernel32.OpenFileMapping(int dwDesiredAccess, boolean bInheritHandle, String lpName);
   Pointer Kernel32.MapViewOfFile(HANDLE hFileMappingObject, int dwDesiredAccess,
                                  int dwFileOffsetHigh, int dwFileOffsetLow,
                                  int dwNumberOfBytesToMap);
   boolean Kernel32.UnmapViewOfFile(Pointer lpBaseAddress);
   boolean Kernel32.CloseHandle(HANDLE hObject);
   ```
   Constants: `WinNT.PAGE_READWRITE = 0x04`; `WinBase.FILE_MAP_ALL_ACCESS = WinNT.SECTION_ALL_ACCESS`; `WinBase.FILE_MAP_READ = WinNT.SECTION_MAP_READ (0x0004)`; `WinBase.FILE_MAP_WRITE = WinNT.SECTION_MAP_WRITE (0x0002)`; and
   ```java
   HANDLE WinBase.INVALID_HANDLE_VALUE =
       new HANDLE(Pointer.createConstant(Native.POINTER_SIZE == 8 ? -1 : 0xFFFFFFFFL));
   ```
   So `CreateFileMapping(WinBase.INVALID_HANDLE_VALUE, null, WinNT.PAGE_READWRITE, 0, size, "Local\\DrelayHookShm")` is the pagefile-backed named section. `MapViewOfFile` returns a `com.sun.jna.Pointer`.
   Sources: [Kernel32.java L1192–1255](https://raw.githubusercontent.com/java-native-access/jna/master/contrib/platform/src/com/sun/jna/platform/win32/Kernel32.java), [WinBase.java L51, L221–225](https://raw.githubusercontent.com/java-native-access/jna/master/contrib/platform/src/com/sun/jna/platform/win32/WinBase.java), [WinNT.java L741, L786–790](https://raw.githubusercontent.com/java-native-access/jna/master/contrib/platform/src/com/sun/jna/platform/win32/WinNT.java).

2. **Pure Java, no dependency — `FileChannel.map` (Java 25).** Java 25 has:
   ```java
   public MemorySegment FileChannel.map(FileChannel.MapMode mode, long offset, long size, Arena arena)
   ```
   ([Java 25 FileChannel javadoc](https://docs.oracle.com/en/java/javase/25/docs/api/java.base/java/nio/channels/FileChannel.html)). The JDK's Windows implementation is:
   ```c
   mapping = CreateFileMapping(fileHandle, NULL, fileProtect, highLen, lowLen, NULL); /* No name for object */
   mapAddress = MapViewOfFile(mapping, mapAccess, highOffset, lowOffset, (SIZE_T)len);
   ```
   with `MAP_RW → PAGE_READWRITE / FILE_MAP_WRITE`, `MAP_RO → PAGE_READONLY / FILE_MAP_READ`.
   Source: [FileDispatcherImpl.c, `Java_sun_nio_ch_FileDispatcherImpl_map0`](https://raw.githubusercontent.com/openjdk/jdk/master/src/java.base/windows/native/libnio/ch/FileDispatcherImpl.c).
   ⇒ **Java cannot create a *named* section via `FileChannel.map`** (the name argument is a hard-coded `NULL`), and it **cannot back a mapping with the pagefile**. What it *can* do is open the **same file path** in both processes and map it — which Microsoft explicitly blesses: "Multiple processes can share a view of the same file by either using a single shared file mapping object **or creating separate file mapping objects backed by the same file**."
   ⇒ This is the **zero-dependency transport**. Cost: it is file-backed, so dirty pages can be written back to disk by the modified-page writer. At 200 msg/s × 150 B = 30 KB/s this is negligible, but it is not free, and the mapping is not anonymous.
   Also note: wrapping the returned `MemorySegment` in `asByteBuffer()` gives you a direct `ByteBuffer` for `VarHandle`s; `MemorySegment.map` itself does **not** call a restricted method (verified in [FileChannelImpl.java L1442–1463](https://raw.githubusercontent.com/openjdk/jdk/master/src/java.base/share/classes/sun/nio/ch/FileChannelImpl.java) — no `reinterpret`), so **no `--enable-native-access` is needed** for this path.

3. **JNA `MemoryMappedFile`** — exists in `com.sun.jna.platform`, but it wraps a file, so it adds nothing over (2) except dependency weight.

4. **`sun.misc.Unsafe` / `ByteBuffer` tricks** — `Unsafe` memory-access methods are being removed ([JEP 471](https://openjdk.org/jeps/471), referenced from JEP 472). Don't.

**Memory-ordering discipline for a cross-process SPSC ring on x86-64.** The authoritative mapping between C++ and Java is documented by the JDK itself (Java 25 `VarHandle` javadoc):

| Java 25 | Documented C++ equivalent |
|---|---|
| `VarHandle.getAcquire(...)` | "memory ordering effects compatible with `memory_order_acquire`" |
| `VarHandle.setRelease(...)` | "compatible with `memory_order_release` ordering" |
| `VarHandle.setVolatile(...)` | "compatible with `memory_order_seq_cst`" |
| `VarHandle.acquireFence()` / `releaseFence()` / `loadLoadFence()` / `storeStoreFence()` / `fullFence()` | static fences for finer control |

Source: [Java 25 VarHandle javadoc](https://docs.oracle.com/en/java/javase/25/docs/api/java.base/java/lang/invoke/VarHandle.html) (the acquire/release/critical-fence statements are verbatim from the javadoc). C++ side: the equivalent is `std::atomic<T>` with `memory_order_acquire` / `memory_order_release`.

**The JIT hazard that actually bites.** On x86-64 (TSO) acquire/release fences compile to nothing, so it is tempting to use a plain field. Do not: a **plain read of a shared index inside a spin loop can be hoisted out of the loop by the JIT**, producing an infinite loop. Always read the synchronisation variable with an acquire/volatile access mode, or issue an explicit `VarHandle.acquireFence()` inside the loop.

**Simplest correct design: sequence-stamped slots, not head/tail pointers.**

```
Header (its own 64-byte cache line):  magic:u32  version:u32  capacity:u32  slotSize:u32
                                      epoch:u32  flags:u32   hookPid:u32  _pad:u32
                                      heartbeatQpc:u64        producerDropped:u64
Slot[i] (slotSize bytes, 64-byte aligned):
    seq:u64           // 0 = never written; n means "message n is complete here"
    len:u32  type:u16  flags:u16
    payload[len]
```

- **Producer (C++), holding a private counter `n`:** write payload into slot `n % capacity`; then `slot.seq.store(n, std::memory_order_release)`.
- **Consumer (Java), holding `lastSeq`:** read `slot[(lastSeq+1) % capacity].seq` with `getAcquire`; if `== lastSeq+1`, copy the payload and `lastSeq++`. If it is `> lastSeq+1` you overran — increment a `dropped` counter and jump `lastSeq` forward. **The producer never blocks.**
- **No head/tail variables, no ABA, no CAS, no false sharing between two indices.** This is strictly simpler than a classic head/tail ring and trivially detects overrun. (A classic head/tail SPSC ring with cache-line padding is described in [Rico's Tech Blog, *Windows IPC & Memory Management Architecture*](https://ricosheaven.com/en/posts/win32-ipc-memory-management-guide/) — **informal, but the pattern shown is the standard one**; the blog also correctly notes that `INVALID_HANDLE_VALUE` mappings live in RAM, not on disk, and that kernel objects are reference-counted.)
- Pad the header fields onto separate cache lines (64 B on x86-64) to avoid false sharing — standard practice, same source.

**Failure semantics of a mapping (Microsoft, [CreateFileMappingW](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-createfilemappingw)):** "Mapped views of a file mapping object maintain internal references to the object, and a file mapping object does not close until all references to it are released." ⇒ **If the hook dies, Java's view stays valid and readable but frozen.** You cannot detect death from the mapping alone — you need a heartbeat. And because `CreateFileMapping` on an existing name "returns a handle to the existing object (with its current size, not the specified size)" (`ERROR_ALREADY_EXISTS`), a hook restart after Java attached would inherit stale sequence numbers ⇒ **bump `epoch` on every hook start and have Java reset `lastSeq` when `epoch` changes.**

### 2.3 Loopback TCP / UDP

**Measured, Windows-native** (`anton-dutov/bench-win-ipc`, C++, 100,000 iterations, 4–4096 B, network adapters disabled):

| Bytes | Named pipe | TCP |
|---|---|---|
| 4 | 300 B/ms (≈13.3 µs/iteration) | 386 B/ms (≈10.4 µs/iteration) |
| 64 | 4.66 KiB/ms | 10.63 KiB/ms |
| 128 | 9.38 KiB/ms | 13.90 KiB/ms |
| 1024 | 72.20 KiB/ms | 99.21 KiB/ms |
| 4096 | 293.04 KiB/ms | 361.99 KiB/ms |

Author's stated conclusion: **"TCP is better option."** Source: [bench-win-ipc README](https://raw.githubusercontent.com/anton-dutov/bench-win-ipc/main/README.md).
⚠️ **Read the number with the right caveat:** the README does not state whether an iteration is a one-way send or a send+receive exchange. The Sent-rate ≈ Recv-rate pairing suggests a request/response exchange, in which case ~10 µs (TCP) / ~13 µs (pipe) is a **round trip**. If it is one-way, halve nothing — it's 10 µs one-way. Either way: **Windows loopback TCP is on the order of 10 µs, i.e. ~1000× inside a 10 ms budget.** Cite it as "≈10 µs per small-message exchange (methodology ambiguity noted)".

**Informal, Linux, Python** (`suenot/trading-ipc-bench`, GitHub Actions `ubuntu-latest`, 64 B, p50 round-trip): `uds 20.2 µs`, `pipe 26.3 µs`, `tcp 41.7 µs`, `shm 77.6 µs`, `zmq_ipc 118.4 µs`. Source: [trading-ipc-bench README](https://raw.githubusercontent.com/suenot/trading-ipc-bench/main/README.md). ⚠️ The `shm` figure being *worst* is an artefact of that harness (`mmap` + POSIX semaphore signalling, with Python overhead dominating). **Do not conclude shared memory is slow from this.** It is useful only as a sanity bound on what interpreter-level IPC costs.

**Windows loopback fast path.** `SIO_LOOPBACK_FAST_PATH` configures "a TCP socket for lower latency and faster operations on the loopback interface", moves packets through the Transport Layer instead of the Network Layer, and "**is deprecated and is not recommended to be used in your code**"; it is "disabled by default", "must be used on both sides of the loopback session", and is **silently disabled whenever a Windows Filtering Platform callout is present** ([SIO_LOOPBACK_FAST_PATH](https://learn.microsoft.com/en-us/windows/win32/winsock/sio-loopback-fast-path)). The JDK wires it to the system property `jdk.net.useFastTcpLoopback` (default `false`) — see `sun.nio.ch.Net.isFastTcpLoopbackRequested()` reading `System.getProperty("jdk.net.useFastTcpLoopback", "false")` ([Net.java L461](https://raw.githubusercontent.com/openjdk/jdk/master/src/java.base/share/classes/sun/nio/ch/Net.java), and the native `NET_EnableFastTcpLoopback` in [net_util_md.c L397–414](https://raw.githubusercontent.com/openjdk/jdk/master/src/java.base/windows/native/libnet/net_util_md.c)), guarded by a Windows-10-RS3 (build 16299) version check. The original JDK 8u60 release note says the benefit accrues to "applications running on **server editions** of Microsoft Windows" ([release note](https://builds.shipilev.net/backports-monitor/release-notes-8u60.txt)). **Recommendation: do not depend on it.**

**Java-side latency hygiene if you do use TCP:** `socket.setTcpNoDelay(true)` (Nagle), direct `ByteBuffer`s, a dedicated blocking thread rather than a `Selector` (a selector adds a wakeup round trip), and a fixed-length framing read (`readFully`) to avoid per-message allocation.

### 2.4 `WM_COPYDATA`, `PostMessage`, registered messages, IOCTL

- `WM_COPYDATA` is a `SendMessage`-carried copy. Microsoft: "The data being passed must not contain pointers or other references to objects not accessible to the application receiving the data"; "The receiving application should consider the data read-only. The `lParam` parameter is valid only during the processing of the message… If the receiving application must access the data after `SendMessage` returns, it must copy the data into a local buffer." ([WM_COPYDATA](https://learn.microsoft.com/en-us/windows/win32/dataxchg/wm-copydata))
- Why it is the wrong tool here: it is **synchronous** — the sender blocks until the receiver's window procedure returns; the JVM would need a window and a running message pump; the payload lifetime ends when the handler returns; and it is a per-message kernel copy. It also cannot carry the 144 Hz stream without either blocking the game's main thread or bouncing through a second thread. **Verdict: unsuitable.** (JNA does map `COPYDATASTRUCT` and `User32.SendMessage` — added in JNA 2.x, `CHANGES.md` #774 — so it is *possible*, just wrong.)
- **IOCTL via a driver**: overkill. A kernel driver means a signed `.sys`, an install step, and elevated rights. Stated as engineering judgement, not a cited fact.

### 2.5 Transport comparison

| Transport | Latency (measured/derived) | Complexity | Peer dies | Peer paused | Peer restarts | Admin? | Debuggability |
|---|---|---|---|---|---|---|---|
| **SHM + seq-stamped SPSC ring (recommended)** | **~0.1–1 µs** (two release/acquire stores, no syscall) | Medium — you own the layout & ordering | Java's view stays valid but frozen → detect via heartbeat | Detected by heartbeat staleness | Needs `epoch` bump; pagefile section may be recreated | **No** (`Local\` prefix) | Hardest: inspect with a hex viewer; you must build a dumper |
| **Named pipe (message mode, JNA)** | ~13 µs/exchange (Win, measured) + ~1.35 µs JNA/call | Medium — needs `jna-platform` | `ReadFile` → `ERROR_BROKEN_PIPE` (109) → Java sees EOF | Blocks (synchronous handle) | Recreate instance: `DisconnectNamedPipe` + `ConnectNamedPipe` | **No** | Good — pipe names visible, can attach a debugger |
| **Named pipe (byte mode, `RandomAccessFile`)** | same | **Lowest** — zero deps | same (JDK maps 109→EOF, 232→`IOS_UNAVAILABLE`) | Blocks; only `close()` unblocks | same | **No** | Good |
| **Loopback TCP** | ~10 µs/exchange (Win, measured) | **Lowest** — you already have socket code | RST → `IOException` | `SO_TIMEOUT` | Reconnect loop | **No** | **Best** — Wireshark, netstat, telnet |
| **Loopback UDP** | similar to TCP, minus connection setup | Low | Silent packet loss (no RST) — **you must add your own reliability** | Silent loss | Trivial (stateless) | **No** | Good |
| **`WM_COPYDATA`** | kernel copy + synchronous message dispatch | Medium | Receiver-side only | Sender blocks | n/a | **No** | Poor |
| **Driver + IOCTL** | Fastest possible | Very high | n/a | n/a | n/a | **Yes** | Very poor |

Robustness notes with citations: `ERROR_BROKEN_PIPE = 109` and `ERROR_NO_DATA = 232` are the codes the JDK maps to EOF/`UNAVAILABLE` on read ([FileDispatcherImpl.c](https://raw.githubusercontent.com/openjdk/jdk/master/src/java.base/windows/native/libnio/ch/FileDispatcherImpl.c)); `ERROR_PIPE_LISTENING` is what a nonblocking `ConnectNamedPipe` returns when no client is waiting ([Named Pipe Type, Read, and Wait Modes](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-type-read-and-wait-modes)); "an instance of a named pipe is always deleted when the last handle to the instance of the named pipe is closed" ([CreateNamedPipe](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea)); a file mapping "does not close until all references to it are released" ([CreateFileMappingW](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-createfilemappingw)).

---

## 3. Shared memory + JNA concrete API surface

### 3.1 Java-side attach (once, at startup)

```java
public interface Kernel32Shm extends StdCallLibrary {
    Kernel32Shm INSTANCE = Native.load("kernel32", Kernel32Shm.class, W32APIOptions.DEFAULT_OPTIONS);

    HANDLE  CreateFileMapping(HANDLE hFile, SECURITY_ATTRIBUTES sa, int flProtect,
                              int maxSizeHigh, int maxSizeLow, String lpName);
    HANDLE  OpenFileMapping(int dwDesiredAccess, boolean bInheritHandle, String lpName);
    Pointer MapViewOfFile(HANDLE hMap, int dwDesiredAccess, int offsetHigh, int offsetLow, int nBytes);
    boolean UnmapViewOfFile(Pointer base);
    boolean CloseHandle(HANDLE h);
}
```
(If you take `jna-platform`, drop this and use `com.sun.jna.platform.win32.Kernel32.INSTANCE` directly — the signatures are identical, verified in §2.2.)

Two ways to obtain the `Pointer`:
- **Hook creates it** (recommended; the hook is in the process that must not fail): hook calls `CreateFileMapping(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, size, L"Local\\DrelayHookShm")`. Java calls `OpenFileMapping(FILE_MAP_ALL_ACCESS, false, "Local\\DrelayHookShm")` → `MapViewOfFile(hMap, FILE_MAP_ALL_ACCESS, 0, 0, size)`.
- **Java creates it**: same calls with `CreateFileMapping(WinBase.INVALID_HANDLE_VALUE, null, WinNT.PAGE_READWRITE, 0, size, "Local\\DrelayHookShm")`.

Byte-order note: **JNA 5.19.1 has no global byte-order API.** I grepped the released `jna-5.19.1-sources.jar`: `ByteOrder` appears only in `ELFAnalyser.java` and once in `Pointer.java`. The one occurrence is:
```java
public ByteBuffer getByteBuffer(long offset, long length) {
    return Native.getDirectByteBuffer(this, this.peer, offset, length).order(ByteOrder.nativeOrder());
}
```
([Pointer.java L653–654](https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Pointer.java)). ⇒ `Pointer.getInt/getLong/getFloat/getDouble` are **native byte order**, which on Windows x64 **is little-endian** — matching MSVC `int32_t`/`int64_t`/`float`/`double` laid out with default packing. For explicit control use `Pointer.getByteBuffer(...).order(ByteOrder.LITTLE_ENDIAN)`, or `Integer.reverseBytes`/`Long.reverseBytes` if you ever need big-endian.

### 3.2 Reading and writing at offsets — the two options

**(a) `Pointer` accessors.** All are implemented as `static native` JNI calls in `Native` (verified: `Native.getInt(Pointer,long,long)` at L2405 and `Native.setInt(Pointer,long,long,int)` at L2448 of [Native.java](https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Native.java)). API: `getByte/getChar/getShort/getInt/getLong/getFloat/getDouble`, `setByte/…/setDouble`, `getPointer/setPointer`, `read(long,byte[],int,int)`, `write(long,byte[],int,int)`, `getByteArray/getIntArray/getLongArray/getFloatArray/getDoubleArray`, `getString/setString`, `share(long)`, `clear(long)`, `setMemory(long,long,byte)`.

**Cost per access:** one JNI downcall ≈ **tens of nanoseconds** (measured JNI downcall = 56.6 ns on Windows/x64, [java-native-benchmark](https://raw.githubusercontent.com/archenroot/java-native-benchmark-archenroot/master/README.md)). **This is an inference from the JNI downcall figure, not a direct measurement of `Pointer.getInt`** — I could not fetch the `JNA Native.setXXX() slow` StackOverflow thread (SO returns 403 to my fetcher) and found no other direct microbenchmark of `Pointer.getInt`.

**(b) ByteBuffer views — better for anything more than a handful of fields.**
```java
ByteBuffer bb = mappedPointer.getByteBuffer(0, SIZE).order(ByteOrder.nativeOrder());
IntBuffer   ib = bb.asIntBuffer();
FloatBuffer fb = bb.asFloatBuffer();
// then plain ib.get(idx) / fb.put(idx, v) — no JNI call per element
```
`Pointer.getByteBuffer(long,long)` exists in 5.19.1 ([Pointer.java L653](https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Pointer.java)); `Memory.getByteBuffer(long,long)` also exists and additionally pins the `Memory` against GC while the buffer lives ([Memory.java L589](https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Memory.java)). **You cannot use `Memory` for a mapped view** — `Memory` is a `Pointer` backed by `malloc`, not by `MapViewOfFile`. Keep the raw `Pointer` and derive your `ByteBuffer`s from it, holding the `Pointer` in a `static final` field so it is never collected.

**Verdict on §3:** *"Are JNA's `Pointer.getInt`/`setInt` fast enough for a per-frame polling loop?"* — **Yes, comfortably.** At 144 fps with 8 fields you make ~1150 JNI calls/s ≈ 65 µs/s ≈ **0.0065 % CPU**. *"Are `Memory`/`IntBuffer` views better?"* — **Yes for bulk or repeated access**: build the `ByteBuffer` once at attach and never call a `Pointer.getXxx` in the loop. `Memory` is *not* usable for a mapped region.

---

## 4. Build and deployment shape

### 4.1 Maven

```xml
<dependency>
  <groupId>net.java.dev.jna</groupId>
  <artifactId>jna</artifactId>
  <version>5.19.1</version>
</dependency>
<!-- add jna-platform only if you want the prebuilt Kernel32 named-pipe mappings -->
```
Do **not** add `jna-jpms`/`jna-platform-jpms` unless you are modularising `drelay` (they publish `module-info` alongside the main jar; the FAQ notes you must also `opens` packages containing `Structure`/`PointerType` subclasses to `com.sun.jna` under JPMS).

**Vendoring option (keeps `javac` builds honest):** commit `lib/jna-5.19.1.jar` (1,958,853 B) into the repo and put it on the compile classpath. For Maven, use the normal dependency. Both work; the file is dual-licensed Apache-2.0 so redistribution is fine ([pom](https://repo1.maven.org/maven2/net/java/dev/jna/jna/5.19.1/jna-5.19.1.pom)).

### 4.2 Single-jar property

Keep `java -jar drelay.jar` by shading with **maven-shade-plugin** (current 3.6.2, [docs](https://maven.apache.org/plugins/maven-shade-plugin/examples/resource-transformers.html)):

```xml
<plugin>
  <artifactId>maven-shade-plugin</artifactId>
  <version>3.6.2</version>
  <configuration>
    <transformers>
      <transformer implementation="org.apache.maven.plugins.shade.resource.ManifestResourceTransformer">
        <mainClass>…</mainClass>
        <manifestEntries>
          <Enable-Native-Access>ALL-UNNAMED</Enable-Native-Access>
        </manifestEntries>
      </transformer>
    </transformers>
  </configuration>
</plugin>
```
- `Enable-Native-Access: ALL-UNNAMED` is what suppresses the JEP 472 / JEP 454 restricted-method warnings for JNA's `System.load` and for JNA's bound `native` methods. JEP 472: *"You can add `Enable-Native-Access: ALL-UNNAMED` to the manifest of an executable JAR file … (The only supported value for the `Enable-Native-Access` manifest entry is `ALL-UNNAMED`; other values cause an exception to be thrown.)"* ([JEP 472](https://openjdk.org/jeps/472)).
- **`ServicesResourceTransformer` is not needed for JNA** — JNA 5.x does not ship `META-INF/services` entries that must be merged (it uses `Native.load`, not `ServiceLoader`). Only add it if another dependency needs it. Citation for what it does: [shade plugin docs](https://maven.apache.org/plugins/maven-shade-plugin/examples/resource-transformers.html).

### 4.3 ⚠️ Do not relocate JNA

JNA's own native-stub extraction path is a **hard-coded string literal**:
```java
String libName = "/com/sun/jna/" + Platform.RESOURCE_PREFIX + "/" + mappedName;
File lib = extractFromResourcePath(libName, Native.class.getClassLoader());
```
Source: `Native.loadNativeDispatchLibraryFromClasspath()`, [Native.java L1102](https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Native.java) (verified in 5.19.1).

If you relocate `com.sun.jna` → `com.example.shaded.jna`, the class *and* the `jnidispatch.dll` resource path are supposed to move together. The shade plugin does rewrite string constants that match relocated package patterns in many cases, but **I could not verify that it rewrites this particular `"/com/sun/jna/"`-prefixed literal**, and I found no JNA-maintainer statement confirming relocation is supported. **Recommendation: do not relocate JNA.** `drelay` is the only consumer, so there is no package conflict to resolve — merging is sufficient.

### 4.4 Shipping and loading the native DLL

`Platform.RESOURCE_PREFIX` on Windows x64 is **`win32-x86-64`** — `getNativeLibraryResourcePrefix()` returns `"win32-" + arch` for `Platform.WINDOWS`, and `getCanonicalArchitecture()` maps `"x86_64"`/`"amd64"` → `"x86-64"`. Source: [Platform.java L253–265, L303–330](https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Platform.java) (verified in 5.19.1).

**Option A (recommended): let JNA extract your DLL from the jar.**
Put the DLL in the jar at `win32-x86-64/drelay_hook.dll`, then:
```java
DrelayHook hook = Native.load("drelay_hook", DrelayHook.class);
```
`extractFromResourcePath(name, loader)` computes `libname = NativeLibrary.mapSharedLibraryName(name)` (`drelay_hook` → `drelay_hook.dll`) and `resourcePath = Platform.RESOURCE_PREFIX + "/" + libname` → `win32-x86-64/drelay_hook.dll`, falling back to the bare `libname`, and throws `IOException("Native library (...) not found in resource path (...)")` if absent. Sources: [Native.java L1144–1177](https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Native.java), [GettingStarted.md](https://raw.githubusercontent.com/java-native-access/jna/master/www/GettingStarted.md) ("Make your native library available on your classpath, under the path `{OS}-{ARCH}/{LIBRARY}` … If the resource is within a jar file it will be automatically extracted when loaded").

**Option B: extract yourself, then `System.load`.**
```java
File dll = Native.extractFromResourcePath("win32-x86-64/drelay_hook.dll"); // absolute resource path form
System.load(dll.getAbsolutePath());
```
The absolute-path form (leading `/` in the resource, or a full resource path) skips name mapping and `RESOURCE_PREFIX` prepending ([Native.java L1172–1173](https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Native.java)).

**Option C: ship the DLL next to the jar.** Set `-Djna.library.path=C:\path\to\native` for *your* DLL, and `-Djna.boot.library.path=…` only if you are shipping JNA's own `jnidispatch.dll` out-of-band. `jna.library.path` "is similar to `java.library.path`, but only applies to libraries loaded by JNA" ([GettingStarted.md](https://raw.githubusercontent.com/java-native-access/jna/master/www/GettingStarted.md)). **This is the most robust option in the field** — it removes the temp-file extraction from the critical path, which matters because Application Control / Smart App Control / AV may block loading a DLL out of `%TEMP%`.

**System properties (all verified in `jna-5.19.1-sources.jar`):**

| Property | Effect |
|---|---|
| `jna.library.path` | Search path for *your* libraries |
| `jna.boot.library.path` | Search path for `jnidispatch` itself, tried first, via `System.load` ([L1020–1037](https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Native.java)) |
| `jna.nosys` | Default **`"true"`**: skip `System.loadLibrary` for `jnidispatch` ([L1070](https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Native.java)) |
| `jna.noclasspath` | Skip classpath extraction of `jnidispatch` entirely ([L1081](https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Native.java)) |
| `jna.nounpack` | Never unpack; fail if the library is not already installed ([javadoc L91–93](https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Native.java)) |
| `jna.tmpdir` | Override the extraction temp dir; otherwise `java.io.tmpdir` on Windows ([getTempDir(), L1385–1396](https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Native.java)) |
| `jnidispatch.preserve` | Don't `deleteOnExit()` the extracted file ([L1240](https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Native.java)) |
| `jna.debug_load`, `jna.debug_load.jna` | Print the library search steps ([FAQ](https://raw.githubusercontent.com/java-native-access/jna/master/www/FrequentlyAskedQuestions.md)) |
| `jna.prefix` | Override the detected `RESOURCE_PREFIX` ([Platform.java L304](https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Platform.java)) |

### 4.5 Classloading pitfalls (all cited above)

1. **`System.loadLibrary` cannot find a library that is only inside a jar.** `System.load`/`System.loadLibrary` need a filesystem path or a `java.library.path` entry. Hence extraction, or shipping the DLL beside the jar.
2. **Windows will not delete a loaded DLL**, so the `deleteOnExit()` file survives the process and is swept on the *next* JVM start (`removeTemporaryFiles()` at [L1013](https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Native.java)). Expect one stale `jna*.dll` in `%TEMP%` after each run; set `jna.tmpdir` to a `drelay`-owned directory to keep it tidy.
3. **Classloader selection matters.** `extractFromResourcePath(name)` uses the *thread context* classloader, falling back to `Native.class`'s loader ([L1164–1170](https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Native.java)). If you load the DLL from a plugin/custom classloader, pass it explicitly: `Native.extractFromResourcePath(name, loader)`.
4. **Shutdown-hook crash.** The FAQ: with direct mapping you "must keep a reference to the JNA class `com.sun.jna.Native` until your shutdown hook completes", otherwise the VM can crash in the shutdown hook on Windows.
5. **Relocation risk** — see §4.3.

---

## 5. What already exists

| Project | Binding | Transport | Relevance / what it proves |
|---|---|---|---|
| **`java-native-access/jna`** — `com.sun.jna.platform.win32.Kernel32` named-pipe API + [`Kernel32NamedPipeTest.java`](https://raw.githubusercontent.com/java-native-access/jna/master/contrib/platform/test/com/sun/jna/platform/win32/Kernel32NamedPipeTest.java) | JNA | **Named pipe, message mode** | A **maintainer-blessed, CI-exercised** duplex message-mode named pipe from Java: server `CreateNamedPipe(PIPE_TYPE_MESSAGE\|PIPE_READMODE_MESSAGE\|PIPE_WAIT)`; client `CreateFile` + `SetNamedPipeHandleState(PIPE_READMODE_MESSAGE)`. Named-pipe support added in JNA 4.2.0 ([CHANGES.md #431](https://raw.githubusercontent.com/java-native-access/jna/master/CHANGES.md)). **This is the single most reusable artefact for the control channel.** |
| **`ShirasawaSama/JavaSharedMemory`** (MIT) | **FFM (JEP 434 preview)** | **Windows `CreateFileMapping`**; Linux/macOS `mmap`/`shmget` | Shared memory between Java and C++/Rust/Go. Closest existing project to the recommended design. README claims ">2x performance improvement" vs Windows anonymous pipes. Uses the *preview* FFM (`--enable-preview`), so it is a design reference, not a dependency. [repo](https://github.com/ShirasawaSama/JavaSharedMemory) |
| **`Minzoid/Memory`** (MIT, Java 21+) | **JNA** | none (reads the target process directly: `OpenProcess`/`ReadProcessMemory`/`VirtualProtectEx`) | The closest existing analogue of "JNA + a Windows game process": AoB scanning on `ForkJoinPool`, pointer-chain resolution, `VirtualProtectEx`. Notable deployment detail: it packages **`Enable-Native-Access: ALL-UNNAMED` in the JAR manifest for Java 22+** and stderr-filters JNA warnings on 17–21 — the same trick recommended in §4.2. [repo](https://github.com/Minzoid/Memory) |
| **`UnderMybrella/ColonelAccess`** | JNA | none | Multiplatform JNA process-memory reader. Same family. [repo](https://github.com/UnderMybrella/ColonelAccess) |
| **`real-logic/aeron`** + **`real-logic/agrona`** | pure Java (no binding) | **memory-mapped file + ring buffers** (IPC transport) | The canonical JVM shared-memory messaging stack; Aeron's README advertises "UDP unicast, UDP multicast, and IPC message transport" and "Performance is the key focus… the highest throughput with the lowest and most predictable latency". ⚠️ **I could not verify Aeron's Windows IPC implementation detail or fetch published IPC latency figures** — the docs URLs I tried returned 404. Treat the *architecture* (mmap + ring + CnC file) as the pattern; do not quote numbers. [repo](https://github.com/real-logic/aeron) |
| **`OpenHFT/Chronicle-Queue` / `Chronicle-Bytes`** | pure Java | memory-mapped file | The other major JVM shared-memory log. ⚠️ I could **not** fetch its README (the raw URL returned a version-pinned path I could not resolve), so **no latency figures are quoted here.** |
| **Smith & Wells, SACJ 29(3), 2017** | **JNI + native C** | Windows shared memory + semaphore, named pipes, sockets | Peer-reviewed. Confirms Java's IPC gap and that native Windows IPC beats Java sockets. [abstract](https://sacj.cs.uct.ac.za/index.php/sacj/article/view/500) |
| **`anton-dutov/bench-win-ipc`** | native C++ | named pipe vs TCP | The Windows measurement in §2.3. [repo](https://raw.githubusercontent.com/anton-dutov/bench-win-ipc/main/README.md) |
| **`suenot/trading-ipc-bench`** | Python | TCP/UDS/pipe/shm/zmq/nng/websocket/gRPC/Redis/NATS | Cross-transport RTT table (§2.3). Linux, informal. [repo](https://github.com/suenot/trading-ipc-bench) |
| **JNA `COPYDATASTRUCT` + `User32.SendMessage`** | JNA | `WM_COPYDATA` | Exists ([CHANGES.md #774](https://raw.githubusercontent.com/java-native-access/jna/master/CHANGES.md)) but unsuitable (§2.4). |

**Synthesis of what real projects choose.** For JVM↔native high-frequency state, the dominant pattern is **shared memory with a ring buffer and no per-message syscall** (Aeron IPC, Chronicle, JavaSharedMemory). For JVM↔native *control/command* traffic, **named pipes** dominate, and when a Java project needs message-mode pipes it uses **JNA**, never `java.nio`. For "read a Windows game process from Java", **JNA** is the near-universal choice (Minzoid/Memory, ColonelAccess, OSHI, Waffle). Nobody in the surveyed set uses JNI for this class of problem on Java 22+, and the one peer-reviewed Java-on-Windows IPC paper predates FFM.

---

## 6. Recommended architecture

### 6.1 The design

```
┌─────────── game process (x64 .NET 9) ───────────┐        ┌───────── JVM: drelay (Java 25) ─────────┐
│ drelay_hook.dll                                 │        │                                         │
│  ┌─ frame callback (game thread) ────────────┐  │        │  HookShmReader (platform thread, 2 kHz) │
│  │ read HP / projectiles / position          │  │        │    spin on slot seq (getAcquire)       │
│  │ run dodge + escape math  ◄── IN C++       │  │        │    decode ~150 B frame                 │
│  │ write frame → SHM ring (release store)    │  │        │    → existing JSONL log + dashboard    │
│  │ drain cmd ring (acquire load)             │  │        │                                         │
│  └───────────────────────────────────────────┘  │        │  HookCmdWriter (same or 2nd thread)     │
│                                                  │        │    write decision → cmd ring (release) │
│  SHM: Local\DrelayHookShm  ◄─────── mmap ───────┼────────┼─── pure ByteBuffer/VarHandle reads      │
└──────────────────────────────────────────────────┘        │  existing auto-nexus packet injector    │
                                                             └─────────────────────────────────────────┘
        control/lifecycle: loopback TCP 127.0.0.1:<port>  (attach, detach, version, config, kill)
```

- **Transport — data path:** pagefile-backed named file mapping `Local\DrelayHookShm` created by the **hook**, opened by Java with JNA `OpenFileMapping` + `MapViewOfFile` **once at attach**. Zero syscalls and zero JNA calls per message.
- **Transport — control path:** loopback TCP, reusing the socket code you already have, for attach/detach/version handshake, config push, and "hook is going away" notification. ~10 µs, trivially debuggable, and it gives you a natural place for the handshake that the mapping cannot provide.
- **Binding:** **`jna.jar` only** (1.86 MiB) — you need exactly three `kernel32` functions, so you can declare your own 4-method `Library` interface and skip `jna-platform` (1.35 MiB) entirely. Use **direct mapping** (`Native.register`) if you want, but with 3–10 calls per session the interface mapping is fine and simpler.
- **Zero-dependency fallback:** if you must not add any jar, replace `OpenFileMapping`/`MapViewOfFile` with `FileChannel.open(Path.of(shmFile), READ, WRITE).map(MapMode.READ_WRITE, 0, SIZE, Arena.ofShared())` and have the hook map the same *file* (`CreateFile` + `CreateFileMapping` + `MapViewOfFile`). Same ring, same code below the attach layer; ~15 lines differ. Cost: file-backed rather than pagefile-backed, and one extra file on disk.

**Why not named pipes as the data path?** They are fast enough (~13 µs, measured) but they are a *syscall per message* plus ~1.35 µs of JNA per call, they cannot express message mode from pure Java, and they give you no zero-copy. Shared memory gives ~0.1–1 µs, is enforced by the same acquire/release discipline you would write anyway, and removes the JNA dependency from the hot path. Use pipes/TCP where blocking semantics are *desirable* — i.e. the control channel.

### 6.2 Message framing

Fixed-size slots so the consumer never parses to advance (validation on read, not to find the end):

```
// ── Header: offset 0, exactly one 64-byte cache line ──
u32 magic        = 0x44484C52 ("RHLD" LE)   // reject garbage / wrong region
u32 version      = 1                        // bump on layout change; Java refuses mismatches
u32 capacity     = 8192                     // slots per ring
u32 slotSize     = 256                      // bytes per slot (150 B payload fits with headroom)
u32 epoch        = <bumped by hook on every start>
u32 flags                            // bit0 = "hook wants detach"
u32 hookPid
u32 _pad
u64 heartbeatQpc                     // QPC ticks, updated every frame by the hook
u64 producerDropped                  // incremented by the HOOK when it laps the consumer

// ── Frame ring: offset 64 ──      producer = hook,   consumer = Java
// ── Cmd   ring: offset 64 + capacity*slotSize ──  producer = Java,   consumer = hook

// ── Slot layout (each slot 64-byte aligned) ──
u64 seq                              // 0 = never written; n = "message n complete"
u32 len                              // payload bytes, <= slotSize - 16
u16 type                             // 1=frame, 2=event, 3=decision, 4=ack, 5=bye
u16 flags
u8  payload[len]                     // see below
// zero-padded to slotSize
```

`seq` is written **last** with `memory_order_release` / `setRelease` semantics; everything before it is plain. This is what makes the payload visible without a fence on x86.

**Payload — `type=1` (frame, hook→Java), fixed binary, 64 B, no allocation:**
`u64 qpcNs | f32 hp | f32 hpMax | f32 x | f32 y | f32 z | f32 vx | f32 vy | u32 flags | u16 projectileCount | u16 reserved | f32[8] nearestProjectile{dist,bearing,ttl,radius}`
(~64 B of the 150 B budget; the remaining ~90 B is headroom for a variable tail.)

**Payload — `type=2`/`type=4` (event/decision), variable but small:** `u32 eventId | u64 qpcNs | u8 action | f32 tx | f32 ty | u16 reason`. Keep it binary; do **not** put JSON on the wire. Serialise to the existing JSONL format on the Java side where the allocation cost is off the game's critical path.

### 6.3 Threading model

**C++ / hook:**
- Write **directly from the per-frame callback on the game thread**. It is a ~200 ns memcpy plus one release store — no syscalls, no locks, no allocation, no `new`, no logging.
- **Never block the game thread.** If a slot is unavailable, overwrite it (the consumer will see the gap via `seq` and count a drop). The producer must not spin waiting for the consumer.
- One exporter per process: `extern "C" __declspec(dllexport) int drelay_hook_start(int configShmEpoch)` and `drelay_hook_stop(void)` for the attach handshake over TCP.
- If building the payload ever becomes expensive, add a dedicated hook thread with a *second* in-process SPSC queue (same seq-slot design, in the hook's own heap) and have the frame callback do only the handoff. Start without it.

**Java:**
- `HookShmReader`: **one dedicated platform thread** (`Thread.ofPlatform().daemon().name("drelay-hook-shm").priority(Thread.MAX_PRIORITY).start(...)`). A virtual thread is the wrong choice here (a CPU-bound spin loop pins its carrier). The loop: `getAcquire` the next slot's `seq` → if ready, copy payload out of the `ByteBuffer` → hand to the existing event bus → repeat. Insert a cheap backoff (`Thread.onSpinWait()` for the first N iterations, then `LockSupport.parkNanos(50_000)` for the idle case) so an idle hook costs ~0 % CPU.
- Decode via **pre-built `ByteBuffer` views** (`IntBuffer`, `FloatBuffer`, or a `VarHandle` over `MemorySegment`/`ByteBuffer`), created once. No `Pointer.getXxx` in the loop.
- `HookCmdWriter`: a small `ArrayBlockingQueue<Decision>` drained by the same reader thread (or a second one) once per poll. Cheap; you already have a JSONL appender thread to model it on.
- Reuse the **existing** auto-nexus escape sender for injection. Do not create a second socket writer.

### 6.4 Failure modes and detection

| Failure | What happens physically | Detection | Response |
|---|---|---|---|
| **Hook never loaded** | `OpenFileMapping` returns `NULL` | JNA `LastErrorException`; `Kernel32.GetLastError()` | Dashboard state `hook: absent`; retry every 2 s; no functional regression |
| **Hook process dies** | Java's view stays valid but frozen (mapping is reference-counted) | `heartbeatQpc` unchanged for > 250 ms **and** `ProcessHandle.of(hookPid).isAlive() == false` | Log `hook: exited`; unmap; retry attach |
| **Hook process suspended** (debugger, EDR freeze) | Same frozen view, but the PID is alive | `heartbeatQpc` stale + `isAlive() == true` | `hook: stalled`; **do not** tear down the mapping; the escape/nexus path must remain independent of the hook |
| **Hook restarts** | `CreateFileMapping` on the existing name returns the *existing* object with *current* size and `ERROR_ALREADY_EXISTS` ([CreateFileMappingW](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-createfilemappingw)) → stale `seq`s | `epoch` changed | Java resets `lastSeq` for both rings, clears counters, logs `hook: reattached (epoch N)` |
| **Java restarts** | Hook's `seq` keeps advancing into a ring nobody drains | Hook sees `producerDropped` climbing | Hook keeps writing (never blocks); logs locally at most once/second; the ring is small so damage is bounded |
| **Java too slow** | Consumer laps | `seq > lastSeq + 1` | Count `dropped`, resync `lastSeq`, emit one JSONL `hook.overflow` event (rate-limited) |
| **Ring corrupted / wrong region** | Garbage `seq` values | `magic`/`version` mismatch at attach, or `seq` outside `[lastSeq, lastSeq + capacity + 1]` | Refuse to attach; never index with an unvalidated `seq` |
| **Layout drift between hook and Java** | Fields read at wrong offsets | `version` mismatch | Refuse to attach; log both versions |
| **Clock skew** | `heartbeatQpc` is QPC ticks, monotonic per boot | n/a — compare only against a *Java-side* `System.nanoTime()` delta on the same ticks-per-second basis, or simpler: compare against a Java-side monotonic counter updated on each observed frame increment | Use the counter approach; it avoids cross-process clock questions entirely |

**Important correctness rule:** the *escape* must not depend on the round trip. Keep the dodge/escape decision in C++ (as the task specifies). Send Java the *events*; let Java's auto-nexus injector be the thing that acts on a request. If you ever need a Java-produced decision to influence the *same* frame, measure the worst case honestly (below).

### 6.5 Latency budget — be honest about where it goes

| Segment | Cost |
|---|---|
| Hook: build payload + memcpy into slot + release store | ~0.2 µs |
| Java: acquire load + decode + existing log/dashboard dispatch | ~1–10 µs (dominated by your existing code, not the transport) |
| Java → hook: release store, hook acquire load | ~0.1 µs |
| **Java poll quantum** (2 kHz) | **≤ 500 µs** |
| **Hook frame quantum** | **0 ms if applied in the same callback; 6.9 ms at 144 fps; 16.7 ms at 60 fps otherwise** |
| **Total worst case, decision applied on the next frame** | **~0.5 ms + one frame** → **~7.4 ms @144 fps, ~17.2 ms @60 fps** |

⇒ **The transport is not the problem; the frame quantisation is.** Two consequences:
1. Any per-frame dodge reaction **must be computed and applied inside the hook's frame callback**, with the Java round trip used only for observability and for the escape/nexus (which has its own, already-existing latency).
2. **"Well under 16 ms" holds at 144 fps but not at 60 fps** if a decision must return from Java before the next frame. Design so that it does not have to.

### 6.6 The smallest possible spike (do this first, before touching drelay)

**Spike 1 — prove the transport and the memory ordering (~120 lines total, no JNA, half a day).**
- `spike/shm_writer.cpp` (MSVC, `/std:c++20`): `CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, 1<<20, L"Local\\DrelaySpike")`; `MapViewOfFile`; `#pragma pack(push,1)` control block with `std::atomic<uint64_t> seq` at offset 0 and a `uint64_t payload[8]`; a loop that writes `payload` then `seq.store(n, std::memory_order_release)` at ~1 kHz, also writing a QPC heartbeat.
- `spike/ShmReader.java` (Java 25, **zero dependencies**): JNA-free — for this spike, use the file-backed variant (`FileChannel.open` + `map(READ_WRITE, 0, 1<<20, Arena.ofShared())`) so that **no library is involved at all**; read `seq` with `MethodHandles.byteBufferViewVarHandle(long[].class, ByteOrder.nativeOrder()).getAcquire(bb, 0)`, copy the payload, re-check `seq`, print observed rate and per-message latency distribution (p50/p99/max) using the writer's QPC heartbeat.
- **Success criteria:** observed rate matches the writer's within 0.1 %; **zero** torn reads over 10 M messages; p99 end-to-end < 1 ms; Java CPU < 1 % while spinning with backoff.
- **Then** repeat with JNA (`OpenFileMapping` + `MapViewOfFile` against the *pagefile-backed* `Local\DrelaySpike`) to prove the JNA attach path and measure the one-time attach cost. This is the only place JNA is used, so if it works here it works everywhere.
- **Then** kill the writer process mid-run and verify the Java reader reports "stalled" within 250 ms and does *not* crash or spin at 100 % CPU.
- **Then** restart the writer with a bumped `epoch` and verify the reader resyncs without losing a message after the resync point.

**Spike 2 — the control channel (~60 lines, half a day).** A 4-method JNA interface (`CreateNamedPipe`, `ConnectNamedPipe`, `ReadFile`, `WriteFile`) plus a C++ client, exchanging 200 B request/response at 200 Hz. Verify `ERROR_BROKEN_PIPE` handling on client death and the `DisconnectNamedPipe`+`ConnectNamedPipe` restart cycle. Do this in parallel with spike 1 — it is independent.

**Spike 3 — only then** wire it into `drelay`: add `net/drelay/hook/HookShm.java`, `HookShmReader.java`, `HookCmdRing.java`, `HookLayout.java` (all offsets/sizes as `static final` constants in one file, mirrored by a generated `hook_layout.h`), plus a `hook.attach`/`hook.event`/`hook.overflow` event type in the existing JSONL schema and a dashboard panel.

**What the spike must NOT do:** no DLL injection, no game reading, no .NET, no dodge math. The spike's only job is to falsify the two things that can actually kill this design — (a) whether cross-process release/acquire works reliably from a JVM-hosted `ByteBuffer` over a mapped Windows section, and (b) whether the JNA attach path is robust. Everything else is ordinary code.

---

## References

**JNA**
- https://raw.githubusercontent.com/java-native-access/jna/master/README.md
- https://raw.githubusercontent.com/java-native-access/jna/master/CHANGES.md
- https://raw.githubusercontent.com/java-native-access/jna/master/www/FrequentlyAskedQuestions.md
- https://raw.githubusercontent.com/java-native-access/jna/master/www/FunctionalDescription.md
- https://raw.githubusercontent.com/java-native-access/jna/master/www/DirectMapping.md
- https://raw.githubusercontent.com/java-native-access/jna/master/www/CallbacksAndClosures.md
- https://raw.githubusercontent.com/java-native-access/jna/master/www/GettingStarted.md
- https://raw.githubusercontent.com/java-native-access/jna/master/www/PlatformLibrary.md
- https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Native.java
- https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Pointer.java
- https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Memory.java
- https://raw.githubusercontent.com/java-native-access/jna/master/src/com/sun/jna/Platform.java
- https://raw.githubusercontent.com/java-native-access/jna/master/contrib/platform/src/com/sun/jna/platform/win32/Kernel32.java
- https://raw.githubusercontent.com/java-native-access/jna/master/contrib/platform/src/com/sun/jna/platform/win32/WinBase.java
- https://raw.githubusercontent.com/java-native-access/jna/master/contrib/platform/src/com/sun/jna/platform/win32/WinNT.java
- https://raw.githubusercontent.com/java-native-access/jna/master/contrib/platform/test/com/sun/jna/platform/win32/Kernel32NamedPipeTest.java
- https://repo1.maven.org/maven2/net/java/dev/jna/jna/maven-metadata.xml
- https://repo1.maven.org/maven2/net/java/dev/jna/jna/5.19.1/
- https://repo1.maven.org/maven2/net/java/dev/jna/jna-platform/5.19.1/
- https://repo1.maven.org/maven2/net/java/dev/jna/jna/5.19.1/jna-5.19.1.pom

**Java / OpenJDK**
- https://openjdk.org/jeps/454 (FFM, delivered JDK 22)
- https://openjdk.org/jeps/472 (restrict JNI, delivered JDK 24)
- https://openjdk.org/projects/jdk/25/
- https://docs.oracle.com/en/java/javase/25/docs/api/java.base/java/lang/foreign/Linker.html
- https://docs.oracle.com/en/java/javase/25/docs/api/java.base/java/lang/invoke/VarHandle.html
- https://docs.oracle.com/en/java/javase/25/docs/api/java.base/java/nio/channels/FileChannel.html
- https://raw.githubusercontent.com/openjdk/jdk/master/src/java.base/share/classes/java/lang/foreign/Linker.java
- https://raw.githubusercontent.com/openjdk/jdk/master/src/java.base/windows/classes/sun/nio/fs/WindowsChannelFactory.java
- https://raw.githubusercontent.com/openjdk/jdk/master/src/java.base/windows/native/libnio/ch/FileDispatcherImpl.c
- https://raw.githubusercontent.com/openjdk/jdk/master/src/java.base/share/classes/sun/nio/ch/FileChannelImpl.java
- https://raw.githubusercontent.com/openjdk/jdk/master/src/java.base/share/classes/sun/nio/ch/Net.java
- https://raw.githubusercontent.com/openjdk/jdk/master/src/java.base/windows/native/libnet/net_util_md.c
- https://builds.shipilev.net/backports-monitor/release-notes-8u60.txt

**Microsoft Learn**
- https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-type-read-and-wait-modes
- https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea
- https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-setnamedpipehandlestate
- https://learn.microsoft.com/en-us/windows/win32/ipc/pipe-names
- https://learn.microsoft.com/en-us/windows/win32/ipc/synchronous-and-overlapped-input-and-output
- https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-createfilemappingw
- https://learn.microsoft.com/en-us/windows/win32/winsock/sio-loopback-fast-path
- https://learn.microsoft.com/en-us/windows/win32/dataxchg/wm-copydata

**Benchmarks**
- https://raw.githubusercontent.com/archenroot/java-native-benchmark-archenroot/master/README.md
- https://raw.githubusercontent.com/Glavo/java-ffi-benchmark/master/README.md
- https://raw.githubusercontent.com/anton-dutov/bench-win-ipc/main/README.md
- https://raw.githubusercontent.com/suenot/trading-ipc-bench/main/README.md

**Other projects / papers**
- https://github.com/ShirasawaSama/JavaSharedMemory
- https://github.com/Minzoid/Memory
- https://github.com/UnderMybrella/ColonelAccess
- https://github.com/real-logic/aeron
- https://sacj.cs.uct.ac.za/index.php/sacj/article/view/500 (DOI 10.18489/sacj.v29i3.500)
- https://maven.apache.org/plugins/maven-shade-plugin/examples/resource-transformers.html
- https://ricosheaven.com/en/posts/win32-ipc-memory-management-guide/ (informal)

---

## Explicit uncertainties

1. **`Pointer.getInt` has no direct microbenchmark.** The "tens of nanoseconds" figure is inferred from the measured JNI downcall cost (56.6 ns, Windows/x64). StackOverflow returned HTTP 403 to my fetcher, so the `JNA Native.setXXX() slow` thread could not be read.
2. **`FileChannel.open` on `\\.\pipe\…` is unverified.** Only `RandomAccessFile` is documented-by-implication (via `FileDispatcherImpl.read0`'s pipe error handling). Test it in spike 2.
3. **Shade-plugin relocation of `com.sun.jna`** — I could not confirm whether the `"/com/sun/jna/"` string literal in `Native.loadNativeDispatchLibraryFromClasspath()` is rewritten. Recommendation: do not relocate.
4. **`bench-win-ipc` iteration semantics** — the README does not state whether an iteration is one-way or a round trip. The ~10 µs (TCP) / ~13 µs (pipe) figures are quoted with that ambiguity.
5. **Aeron's IPC implementation and latency figures** — not verified (docs URLs 404). Aeron is cited only for the existence of an IPC transport.
6. **Chronicle's latency figures** — not verified; no numbers quoted.
7. **`JmhGetSystemTimeSeconds`/`JmhCallOnly` are from a *fork* (`archenroot`) of the original `zakgof/java-native-benchmark`**, on OpenJDK 19 and Windows 10 — a 4-year-old JDK on a low-power laptop CPU. The JNA/JNI/FFM *ratios* are the transferable part; the absolute JNA figure may be better on a modern desktop.
8. **`Glavo/java-ffi-benchmark` is Linux**, not Windows. Its upcall ratio (FFM 3.5–4× JNI) is the key number and is architecture-level, not OS-level, but it is not a Windows measurement.
9. **No measured Windows shared-memory latency number was found.** The "~0.1–1 µs" figure is derived from the design (two atomic stores, no syscall), not measured. Spike 1 exists precisely to replace it with a real number.
