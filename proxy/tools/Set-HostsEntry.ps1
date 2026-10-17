# ============================================================================
#  Set-HostsEntry.ps1
# ============================================================================
# Points the game's NAME-addressed services at this machine, so the client's own
# resolution puts the relay in the path for them. It writes ONE marked block and
# nothing else, and -Release removes that block again.
#
# WHY THIS EXISTS INSTEAD OF AN ADDRESS CLAIM
#   A claim works by assigning the SERVER's address to the loopback interface. The client
#   then reaches the relay - but the relay's own upstream dial goes to that same address,
#   which is now local, so the dial never reaches the internet: measured with the queue,
#   the client's login packet goes upstream and nothing ever comes back, and without an
#   upstream bind the relay instead dials itself in an exponential cascade. A hosts entry
#   has no such conflict: the client resolves the NAME to a local address and the relay
#   dials the real, unresolved-to-local address from the route table.
#
#   The rule this encodes: a route can be captured this way only when the client dials a
#   NAME. The route table's "refresh" says which: a DNS name means the client resolves it
#   and can be redirected; "serverlist" means the client dials a bare IP out of the game's
#   own server list, which a hosts file cannot touch.
#
# WHY IT IS A BLOCK AND NOT ONE LINE
#   The old launcher appended a bare "<address> queue.playdarzas.com" line and left it
#   behind, which breaks the client whenever the relay is not running: the name points at a
#   local address with nothing listening. The block is self-describing and idempotent, and
#   tools\Restore-HostsFile.ps1 already drops any line matching "drelay" or
#   "queue.playdarzas.com", so the two tools agree about what ours looks like.
#
# BYTE ACCURACY AND SAFETY
#   Same disciplines as Restore-HostsFile.ps1, and for the same reason - an earlier script
#   truncated this file to 0 bytes and took 966 unrelated lines with it:
#     * read and written as ISO-8859-1, so every retained byte survives the round trip;
#     * a backup is taken to <hosts>.drelay-backup before the first modification;
#     * the new content is written to a temp file in the same directory and then swapped in
#       with File.Replace, which keeps the target's ACL;
#     * the result is read back and compared line by line before anything is called done.
#
# Usage (elevated for add/release; -Status and -WhatIfOnly run unelevated):
#   .\tools\Set-HostsEntry.ps1 -Status
#   .\tools\Set-HostsEntry.ps1                     # write the block for this route table
#   .\tools\Set-HostsEntry.ps1 -Release            # remove it
#   .\tools\Set-HostsEntry.ps1 -WhatIfOnly         # show the diff, touch nothing
#   .\tools\Set-HostsEntry.ps1 -Address 192.168.0.11
#   .\tools\Set-HostsEntry.ps1 -HostsPath C:\temp\hosts   # for a test, no elevation
#
# Exit codes: 0 = as asked and verified, 1 = the write or its verification failed,
#             2 = usage or elevation problem.
# ============================================================================

[CmdletBinding()]
param(
    [string]$Config,
    [string]$Address = '127.0.0.1',
    [switch]$Release,
    [switch]$Status,
    [switch]$WhatIfOnly,
    [string]$HostsPath
)

$ErrorActionPreference = 'Stop'

if (-not $Config) { $Config = Join-Path (Split-Path -Parent $PSScriptRoot) 'relay-routes.json' }
if (-not $HostsPath) { $HostsPath = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts' }

function Write-Step { param([string]$Text) Write-Host $Text -ForegroundColor Cyan }
function Write-Ok   { param([string]$Text) Write-Host $Text -ForegroundColor Green }
function Write-Bad  { param([string]$Text) Write-Host $Text -ForegroundColor Red }
function Write-Warn2 { param([string]$Text) Write-Host $Text -ForegroundColor Yellow }
function Write-Note { param([string]$Text) Write-Host $Text -ForegroundColor DarkGray }

$enc = [System.Text.Encoding]::GetEncoding(28591)   # ISO-8859-1: one byte <-> one char
$BlockStart = '# >>> drelay (hosts capture) >>>'
$BlockEnd   = '# <<< drelay <<<'

# --- which names the route table needs ---------------------------------------------------
$names = New-Object System.Collections.ArrayList
if (Test-Path -LiteralPath $Config) {
    $table = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
    foreach ($route in $table.routes) {
        $refresh = [string]$route.refresh
        # A DNS name is resolvable by the client and therefore redirectable.
        # "serverlist" means the client dials a bare IP and a hosts file cannot help.
        if ($refresh -and $refresh -ne 'serverlist' -and $refresh -notmatch '^[0-9.]+$') {
            [void]$names.Add($refresh)
        }
    }
} else {
    Write-Bad "route table not found: $Config"
    exit 2
}

Write-Step "hosts file : $HostsPath"
Write-Step "route table: $Config"
if ($names.Count -eq 0) {
    Write-Warn2 'no name-addressed route in this table, so there is nothing to capture this way.'
    Write-Note '  routes addressed by a bare IP (refresh: "serverlist") cannot be captured by hosts.'
    exit 0
}
Write-Step ("names to capture: " + ($names -join ', '))

$wanted = @($names | ForEach-Object { "$Address $_" })

# --- current content ---------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $HostsPath)) {
    Write-Bad "hosts file not found: $HostsPath"
    exit 2
}
$lines = [System.IO.File]::ReadAllLines($HostsPath, $enc)
Write-Note "current hosts: $($lines.Count) line(s), $((Get-Item -LiteralPath $HostsPath -Force).Length) byte(s)"

$inBlock = $false
$present = New-Object System.Collections.ArrayList
foreach ($line in $lines) {
    if ($line -eq $BlockStart) { $inBlock = $true; continue }
    if ($line -eq $BlockEnd)   { $inBlock = $false; continue }
    if ($inBlock) { [void]$present.Add($line) }
}

if ($Status) {
    if ($present.Count -eq 0) { Write-Warn2 'not captured: no drelay block in the hosts file' }
    else {
        Write-Ok 'the hosts file has a drelay block:'
        $present | ForEach-Object { Write-Note "  $_" }
    }
    # Lines outside the block that would still shadow the name for the same reason - the old
    # launcher's bare entry looked exactly like this, and it is what this tool removes.
    $outside = New-Object System.Collections.ArrayList
    $inBlock = $false
    foreach ($line in $lines) {
        if ($line -eq $BlockStart) { $inBlock = $true; continue }
        if ($line -eq $BlockEnd)   { $inBlock = $false; continue }
        if (-not $inBlock) { [void]$outside.Add($line) }
    }
    $stale = @($outside | Where-Object { $_ -match 'queue\.playdarzas\.com' })
    if ($stale.Count -gt 0) {
        Write-Warn2 'stale entries outside the block (a -Release/add will replace them):'
        $stale | ForEach-Object { Write-Note "  $_" }
    }
    exit 0
}

# --- what the file should contain --------------------------------------------------------
$kept = New-Object System.Collections.ArrayList
$inBlock = $false
foreach ($line in $lines) {
    if ($line -eq $BlockStart) { $inBlock = $true; continue }
    if ($line -eq $BlockEnd)   { $inBlock = $false; continue }
    if ($inBlock) { continue }
    # Retired bare entries (the old launcher's) match this and are replaced by the block.
    if ($line -match 'queue\.playdarzas\.com' -and $line -notmatch '^\s*#') { continue }
    [void]$kept.Add($line)
}
while ($kept.Count -gt 0 -and ([string]$kept[$kept.Count - 1]).Trim() -eq '') { $kept.RemoveAt($kept.Count - 1) }

if (-not $Release) {
    [void]$kept.Add('')
    [void]$kept.Add($BlockStart)
    [void]$kept.Add('# written by drelay, removed by: Set-HostsEntry.ps1 -Release')
    [void]$kept.Add('# the relay dials the route table address, not this one, so it still escapes')
    foreach ($entry in $wanted) { [void]$kept.Add($entry) }
    [void]$kept.Add($BlockEnd)
}

$newText = (($kept -join "`r`n") + "`r`n")

Write-Host ''
Write-Step ("resulting file: {0} line(s), {1} byte(s)" -f $kept.Count, $enc.GetByteCount($newText))
if ($Release) { Write-Note 'the drelay block is removed; everything else is retained byte for byte' }
else { $wanted | ForEach-Object { Write-Note "  + $_" } }

if ($WhatIfOnly) {
    Write-Host ''
    Write-Warn2 '-WhatIfOnly: nothing was written.'
    exit 0
}

# --- elevation ---------------------------------------------------------------------------
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$isDefaultPath = ($HostsPath -eq (Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'))
if ($isDefaultPath -and -not $elevated) {
    Write-Bad 'Run this from an elevated prompt: writing the hosts file needs it.'
    exit 2
}

# --- backup once, before the first modification ------------------------------------------
if (-not $Release) {
    $backup = "$HostsPath.drelay-backup"
    if (-not (Test-Path -LiteralPath $backup)) {
        Copy-Item -LiteralPath $HostsPath -Destination $backup -Force
        Write-Ok "backup written: $backup"
    }
}

# --- write: temp file first, then an atomic replace ---------------------------------------
$dir = Split-Path -Parent $HostsPath
$tmp = Join-Path $dir ('.drelay-hosts-{0}.tmp' -f [guid]::NewGuid().ToString('N').Substring(0, 8))
Write-Step 'writing beside the target, then replacing...'
try {
    [System.IO.File]::WriteAllText($tmp, $newText, $enc)
} catch {
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    Write-Bad "could not write the temporary file: $($_.Exception.Message)"
    Write-Bad 'The hosts file itself was NOT touched.'
    exit 1
}
try {
    if (Test-Path -LiteralPath $HostsPath) {
        # [NullString]::Value, not $null: PowerShell binds a bare $null to "" for a [string]
        # parameter, and File.Replace rejects an empty backup path with "the path is not of a
        # legal form" - which reads exactly like a bad target path and is not one.
        [System.IO.File]::Replace($tmp, $HostsPath, [System.Management.Automation.Language.NullString]::Value)
    } else {
        [System.IO.File]::Move($tmp, $HostsPath)
    }
} catch {
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    Write-Bad "could not replace the hosts file: $($_.Exception.Message)"
    Write-Bad 'The hosts file itself was NOT modified - only the temp file existed.'
    Write-Warn2 'Tailscale keeps its MagicDNS section in this file and is the usual holder:'
    Write-Warn2 '    Restart-Service Tailscale     # then re-run this script'
    exit 1
}

# --- verify by reading back --------------------------------------------------------------
$problems = New-Object System.Collections.ArrayList
try {
    $written = [System.IO.File]::ReadAllLines($HostsPath, $enc)
    if ($written.Count -ne $kept.Count) {
        [void]$problems.Add("line count $($written.Count), expected $($kept.Count)")
    } else {
        for ($i = 0; $i -lt $kept.Count; $i++) {
            if ($written[$i] -cne $kept[$i]) { [void]$problems.Add("line $($i + 1) differs"); break }
        }
    }
    $block = @($written | Where-Object { $_ -eq $BlockStart })
    $want = if ($Release) { 0 } else { 1 }
    if ($block.Count -ne $want) { [void]$problems.Add("block markers present: $($block.Count), expected $want") }
} catch {
    [void]$problems.Add("could not read the result back: $($_.Exception.Message)")
}

Write-Host ''
if ($problems.Count -gt 0) {
    Write-Bad 'the hosts file did not verify:'
    $problems | ForEach-Object { Write-Bad "  - $_" }
    Write-Note "a backup is at $HostsPath.drelay-backup - restore with tools\Restore-HostsFile.ps1"
    exit 1
}

if ($Release) { Write-Ok 'released: the drelay block is gone and the rest of the file is intact.' }
else {
    Write-Ok 'captured: the client resolves these names to this machine.'
    Write-Note '  release with: tools\Set-HostsEntry.ps1 -Release'
}
exit 0
