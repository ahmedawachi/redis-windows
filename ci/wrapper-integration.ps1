<#
.SYNOPSIS
  End-to-end test of RedisService.exe: the no-argument (double-click) run, then the Windows service.

.DESCRIPTION
  Runs on windows-latest with administrator rights. Installs the -with-Service package as uniquely named
  services and verifies:
    - install hardening: failure actions, non-crash failure flag, SID type, description, stored parameters,
      Event Log source, ImagePath without options;
    - readiness (PONG through requirepass and a --port override);
    - DEBUG SLEEP 10 does NOT restart; Stop-Process -Force on redis-server restarts it within 20 s with one
      Error event; DEBUG SEGFAULT produces a crash event carrying the Redis bug report;
    - DEBUG SLEEP 90 is detected by the health probe and restarted;
    - sc stop with requirepass set runs SHUTDOWN: "DB saved on disk", no temp-*.rdb even during a BGSAVE, and
      the key survives a restart; the BGSAVE fork child runs inside the job object;
    - repeated crashes make the wrapper give up with exit code 1067 and SCM's recovery action restarts it;
    - taskkill /F of the wrapper kills redis-server through the job object and frees the port within 5 s;
    - the Event Log holds lifecycle events only (no per-line Redis output), in English;
    - --virtual-account install from a path with spaces can write its data; icacls shows the grant;
    - (TLS packages) TLS-only Redis: PONG and a graceful sc stop over TLS; a server certificate the wrapper cannot
      verify (tls-ca-cert-file is the CLIENT CA) is a warning, not a hang, and Redis is not restarted;
    - (MSYS2) install from C:\bin\... with a relative data dir is refused; with an absolute --dir it works and
      dump.rdb lands in that folder, not in the remapped C:\usr\bin\... one.
  Every service is uninstalled in the finally block. Exit code 0 = all checks passed.

.PARAMETER PackageDir
  The unpacked <dist>-with-Service folder (RedisService.exe, redis-server.exe, redis-cli.exe, DLLs, redis.conf).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$PackageDir = (Resolve-Path -LiteralPath $PackageDir).Path
$suffix = -join ((48..57) + (97..122) | Get-Random -Count 6 | ForEach-Object { [char]$_ })
$svc = "RedisCI$suffix"
$svcVirtual = "RedisCIva$suffix"
$svcTrap = "RedisCIbin$suffix"
$svcTls = "RedisCItls$suffix"
$password = 'ci pass with space'
$port = Get-Random -Minimum 20000 -Maximum 40000
$portVirtual = $port + 7
$portTls = $port + 11
$portBin = $port + 13
$work = Join-Path ([System.IO.Path]::GetTempPath()) "redis-wrapper-it-$suffix"
$vaData = Join-Path $env:ProgramData "Redis CI $suffix\data dir"
$cli = Join-Path $PackageDir 'redis-cli.exe'
$exe = Join-Path $PackageDir 'RedisService.exe'
$isMsys2 = Test-Path -LiteralPath (Join-Path $PackageDir 'msys-2.0.dll')
$t0 = Get-Date
$failures = [System.Collections.Generic.List[string]]::new()
$createdServices = [System.Collections.Generic.List[string]]::new()
$cleanupDirs = [System.Collections.Generic.List[string]]::new()

function Write-Step([string]$text) { Write-Host "`n=== $text" -ForegroundColor Cyan }

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw "ASSERTION FAILED: $message" }
    Write-Host "  ok: $message"
}

function Wait-Until([scriptblock]$condition, [int]$timeoutSeconds, [string]$what) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSeconds) {
        if (& $condition) { Write-Host ("  ok: {0} after {1:0.0} s" -f $what, $sw.Elapsed.TotalSeconds); return }
        Start-Sleep -Milliseconds 250
    }
    throw "TIMEOUT after $timeoutSeconds s: $what"
}

function Invoke-Redis([int]$p, [string[]]$arguments, [string]$auth = $password) {
    # redis-cli exits 0 on error replies (NOAUTH, ERR), so callers compare the output, never the exit code.
    $env:REDISCLI_AUTH = $auth
    try { return ((& $cli -h 127.0.0.1 -p $p @arguments 2>&1) | Out-String).Trim() }
    finally { Remove-Item Env:REDISCLI_AUTH -ErrorAction SilentlyContinue }
}

function Test-Pong([int]$p = $port) { (Invoke-Redis $p @('PING')) -eq 'PONG' }

function Get-WrapperPid([string]$name = $svc) {
    $s = Get-CimInstance Win32_Service -Filter "Name='$name'"
    if ($null -eq $s) { return 0 }
    return [int]$s.ProcessId
}

function Get-RedisPid([string]$name = $svc) {
    $wrapper = Get-WrapperPid $name
    if ($wrapper -eq 0) { return 0 }
    $p = Get-CimInstance Win32_Process -Filter "Name='redis-server.exe' AND ParentProcessId=$wrapper" | Select-Object -First 1
    if ($null -eq $p) { return 0 }
    return [int]$p.ProcessId
}

function Get-Events([string]$provider, [int[]]$ids) {
    $filter = @{ LogName = 'Application'; ProviderName = $provider; StartTime = $t0 }
    if ($ids) { $filter['Id'] = $ids }
    @(Get-WinEvent -FilterHashtable $filter -ErrorAction SilentlyContinue)
}

function Get-EventText($record) {
    if ($record.Message) { return $record.Message }
    return (($record.Properties | ForEach-Object { $_.Value }) -join "`n")
}

function Start-Sleeper([int]$seconds) {
    # DEBUG SLEEP blocks the client until Redis wakes up, so it runs as its own process.
    $env:REDISCLI_AUTH = $password
    try {
        return Start-Process -FilePath $cli -ArgumentList @('-h', '127.0.0.1', '-p', "$port", 'DEBUG', 'SLEEP', "$seconds") -PassThru -WindowStyle Hidden
    } finally { Remove-Item Env:REDISCLI_AUTH -ErrorAction SilentlyContinue }
}

function Test-PortFree([int]$p) {
    $null -eq (Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue)
}

function Stop-TestService([string]$name, [int]$timeoutSeconds = 180) {
    & sc.exe stop $name | Out-Null
    (Get-Service -Name $name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds($timeoutSeconds))
}

function Start-TestService([string]$name) {
    & sc.exe start $name | Out-Null
    (Get-Service -Name $name).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
}

function Write-TestConfig([string]$dir, [int]$confPort) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $fwd = $dir -replace '\\', '/'
    $conf = Join-Path $dir 'redis.conf'
    @(
        "port $confPort"
        'bind 127.0.0.1'
        "requirepass `"$password`""
        "dir `"$fwd`""
        "logfile `"$fwd/redis.log`""
        'save 3600 1'
        'appendonly no'
        'loglevel notice'
        'enable-debug-command local'
    ) | Set-Content -Encoding ascii -LiteralPath $conf
    return $conf
}

Add-Type -Namespace RedisCi -Name Native -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)]
public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
[DllImport("kernel32.dll")]
public static extern bool CloseHandle(IntPtr handle);
'@

function New-TestCertificate([string]$commonName, $issuer) {
    # A CA when $issuer is null, otherwise a leaf without EKU (valid as server AND client certificate) signed by it.
    $x509 = 'System.Security.Cryptography.X509Certificates'
    $key = [System.Security.Cryptography.RSA]::Create(2048)
    $req = New-Object "$x509.CertificateRequest" -ArgumentList "CN=$commonName", $key,
        ([System.Security.Cryptography.HashAlgorithmName]::SHA256), ([System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $now = [DateTimeOffset]::UtcNow
    if ($null -eq $issuer) {
        $req.CertificateExtensions.Add((New-Object "$x509.X509BasicConstraintsExtension" -ArgumentList $true, $false, 0, $true))
        $req.CertificateExtensions.Add((New-Object "$x509.X509KeyUsageExtension" -ArgumentList ([System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]'KeyCertSign, CrlSign'), $true))
        $cert = $req.CreateSelfSigned($now.AddDays(-1), $now.AddDays(2))
    } else {
        $req.CertificateExtensions.Add((New-Object "$x509.X509KeyUsageExtension" -ArgumentList ([System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]'DigitalSignature, KeyEncipherment'), $true))
        $san = New-Object "$x509.SubjectAlternativeNameBuilder"
        $san.AddDnsName('localhost')
        $san.AddIpAddress([System.Net.IPAddress]::Loopback)
        $req.CertificateExtensions.Add($san.Build())
        $serial = [byte[]](@(1) + @(1..7 | ForEach-Object { Get-Random -Maximum 256 }))
        $cert = $req.Create($issuer.Cert, $now.AddDays(-1), $now.AddDays(1), $serial)
    }
    return [pscustomobject]@{ Cert = $cert; Key = $key }
}

function Write-TestPem([string]$basePath, $pair) {
    Set-Content -Encoding ascii -LiteralPath "$basePath.crt" -Value $pair.Cert.ExportCertificatePem()
    Set-Content -Encoding ascii -LiteralPath "$basePath.key" -Value $pair.Key.ExportPkcs8PrivateKeyPem()
}

function Invoke-RedisTls([int]$p, [string]$caFile, [string]$certBase, [string[]]$arguments) {
    $env:REDISCLI_AUTH = $password
    try { return ((& $cli --tls --cacert $caFile --cert "$certBase.crt" --key "$certBase.key" -h 127.0.0.1 -p $p @arguments 2>&1) | Out-String).Trim() }
    finally { Remove-Item Env:REDISCLI_AUTH -ErrorAction SilentlyContinue }
}

function Test-InJob([int]$processId) {
    $h = [RedisCi.Native]::OpenProcess(0x1000, $false, $processId)  # PROCESS_QUERY_LIMITED_INFORMATION
    if ($h -eq [IntPtr]::Zero) { throw "OpenProcess($processId) failed" }
    try {
        $inJob = $false
        if (-not [RedisCi.Native]::IsProcessInJob($h, [IntPtr]::Zero, [ref]$inJob)) { throw 'IsProcessInJob failed' }
        return $inJob
    } finally { [void][RedisCi.Native]::CloseHandle($h) }
}

try {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'This script must run as administrator.' }
    foreach ($f in @($exe, $cli, (Join-Path $PackageDir 'redis-server.exe'))) {
        if (-not (Test-Path -LiteralPath $f)) { throw "Package is missing $f" }
    }
    Write-Host "Package: $PackageDir (runtime: $(if ($isMsys2) { 'MSYS2' } else { 'Cygwin' }))"
    Write-Host "Service: $svc, port $port, work dir $work"
    $cleanupDirs.Add($work)

    # ------------------------------------------------------------------ install
    Write-Step 'CLI: invalid input exits 2'
    & $exe run --no-such-option 2>&1 | Out-Null
    Assert-True ($LASTEXITCODE -eq 2) "unknown option exits 2 (got $LASTEXITCODE)"
    & $exe install --port notanumber 2>&1 | Out-Null
    Assert-True ($LASTEXITCODE -eq 2) "bad --port exits 2 (got $LASTEXITCODE)"

    Write-Step 'Double-click: no arguments runs Redis in the foreground with the bundled redis.conf'
    if (-not (Test-PortFree 6379)) {
        Write-Host '  skipped: port 6379 is already in use on this runner' -ForegroundColor Yellow
    } else {
        New-Item -ItemType Directory -Force -Path $work | Out-Null
        $bareOut = Join-Path $work 'no-args.out'
        $bareErr = Join-Path $work 'no-args.err'
        $bare = Start-Process -FilePath $exe -WorkingDirectory $PackageDir -PassThru -NoNewWindow `
            -RedirectStandardOutput $bareOut -RedirectStandardError $bareErr
        $null = $bare.Handle   # keeps ExitCode readable after the process exits
        try {
            # The bundled redis.conf has no password: call redis-cli without REDISCLI_AUTH.
            Wait-Until { ((& $cli -h 127.0.0.1 -p 6379 PING 2>&1) | Out-String).Trim() -eq 'PONG' } 60 'the no-argument run answers PING on 6379'
            Assert-True ((Get-Content -Raw -LiteralPath $bareOut) -match 'Redis for Windows is starting in this window') 'the welcome text is printed'
            & $cli -h 127.0.0.1 -p 6379 SHUTDOWN NOSAVE 2>&1 | Out-Null
            Assert-True ($bare.WaitForExit(30000)) 'a client SHUTDOWN ends the no-argument run (restart policy on-crash)'
            Assert-True ($bare.ExitCode -eq 0) "the no-argument run exits 0 after a clean SHUTDOWN (got $($bare.ExitCode))"
        } catch {
            # Independent of the service tests below: record it and carry on, so one run reports every problem.
            $failures.Add("no-argument run: $_")
            Write-Host "FAILED (the service tests still run): $_" -ForegroundColor Red
            Write-Host ('--- the no-argument run ' + $(if ($bare.HasExited) { "exited with code $($bare.ExitCode)" } else { 'was still running' }))
            foreach ($f in @($bareOut, $bareErr)) {
                if (Test-Path -LiteralPath $f) { Write-Host "--- $f"; Get-Content -LiteralPath $f -Tail 80 | ForEach-Object { Write-Host "  $_" } }
            }
        } finally {
            if (-not $bare.HasExited) { $bare.Kill($true) }
        }
        try { Wait-Until { Test-PortFree 6379 } 10 'port 6379 is free again' }
        catch { $failures.Add("port 6379 after the no-argument run: $_"); Write-Host "FAILED: $_" -ForegroundColor Red }
    }

    Write-Step 'Install'
    $data = Join-Path $work 'data'
    # The config's own port is deliberately wrong: the --port override must win for Redis AND for the wrapper's SHUTDOWN.
    $conf = Write-TestConfig $data ($port + 1)
    Push-Location $env:SystemRoot   # Install from an unrelated working directory.
    try {
        & $exe install --service-name $svc -c $conf --port $port --start-mode manual --stop-timeout 150s
        $code = $LASTEXITCODE
    } finally { Pop-Location }
    Assert-True ($code -eq 0) "install exits 0 (got $code)"
    $createdServices.Add($svc)

    $qc = (& sc.exe qc $svc | Out-String)
    Write-Host $qc
    Assert-True ($qc -match [regex]::Escape("run --service-name $svc")) 'ImagePath is "<exe> run --service-name <name>"'
    Assert-True ($qc -notmatch '--port') 'ImagePath carries no options'
    Assert-True ($qc -match 'SERVICE_START_NAME\s*:\s*LocalSystem') 'default account is LocalSystem'
    $qfailure = (& sc.exe qfailure $svc | Out-String)
    Write-Host $qfailure
    Assert-True ($qfailure -match 'RESET_PERIOD \(in seconds\)\s*:\s*86400') 'failure count resets after 86400 s'
    Assert-True ($qfailure -match 'RESTART -- Delay = 5000') 'first failure action: restart after 5 s'
    Assert-True ($qfailure -match 'RESTART -- Delay = 30000') 'second failure action: restart after 30 s'
    Assert-True ($qfailure -match 'RESTART -- Delay = 60000') 'third failure action: restart after 60 s'
    Assert-True ((& sc.exe qfailureflag $svc | Out-String) -match 'FAILURE_ACTIONS_ON_NONCRASH_FAILURES\s*:\s*TRUE') 'failure actions also fire on a non-zero exit code'
    Assert-True ((& sc.exe qsidtype $svc | Out-String) -match 'UNRESTRICTED') 'service SID type is UNRESTRICTED'
    Assert-True ((& sc.exe qdescription $svc | Out-String) -match 'DESCRIPTION:\s+\S') 'description is set'
    $stored = (Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$svc\Parameters").Arguments
    Write-Host "  stored arguments: $($stored -join ' | ')"
    Assert-True ($stored -contains $conf) 'the absolute config path is stored under Parameters'
    Assert-True ($stored -contains "$port") 'the --port override is stored under Parameters'
    Assert-True (Test-Path -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\$svc") 'Event Log source is registered at install'

    # ------------------------------------------------------------------ start / readiness
    Write-Step 'Start and readiness'
    Start-TestService $svc
    Wait-Until { Test-Pong } 60 'PONG through requirepass on the overridden port'
    Assert-True (Test-PortFree ($port + 1)) 'the port from redis.conf is not used (override won)'
    Wait-Until { @(Get-Events $svc 1001).Count -ge 1 } 30 'Ready event (1001) logged'
    $wrapperPid = Get-WrapperPid
    $redisPid = Get-RedisPid
    Assert-True ($redisPid -ne 0) "redis-server is a child of the wrapper (wrapper $wrapperPid, redis $redisPid)"
    Assert-True (Test-InJob $redisPid) 'redis-server runs inside a job object'

    # ------------------------------------------------------------------ short pause is not a hang
    Write-Step 'DEBUG SLEEP 10 does not trigger a restart'
    $sleeper = Start-Sleeper 10
    Start-Sleep -Seconds 25
    if (-not $sleeper.HasExited) { $sleeper.Kill() }
    Assert-True ((Get-RedisPid) -eq $redisPid) 'same redis-server PID after a 10 s pause'
    Assert-True (@(Get-Events $svc 1002).Count -eq 0) 'no crash event after a 10 s pause'

    # ------------------------------------------------------------------ kill -> restart
    Write-Step 'Stop-Process -Force redis-server -> restart'
    $killedAt = Get-Date
    Stop-Process -Id $redisPid -Force
    Wait-Until { $n = Get-RedisPid; ($n -ne 0) -and ($n -ne $redisPid) -and (Test-Pong) } 20 'a new redis-server answers PING'
    Assert-True ((Get-WrapperPid) -eq $wrapperPid) 'the wrapper itself kept running'
    $crash = @(Get-Events $svc 1002 | Where-Object { $_.TimeCreated -ge $killedAt })
    Assert-True ($crash.Count -eq 1) "exactly one Error event (1002) for the crash (got $($crash.Count))"
    Assert-True ($crash[0].LevelDisplayName -eq 'Error' -or $crash[0].Level -eq 2) 'the crash event is an Error'
    Write-Host (Get-EventText $crash[0])

    # ------------------------------------------------------------------ segfault -> bug report excerpt
    Write-Step 'DEBUG SEGFAULT -> crash event carries the bug report'
    $redisPid = Get-RedisPid
    $segAt = Get-Date
    Invoke-Redis $port @('DEBUG', 'SEGFAULT') | Out-Null
    Wait-Until { $n = Get-RedisPid; ($n -ne 0) -and ($n -ne $redisPid) -and (Test-Pong) } 30 'redis-server restarted after SIGSEGV'
    $seg = @(Get-Events $svc 1002 | Where-Object { $_.TimeCreated -ge $segAt })
    Assert-True ($seg.Count -eq 1) 'one crash event for the segfault'
    $segText = Get-EventText $seg[0]
    Write-Host $segText
    Assert-True ($segText -match 'REDIS BUG REPORT START') 'the crash event includes the Redis bug report'
    Assert-True ($segText -match 'signal 11') 'the exit code is decoded as signal 11 (SIGSEGV)'

    # ------------------------------------------------------------------ hang -> health probe restart
    Write-Step 'DEBUG SLEEP 90 -> health probe restarts redis-server'
    $redisPid = Get-RedisPid
    # Until the wrapper has seen this process answer PING, a hang counts against --start-timeout (120 s), not the
    # health probe.
    Wait-Until { @(Get-Events $svc 1001 | Where-Object { (Get-EventText $_) -match "\(PID $redisPid\) is ready" }).Count -ge 1 } 30 "the wrapper declared redis-server $redisPid ready (event 1001)"
    $hangAt = Get-Date
    $sleeper = Start-Sleeper 90
    Wait-Until { $n = Get-RedisPid; ($n -ne 0) -and ($n -ne $redisPid) -and (Test-Pong) } 88 'redis-server replaced before the 90 s sleep ended'
    if (-not $sleeper.HasExited) { $sleeper.Kill() }
    $hang = @(Get-Events $svc 1002 | Where-Object { $_.TimeCreated -ge $hangAt })
    Assert-True ($hang.Count -eq 1 -and (Get-EventText $hang[0]) -match 'stopped answering PING') 'the crash event names the failed health probe'

    # ------------------------------------------------------------------ graceful stop saves
    Write-Step 'sc stop with requirepass: SHUTDOWN saves, also during a BGSAVE'
    Assert-True ((Invoke-Redis $port @('SET', 'ci:survivor', 'still-here')) -eq 'OK') 'SET a key'
    Assert-True ((Invoke-Redis $port @('DEBUG', 'POPULATE', '1000000', 'ci:bulk', '100')) -eq 'OK') 'populate ~1M keys'
    # Slow the fork down (2 us per key: well over 3 s for 1M keys) so its job membership can be checked while it
    # runs. SHUTDOWN kills the child and saves in the foreground, with the delay set back to 0 below.
    Assert-True ((Invoke-Redis $port @('CONFIG', 'SET', 'rdb-key-save-delay', '2')) -eq 'OK') 'slow down the BGSAVE child'
    Invoke-Redis $port @('BGSAVE') | Out-Null
    $redisPid = Get-RedisPid
    $script:forkPid = 0
    Wait-Until {
        $child = Get-CimInstance Win32_Process -Filter "Name='redis-server.exe' AND ParentProcessId=$redisPid" | Select-Object -First 1
        if ($null -ne $child) { $script:forkPid = [int]$child.ProcessId }
        $script:forkPid -ne 0
    } 20 'the BGSAVE fork child is running'
    Assert-True (Test-InJob $script:forkPid) "the BGSAVE fork child (PID $script:forkPid) runs inside the job object"
    Assert-True ((Invoke-Redis $port @('CONFIG', 'SET', 'rdb-key-save-delay', '0')) -eq 'OK') 'no save delay for the final SHUTDOWN save'
    $logBefore = (Get-Content -LiteralPath (Join-Path $data 'redis.log') -Raw).Length
    $stopWatch = [System.Diagnostics.Stopwatch]::StartNew()
    Stop-TestService $svc 180
    Write-Host ("  stopped in {0:0.0} s" -f $stopWatch.Elapsed.TotalSeconds)
    $logAfter = (Get-Content -LiteralPath (Join-Path $data 'redis.log') -Raw).Substring($logBefore)
    Assert-True ($logAfter -match 'User requested shutdown') 'Redis logged "User requested shutdown"'
    Assert-True ($logAfter -match 'DB saved on disk') 'Redis logged "DB saved on disk"'
    Assert-True (@(Get-ChildItem -LiteralPath $data -Filter 'temp-*.rdb').Count -eq 0) 'no temp-*.rdb left behind'
    Assert-True (Test-PortFree $port) 'port is free after the stop'
    Assert-True (@(Get-Events $svc 1006).Count -ge 1) 'Stopped event (1006) logged'
    Start-TestService $svc
    Wait-Until { Test-Pong } 120 'PONG after restart (RDB loaded)'
    Assert-True ((Invoke-Redis $port @('GET', 'ci:survivor')) -eq 'still-here') 'the key survived the stop'
    # Keep the remaining restarts fast: an empty dataset loads instantly.
    Invoke-Redis $port @('FLUSHALL') | Out-Null
    Assert-True ((Invoke-Redis $port @('SAVE')) -eq 'OK') 'dataset emptied and saved'

    # ------------------------------------------------------------------ crash loop -> 1067 -> SCM recovery
    Write-Step 'Crash loop: the wrapper gives up with 1067 and SCM restarts the service'
    $wrapperPid = Get-WrapperPid
    $loopAt = Get-Date
    $gaveUp = $false
    $kills = 0
    $deadline = (Get-Date).AddMinutes(8)
    while (-not $gaveUp -and $kills -lt 6 -and (Get-Date) -lt $deadline) {
        if (@(Get-Events $svc 1004 | Where-Object { $_.TimeCreated -ge $loopAt }).Count -gt 0) { $gaveUp = $true; break }
        $r = Get-RedisPid
        if ($r -eq 0 -or -not (Test-Pong)) { Start-Sleep -Milliseconds 250; continue }
        $kills++
        Write-Host "  kill #$kills (redis PID $r)"
        Stop-Process -Id $r -Force -ErrorAction SilentlyContinue
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($sw.Elapsed.TotalSeconds -lt 90) {
            if (@(Get-Events $svc 1004 | Where-Object { $_.TimeCreated -ge $loopAt }).Count -gt 0) { $gaveUp = $true; break }
            $n = Get-RedisPid
            if ($n -ne 0 -and $n -ne $r -and (Test-Pong)) { break }
            Start-Sleep -Milliseconds 250
        }
    }
    Assert-True $gaveUp "the wrapper logged the crash-loop give-up event (1004) after $kills kills"
    Assert-True ($kills -eq 5) 'it gave up on the 5th crash within the window'
    Write-Host (Get-EventText (Get-Events $svc 1004 | Select-Object -First 1))
    Wait-Until { $w = Get-WrapperPid; ($w -ne 0) -and ($w -ne $wrapperPid) } 60 'SCM recovery started a new wrapper process'
    Wait-Until { Test-Pong } 60 'PONG after SCM recovery'
    $scm = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager'; StartTime = $loopAt } -ErrorAction SilentlyContinue |
        Where-Object { $_.Id -in 7023, 7024, 7031, 7034 })
    if ($scm.Count -gt 0) { Write-Host "  SCM logged: $($scm[0].Id) $($scm[0].Message)" } else { Write-Warning 'No SCM 7023/7024/7031 event found (non-fatal).' }

    # ------------------------------------------------------------------ job object
    Write-Step 'taskkill /F the wrapper: the job object takes redis-server down'
    $wrapperPid = Get-WrapperPid
    $redisPid = Get-RedisPid
    Assert-True ($redisPid -ne 0) "redis-server running under wrapper $wrapperPid"
    & taskkill.exe /F /PID $wrapperPid | Out-Null
    Wait-Until { $null -eq (Get-Process -Id $redisPid -ErrorAction SilentlyContinue) } 5 'redis-server is gone'
    Wait-Until { Test-PortFree $port } 5 "port $port is free"

    # ------------------------------------------------------------------ Event Log content
    Write-Step 'Event Log: lifecycle events only, English'
    $all = @(Get-Events $svc)
    $ids = $all | Group-Object Id | Sort-Object Name | ForEach-Object { "$($_.Name) x$($_.Count)" }
    Write-Host "  events: $($ids -join ', ')"
    foreach ($id in 1000, 1001, 1002, 1003, 1004, 1005, 1006) {
        Assert-True (@($all | Where-Object Id -eq $id).Count -ge 1) "event $id present"
    }
    Assert-True (@($all | Where-Object Id -eq 2000).Count -eq 0) 'no per-line Redis stdout events (2000)'
    Assert-True (@($all | Where-Object Id -eq 2001).Count -le 40) 'stderr events are rate-limited'
    Assert-True ($all.Count -lt 300) "event volume is lifecycle-sized ($($all.Count) events)"
    # The console-less launcher and both job objects must have worked; their fallbacks only log a warning.
    $fallbacks = @($all | Where-Object { (Get-EventText $_) -match 'falling back to a hidden console|Could not place the wrapper in a job object|Could not create the redis-server job object|Could not assign redis-server to its job object' })
    Assert-True ($fallbacks.Count -eq 0) 'no launcher or job-object fallback warnings'
    $cjk = @($all | Where-Object { (Get-EventText $_) -match '[\u4e00-\u9fff]' })
    Assert-True ($cjk.Count -eq 0) 'all event text is English'

    # ------------------------------------------------------------------ virtual account, path with spaces
    Write-Step 'Virtual account install from a path with spaces'
    $vaRoot = Join-Path $env:ProgramFiles "Redis CI $suffix"
    $cleanupDirs.Add($vaRoot)
    Copy-Item -Recurse -LiteralPath $PackageDir -Destination $vaRoot
    $cleanupDirs.Add((Split-Path -Parent $vaData))
    $vaConf = Write-TestConfig $vaData $portVirtual
    & (Join-Path $vaRoot 'RedisService.exe') install --service-name $svcVirtual -c $vaConf --virtual-account --start-mode auto
    $code = $LASTEXITCODE
    $createdServices.Add($svcVirtual)
    Assert-True ($code -eq 0) "virtual-account install exits 0 (got $code)"
    Assert-True ((& sc.exe qc $svcVirtual | Out-String) -match [regex]::Escape("NT SERVICE\$svcVirtual")) "runs as NT SERVICE\$svcVirtual"
    $acl = (& icacls.exe $vaData | Out-String)
    Write-Host $acl
    Assert-True ($acl -match [regex]::Escape("NT SERVICE\$svcVirtual")) 'icacls shows the grant on the data directory'
    Wait-Until { Test-Pong $portVirtual } 60 'PONG from the virtual-account service'
    Assert-True ((Invoke-Redis $portVirtual @('SET', 'va', '1')) -eq 'OK') 'SET through the virtual-account service'
    Stop-TestService $svcVirtual 180
    Assert-True (Test-Path -LiteralPath (Join-Path $vaData 'dump.rdb')) 'the virtual account wrote dump.rdb into the intended folder'
    Assert-True ((Get-Content -LiteralPath (Join-Path $vaData 'redis.log') -Raw) -match 'DB saved on disk') 'final save succeeded under the virtual account'
    Assert-True (@(Get-Events $svcVirtual 1001).Count -ge 1) 'lifecycle events are logged under the virtual account (source pre-registered)'

    # ------------------------------------------------------------------ TLS only
    $hasTls = @(Get-ChildItem -LiteralPath $PackageDir -Filter '*.dll' | Where-Object Name -match '^(msys|cyg)ssl-').Count -gt 0
    if ($hasTls) {
        Write-Step 'TLS only: readiness and a graceful stop over TLS'
        $tlsDir = Join-Path $work 'tls'
        New-Item -ItemType Directory -Force -Path $tlsDir | Out-Null
        $fwdTls = $tlsDir -replace '\\', '/'
        $serverCa = New-TestCertificate 'RedisService CI server CA' $null
        $clientCa = New-TestCertificate 'RedisService CI client CA' $null
        Write-TestPem "$tlsDir/server-ca" $serverCa
        Write-TestPem "$tlsDir/client-ca" $clientCa
        Write-TestPem "$tlsDir/server" (New-TestCertificate 'localhost' $serverCa)
        Write-TestPem "$tlsDir/client" (New-TestCertificate 'redis-ci-client' $clientCa)
        $tlsConf = Join-Path $tlsDir 'redis.conf'
        function Write-TlsConfig([bool]$separateClientCa) {
            $lines = @(
                'port 0'
                "tls-port $portTls"
                'bind 127.0.0.1'
                "requirepass `"$password`""
                "dir `"$fwdTls`""
                "logfile `"$fwdTls/redis.log`""
                'save 3600 1'
                'appendonly no'
                "tls-cert-file `"$fwdTls/server.crt`""
                "tls-key-file `"$fwdTls/server.key`""
            )
            if ($separateClientCa) {
                # tls-ca-cert-file is the CA Redis verifies CLIENTS with; the server's own certificate is from
                # another CA, so the wrapper cannot verify the server even though Redis is healthy.
                $lines += "tls-ca-cert-file `"$fwdTls/client-ca.crt`""
                $lines += "tls-client-cert-file `"$fwdTls/client.crt`""
                $lines += "tls-client-key-file `"$fwdTls/client.key`""
            } else {
                $lines += "tls-ca-cert-file `"$fwdTls/server-ca.crt`""
            }
            $lines | Set-Content -Encoding ascii -LiteralPath $tlsConf
        }
        Write-TlsConfig $false
        & $exe install --service-name $svcTls -c $tlsConf --start-mode manual --health-interval 1s --health-failures 3 --start-timeout 20s
        $code = $LASTEXITCODE
        $createdServices.Add($svcTls)
        Assert-True ($code -eq 0) "TLS install exits 0 (got $code)"
        Start-TestService $svcTls
        Wait-Until { (Invoke-RedisTls $portTls "$fwdTls/server-ca.crt" "$fwdTls/server" @('PING')) -eq 'PONG' } 60 'PONG over TLS'
        Wait-Until { @(Get-Events $svcTls 1001).Count -ge 1 } 30 'the wrapper saw PONG over TLS (event 1001)'
        Assert-True (@(Get-Events $svcTls 1009 | Where-Object { (Get-EventText $_) -match 'TLS' }).Count -eq 0) 'no TLS warning with a matching CA'
        Assert-True ((Invoke-RedisTls $portTls "$fwdTls/server-ca.crt" "$fwdTls/server" @('SET', 'ci:tls', 'kept')) -eq 'OK') 'SET over TLS'
        $tlsLog = Join-Path $tlsDir 'redis.log'
        $logBefore = (Get-Content -LiteralPath $tlsLog -Raw).Length
        Stop-TestService $svcTls 180
        $logAfter = (Get-Content -LiteralPath $tlsLog -Raw).Substring($logBefore)
        Assert-True ($logAfter -match 'User requested shutdown') 'the wrapper sent SHUTDOWN over TLS'
        Assert-True ($logAfter -match 'DB saved on disk') 'the final save over TLS succeeded'
        Assert-True (Test-PortFree $portTls) 'TLS port is free after the stop'

        Write-Step 'TLS: a server certificate the wrapper cannot verify is a warning, not a hang'
        Write-TlsConfig $true
        $tlsAt = Get-Date
        Start-TestService $svcTls
        Wait-Until { (Invoke-RedisTls $portTls "$fwdTls/server-ca.crt" "$fwdTls/client" @('PING')) -eq 'PONG' } 60 'Redis answers redis-cli over TLS'
        Wait-Until { @(Get-Events $svcTls 1009 | Where-Object { $_.TimeCreated -ge $tlsAt -and (Get-EventText $_) -match 'TLS connection' }).Count -eq 1 } 30 'one TLS ConfigWarning (1009)'
        $tlsPid = Get-RedisPid $svcTls
        Start-Sleep -Seconds 20   # Far beyond --start-timeout 20s from start and the 3 x 1 s miss budget.
        Assert-True ((Get-RedisPid $svcTls) -eq $tlsPid) "redis-server was not restarted (PID $tlsPid)"
        Assert-True (@(Get-Events $svcTls 1002 | Where-Object { $_.TimeCreated -ge $tlsAt }).Count -eq 0) 'no crash event for a TLS configuration mismatch'
        Assert-True ((Invoke-RedisTls $portTls "$fwdTls/server-ca.crt" "$fwdTls/client" @('GET', 'ci:tls')) -eq 'kept') 'the key saved by the TLS stop survived'
        Stop-TestService $svcTls 180
        Assert-True (@(Get-Events $svcTls 1008 | Where-Object { $_.TimeCreated -ge $tlsAt -and (Get-EventText $_) -match 'Could not send SHUTDOWN' }).Count -ge 1) 'the stop problem names the unreachable TLS endpoint'
        Wait-Until { Test-PortFree $portTls } 10 'TLS port is free after the stop'
    } else {
        Write-Host "`n=== TLS: skipped (the package has no OpenSSL DLL)" -ForegroundColor Yellow
    }

    # ------------------------------------------------------------------ bin folder trap (MSYS2)
    if ($isMsys2) {
        Write-Step 'MSYS2: install under C:\bin\... with a relative data dir is refused'
        $binRoot = "C:\bin\RedisCI$suffix"
        $cleanupDirs.Add($binRoot)
        New-Item -ItemType Directory -Force -Path 'C:\bin' | Out-Null
        Copy-Item -Recurse -LiteralPath $PackageDir -Destination $binRoot
        $out = (& (Join-Path $binRoot 'RedisService.exe') install --service-name $svcTrap -c (Join-Path $binRoot 'redis.conf') 2>&1 | Out-String)
        $code = $LASTEXITCODE
        Write-Host $out
        if ($null -ne (Get-Service -Name $svcTrap -ErrorAction SilentlyContinue)) { $createdServices.Add($svcTrap) }
        Assert-True ($code -eq 1) "install is refused (exit $code)"
        Assert-True ($out -match "'bin'") 'the message explains the bin/usr folder problem'
        Assert-True ($null -eq (Get-Service -Name $svcTrap -ErrorAction SilentlyContinue)) 'no service was created'

        Write-Step 'MSYS2: the same C:\bin\... package with an absolute --dir works and saves into that folder'
        $binData = Join-Path $work 'bin-data'
        New-Item -ItemType Directory -Force -Path $binData | Out-Null
        $fwdBin = $binData -replace '\\', '/'
        $binConf = Join-Path $binRoot 'ci.conf'
        @(
            "port $portBin"
            'bind 127.0.0.1'
            "requirepass `"$password`""
            'save 3600 1'
            'appendonly no'
            "logfile `"$fwdBin/redis.log`""
        ) | Set-Content -Encoding ascii -LiteralPath $binConf
        $out = (& (Join-Path $binRoot 'RedisService.exe') install --service-name $svcTrap -c $binConf --dir $binData --start-mode manual 2>&1 | Out-String)
        $code = $LASTEXITCODE
        Write-Host $out
        if ($null -ne (Get-Service -Name $svcTrap -ErrorAction SilentlyContinue)) { $createdServices.Add($svcTrap) }
        Assert-True ($code -eq 0) "install with an absolute --dir exits 0 (got $code)"
        Start-TestService $svcTrap
        Wait-Until { Test-Pong $portBin } 60 'PONG from the C:\bin install'
        Assert-True ((Invoke-Redis $portBin @('SET', 'ci:bin', '1')) -eq 'OK') 'SET through the C:\bin install'
        Stop-TestService $svcTrap 180
        Assert-True (Test-Path -LiteralPath (Join-Path $binData 'dump.rdb')) 'SHUTDOWN SAVE wrote dump.rdb into the absolute --dir'
        Assert-True ((Get-Content -LiteralPath (Join-Path $binData 'redis.log') -Raw) -match 'DB saved on disk') 'final save logged in the absolute logfile'
        $remapped = "C:\usr\bin\RedisCI$suffix"
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $remapped 'dump.rdb'))) "nothing was written to the remapped $remapped"
    }

    Write-Host "`nALL WRAPPER INTEGRATION CHECKS PASSED" -ForegroundColor Green
}
catch {
    $failures.Add($_.ToString())
    Write-Host "`nFAILED: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
    foreach ($name in $createdServices) {
        Write-Host "--- Application events for $name"
        Get-Events $name | Sort-Object TimeCreated | Select-Object -Last 40 | ForEach-Object {
            Write-Host ("[{0:HH:mm:ss}] {1} {2}: {3}" -f $_.TimeCreated, $_.Id, $_.LevelDisplayName, (Get-EventText $_))
        }
    }
    foreach ($log in @((Join-Path $work 'data\redis.log'), (Join-Path $vaData 'redis.log'), (Join-Path $work 'tls\redis.log'), (Join-Path $work 'bin-data\redis.log'))) {
        if (Test-Path -LiteralPath $log) { Write-Host "--- tail of $log"; Get-Content -LiteralPath $log -Tail 60 | ForEach-Object { Write-Host $_ } }
    }
}
finally {
    foreach ($name in $createdServices) {
        Write-Host "Uninstalling $name"
        & $exe uninstall --service-name $name --stop-timeout 180s 2>&1 | ForEach-Object { Write-Host "  $_" }
        if ($null -ne (Get-Service -Name $name -ErrorAction SilentlyContinue)) {
            & sc.exe stop $name 2>&1 | Out-Null
            Start-Sleep -Seconds 2
            & sc.exe delete $name 2>&1 | Out-Null
        }
        Remove-Item -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\$name" -Recurse -ErrorAction SilentlyContinue
    }
    # Nothing from these packages may survive the test.
    try {
    $roots = @($cleanupDirs) + @($PackageDir)
    $leftovers = @(Get-CimInstance Win32_Process -Filter "Name='redis-server.exe' OR Name='RedisService.exe'" | Where-Object {
        $proc = $_
        $proc.ExecutablePath -and @($roots | Where-Object { $proc.ExecutablePath -like "$_*" }).Count -gt 0
    })
    foreach ($p in $leftovers) {
        Write-Warning "Killing leftover $($p.Name) $($p.ProcessId) ($($p.ExecutablePath))"
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
    } catch { Write-Warning "Leftover-process cleanup failed: $_" }
    foreach ($d in $cleanupDirs) { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($failures.Count -gt 0) { exit 1 }
exit 0
