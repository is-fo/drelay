# ============================================================================
#  Set-AddressClaim.ps1
# ============================================================================
# Claims (or releases) the game servers' addresses on the loopback pseudo-interface, so the game
# client's own dial to those addresses is delivered into the local stack, where the relay listens.
#
# This replaces the WinDivert packet redirect for game traffic: no driver, no packet rewriting, no
# hosts entry. The addresses come from the same relay-routes.json the relay reads (routes[].remoteHost).
#
# WHY LOOPBACK
#   Windows delivers a connection to a local address locally only across the loopback boundary.
#   Measured on this machine: a claim on a physical NIC did NOT escape a dial from another NIC on the
#   same subnet - the dial was delivered locally, and the relay then accepted its own dials and looped
#   (hundreds of sessions per second). networking.Relay therefore refuses to start on such a layout,
#   and this script only ever claims on loopback. It also refuses to touch an address that a physical
#   NIC already owns, because that is how a machine loses its network.
#
# PERSISTENCE
#   Claims go into both policy stores by default, so they survive a reboot. Use -SessionOnly to keep
#   them out of the persistent store (a reboot clears them), and -Release when you are done. While an
#   address is claimed and the relay is NOT running, connections to that address fail locally instead
#   of reaching the real server - so release the claim when you stop.
#
# Powershell 5.1 cannot be given $PSStyle, so keep this file free of 7-only syntax: it runs under
# whichever shell is present, and the launcher invokes it with -NoProfile -ExecutionPolicy Bypass.
#
# Usage (elevated for claim/release; -Status runs unelevated):
#   .\tools\Set-AddressClaim.ps1                      # claim every route's address
#   .\tools\Set-AddressClaim.ps1 -Status              # what is claimed right now
#   .\tools\Set-AddressClaim.ps1 -Release             # give the addresses back
#   .\tools\Set-AddressClaim.ps1 -Route Game -SessionOnly
#   .\tools\Set-AddressClaim.ps1 -SkipVerify
#
# Exit codes: 0 = as asked, 1 = a claim could not be verified, 2 = configuration/usage problem,
#             3 = refused (an address belongs to a real interface).
# ============================================================================

[CmdletBinding()]
param(
    [string]$Config,
    [string]$Route,
    [switch]$Release,
    [switch]$Status,
    [switch]$SessionOnly,
    [switch]$SkipVerify,
    [int]$PreferredTimeoutSeconds = 15,
    [int]$VerifyTimeoutSeconds = 3
)

$ErrorActionPreference = 'Stop'

if (-not $Config) { $Config = Join-Path (Split-Path -Parent $PSScriptRoot) 'relay-routes.json' }

function Write-Step { param([string]$Text) Write-Host $Text -ForegroundColor Cyan }
function Write-Ok   { param([string]$Text) Write-Host $Text -ForegroundColor Green }
function Write-Bad  { param([string]$Text) Write-Host $Text -ForegroundColor Red }
function Write-Warn2 { param([string]$Text) Write-Host $Text -ForegroundColor Yellow }
function Write-Note { param([string]$Text) Write-Host $Text -ForegroundColor DarkGray }

function Get-Loopback {
    $lb = Get-NetIPInterface -AddressFamily IPv4 | Where-Object { $_.InterfaceIndex -eq 1 } | Select-Object -First 1
    if (-not $lb) {
        $lb = Get-NetIPInterface -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -like 'Loopback*' } | Select-Object -First 1
    }
    return $lb
}

function Get-Assigned([string]$Ip) {
    Get-NetIPAddress -AddressFamily IPv4 -IPAddress $Ip -ErrorAction SilentlyContinue | Select-Object -First 1
}

# Addresses the relay claimed at runtime because a GmReconnect named them (a realm address is not in
# the route table, so it has to be tracked separately). The relay releases its own on exit; this
# covers a relay that was killed.
function Get-LearnedClaimsFile {
    return (Join-Path (Split-Path -Parent $PSScriptRoot) 'work\learned-claims.txt')
}

function Get-LearnedClaims {
    $file = Get-LearnedClaimsFile
    if (-not (Test-Path -LiteralPath $file)) { return @() }
    return @(Get-Content -LiteralPath $file -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -match '^\d{1,3}(\.\d{1,3}){3}$' } |
        Select-Object -Unique)
}

function Test-IPv4Literal([string]$Value) {
    $parsed = $null
    return [System.Net.IPAddress]::TryParse($Value, [ref]$parsed) -and
           $parsed.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork
}

function Wait-Preferred([string]$Ip, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $state = 'absent'
    while ((Get-Date) -lt $deadline) {
        $a = Get-Assigned $Ip
        if ($a) {
            $state = $a.AddressState
            if ($state -eq 'Preferred') { return $a }
        }
        Start-Sleep -Milliseconds 250
    }
    return $state            # a string: 'Tentative' / 'Duplicate' / 'absent'
}

function Add-Claim([string]$Ip, [string]$Alias, [int]$Index, [bool]$SessionOnly) {
    $why = New-Object System.Collections.ArrayList
    if ($SessionOnly) {
        try {
            New-NetIPAddress -IPAddress $Ip -PrefixLength 32 -InterfaceIndex $Index -PolicyStore ActiveStore -ErrorAction Stop | Out-Null
            return 'New-NetIPAddress (ActiveStore only)'
        } catch { [void]$why.Add("ActiveStore: $($_.Exception.Message)") }
    }
    try {
        New-NetIPAddress -IPAddress $Ip -PrefixLength 32 -InterfaceIndex $Index -ErrorAction Stop | Out-Null
        return 'New-NetIPAddress'
    } catch { [void]$why.Add("New-NetIPAddress: $($_.Exception.Message)") }

    # Some builds refuse loopback aliases through the cmdlet; netsh accepts them.
    $out = & netsh interface ipv4 add address "name=$Alias" "address=$Ip" mask=255.255.255.255 2>&1
    if ($LASTEXITCODE -eq 0) { return 'netsh' }
    [void]$why.Add("netsh: $out")
    throw ($why -join ' | ')
}

function Remove-Claim([string]$Ip, [string]$Alias, [int]$Index) {
    Remove-NetIPAddress -IPAddress $Ip -InterfaceIndex $Index -Confirm:$false -ErrorAction SilentlyContinue
    if (Get-Assigned $Ip) {
        & netsh interface ipv4 delete address "name=$Alias" "address=$Ip" 2>&1 | Out-Null
    }
}

# Is a connection to this address:port delivered locally? Uses SocketErrorCode, so the verdict does
# not depend on the localized Windows error text.
function Test-Capture([string]$Ip, [int]$Port, [int]$TimeoutSeconds) {
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $task = $client.ConnectAsync($Ip, $Port)
        # Task.Wait throws when the task faulted, and a refused connection is the normal, expected
        # result here, so swallow that and read the state instead.
        try { $null = $task.Wait($TimeoutSeconds * 1000) } catch { }
        if ($task.IsFaulted) {
            $inner = $task.Exception.InnerException
            if ($inner -is [System.Net.Sockets.SocketException]) {
                switch ($inner.SocketErrorCode.ToString()) {
                    'ConnectionRefused'   { return 'refused' }
                    'TimedOut'            { return 'timeout' }
                    default               { return 'failed' }
                }
            }
            return 'failed'
        }
        if (-not $task.IsCompleted) { return 'timeout' }
        # .NET dual-stack sockets report an IPv4 local endpoint as its IPv4-mapped IPv6 form, i.e.
        # "::ffff:18.145.161.25" rather than "18.145.161.25". Comparing the two strings directly
        # never matches, which reads a perfect capture as "escaped" - so normalise before comparing,
        # or the check inverts the truth on any machine whose socket picks the mapped form.
        $local = $client.Client.LocalEndPoint.Address.IPAddressToString
        if ($local -match '^::ffff:(\d{1,3}(?:\.\d{1,3}){3})$') { $local = $Matches[1] }
        if ($local -eq $Ip) { return 'local' }
        return "escaped:$local"
    } finally {
        $client.Close()
    }
}

# ---------------------------------------------------------------------------
# read the route table
# ---------------------------------------------------------------------------
if (-not (Test-Path $Config)) {
    Write-Bad "route table not found: $Config"
    exit 2
}
try {
    $doc = Get-Content $Config -Raw | ConvertFrom-Json
} catch {
    Write-Bad "cannot parse $Config : $($_.Exception.Message)"
    exit 2
}

$targets = New-Object System.Collections.ArrayList
foreach ($r in @($doc.routes)) {
    if (-not $r) { continue }
    if ($Route -and ($r.name -ne $Route)) { continue }
    if (-not $r.remoteHost) { continue }
    $remote = [string]$r.remoteHost
    $port = if ($r.remotePort) { [int]$r.remotePort } else { [int]$r.listenPort }
    $ip = $null
    if (Test-IPv4Literal $remote) {
        $ip = $remote
    } else {
        Write-Warn2 "route $($r.name): '$remote' is a name; resolving it now."
        Write-Warn2 '  if the hosts file points that name at this machine, it resolves to the LAN address,'
        Write-Warn2 '  which this script refuses to claim (see -Status below).'
        try {
            $resolved = [System.Net.Dns]::GetHostAddresses($remote) |
                Where-Object { $_.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork } |
                Select-Object -First 1
            if ($resolved) { $ip = $resolved.IPAddressToString }
        } catch { }
        if (-not $ip) {
            Write-Warn2 "  could not resolve '$remote'; skipping that route"
            continue
        }
    }
    [void]$targets.Add([pscustomobject]@{ Name = $r.name; Ip = $ip; Port = $port })
}

if ($targets.Count -eq 0) {
    Write-Bad 'no usable routes in the route table'
    exit 2
}

$lb = Get-Loopback
if (-not $lb) {
    Write-Bad 'could not identify the loopback pseudo-interface'
    exit 2
}

Write-Step "route table: $Config"
Write-Step "loopback interface: [$($lb.InterfaceIndex)] $($lb.InterfaceAlias)"
Write-Host ''

# ---------------------------------------------------------------------------
# -Status
# ---------------------------------------------------------------------------
if ($Status) {
    foreach ($t in $targets) {
        $a = Get-Assigned $t.Ip
        if (-not $a) {
            Write-Warn2 ("{0,-12} {1,-16} NOT claimed (the client would reach the real server directly)" -f $t.Name, $t.Ip)
        } elseif ($a.InterfaceIndex -eq $lb.InterfaceIndex) {
            Write-Ok   ("{0,-12} {1,-16} claimed on loopback ({2})" -f $t.Name, $t.Ip, $a.AddressState)
        } else {
            Write-Bad  ("{0,-12} {1,-16} assigned to '$($a.InterfaceAlias)' - a real interface, not a claim" -f $t.Name, $t.Ip)
        }
    }
    # Addresses the running relay claimed because a GmReconnect named them (a realm address is not
    # in the route table, so it cannot be reported above).
    $learned = Get-LearnedClaims
    if ($learned.Count -gt 0) {
        Write-Host ''
        Write-Step 'runtime claims (learned from a GmReconnect retarget):'
        foreach ($ip in $learned) {
            $a = Get-Assigned $ip
            if ($a) { Write-Note "  $ip is assigned on '$($a.InterfaceAlias)'" }
            else { Write-Warn2 "  $ip is listed but not assigned" }
        }
    }
    exit 0
}

# ---------------------------------------------------------------------------
# -Release
# ---------------------------------------------------------------------------
if ($Release) {
    $identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Bad 'Run this from an elevated prompt: removing an address needs it.'
        exit 2
    }
    $left = 0
    foreach ($t in $targets) {
        $a = Get-Assigned $t.Ip
        if (-not $a) { Write-Note "  $($t.Ip) was not claimed"; continue }
        if ($a.InterfaceIndex -ne $lb.InterfaceIndex) {
            Write-Warn2 "  $($t.Ip) belongs to '$($a.InterfaceAlias)', not to a claim - leaving it alone"
            continue
        }
        Remove-Claim -Ip $t.Ip -Alias $lb.InterfaceAlias -Index $lb.InterfaceIndex
        if (Get-Assigned $t.Ip) { Write-Bad "  $($t.Ip) is still assigned"; $left++ }
        else { Write-Ok "  $($t.Ip) released" }
    }

    # Realm addresses the relay claimed at runtime after a GmReconnect named them. The relay releases
    # its own on exit; this covers a relay that was killed outright.
    $learned = Get-LearnedClaims
    foreach ($ip in $learned) {
        $a = Get-Assigned $ip
        if (-not $a) { continue }
        if ($a.InterfaceIndex -ne $lb.InterfaceIndex) {
            Write-Warn2 "  $ip (runtime claim) belongs to '$($a.InterfaceAlias)' - leaving it alone"
            continue
        }
        Remove-Claim -Ip $ip -Alias $lb.InterfaceAlias -Index $lb.InterfaceIndex
        if (Get-Assigned $ip) { Write-Bad "  $ip (runtime claim) is still assigned"; $left++ }
        else { Write-Ok "  $ip released (runtime claim from a GmReconnect)" }
    }
    if ($learned.Count -gt 0) {
        Remove-Item -LiteralPath (Get-LearnedClaimsFile) -Force -ErrorAction SilentlyContinue
    }

    if ($left -gt 0) { exit 1 }
    Write-Host ''
    Write-Ok 'no claimed addresses left behind'
    exit 0
}

# ---------------------------------------------------------------------------
# claim
# ---------------------------------------------------------------------------
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Bad 'Run this from an elevated prompt: assigning an address needs it.'
    exit 2
}

$refused = 0
$unverified = 0
$claimed = 0

foreach ($t in $targets) {
    $assigned = Get-Assigned $t.Ip
    if ($assigned) {
        if ($assigned.InterfaceIndex -ne $lb.InterfaceIndex) {
            Write-Bad "REFUSING $($t.Ip): it is assigned to '$($assigned.InterfaceAlias)' (index $($assigned.InterfaceIndex))."
            Write-Bad '  That is a real interface address, not a claim. Claiming it here would take the machine off the network.'
            $refused++
            continue
        }
        Write-Note "  $($t.Ip) is already claimed on loopback"
    } else {
        Write-Step "claiming $($t.Ip)/32 for route $($t.Name)..."
        try {
            $how = Add-Claim -Ip $t.Ip -Alias $lb.InterfaceAlias -Index $lb.InterfaceIndex -SessionOnly ([bool]$SessionOnly)
            Write-Note "  added via $how"
        } catch {
            Write-Bad "  could not claim $($t.Ip): $($_.Exception.Message)"
            $unverified++
            continue
        }
    }

    $state = Wait-Preferred -Ip $t.Ip -Seconds $PreferredTimeoutSeconds
    if ($state -is [string]) {
        Write-Bad "  $($t.Ip) never became usable (state: $state)"
        switch ($state) {
            'Duplicate' {
                Write-Bad '  duplicate address detection: something on this link answers ARP for it, so claiming it'
                Write-Bad '  locally cannot work here (a router doing proxy ARP will do that).'
            }
            'Tentative' {
                Write-Bad '  duplicate address detection never finished - try again, and check for an ARP conflict.'
            }
        }
        $unverified++
        continue
    }
    $claimed++

    if (-not $SkipVerify) {
        $result = Test-Capture -Ip $t.Ip -Port $t.Port -TimeoutSeconds $VerifyTimeoutSeconds
        switch -Regex ($result) {
            '^local$'    { Write-Ok   "  verified: a dial to $($t.Ip):$($t.Port) is served locally (the relay is listening)" }
            '^refused$'  { Write-Ok   "  verified: a dial to $($t.Ip):$($t.Port) is refused locally, i.e. captured (relay not running yet)" }
            '^escaped'   {
                Write-Bad  "  NOT captured: a dial to $($t.Ip):$($t.Port) left the machine ($result)"
                Write-Bad  '  the claim did not take effect; the client would talk to the real server directly.'
                $unverified++
            }
            '^timeout$'  { Write-Warn2 "  no answer from $($t.Ip):$($t.Port) within $VerifyTimeoutSeconds s - cannot tell (nothing listening?)" }
            default      { Write-Warn2 "  dial to $($t.Ip):$($t.Port) failed: $result" }
        }
    }
}

Write-Host ''
Write-Host "claimed: $claimed   refused: $refused   problem: $unverified"
Write-Host ''
Write-Warn2 'While an address is claimed, this machine cannot reach the real server at that address unless the'
Write-Warn2 'relay is running. Release the claims when you stop:  .\tools\Set-AddressClaim.ps1 -Release'

if ($refused -gt 0) { exit 3 }
if ($unverified -gt 0) { exit 1 }
exit 0
