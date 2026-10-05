# ============================================================================
#  Restore-DhcpAddressing.ps1
# ============================================================================
# Repair a NIC that an address-claim test left with DHCP switched off.
#
# Why this exists: New-NetIPAddress turns DHCP off on a DHCP-enabled interface
# (https://learn.microsoft.com/en-us/powershell/module/nettcpip/new-netipaddress). A test that adds
# an address and only removes that address again leaves the NIC with DHCP off and no address at all:
# Windows falls back to APIPA (169.254.x.x), there is no default route and no DNS, and the adapter
# still reports "Up" while nothing works. That is how 'Ethernet 2' was left on 2026-10-01.
#
# Usage (from an ELEVATED prompt):
#   .\tools\Restore-DhcpAddressing.ps1 -InterfaceAlias 'Ethernet 2'            # repair
#   .\tools\Restore-DhcpAddressing.ps1 -InterfaceAlias 'Ethernet 2' -DryRun    # only report
#   .\tools\Restore-DhcpAddressing.ps1 -InterfaceAlias 'Ethernet 2' -RemoveManualAddresses
#
# The equivalent by hand:
#   netsh interface ipv4 set address name="Ethernet 2" source=dhcp
#   ipconfig /renew "Ethernet 2"
# or in the GUI: Network Connections -> adapter -> IPv4 -> "Obtain an IP address automatically".
#
# Exit codes: 0 = DHCP address, gateway and DNS verified / nothing to repair,
#             1 = the repair ran but verification failed, 2 = no such interface.
# ============================================================================

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InterfaceAlias,
    [switch]$DryRun,
    [switch]$RemoveManualAddresses,
    [int]$WaitSeconds = 30
)

$ErrorActionPreference = 'Stop'

function Write-Step { param([string]$Text) Write-Host $Text -ForegroundColor Cyan }
function Write-Ok   { param([string]$Text) Write-Host $Text -ForegroundColor Green }
function Write-Bad  { param([string]$Text) Write-Host $Text -ForegroundColor Red }
function Write-Warn2 { param([string]$Text) Write-Host $Text -ForegroundColor Yellow }

$iface = Get-NetIPInterface -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -eq $InterfaceAlias } | Select-Object -First 1
if (-not $iface) {
    Write-Bad "No IPv4 interface named '$InterfaceAlias'. Available:"
    Get-NetIPInterface -AddressFamily IPv4 | ForEach-Object { Write-Host "  [$($_.InterfaceIndex)] $($_.InterfaceAlias)" }
    exit 2
}
$idx = $iface.InterfaceIndex

function Show-State {
    $i = Get-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4
    Write-Host "interface:   [$idx] $($i.InterfaceAlias)   admin/oper: $($i.ConnectionState)   DHCP: $($i.Dhcp)"
    Write-Host 'addresses:'
    $addrs = @(Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue)
    if ($addrs.Count -eq 0) { Write-Host '  (none)' }
    foreach ($a in $addrs) {
        Write-Host "  $($a.IPAddress)/$($a.PrefixLength)  origin=$($a.PrefixOrigin)/$($a.SuffixOrigin)  state=$($a.AddressState)"
    }
    $gw = (Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Select-Object -First 1).NextHop
    $dns = @((Get-DnsClientServerAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses)
    Write-Host "gateway:     $(if ($gw) { $gw } else { '(none)' })"
    Write-Host "DNS:         $(if ($dns.Count) { $dns -join ', ' } else { '(none)' })"
}

Write-Step "current state of '$InterfaceAlias':"
Show-State
Write-Host ''

$dhcpAddr = @(Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                Where-Object { $_.PrefixOrigin -eq 'Dhcp' })
if ($iface.Dhcp -eq 'Enabled' -and $dhcpAddr.Count -gt 0) {
    Write-Ok 'DHCP is enabled and this interface holds a DHCP address: nothing to repair.'
    exit 0
}

$manual = @(Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                Where-Object { $_.PrefixOrigin -ne 'Dhcp' -and $_.IPAddress -notlike '169.254.*' -and $_.IPAddress -notlike '127.*' })

Write-Warn2 'This interface is not using DHCP:'
Write-Warn2 "  DHCP setting : $($iface.Dhcp)"
Write-Warn2 "  DHCP address : $(if ($dhcpAddr.Count) { ($dhcpAddr | ForEach-Object { $_.IPAddress }) -join ', ' } else { 'none' })"
if ($manual.Count -gt 0) {
    Write-Warn2 "  static addresses still configured: $(($manual | ForEach-Object { "$($_.IPAddress)/$($_.PrefixLength)" }) -join ', ')"
    if (-not $RemoveManualAddresses) {
        Write-Warn2 '  (they are left alone; add -RemoveManualAddresses to delete them)'
    }
}
Write-Host ''

if ($DryRun) {
    Write-Step 'DRY RUN - nothing was changed. The repair would run:'
    Write-Host "  Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -Dhcp Enabled"
    if ($RemoveManualAddresses) {
        foreach ($a in $manual) { Write-Host "  Remove-NetIPAddress -InterfaceIndex $idx -IPAddress $($a.IPAddress) -Confirm:`$false" }
    }
    Write-Host "  ipconfig /renew `"$InterfaceAlias`""
    exit 0
}

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Bad 'Run this from an elevated prompt: changing the interface configuration needs it.'
    exit 1
}

Write-Step "enabling DHCP on '$InterfaceAlias'..."
Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -Dhcp Enabled

if ($RemoveManualAddresses) {
    foreach ($a in $manual) {
        Write-Step "  removing static $($a.IPAddress)/$($a.PrefixLength)"
        Remove-NetIPAddress -InterfaceIndex $idx -IPAddress $a.IPAddress -Confirm:$false -ErrorAction Continue
    }
}

Write-Step 'renewing the lease...'
& ipconfig /renew "$InterfaceAlias" 2>&1 | Out-Null

$deadline = (Get-Date).AddSeconds($WaitSeconds)
$lease = $null
while (-not $lease -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $lease = Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                Where-Object { $_.PrefixOrigin -eq 'Dhcp' } | Select-Object -First 1
}

Write-Host ''
Write-Step 'state after the repair:'
Show-State
Write-Host ''

$gw = (Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Select-Object -First 1).NextHop
$dns = @((Get-DnsClientServerAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses)
$ok = $true

if ($lease) { Write-Ok "DHCP lease: $($lease.IPAddress)/$($lease.PrefixLength)" }
else { $ok = $false; Write-Bad "no DHCP lease after $WaitSeconds s - check the cable/switch port, or restart the adapter:"; Write-Bad "  Restart-NetAdapter -InterfaceIndex $idx" }

if ($gw) { Write-Ok "default gateway: $gw" } else { $ok = $false; Write-Bad 'no default gateway route' }
if ($dns.Count -gt 0) { Write-Ok "DNS: $($dns -join ', ')" } else { Write-Warn2 'no DNS servers (name resolution will fail)' }

if ($gw) {
    if (Test-Connection -TargetName $gw -Count 1 -Quiet -ErrorAction SilentlyContinue) { Write-Ok "gateway $gw answers" }
    else { Write-Warn2 "gateway $gw does not answer ping (it may just block ICMP)" }
}

Write-Host ''
if ($ok) {
    Write-Ok "PASS: '$InterfaceAlias' is back on DHCP. Unplug/disable Wi-Fi if you want it preferred again."
    exit 0
} else {
    Write-Bad "FAIL: '$InterfaceAlias' is still not usable. Try: Restart-NetAdapter -InterfaceIndex $idx"
    exit 1
}
