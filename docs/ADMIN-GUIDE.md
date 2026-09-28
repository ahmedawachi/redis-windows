# Admin guide: from download to production

One path, start to finish, for setting up this fork of Redis for Windows on a
server: download, configure, install as a service, connect applications,
monitor, upgrade and recover. Every step has the exact command. Work through
the sections in order and tick the [sign-off checklist](#13-sign-off-checklist)
at the end.

For the reasoning behind a step, or deeper detail, follow the links to the
[operations guide](OPERATIONS.md).

Contents

1. [Before you start](#1-before-you-start)
2. [Get the release](#2-get-the-release)
3. [Create the folders and the config](#3-create-the-folders-and-the-config)
4. [Replacing an existing Redis for Windows install](#4-replacing-an-existing-redis-for-windows-install)
5. [Install the service](#5-install-the-service)
6. [Verify](#6-verify)
7. [Connect your applications](#7-connect-your-applications)
8. [Network and security](#8-network-and-security)
9. [Monitoring, alerting and logs](#9-monitoring-alerting-and-logs)
10. [Upgrades, rollback and security releases](#10-upgrades-rollback-and-security-releases)
11. [Troubleshooting](#11-troubleshooting)
12. [Uninstall](#12-uninstall)
13. [Sign-off checklist](#13-sign-off-checklist)

---

## 1. Before you start

**What you install.** One zip holds everything:

| File | What it is |
|---|---|
| `redis-server.exe`, `redis-cli.exe`, … | Redis, built from the official source for Windows |
| `RedisService.exe` | the Windows service that runs Redis, restarts it after a crash and stops it cleanly. Self-contained: **no .NET install is needed** |
| `msys-*.dll` | the runtime Redis runs on (shipped in the zip) |
| `redis.windows-cache.conf` | the recommended configuration for cache workloads |
| `install-service.bat`, `uninstall-service.bat`, `start.bat` | one-click helpers for trying it out and for small installs |

**Requirements**

- Windows 10/11 or Windows Server 2016 or later, x64.
- An administrator account for the install (not needed to just try it).
- Disk: about 100 MB for two release folders, plus log space. With snapshots
  enabled, room for one `dump.rdb` of about the dataset size.
- **Memory and page file.** Redis on Windows fails an allocation when the
  *whole host* runs out of commit (RAM + page file), not only when Redis
  reaches `maxmemory`. Keep the page file system-managed, or large, on a
  volume with free space. Size `maxmemory` so that the host stays below about
  85 % committed at its busiest.

**Decide two things now**

1. **Cache only, or data that must survive a restart?** For a cache (the
   usual case, for example ASP.NET Core `IDistributedCache`), keep the
   shipped `save ""`: no snapshots and no background forks, and after a
   restart the cache refills itself. If data must survive restarts, use
   `save 3600 1` instead and read [section 10](#10-upgrades-rollback-and-security-releases) about stopping cleanly.
2. **Who connects?** Applications on the same server (the default: Redis
   listens only on `127.0.0.1`), or other servers too ([section 8](#8-network-and-security)).

> [!TIP]
> Want to try it before any of this? Unzip the release anywhere, double-click
> `RedisService.exe`, then `redis-cli.exe` and type `PING`. Close the window
> to stop it. Nothing is installed.

## 2. Get the release

1. Download from [Releases](https://github.com/ahmedawachi/redis-windows/releases)
   the file `Redis-<version>-fork.<n>-Windows-x64-msys2-with-Service.zip`,
   for example `Redis-8.10.2-fork.1-Windows-x64-msys2-with-Service.zip`.
   The MSYS2 build is the recommended one; the Cygwin build works the same way.
2. Check its SHA256 against the hash listed in the release notes:
   ```powershell
   Get-FileHash .\Redis-8.10.2-fork.1-Windows-x64-msys2-with-Service.zip -Algorithm SHA256
   ```
3. Unzip it into its own versioned folder and clear the "downloaded from the
   internet" marker, so SmartScreen does not stop the programs:
   ```powershell
   $release = 'C:\Redis\releases\8.10.2-fork.1'
   Expand-Archive .\Redis-8.10.2-fork.1-Windows-x64-msys2-with-Service.zip -DestinationPath C:\Redis\releases\
   Move-Item C:\Redis\releases\Redis-8.10.2-fork.1-Windows-x64-msys2-with-Service $release
   Get-ChildItem $release -Recurse | Unblock-File
   & "$release\redis-server.exe" --version
   ```
   The later steps use `$release`; in a new PowerShell window, set it again
   first.

> [!IMPORTANT]
> Never unzip into, or create, a folder named `bin` or `usr` on the path (for
> example `C:\bin\Redis` or `C:\Redis\bin`). The runtime maps those names to
> its own folders and Redis then cannot find its config.
> [Why](OPERATIONS.md#1-recommended-layout).

## 3. Create the folders and the config

Target layout (config, data and logs live outside the release folders, so an
upgrade only swaps binaries):

```
C:\Redis\
  releases\8.10.2-fork.1\   the unzipped release (treat as read-only)
  conf\redis.conf           your config
  data\                     dump.rdb, if snapshots are enabled
  logs\redis.log            the Redis log
  tools\                    optional: the scripts from the repository's scripts\ folder
```

1. Create the folders and copy the recommended config:
   ```powershell
   New-Item -ItemType Directory -Force C:\Redis\conf, C:\Redis\data, C:\Redis\logs | Out-Null
   Copy-Item "$release\redis.windows-cache.conf" C:\Redis\conf\redis.conf
   ```
2. Generate a strong password (hex, so it is safe in any connection string):
   ```powershell
   $bytes = New-Object byte[] 32
   [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
   -join ($bytes | ForEach-Object { $_.ToString('x2') })
   ```
   Store it in your password vault now; the applications need it too.
3. Edit `C:\Redis\conf\redis.conf`. The lines to look at:

   | Directive | Shipped value | Set it to |
   |---|---|---|
   | `requirepass` | `REPLACE_WITH_A_LONG_RANDOM_SECRET` | the password from step 2 (**required**) |
   | `maxmemory` | `256mb` | what the cache may use, for example `1gb`. Leave host headroom (section 1) |
   | `maxmemory-policy` | `allkeys-lru` | keep for a cache |
   | `save` | `""` | keep for a cache; `3600 1` if data must survive restarts |
   | `port` | `6379` | change only if another Redis already uses 6379 |
   | `bind` | `127.0.0.1` | keep, unless other servers connect (section 8) |
   | `dir`, `logfile` | `C:/Redis/data`, `C:/Redis/logs/redis.log` | keep; use absolute paths with forward slashes |
   | `maxclients` | `895` | keep; this build caps it there anyway |

4. Allow only Administrators and SYSTEM to read the config, since it holds the
   password:
   ```powershell
   icacls C:\Redis\conf\redis.conf /inheritance:r /grant:r "*S-1-5-32-544:(F)" "*S-1-5-18:(F)"
   ```

## 4. Replacing an existing Redis for Windows install

Skip this section on a clean server.

1. **Record what is there.** Note the service name, program path and
   config path, and keep a copy of the old config:
   ```powershell
   Get-CimInstance Win32_Service | Where-Object { $_.PathName -match 'redis' } |
       Select-Object Name, State, StartMode, StartName, PathName
   sc.exe qc Redis
   ```
   Copy anything you want to keep from the old config (password, port,
   `maxmemory`) into the new `C:\Redis\conf\redis.conf`.
2. **Plan the downtime.** For a cache it is a few seconds to a minute;
   applications with a Redis fallback keep working meanwhile.
3. **Stop and remove the old service** with the old install's own program
   (use the service name from step 1):
   ```powershell
   & 'C:\path\to\old\RedisService.exe' uninstall --service-name Redis
   # no RedisService.exe in the old install? then: sc.exe stop Redis; sc.exe delete Redis
   ```
4. **Old snapshot.** A cache on `save ""` must not start from an old
   `dump.rdb`: Redis loads it at every start and never rewrites it, so stale
   keys would come back after each restart. Rename it:
   ```powershell
   Get-ChildItem C:\Redis\data -Filter *.rdb -ErrorAction SilentlyContinue |
       Rename-Item -NewName { '{0}.{1:yyyyMMdd-HHmmss}.old' -f $_.Name, (Get-Date) }
   ```
   Keeping the data instead? Copy the old `dump.rdb` into `C:\Redis\data`
   and keep a `save` rule in the new config.
5. **Old binaries in `C:\Redis` itself?** If the old release was unzipped
   straight into `C:\Redis`, move it aside first (for example to
   `C:\Redis-old`) so only the new layout remains; it is also your way back.
6. **Remove old workarounds:** a scheduled watchdog task, or `sc failure`
   settings you added by hand. The new service sets its own recovery actions.
7. Continue with section 5.

## 5. Install the service

From an elevated PowerShell:

```powershell
& "$release\RedisService.exe" install --service-name Redis `
    -c C:\Redis\conf\redis.conf --dir C:\Redis\data
```

The service is created, set to start automatically with Windows and started.
It also configures Windows recovery actions (restart after 5 s, 30 s and
60 s) and registers its Event Log source.

Useful options (all are optional; `RedisService.exe --help` lists every one):

| Option | When to use it |
|---|---|
| `--virtual-account` | Run as `NT SERVICE\Redis` instead of LocalSystem. Recommended for new installs; the installer grants it access to the data, log and config folders |
| `--delayed-start` | Start a little after boot, when other services start first |
| `--port N` | Only if you did not set the port in `redis.conf` |
| `--stop-timeout 300s` | Large datasets with snapshots, which need longer to save on stop |

<details>
<summary><b>Small or test server: the one-click installer</b></summary>

Move the unzipped folder to its permanent place, then double-click
`install-service.bat`. It asks for administrator rights, installs the
`Redis` service with the `redis.conf` from that folder and a `data` folder
next to it, and starts it. `uninstall-service.bat` removes it again and keeps
the data. For production, prefer the layout and commands above: they keep
config and data out of the release folder, which makes upgrades a
two-command job.

</details>

## 6. Verify

```powershell
sc.exe query Redis                                       # STATE : 4  RUNNING
Get-WinEvent -ProviderName Redis -MaxEvents 10 |
    Format-Table TimeCreated, Id, LevelDisplayName, Message -Wrap   # start and ready events
$env:REDISCLI_AUTH = Read-Host 'Redis password'           # never pass the password with -a
& "$release\redis-cli.exe" PING                           # PONG
& "$release\redis-cli.exe" INFO server | Select-String 'redis_version|os:'
& "$release\redis-cli.exe" INFO memory | Select-String 'process_private_bytes|system_commit'
Remove-Item Env:\REDISCLI_AUTH
Get-Content C:\Redis\logs\redis.log -Tail 20             # "Ready to accept connections"
```

Then prove the self-healing once, in a maintenance window: kill Redis and
watch the service bring it back within seconds.

```powershell
Stop-Process -Name redis-server -Force
Start-Sleep 10; sc.exe query Redis                        # still RUNNING
Get-WinEvent -ProviderName Redis -MaxEvents 5 | Format-Table TimeCreated, Id, Message -Wrap   # crash + restart
```

Finally reboot once (or at the next planned reboot) and check that Redis is
running afterwards without anyone touching it.

## 7. Connect your applications

Connection string for StackExchange.Redis (used by ASP.NET Core
`AddStackExchangeRedisCache` and most .NET clients):

```
localhost:6379,password=<the password>,abortConnect=false
```

- `abortConnect=false` lets the application start, and keep running, while
  Redis restarts.
- Keep the password out of source control: use environment variables, user
  secrets or your secret store, never a committed `appsettings.json`.
- Other clients take the same three facts: host, port, password.

Test from the application host with the application's own health check, if it
has one, or with `redis-cli -h <host> PING` as in section 6.

## 8. Network and security

- **Default: this machine only.** `bind 127.0.0.1` and `protected-mode yes`
  keep Redis unreachable from the network. Keep it that way if the
  applications run on the same server.
- **Other servers must connect?**
  1. Set `bind` to the server's LAN address (and `127.0.0.1`), keep the
     password.
  2. Open the port only for the application servers:
     ```powershell
     New-NetFirewallRule -DisplayName 'Redis from app servers' -Direction Inbound -Protocol TCP `
         -LocalPort 6379 -RemoteAddress 10.0.0.21,10.0.0.22 -Action Allow
     ```
  3. Prefer TLS across the network: the standard build includes it
     (`tls-port`, `tls-cert-file`, `tls-key-file`, `tls-ca-cert-file` in
     `redis.conf`). The service speaks TLS to Redis on its own.
- **Never** expose Redis to the internet.
- **Antivirus.** Real-time scanning of the Redis folders slows every snapshot.
  If your policy allows, exclude `C:\Redis\data` and the `redis-server.exe`
  process, and measure before and after ([how](OPERATIONS.md#7-incident-host-evidence-checklist)).
- Keep `enable-debug-command no` (the shipped value).

## 9. Monitoring, alerting and logs

The minimum worth wiring up on day one:

| Watch | Where | Alert when |
|---|---|---|
| The application's own Redis health check | your monitoring | unhealthy for more than 1-2 minutes |
| Service crash and give-up events | Application log, source `Redis` | any Error |
| Service stopped unexpectedly | System log, Service Control Manager 7031 / 7034 | any |
| Host memory commit | PerfMon `\Memory\% Committed Bytes In Use` | above 85 % |
| Redis memory | PerfMon `\Process(redis-server)\Private Bytes`, or `INFO memory` → `process_private_bytes` | above 2 × `maxmemory` |
| Clients closed instead of crashing | `INFO stats` → `client_oom_disconnections` | any increase: the host is short of memory |

Do not alert on `used_memory_rss` or `mem_fragmentation_ratio` from stock
Windows builds; they are placeholders there. This build reports real values.

Two jobs to set up once:

- **Log rotation.** The Redis log never rotates itself; schedule the
  [daily rotation task](OPERATIONS.md#6-log-rotation).
- **A memory history.** A [PerfMon data collector](OPERATIONS.md#72-keep-a-perfmon-data-collector-running)
  answers "what used the memory?" after an incident.

## 10. Upgrades, rollback and security releases

**Upgrade** (a few seconds without Redis):

```powershell
$old = 'C:\Redis\releases\8.10.2-fork.1'
$new = 'C:\Redis\releases\8.10.2-fork.2'   # verified and unzipped as in section 2
& "$old\RedisService.exe" uninstall --service-name Redis
& "$new\RedisService.exe" install --service-name Redis -c C:\Redis\conf\redis.conf --dir C:\Redis\data   # same options as before
```

Then verify as in section 6. Keep the previous release folder for rollback
and delete older ones.

**Rollback:** the same two commands with the folders swapped.

**Stopping and restarting** is always safe: `Stop-Service Redis` sends Redis
an authenticated `SHUTDOWN`, which saves first when snapshots are enabled, and
waits for it (up to `--stop-timeout`, 120 s by default).

**Security releases.** Watch this repository's releases (Watch → Custom →
Releases). New Redis versions are built as pre-releases first; install a
SECURITY or CRITICAL release within a week. The full policy is in the
[operations guide](OPERATIONS.md#4-security-release-policy).

## 11. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Service does not start, error 1053 or exit 1 in the log | config error, or the port is in use | read the end of `C:\Redis\logs\redis.log`; `Get-NetTCPConnection -LocalPort 6379` shows who holds the port |
| Service stopped with exit code 1067 | Redis crashed 5 times in 10 minutes; Windows recovery restarts the service | read the crash event in the Application log (source `Redis`); it includes the Redis crash report |
| `Out Of Memory allocating N bytes!` in the log | Windows refused memory: the host ran out of commit | [check the host](OPERATIONS.md#71-did-the-host-run-out-of-commit): page file, other processes |
| `can't open config file '/bin/...'` | a folder named `bin` or `usr` on the path | move the release, see section 2 |
| Old keys come back after a restart | a leftover `dump.rdb` with `save ""` | section 4, step 4 |
| `NOAUTH Authentication required.` | the tool did not send the password | set `REDISCLI_AUTH` first (section 6) |
| `ERR max number of clients reached` | 895 connections in use | look for a client that leaks connections |
| Windows says "Windows protected your PC" | the zip was not unblocked | `Unblock-File` (section 2), or More info → Run anyway |
| Warning `maxclients has been reduced ... to 895` | expected on this build | nothing to do |

After an incident, collect the evidence in one go with
[`collect-host-evidence.ps1`](OPERATIONS.md#7-incident-host-evidence-checklist)
(read-only) before restarting anything.

## 12. Uninstall

```powershell
& "$release\RedisService.exe" uninstall --service-name Redis
```

This stops Redis cleanly and removes the service. The folders under `C:\Redis`
are left in place; delete them yourself once you no longer need the data or
logs.

## 13. Sign-off checklist

- [ ] Release zip hash verified, files unblocked (section 2)
- [ ] Release unzipped into `C:\Redis\releases\<version>`, no `bin`/`usr` on the path
- [ ] `requirepass` set to a generated secret, stored in the vault (section 3)
- [ ] `maxmemory` sized, page file checked (sections 1 and 3)
- [ ] `redis.conf` readable only by Administrators and SYSTEM (section 3)
- [ ] Any previous Redis service removed, old `dump.rdb` handled (section 4)
- [ ] Service installed and **running**, `PONG` with the password (sections 5-6)
- [ ] Crash test passed: killed `redis-server` came back on its own (section 6)
- [ ] Survives a reboot (section 6)
- [ ] Applications connect with the password from a secret store (section 7)
- [ ] Network exposure as intended; firewall rule only if needed (section 8)
- [ ] Alerts wired: health check, service events, host commit (section 9)
- [ ] Log rotation task scheduled (section 9)
- [ ] Upgrade and rollback steps written into your runbook (section 10)
