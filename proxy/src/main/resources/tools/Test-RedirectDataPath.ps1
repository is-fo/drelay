# ============================================================================
#  Test-RedirectDataPath.ps1
# ============================================================================
# Answers one question, on this machine, in about fifteen seconds and without the game, the relay or
# the real server: DOES A PACKET REFLECTED BY THE REDIRECTOR REACH A LOCAL LISTENER, AND DOES THE
# ANSWER GET BACK TO THE CALLER?
#
# WHY THIS EXISTS
#   The redirector's whole claim is that a packet it re-injects inward is delivered to a local
#   listener, and that the listener's answer is delivered back to the original socket. Neither half is
#   obviously true - an outbound packet re-injected outward is NOT re-routed, which is why rewriting
#   the destination address (what the previous redirector did) produced `seen=21 redirected=21` and
#   zero sessions (docs/PROTOCOL.md §5). So the property is measured, not assumed:
#
#     * a listener is opened on a port of its own (16410 by default - NOT the game's, so a relay can
#       keep running while this runs);
#     * the redirector is started on that port only;
#     * a plain socket dials a public address that is deliberately not reachable (192.0.2.1 is
#       TEST-NET-1, reserved by RFC 5737) on that port;
#     * the listener must accept a connection whose peer is that address, and a few bytes must survive
#       the round trip in BOTH directions.
#
#   If the listener accepts and the echo comes back, both directions are being reflected and the
#   redirector works. If nothing arrives, the redirector's own counters say why: `seen` at zero is a
#   filter problem, `seen` rising with `reflected` at zero is the loop guard refusing everything, and
#   `reflected` rising with nothing accepted is a delivery problem.
#
#   The firewall: the listener now receives traffic from a public address, so Windows Firewall treats
#   it as an inbound connection and drops it under the default policy. This script adds the same
#   one-name rule the launcher does, and removes it again. Pass -NoFirewall to see that failure for
#   itself.
#
# Usage (elevated - it loads the WinDivert driver):
#   .\tools\Test-RedirectDataPath.ps1
#   .\tools\Test-RedirectDataPath.ps1 -Port 16411 -KeepRule
#   .\tools\Test-RedirectDataPath.ps1 -Redirector C:\drelay\redirector\drelay-redirect.exe
#
# Exit codes: 0 = a reflected connection completed in both directions, 1 = it did not,
#             2 = usage/elevation/driver problem.
# ============================================================================

[CmdletBinding()]
param(
    [int]$Port = 16410,
    # TEST-NET-1: reserved for documentation, so nothing real can answer on it. The redirector
    # intercepts the packet before it could ever leave, which is the point.
    [string]$Target = '192.0.2.1',
    [string]$Redirector,
    [switch]$NoFirewall,
    [switch]$KeepRule,
    [int]$TimeoutSeconds = 15
)

$ErrorActionPreference = 'Stop'

function Write-Step { param([string]$Text) Write-Host $Text -ForegroundColor Cyan }
function Write-Ok   { param([string]$Text) Write-Host $Text -ForegroundColor Green }
function Write-Bad  { param([string]$Text) Write-Host $Text -ForegroundColor Red }
function Write-Warn2 { param([string]$Text) Write-Host $Text -ForegroundColor Yellow }
function Write-Note { param([string]$Text) Write-Host $Text -ForegroundColor DarkGray }

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Bad 'Run this from an elevated prompt: WinDivert installs a kernel driver and needs the token.'
    exit 2
}

$root = Split-Path -Parent $PSScriptRoot
if (-not $Redirector) {
    # The same two layouts the launcher searches: beside the jar in a release, under target\ in a
    # checkout.
    foreach ($candidate in @(
            (Join-Path $root 'redirector\drelay-redirect.exe'),
            (Join-Path $root 'target\redirector\drelay-redirect.exe'))) {
        if (Test-Path -LiteralPath $candidate) { $Redirector = $candidate; break }
    }
}
if (-not (Test-Path -LiteralPath $Redirector)) {
    Write-Bad "redirector not found: $Redirector"
    Write-Note '  build it with: proxy\redirector\build.ps1'
    exit 2
}
if (-not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $Redirector) 'WinDivert.dll'))) {
    Write-Bad "WinDivert.dll is not beside $Redirector"
    Write-Note '  build it with: proxy\redirector\build.ps1'
    exit 2
}

$ruleName = 'drelay redirect selftest'
$ready = Join-Path ([System.IO.Path]::GetTempPath()) ('drelay-redirect-' + [guid]::NewGuid().ToString('N') + '.ready')
$logDir = Split-Path -Parent $ready
$stdout = Join-Path $logDir ('drelay-redirect-' + [guid]::NewGuid().ToString('N') + '.out')
$stderr = Join-Path $logDir ('drelay-redirect-' + [guid]::NewGuid().ToString('N') + '.err')
$redirectorProcess = $null
$listener = $null
$client = $null
$accepted = $null

function Remove-Rule {
    if ($KeepRule) {
        Write-Note "  keeping the firewall rule '$ruleName' as asked"
        return
    }
    & netsh advfirewall firewall delete rule name="$ruleName" | Out-Null
}

try {
    Write-Step "redirector : $Redirector"
    Write-Step "port       : $Port"
    Write-Step "dial target: ${Target}:$Port (never actually reached)"

    if (-not $NoFirewall) {
        # Same shape as the launcher's rule, different name so this can never disturb a live run.
        & netsh advfirewall firewall delete rule name="$ruleName" | Out-Null
        & netsh advfirewall firewall add rule name="$ruleName" dir=in action=allow protocol=TCP localport=$Port profile=any | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Warn2 'could not add the firewall rule; the reflected handshake may be dropped'
        } else {
            Write-Note "  allowed inbound TCP $Port (rule '$ruleName', removed on exit)"
        }
    } else {
        Write-Warn2 '-NoFirewall: port 16410 inbound is not allowed, so the handshake may be dropped'
    }

    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any, $Port)
    $listener.Start()
    Write-Ok "listening on 0.0.0.0:$Port"

    Remove-Item -LiteralPath $ready, $stdout, $stderr -ErrorAction SilentlyContinue
    $redirectorProcess = Start-Process -FilePath $Redirector `
        -ArgumentList @('--ports', "$Port", '--ready', $ready, '--stats-interval', '1', '--samples', '4') `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden

    # The driver installs on the first WinDivertOpen, so allow it a few seconds before concluding
    # anything; the redirector writes its verdict as soon as the handle is open.
    $verdict = $null
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $ready) {
            $verdict = (Get-Content -LiteralPath $ready -Raw).Trim()
            if ($verdict) { break }
        }
        if ($redirectorProcess.HasExited) { break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $verdict) {
        Write-Bad 'the redirector did not report that the driver is open'
        if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout | ForEach-Object { Write-Note "  $_" } }
        if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr | ForEach-Object { Write-Note "  $_" } }
        exit 2
    }
    if ($verdict -like 'failed*') {
        Write-Bad "the redirector failed: $verdict"
        exit 2
    }
    Write-Ok "redirector ready ($verdict)"

    Write-Step "dialling ${Target}:$Port ..."
    $client = [System.Net.Sockets.TcpClient]::new([System.Net.Sockets.AddressFamily]::InterNetwork)
    $connect = $client.ConnectAsync([System.Net.IPAddress]::Parse($Target), $Port)
    if (-not $connect.Wait([TimeSpan]::FromSeconds($TimeoutSeconds))) {
        Write-Bad "the dial to ${Target}:$Port never completed - the redirector did not answer it"
        exit 1
    }
    if ($connect.IsFaulted) {
        Write-Bad ("the dial failed: " + $connect.Exception.GetBaseException().Message)
        Write-Note '  a refused connection means the redirect was not applied to this packet'
        exit 1
    }
    Write-Ok 'the dial completed'

    # The listener must accept it, with the public address as the peer - that is the reflection.
    $acceptTask = $listener.AcceptTcpClientAsync()
    if (-not $acceptTask.Wait([TimeSpan]::FromSeconds($TimeoutSeconds))) {
        Write-Bad "the dial completed but no local listener saw the connection"
        Write-Note '  this is the delivery half failing: the packet was acknowledged locally, not handed to'
        Write-Note '  the listener. Check the redirector counters below for reflected > 0.'
        exit 1
    }
    $accepted = $acceptTask.Result
    $peer = $accepted.Client.RemoteEndPoint.ToString()
    Write-Ok "the listener accepted a connection from $peer"
    if (-not $peer.StartsWith($Target)) {
        Write-Warn2 "the peer address is not $Target, which is not the reflection this test expects"
    }

    # Both directions: client -> listener must arrive, and the listener's answer must come back.
    Write-Step 'exchanging bytes...'
    $payload = [System.Text.Encoding]::ASCII.GetBytes('DRELATELOOP')
    $client.GetStream().Write($payload, 0, $payload.Length)
    $client.GetStream().Flush()

    $accepted.Client.ReceiveTimeout = $TimeoutSeconds * 1000
    $buffer = New-Object byte[] 64
    $received = $accepted.Client.GetStream().Read($buffer, 0, $buffer.Length)
    if ($received -le 0) {
        Write-Bad 'the listener received no bytes from the client'
        exit 1
    }
    $text = [System.Text.Encoding]::ASCII.GetString($buffer, 0, $received)
    if ($text -ne 'DRELATELOOP') {
        Write-Bad "the listener received '$text', not the bytes the client sent"
        exit 1
    }
    Write-Ok 'the listener received the client''s bytes'

    $echo = [System.Text.Encoding]::ASCII.GetBytes('DRELATEOK')
    $accepted.Client.GetStream().Write($echo, 0, $echo.Length)
    $accepted.Client.GetStream().Flush()

    $client.Client.ReceiveTimeout = $TimeoutSeconds * 1000
    $back = $client.GetStream().Read($buffer, 0, $buffer.Length)
    if ($back -le 0) {
        Write-Bad 'the client received no answer'
        exit 1
    }
    $answer = [System.Text.Encoding]::ASCII.GetString($buffer, 0, $back)
    if ($answer -ne 'DRELATEOK') {
        Write-Bad "the client received '$answer', not the listener's answer"
        exit 1
    }
    Write-Ok 'the answer came back to the client'

    Write-Host ''
    Write-Ok 'PASS: a dial to a public address was delivered to a local listener and the answer returned'
    Write-Note '  the redirector works. Start the relay with: .\drelay.ps1 -Redirect'
    exit 0
} finally {
    foreach ($socket in @($accepted, $client)) {
        if ($socket) { try { $socket.Close() } catch { } }
    }
    if ($listener) { try { $listener.Stop() } catch { } }
    if ($redirectorProcess -and -not $redirectorProcess.HasExited) {
        try { $redirectorProcess.Kill() } catch { }
        try { $redirectorProcess.WaitForExit(5000) | Out-Null } catch { }
    }
    if (Test-Path -LiteralPath $stdout) {
        $lines = @(Get-Content -LiteralPath $stdout)
        if ($lines.Count -gt 0) {
            Write-Host ''
            Write-Step 'redirector output:'
            $lines | Select-Object -Last 20 | ForEach-Object { Write-Note "  $_" }
        }
    }
    if (Test-Path -LiteralPath $stderr) {
        $errLines = @(Get-Content -LiteralPath $stderr)
        if ($errLines.Count -gt 0) {
            Write-Step 'redirector error output:'
            $errLines | Select-Object -Last 20 | ForEach-Object { Write-Note "  $_" }
        }
    }
    Remove-Rule
    Remove-Item -LiteralPath $ready, $stdout, $stderr -ErrorAction SilentlyContinue
}
