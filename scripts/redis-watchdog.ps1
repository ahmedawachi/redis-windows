<#
.SYNOPSIS
    Interim health watchdog for a Redis Windows service that still runs the
    STOCK RedisService.exe wrapper (which never restarts a dead redis-server).

.DESCRIPTION
    Run once per minute from Task Scheduler (see docs/OPERATIONS.md,
    "Interim watchdog"). Each run:

      1. Checks the Windows service. If it is not Running, the script does
         nothing (an operator stopped it on purpose), unless -StartIfStopped.
      2. Sends an authenticated PING with redis-cli. The password is passed in
         the REDISCLI_AUTH environment variable of the redis-cli child process
         only - never on the command line with -a, where other users could read
         it from the process list.
      3. Treats anything other than the exact reply "PONG" as a failure.
         redis-cli exits with code 0 even for error replies such as NOAUTH or
         LOADING, so the exit code alone proves nothing.
      4. After -FailureThreshold consecutive failures (counted across runs in a
         small state file), restarts the service with Restart-Service. No
         second restart happens within -GraceMinutes of the previous one.

    Every state change is written to the Windows Application event log under
    the source "RedisWatchdog" (created on first use; that needs admin rights,
    which a task running as SYSTEM has):

      1000  Information  Probe healthy again after failures (recovered).
      1001  Information  Service restarted by the watchdog and is Running.
      1002  Information  Service was stopped and has been started (-StartIfStopped).
      2001  Warning      Probe failed (count / threshold and the reason).
      2002  Warning      Threshold reached but inside the grace period; no restart.
      3001  Error        Threshold reached; restarting the service.
      3002  Error        Restart failed.
      3003  Error        Watchdog configuration error (service or redis-cli not found).

    Nothing is logged while the service is healthy, so the log stays quiet.

    This script is a stop-gap. Remove the scheduled task once the service runs
    this fork's RedisService.exe, which supervises and restarts redis-server
    itself - two supervisors restarting the same service fight each other.

    Compatible with Windows PowerShell 5.1 and PowerShell 7 on Windows.
    -ProbeOnly also works on other platforms (it only runs redis-cli).

.PARAMETER ServiceName
    Windows service name. Default: Redis.

.PARAMETER RedisCliPath
    Full path of redis-cli.exe. Default: the folder of the service's
    executable, then the folder of this script.

.PARAMETER HostName
    Address to probe. Default: 127.0.0.1. Must be an address Redis binds to.

.PARAMETER Port
    Port to probe. Default: the --port option in the service's command line,
    else the last "port" line of the config file, else 6379.

.PARAMETER ConfigPath
    redis.conf to read "requirepass" and "port" from (last value wins, as in
    Redis). Default: the -c / --config option in the service's command line.
    "include" directives are not followed.

.PARAMETER PasswordFile
    Alternative password source: a file whose first line is the password.
    Takes precedence over the config file. Protect it with an ACL.

.PARAMETER User
    ACL user name, if the password belongs to a named ACL user rather than
    the default user.

.PARAMETER FailureThreshold
    Consecutive failed runs before a restart. Default: 3.

.PARAMETER ProbeTimeoutSeconds
    How long one PING may take before it counts as a failure. A hung server
    accepts the connection but never answers; this catches it. Default: 5.

.PARAMETER GraceMinutes
    Minimum minutes between two restarts by this script, so a slow start
    (loading a large RDB answers "LOADING") does not cause a restart loop.
    Default: 5.

.PARAMETER StateDirectory
    Folder for the failure counter. Default: %ProgramData%\RedisWatchdog.

.PARAMETER StartIfStopped
    Also start the service when it is found Stopped. Off by default, so a
    deliberate stop by an operator is respected.

.PARAMETER ProbeOnly
    Run a single probe, print the result and exit (0 = PONG, 1 = failure).
    No state file, no event log, no restart. Use it to test the settings.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Redis\tools\redis-watchdog.ps1 -ServiceName Redis -ProbeOnly

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Redis\tools\redis-watchdog.ps1 -ServiceName Redis -ConfigPath C:\Redis\redis.conf -FailureThreshold 3
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', 'PasswordFile', Justification = 'A file path, not a password.')]
[CmdletBinding()]
param(
    [string]$ServiceName = 'Redis',
    [string]$RedisCliPath,
    [string]$HostName = '127.0.0.1',
    [ValidateRange(0, 65535)]
    [int]$Port = 0,
    [string]$ConfigPath,
    [string]$PasswordFile,
    [string]$User,
    [ValidateRange(1, 100)]
    [int]$FailureThreshold = 3,
    [ValidateRange(1, 300)]
    [int]$ProbeTimeoutSeconds = 5,
    [ValidateRange(0, 1440)]
    [int]$GraceMinutes = 5,
    [string]$StateDirectory,
    [string]$EventSource = 'RedisWatchdog',
    [switch]$StartIfStopped,
    [switch]$ProbeOnly
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# ----------------------------------------------------------------------------
# Helpers
# ----------------------------------------------------------------------------

function ConvertFrom-RedisConfigValue {
    # Undo the quoting rules of Redis's config parser (sdssplitargs) for one
    # value: "double quoted" with backslash escapes, or 'single quoted'.
    param([string]$Raw)
    $v = $Raw.Trim()
    if ($v.Length -ge 2 -and $v.StartsWith('"') -and $v.EndsWith('"')) {
        $inner = $v.Substring(1, $v.Length - 2)
        $sb = New-Object System.Text.StringBuilder
        $i = 0
        while ($i -lt $inner.Length) {
            $c = $inner[$i]
            if ($c -eq '\' -and ($i + 1) -lt $inner.Length) {
                $n = $inner[$i + 1]
                if ($n -ceq 'x' -and ($i + 3) -lt $inner.Length -and
                    $inner.Substring($i + 2, 2) -match '^[0-9A-Fa-f]{2}$') {
                    [void]$sb.Append([char][Convert]::ToInt32($inner.Substring($i + 2, 2), 16))
                    $i += 4
                    continue
                }
                switch -CaseSensitive ([string]$n) {
                    'n' { [void]$sb.Append("`n") }
                    'r' { [void]$sb.Append("`r") }
                    't' { [void]$sb.Append("`t") }
                    'b' { [void]$sb.Append([char]8) }
                    'a' { [void]$sb.Append([char]7) }
                    default { [void]$sb.Append($n) }
                }
                $i += 2
                continue
            }
            [void]$sb.Append($c)
            $i++
        }
        return $sb.ToString()
    }
    if ($v.Length -ge 2 -and $v.StartsWith("'") -and $v.EndsWith("'")) {
        return $v.Substring(1, $v.Length - 2).Replace("\'", "'")
    }
    return $v
}

function Get-RedisConfigValue {
    # Last occurrence of a directive in a redis.conf, or $null.
    param([string]$Path, [string]$Name)
    $result = $null
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        $t = $line.Trim()
        if ($t.Length -eq 0 -or $t.StartsWith('#')) { continue }
        $m = [regex]::Match($t, '^(?i)' + [regex]::Escape($Name) + '\s+(.*)$')
        if ($m.Success) { $result = ConvertFrom-RedisConfigValue $m.Groups[1].Value }
    }
    return $result
}

function Get-ServiceCommandLine {
    # ImagePath of the service, or $null when it cannot be read.
    param([string]$Name)
    if (-not (Get-Command Get-CimInstance -ErrorAction SilentlyContinue)) { return $null }
    try {
        $svc = Get-CimInstance -ClassName Win32_Service -Filter ("Name='{0}'" -f $Name.Replace("'", "''"))
        if ($svc) { return [string]$svc.PathName }
    } catch {
        Write-Verbose ("Could not read the service command line: {0}" -f $_.Exception.Message)
    }
    return $null
}

function Get-ExecutableFromCommandLine {
    param([string]$CommandLine)
    if (-not $CommandLine) { return $null }
    $m = [regex]::Match($CommandLine, '^\s*"([^"]+)"')
    if ($m.Success) { return $m.Groups[1].Value }
    $m = [regex]::Match($CommandLine, '^\s*(\S+)')
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

function Get-OptionFromCommandLine {
    # Value of the first matching option (-c "x", --port 6380, --config=x ...).
    param([string]$CommandLine, [string[]]$Names)
    if (-not $CommandLine) { return $null }
    foreach ($n in $Names) {
        $pattern = '(?:^|\s)' + [regex]::Escape($n) + '(?:\s+|=)(?:"([^"]*)"|(\S+))'
        $m = [regex]::Match($CommandLine, $pattern)
        if ($m.Success) {
            if ($m.Groups[1].Success) { return $m.Groups[1].Value }
            return $m.Groups[2].Value
        }
    }
    return $null
}

function Invoke-RedisPing {
    # One PING through redis-cli. Returns an object with Ok, Reason, Output.
    param(
        [string]$CliPath,
        [string]$TargetHost,
        [int]$TargetPort,
        [string]$AuthSecret,
        [string]$AclUser,
        [int]$TimeoutSeconds
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $CliPath
    $cliArgs = '-h {0} -p {1} -t {2}' -f $TargetHost, $TargetPort, $TimeoutSeconds
    if ($AclUser) { $cliArgs += (' --user "{0}"' -f $AclUser.Replace('"', '')) }
    $psi.Arguments = $cliArgs + ' PING'
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    if ($AuthSecret) {
        # Visible to the child process only; never placed on a command line.
        $psi.EnvironmentVariables['REDISCLI_AUTH'] = $AuthSecret
    }

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    try {
        [void]$proc.Start()
    } catch {
        return [pscustomobject]@{ Ok = $false; Reason = ('redis-cli could not be started: {0}' -f $_.Exception.Message); Output = '' }
    }
    try {
        $outTask = $proc.StandardOutput.ReadToEndAsync()
        $errTask = $proc.StandardError.ReadToEndAsync()
        if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) {
            try { $proc.Kill() } catch { Write-Verbose 'redis-cli already exited.' }
            return [pscustomobject]@{ Ok = $false; Reason = ('no reply within {0} s (server hung or unreachable)' -f $TimeoutSeconds); Output = '' }
        }
        $proc.WaitForExit()
        $out = ([string]$outTask.Result).Trim()
        $err = ([string]$errTask.Result).Trim()
    } finally {
        $proc.Dispose()
    }

    if ($out -ceq 'PONG') {
        return [pscustomobject]@{ Ok = $true; Reason = 'PONG'; Output = $out }
    }
    $detail = (($out + ' ' + $err).Trim() -replace '\s+', ' ')
    if ($detail.Length -gt 300) { $detail = $detail.Substring(0, 300) + '...' }
    if (-not $detail) { $detail = '(no output)' }
    return [pscustomobject]@{ Ok = $false; Reason = ('unexpected reply: {0}' -f $detail); Output = $out }
}

function Write-WatchdogEvent {
    param(
        [ValidateSet('Information', 'Warning', 'Error')]
        [string]$Level,
        [int]$EventId,
        [string]$Message
    )
    $text = "[{0}] {1}" -f $ServiceName, $Message
    Write-Verbose ("{0} {1}: {2}" -f $Level, $EventId, $text)
    try {
        if (-not [System.Diagnostics.EventLog]::SourceExists($EventSource)) {
            [System.Diagnostics.EventLog]::CreateEventSource($EventSource, 'Application')
        }
        $type = [System.Diagnostics.EventLogEntryType]::$Level
        [System.Diagnostics.EventLog]::WriteEntry($EventSource, $text, $type, $EventId)
    } catch {
        # Event log unavailable (no admin rights to create the source): fall
        # back to a text file next to the state file.
        try {
            $line = '{0:u} {1} {2} {3}' -f (Get-Date).ToUniversalTime(), $Level, $EventId, $text
            Add-Content -LiteralPath (Join-Path $script:StateDir 'watchdog.log') -Value $line
        } catch {
            Write-Warning $text
        }
    }
}

function Read-WatchdogState {
    # State survives between scheduled runs. Times are stored as UTC ticks
    # (an integer), which round-trips identically in PowerShell 5.1 and 7.
    param([string]$Path)
    $state = [pscustomobject]@{ ConsecutiveFailures = 0; LastRestartTicksUtc = [int64]0; AwaitingRecovery = $false }
    if (Test-Path -LiteralPath $Path) {
        try {
            $loaded = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
            if ($loaded.PSObject.Properties['ConsecutiveFailures']) { $state.ConsecutiveFailures = [int]$loaded.ConsecutiveFailures }
            if ($loaded.PSObject.Properties['LastRestartTicksUtc']) { $state.LastRestartTicksUtc = [int64]$loaded.LastRestartTicksUtc }
            if ($loaded.PSObject.Properties['AwaitingRecovery']) { $state.AwaitingRecovery = [bool]$loaded.AwaitingRecovery }
        } catch {
            Write-Verbose 'State file unreadable; starting from zero.'
        }
    }
    return $state
}

function Save-WatchdogState {
    param([string]$Path, $State)
    $obj = [pscustomobject]@{
        ConsecutiveFailures = [int]$State.ConsecutiveFailures
        LastRestartTicksUtc = [int64]$State.LastRestartTicksUtc
        AwaitingRecovery    = [bool]$State.AwaitingRecovery
    }
    Set-Content -LiteralPath $Path -Value ($obj | ConvertTo-Json) -Encoding ASCII
}

# ----------------------------------------------------------------------------
# Resolve settings
# ----------------------------------------------------------------------------

$serviceCommandLine = $null
if (-not $ProbeOnly -or -not $RedisCliPath -or -not $ConfigPath -or $Port -eq 0) {
    $serviceCommandLine = Get-ServiceCommandLine -Name $ServiceName
}

if (-not $ConfigPath) {
    $ConfigPath = Get-OptionFromCommandLine -CommandLine $serviceCommandLine -Names @('-c', '--config')
}
if ($ConfigPath -and -not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    Write-Verbose ("Config file not found, ignoring: {0}" -f $ConfigPath)
    $ConfigPath = $null
}

if ($Port -eq 0) {
    $p = Get-OptionFromCommandLine -CommandLine $serviceCommandLine -Names @('--port')
    if (-not $p -and $ConfigPath) { $p = Get-RedisConfigValue -Path $ConfigPath -Name 'port' }
    if ($p -and ($p -as [int])) { $Port = [int]$p } else { $Port = 6379 }
}

if (-not $RedisCliPath) {
    $candidates = @()
    $exe = Get-ExecutableFromCommandLine -CommandLine $serviceCommandLine
    if ($exe) { $candidates += (Join-Path (Split-Path -Parent $exe) 'redis-cli.exe') }
    if ($PSScriptRoot) {
        $candidates += (Join-Path $PSScriptRoot 'redis-cli.exe')
        $candidates += (Join-Path (Split-Path -Parent $PSScriptRoot) 'redis-cli.exe')
    }
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c -PathType Leaf) { $RedisCliPath = $c; break }
    }
}

$password = $null
if ($PasswordFile) {
    $password = [string](Get-Content -LiteralPath $PasswordFile -TotalCount 1)
} elseif ($ConfigPath) {
    $password = Get-RedisConfigValue -Path $ConfigPath -Name 'requirepass'
}
# Otherwise redis-cli inherits REDISCLI_AUTH from this process, if it is set.

# ----------------------------------------------------------------------------
# Probe-only mode
# ----------------------------------------------------------------------------

if ($ProbeOnly) {
    if (-not $RedisCliPath -or -not (Test-Path -LiteralPath $RedisCliPath -PathType Leaf)) {
        Write-Output 'FAIL: redis-cli.exe not found; pass -RedisCliPath.'
        exit 2
    }
    $r = Invoke-RedisPing -CliPath $RedisCliPath -TargetHost $HostName -TargetPort $Port -AuthSecret $password -AclUser $User -TimeoutSeconds $ProbeTimeoutSeconds
    $authSource = 'none'
    if ($PasswordFile) { $authSource = 'password file' }
    elseif ($password) { $authSource = 'requirepass from config' }
    elseif ($env:REDISCLI_AUTH) { $authSource = 'inherited REDISCLI_AUTH' }
    Write-Output ('Target {0}:{1}  redis-cli {2}  auth: {3}' -f $HostName, $Port, $RedisCliPath, $authSource)
    if ($r.Ok) { Write-Output 'OK: PONG'; exit 0 }
    Write-Output ('FAIL: {0}' -f $r.Reason)
    exit 1
}

# ----------------------------------------------------------------------------
# Watchdog mode
# ----------------------------------------------------------------------------

if ($StateDirectory) {
    $script:StateDir = $StateDirectory
} elseif ($env:ProgramData) {
    $script:StateDir = Join-Path $env:ProgramData 'RedisWatchdog'
} else {
    $script:StateDir = Join-Path ([System.IO.Path]::GetTempPath()) 'RedisWatchdog'
}
if (-not (Test-Path -LiteralPath $script:StateDir)) {
    New-Item -ItemType Directory -Path $script:StateDir -Force | Out-Null
}
$safeName = ($ServiceName -replace '[^A-Za-z0-9_.-]', '_')
$statePath = Join-Path $script:StateDir ('{0}.state.json' -f $safeName)

try {
    $service = Get-Service -Name $ServiceName
} catch {
    Write-WatchdogEvent -Level Error -EventId 3003 -Message ("Service '{0}' not found: {1}" -f $ServiceName, $_.Exception.Message)
    exit 2
}

if (-not $RedisCliPath -or -not (Test-Path -LiteralPath $RedisCliPath -PathType Leaf)) {
    Write-WatchdogEvent -Level Error -EventId 3003 -Message 'redis-cli.exe not found. Pass -RedisCliPath.'
    exit 2
}

$state = Read-WatchdogState -Path $statePath

if ($service.Status -ne 'Running') {
    if ($service.Status -eq 'Stopped' -and $StartIfStopped) {
        try {
            Start-Service -Name $ServiceName
            Write-WatchdogEvent -Level Information -EventId 1002 -Message 'Service was stopped; started it (-StartIfStopped).'
        } catch {
            Write-WatchdogEvent -Level Error -EventId 3002 -Message ("Service was stopped and could not be started: {0}" -f $_.Exception.Message)
        }
    }
    # Stopped on purpose, or starting/stopping: do not probe, do not count.
    $state.ConsecutiveFailures = 0
    $state.AwaitingRecovery = $false
    Save-WatchdogState -Path $statePath -State $state
    exit 0
}

$result = Invoke-RedisPing -CliPath $RedisCliPath -TargetHost $HostName -TargetPort $Port -AuthSecret $password -AclUser $User -TimeoutSeconds $ProbeTimeoutSeconds

if ($result.Ok) {
    if ($state.AwaitingRecovery) {
        Write-WatchdogEvent -Level Information -EventId 1000 -Message 'Redis answers PONG again after the watchdog restart.'
    } elseif ($state.ConsecutiveFailures -gt 0) {
        Write-WatchdogEvent -Level Information -EventId 1000 -Message ("Redis answers PONG again after {0} failed probe(s)." -f $state.ConsecutiveFailures)
    }
    $state.ConsecutiveFailures = 0
    $state.AwaitingRecovery = $false
    Save-WatchdogState -Path $statePath -State $state
    exit 0
}

$state.ConsecutiveFailures++
Write-WatchdogEvent -Level Warning -EventId 2001 -Message ("Health probe failed ({0}/{1}) on {2}:{3}: {4}" -f $state.ConsecutiveFailures, $FailureThreshold, $HostName, $Port, $result.Reason)

if ($state.ConsecutiveFailures -lt $FailureThreshold) {
    Save-WatchdogState -Path $statePath -State $state
    exit 1
}

$nowTicks = [DateTime]::UtcNow.Ticks
if ($state.LastRestartTicksUtc -gt 0 -and
    [TimeSpan]::FromTicks($nowTicks - $state.LastRestartTicksUtc).TotalMinutes -lt $GraceMinutes) {
    Write-WatchdogEvent -Level Warning -EventId 2002 -Message ("Failure threshold reached, but the last restart was less than {0} minute(s) ago; not restarting yet." -f $GraceMinutes)
    Save-WatchdogState -Path $statePath -State $state
    exit 1
}

Write-WatchdogEvent -Level Error -EventId 3001 -Message ("{0} consecutive failed probes; restarting the service. Last reason: {1}" -f $state.ConsecutiveFailures, $result.Reason)
$state.LastRestartTicksUtc = $nowTicks
$state.ConsecutiveFailures = 0
$state.AwaitingRecovery = $true
Save-WatchdogState -Path $statePath -State $state

try {
    Restart-Service -Name $ServiceName -Force
    $service.Refresh()
    Write-WatchdogEvent -Level Information -EventId 1001 -Message ("Service restarted by the watchdog; status is now {0}." -f $service.Status)
} catch {
    Write-WatchdogEvent -Level Error -EventId 3002 -Message ("Restart-Service failed: {0}" -f $_.Exception.Message)
}
exit 1
