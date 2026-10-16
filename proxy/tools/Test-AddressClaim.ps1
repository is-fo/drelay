# ============================================================================
#  Test-AddressClaim.ps1
# ============================================================================
# Probe whether claiming the game server's address puts the client's traffic into the local stack,
# and whether the relay's own upstream dial still escapes to the real server.
#
# Background: the client dials a bare IP from https://playdarzas.com/api/v1/serverlist, so a hosts
# entry cannot shadow it. Two packet-level redirect mechanisms were tried against the real client
# and neither produced a relay session:
#
#   * rewrite the destination and re-inject -> packets were rewritten but never delivered locally
#   * drop the first SYN, forward the retransmit -> the retransmit went to the public address, so
#     the client connected straight to the real server and the relay saw nothing
#
# This script tests the remaining mechanism: if the real server's address is claimed locally, the
# kernel should route the client's connection to that address into the local stack, where the relay
# can accept it.
#
# ----------------------------------------------------------------------------
# WHAT THE FIRST REVISION GOT WRONG (measured on this machine, 2026-10-01)
# ----------------------------------------------------------------------------
# The old script claimed the address on the NIC that held the LAN address and bound its listener
# immediately. Two documented behaviours made that both fail and damage the NIC:
#
#   1. New-NetIPAddress turns DHCP OFF on a DHCP-enabled interface. Microsoft's own page says so:
#        "If you run this cmdlet to add an IP address to an interface on which DHCP is already
#         enabled, then DHCP is automatically disabled."
#      (https://learn.microsoft.com/en-us/powershell/module/nettcpip/new-netipaddress)
#      The old cleanup removed only the test address, so the NIC was left static with no address at
#      all: APIPA 169.254.x.x, no default route, no connectivity - while the script still printed
#      "machine restored to its previous addressing". That is the state 'Ethernet 2' was found in.
#
#   2. The same page: "the new IP address is not usable until DAD successfully finishes". The old
#      script bound the listener immediately and raced duplicate address detection, so Start()
#      failed with WSAEADDRNOTAVAIL (10049) "The requested address is not valid in its context".
#
# ----------------------------------------------------------------------------
# WHY THE CLAIM GOES ON THE LOOPBACK INTERFACE BY DEFAULT
# ----------------------------------------------------------------------------
# Windows delivers a connection to a local address only when the sending socket belongs to the same
# interface. Measured on this machine with local addresses:
#
#   listener 100.90.214.22 (Tailscale) + client bound to 192.168.0.40 (Wi-Fi) -> NOT delivered
#   listener 100.90.214.22 (Tailscale) + client unbound                       -> delivered locally
#   listener 192.168.0.40  (Wi-Fi)     + client bound to 192.168.0.40         -> delivered locally
#
# The game client binds no source address, so it lands in the local stack either way. The relay's
# upstream dial *does* bind the LAN address, so it only escapes to the real server when the claimed
# address sits on a different interface than the LAN address. Claiming on the LAN NIC therefore has
# the relay connect to itself. Claiming on the loopback pseudo-interface satisfies both halves and
# touches no hardware configuration, no DHCP setting and no route.
#
# ----------------------------------------------------------------------------
# Usage (from an ELEVATED prompt):
#   .\tools\Test-AddressClaim.ps1
#   .\tools\Test-AddressClaim.ps1 -RealIp 18.145.161.25 -GamePort 6410
#   .\tools\Test-AddressClaim.ps1 -InterfaceAlias 'Ethernet 2' -AllowDhcpDisruption   # not advised
#
# Exit codes:
#   0  both halves behave as the relay design needs
#   1  the client's connection did not land in the local stack (claim does not route)
#   2  an address this script uses is already assigned; remove it and re-run
#   3  the machine was NOT restored to its previous addressing
#   4  refused (not elevated, unknown interface, or a NIC claim without -AllowDhcpDisruption)
#   5  the client lands locally, but a LAN-bound upstream dial is captured too (relay would loop)
# ============================================================================

[CmdletBinding()]
param(
    [string]$RealIp = '18.145.161.25',
    [int]$GamePort = 6410,
    [string]$LanIp,
    [string]$InterfaceAlias,
    [switch]$AllowDhcpDisruption,
    [int]$PreferredTimeoutSeconds = 20,
    [int]$ProbeTimeoutSeconds = 4
)

$ErrorActionPreference = 'Stop'

# TEST-NET-3 (RFC 5737): guaranteed never to be a real host, so the escape probe cannot reach one.
$ProbeIp = '203.0.113.7'

function Write-Step { param([string]$Text) Write-Host $Text -ForegroundColor Cyan }
function Write-Ok   { param([string]$Text) Write-Host $Text -ForegroundColor Green }
function Write-Bad  { param([string]$Text) Write-Host $Text -ForegroundColor Red }
function Write-Note { param([string]$Text) Write-Host $Text -ForegroundColor DarkGray }
function Write-Warn2 { param([string]$Text) Write-Host $Text -ForegroundColor Yellow }

function Get-Address { param([string]$Ip)
    Get-NetIPAddress -AddressFamily IPv4 -IPAddress $Ip -ErrorAction SilentlyContinue | Select-Object -First 1
}

# Report NICs that an earlier claim left with DHCP off and no usable address.
function Show-AddressingHealth {
    $broken = New-Object System.Collections.ArrayList
    foreach ($i in Get-NetIPInterface -AddressFamily IPv4) {
        if ($i.Dhcp -ne 'Disabled') { continue }
        if ($i.InterfaceIndex -eq 1) { continue }   # loopback: no DHCP and only 127.0.0.1, by design
        $all = @(Get-NetIPAddress -InterfaceIndex $i.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue)
        if ($all.Count -eq 0) { continue }
        $usable = @($all | Where-Object { $_.IPAddress -notlike '169.254.*' })
        if ($usable.Count -eq 0) { [void]$broken.Add($i.InterfaceAlias) }
    }
    if ($broken.Count -gt 0) {
        Write-Warn2 "Leftover damage: DHCP is off with no usable address on: $($broken -join ', ')"
        Write-Warn2 "  repair with: .\tools\Restore-DhcpAddressing.ps1 -InterfaceAlias '<name>'"
        Write-Host ''
    }
}

function Wait-AddressPreferred {
    param([string]$Ip, [int]$TimeoutSeconds)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastState = 'absent'
    while ((Get-Date) -lt $deadline) {
        $a = Get-Address -Ip $Ip
        if ($a) {
            $lastState = $a.AddressState
            if ($a.AddressState -eq 'Preferred') { return $a }
        }
        Start-Sleep -Milliseconds 250
    }
    return $lastState   # a string, i.e. 'Tentative' / 'Duplicate' / 'absent'
}

function Add-ClaimAddress {
    param([string]$Ip, [int]$IfIndex, [string]$Alias, [bool]$Loopback)
    try {
        if ($Loopback) {
            New-NetIPAddress -IPAddress $Ip -PrefixLength 32 -InterfaceIndex $IfIndex -ErrorAction Stop | Out-Null
        } else {
            # SkipAsSource keeps the claimed address out of source-address selection and out of DNS.
            New-NetIPAddress -IPAddress $Ip -PrefixLength 32 -InterfaceIndex $IfIndex -SkipAsSource $true -ErrorAction Stop | Out-Null
        }
        return 'New-NetIPAddress'
    } catch {
        $why = $_.Exception.Message
        # Some builds refuse addresses on the loopback interface through the cmdlet; netsh accepts them.
        $out = & netsh interface ipv4 add address "name=$Alias" "address=$Ip" mask=255.255.255.255 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "New-NetIPAddress failed ($why) and the netsh fallback failed too: $out"
        }
        return 'netsh'
    }
}

function Remove-ClaimAddress {
    param([string]$Ip, [int]$IfIndex, [string]$Alias)
    Remove-NetIPAddress -IPAddress $Ip -InterfaceIndex $IfIndex -Confirm:$false -ErrorAction SilentlyContinue
    if (Get-Address -Ip $Ip) {
        & netsh interface ipv4 delete address "name=$Alias" "address=$Ip" 2>&1 | Out-Null
    }
}

# ---------------------------------------------------------------------------
# pre-flight
# ---------------------------------------------------------------------------
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Bad 'Run this from an elevated prompt: assigning an address needs it.'
    exit 4
}

Show-AddressingHealth

$loopbackIf = Get-NetIPInterface -AddressFamily IPv4 | Where-Object { $_.InterfaceIndex -eq 1 } | Select-Object -First 1
if (-not $loopbackIf) {
    Write-Bad 'Could not identify the loopback pseudo-interface (interface index 1).'
    exit 4
}

$claimOnLoopback = -not $InterfaceAlias
if ($claimOnLoopback) {
    $claim = $loopbackIf
} else {
    $claim = Get-NetIPInterface -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -eq $InterfaceAlias } | Select-Object -First 1
    if (-not $claim) {
        Write-Bad "No IPv4 interface named '$InterfaceAlias'. Available:"
        Get-NetIPInterface -AddressFamily IPv4 | ForEach-Object { Write-Host "  [$($_.InterfaceIndex)] $($_.InterfaceAlias)" }
        exit 4
    }
}

foreach ($ip in @($RealIp, $ProbeIp)) {
    if (Get-Address -Ip $ip) {
        Write-Warn2 "$ip is ALREADY assigned to this machine. Remove it before testing:"
        Write-Warn2 "  Remove-NetIPAddress -IPAddress $ip -Confirm:`$false"
        exit 2
    }
}

# The LAN address stands in for whatever the relay would bind upstream.
$lanNote = ''
if (-not $LanIp) {
    $candidates = @(Get-NetIPAddress -AddressFamily IPv4 |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' -and $_.PrefixOrigin -eq 'Dhcp' })
    if ($claimOnLoopback) {
        # the relay binds the real NIC, which is a different interface from the loopback claim
        $LanIp = ($candidates | Where-Object { $_.InterfaceIndex -ne $claim.InterfaceIndex } | Select-Object -First 1).IPAddress
        $lanNote = ' (DHCP address, different interface than the claim)'
    } else {
        # the relay would bind on the same NIC that holds the claim - the case that loops
        $LanIp = ($candidates | Where-Object { $_.InterfaceIndex -eq $claim.InterfaceIndex } | Select-Object -First 1).IPAddress
        if (-not $LanIp) { $LanIp = ($candidates | Select-Object -First 1).IPAddress }
        $lanNote = ' (same NIC as the claim)'
    }
}
if (-not $LanIp) {
    Write-Bad 'No LAN address to dial from. Pass -LanIp <address>.'
    exit 4
}

$claimDesc = "[$($claim.InterfaceIndex)] $($claim.InterfaceAlias)"
if ($claimOnLoopback) { $claimDesc += ' (loopback: no hardware, DHCP setting or route is touched)' } else { $claimDesc += ' (physical NIC)' }

# Snapshot everything a physical-NIC claim can disturb.
$snapshot = $null
if (-not $claimOnLoopback) {
    $nicIf = Get-NetIPInterface -InterfaceIndex $claim.InterfaceIndex -AddressFamily IPv4
    if ($nicIf.Dhcp -eq 'Enabled' -and -not $AllowDhcpDisruption) {
        Write-Bad "Adding an address to '$($claim.InterfaceAlias)' switches DHCP off on it (documented New-NetIPAddress"
        Write-Bad 'behaviour) and drops its lease. That is what broke Ethernet 2.'
        Write-Bad 'Use the default (loopback) claim instead, or re-run with -AllowDhcpDisruption to accept it:'
        Write-Bad 'this run snapshots the DHCP state, addresses, DNS and gateway, and re-enables and verifies them.'
        exit 4
    }
    $snapshot = [pscustomobject]@{
        Alias     = $claim.InterfaceAlias
        Index     = $claim.InterfaceIndex
        Dhcp      = $nicIf.Dhcp
        Addresses = @(Get-NetIPAddress -InterfaceIndex $claim.InterfaceIndex -AddressFamily IPv4 |
                        Select-Object IPAddress, PrefixLength, PrefixOrigin)
        Dns       = @((Get-DnsClientServerAddress -InterfaceIndex $claim.InterfaceIndex -AddressFamily IPv4).ServerAddresses)
        Gateway   = (Get-NetRoute -InterfaceIndex $claim.InterfaceIndex -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
                        Select-Object -First 1).NextHop
    }
}

Write-Step "claim interface: $claimDesc"
Write-Host "test address:    $RealIp/32    escape probe: $ProbeIp/32$lanNote    LAN address: $LanIp"
Write-Host ''

# ---------------------------------------------------------------------------
# claim, probe, restore
# ---------------------------------------------------------------------------
$addedReal = $false
$addedProbe = $false
$client = $null; $escClient = $null; $listener = $null; $probeListener = $null
$accepted = $null; $escAccepted = $null
$clientLandsLocally = $false
$upstreamEscapes = $false
$verdict = 4
$restoreOk = $true

try {
    Write-Step "adding $RealIp/32 and $ProbeIp/32..."
    $m1 = Add-ClaimAddress -Ip $RealIp -IfIndex $claim.InterfaceIndex -Alias $claim.InterfaceAlias -Loopback $claimOnLoopback
    $addedReal = $true
    $m2 = Add-ClaimAddress -Ip $ProbeIp -IfIndex $claim.InterfaceIndex -Alias $claim.InterfaceAlias -Loopback $claimOnLoopback
    $addedProbe = $true
    Write-Note "  added via $m1 / $m2"

    Write-Step 'waiting for duplicate address detection to finish (this is the step the old script skipped)...'
    $a1 = Wait-AddressPreferred -Ip $RealIp -TimeoutSeconds $PreferredTimeoutSeconds
    $a2 = Wait-AddressPreferred -Ip $ProbeIp -TimeoutSeconds $PreferredTimeoutSeconds
    if ($a1 -isnot [string] -and $a2 -isnot [string]) {
        Write-Ok "  both addresses are Preferred and usable"
    } elseif ($a1 -eq 'Duplicate' -or $a2 -eq 'Duplicate') {
        Write-Bad "  duplicate address detected ($a1 / $a2): something on this link answers ARP for the claimed"
        Write-Bad '  address - typically proxy ARP on the router. Claiming will not work on this network.'
        $verdict = 1
    } else {
        Write-Bad "  the address never became usable within ${PreferredTimeoutSeconds}s (state: $a1 / $a2)"
        $verdict = 1
    }

    if ($verdict -ne 1) {
        # --- probe 1: the game client's shape (no source bind) -------------------
        Write-Host ''
        Write-Step "probe 1: listener on ${RealIp}:${GamePort}, client unbound (what the game client does)"
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse($RealIp), $GamePort)
        $listener.Start()
        Write-Note '  bound and listening'
        $acceptTask = $listener.AcceptTcpClientAsync()
        $client = [System.Net.Sockets.TcpClient]::new()
        $null = $client.ConnectAsync([System.Net.IPAddress]::Parse($RealIp), $GamePort)
        if ($acceptTask.Wait($ProbeTimeoutSeconds * 1000)) {
            $accepted = $acceptTask.Result
            $clientLandsLocally = $true
            Write-Ok "  PASS: the listener accepted the connection from $($accepted.Client.RemoteEndPoint)"
        } else {
            Write-Bad "  FAIL: the client's dial to $RealIp did not arrive at the local listener"
        }

        # --- probe 2: the relay's upstream shape (source bound to the LAN address) ---
        Write-Host ''
        Write-Step "probe 2: client bound to $LanIp (the relay's upstream shape) dialling $ProbeIp"
        Write-Note '  the dial goes to TEST-NET-3, so no real server is contacted'
        $probeListener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse($ProbeIp), $GamePort)
        $probeListener.Start()
        $escAcceptTask = $probeListener.AcceptTcpClientAsync()
        $escClient = [System.Net.Sockets.TcpClient]::new()
        $escClient.Client.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse($LanIp), 0))
        $escTask = $escClient.ConnectAsync([System.Net.IPAddress]::Parse($ProbeIp), $GamePort)
        if ($escAcceptTask.Wait($ProbeTimeoutSeconds * 1000)) {
            $escAccepted = $escAcceptTask.Result
            $upstreamEscapes = $false
            Write-Bad '  FAIL: a LAN-bound dial to a claimed address is delivered locally.'
            Write-Bad '        A relay on this layout would connect to itself.'
        } else {
            $upstreamEscapes = $true
            Write-Ok '  PASS: no local accept - the dial left the host, as the relay needs'
            if ($escTask.IsFaulted) {
                Write-Note "  (it failed outright: $($escTask.Exception.InnerException.Message))"
            }
        }

        if ($clientLandsLocally -and $upstreamEscapes) { $verdict = 0 }
        elseif (-not $clientLandsLocally) { $verdict = 1 }
        else { $verdict = 5 }
    }
} catch {
    Write-Host ''
    Write-Bad "FAIL: $($_.Exception.Message)"
    if ($verdict -eq 4) { $verdict = 1 }
} finally {
    if ($accepted) { $accepted.Close() }
    if ($escAccepted) { $escAccepted.Close() }
    if ($client) { $client.Close() }
    if ($escClient) { $escClient.Close() }
    if ($listener) { $listener.Stop() }
    if ($probeListener) { $probeListener.Stop() }

    if ($addedReal -or $addedProbe) {
        Write-Host ''
        Write-Step 'removing the claimed addresses...'
        if ($addedReal) { Remove-ClaimAddress -Ip $RealIp -IfIndex $claim.InterfaceIndex -Alias $claim.InterfaceAlias }
        if ($addedProbe) { Remove-ClaimAddress -Ip $ProbeIp -IfIndex $claim.InterfaceIndex -Alias $claim.InterfaceAlias }
    }

    if ($snapshot) {
        Write-Step "restoring '$($snapshot.Alias)' addressing..."
        $now = Get-NetIPInterface -InterfaceIndex $snapshot.Index -AddressFamily IPv4
        if ($snapshot.Dhcp -eq 'Enabled' -and $now.Dhcp -ne 'Enabled') {
            Write-Warn2 "  DHCP was switched off by New-NetIPAddress (now '$($now.Dhcp)'); re-enabling it"
            Set-NetIPInterface -InterfaceIndex $snapshot.Index -AddressFamily IPv4 -Dhcp Enabled -ErrorAction Continue
        }
        if ($snapshot.Dhcp -eq 'Enabled') {
            & ipconfig /renew "$($snapshot.Alias)" 2>&1 | Out-Null
            $deadline = (Get-Date).AddSeconds(30)
            $dhcpAddr = $null
            while (-not $dhcpAddr -and (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 500
                $dhcpAddr = Get-NetIPAddress -InterfaceIndex $snapshot.Index -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                                Where-Object { $_.PrefixOrigin -eq 'Dhcp' } | Select-Object -First 1
            }
            if ($dhcpAddr) { Write-Note "  DHCP lease back: $($dhcpAddr.IPAddress)/$($dhcpAddr.PrefixLength)" }
            else { $restoreOk = $false; Write-Bad '  no DHCP lease came back within 30s' }
        }
        foreach ($a in $snapshot.Addresses) {
            if (Get-Address -Ip $a.IPAddress) { continue }
            if ($a.PrefixOrigin -eq 'Dhcp') { continue }   # covered by the renew above
            Write-Warn2 "  re-adding $($a.IPAddress)/$($a.PrefixLength) ($($a.PrefixOrigin))"
            New-NetIPAddress -InterfaceIndex $snapshot.Index -IPAddress $a.IPAddress -PrefixLength $a.PrefixLength -ErrorAction Continue | Out-Null
            if (-not (Get-Address -Ip $a.IPAddress)) { $restoreOk = $false; Write-Bad "  could not restore $($a.IPAddress)" }
        }
        if ($snapshot.Dns.Count -gt 0) {
            $dnsNow = @((Get-DnsClientServerAddress -InterfaceIndex $snapshot.Index -AddressFamily IPv4).ServerAddresses)
            if ($dnsNow.Count -eq 0) {
                Write-Warn2 "  DNS servers are gone; restoring $($snapshot.Dns -join ', ')"
                Set-DnsClientServerAddress -InterfaceIndex $snapshot.Index -ServerAddresses $snapshot.Dns -ErrorAction Continue
            }
        }
        if ($snapshot.Gateway) {
            $gwNow = (Get-NetRoute -InterfaceIndex $snapshot.Index -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
                        Select-Object -First 1).NextHop
            if ($gwNow -ne $snapshot.Gateway) {
                $restoreOk = $false
                Write-Bad "  default gateway is '$gwNow' but was '$($snapshot.Gateway)'"
            }
        }
    }

    foreach ($ip in @($RealIp, $ProbeIp)) {
        if (Get-Address -Ip $ip) {
            $restoreOk = $false
            Write-Bad "  $ip is still assigned; remove it with: Remove-NetIPAddress -IPAddress $ip -Confirm:`$false"
        }
    }

    if ($restoreOk) {
        Write-Ok 'machine restored to its previous addressing (verified)'
    } else {
        Write-Bad 'RESTORATION INCOMPLETE - run: .\tools\Restore-DhcpAddressing.ps1 -InterfaceAlias ''<name>'''
    }
}

# ---------------------------------------------------------------------------
# verdict
# ---------------------------------------------------------------------------
Write-Host ''
switch ($verdict) {
    0 {
        Write-Ok  'PASS: claiming the address routes the client into the local stack, and a source-bound'
        Write-Ok  '      upstream dial is not captured. The relay can bind the claimed address directly,'
        Write-Ok  '      with no packet rewriting, if the claim lives on a different interface than the'
        Write-Ok  '      upstream source (loopback works, as tested here).'
    }
    1 {
        Write-Bad 'FAIL: claiming the address does not put the client in the local stack on this setup.'
    }
    5 {
        Write-Bad 'FAIL: the client lands locally, but the upstream dial is captured too - the relay would'
        Write-Bad '      connect to itself. Claim on an interface other than the one holding the upstream'
        Write-Bad '      source address (the loopback default does this).'
    }
    default {
        Write-Bad 'FAIL: the probe did not complete.'
    }
}
if (-not $restoreOk -and $verdict -eq 0) { $verdict = 3 }

exit $verdict
