# Operations guide

How to install, upgrade, monitor and investigate this fork of Redis for Windows
in production. Setting up a server for the first time? The
[admin guide](ADMIN-GUIDE.md) walks through it in order; this guide is the
reference behind it. It assumes the MSYS2 package with the service wrapper
(`...-msys2-with-Service.zip`). The Cygwin package works the same way.

Contents

1. [Recommended layout](#1-recommended-layout)
2. [First install](#2-first-install)
3. [Upgrade and rollback](#3-upgrade-and-rollback)
4. [Security-release policy](#4-security-release-policy)
5. [Monitoring and alerting](#5-monitoring-and-alerting)
6. [Log rotation](#6-log-rotation)
7. [Incident: host evidence checklist](#7-incident-host-evidence-checklist)
8. [Hosts still on the stock wrapper](#8-hosts-still-on-the-stock-wrapper)
9. [Reference: exit codes and common errors](#9-reference-exit-codes-and-common-errors)

---

## 1. Recommended layout

```
C:\Redis\
  releases\
    8.10.2-fork.2\      current package, unzipped as-is (treat as read-only)
    8.10.2-fork.1\      previous package (N-1), kept for rollback
  conf\redis.conf       the config (from conf/redis.windows-cache.conf)
  data\                 dir: RDB files, if any
  logs\redis.log        logfile
  tools\                redis-watchdog.ps1, collect-host-evidence.ps1
```

Rules that avoid known path failures of the Cygwin/MSYS2 runtime:

- The MSYS2 runtime treats the folder **two levels above** the folder that
  holds `msys-2.0.dll` as its root `/`, and maps `/bin` to `<root>\usr\bin`
  (Cygwin: one level above). With the layout above the root is `C:\Redis`, so
  never create `C:\Redis\bin`, `C:\Redis\usr` or `C:\Redis\etc\fstab`, and
  never unzip a package into a folder named `bin` or `usr` (for example
  `C:\bin\Redis-...`): Redis then cannot find its own config
  ("can't open config file /bin/...").
- Use absolute Windows paths with forward slashes in `redis.conf`
  (`dir "C:/Redis/data"`, `logfile "C:/Redis/logs/redis.log"`). They bypass the
  runtime's mount table entirely.
- Keep config, data and logs **outside** the versioned release folders, so an
  upgrade or rollback only swaps binaries.

## 2. First install

1. **Verify the download.** Compare the SHA256 of the zip with the hash in the
   release notes:
   ```powershell
   Get-FileHash .\Redis-8.10.2-fork.2-Windows-x64-msys2-with-Service.zip -Algorithm SHA256
   ```
   If the release carries a build provenance attestation, also run
   `gh attestation verify <zip> --repo <owner>/<repo>`.
2. **Unzip** into `C:\Redis\releases\<version>\` and clear the
   "downloaded from the internet" marker:
   ```powershell
   Get-ChildItem C:\Redis\releases\8.10.2-fork.2 -Recurse | Unblock-File
   ```
3. **Config.** Copy `conf/redis.windows-cache.conf` to `C:\Redis\conf\redis.conf`,
   set `requirepass`, `maxmemory`, and check `dir` / `logfile`. Create the
   `data` and `logs` folders. If `dir` already holds a `dump.rdb` from an
   earlier config with save points, stop the service and delete or rename it
   (and any `temp-*.rdb`) now: Redis loads an existing RDB at every startup
   whatever `save` says, and with `save ""` that snapshot is never rewritten,
   so it would come back, frozen and stale, on every restart:
   ```powershell
   Get-ChildItem C:\Redis\data -Filter *.rdb |
       Rename-Item -NewName { '{0}.{1:yyyyMMdd-HHmmss}.old' -f $_.Name, (Get-Date) }
   ```
4. **Protect the config file** - it holds the password. Allow only
   Administrators and SYSTEM:
   ```powershell
   icacls C:\Redis\conf\redis.conf /inheritance:r /grant:r "*S-1-5-32-544:(F)" "*S-1-5-18:(F)"
   ```
   With `--virtual-account` there is nothing more to do here: the account
   `NT SERVICE\<name>` exists only once the service is created, and the
   installer then grants it Read on the config file itself. After step 5,
   `icacls C:\Redis\conf\redis.conf` shows the grant.
5. **Install the service** from the release folder:
   ```powershell
   C:\Redis\releases\8.10.2-fork.2\RedisService.exe install --service-name Redis `
       -c C:\Redis\conf\redis.conf --dir C:\Redis\data
   ```
   Useful options (see `RedisService.exe --help` for the full list):

   | Option | Meaning |
   |---|---|
   | `--virtual-account` | Run as `NT SERVICE\<name>` instead of LocalSystem and grant it access to the data, log and config folders. Opt-in; LocalSystem stays the default for compatibility. |
   | `--delayed-start` | Delayed automatic start (requires `--start-mode auto`). |
   | `--restart-policy on-crash\|always\|never` | `on-crash` (default): restart redis-server after a crash; a clean exit the wrapper did not ask for (for example a client `SHUTDOWN`) stops the service. |
   | `--max-restarts N` / `--restart-window T` | Crash-loop cap (default 5 in 10m). When exceeded the service stops with exit code 1067 so the SCM recovery actions take over. |
   | `--health-interval T` / `--health-failures N` | Liveness PING (default every 5s; 12 misses = restart). |
   | `--stop-timeout T` | Graceful shutdown budget (default 120s). |
   | `--auth-password-file FILE` | Password for the wrapper's own connections, if it must differ from `requirepass`. |

   The options are stored under
   `HKLM\SYSTEM\CurrentControlSet\Services\<name>\Parameters` (value
   `Arguments`); the service's ImagePath is only `"...\RedisService.exe" run --service-name <name>`.
6. **Verify:**
   ```powershell
   sc.exe qc Redis; sc.exe qfailure Redis; sc.exe qfailureflag Redis
   # this fork's wrapper logs under the service name as event source (here: Redis)
   Get-WinEvent -ProviderName Redis -MaxEvents 20 | Format-Table TimeCreated, Id, LevelDisplayName, Message -Wrap
   $env:REDISCLI_AUTH = Read-Host 'Redis password'     # never pass -a on the command line
   C:\Redis\releases\8.10.2-fork.2\redis-cli.exe -p 6379 PING
   C:\Redis\releases\8.10.2-fork.2\redis-cli.exe -p 6379 INFO memory
   Remove-Item Env:\REDISCLI_AUTH
   ```
   `PING` must print `PONG`; `INFO memory` must show `process_private_bytes`.

## 3. Upgrade and rollback

Every version lives in its own folder. An upgrade is "reinstall the service
from the new folder with the same options"; a rollback is the same step
pointing at the previous folder. Always reinstall (rather than editing
ImagePath by hand) when the binary folder, account or options change: the
installer also sets the failure actions (restart after 5 s / 30 s / 60 s, reset
after one day, also on non-crash failures), registers the event log source and,
with `--virtual-account`, grants the folder ACLs.

**Upgrade**

1. Verify and unzip the new package as in section 2, steps 1-2.
2. Optional pre-flight: `...\<new>\redis-server.exe --version` and
   `...\<new>\RedisService.exe --version`.
3. If the config keeps a `save` point and a warm start matters, nothing extra
   is needed: this fork's wrapper shuts Redis down with an authenticated
   `SHUTDOWN`, which saves. (On the stock wrapper, run an authenticated `SAVE`
   first - it hard-kills Redis after 5 s without saving.)
4. In a maintenance window (expect a few seconds to about 10 s without Redis;
   a pure cache on `save ""` comes back empty, provided no `dump.rdb` is left
   in `dir` - if this upgrade also switches the config from save points to
   `save ""`, delete or rename the old `dump.rdb` between the two commands, as
   in section 2, step 3):
   ```powershell
   C:\Redis\releases\8.10.2-fork.1\RedisService.exe uninstall --service-name Redis
   C:\Redis\releases\8.10.2-fork.2\RedisService.exe install --service-name Redis `
       -c C:\Redis\conf\redis.conf --dir C:\Redis\data    # plus the same options as before
   ```
5. Verify as in section 2, step 6.
6. Keep the previous folder (N-1). Delete N-2 and older.

**Rollback:** the same two commands with the folders swapped (uninstall with
the new exe, install with the previous exe and the same options).

**First upgrade from the stock wrapper:** uninstall with the stock
`RedisService.exe uninstall --service-name Redis`, install from the new
folder, then remove the interim watchdog task (section 8) and any
`sc failure` settings you added by hand - the new installer sets its own.
Stock installs resolve a relative `-c` against the installer's current
directory, so always pass absolute paths.

## 4. Security-release policy

Redis marks each release with an "Upgrade urgency" (LOW, MODERATE, HIGH,
CRITICAL, SECURITY). Targets for this fork:

| Upstream urgency | Patch series rebased and a CI-green prerelease built | In production |
|---|---|---|
| SECURITY or CRITICAL | within **3 working days** | within **7 calendar days**, sooner if the fix is reachable in our configuration |
| HIGH | within 10 working days | within 30 days |
| MODERATE / LOW | next planned maintenance | next planned maintenance |

Process:

1. The scheduled upstream build picks up every new Redis tag and builds it,
   with the patch series, as a **prerelease only** - it never publishes
   "latest". A failing patch application fails that build loudly; that is the
   signal to rebase. Also watch the redis/redis releases (GitHub: Watch ->
   Custom -> Releases).
2. Rebase: extract the new tag, run `scripts/apply-patches.sh <src>`, fix any
   rejected hunk, regenerate the patches with `git format-patch`, update
   `patches/redis/README.md`.
3. Run the verification workflow (smoke gate, Tcl test subset, benchmarks,
   wrapper integration). All must pass.
4. Publish the prerelease; run it on a staging host for at least 24 hours
   under a production-like load (long soak tests cannot run on hosted CI
   runners - they stop after 6 hours).
5. Promote to a release and roll out with section 3, keeping N-1.
6. Record the exposure assessment in the change ticket (which fixed issues are
   reachable with our config: Lua, modules, cluster bus, ACL, network
   exposure). A low assessment never skips a SECURITY release without an
   explicit, recorded decision.

The same applies to the bundled runtime pieces: OpenSSL advisories matter for
the TLS build (the no-TLS build does not ship OpenSSL), and the MSYS2/Cygwin
runtime version is recorded in each build's `build-info` file - review it
when it changes.

## 5. Monitoring and alerting

**The first alert to wire up is the client's own Redis health check.** An
application that probes Redis (for example an ASP.NET Core
`AddHealthChecks().AddRedis(...)` endpoint that reports `Degraded`) already
knows within seconds when Redis is gone. Alert when it reports unhealthy or
degraded for more than 1-2 minutes. Without that, an outage can go unnoticed
for as long as the application silently falls back to the database.

**Memory: measure Private Bytes, not Redis's RSS.** On stock Cygwin/MSYS2
builds `used_memory_rss` is fake: it is `used_memory` sampled by the cron, so
`mem_fragmentation_ratio` is always 1.00 and "Fork CoW" is always 0. Never
alert on them. Use:

| Signal | Where | Alert |
|---|---|---|
| Host commit in use | PerfMon `\Memory\% Committed Bytes In Use`, or INFO `system_commit_total / system_commit_limit` (fork only) | warn > 85 %, critical > 95 % |
| Redis private commit | PerfMon `\Process(redis-server)\Private Bytes`, or INFO `process_private_bytes` (fork only) | investigate > 2 x `maxmemory` |
| Redis working set | INFO `process_working_set` (fork only) | informational |
| Soft-OOM disconnects | INFO stats `client_oom_disconnections` (fork only, patch 0007) | warn on any increase (Windows refused a commit and the server survived it by closing one client) |
| Wrapper lifecycle | Application log, source = the service name (this fork, e.g. `Redis`; the stock wrapper uses `RedisService`): crash (Error, with the Redis crash excerpt), restart, crash-loop give-up | alert on Error |
| Service stopped unexpectedly | System log, Service Control Manager 7031 / 7034 | alert |
| Stock-wrapper hosts | Application log, source `RedisWatchdog` 3001 / 3002 | alert |
| Latency spikes | `LATENCY LATEST` (needs `latency-monitor-threshold`, 100 ms in the cache config) | review after incidents |

The four memory INFO fields exist only in this fork's Cygwin/MSYS2 builds
(memory section, patch 0003). `client_oom_disconnections` (stats section, patch
0007) exists in every fork build; on non-Windows builds it stays 0 unless
`oom-soft-client-buffers` is turned on.

**Large values.** Under commit pressure the allocations that fail first are
large, fresh ones: a big write needs a query buffer the size of the value, and
on stock builds a big read needs a reply buffer of the same size. Fork builds
split replies into blocks of at most 256 KB (`reply-node-max-bytes`) and close
only the affected client if a buffer still cannot be allocated
(`oom-soft-client-buffers`). To see which keys are large, use
`redis-cli --bigkeys` or `MEMORY USAGE <key>` (authenticated, off-peak).

## 6. Log rotation

Redis opens the log file, appends one line and closes it again for every log
line, so external rotation needs no signal or restart: rename or truncate the
file and the next line creates a new one. Example daily task (run as SYSTEM):

```powershell
$log = 'C:\Redis\logs\redis.log'
if ((Test-Path $log) -and (Get-Item $log).Length -gt 50MB) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    Move-Item $log "$log.$stamp"          # retry once on a sharing violation
    Get-ChildItem "$log.*" | Sort-Object LastWriteTime -Descending | Select-Object -Skip 14 | Remove-Item
}
```

Keep enough history to cover a crash report: the crash dump is written into
this file.

## 7. Incident: host evidence checklist

Everything in this section is read-only unless it says otherwise. Start with
the collector script, which gathers most of it into one zip:

```powershell
# elevated PowerShell; -From/-To bracket the incident
C:\Redis\tools\collect-host-evidence.ps1 -From '2026-01-15 10:30' -To '2026-01-15 10:45' -ServiceName Redis
```

Add `-IncludeRedisLogTail` to include the end of the Redis log (off by
default: crash reports can contain key names). Review the zip before sharing
it outside your organisation.

### 7.1 Did the host run out of commit?

"Out Of Memory allocating N bytes" from a Cygwin/MSYS2 Redis means Windows
refused to commit memory for that request, after the runtime's allocator tried
all its fallbacks. It says nothing about `maxmemory`. Check, in order:

1. **Event ID 2004**, System log, source
   `Microsoft-Windows-Resource-Exhaustion-Detector` ("low virtual memory
   condition"). Its text lists the processes that consumed the most commit at
   that moment:
   ```powershell
   Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Resource-Exhaustion-Detector'; Id = 2004;
       StartTime = '2026-01-15 10:30'; EndTime = '2026-01-15 10:45' } | Format-List TimeCreated, Message
   ```
   Also look at Application Popup **Event 26** and any
   `Resource-Exhaustion-Resolver` events.
2. **Pagefile.** A small, fixed or disabled pagefile, or a full pagefile
   volume, lowers the commit limit:
   ```powershell
   Get-CimInstance Win32_PageFileUsage | Select-Object Name, AllocatedBaseSize, CurrentUsage, PeakUsage
   Get-CimInstance Win32_PageFileSetting | Select-Object Name, InitialSize, MaximumSize
   (Get-CimInstance Win32_ComputerSystem).AutomaticManagedPagefile
   Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | Select-Object DeviceID, @{n='FreeGB';e={[math]::Round($_.FreeSpace/1GB,1)}}
   ```
3. **Other processes on the host** (web worker processes, other services):
   their Private Bytes at the time. The collector's `processes-*.csv` files
   show current values and each process's peak private commit.

If Event 2004 names another process close to the limit, the host ran out of
commit: fix capacity (pagefile, memory, the other process) - no Redis change
can prevent a refused commit. If there is no such event and the pagefile is
large, suspect Redis's own growth and capture its memory map (7.3).

### 7.2 Keep a PerfMon data collector running

A circular binary log of commit and per-process memory, sampled every 15 s,
costs little and answers the question after the next incident:

```powershell
logman create counter RedisCommit -f bincirc -max 1024 -si 00:00:15 `
    -o "C:\PerfLogs\RedisCommit\RedisCommit" `
    -c "\Memory\Committed Bytes" "\Memory\Commit Limit" "\Memory\% Committed Bytes In Use" `
       "\Memory\Available MBytes" "\Paging File(_Total)\% Usage" `
       "\Process(*)\Private Bytes" "\Process(*)\Working Set"
logman start RedisCommit
logman query RedisCommit
```

Data collectors do not restart after a reboot by themselves; add a startup
task:

```powershell
schtasks /Create /TN "RedisCommitCollector" /SC ONSTART /RU SYSTEM /RL HIGHEST /TR "logman start RedisCommit"
```

To read a time window, convert the `.blg` file from the output folder:

```powershell
relog "C:\PerfLogs\RedisCommit\<file>.blg" -f csv -o C:\PerfLogs\window.csv -b "01/15/2026 10:30:00" -e "01/15/2026 10:45:00"
```

Counter names are localized: on a non-English Windows use the local names (or
the collector script's WMI-based `commit-wmi.txt`).

### 7.3 Redis's own memory map

- **VMMap** (Sysinternals): open redis-server interactively, or save a
  snapshot with `vmmap64.exe -accepteula -p <PID> C:\Evidence\redis.mmp`.
  Compare private committed memory with `used_memory`.
- From an MSYS2 shell: `ps -W | grep redis-server` for the Cygwin PID, then
  `cat /proc/<pid>/maps`.
- Process Explorer -> redis-server -> Job tab: confirms whether a job object
  with a memory limit applies (only this fork's wrapper creates a job, and it
  sets no memory limit).

### 7.4 Service configuration and shutdown budget

```powershell
sc.exe qc Redis            # ImagePath, account
sc.exe qfailure Redis      # recovery actions
sc.exe qfailureflag Redis  # 1 = also recover when the service stops with an error code (needed for exit 1067)
sc.exe qsidtype Redis
reg query "HKLM\SYSTEM\CurrentControlSet\Control" /v WaitToKillServiceTimeout
```

`WaitToKillServiceTimeout` bounds how long services get at system shutdown.
With `save ""` nothing needs saving, so a short budget is harmless; with a save
point, a large dataset may not finish its final save before a reboot kills it.

### 7.5 Microsoft Defender

Status (read-only):

```powershell
Get-MpComputerStatus | Select-Object AMServiceEnabled, AntivirusEnabled, RealTimeProtectionEnabled, IsTamperProtected, AMProductVersion
Get-MpPreference | Select-Object ExclusionPath, ExclusionProcess   # needs elevation to show the lists
```

Every Cygwin fork (BGSAVE) starts a new process that reloads the runtime DLLs,
which real-time scanning can slow down. This only matters if you keep a `save`
point. **Controlled exclusion test** (this one CHANGES Defender settings: get
approval from whoever owns endpoint security, run it off-peak, and undo it):

```powershell
$cli = 'C:\Redis\releases\8.10.2-fork.2\redis-cli.exe'
$env:REDISCLI_AUTH = Read-Host 'Redis password'
function Measure-Forks([int]$n = 20) {
    1..$n | ForEach-Object {
        & $cli -p 6379 BGSAVE | Out-Null
        do { Start-Sleep -Milliseconds 500 } while ((& $cli -p 6379 INFO persistence) -match 'rdb_bgsave_in_progress:1')
        [int](((& $cli -p 6379 INFO stats) -match '^latest_fork_usec:') -replace '\D', '')
    } | Measure-Object -Average -Maximum
}
$before = Measure-Forks
Add-MpPreference -ExclusionProcess 'C:\Redis\releases\8.10.2-fork.2\redis-server.exe'
$after = Measure-Forks
Remove-MpPreference -ExclusionProcess 'C:\Redis\releases\8.10.2-fork.2\redis-server.exe'
Remove-Item Env:\REDISCLI_AUTH
$before, $after
```

**Clean up afterwards.** The 40 BGSAVEs leave a `dump.rdb` in `dir`. With
`save ""` nothing rewrites it, and Redis loads it at every startup, so the
next restart would bring back this test's snapshot, frozen. Stop the service,
delete or rename `<dir>\dump.rdb` (section 2, step 3), then start it again.

Each BGSAVE temporarily doubles Redis's commit (Cygwin fork copies the whole
heap). Keep a permanent exclusion only if the difference is material and
security approves it; prefer the process exclusion over a folder exclusion.

### 7.6 Wrapper and Redis logs

- Application log, source = the service name (this fork, e.g. `Redis`) or
  `RedisService` (stock wrapper): this fork logs one Error event per crash
  with the decoded exit status and the Redis crash excerpt.
- Redis log (`logfile`): look for `Out Of Memory allocating`, followed on
  this fork by an `Out Of Memory diagnostics: system_commit_total=...
  system_commit_limit=...` line with the host's commit state at that moment,
  then `REDIS BUG REPORT START`; and look for long gaps after
  `Background saving started` (a hung fork).

## 8. Hosts still on the stock wrapper

The stock `RedisService.exe` never restarts a dead redis-server and keeps
reporting Running, so SCM recovery never fires. Until the host is upgraded:

### 8.1 Interim watchdog

`scripts/redis-watchdog.ps1` probes Redis once per run with an authenticated
`redis-cli PING` (password via the `REDISCLI_AUTH` environment variable of the
child process, never `-a`), treats anything other than the exact reply `PONG`
as a failure (redis-cli exits 0 even on `NOAUTH`), and after N consecutive
failures runs `Restart-Service`. It logs state changes to the Application
log under source `RedisWatchdog` (IDs in the script's help:
`Get-Help .\redis-watchdog.ps1 -Full`).

1. Copy the script to `C:\Redis\tools\` and protect the folder
   (Administrators and SYSTEM only - the task runs the script as SYSTEM).
2. Test the settings:
   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Redis\tools\redis-watchdog.ps1 -ServiceName Redis -ProbeOnly
   ```
   It reads the port and `requirepass` from the config named in the service
   command line; override with `-ConfigPath`, `-Port`, `-RedisCliPath`,
   `-PasswordFile`.
3. Register the task (every minute, as SYSTEM, one instance at a time, killed
   if a run exceeds 5 minutes):
   ```powershell
   $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
       -Argument '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "C:\Redis\tools\redis-watchdog.ps1" -ServiceName Redis -FailureThreshold 3'
   $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes 1)
   $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 5) `
       -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
   $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
   Register-ScheduledTask -TaskName 'RedisWatchdog' -Action $action -Trigger $trigger -Settings $settings -Principal $principal
   ```
   (Leaving out `-RepetitionDuration` repeats indefinitely on Windows Server
   2016 and later.) The same with `schtasks`:
   ```cmd
   schtasks /Create /TN "RedisWatchdog" /SC MINUTE /MO 1 /RU SYSTEM /RL HIGHEST /F ^
     /TR "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\Redis\tools\redis-watchdog.ps1 -ServiceName Redis"
   ```
   (`/TR` is limited to 261 characters; `schtasks` cannot set the execution
   time limit or the instance policy - use the PowerShell form where possible.)
4. A run's result is 0 when healthy and 1 while failing; the Application log
   has the details. Remove the task after upgrading to this fork's wrapper:
   `Unregister-ScheduledTask -TaskName RedisWatchdog -Confirm:$false`.

With `-FailureThreshold 3` and a one-minute interval, a dead or hung Redis is
restarted after about 3 minutes; `-GraceMinutes` (default 5) prevents a second
restart while a large dataset is still loading.

### 8.2 Service recovery actions

Configure them anyway; they cover the wrapper process itself crashing (not a
redis-server crash under the stock wrapper):

```powershell
sc.exe failure Redis reset= 86400 actions= restart/5000/restart/30000/restart/60000
sc.exe failureflag Redis 1
```

This fork's installer sets the same values itself.

### 8.3 Planned stops

The stock wrapper's stop cannot authenticate and hard-kills Redis after 5 s.
With a `save` point configured, run an authenticated `SAVE` before a planned
stop or reboot.

## 9. Reference: exit codes and common errors

**redis-server exit status as seen by Windows** (a Cygwin/MSYS2 process
started by a non-Cygwin parent such as the wrapper):

| Windows exit code | Meaning |
|---|---|
| 0 | clean exit (`SHUTDOWN`) |
| 1-255 | `exit(n)`, e.g. 1 = config error or cannot bind |
| multiple of 256 | killed by signal `code / 256` - e.g. 1536 (0x600) = SIGABRT (6), which is how an out-of-memory panic ends; 2816 (0xB00) = SIGSEGV (11). A core flag may add 0x8000. |

**Wrapper exit code 1067** (ERROR_PROCESS_ABORTED): the crash-loop cap was
reached (or the restart policy is `never` and Redis died); SCM recovery actions
take over if `sc qfailureflag` is 1.

| Message | Cause | Fix |
|---|---|---|
| `Unresolved Configuration(s) Detected: >>> 'oom-soft-client-buffers yes'` | The line was uncommented on a build without patch 0007 (a stock build) | Use this fork's binaries, or comment the line out again (fork builds default to `yes` anyway) |
| Old keys reappear after a restart on `save ""` | A leftover `dump.rdb` in `dir` is loaded at every startup and never rewritten | Stop the service, delete or rename `<dir>\dump.rdb`, start it (section 2, step 3) |
| `can't open config file '/bin/...'`, `server root dir /bin/...` | Package unzipped under a folder named `bin` (runtime root mapping) | Move it, see section 1 |
| `Out Of Memory allocating N bytes!` | Windows refused a commit | Section 7.1 |
| `ERR max number of clients reached` | `maxclients` reached (the fork clamps it below FD_SETSIZE on select builds) | Look for a connection leak |
| `NOAUTH Authentication required.` from a tool | The tool did not send the password | Set `REDISCLI_AUTH` for redis-cli |
