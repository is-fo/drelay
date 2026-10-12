<#
.SYNOPSIS
    Exit 2a: deny the hook its log file while it runs, and assert the four properties.

.DESCRIPTION
    IMPLEMENTATION-PLAN.md Phase 1 Exit 2a, as a runnable test.

    While the hook is attached and the frame tick is patched, this script holds
    `<run dir>\hook.log` open with sharing disabled -- the mechanism every observed
    append failure has had, a second process holding the file -- releases it, lets the
    run finish, and asserts:

      1. the game is unaffected      the launcher exits 0, the run reaches
                                     `phase 1 complete`, and the Windows event log
                                     records no fault for the window;
      2. the frame cost is unchanged frames.csv's elapsed_us against a baseline run's;
                                     the first call is expected to be the one slow
                                     frame and nothing else should be;
      3. the kill was felt           `hook.log.append-failed` exists, and summary.txt
                                     carries `log appends : N failed pass(es)`;
      4. a transient kill lost nothing  `log lines: N written, 0 overflowed` with at
                                     least N lines in hook.log -- the retry caught up.

    THE LOCK IS TAKEN DURING THE COLLECTION DELAY, so it is held before the evidence
    files are written.  That is the ordering assertion 3 needs for the `summary.txt`
    line to appear at all, and it is why the default `-CollectDelay` is not zero.

    The script also proves that the lock denies a write to itself (see "control"): a
    run that reports no append failure must not be a run whose control never engaged
    -- PHASE1-HANDOFF.md section 8, third question.

.PARAMETER BaselineRunDir
    An uninterrupted run of the same build, for assertion 2's comparison.  Defaults to
    the operator's ARMED run of 2026-10-11 when it is still present.

.PARAMETER KeepReading
    Pass DRELAY_KEEP_READING=1 to the child, restoring the pre-`--live-seconds`
    behaviour.  Off by default; this test does not need it.

.EXAMPLE
    .\Run-Exit2a-LogDenial.ps1 -Verbose

.EXAMPLE
    # A different collection delay, for an operator who needs longer in the menu.
    .\Run-Exit2a-LogDenial.ps1 -CollectDelay 30000
#>
[CmdletBinding()]
param(
    [string] $GamePath = "E:\SteamLibrary\steamapps\common\Darza's Dominion",
    [string] $RunRoot = (Join-Path $env:LOCALAPPDATA 'drelay\hook'),
    [int]    $CollectDelay = 20000,
    [int]    $LiveSeconds = 20,
    [int]    $HoldSeconds = 25,
    [string] $CaseDir,
    [string] $BaselineRunDir = (Join-Path $env:LOCALAPPDATA 'drelay\hook\20261011-221035-p4364'),
    [switch] $KeepReading
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$out = Join-Path $repo 'autododgeresearch\hook\out'
$launcher = Join-Path $out 'Drelay.Launcher.exe'
if (-not (Test-Path -LiteralPath $launcher)) { throw "no launcher at $launcher -- run build.ps1 first" }
if (-not (Test-Path -LiteralPath $GamePath)) { throw "no game at $GamePath" }
if (-not $CaseDir) { $CaseDir = Join-Path $repo 'autododgeresearch\hook\obj\cases\exit2a-logdenial' }

New-Item -ItemType Directory -Force -Path $CaseDir | Out-Null
Get-ChildItem $CaseDir -File | Remove-Item -Force -ErrorAction SilentlyContinue

$checks = New-Object System.Collections.Generic.List[object]
function Add-Check([string] $name, [string] $state, [string] $detail) {
    $checks.Add([pscustomobject]@{ Check = $name; State = $state; Detail = $detail })
    $colour = switch ($state) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
    Write-Host ("  {0,-4} {1,-34} {2}" -f $state, $name, $detail) -ForegroundColor $colour
}

Write-Host "=== Exit 2a: the log target is denied to the hook ===" -ForegroundColor Cyan
Write-Host "case dir      : $CaseDir"
Write-Host "baseline      : $BaselineRunDir"

# --- the build this run is pinned to ------------------------------------------
$identity = foreach ($f in 'Drelay.Hook.dll', 'Drelay.Bootstrap.dll', 'Drelay.Launcher.exe') {
    $p = Join-Path $out $f
    $h = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash
    "$f  $h"
}
$identity | Set-Content (Join-Path $CaseDir 'build-identity.txt')
$identity | ForEach-Object { Write-Host "  build: $_" -ForegroundColor DarkGray }

# --- a stale game holds the hook DLL and poisons the next run ------------------
Get-Process -Name DarzasDominion, 'Drelay.Launcher' -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "killing stale $($_.ProcessName) $($_.Id)"; $_.Kill() }
Start-Sleep -Milliseconds 500

$runDirsBefore = @(Get-ChildItem $RunRoot -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
$startedLocal = Get-Date
$sw = [System.Diagnostics.Stopwatch]::StartNew()

# --- launch ---------------------------------------------------------------------
$argv = @(
    '--game', "`"$GamePath`"",
    '--runroot', "`"$RunRoot`"",
    '--collect-delay', $CollectDelay,
    '--live-seconds', $LiveSeconds,
    '--verbose'
)
$stdout = Join-Path $CaseDir 'launcher.stdout.txt'
$stderr = Join-Path $CaseDir 'launcher.stderr.txt'

# `DRELAY_KEEP_READING` is a plain `DRELAY_` name precisely so it survives the
# launcher's environment filter and reaches the hook.
if ($KeepReading) { $env:DRELAY_KEEP_READING = '1' } else { Remove-Item env:DRELAY_KEEP_READING -ErrorAction SilentlyContinue }

Write-Host ""
Write-Host "Launching. Get past the menu and into gameplay when the hook says collection" -ForegroundColor Cyan
Write-Host "has started; the lock is taken within the first second of the run either way." -ForegroundColor Cyan
Write-Host ""

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

# --- wait for the run directory, then deny its log -------------------------------
$runDir = $null
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline -and -not $runDir) {
    $runDir = Get-ChildItem $RunRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $runDirsBefore -notcontains $_.Name } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $runDir) { Start-Sleep -Milliseconds 100 }
}
if (-not $runDir) { throw "the launcher never created a run directory under $RunRoot" }
$hookLog = Join-Path $runDir.FullName 'hook.log'

# --- take the log away from the hook ---------------------------------------------
#
# THE MECHANISM IS A SECOND PROCESS HOLDING THE FILE, NOT A DELETE.  Windows keeps a
# deleted file alive while a handle is open, and the hook recreates the path on its
# next flush pass, so a rename or a delete tests nothing.  What every observed failure
# has actually had is another handle on that path -- a scanner, or the launcher's own
# tail -- so the denial here is `FileShare::None`, which refuses the native flusher's
# `_wfopen_s(..., "ab")` for as long as this handle lives.
#
# IT IS TAKEN DURING THE COLLECTION DELAY, BEFORE THE EVIDENCE FILES ARE WRITTEN.
# `summary.txt` prints the failed-append counter when the live window closes, so a lock
# taken after that would leave a `.append-failed` side file and a summary that says
# nothing about it.  Assertion 3 asks for both, and this ordering is what produces both.
$lockDeadline = $startedLocal.AddSeconds($CollectDelay / 1000 + 15)
$lock = $null
while ((Get-Date) -lt $lockDeadline -and -not $lock) {
    try {
        $lock = [System.IO.File]::Open($hookLog, [System.IO.FileMode]::OpenOrCreate,
                                       [System.IO.FileAccess]::Read,
                                       [System.IO.FileShare]::None)
    } catch [System.IO.IOException] {
        # The launcher's live tail opens this same path for microseconds every two
        # seconds; a refusal here is that, and it is worth another try, not a failure.
        Start-Sleep -Milliseconds 25
    }
}
if (-not $lock) { throw "could not deny $hookLog within the collection delay: another handle never let go" }
$lockTakenAt = Get-Date
Write-Host "log denied    : $hookLog (exclusive since t+$([int]($lockTakenAt - $startedLocal).TotalSeconds) s)" -ForegroundColor Yellow

# --- control: the lock really does deny a write ------------------------------------
#
# ASSERTION 3 IS ONLY EVIDENCE IF THE CONTROL ENGAGED.  A run that reports no append
# failure is a failure of assertion 3 and a pass of assertion 4, and the two are the
# same run unless this proves the denial was real -- PHASE1-HANDOFF.md section 8, third
# question.  So a second open of the same path, for writing, is attempted and must fail.
$controlOk = $false
$controlMsg = ''
$probe = $null
try {
    $probe = [System.IO.File]::Open($hookLog, [System.IO.FileMode]::Open,
                                    [System.IO.FileAccess]::Write,
                                    [System.IO.FileShare]::ReadWrite)
} catch [System.IO.IOException] {
    $controlOk = $true
    $controlMsg = $_.Exception.Message
} finally {
    if ($probe) { $probe.Dispose() }
}
"lock taken at : $($lockTakenAt.ToString('o'))`nshare mode    : None (read handle held by this script)`nwrite probe   : $(if ($controlOk) { 'refused' } else { 'SUCCEEDED -- the lock denies nothing' })`nprobe error   : $controlMsg" |
    Set-Content (Join-Path $CaseDir 'lock.txt')
Write-Host "lock control  : $(if ($controlOk) { 'a write to the log was refused' } else { 'A WRITE SUCCEEDED; the denial is not real' })" `
    -ForegroundColor $(if ($controlOk) { 'DarkGray' } else { 'Red' })

# --- hold it, then let go ----------------------------------------------------------
Write-Host ""
Write-Host "The log is denied for $HoldSeconds s, across the end of the collection delay and" -ForegroundColor Cyan
Write-Host "the start of the recording window. Stay in gameplay; do not close the game yet." -ForegroundColor Cyan
Write-Host ""
Start-Sleep -Seconds $HoldSeconds
$lock.Dispose()
$lock = $null
Write-Host "lock released : t+$([int]((Get-Date) - $startedLocal).TotalSeconds) s; the hook's retry should recover every line"
Write-Host ""
Write-Host "Now close the game once the launcher prints 'phase 1 complete'." -ForegroundColor Cyan

# --- wait for the run to finish ----------------------------------------------------
$timeoutMs = [int](($CollectDelay / 1000 + $LiveSeconds + 300) * 1000)
if (-not $child.WaitForExit($timeoutMs)) {
    Write-Warning "the launcher did not exit within $([int]($timeoutMs / 1000)) s; killing it"
    $child.Kill()
    $child.WaitForExit(10000) | Out-Null
}
$sw.Stop()
$exitCode = $child.ExitCode
Write-Host ""
Write-Host "run directory : $($runDir.FullName)"
Write-Host "launcher exit : $exitCode after $([int]$sw.Elapsed.TotalSeconds) s"

Get-ChildItem $runDir.FullName -File -ErrorAction SilentlyContinue |
    Copy-Item -Destination $CaseDir -Force -ErrorAction SilentlyContinue

"exit=$exitCode`nelapsed_s=$([int]$sw.Elapsed.TotalSeconds)`nrundir=$($runDir.FullName)`nlock_taken_local=$($lockTakenAt.ToString('o'))`nwrite_probe_refused=$controlOk" |
    Set-Content (Join-Path $CaseDir 'run.txt')

# --- event log ----------------------------------------------------------------------
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

# `elapsed_us` is the last column of frames.csv.  The first call is the JIT-warm one --
# 274 us at 60 Hz in the measured runs and the only slow one -- so it is reported and
# never compared: assertion 2 is about every call after it.
function Get-FrameCost([string] $dir) {
    $path = Join-Path $dir 'frames.csv'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $all = @(Import-Csv -LiteralPath $path |
        Where-Object { $_.elapsed_us -match '^\d+$' } |
        ForEach-Object { [int]$_.elapsed_us })
    if ($all.Count -eq 0) { return $null }
    $after = if ($all.Count -gt 1) { @($all[1..($all.Count - 1)]) } else { @() }
    $sorted = @($all | Sort-Object)
    $half = [int]($sorted.Count / 2)
    $median = if ($sorted.Count % 2 -eq 1) { $sorted[$half] } else { [int](($sorted[$half - 1] + $sorted[$half]) / 2) }
    [pscustomobject]@{
        Path            = $path
        Count           = $all.Count
        First           = $all[0]
        Worst           = ($all | Measure-Object -Maximum).Maximum
        WorstAfterFirst = $(if ($after.Count -gt 0) { ($after | Measure-Object -Maximum).Maximum } else { 0 })
        Median          = $median
    }
}

Write-Host ""
Write-Host "--- 1. the game is unaffected ---" -ForegroundColor Cyan
Add-Check 'launcher exit code 0' `
    $(if ($null -eq $exitCode) { 'GAP' } elseif ($exitCode -eq 0) { 'PASS' } else { 'FAIL' }) `
    $(if ($null -eq $exitCode) { 'the host did not expose the launcher exit code; this is not a pass' } else { "exit=$exitCode" })
$faults = @($events | Where-Object { $_.ProviderName -in @('.NET Runtime', 'Application Error') })
Add-Check 'no fault recorded for the window' $(if ($faults.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
    $(if ($faults.Count -eq 0) { 'no Application Error / .NET Runtime event since the launch' } else { "$($faults.Count) event(s); see eventlog.txt" })
$complete = $hookLines | Select-String -Pattern 'phase 1 complete' | Select-Object -First 1
Add-Check 'the run reached phase 1 complete' $(if ($complete) { 'PASS' } else { 'FAIL' }) `
    $(if ($complete) { $complete.Line.Trim() } else { 'no such line in hook.log' })

Write-Host ""
Write-Host "--- 2. the frame cost is unchanged ---" -ForegroundColor Cyan
$runCost = Get-FrameCost $runDir.FullName
$baseCost = Get-FrameCost $BaselineRunDir
Add-Check 'frames.csv carries the run''s frame calls' $(if ($runCost) { 'PASS' } else { 'FAIL' }) `
    $(if ($runCost) { "$($runCost.Count) call(s): first $($runCost.First) us, median $($runCost.Median) us, worst $($runCost.Worst) us" } else { "no frames.csv under $($runDir.FullName)" })
Add-Check 'the worst call after the first is inside the 300 us ceiling' `
    $(if ($null -eq $runCost) { 'FAIL' } elseif ($runCost.WorstAfterFirst -le 300) { 'PASS' } else { 'FAIL' }) `
    "$(if ($runCost) { "$($runCost.WorstAfterFirst) us" } else { 'no frames.csv' }) -- the ceiling is HOOK-ARCHITECTURE.md section 7"
Add-Check 'the cost matches the untouched baseline' `
    $(if ($null -eq $baseCost) { 'GAP' } elseif ($null -eq $runCost) { 'FAIL' } elseif ($runCost.WorstAfterFirst -le [math]::Max($baseCost.WorstAfterFirst * 2, 50)) { 'PASS' } else { 'FAIL' }) `
    $(if ($null -eq $baseCost) { "no frames.csv under $BaselineRunDir -- the comparison was not made" } elseif ($null -eq $runCost) { 'this run has no frames.csv' } else { "worst after the first $($runCost.WorstAfterFirst) us vs the baseline's $($baseCost.WorstAfterFirst) us (baseline median $($baseCost.Median) us, worst $($baseCost.Worst) us)" })

Write-Host ""
Write-Host "--- 3. the kill was felt ---" -ForegroundColor Cyan
Add-Check 'the lock denied a write to itself' $(if ($controlOk) { 'PASS' } else { 'FAIL' }) $controlMsg
$sidecar = Join-Path $CaseDir 'hook.log.append-failed'
$hasSidecar = Test-Path -LiteralPath $sidecar
$sideText = if ($hasSidecar) { Get-Content -LiteralPath $sidecar -Raw } else { '' }
Add-Check 'hook.log.append-failed exists' $(if ($hasSidecar) { 'PASS' } else { 'FAIL' }) `
    $(if ($hasSidecar) { (@($sideText -split "`r?`n" | Where-Object { $_ -match '^\s*(path|errno|win32 error)\s*:' }) -join ' | ') } else { "no $sidecar -- the hook never reported a failed append" })
$appendLine = Get-Summary 'log appends\s*:'
$failedPasses = $null
if ($appendLine -match ':\s*(\d+)\s+failed') { $failedPasses = [int]$Matches[1] }
Add-Check 'summary.txt counts the failed passes' `
    $(if ($null -ne $failedPasses -and $failedPasses -ge 1) { 'PASS' } else { 'FAIL' }) `
    $(if ($appendLine) { "$appendLine -- the lock preceded the evidence write by design" } else { 'no log appends line in summary.txt: the lock did not precede the write, or the denial never reached the hook' })

Write-Host ""
Write-Host "--- 4. a transient kill lost no line ---" -ForegroundColor Cyan
$linesLine = Get-Summary 'log lines\s*:'
$written = $null
$overflowed = $null
if ($linesLine -match ':\s*(\d+)\s+written,\s*(\d+)\s+overflowed') {
    $written = [int]$Matches[1]
    $overflowed = [int]$Matches[2]
}
Add-Check 'the ring discarded nothing' `
    $(if ($null -ne $overflowed -and $overflowed -eq 0 -and $written -gt 0) { 'PASS' } else { 'FAIL' }) `
    $linesLine
Add-Check 'every written line reached hook.log' `
    $(if ($null -eq $written) { 'FAIL' } elseif ($hookLines.Count -ge $written) { 'PASS' } else { 'FAIL' }) `
    "$($hookLines.Count) line(s) in hook.log against $written written -- the read cursor advances only on a successful write, so the retry is the mechanism this asserts"

# --- verdict -----------------------------------------------------------------------
$failed = @($checks | Where-Object { $_.State -eq 'FAIL' })
$gaps = @($checks | Where-Object { $_.State -eq 'GAP' })
$verdict = if ($failed.Count -eq 0) { 'EXIT 2A PASS' } else { 'EXIT 2A FAIL' }
Write-Host ""
Write-Host ("{0} -- {1} passed, {2} failed, {3} gap(s)" -f $verdict, ($checks.Count - $failed.Count - $gaps.Count), $failed.Count, $gaps.Count) `
    -ForegroundColor $(if ($failed.Count -eq 0) { 'Green' } else { 'Red' })
"$verdict`n$($checks | Format-Table -AutoSize | Out-String)" | Set-Content (Join-Path $CaseDir 'verdict.txt')
Write-Host "artifacts in $CaseDir"

if ($failed.Count -gt 0) { exit 1 }