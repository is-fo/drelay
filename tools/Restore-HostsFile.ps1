# ============================================================================
#  Restore-HostsFile.ps1
# ============================================================================
# Puts the hosts file back from the backup drelay.ps1 made
# (<hosts>.drelay-backup), minus the drelay lines.
#
# WHY THIS EXISTS
#   A run of the old launcher script left the hosts file TRUNCATED TO 0 BYTES.
#   `Set-Content -Path $hostsPath -Value $kept -Encoding ASCII` opens the target for
#   writing - which zeroes it - and only then failed, so 966 lines of configuration that
#   have nothing to do with this project were lost: a large Adobe block list, Docker
#   Desktop's host.docker.internal entries, and Tailscale's MagicDNS section.
#   The claim design means drelay needs no hosts entry at all, so this restores everything
#   except the drelay lines. No current script writes the hosts file at all: drelay does not need a hosts entry.
#
# BYTE ACCURACY
#   The file is read and written as ISO-8859-1, which maps every byte to exactly one
#   character and back, so every retained line comes out byte-identical whatever the
#   original was encoded as. (This hosts file contains 3 non-ASCII bytes, which a UTF-8
#   round trip or an ASCII re-encode would silently replace.)
#
# SAFETY
#   The new content is fully written to a temp file in the same directory first. The
#   original is replaced only after that write has succeeded, using an atomic replace that
#   keeps its ACL - so a failure can never truncate the file again. The result is then read
#   back and compared line by line before anything is called done.
#
# Usage (elevated for the write; -WhatIfOnly runs unelevated):
#   .\tools\Restore-HostsFile.ps1 -WhatIfOnly          # show the diff, touch nothing
#   .\tools\Restore-HostsFile.ps1                      # restore from <hosts>.drelay-backup
#   .\tools\Restore-HostsFile.ps1 -From D:\hosts.bak   # restore from another backup
#
# Exit codes: 0 = restored and verified, 1 = restore failed, 2 = usage/configuration problem.
# ============================================================================

[CmdletBinding()]
param(
    [string]$From,
    [switch]$WhatIfOnly
)

$ErrorActionPreference = 'Stop'

function Write-Step { param([string]$Text) Write-Host $Text -ForegroundColor Cyan }
function Write-Ok   { param([string]$Text) Write-Host $Text -ForegroundColor Green }
function Write-Bad  { param([string]$Text) Write-Host $Text -ForegroundColor Red }
function Write-Warn2 { param([string]$Text) Write-Host $Text -ForegroundColor Yellow }
function Write-Note { param([string]$Text) Write-Host $Text -ForegroundColor DarkGray }

# ISO-8859-1: one character per byte in both directions, so nothing is transcoded.
$enc = [System.Text.Encoding]::GetEncoding(28591)

$hostsPath = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
if (-not $From) { $From = "$hostsPath.drelay-backup" }

Write-Step "hosts file : $hostsPath"
Write-Step "restore from: $From"

if (-not (Test-Path -LiteralPath $From)) {
    Write-Bad "backup not found: $From"
    Write-Bad 'Nothing to restore from. If a drelay run truncated the file, look for <hosts>.drelay-backup,'
    Write-Bad 'or restore C:\Windows\System32\drivers\etc\hosts from a system backup / File History.'
    exit 2
}

# --- read both sides -------------------------------------------------------------------
try {
    $backupLines = [System.IO.File]::ReadAllLines($From, $enc)
} catch {
    Write-Bad "cannot read the backup: $($_.Exception.Message)"
    exit 2
}

if ($backupLines.Count -lt 10) {
    Write-Bad "the backup has only $($backupLines.Count) line(s); refusing to restore from something that small."
    exit 2
}

$currentLines = @()
$currentState = 'readable'
try {
    $currentLines = [System.IO.File]::ReadAllLines($hostsPath, $enc)
} catch [System.IO.IOException] {
    $currentState = "locked by another process ($($_.Exception.Message -replace '\s+', ' '))"
} catch {
    $currentState = "unreadable ($($_.Exception.Message -replace '\s+', ' '))"
}

"current hosts: $($currentLines.Count) line(s), $((Get-Item -LiteralPath $hostsPath -Force).Length) byte(s) - $currentState"

# --- what the restored file will contain ------------------------------------------------
$kept = New-Object System.Collections.ArrayList
$dropped = New-Object System.Collections.ArrayList
foreach ($line in $backupLines) {
    if ($line -match 'queue\.playdarzas\.com' -or $line -match 'drelay') {
        [void]$dropped.Add($line)
        continue
    }
    [void]$kept.Add($line)
}
# The drelay block was appended after a blank line; drop the blank it leaves dangling.
while ($kept.Count -gt 0 -and ([string]$kept[$kept.Count - 1]).Trim() -eq '') {
    $kept.RemoveAt($kept.Count - 1)
}

$newText = (($kept -join "`r`n") + "`r`n")

Write-Host ''
Write-Step "restored file: $($kept.Count) line(s), $($enc.GetByteCount($newText)) byte(s)"
Write-Step "dropped drelay line(s): $($dropped.Count)"
$dropped | ForEach-Object { Write-Note "  - $_" }

Write-Host ''
Write-Note 'what comes back:'
$markers = @($kept | Where-Object {
    $_ -match '^\s*#\s*(AdobeNetBlock-(start|end)|Added by Docker Desktop|End of section|TailscaleHostsSection(Start|End))\s*$'
})
if ($markers.Count -gt 0) { $markers | ForEach-Object { Write-Note "  $_" } }
else { Write-Note '  (no recognised section markers - see the first/last lines below)' }
$mappings = @($kept | Where-Object { $_ -match '^\s*[0-9a-fA-F:.]+\s+\S' })
Write-Note ("  {0} address mapping line(s) in {1} line(s) total" -f $mappings.Count, $kept.Count)

if ($WhatIfOnly) {
    Write-Host ''
    Write-Warn2 '-WhatIfOnly: nothing was written.'
    Write-Host ''
    Write-Note 'first 3 lines that would be written:'
    $kept | Select-Object -First 3 | ForEach-Object { Write-Note "  $_" }
    Write-Note 'last 3 lines that would be written:'
    $kept | Select-Object -Last 3 | ForEach-Object { Write-Note "  $_" }
    exit 0
}

# --- elevation -------------------------------------------------------------------------
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Bad 'Run this from an elevated prompt: writing the hosts file needs it.'
    exit 2
}

# --- write: temp file first, then an atomic replace ------------------------------------
$dir = Split-Path -Parent $hostsPath
$tmp = Join-Path $dir ('.drelay-hosts-{0}.tmp' -f [guid]::NewGuid().ToString('N').Substring(0, 8))
Write-Host ''
Write-Step "writing $([System.IO.Path]::GetFileName($tmp)) beside the target, then replacing..."

try {
    [System.IO.File]::WriteAllText($tmp, $newText, $enc)
} catch {
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    Write-Bad "could not write the temporary file: $($_.Exception.Message)"
    Write-Bad 'The hosts file itself was NOT touched.'
    exit 1
}

try {
    if (Test-Path -LiteralPath $hostsPath) {
        [System.IO.File]::Replace($tmp, $hostsPath, $null)
    } else {
        [System.IO.File]::Move($tmp, $hostsPath)
    }
} catch {
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    Write-Bad "could not replace the hosts file: $($_.Exception.Message)"
    Write-Bad 'The hosts file itself was NOT modified - only the temp file existed.'
    Write-Warn2 'If another process holds the file open, close it or reboot, then re-run this script.'
    Write-Warn2 'Tailscale keeps its MagicDNS section in this file and is the usual holder:'
    Write-Warn2 '    Restart-Service Tailscale     # then re-run this script'
    exit 1
}

# --- verify by reading it back and comparing -------------------------------------------
$problems = New-Object System.Collections.ArrayList
try {
    $written = [System.IO.File]::ReadAllLines($hostsPath, $enc)
    if ($written.Count -ne $kept.Count) {
        [void]$problems.Add("line count $($written.Count), expected $($kept.Count)")
    } else {
        for ($i = 0; $i -lt $kept.Count; $i++) {
            if ($written[$i] -cne $kept[$i]) {
                [void]$problems.Add("line $($i + 1) differs")
                break
            }
        }
    }
    $leftover = @($written | Where-Object { $_ -match 'queue\.playdarzas\.com' -or $_ -match 'drelay' })
    if ($leftover.Count -gt 0) { [void]$problems.Add("$($leftover.Count) drelay line(s) still present") }
} catch {
    [void]$problems.Add("could not read the result back: $($_.Exception.Message)")
}

if ($problems.Count -gt 0) {
    Write-Bad 'the restored file did not verify:'
    $problems | ForEach-Object { Write-Bad "  - $_" }
    Write-Note "backup is still here: $From"
    exit 1
}

& ipconfig /flushdns | Out-Null

$size = (Get-Item -LiteralPath $hostsPath -Force).Length
Write-Ok "verified: $($kept.Count) lines, $size bytes, byte-for-byte identical to the intended content"
Write-Note "backup kept at: $From"

Write-Host ''
Write-Step 'resolution check:'
try {
    $q = (Resolve-DnsName queue.playdarzas.com -Type A -ErrorAction Stop | Where-Object IPAddress | Select-Object -First 1).IPAddress
    Write-Note "  queue.playdarzas.com -> $q"
    Write-Note '  (the real address, served by DNS - drelay captures it by claiming that address, not by name)'
} catch {
    Write-Warn2 "  queue.playdarzas.com did not resolve: $($_.Exception.Message)"
}
foreach ($n in 'host.docker.internal') {
    try {
        $r = (Resolve-DnsName $n -Type A -ErrorAction Stop | Where-Object IPAddress | Select-Object -First 1).IPAddress
        Write-Note "  $n -> $r"
    } catch { Write-Warn2 "  $n did not resolve" }
}

Write-Host ''
Write-Ok 'hosts file restored.'
exit 0
