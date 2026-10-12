<#
.SYNOPSIS
    The Phase 1 endurance run: N minutes of play with the hook reading live state,
    then the assertions that decide whether it counts.

.DESCRIPTION
    IMPLEMENTATION-PLAN.md's Exit 1 and PHASE1-VERIFICATION.md section 8 item 7.  The
    hook's recording window is `--live-seconds`, and the evidence files are written when
    it closes, so this script passes `-Minutes` through as that window and then checks:

      launcher exit code 0, no Application Error / .NET Runtime event for the window,
      `phase 1 complete`, frame calls consistent with the whole session, live reads
      present and plausible, the frame read reported PAUSED (the window control fired),
      the hook's own worst frame call inside the 300 us ceiling, and -- from the game's
      own log, which is ground truth (PHASE1-HANDOFF.md section 11) -- the zone
      transitions, GCs and slow frames that actually happened while the hook was in.

    WHAT IT CANNOT PROVE.  "No frame regression" is asserted for the hook's own
    measured cost only (`worst call`, measured inside the detour).  The game's own
    `[StallProbe] slow frame` lines are reported as context and never as a pass: the
    game hitches on its own, and there is no hour-long un-hooked baseline to compare
    against, so a claim either way would be invented.

.PARAMETER Minutes
    How long the hook records, in minutes.  The criterion asks for 30-60.
    `--live-seconds` is `Minutes * 60`.

.PARAMETER KeepReading
    Do not pause the frame read when the window closes.  Off by default: the pause is
    the wired control this run is meant to exercise.

.EXAMPLE
    .\Run-Endurance.ps1 -Minutes 60

.EXAMPLE
    # A first, cheaper pass at 30 minutes.
    .\Run-Endurance.ps1 -Minutes 30
#>
[CmdletBinding()]
param(
    [string] $GamePath = "E:\SteamLibrary\steamapps\common\Darza's Dominion",
    [string] $RunRoot = (Join-Path $env:LOCALAPPDATA 'drelay\hook'),
    [int]    $Minutes = 60,
    [int]    $CollectDelay = 20000,
    [string] $CaseDir,
    [switch] $KeepReading
)

$ErrorActionPreference = 'Stop'

if ($Minutes -lt 1 -or $Minutes -gt 360) { throw "-Minutes must be between 1 and 360" }

$repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$out = Join-Path $repo 'autododgeresearch\hook\out'
$launcher = Join-Path $out 'Drelay.Launcher.exe'
if (-not (Test-Path -LiteralPath $launcher)) { throw "no launcher at $launcher -- run build.ps1 first" }
if (-not (Test-Path -LiteralPath $GamePath)) { throw "no game at $GamePath" }
if (-not $CaseDir) { $CaseDir = Join-Path $repo ("autododgeresearch\hook\obj\cases\endurance-{0}m" -f $Minutes) }

New-Item -ItemType Directory -Force -Path $CaseDir | Out-Null
Get-ChildItem $CaseDir -File | Remove-Item -Force -ErrorAction SilentlyContinue

$checks = New-Object System.Collections.Generic.List[object]
function Add-Check([string] $name, [string] $state, [string] $detail) {
    $checks.Add([pscustomobject]@{ Check = $name; State = $state; Detail = $detail })
    $colour = switch ($state) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
    Write-Host ("  {0,-4} {1,-38} {2}" -f $state, $name, $detail) -ForegroundColor $colour
}

Write-Host "=== Phase 1 endurance run: $Minutes minute(s) ===" -ForegroundColor Cyan
Write-Host "case dir      : $CaseDir"
Write-Host ""
Write-Host "WHAT TO DO WHILE IT RUNS" -ForegroundColor Cyan
Write-Host "  1. cap the frame rate at 60 in the game's own settings (the design point)"
Write-Host "  2. get into gameplay and stay there for the whole window"
Write-Host "  3. change zones at least once, so a map transition is inside the window"
Write-Host "  4. alt-tab away and back once"
Write-Host "  5. do not close the game until the launcher prints 'phase 1 complete'"
Write-Host ""
Write-Host "THE WINDOW IS $Minutes MINUTE(S); the evidence is written when it closes." -ForegroundColor Yellow
Write-Host ""

$identity = foreach ($f in 'Drelay.Hook.dll', 'Drelay.Bootstrap.dll', 'Drelay.Launcher.exe') {
    $p = Join-Path $out $f
    "$f  $((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash)"
}
$identity | Set-Content (Join-Path $CaseDir 'build-identity.txt')
$identity | ForEach-Object { Write-Host "  build: $_" -ForegroundColor DarkGray }

Get-Process -Name DarzasDominion, 'Drelay.Launcher' -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "killing stale $($_.ProcessName) $($_.Id)"; $_.Kill() }
Start-Sleep -Milliseconds 500

# The game's own log is ground truth for what actually happened.  Its length is
# recorded now so the run is read from its own offset and not from an older session.
$gameLog = Join-Path $env:LOCALAPPDATA 'RippleStudio\Darza\logs\darza.log'
$logOffsetBefore = if (Test-Path -LiteralPath $gameLog) { (Get-Item -LiteralPath $gameLog).Length } else { 0 }

$runDirsBefore = @(Get-ChildItem $RunRoot -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
$startedLocal = Get-Date
$sw = [System.Diagnostics.Stopwatch]::StartNew()


$argv = @(
    '--game', "`"$GamePath`"",
    '--runroot', "`"$RunRoot`"",
    '--collect-delay', $CollectDelay,
	'--live-seconds', ($Minutes * 60),
    '--verbose'
)
$stdout = Join-Path $CaseDir 'launcher.stdout.txt'
$stderr = Join-Path $CaseDir 'launcher.stderr.txt'

# `DRELAY_KEEP_READING` is a plain `DRELAY_` name precisely so it survives the
# launcher's environment filter and reaches the hook; the child inherits it from here.
if ($KeepReading) { $env:DRELAY_KEEP_READING = '1' } else { Remove-Item env:DRELAY_KEEP_READING -ErrorAction SilentlyContinue }

$child = Start-Process -FilePath $launcher -ArgumentList $argv -PassThru -NoNewWindow `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr

# TOUCH `.Handle` BEFORE WAITING.  Windows PowerShell 5.1's Start-Process returns a
# Process whose exit code is unreachable (`$child.ExitCode` is $null, and `Refresh()`
# does not help) unless the process handle has been materialised first; PowerShell 7
# is unaffected.  This cost both acceptance runs of 2026-10-12 a false FAIL on
# "launcher exit code 0" while the launcher had in fact exited 0.  Measured on this
# machine: PS 5.1.19041.6328 with -PassThru -NoNewWindow and redirection gives $null
# without this line and the real code with it.
$null = $child.Handle

$timeoutMs = ($Minutes * 60 + 300) * 1000
if (-not $child.WaitForExit($timeoutMs)) {
    Write-Warning "the launcher did not exit within $([int]($timeoutMs / 1000)) s; killing it"
    $child.Kill()
    $child.WaitForExit(10000) | Out-Null
}
$sw.Stop()
$exitCode = $child.ExitCode

$runDir = Get-ChildItem $RunRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $runDirsBefore -notcontains $_.Name } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $runDir) { throw "the launcher never created a run directory under $RunRoot" }
Write-Host ""
Write-Host "run directory : $($runDir.FullName)"
Write-Host "launcher exit : $exitCode after $([int]$sw.Elapsed.TotalSeconds) s"
"exit=$exitCode`nelapsed_s=$([int]$sw.Elapsed.TotalSeconds)`nminutes=$Minutes`nrundir=$($runDir.FullName)" |
    Set-Content (Join-Path $CaseDir 'run.txt')

Get-ChildItem $runDir.FullName -File -ErrorAction SilentlyContinue |
    Copy-Item -Destination $CaseDir -Force -ErrorAction SilentlyContinue

# --- the game's own log, from the offset this run started at ----------------------
$newGameLines = @()
if (Test-Path -LiteralPath $gameLog) {
    $fs = [System.IO.File]::Open($gameLog, [System.IO.FileMode]::Open,
                                 [System.IO.FileAccess]::Read,
                                 [System.IO.FileShare]::ReadWrite)
    try {
        $length = $fs.Length
        # A rotated or truncated log would make the offset meaningless; read it all
        # rather than seek past the end and report an empty window.
        $from = if ($length -ge $logOffsetBefore) { $logOffsetBefore } else { 0 }
        $fs.Seek($from, [System.IO.SeekOrigin]::Begin) | Out-Null
        $reader = New-Object System.IO.StreamReader($fs)
        $newGameLines = @($reader.ReadToEnd() -split "`r?`n")
    } finally { $fs.Dispose() }
}
$newGameLines | Set-Content (Join-Path $CaseDir 'darza.log.window.txt')

# --- event log --------------------------------------------------------------------
$since = $startedLocal.AddSeconds(-5)
$events = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $since } -ErrorAction SilentlyContinue |
    Where-Object { $_.ProviderName -in @('.NET Runtime', 'Application Error', 'Windows Error Reporting') }
$events | ForEach-Object {
    "----- $($_.TimeCreated.ToUniversalTime().ToString('o'))  $($_.ProviderName)  id=$($_.Id)"
    $_.Message
    ''
} | Set-Content (Join-Path $CaseDir 'eventlog.txt')

# --- assertions --------------------------------------------------------------------
$summaryLines = if (Test-Path (Join-Path $CaseDir 'summary.txt')) { Get-Content (Join-Path $CaseDir 'summary.txt') } else { @() }
$hookLines = if (Test-Path (Join-Path $CaseDir 'hook.log')) { Get-Content (Join-Path $CaseDir 'hook.log') } else { @() }
function Get-Summary([string] $pattern) {
    $m = $summaryLines | Select-String -Pattern $pattern | Select-Object -First 1
    if ($m) { $m.Line.Trim() } else { '' }
}

Write-Host ""
Write-Host "--- the run survived ---" -ForegroundColor Cyan
Add-Check 'launcher exit code 0' `
    $(if ($null -eq $exitCode) { 'GAP' } elseif ($exitCode -eq 0) { 'PASS' } else { 'FAIL' }) `
    $(if ($null -eq $exitCode) { 'the host did not expose the launcher exit code; this is not a pass' } else { "exit=$exitCode" })
$faults = @($events | Where-Object { $_.ProviderName -in @('.NET Runtime', 'Application Error') })
Add-Check 'no fault recorded for the window' $(if ($faults.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
    $(if ($faults.Count -eq 0) { "$Minutes minute(s) with no Application Error / .NET Runtime event" } else { "$($faults.Count) event(s); see eventlog.txt" })
$complete = $hookLines | Select-String -Pattern 'phase 1 complete' | Select-Object -First 1
Add-Check 'the run reached phase 1 complete' $(if ($complete) { 'PASS' } else { 'FAIL' }) `
    $(if ($complete) { $complete.Line.Trim() } else { 'no such line in hook.log' })

Write-Host ""
Write-Host "--- the window covered the session ---" -ForegroundColor Cyan
$calls = $null
if ((Get-Summary 'calls\s*:') -match 'calls\s*:\s*(\d+)') { $calls = [int]$Matches[1] }
$expectedMin = $Minutes * 60 * 30      # a 30 Hz average floor
Add-Check 'frame calls cover the window' `
    $(if ($null -ne $calls -and $calls -ge $expectedMin) { 'PASS' } elseif ($null -eq $calls) { 'FAIL' } else { 'FAIL' }) `
    $(if ($null -ne $calls) { "$calls frame(s) over $([int]$sw.Elapsed.TotalSeconds) s = $([math]::Round($calls / $sw.Elapsed.TotalSeconds, 1)) Hz average; floor for $Minutes min is $expectedMin" } else { 'no calls line in summary.txt' })
$frameRowLine = Get-Summary 'frame rows'
Add-Check 'the frame ring is reported' 'GAP' "$frameRowLine -- an hour cannot fit the ring; the counters above are the endurance evidence, not frames.csv"

Write-Host ""
Write-Host "--- the read ran, and then stopped ---" -ForegroundColor Cyan
$reads = Get-Summary 'frame reads'
$okCount = $null
if ($reads -match ':\s*(\d+)\s+ok') { $okCount = [int]$Matches[1] }
Add-Check 'live reads happened' $(if ($null -ne $okCount -and $okCount -gt 0) { 'PASS' } else { 'FAIL' }) $reads
$valueLine = Get-Summary 'read value'
$value = $null
if ($valueLine -match ':\s*(-?[\d.]+)') { $value = [double]$Matches[1] }
Add-Check 'the value read is plausible' $(if ($null -ne $value -and [math]::Abs($value) -lt 100000) { 'PASS' } else { 'FAIL' }) $valueLine
$readState = Get-Summary 'frame read\s+:'
Add-Check 'the read was paused at the window close' `
    $(if ($KeepReading) { 'GAP' } elseif ($readState -match 'PAUSED') { 'PASS' } else { 'FAIL' }) `
    $(if ($KeepReading) { '-KeepReading was passed, so the pause is deliberately off' } else { $readState })

Write-Host ""
Write-Host "--- the hook's own cost ---" -ForegroundColor Cyan
$worstLine = Get-Summary 'worst call'
$worst = $null
if ($worstLine -match ':\s*(\d+)\s*us') { $worst = [int]$Matches[1] }
Add-Check 'worst frame call within the 300 us ceiling' `
    $(if ($null -eq $worst) { 'FAIL' } elseif ($worst -le 300) { 'PASS' } else { 'FAIL' }) `
    "$worstLine -- the first call is the slow one (274 us measured at 60 Hz); the ceiling is HOOK-ARCHITECTURE.md section 7"

Write-Host ""
Write-Host "--- what the game's own log says ---" -ForegroundColor Cyan
$maps = @($newGameLines | Select-String -Pattern 'Now playing: ([^,]+), From Map Info: True' |
    ForEach-Object { $_.Matches[0].Groups[1].Value.Trim() } | Sort-Object -Unique)
Add-Check 'a map transition is in the window' $(if ($maps.Count -ge 2) { 'PASS' } else { 'GAP' }) `
    "$($maps.Count) distinct map-info track(s): $($maps -join ', ') -- a proxy, because the log names the track and not the map"
$gcCount = @($newGameLines | Select-String -Pattern 'GC').Count
Add-Check 'a collection happened while the hook was in' $(if ($gcCount -ge 1) { 'PASS' } else { 'GAP' }) `
    "$gcCount line(s) mentioning GC in the game's log for this window"
$stalls = @($newGameLines | Select-String -Pattern '\[StallProbe\] slow frame (\d+) ms')
$stallMax = 0
foreach ($s in $stalls) { if ($s.Line -match 'slow frame (\d+) ms') { $m = [int]$Matches[1]; if ($m -gt $stallMax) { $stallMax = $m } } }
Add-Check 'the game hitched on its own, for context only' 'GAP' `
    "$($stalls.Count) slow-frame report(s), worst $stallMax ms -- the game's own watchdog, not the hook's cost; there is no un-hooked hour to compare with"

# --- verdict -----------------------------------------------------------------------
$failed = @($checks | Where-Object { $_.State -eq 'FAIL' })
$gaps = @($checks | Where-Object { $_.State -eq 'GAP' })
$verdict = if ($failed.Count -eq 0) { "ENDURANCE PASS ($Minutes min)" } else { "ENDURANCE FAIL ($Minutes min)" }
Write-Host ""
Write-Host ("{0} -- {1} passed, {2} failed, {3} gap(s)" -f $verdict, ($checks.Count - $failed.Count - $gaps.Count), $failed.Count, $gaps.Count) `
    -ForegroundColor $(if ($failed.Count -eq 0) { 'Green' } else { 'Red' })
"$verdict`n$($checks | Format-Table -AutoSize | Out-String)" | Set-Content (Join-Path $CaseDir 'verdict.txt')
Write-Host "artifacts in $CaseDir"

if ($failed.Count -gt 0) { exit 1 }
