# Redis patches for the Windows (Cygwin / MSYS2) builds

This folder holds the fork's changes to the Redis source, as `git format-patch`
files (`-p1`, relative to the Redis source root). The CI applies them in lexical
order to the extracted upstream tarball before it builds.

- **Patch base:** Redis 8.10.2 (a security release). The series also applies to
  8.10.1, the production version, with no fuzz (checked for all eight patches).
- **Scope:** 0001-0004 and 0008 are limited to Cygwin/MSYS2 builds, or to the
  select() event-loop backend. The others build on every platform, and change nothing
  there unless you turn them on:
  - 0005 is a build-flag fix.
  - 0006 adds `reply-node-max-bytes`, which splits large replies over smaller
    buffers, with the same bytes on the wire and the same accounting.
  - 0007 adds `oom-soft-client-buffers`.

  Both options are on by default only on Cygwin/MSYS2. With them off (the
  default elsewhere), Linux, macOS and the BSDs behave as upstream, and the
  tests turn them on to run everywhere.

## Applying

```bash
scripts/apply-patches.sh <redis-source-dir>
```

- Uses `patch` if it is installed, otherwise `git apply`. `APPLY_TOOL=patch|git`
  forces one; `PATCH_DIR` points at another patch folder.
- Validates the whole series first. Then it checks each patch with no fuzz before
  applying it, so a patch that does not apply leaves no partial hunks. It stops
  at the first failure with exit code 1, and it confirms that every file a patch
  names really changed.
- Works with bash 3.2+ on MSYS2, Cygwin, Linux and macOS.
- **The CI needs `patch` installed:** `pacman -S patch` on MSYS2, and the `patch`
  package on Cygwin. `git apply` works too, but under Cygwin the only git on PATH
  may be Git for Windows, which does not understand POSIX paths.
- `.gitattributes` here marks `*.patch -text`. A CRLF checkout would make every
  hunk fail, and the script refuses such files with a clear message.

**Build expectations after patching:**

- Drop the `sed -i 's/__GNU_VISIBLE/1/' /usr/include/dlfcn.h` step. 0001 makes it
  unnecessary, and 0001 still compiles if an old runner applies the sed anyway.
- Build with `make OPTIMIZATION=-O2 ...`. Never put `-O` levels in `CFLAGS`.

## The series

| Patch | Platforms | Visible change |
|---|---|---|
| 0001-cygwin-gnu-source | Cygwin/MSYS2 | none (build fix) |
| 0002-select-fdsetsize-guard | select() builds (Cygwin/MSYS2) | `maxclients` capped at 895; new WARNING log line |
| 0003-win32-memory-reporting | Cygwin/MSYS2 | real `used_memory_rss`; 4 new INFO memory fields; fork log line relabelled |
| 0004-oom-handler-commit-log | Cygwin/MSYS2 | one extra log line before an OOM panic |
| 0005-xxhash-optimization | all | none (xxhash built at -O2 instead of -O0) |
| 0006-reply-node-cap | all; on by default only on Cygwin/MSYS2 | new hidden config `reply-node-max-bytes`; a large reply uses reply buffers of at most 256 KB |
| 0007-soft-oom-client-buffers | all; on by default only on Cygwin/MSYS2 | new config `oom-soft-client-buffers`; new INFO stats field `client_oom_disconnections`; `-OOM` error and WARNING log lines; new `DEBUG SET-ALLOC-FAIL-THRESHOLD` |
| 0008-cygwin-absolute-drive-paths | Cygwin/MSYS2 | `redis-server.exe C:/path/redis.conf` works: Windows drive paths count as absolute |

Each patch's commit message has the full what, why and risk.

### 0001: cygwin-gnu-source

- **What:** `src/debug.c` sets `__GNU_VISIBLE` to 1 only around its own
  `#include <dlfcn.h>` on `__CYGWIN__`, then restores it.
- **Why:** Cygwin's `dlfcn.h` hides `Dl_info` and `dladdr()` unless `_GNU_SOURCE` is
  set, and Redis sets it only on Linux, so `debug.c` did not compile. The CI used
  to edit the toolchain header in place.
- **Why not `_GNU_SOURCE`:** defining it for the file would also switch
  `strerror_r()` and `basename()` to their GNU variants. This patch has exactly the
  effect of the old sed, confined to one file.
- **How to test:** the Cygwin and MSYS2 builds succeed without the sed. In the build
  shell, `grep -c __GNU_VISIBLE /usr/include/dlfcn.h` shows the header is untouched.

### 0002: select-fdsetsize-guard

- **What:** applies only to builds using the select() event loop (`USE_AE_SELECT` in
  `config.h`: no epoll, kqueue or evport).
  - At startup, and on every `CONFIG SET maxclients`, `maxclients` is capped at
    `FD_SETSIZE - CONFIG_FDSET_INCR - 1` = **895**.
  - `aeApiAddEvent()` refuses any fd >= `FD_SETSIZE` with `ERANGE`.
  - The build fails if `FD_SETSIZE` is too small, for example 64 when
    `__CYGWIN__` is not predefined.
- **Why:** Cygwin allows 3200 open files, so Redis used to start with
  `maxclients 3168` and an event loop of 3296 slots, while `fd_set` holds 1024 bits.
  The first connection at fd 1024 or above overran the heap on every select()
  wakeup, or panicked. Forcing the select backend on macOS reproduced the panic:
  1,100 connections gave `aeApiPoll: select, Invalid argument`.
- **Log line:** `This build uses the select() event loop, which cannot watch file
  descriptors of 1024 or higher (FD_SETSIZE). maxclients has been reduced from 10000
  to 895.` (10000 is the default `maxclients`; the cap is applied before the
  open-files limit, so the old "reduced to 3168" line no longer appears.)
- **CONFIG SET above the cap:** `ERR ... The operating system is not able to
  handle the specified number of clients, try with 895`.
- **Not done: raising `FD_SETSIZE`.** It is safe only if every translation unit sees
  the same value, and a two-connection cache gains nothing from it. The cap is
  derived from `FD_SETSIZE`, so passing `CFLAGS=-DFD_SETSIZE=2048`, which reaches
  src and deps alike, raises it automatically. The runtime still limits a session
  to 2048 sockets.
- **Known limit:** `redis-benchmark` and `redis-cli` size their own event loops at
  10240. With this patch, their connections above fd 1023 are refused rather than
  corrupting memory, and those benchmark clients stall. **On these builds, run
  redis-benchmark with `-c 900` or fewer, and use a script for the connection
  test below.**
- **How to test (Windows CI):**
  1. Start `redis-server --maxclients 3000`.
  2. The log has the line above, and `CONFIG GET maxclients` returns `895`.
  3. `CONFIG SET maxclients 2000` fails with "try with 895".
  4. Open 1,100 TCP connections from Python or PowerShell and send `PING` on each:
     895 get `+PONG` and 205 get `-ERR max number of clients reached`.
  5. The server still answers `PING`.
  6. Also record `gcc -dM -E - </dev/null | grep -E '__CYGWIN__|__MSYS__'`.

### 0003: win32-memory-reporting

- **What:** Cygwin/MSYS2 builds only (`HAVE_WIN32_MEMINFO` when `__CYGWIN__` is
  defined; the Makefile adds `win32_meminfo.o` when `uname -s` starts with CYGWIN
  or MSYS).
  - New file `src/win32_meminfo.c` includes only `<windows.h>` and `<psapi.h>`. It
    calls the kernel32 functions `K32GetProcessMemoryInfo` and
    `K32GetPerformanceInfo`, falling back to `GlobalMemoryStatusEx`. The gcc driver
    already links kernel32, so no import library is added, and nothing calls
    malloc.
  - `used_memory_rss` becomes the process **working set**, instead of a copy of
    `used_memory`. `mem_fragmentation_ratio` and the `allocator_*` ratios become
    real. Windows can trim a working set, and it includes shared DLL pages, so the
    ratio can drop below 1.0.
  - In the BGSAVE child, `zmalloc_get_private_dirty(-1)` returns the child's
    private commit charge. Cygwin fork copies the parent's heap; it is not
    copy-on-write. That value feeds the log line below and `rdb_last_cow_size`,
    `current_cow_size`, `current_cow_peak`, `aof_last_cow_size` and
    `module_fork_last_cow_size` on these builds.
- **Not used: `/proc/self/stat` or `statm`.** Field 24 counts
  4 KB pages while `sysconf(_SC_PAGESIZE)` is 64 KB, which inflates RSS 16x. statm
  walks the whole working set with malloc.
- **New INFO fields, `# Memory` section** (Cygwin/MSYS2 builds only, read live on
  each `INFO`, 0 = unavailable):

  | Field | Source | Meaning |
  |---|---|---|
  | `process_private_bytes` | `PrivateUsage` | this process's private commit charge: what "Private Bytes" shows in PerfMon |
  | `process_working_set` | `WorkingSetSize` | resident memory; same value as `used_memory_rss`, but live |
  | `system_commit_total` | `CommitTotal x PageSize` | commit charged system-wide |
  | `system_commit_limit` | `CommitLimit x PageSize` | RAM plus page files; allocations fail at this limit |

- **Log line change:** the child's line `Fork CoW for RDB: current 0 MB, ...` becomes
  `Fork private copy for RDB: current N MB, peak N MB, average N MB (Cygwin fork is
  not copy-on-write)`.
- **Cost:** the 100 ms cron reads one cheap counter (`ProcessVmCounters`).
  GetPerformanceInfo is heavier, since it counts every process's handles and
  threads, so it runs only when `INFO memory` is requested.
- **How to test (Windows CI):**
  1. `DEBUG POPULATE 200000 k 1024`.
  2. `used_memory_rss` is not equal to `used_memory`, and is within 30% of
     `(Get-Process redis-server).WorkingSet64`.
  3. `process_private_bytes` is within 30% of `PrivateMemorySize64`.
  4. `0 < system_commit_total < system_commit_limit`.
  5. `BGSAVE`: the log shows `Fork private copy for RDB` with a non-zero `current`
     value, roughly the parent's private bytes.
  6. The build itself proves that the w32api headers (`windows.h`, `psapi.h`) ship
     with MSYS2 `gcc` and Cygwin `gcc-core`.

### 0004: oom-handler-commit-log

- **What:** Cygwin/MSYS2 builds only. When an allocation fails,
  `redisOutOfMemoryHandler()` first samples the counters from 0003. After the
  existing `Out Of Memory allocating N bytes!` line and before the panic, it writes:

  ```
  <pid>:signal-handler (<unix time>) Out Of Memory diagnostics: system_commit_total=<bytes> system_commit_limit=<bytes> system_physical_available=<bytes> process_private_bytes=<bytes> process_working_set=<bytes> source=GetPerformanceInfo
  ```

  The line is written by `serverLogFromHandler()`, the existing async-signal-safe
  logger. It formats into a stack buffer and writes with `open()`/`write()`, so
  nothing allocates heap after the failure. The `signal-handler` prefix is that
  logger's fixed format.
- **Reading the source field:** `source=GlobalMemoryStatusEx` means
  GetPerformanceInfo itself failed under the pressure. Those figures are capped by
  a job object's commit limit, if there is one. `source=unavailable` means both
  calls failed.
- **Why:** on this platform malloc returns NULL only after Windows has refused a
  commit. This line tells, in the log itself, whether the host was at its commit
  limit or Redis's own commit had grown. `errno` (always ENOMEM) and
  `GetLastError()` (already overwritten) cannot.
- **How to test (Windows CI):**
  1. On a throwaway instance with `enable-debug-command yes`, run `redis-cli DEBUG OOM`.
     It calls `zmalloc(SIZE_MAX/2)`, which goes through the same handler, so no
     new debug hook is needed.
  2. The server aborts, and the log contains `Out Of Memory diagnostics:
     system_commit_total=` with non-zero `system_commit_limit=`.
- **Keep `enable-debug-command no` in production configs** (the default), so
  `DEBUG OOM` cannot be reached there.

### 0005: xxhash-optimization

- **What:** `deps/Makefile` passes xxhash an explicit level: the `OPTIMIZATION`
  given on the make command line, else `-O2`. A level in `CFLAGS` still wins.
- **Why:** xxhash's own `CFLAGS ?= -O3` is overridden by the `CFLAGS` that
  deps/Makefile passes, so it built at -O0 even in upstream's default build.
  Unlike hiredis, `OPTIMIZATION` alone never reached it.
- **How to test:** the xxhash compile line in the build log shows `-fPIC -O2`.

### 0006: reply-node-cap

- **What:** a new hidden config, `reply-node-max-bytes`: 0 (no split) or at least 16 KB. Values in between are refused with an error.
  - When it is set, `_addReplyPayloadToList()` (not `_addReplyProtoToList`,
    which exists only in comments) splits a plain reply over list nodes of at
    most that many bytes of buffer. Nodes are still at least 16 KB.
  - Default 256 KB - 64 on `__CYGWIN__` builds, so a 1.87 MB hash field becomes
    8 nodes instead of one 1.87 MB block. Default 0 elsewhere, which keeps
    upstream's allocation pattern.
  - `CONFIG SET` can change it at run time. Being hidden, it does not show in
    `CONFIG GET *`; ask for it by name.
  - Fake clients (Lua, modules, AOF loading) are never split: their reply is
    concatenated afterwards anyway.
  - Copy-avoidance nodes (`BULK_STR_REF`, used by GET on large strings) are
    unchanged.
- **Why:** a production crash of the MSYS2 build was this allocation,
  `zmalloc_usable(1868918 + 24)`, for an HMGET of a 1.87 MB hash field. Hash values are always copied into the
  reply. With the node header, the zmalloc prefix and dlmalloc's chunk overhead
  included, a node stays below the 256 KB mmap threshold of the Cygwin/MSYS2 malloc.
  So reply nodes come from the heap and reuse freed chunks, instead of a fresh
  `VirtualAlloc` commit per reply.
- **Unchanged:**
  - `reply_bytes` still grows by every node. `client-output-buffer-limit` is still
    checked once, after the whole payload.
  - **Write batching.** The nodes after the first carry a `buf_continued` flag,
    which fits in the existing padding: `clientReplyBlock` stays 24 bytes.
    `_writevToClient()` adds continuation nodes to the same `writev()` even past
    `NET_MAX_WRITES_PER_EVENT`, so a split reply is offered to the socket exactly
    as the one large node was.
  - Without that flag, each event would write only 256 KB. On macOS a 1.9 MB
    HGET took 8 writes and 8 event-loop cycles per request instead of 1, and on
    Cygwin each extra cycle is a select() round trip.
- **Still not removed:** the stored value and the query buffer of each big HSET
  still need about 1.9 MB contiguous. 0007 makes the query buffer failure
  non-fatal.
- **How to test:** `tests/unit/win-soft-oom.tcl`, tests named "Reply node cap".
  They check that the default is on only for Cygwin/MSYS2, and that with the
  option off a 2 MB reply is not split. With it on, CLIENT INFO inside MULTI
  after a 2 MB HMGET shows `oll` >= 8 and `omem` <= `oll` x 256 KB; unpatched
  8.10.2 shows `oll=1`. Benchmark:
  `redis-benchmark -n 4000 -c 1 hget h data` (and `-c 2`) against a 1.9 MB field.
  INFO `total_writes_processed` and `eventloop_cycles` should match an unpatched
  build.

### 0007: soft-oom-client-buffers

- **What:** a new config, `oom-soft-client-buffers yes|no`.
  - `CONFIG SET` can change it at run time.
  - Default `yes` on `__CYGWIN__` builds (MSYS2 and Cygwin), `no` elsewhere.
  - When it is on, these buffers of **real socket clients** use the try-allocators
    (`ztrymalloc_usable`, `sdstrynewlen`, and new `sdsTryMakeRoomFor` /
    `sdsTryMakeRoomForNonGreedy`). If one cannot be allocated, only that client
    is closed.
  - A failed `sdsTryMakeRoomFor` leaves the string valid. Sizes of
    `SIZE_MAX/2` or more, for which `ztryrealloc` frees the old buffer, are
    refused before anything is allocated.
- **Query buffer growth** in `readQueryFromClient()`, on both the big-argument
  path and the greedy path:
  - Nothing of the request has run yet.
  - The client gets
    `-OOM could not allocate the query buffer for this request, closing the connection`
    and is closed after that reply.
  - The error travels as a new read error, `CLIENT_READ_QUERYBUF_OOM`, so it
    also works with `io-threads` > 1.
  - The unread request keeps the socket readable. If the growth fails again
    before the `-OOM` reply is written (the client is not reading its earlier
    replies), the client is closed at once, without that reply. It is not
    polled again, so the event loop cannot spin on it, and it is counted once.
  - Like upstream, a request whose header arrived but whose value stops
    arriving just waits for more data. The failure happens on the next read.
  - The size hint in `processMultibulkBuffer()` and the speculative pre-allocation
    after a fat argument are optimisations only. When they fail softly, the hint
    is skipped and the pre-allocation falls back to 16 KB. The read above then
    decides.
- **Reply list nodes** (plain payloads on the client's own reply list):
  - The command has already run, so its reply is lost, as it would be on a
    network drop.
  - The client is closed asynchronously (`freeClientAsync`, `CLIENT_CLOSE_ASAP`),
    and nothing it pipelined after that command runs.
  - **A write command stays applied and propagated.** HSET inside a MULTI whose
    EXEC reply fails is still written. The client, for example
    StackExchange.Redis, sees a dropped connection and reconnects.
- **Still fatal, as before:**
  - masters, replicas and monitors;
  - fake clients: Lua and function callers, modules, AOF loading;
  - copy-avoidance nodes, which hold an object reference;
  - push messages queued during a command;
  - keyspace, replication, AOF/RDB, module, Lua and MULTI allocations;
  - every other allocation.

  So this buys time; it does not make the server immune.
- **INFO:** `client_oom_disconnections` in `# Stats` counts both kinds, and
  `CONFIG RESETSTAT` clears it.
- **Log lines** (WARNING, at most one per second in total):
  - `Client <info> scheduled to be closed ASAP: could not allocate <n> bytes for its reply (oom-soft-client-buffers).`
  - `Closing client that could not allocate its query buffer (oom-soft-client-buffers): <info> (pending bulk length: <n>)`
- **Fault injection:** `DEBUG SET-ALLOC-FAIL-THRESHOLD <bytes>`.
  - Soft allocations of `<bytes>` or more fail; `0` turns it off. For a query
    buffer the size is its content plus the growth.
  - It affects only the allocations listed above, and only while
    `oom-soft-client-buffers` is on. With the option off it has no effect.
  - It works on every platform.
  - DEBUG stays behind `enable-debug-command`, which is `no` by default. Keep it
    `no` in production.
  - With `reply-node-max-bytes` at its Windows default, reply nodes are at most
    256 KB (0006), so a threshold above that never hits a reply. Use 65536 to
    fail large replies, and 1048576 to fail only a 2 MB query buffer.
- **How to test:** `tests/unit/win-soft-oom.tcl`, tests named "Soft OOM". It is
  picked up automatically, because `test_helper.tcl` globs `tests/unit`.
  - The raw-socket tests (a large HSET on a plain TCP socket, and a client
    that cannot receive its `-OOM`) are tagged `tls:skip`, so `--tls` runs
    skip them. The same checks through the regular test client still run.
  - The raw HSET helper sends the header together with 2000 bytes of the value,
    then keeps sending 1000-byte pieces every 50 ms until a reply or EOF
    arrives (5 s at most). It does not depend on how packets are coalesced.
  - The "cannot receive its -OOM" tests pipeline 20 GETs of a 2 MB value
    without reading them, then send a large HSET. They check that the client
    is closed within 5 s and, without io-threads, that the event loop runs
    fewer than 1000 cycles in the next 500 ms.
  - Counts are checked as deltas, so a skipped test does not shift later ones.

  On the Windows CI:
  1. `./runtest --single unit/win-soft-oom`
  2. Or by hand, on an instance with `enable-debug-command yes`:
     `HSET h data <2 MB>`, `DEBUG SET-ALLOC-FAIL-THRESHOLD 65536`, then from a
     second connection `HMGET h data`. That connection is closed; `PING` on the
     first still answers; `INFO stats` shows `client_oom_disconnections:1`.
  3. `DEBUG SET-ALLOC-FAIL-THRESHOLD 1048576` and a 2 MB `HSET` gets the `-OOM`
     error, and the key is not created.
  4. On the Windows build, `CONFIG GET oom-soft-client-buffers` returns `yes`
     with no config line.

### 0008: cygwin-absolute-drive-paths

- **What:** on Cygwin/MSYS2 builds, `getAbsolutePath()` returns a path that
  starts with a drive letter and a separator (`C:/...`, `C:\...`) unchanged,
  as it already does for a path starting with `/`. A drive-relative path
  such as `C:redis.conf` is still treated as relative.
- **Why:** Redis stores its config file path through this helper, which
  treated anything not starting with `/` as relative and prepended the current
  directory. `redis-server.exe C:/Redis/redis.conf` therefore tried to open
  `/<cwd>/C:/Redis/redis.conf` and exited with `Fatal error, can't open config
  file`, and so did every start by the service, which passes Windows paths.
  The runtime itself opens Windows paths as they are. The same helper resolves
  the executable path and AOF directory paths.
- **How to test:** on Windows, `redis-server.exe C:/path/to/redis.conf` starts,
  and `CONFIG GET` shows the settings from that file. On other platforms nothing
  changes; the branch is compiled only with `__CYGWIN__`.

## Testing

**On Linux or macOS**, for everything that is not Windows-specific:

```bash
scripts/apply-patches.sh <redis-8.10.2-source>
cd <redis-8.10.2-source>
make -j4 OPTIMIZATION=-O2
./runtest --single unit/win-soft-oom --single unit/type/hash --single unit/networking \
          --single unit/querybuf --single unit/obuf-limits --single unit/scripting
```

Add `--config oom-soft-client-buffers yes --config reply-node-max-bytes 262080` to
run the upstream suites with the Windows defaults.

What these checks establish:

- The series applies with no fuzz to 8.10.2 and 8.10.1, with both `patch` and
  `git apply`, and refuses an already-patched, conflicting or CRLF-converted tree
  without leaving partial hunks.
- Both patched trees build without warnings.
- `unit/win-soft-oom` and the upstream units above pass, with and without
  `io-threads 2`.
- The tests discriminate: on unpatched 8.10.2 the node-cap test fails with
  `oll` = 1, and the "cannot receive its -OOM" tests fail without the
  networking fix in 0007.
- Every server, cli and benchmark translation unit compiles with
  `clang -target x86_64-pc-cygwin -fsyntax-only` against the MSYS2 runtime 3.6.10
  headers plus the mingw-w64 w32api headers: 0 errors, no new warnings. Without the
  series, only `debug.c` fails (the `Dl_info`/`dladdr` errors 0001 fixes).
- 0002 on the select() backend (a macOS build with kqueue disabled): 1,100
  connections give 895 PONG and 205 rejections with the server alive, where
  unpatched 8.10.2 panics with `aeApiPoll: select, Invalid argument`.

**Only the Windows CI can prove:**

- that the binaries link and run;
- the Win32 values themselves;
- the Cygwin select() behaviour past fd 1024;
- that `__CYGWIN__` is predefined by the MSYS2 gcc, and with it the Windows
  defaults of both options;
- the OOM log line;
- that a **real** Windows commit failure takes the soft path. Locally only
  `DEBUG SET-ALLOC-FAIL-THRESHOLD` exercises it. The code after `NULL` is the
  same either way, but the real case needs host commit pressure;
- write batching and throughput of large replies on Winsock. Run the 0006
  benchmark above against an unpatched build.

Use the "How to test" steps above.

## Maintaining the series

For a new Redis release, for example a security release:

1. Extract the pristine tarball and run `git init`, then commit it as
   "pristine <version>".
2. Run `git am patches/redis/*.patch`, and resolve any conflict per commit.
3. Export with `git format-patch -N --no-signature --zero-commit -o out <base>..HEAD`,
   then rename the files to `NNNN-short-name.patch`.
4. Keep one commit per patch. Its message is the patch header: what, why, and
   risk.
