<#
.SYNOPSIS
    Collects READ-ONLY host evidence for a Redis-on-Windows incident into a
    timestamped folder and a zip file.

.DESCRIPTION
    Use after an out-of-memory abort, a crash, a hang or an unexplained
    restart of a Redis service built with Cygwin/MSYS2. It answers the first
    question of every such incident: did the whole host run out of commit
    (RAM + pagefile), or did Redis itself grow?

    The script only READS system state. It changes no setting, service,
    registry value, Defender preference or file outside its own output
    folder. Run it from an elevated PowerShell for complete results; without
    admin rights some sections (Security-sensitive events, Defender
    exclusions, other users' processes) come back partial, and the summary
    says which.

    Collected:
      - System and Application events between -From and -To from:
        Microsoft-Windows-Resource-Exhaustion-Detector (Event 2004 names the
        processes that used the most commit), -Resolver, Application Popup
        (Event 26), Service Control Manager, RedisService (stock wrapper), the
        service name (this fork's wrapper logs under it), RedisWatchdog, Application Error / Windows Error Reporting /
        .NET Runtime, and system restart/shutdown events.
      - Pagefile configuration and usage, disk free space.
      - A commit snapshot (Committed Bytes, Commit Limit, % in use) from WMI
        (language independent) and from Get-Counter (English counter names).
      - Processes: PrivateMemorySize64, WorkingSet64, peak private commit for
        redis-server, w3wp and dotnet (or -ProcessName), plus the 30 largest
        processes by private bytes.
      - sc qc / qfailure / qfailureflag / qsidtype / qdescription / queryex for
        -ServiceName, its registry key, WaitToKillServiceTimeout, and file
        versions and SHA256 hashes of the binaries next to the service exe.
      - Microsoft Defender status and preferences.
      - The service's redis.conf with requirepass, masterauth, TLS key
        passwords and ACL user passwords replaced by REDACTED.
      - Optionally (-IncludeRedisLogTail) the last lines of the Redis log.
        Off by default: the log can contain key names from crash reports.

.PARAMETER From
    Start of the event time window. Default: 24 hours ago.

.PARAMETER To
    End of the event time window. Default: now.

.PARAMETER ServiceName
    Redis Windows service name. Default: Redis.

.PARAMETER RedisConfigPath
    redis.conf to include (redacted). Default: the -c / --config option in
    the service's command line.

.PARAMETER ProcessName
    Processes to report in detail. Default: redis-server, w3wp, dotnet.

.PARAMETER OutputRoot
    Folder in which the evidence folder and zip are created. Default: the
    current folder, or %TEMP% when the current folder is under the Windows
    directory.

.PARAMETER IncludeRedisLogTail
    Also copy the last -LogTailLines lines of the Redis log file.

.PARAMETER LogTailLines
    Number of log lines for -IncludeRedisLogTail. Default: 3000.

.PARAMETER NoZip
    Leave the folder unzipped.

.EXAMPLE
    .\collect-host-evidence.ps1 -From '2026-01-15 10:30' -To '2026-01-15 10:45' -ServiceName Redis

.EXAMPLE
    .\collect-host-evidence.ps1 -ServiceName Redis -IncludeRedisLogTail -OutputRoot D:\Evidence
#>
[CmdletBinding()]
param(
    [datetime]$From = (Get-Date).AddHours(-24),
    [datetime]$To = (Get-Date),
    [string]$ServiceName = 'Redis',
    [string]$RedisConfigPath,
    [string[]]$ProcessName = @('redis-server', 'w3wp', 'dotnet'),
    [string]$OutputRoot,
    [switch]$IncludeRedisLogTail,
    [ValidateRange(1, 1000000)]
    [int]$LogTailLines = 3000,
    [switch]$NoZip
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ($From -ge $To) {
    throw "-From ($From) must be earlier than -To ($To)."
}

# ----------------------------------------------------------------------------
# Output folder and helpers
# ----------------------------------------------------------------------------

if (-not $OutputRoot) {
    $OutputRoot = (Get-Location).ProviderPath
    if ($env:windir -and $OutputRoot.StartsWith($env:windir, [StringComparison]::OrdinalIgnoreCase)) {
        $OutputRoot = [System.IO.Path]::GetTempPath()
    }
}
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir = Join-Path $OutputRoot ('redis-host-evidence-{0}' -f $stamp)
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$script:Problems = New-Object System.Collections.Generic.List[string]

function Save-Text {
    param([string]$Name, $Content)
    $path = Join-Path $outDir $Name
    ($Content | Out-String -Width 4096) | Out-File -LiteralPath $path -Encoding UTF8 -Width 4096
}

function Save-Csv {
    param([string]$Name, $Rows)
    $path = Join-Path $outDir $Name
    if ($null -eq $Rows -or @($Rows).Count -eq 0) {
        '(no rows)' | Out-File -LiteralPath $path -Encoding UTF8
        return
    }
    $Rows | Export-Csv -LiteralPath $path -NoTypeInformation -Encoding UTF8
}

function Invoke-Section {
    # Runs one collection step; a failure is recorded and the script goes on.
    param([string]$Name, [scriptblock]$Body)
    Write-Host ("Collecting: {0}" -f $Name)
    try {
        & $Body
    } catch {
        $msg = '{0}: {1}' -f $Name, $_.Exception.Message
        $script:Problems.Add($msg)
        Write-Warning $msg
    }
}

function Get-RedactedText {
    # Removes secrets from redis.conf lines and from command lines.
    param([string[]]$Lines)
    $secretDirectives = 'requirepass|masterauth|tls-key-file-pass|tls-client-key-file-pass'
    foreach ($line in $Lines) {
        $out = $line
        # Directive lines, also when commented out: an old real password is
        # often left behind as a comment.
        $out = [regex]::Replace($out, '^(\s*#?\s*(?:' + $secretDirectives + ')\s+)\S.*$', '${1}REDACTED', 'IgnoreCase')
        # Inline ACL users: >plaintext, <plaintext, #sha256 and !sha256 tokens.
        if ($out -match '^\s*#?\s*user\s+\S+') {
            $out = [regex]::Replace($out, '(?<=\s)([<>#!])\S+', '${1}REDACTED')
        }
        # Command-line forms: --requirepass x, --masterauth x, redis-cli -a x.
        $out = [regex]::Replace($out, '(--(?:' + $secretDirectives + ')(?:\s+|=))("[^"]*"|\S+)', '${1}REDACTED', 'IgnoreCase')
        $out = [regex]::Replace($out, '((?:^|\s)-a\s+)("[^"]*"|\S+)', '${1}REDACTED')
        $out
    }
}

function Get-OptionFromCommandLine {
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

function ConvertTo-WindowsPath {
    # /cygdrive/c/x -> C:\x ; C:/x -> C:\x ; anything else unchanged.
    param([string]$Path)
    if (-not $Path) { return $Path }
    $p = $Path.Trim().Trim('"')
    $m = [regex]::Match($p, '^/cygdrive/([A-Za-z])(/.*)?$')
    if ($m.Success) {
        $rest = ''
        if ($m.Groups[2].Success) { $rest = $m.Groups[2].Value }
        return ('{0}:{1}' -f $m.Groups[1].Value.ToUpperInvariant(), $rest.Replace('/', '\'))
    }
    if ($p -match '^[A-Za-z]:/') { return $p.Replace('/', '\') }
    return $p
}

function Export-EventSet {
    # Queries one event set; writes <label>.csv and, when asked, <label>.xml.
    param(
        [string]$Label,
        [hashtable]$Filter,
        [switch]$WithXml
    )
    $f = $Filter.Clone()
    $f['StartTime'] = $From
    $f['EndTime'] = $To
    $events = @()
    try {
        $events = @(Get-WinEvent -FilterHashtable $f -MaxEvents 5000 -ErrorAction Stop)
    } catch {
        $id = [string]$_.FullyQualifiedErrorId
        if ($id -like 'NoMatchingEventsFound*') {
            $events = @()
        } elseif ($id -like 'NoMatchingProvidersFound*' -or $id -like 'NoMatchingLogsFound*') {
            Save-Text ('events-{0}.csv' -f $Label) '(provider or log not registered on this host)'
            return @()
        } else {
            throw
        }
    }
    $rows = foreach ($e in $events) {
        $message = $null
        try { $message = $e.FormatDescription() } catch { $message = $null }
        if (-not $message) { $message = ($e.Properties | ForEach-Object { $_.Value }) -join ' | ' }
        [pscustomobject]@{
            TimeCreated  = $e.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss.fff')
            LogName      = $e.LogName
            ProviderName = $e.ProviderName
            Id           = $e.Id
            Level        = $e.LevelDisplayName
            Message      = (($message -replace '\s+', ' ').Trim())
        }
    }
    Save-Csv ('events-{0}.csv' -f $Label) $rows
    if ($WithXml -and $events.Count -gt 0) {
        Save-Text ('events-{0}.xml' -f $Label) (($events | ForEach-Object { $_.ToXml() }) -join [Environment]::NewLine)
    }
    return @($rows)
}

$isAdmin = $false
try {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
} catch {
    $script:Problems.Add('Could not determine whether the session is elevated.')
}

# ----------------------------------------------------------------------------
# Service facts first: they tell us where the config and binaries are
# ----------------------------------------------------------------------------

$serviceInfo = $null
Invoke-Section 'service' {
    $script:serviceInfo = Get-CimInstance -ClassName Win32_Service -Filter ("Name='{0}'" -f $ServiceName.Replace("'", "''"))
    if (-not $script:serviceInfo) {
        Save-Text 'service.txt' ("Service '{0}' not found." -f $ServiceName)
        return
    }
    $svcText = New-Object System.Text.StringBuilder
    [void]$svcText.AppendLine(($script:serviceInfo |
        Select-Object Name, DisplayName, State, Status, StartMode, DelayedAutoStart, StartName, ProcessId, ExitCode, ServiceSpecificExitCode, PathName |
        Format-List | Out-String -Width 4096))
    foreach ($verb in @('qc', 'qfailure', 'qfailureflag', 'qsidtype', 'qdescription', 'queryex')) {
        [void]$svcText.AppendLine(('==== sc.exe {0} {1}' -f $verb, $ServiceName))
        [void]$svcText.AppendLine((& sc.exe $verb $ServiceName 2>&1 | Out-String -Width 4096))
    }
    $regPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\{0}' -f $ServiceName
    if (Test-Path -LiteralPath $regPath) {
        [void]$svcText.AppendLine('==== Registry ' + $regPath)
        [void]$svcText.AppendLine((Get-ItemProperty -LiteralPath $regPath |
            Select-Object * -ExcludeProperty PSPath, PSParentPath, PSChildName, PSDrive, PSProvider, FailureActions |
            Format-List | Out-String -Width 4096))
        $paramPath = Join-Path $regPath 'Parameters'
        if (Test-Path -LiteralPath $paramPath) {
            [void]$svcText.AppendLine('==== Registry ' + $paramPath)
            $paramProps = Get-ItemProperty -LiteralPath $paramPath
            [void]$svcText.AppendLine(($paramProps |
                Select-Object * -ExcludeProperty PSPath, PSParentPath, PSChildName, PSDrive, PSProvider, Arguments |
                Format-List | Out-String -Width 4096))
            # Format-List would cut this REG_MULTI_SZ after $FormatEnumerationLimit
            # (4) elements, so write every element explicitly.
            if ($paramProps.PSObject.Properties['Arguments']) {
                $storedArgv = @($paramProps.Arguments)
                [void]$svcText.AppendLine(('Arguments ({0} elements, REG_MULTI_SZ):' -f $storedArgv.Count))
                for ($i = 0; $i -lt $storedArgv.Count; $i++) {
                    [void]$svcText.AppendLine(('  [{0}] {1}' -f $i, $storedArgv[$i]))
                }
                $joined = ($storedArgv | ForEach-Object {
                    if ([string]$_ -match '\s') { '"' + $_ + '"' } else { [string]$_ }
                }) -join ' '
                [void]$svcText.AppendLine(('Arguments (joined): {0}' -f $joined))
            }
        }
    }
    Save-Text 'service.txt' ((Get-RedactedText -Lines ($svcText.ToString() -split "`r?`n")) -join [Environment]::NewLine)
}

$serviceExe = $null
if ($serviceInfo -and $serviceInfo.PathName) {
    $m = [regex]::Match([string]$serviceInfo.PathName, '^\s*"([^"]+)"')
    if ($m.Success) { $serviceExe = $m.Groups[1].Value }
    else { $serviceExe = ([string]$serviceInfo.PathName -split '\s+')[0] }
    if (-not $RedisConfigPath) {
        $c = Get-OptionFromCommandLine -CommandLine ([string]$serviceInfo.PathName) -Names @('-c', '--config')
        if ($c) { $RedisConfigPath = ConvertTo-WindowsPath $c }
    }
}

if (-not $RedisConfigPath) {
    # This fork's RedisService.exe keeps its options under
    # Services\<name>\Parameters, value "Arguments" (REG_MULTI_SZ), and only
    # "run --service-name <name>" in ImagePath.
    $paramKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\{0}\Parameters' -f $ServiceName
    try {
        if (Test-Path -LiteralPath $paramKey) {
            $argv = @((Get-ItemProperty -LiteralPath $paramKey -Name Arguments -ErrorAction Stop).Arguments)
            for ($i = 0; $i -lt $argv.Count; $i++) {
                if (($argv[$i] -eq '-c' -or $argv[$i] -eq '--config') -and ($i + 1) -lt $argv.Count) {
                    $RedisConfigPath = ConvertTo-WindowsPath $argv[$i + 1]
                } elseif ($argv[$i] -like '--config=*') {
                    $RedisConfigPath = ConvertTo-WindowsPath $argv[$i].Substring(9)
                }
            }
        }
    } catch {
        $script:Problems.Add(('Could not read {0}: {1}' -f $paramKey, $_.Exception.Message))
    }
}
if (-not $RedisConfigPath -and $serviceExe) {
    # Common layout (and this fork's default): redis.conf next to RedisService.exe.
    $candidate = Join-Path (Split-Path -Parent $serviceExe) 'redis.conf'
    if (Test-Path -LiteralPath $candidate) { $RedisConfigPath = $candidate }
}

Invoke-Section 'binaries' {
    if (-not $serviceExe -or -not (Test-Path -LiteralPath $serviceExe)) {
        Save-Text 'binaries.txt' '(service executable not found; skipped)'
        return
    }
    $dir = Split-Path -Parent $serviceExe
    $names = @('RedisService.exe', 'redis-server.exe', 'redis-cli.exe', 'msys-2.0.dll', 'cygwin1.dll',
        'msys-ssl-3.dll', 'msys-crypto-3.dll', 'cygssl-3.dll', 'cygcrypto-3.dll')
    $rows = foreach ($n in $names) {
        $p = Join-Path $dir $n
        if (Test-Path -LiteralPath $p) {
            $fi = Get-Item -LiteralPath $p
            [pscustomobject]@{
                File           = $n
                Length         = $fi.Length
                LastWriteTime  = $fi.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')
                FileVersion    = $fi.VersionInfo.FileVersion
                ProductVersion = $fi.VersionInfo.ProductVersion
                SHA256         = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash
            }
        }
    }
    Save-Csv 'binaries.csv' $rows

    $server = Join-Path $dir 'redis-server.exe'
    if (Test-Path -LiteralPath $server) {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $server
        $psi.Arguments = '--version'
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $proc = [System.Diagnostics.Process]::Start($psi)
        $outTask = $proc.StandardOutput.ReadToEndAsync()
        if ($proc.WaitForExit(15000)) {
            Save-Text 'redis-server-version.txt' $outTask.Result
        } else {
            try { $proc.Kill() } catch { Write-Verbose 'already exited' }
            Save-Text 'redis-server-version.txt' '(redis-server --version did not return within 15 s)'
        }
        $proc.Dispose()
    }
}

# ----------------------------------------------------------------------------
# System, pagefile, commit
# ----------------------------------------------------------------------------

Invoke-Section 'system' {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    $cs = Get-CimInstance -ClassName Win32_ComputerSystem
    $info = [ordered]@{
        CollectedAt                   = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')
        WindowStart                   = $From.ToString('yyyy-MM-dd HH:mm:ss')
        WindowEnd                     = $To.ToString('yyyy-MM-dd HH:mm:ss')
        Elevated                      = $isAdmin
        OSCaption                     = $os.Caption
        OSVersion                     = $os.Version
        OSBuild                       = $os.BuildNumber
        LastBootUpTime                = $os.LastBootUpTime
        TotalPhysicalMemoryMB         = [math]::Round($cs.TotalPhysicalMemory / 1MB)
        FreePhysicalMemoryMB          = [math]::Round($os.FreePhysicalMemory / 1KB)
        CommitLimitMB_TotalVirtual    = [math]::Round($os.TotalVirtualMemorySize / 1KB)
        CommitAvailableMB_FreeVirtual = [math]::Round($os.FreeVirtualMemory / 1KB)
        AutomaticManagedPagefile      = $cs.AutomaticManagedPagefile
        LogicalProcessors             = $cs.NumberOfLogicalProcessors
        PowerShellVersion             = $PSVersionTable.PSVersion.ToString()
    }
    Save-Text 'system.txt' ([pscustomobject]$info | Format-List)
}

Invoke-Section 'pagefile' {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('==== Win32_PageFileUsage (sizes in MB)')
    [void]$sb.AppendLine((Get-CimInstance -ClassName Win32_PageFileUsage |
        Select-Object Name, AllocatedBaseSize, CurrentUsage, PeakUsage, TempPageFile, InstallDate |
        Format-List | Out-String -Width 4096))
    [void]$sb.AppendLine('==== Win32_PageFileSetting (sizes in MB; 0/0 = system managed)')
    [void]$sb.AppendLine((Get-CimInstance -ClassName Win32_PageFileSetting |
        Select-Object Name, InitialSize, MaximumSize | Format-List | Out-String -Width 4096))
    [void]$sb.AppendLine('==== AutomaticManagedPagefile')
    [void]$sb.AppendLine([string](Get-CimInstance -ClassName Win32_ComputerSystem).AutomaticManagedPagefile)
    $mm = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management'
    [void]$sb.AppendLine('==== ' + $mm)
    [void]$sb.AppendLine((Get-ItemProperty -LiteralPath $mm |
        Select-Object PagingFiles, ExistingPageFiles, TempPageFile, ClearPageFileAtShutdown | Format-List | Out-String -Width 4096))
    [void]$sb.AppendLine('==== Fixed disks')
    [void]$sb.AppendLine((Get-CimInstance -ClassName Win32_LogicalDisk -Filter 'DriveType=3' |
        Select-Object DeviceID, VolumeName,
            @{ n = 'SizeGB'; e = { [math]::Round($_.Size / 1GB, 1) } },
            @{ n = 'FreeGB'; e = { [math]::Round($_.FreeSpace / 1GB, 1) } } |
        Format-Table -AutoSize | Out-String -Width 4096))
    Save-Text 'pagefile.txt' $sb.ToString()
}

Invoke-Section 'commit (WMI)' {
    $mem = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfOS_Memory
    $pf = @(Get-CimInstance -ClassName Win32_PerfFormattedData_PerfOS_PagingFile)
    $row = [pscustomobject]@{
        Time                       = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        CommittedMB                = [math]::Round($mem.CommittedBytes / 1MB)
        CommitLimitMB              = [math]::Round($mem.CommitLimit / 1MB)
        PercentCommittedBytesInUse = $mem.PercentCommittedBytesInUse
        AvailableMB                = $mem.AvailableMBytes
        PoolNonpagedMB             = [math]::Round($mem.PoolNonpagedBytes / 1MB)
        PoolPagedMB                = [math]::Round($mem.PoolPagedBytes / 1MB)
        PagingFileUsagePercent     = (($pf | ForEach-Object { '{0}={1}%' -f $_.Name, $_.PercentUsage }) -join '; ')
        PagingFilePeakPercent      = (($pf | ForEach-Object { '{0}={1}%' -f $_.Name, $_.PercentUsagePeak }) -join '; ')
    }
    Save-Text 'commit-wmi.txt' ($row | Format-List)
}

Invoke-Section 'commit (Get-Counter, English counter names)' {
    $paths = @(
        '\Memory\Committed Bytes',
        '\Memory\Commit Limit',
        '\Memory\% Committed Bytes In Use',
        '\Memory\Available MBytes',
        '\Paging File(_Total)\% Usage'
    )
    $samples = Get-Counter -Counter $paths -SampleInterval 1 -MaxSamples 5
    $rows = foreach ($s in $samples) {
        foreach ($c in $s.CounterSamples) {
            [pscustomobject]@{
                Time    = $s.Timestamp.ToString('yyyy-MM-dd HH:mm:ss')
                Counter = $c.Path
                Value   = $c.CookedValue
            }
        }
    }
    Save-Csv 'commit-counters.csv' $rows
}

# ----------------------------------------------------------------------------
# Processes
# ----------------------------------------------------------------------------

Invoke-Section 'processes' {
    $procSelect = @(
        'Name', 'Id',
        @{ n = 'PrivateMB'; e = { [math]::Round($_.PrivateMemorySize64 / 1MB, 1) } },
        @{ n = 'WorkingSetMB'; e = { [math]::Round($_.WorkingSet64 / 1MB, 1) } },
        @{ n = 'PeakWorkingSetMB'; e = { [math]::Round($_.PeakWorkingSet64 / 1MB, 1) } },
        @{ n = 'PagedMB'; e = { [math]::Round($_.PagedMemorySize64 / 1MB, 1) } },
        @{ n = 'VirtualMB'; e = { [math]::Round($_.VirtualMemorySize64 / 1MB, 1) } },
        'HandleCount',
        @{ n = 'Threads'; e = { $_.Threads.Count } },
        @{ n = 'StartTime'; e = { try { $_.StartTime.ToString('yyyy-MM-dd HH:mm:ss') } catch { $null } } }
    )
    $named = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    Save-Csv 'processes-named.csv' ($named | Select-Object $procSelect)
    Save-Csv 'processes-top30-private.csv' (Get-Process | Sort-Object PrivateMemorySize64 -Descending | Select-Object -First 30 | Select-Object $procSelect)

    # Win32_Process adds the parent, the peak private commit (PeakPageFileUsage,
    # KB) and the command line (secrets redacted).
    $filter = (($ProcessName | ForEach-Object { "Name='{0}.exe'" -f $_.Replace("'", "''") }) -join ' OR ')
    $wmi = @(Get-CimInstance -ClassName Win32_Process -Filter $filter)
    $rows = foreach ($p in $wmi) {
        [pscustomobject]@{
            Name                = $p.Name
            ProcessId           = $p.ProcessId
            ParentProcessId     = $p.ParentProcessId
            PrivateCommitMB     = [math]::Round($p.PageFileUsage / 1KB, 1)
            PeakPrivateCommitMB = [math]::Round($p.PeakPageFileUsage / 1KB, 1)
            CreationDate        = $p.CreationDate
            CommandLine         = ((Get-RedactedText -Lines @([string]$p.CommandLine)) -join ' ')
        }
    }
    Save-Csv 'processes-named-wmi.csv' $rows
}

# ----------------------------------------------------------------------------
# Shutdown budget and Defender
# ----------------------------------------------------------------------------

Invoke-Section 'WaitToKillServiceTimeout' {
    $v = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control' -Name WaitToKillServiceTimeout -ErrorAction SilentlyContinue
    if ($v) { Save-Text 'shutdown-timeout.txt' ('WaitToKillServiceTimeout = {0} ms' -f $v.WaitToKillServiceTimeout) }
    else { Save-Text 'shutdown-timeout.txt' 'WaitToKillServiceTimeout not set (OS default applies).' }
}

Invoke-Section 'defender' {
    if (-not (Get-Command Get-MpComputerStatus -ErrorAction SilentlyContinue)) {
        Save-Text 'defender.txt' 'Defender cmdlets not available on this host.'
        return
    }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('==== Get-MpComputerStatus')
    [void]$sb.AppendLine((Get-MpComputerStatus | Format-List * | Out-String -Width 4096))
    [void]$sb.AppendLine('==== Get-MpPreference (exclusion lists need elevation to be readable)')
    [void]$sb.AppendLine((Get-MpPreference | Format-List * | Out-String -Width 4096))
    Save-Text 'defender.txt' $sb.ToString()
}

# ----------------------------------------------------------------------------
# Events
# ----------------------------------------------------------------------------

$allEvents = New-Object System.Collections.Generic.List[object]
$eventSets = @(
    @{ Label = 'resource-exhaustion-detector'; Filter = @{ ProviderName = 'Microsoft-Windows-Resource-Exhaustion-Detector' }; Xml = $true },
    @{ Label = 'resource-exhaustion-resolver'; Filter = @{ ProviderName = 'Microsoft-Windows-Resource-Exhaustion-Resolver' }; Xml = $true },
    @{ Label = 'application-popup'; Filter = @{ LogName = 'System'; ProviderName = 'Application Popup' }; Xml = $true },
    @{ Label = 'service-control-manager'; Filter = @{ LogName = 'System'; ProviderName = 'Service Control Manager' }; Xml = $false },
    @{ Label = 'system-restarts'; Filter = @{ LogName = 'System'; Id = @(41, 1074, 6005, 6006, 6008, 6009, 12, 13) }; Xml = $false },
    @{ Label = 'app-crashes'; Filter = @{ LogName = 'Application'; ProviderName = @('Application Error', 'Windows Error Reporting', 'Application Hang', '.NET Runtime') }; Xml = $false }
)
$redisSources = @('RedisService', 'RedisWatchdog')
if ($redisSources -notcontains $ServiceName) { $redisSources += $ServiceName }
foreach ($src in $redisSources) {
    $eventSets += @{ Label = ('app-{0}' -f ($src -replace '[^A-Za-z0-9_.-]', '_')); Filter = @{ LogName = 'Application'; ProviderName = $src }; Xml = $false }
}

foreach ($set in $eventSets) {
    $s = $set
    Invoke-Section ('events: {0}' -f $s.Label) {
        $rows = Export-EventSet -Label $s.Label -Filter $s.Filter -WithXml:([bool]$s.Xml)
        foreach ($r in @($rows)) { if ($r) { $allEvents.Add($r) } }
    }
}
Invoke-Section 'events: timeline' {
    Save-Csv 'events-timeline.csv' ($allEvents | Sort-Object TimeCreated)
}

# ----------------------------------------------------------------------------
# Redis config (redacted) and optional log tail
# ----------------------------------------------------------------------------

$logFileFromConfig = $null
Invoke-Section 'redis.conf (redacted)' {
    if (-not $RedisConfigPath -or -not (Test-Path -LiteralPath $RedisConfigPath -PathType Leaf)) {
        Save-Text 'redis.conf.redacted.txt' ("Config file not found: '{0}'. Pass -RedisConfigPath." -f $RedisConfigPath)
        return
    }
    $lines = [System.IO.File]::ReadAllLines($RedisConfigPath)
    $header = @(('# Source: ' + $RedisConfigPath), '# Secrets replaced by REDACTED by collect-host-evidence.ps1', '')
    Save-Text 'redis.conf.redacted.txt' (($header + (Get-RedactedText -Lines $lines)) -join [Environment]::NewLine)
    $includes = @($lines | Where-Object { $_ -match '^\s*include\s+' })
    if ($includes.Count -gt 0) {
        Save-Text 'redis.conf.includes.txt' ("Included files are NOT collected; review them by hand:`r`n" + ($includes -join "`r`n"))
    }
    foreach ($l in $lines) {
        $m = [regex]::Match($l, '^\s*logfile\s+"?([^"]*)"?\s*$', 'IgnoreCase')
        if ($m.Success) { $script:logFileFromConfig = ConvertTo-WindowsPath $m.Groups[1].Value }
    }
}

if ($IncludeRedisLogTail) {
    Invoke-Section 'redis log tail' {
        if (-not $script:logFileFromConfig) {
            Save-Text 'redis-log-tail.txt' 'No logfile directive found in the config (logging to stdout is lost under a service).'
            return
        }
        if (-not [System.IO.Path]::IsPathRooted($script:logFileFromConfig)) {
            Save-Text 'redis-log-tail.txt' ("logfile '{0}' is relative; copy it by hand." -f $script:logFileFromConfig)
            return
        }
        $tail = Get-Content -LiteralPath $script:logFileFromConfig -Tail $LogTailLines
        Save-Text 'redis-log-tail.txt' ((Get-RedactedText -Lines $tail) -join [Environment]::NewLine)
    }
}

# ----------------------------------------------------------------------------
# Summary and zip
# ----------------------------------------------------------------------------

$summary = New-Object System.Text.StringBuilder
[void]$summary.AppendLine('Redis host evidence (read-only collection)')
[void]$summary.AppendLine(('Collected : {0}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')))
[void]$summary.AppendLine(('Window    : {0} .. {1}' -f $From.ToString('yyyy-MM-dd HH:mm:ss'), $To.ToString('yyyy-MM-dd HH:mm:ss')))
[void]$summary.AppendLine(('Service   : {0}' -f $ServiceName))
[void]$summary.AppendLine(('Config    : {0}' -f $RedisConfigPath))
[void]$summary.AppendLine(('Elevated  : {0}' -f $isAdmin))
[void]$summary.AppendLine('')
[void]$summary.AppendLine('Where to look first:')
[void]$summary.AppendLine('  events-resource-exhaustion-detector.csv/.xml  Event 2004 = low virtual memory (commit); names the top consumers')
[void]$summary.AppendLine('  commit-wmi.txt, system.txt, pagefile.txt      commit limit vs committed now; pagefile size and disk space')
[void]$summary.AppendLine('  processes-*.csv                               private bytes / peak private commit per process')
[void]$summary.AppendLine('  events-app-<service name>.csv, events-app-RedisService.csv  wrapper events: this fork logs under the service name, the stock wrapper as RedisService; signal N shows up as exit code N*256')
[void]$summary.AppendLine('')
if ($script:Problems.Count -gt 0) {
    [void]$summary.AppendLine('Sections that failed or were partial:')
    foreach ($p in $script:Problems) { [void]$summary.AppendLine('  - ' + $p) }
} else {
    [void]$summary.AppendLine('All sections collected.')
}
if (-not $isAdmin) {
    [void]$summary.AppendLine('')
    [void]$summary.AppendLine('NOTE: not elevated - re-run from an elevated PowerShell for complete results.')
}
Save-Text '00-summary.txt' $summary.ToString()

Write-Warning 'Review the files before sharing them outside your organisation (host name, process command lines).'
Write-Output ("Evidence folder: {0}" -f $outDir)
if (-not $NoZip) {
    $zip = $outDir + '.zip'
    Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zip -Force
    Write-Output ("Evidence zip   : {0}" -f $zip)
}
