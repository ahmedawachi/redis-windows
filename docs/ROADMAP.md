# Roadmap

Why this fork exists, what it changes, and what is still open.

Status legend:

- **Done (this pass)** - code or config is in this repository. Windows-only
  behaviour is proven only by the Windows CI jobs named in the item.
- **Needs Windows measurement** - implemented or proposed, but the benefit or
  safety can only be confirmed on Windows (CI runner or a real host).
- **Deferred** - agreed direction, not started.
- **Rejected** - considered and decided against.

## 1. The incident that started it

A production deployment of the upstream MSYS2 build (Redis 8.10.1 under the
upstream `RedisService.exe`) aborted with `Out Of Memory allocating 1868942
bytes` while its dataset was only about 64 MB, well under `maxmemory`. It
stayed down for over half an hour.

What the source and the log show:

1. The failed allocation was one reply-buffer node for a ~1.87 MB hash field
   (HMGET copies hash values into the reply; the client's static reply
   buffer was already full, so the rest needed one contiguous node).
2. On Cygwin/MSYS2 the "libc" allocator is the runtime's dlmalloc behind one
   global lock. Requests of 256 KB and more are served by fresh
   `VirtualAlloc` commits, with two fallbacks. `malloc` returns NULL only when
   Windows refuses the commit every time, i.e. the **host** (or a quota) was
   out of commit - not Redis's dataset limit.
3. Redis treats any failed allocation as fatal (`serverPanic` -> `abort()`).
4. The upstream wrapper only logged a warning when redis-server died, never
   restarted it, and kept reporting Running, so Windows service recovery could
   never fire.
5. On this platform `used_memory_rss` is fake (it equals `used_memory`), so
   nobody could see Redis's real footprint or the host's commit.

Ranked causes of the refused commit: the whole host hitting its commit limit
(most likely; another process shares the host); Redis's own commit growing
unseen (not excluded until Private Bytes data exists); a job/quota limit
(unlikely). Evidence collection is scripted in
`scripts/collect-host-evidence.ps1` and described in
[OPERATIONS.md section 7](OPERATIONS.md#7-incident-host-evidence-checklist).

## 2. Configuration (no rebuild)

| Item | Status |
|---|---|
| Cache profile `conf/redis.windows-cache.conf`: `save ""` (no forks; `save 3600 1` documented as the warm-start alternative), `appendonly no`, `stop-writes-on-bgsave-error no`, `maxmemory` + `allkeys-lru`, `maxclients 895`, `io-threads 1`, `hz 10`, explicit absolute `logfile`/`dir`, loopback bind, `protected-mode yes`, `requirepass`, `latency-monitor-threshold 100`, `enable-debug-command no`; `oom-soft-client-buffers yes` present but commented out (fork builds default to it; stock builds reject it) | Done (this pass). Every stock directive checked against Redis 8.10.2 `config.c` and load-tested on a stock build. |
| Interim watchdog for hosts on the stock wrapper (`scripts/redis-watchdog.ps1`, authenticated PING, exact `PONG`, N failures -> `Restart-Service`) | Done (this pass). Probe and state logic tested off-Windows; `Restart-Service` / event log behaviour needs a Windows run. |
| Read-only host evidence collector (`scripts/collect-host-evidence.ps1`) | Done (this pass). Parsed and partially exercised off-Windows; full run needs Windows. |
| Operations guide: upgrade/rollback, security-release policy, monitoring, PerfMon collector, Defender test (`docs/OPERATIONS.md`) | Done (this pass) |
| Alert on the application's own Redis health check | Ops action - see OPERATIONS.md section 5 |

## 3. Tier 1 - low-risk fork changes

| # | Change | Status |
|---|---|---|
| T1-1 | Build at `OPTIMIZATION=-O2`, no LTO (upstream binaries are built at -O0). Never put `-O` levels in `CFLAGS`. Workflow input to select another level (e.g. `-O0` fallback). | Done (this pass). Needs Windows measurement: start-up and test-suite pass at -O2 on both toolchains, and the real CPU/latency gain (macOS proxy, published with its raw data on the benchmark page: 1.6-1.9x less CPU per pipelined small command, background saves 1.7-2.2x faster, multi-MB values even). |
| T1-2 | CI-only bisect matrix to find what broke the old 7.2 -O3+LTO builds (-O3, LTO at O2, `-rdynamic`, ...) | Deferred |
| T1-3 | Reject upstream experimental flag sets: no `-march`, `-funroll-loops`, `-fomit-frame-pointer`, no bare -O3+LTO | Done (this pass) - policy enforced by the build script |
| T1-4 | Pipeline hygiene: `_GNU_SOURCE` source patch instead of editing the toolchain's `dlfcn.h` (patch 0001), xxhash at -O2 (patch 0005), strip binaries and ship debug info separately, record toolchain versions in a `build-info` file | Done (this pass). Pinning the MSYS2 runtime version is Deferred. |
| T1-5 | FD_SETSIZE guard: clamp `maxclients` to 895 (keeps `maxclients + 128` below FD_SETSIZE 1024) on select() builds and reject any fd >= FD_SETSIZE (patch 0002) - a stock build corrupts heap memory once any fd >= 1024 exists | Done (this pass). Needs Windows measurement: 1,100+ idle connections test in CI. |
| T1-6 | Real memory reporting: INFO `process_private_bytes`, `process_working_set`, `system_commit_total`, `system_commit_limit` (Cygwin/MSYS2 builds) | Done (this pass). Needs Windows measurement: values match PerfMon. |
| T1-7 | Log host commit state (commit total/limit, available memory, process private bytes/working set) from the out-of-memory handler, without allocating | Done (this pass, patch 0004). Needs a Windows CI run of the crash-log check. |
| T1-8 | Supervisor in the wrapper: restart on crash with backoff, crash-loop cap -> exit 1067 so SCM recovery fires; a clean exit the wrapper did not request stops the service by default (`--restart-policy`) | Done (this pass). Proven by the Windows wrapper integration test. |
| T1-9 | Correct graceful shutdown: authenticated `SHUTDOWN` over the wrapper's own RESP client (reads port/bind/requirepass from redis.conf), `--stop-timeout` | Done (this pass). Windows integration test. |
| T1-10 | Job object on the wrapper so a wrapper crash never orphans redis-server | Done (this pass). Windows integration test. |
| T1-11 | Health probe (PING every 5 s, restart after 12 misses) and real readiness (wait for PONG) | Done (this pass). Windows integration test. |
| T1-12 | Install hardening: failure actions (restart 5 s / 30 s / 60 s, reset one day) with the non-crash flag, SID type unrestricted, event source registered at install, real description, options under the service's `Parameters` key instead of ImagePath, delayed start, virtual account `NT SERVICE\<name>` as an **opt-in** flag (`--virtual-account`, which also grants the folder ACLs; LocalSystem remains the default for backward compatibility) | Done (this pass). Windows integration test (`sc qfailure`, `qfailureflag`, `qsidtype`). |
| T1-13 | Event log and CLI: lifecycle events in English, crash excerpt, strict option parsing (exit code 2 on bad options), existing flags keep working | Done (this pass) |

## 4. Tier 2 - Redis source patches

| # | Patch | Status |
|---|---|---|
| T2-1 | Soft OOM for client buffers (`oom-soft-client-buffers yes`): when a large query/reply buffer commit is refused, close only that client and count it in `client_oom_disconnections`; dataset allocations stay fatal. Test hook `DEBUG SET-ALLOC-FAIL-THRESHOLD <bytes>`. | Done (patch 0007; default `yes` on MSYS2/Cygwin builds). Fault-injection path tested by `unit/win-soft-oom` and `ci/smoke.sh`; still needs a Windows measurement under real commit pressure. |
| T2-2 | Cap reply-list node size (~256 KB, config `reply-node-max-bytes`, on by default only on Cygwin/MSYS2) so a large reply never needs one contiguous block | Done (patch 0006; write batching unchanged). Windows bench job compares it against an unpatched build. |
| T2-3 | Zero-copy replies for large hash field values | Deferred |
| T2-4 | Tune the runtime's dlmalloc (`M_MMAP_THRESHOLD`, `M_TRIM_THRESHOLD`) instead of replacing it | Deferred - needs Windows measurement |
| T2-5 | Allocator policy: no jemalloc, no raw VirtualAlloc/HeapAlloc allocators (memory they hand out is not copied into a Cygwin fork child) | Adopted as policy |
| T2-6 | Socketpair notifiers instead of pipes (removes the runtime's polling pipe thread from select()) | Deferred - needs Windows measurement |
| T2-7 | Profile-guided optimization | Deferred |

## 5. Tier 3 - big bets

| Bet | Status |
|---|---|
| Forkless snapshots (Valkey 9.2 has them) | Deferred - only if persistence becomes a hard requirement; `save ""` removes forks for a cache |
| Porting forkless snapshots into Redis 8.10 | Rejected for now (large, high corruption risk) |
| Copy-on-write snapshot emulation under Cygwin | Deferred (unprototyped; a merge bug would corrupt live data) |
| Native Winsock event backend (WSAPoll / AFD) | Deferred - needs T2-6 first and only matters with many pooled connections |
| Full MSVC port | Rejected |
| Rewriting components in Rust | Rejected for this fork - every defect found was in configuration, the wrapper's supervision logic, or allocation policy on the Cygwin runtime, which a language change does not fix; a Rust component inside redis-server would be a permanent divergence from upstream, and the wrapper is a small, now-tested C# program. The existing code is fixed and extended in place. |

## 6. CI and releases

| Item | Status |
|---|---|
| Keep both MSYS2 and Cygwin builds | Done (this pass) |
| Scheduled upstream build publishes new Redis tags as **prereleases only**, never "latest" | Done (this pass) |
| Build inputs: optimization level (default `-O2`), TLS on by default with a no-TLS option | Done (this pass) |
| Verification: smoke gate (PING, SET/GET, 2 MB HSET/HMGET, BGSAVE + DEBUG RELOAD with digest equality, SHUTDOWN SAVE, restart), Tcl test subset with a skip list, benchmarks (baseline vs fork, same job), wrapper integration test (`ci/wrapper-integration.ps1`) | Done (this pass); results only from Windows CI |
| Soak test (6-24 h, production-shaped load) | Deferred - hosted runners stop after 6 h; needs a self-hosted Windows Server runner or a staging VM |
| SHA256 in release notes; build provenance attestations; code signing | Hashes: Done. Attestations/signing: Deferred |
| Private hosted runners are 2 vCPU / 8 GB: size fork-scaling tests accordingly (no 1 GB BGSAVE tests on them) | Constraint noted |

## 7. Alternatives worth knowing

- **Garnet** (Microsoft, .NET): no fork, no Cygwin allocator, IOCP
  networking, runs as a Windows service. Differences: eviction is not LRU,
  persistence is checkpoints not RDB, default log memory size must be reduced,
  RESP3 and health-check behaviour must be tested with your client. Worth a
  staging bake-off.
- **Valkey 9.2**: forkless snapshots; whether it builds under MSYS2 is
  untested.
- **Memurai**: commercial Windows port.
- Not viable today: unmaintained Windows ports of Redis 5, Dragonfly (Linux
  only), KeyDB (no Windows build).

## 8. Open questions that need a Windows host

- Does Redis 8.10.x at -O2 start and pass the test subset on both toolchains?
- Actual CPU/latency gain of -O2 on Windows.
- The fork's INFO memory fields vs PerfMon on a real host.
- Fork (BGSAVE) cost per GB, and whether a Defender process exclusion reduces
  it.
- Exact exit code Windows reports for an out-of-memory abort (expected 0x600,
  possibly with 0x8000).
- Shutdown budget (`WaitToKillServiceTimeout`) and preshutdown behaviour on
  Windows Server 2022.
- Whether the Tcl test suite runs fully under MSYS2/Cygwin.
