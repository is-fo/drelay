# ============================================================================
#  Test-ClaimDataPath.ps1
# ============================================================================
# Answers one question, for one route: can this machine send DATA to a server address that it has
# claimed on the loopback interface?
#
# WHY THIS EXISTS
#   The relay catches the client's connection by assigning the server's address to the loopback
#   interface. Every connection TO that address is then delivered locally, where the relay's listener
#   accepts it. The relay must then reach the real server itself, from a socket bound to the LAN
#   address - and on some Windows builds the stack accepts that connection's SYN, completes the
#   handshake, and then delivers every DATA segment back to the local stack (where it is dropped and
#   ACKed), so the real server never receives a byte. Nothing reports an error: the relay's write
#   succeeds, the server keeps sending its keepalive Ping, and the client sits on the loading screen
#   until it is dropped.
#
#   Measured on Windows 11 10.0.26200 (this laptop) with pktmon, without any relay in the picture:
#   a plain socket bound to 192.168.0.11, connected to a claimed 18.145.161.25:6410, exchanges
#   SYN/SYN-ACK/ACK and then its 135-byte payload is logged by pktmon as
#   "Drop: ip: 192.168.0.11.NNNN > 18.145.161.25.6410: Flags [P.] ... length 135" in the Rx
#   direction - i.e. Windows delivered its own outbound packet to itself and discarded it. Releasing
#   the claim before the send made the same payload appear on the wire. On Windows 10 the claim
#   design is reported working, which is why the same jar behaves differently on two machines.
#
# HOW IT DECIDES
#   It sends a real game Hello carrying a deliberately invalid queue token. A server that RECEIVES
#   it always answers with GmFailure (id 0x68), "Invalid queue token." - that is the positive signal,
#   and it needs no account, no game and no relay. If the only thing that comes back is the
#   keepalive GmPing (id 0x5C), the payload never arrived.
#
# Usage (elevated - it claims and releases the address):
#   .\tools\Test-ClaimDataPath.ps1                       # uses tools\..\relay-routes.json, route Game
#   .\tools\Test-ClaimDataPath.ps1 -Route Game_Slave
#   .\tools\Test-ClaimDataPath.ps1 -Config D:\drelay\relay-routes.json -KeepClaim
#
# Exit codes: 0 = data reaches the server, 1 = data is dropped locally (the claim design cannot
# forward on this machine), 2 = usage/elevation problem, 3 = the address could not be reached.
# ============================================================================

[CmdletBinding()]
param(
    [string]$Config,
    [string]$Route = 'Game',
    [switch]$KeepClaim,
    [int]$ReadSeconds = 6
)

$ErrorActionPreference = 'Stop'
if (-not $Config) { $Config = Join-Path (Split-Path -Parent $PSScriptRoot) 'relay-routes.json' }

function Write-Step { param([string]$Text) Write-Host $Text -ForegroundColor Cyan }
function Write-Ok   { param([string]$Text) Write-Host $Text -ForegroundColor Green }
function Write-Bad  { param([string]$Text) Write-Host $Text -ForegroundColor Red }
function Write-Warn2 { param([string]$Text) Write-Host $Text -ForegroundColor Yellow }
function Write-Note { param([string]$Text) Write-Host $Text -ForegroundColor DarkGray }

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Bad 'Run this from an elevated prompt: it claims and releases an address.'
    exit 2
}
if (-not (Test-Path -LiteralPath $Config)) {
    Write-Bad "route table not found: $Config"
    exit 2
}

$table = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
$target = @($table.routes) | Where-Object { $_.name -eq $Route } | Select-Object -First 1
if (-not $target) {
    Write-Bad "no route named '$Route' in $Config"
    exit 2
}
if (-not $target.remoteHost -or -not ($target.remoteHost -match '^\d{1,3}(\.\d{1,3}){3}$')) {
    Write-Bad "route '$Route' has no literal IPv4 remoteHost; this test needs one."
    exit 2
}

$server = [string]$target.remoteHost
$port = if ($target.remotePort) { [int]$target.remotePort } else { [int]$target.listenPort }

# The LAN source the relay would bind its own upstream dials to.
$lan = $null
foreach ($probe in '8.8.8.8', '1.1.1.1') {
    try {
        $s = [System.Net.Sockets.UdpClient]::new()
        $s.Connect($probe, 53)
        $lan = $s.Client.LocalEndPoint.Address.IPAddressToString
        $s.Dispose()
        if ($lan -and $lan -notmatch '^127\.' -and $lan -ne '0.0.0.0') { break }
    } catch { }
}
if (-not $lan) {
    Write-Bad 'could not determine this machine''s LAN address.'
    exit 2
}

Write-Step "route        : $Route -> ${server}:$port"
Write-Step "upstream from: $lan"
Write-Host ''

# A real game Hello with an invalid 32-byte queue token. The server answers any Hello it parses.
$token = 'DRELATESTINVALIDQUEUETOKEN123456'
if ($token.Length -ne 32) { throw 'token must be 32 characters' }
$payload = [byte[]](
    0x37, 0x00, 0x32) + [System.Text.Encoding]::ASCII.GetBytes('2y0FYlh4mowcUZl4fqoFcIB') +
    [byte[]](0x05) + [System.Text.Encoding]::ASCII.GetBytes('5.8.2') +
    [byte[]](0x00, 0x02, 0xB6, 0x37, 0x3C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x9E, 0x01, 0x20) +
    [System.Text.Encoding]::ASCII.GetBytes($token) +
    [byte[]](0x14) + [System.Text.Encoding]::ASCII.GetBytes('mMYVSvPiVzDT2YcEuqfm') + [byte[]](0x00, 0x00, 0x00, 0x00, 0x14, 0x14)

$claimScript = Join-Path $PSScriptRoot 'Set-AddressClaim.ps1'
$claimed = $false
try {
    Write-Step "claiming $server on loopback (SessionOnly)..."
    # A separate process on purpose: the claim script ends with `exit`, and calling it in-process
    # from another script leaves the caller in an unpredictable state.
    $claimOut = & powershell -NoProfile -ExecutionPolicy Bypass -File $claimScript -Config $Config -Route $Route -SessionOnly -SkipVerify 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Bad "the claim failed (exit $LASTEXITCODE)"
        exit 2
    }
    $claimed = $true

    $client = [System.Net.Sockets.TcpClient]::new()
    $client.Client.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse($lan), 0))
    $client.ReceiveTimeout = 3000
    try {
        $client.Connect($server, $port)
    } catch {
        Write-Bad "could not connect to ${server}:$port from $lan : $($_.Exception.Message)"
        exit 3
    }

    $stream = $client.GetStream()
    $frame = [byte[]]::new(4 + $payload.Length)
    $frame[0] = [byte](($payload.Length -shr 24) -band 0xFF)
    $frame[1] = [byte](($payload.Length -shr 16) -band 0xFF)
    $frame[2] = [byte](($payload.Length -shr 8) -band 0xFF)
    $frame[3] = [byte]($payload.Length -band 0xFF)
    [Array]::Copy($payload, 0, $frame, 4, $payload.Length)
    $stream.Write($frame, 0, $frame.Length)
    $stream.Flush()
    Write-Note ("sent {0} byte(s); reading for {1} s..." -f $frame.Length, $ReadSeconds)

    $buffer = [byte[]]::new(65536)
    $received = New-Object System.Collections.ArrayList
    $deadline = (Get-Date).AddSeconds($ReadSeconds)
    try {
        while ((Get-Date) -lt $deadline) {
            $n = $stream.Read($buffer, 0, $buffer.Length)
            if ($n -le 0) { break }
            [void]$received.Add([byte[]]$buffer[0..($n - 1)])
        }
    } catch {
        # a read timeout is the interesting case: nothing came back
    }
    $client.Close()

    $bytes = ($received | ForEach-Object { $_.Length } | Measure-Object -Sum).Sum
    $types = New-Object System.Collections.ArrayList
    $all = [byte[]]($received | ForEach-Object { $_ })
    $at = 0
    while ($at + 4 -le $all.Length) {
        $len = ($all[$at] -shl 24) -bor ($all[$at + 1] -shl 16) -bor ($all[$at + 2] -shl 8) -bor $all[$at + 3]
        if ($len -le 0 -or $at + 4 + $len -gt $all.Length) { break }
        if ($len -ge 2) {
            $id = $all[$at + 4] -bor ($all[$at + 5] -shl 8)
            [void]$types.Add(('0x{0:X2}' -f $id))
        }
        $at += 4 + $len
    }
    $types = @($types | Select-Object -Unique)

    Write-Host ''
    Write-Note ("server sent {0} byte(s); packet type(s): {1}" -f $bytes, ($types -join ', '))

    $gotFailure = $types -contains '0x68'
    $gotAnythingButPing = @($types | Where-Object { $_ -ne '0x5C' }).Count -gt 0

    if ($gotFailure) {
        Write-Ok 'PASS: the payload reached the server - it parsed the Hello and rejected the token.'
        Write-Note '  a claim-based route can forward on this machine.'
        exit 0
    }
    if ($gotAnythingButPing) {
        Write-Ok 'PASS: the server parsed the payload (a non-Ping reply came back).'
        exit 0
    }
    Write-Bad 'FAIL: only keepalive Pings came back, so the payload never reached the server.'
    Write-Bad '  Windows delivered this machine''s own outbound data back to itself and dropped it,'
    Write-Bad '  because the destination address is assigned to the loopback interface. The claim'
    Write-Bad '  design cannot forward traffic on this machine; the relay will connect, the server'
    Write-Bad '  will keep sending Ping, and the game will hang on its loading screen.'
    Write-Note '  Evidence: run this under pktmon with --type drop and look for the Tx segment'
    Write-Note '  "192.168.x.x.NNNN > <server>:<port> ... length <n>" listed as an Rx drop.'
    Write-Note '  The relay itself is not at fault - a plain socket shows the same behaviour.'
    exit 1
} finally {
    if (-not $KeepClaim -and $claimed) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $claimScript -Config $Config -Release 2>&1 | Out-Null
    }
}
