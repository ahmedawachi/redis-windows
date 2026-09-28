<#
.SYNOPSIS
  Diagnostic: how redis-server writes its data files when the service runs as a virtual account.

.DESCRIPTION
  Installs the -with-Service package as NT SERVICE\<name> services in several layouts and, for each one,
  reports the SAVE and BGSAVE outcome, the Redis log, the redis-server token's user, owner and primary
  group, and the owner and ACL of every file and folder Redis created. Used by windows-probe.yml; it
  reports and always exits 0, so it never decides a release.

  Layouts:
    shipped      the package as released, data folder on another path of the same drive
    fstab-noacl  the same, plus an etc\fstab next to the package folder that mounts drives with noacl
    full-control the same as shipped, with the data folder granted Full Control instead of Modify
    under-root   the data folder inside the folder that holds the package (the runtime's "/" mount)

.PARAMETER PackageDir
  The unpacked <dist>-with-Service folder.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$PackageDir = (Resolve-Path -LiteralPath $PackageDir).Path
$pkgName = Split-Path -Leaf $PackageDir
$cli = Join-Path $PackageDir 'redis-cli.exe'
$suffix = -join ((48..57) + (97..122) | Get-Random -Count 5 | ForEach-Object { [char]$_ })
$basePort = Get-Random -Minimum 41000 -Maximum 48000
$top = "C:\rwp-$suffix"
$services = [System.Collections.Generic.List[object]]::new()
$summary = [System.Collections.Generic.List[string]]::new()

Add-Type -Namespace RedisProbe -Name Token -UsingNamespace System.Security.Principal -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
[DllImport("kernel32.dll")]
static extern bool CloseHandle(IntPtr h);
[DllImport("advapi32.dll", SetLastError = true)]
static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
[DllImport("advapi32.dll", SetLastError = true)]
static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buffer, int length, out int returned);

static string Name(SecurityIdentifier sid) {
    try { return sid.Translate(typeof(NTAccount)).Value + " (" + sid.Value + ")"; }
    catch { return sid.Value; }
}

// TokenUser = 1, TokenOwner = 4, TokenPrimaryGroup = 5: each structure starts with the SID pointer.
public static string Describe(int pid) {
    IntPtr process = OpenProcess(0x1000, false, pid);
    if (process == IntPtr.Zero) return "OpenProcess failed: " + Marshal.GetLastWin32Error();
    IntPtr token;
    try {
        if (!OpenProcessToken(process, 0x0008, out token)) return "OpenProcessToken failed: " + Marshal.GetLastWin32Error();
    } finally { CloseHandle(process); }
    try {
        var parts = new System.Collections.Generic.List<string>();
        foreach (var entry in new[] { new { Cls = 1, Label = "user" }, new { Cls = 4, Label = "owner" }, new { Cls = 5, Label = "primary group" } }) {
            int length;
            GetTokenInformation(token, entry.Cls, IntPtr.Zero, 0, out length);
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try {
                if (!GetTokenInformation(token, entry.Cls, buffer, length, out length)) { parts.Add(entry.Label + ": error " + Marshal.GetLastWin32Error()); continue; }
                parts.Add(entry.Label + ": " + Name(new SecurityIdentifier(Marshal.ReadIntPtr(buffer))));
            } finally { Marshal.FreeHGlobal(buffer); }
        }
        return string.Join("; ", parts);
    } finally { CloseHandle(token); }
}
'@

function Invoke-Cli([int]$port, [string[]]$arguments) {
    ((& $cli -h 127.0.0.1 -p $port @arguments 2>&1) | Out-String).Trim()
}

function Get-InfoField([int]$port, [string]$field) {
    $line = (Invoke-Cli $port @('INFO', 'persistence')) -split "`r?`n" | Where-Object { $_ -like "${field}:*" } | Select-Object -First 1
    if ($line) { return $line.Substring($field.Length + 1).Trim() }
    return '?'
}

function Show-Security([string]$path) {
    try {
        $acl = Get-Acl -LiteralPath $path
        Write-Host "  $path"
        Write-Host "    owner $($acl.Owner), group $($acl.Group)"
        Write-Host "    $($acl.Sddl)"
        foreach ($a in $acl.Access) {
            Write-Host ("    {0} {1}: {2}{3}" -f $a.AccessControlType, $a.IdentityReference, $a.FileSystemRights, $(if ($a.IsInherited) { ' (inherited)' } else { '' }))
        }
    } catch { Write-Host "  $path : $_" }
}

function Get-RedisPid([string]$name) {
    $s = Get-CimInstance Win32_Service -Filter "Name='$name'"
    if ($null -eq $s -or $s.ProcessId -eq 0) { return 0 }
    $p = Get-CimInstance Win32_Process -Filter "Name='redis-server.exe' AND ParentProcessId=$($s.ProcessId)" | Select-Object -First 1
    if ($null -eq $p) { return 0 }
    return [int]$p.ProcessId
}

function Invoke-Layout([string]$layout, [int]$index) {
    Write-Host "`n=== $layout" -ForegroundColor Cyan
    $root = Join-Path $top $layout
    $pkg = Join-Path $root $pkgName
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    Copy-Item -Recurse -LiteralPath $PackageDir -Destination $pkg
    $data = if ($layout -eq 'under-root') { Join-Path $root 'data' } else { Join-Path $env:ProgramData "rwp-$suffix $layout\data dir" }
    New-Item -ItemType Directory -Force -Path $data | Out-Null
    $services.Add([pscustomobject]@{ Name = "RedisProbe$index$suffix"; Exe = (Join-Path $pkg 'RedisService.exe'); Data = $data })
    $name = "RedisProbe$index$suffix"
    $port = $basePort + $index
    $fwd = $data -replace '\\', '/'
    $conf = Join-Path $data 'redis.conf'
    @(
        "port $port"
        'bind 127.0.0.1'
        "dir `"$fwd`""
        "logfile `"$fwd/redis.log`""
        'save ""'
        'appendonly no'
    ) | Set-Content -Encoding ascii -LiteralPath $conf

    & (Join-Path $pkg 'RedisService.exe') install --service-name $name -c $conf --virtual-account --start-mode manual | ForEach-Object { Write-Host "  $_" }
    if ($LASTEXITCODE -ne 0) { throw "install failed with exit code $LASTEXITCODE" }

    if ($layout -eq 'fstab-noacl') {
        New-Item -ItemType Directory -Force -Path (Join-Path $root 'etc') | Out-Null
        'none /cygdrive cygdrive binary,noacl,posix=0,user 0 0' | Set-Content -Encoding ascii -LiteralPath (Join-Path $root 'etc\fstab')
        & icacls.exe $root /grant "NT SERVICE\${name}:(OI)(CI)RX" | Out-Null
    }
    if ($layout -eq 'full-control') {
        & icacls.exe $data /grant "NT SERVICE\${name}:(OI)(CI)F" | Out-Null
    }

    & sc.exe start $name | Out-Null
    (Get-Service -Name $name).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
    $pong = ''
    for ($i = 0; $i -lt 60 -and $pong -ne 'PONG'; $i++) { Start-Sleep -Milliseconds 500; $pong = Invoke-Cli $port @('PING') }
    Write-Host "  PING: $pong"
    $redisPid = Get-RedisPid $name
    Write-Host "  token: $([RedisProbe.Token]::Describe($redisPid))"

    Write-Host "  SET: $(Invoke-Cli $port @('SET', 'probe', 'value'))"
    $save = Invoke-Cli $port @('SAVE')
    Write-Host "  SAVE: $save"
    $bgsave = Invoke-Cli $port @('BGSAVE')
    for ($i = 0; $i -lt 40 -and (Get-InfoField $port 'rdb_bgsave_in_progress') -ne '0'; $i++) { Start-Sleep -Milliseconds 250 }
    $bgStatus = Get-InfoField $port 'rdb_last_bgsave_status'
    Write-Host "  BGSAVE: $bgsave -> $bgStatus"
    Write-Host "  CONFIG SET appendonly yes: $(Invoke-Cli $port @('CONFIG', 'SET', 'appendonly', 'yes'))"
    for ($i = 0; $i -lt 40 -and (Get-InfoField $port 'aof_rewrite_in_progress') -ne '0'; $i++) { Start-Sleep -Milliseconds 250 }
    Write-Host "  AOF rewrite: $(Get-InfoField $port 'aof_last_bgrewrite_status'), aof_enabled $(Get-InfoField $port 'aof_enabled')"

    Write-Host '  --- files'
    Show-Security $data
    Get-ChildItem -LiteralPath $data -Recurse -Force | ForEach-Object { Show-Security $_.FullName }
    Write-Host '  --- redis.log'
    Get-Content -LiteralPath (Join-Path $data 'redis.log') -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" }

    & sc.exe stop $name | Out-Null
    try { (Get-Service -Name $name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) } catch { Write-Host "  stop: $_" }
    $dump = Test-Path -LiteralPath (Join-Path $data 'dump.rdb')
    $summary.Add("| $layout | $save | $bgStatus | $dump |")
}

try {
    $layouts = @('shipped', 'fstab-noacl', 'full-control', 'under-root')
    for ($i = 0; $i -lt $layouts.Count; $i++) {
        try { Invoke-Layout $layouts[$i] $i }
        catch {
            Write-Host "  $($layouts[$i]) did not complete: $_" -ForegroundColor Yellow
            $summary.Add("| $($layouts[$i]) | did not complete: $($_.ToString() -replace '\|', '/') | | |")
        }
    }
} finally {
    foreach ($s in $services) {
        & $s.Exe uninstall --service-name $s.Name --stop-timeout 60s 2>&1 | ForEach-Object { Write-Host "  $_" }
        if ($null -ne (Get-Service -Name $s.Name -ErrorAction SilentlyContinue)) {
            & sc.exe stop $s.Name 2>&1 | Out-Null
            Start-Sleep -Seconds 2
            & sc.exe delete $s.Name 2>&1 | Out-Null
        }
        Remove-Item -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\$($s.Name)" -Recurse -ErrorAction SilentlyContinue
    }
    Get-CimInstance Win32_Process -Filter "Name='redis-server.exe' OR Name='RedisService.exe'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath -like "$top\*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    foreach ($s in $services) { Remove-Item -LiteralPath (Split-Path -Parent $s.Data) -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $top -Recurse -Force -ErrorAction SilentlyContinue
}

$lines = @('', '| Layout | SAVE | BGSAVE | dump.rdb after stop |', '|---|---|---|---|') + $summary
$lines | ForEach-Object { Write-Host $_ }
if ($env:GITHUB_STEP_SUMMARY) { (@("#### Virtual-account data writes ($env:TOOLCHAIN)") + $lines) | Out-File -Append -Encoding utf8 $env:GITHUB_STEP_SUMMARY }
exit 0
