<#
.SYNOPSIS
    A Phase 3 session: run one of the phase's experiments and publish a verdict.

.DESCRIPTION
    IMPLEMENTATION-PLAN.md's Phase 3 criteria, as runnable sessions.  Phase 3 asks one question
    with three ways of failing it, so this runner offers the three experiments that answer it:

      * `Sweep`  -- `--move-steer sweep:240`.  Walks eight evenly spaced INPUT angles, 240 frames
                    each, and logs the commanded value beside the value read back and the heading
                    the player actually travelled.  This is the calibration run: it measures the
                    `MoveAngle + (-WorldRotation + aimOffset)` transform instead of assuming it,
                    and it is the run whose displacement log is the per-frame half of the move
                    budget (PHASE3-SCOPE.md section 5.2 measured the other half offline).
      * `Hold`   -- `--move-steer hold:DEG:FRAMES`.  One world heading, the shape the solver's
                    output takes.  A run of this is what "the dodge can steer" will look like.
      * `Escape` -- `--escape-test SEC`.  Calls `World.RequestEscape()` once, from inside the
                    game, SEC seconds after the world exists.  This is the FLOOR -- the thing
                    reached when no admissible move avoids lethal damage -- and it is tested
                    alone, before the solver exists to need it, because `escape` was reported
                    MISSING in the live run of 2026-10-13 for the one reason a target can be
                    missing with nothing wrong: nothing had ever called it.

    It can also run all three end to end (`-Experiment All`), which is the cheapest way for an
    operator to answer the whole spike in one sitting.

    WHY A SCRIPT AND NOT A COMMAND LINE.  Same reason as `Run-Phase2Session.ps1`: the evidence
    only becomes a result once someone has read it out and said which run it came from, and a
    run whose assertions live in prose is a run nobody re-checks.

    IT LAUNCHES THE GAME AND WAITS FOR A PERSON.  The client's menus are not automated from
    outside -- the window renders black to every capture path tried (PHASE2-SCOPE.md section
    4.13) -- so this starts the game, says what to do, waits, closes it, and analyses.  **The
    character will walk on its own during Sweep and Hold.**  Run it somewhere it does not matter.

    THE GAME CRASHING WHEN YOU CLOSE IT IS EXPECTED AND IS NOT THIS RUN'S RESULT.  Every Phase 3
    run so far has left a `crash.txt` and an event-log `0x80131506` at the moment the operator
    closed the game; a control run with steering switched off and the movement target unpatched
    reproduced it, so it belongs to the hook's long-standing close behaviour and not to this
    phase (PHASE3-SCOPE.md section 5.1). Read the verdict, not the crash file.

.PARAMETER Experiment
    Sweep, Hold, Escape, or All.  Default Sweep.

.PARAMETER Seconds
    How long to keep the game open.  **This has to outlast the phases or the run proves nothing**:
    `sweep:240` needs about 35 s of gameplay for its eight phases and `hold` 600 frames is about
    10 s, so the default is generous and a run closed early reports "no phase summary" -- which is
    what the first two live attempts show.  It does NOT include the game's own startup: the hook's
    collection delay plus the time to reach a zone is on top.

.PARAMETER Character
    The character slot for the `Hold` experiment's preview.  Recorded in the verdict so the run
    can be attributed; it is not sent to the game.

.PARAMETER RelayLog
    The relay's session log, if the relay was run alongside.  Needed for the two checks that only
    the wire can answer: whether the server sent `GmKicked` (movement validation) and whether the
    escape actually left the machine (`GmEscape` / `EscapeAck`).  Without it those checks report
    "no relay log given" rather than a pass.

.PARAMETER AnalyseOnly
    Skip the launch and analyse a run directory that already exists.  This is how the analysis is
    verified without a game, and how a session that has already been played is re-read.

.EXAMPLE
    .\Run-Phase3Session.ps1 -Experiment Sweep -Seconds 240

.EXAMPLE
    # All three, one after another -- the operator stays in the world between them.
    .\Run-Phase3Session.ps1 -Experiment All -Seconds 240 -RelayLog .\logs\relay-....log

.EXAMPLE
    # Re-read a run that has already been played.
    .\Run-Phase3Session.ps1 -AnalyseOnly "$env:LOCALAPPDATA\drelay\hook\20261014-023248-p5392"
#>
[CmdletBinding()]
param(
    [ValidateSet('Sweep', 'Hold', 'Escape', 'All')]
    [string] $Experiment = 'Sweep',
    [string] $GamePath = "E:\SteamLibrary\steamapps\common\Darza's Dominion",
    [string] $RunRoot = (Join-Path $env:LOCALAPPDATA 'drelay\hook'),
    [int]    $Seconds = 240,
    [int]    $HoldDegrees = 90,
    [int]    $HoldFrames = 600,
    [int]    $EscapeAfterSeconds = 25,
    [string] $RelayLog,
    [string] $CaseDir,
    [string] $AnalyseOnly
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$out = Join-Path $repo 'autododgeresearch\hook\out'
$launcher = Join-Path $out 'Drelay.Launcher.exe'
$report = Join-Path $repo 'autododgeresearch\tools\steering_report.py'
if (-not $CaseDir) { $CaseDir = Join-Path $repo 'autododgeresearch\hook\obj\cases\phase3-session' }
New-Item -ItemType Directory -Force -Path $CaseDir | Out-Null

$checks = New-Object System.Collections.Generic.List[object]
function Add-Check([string] $name, [string] $state, [string] $detail) {
    $checks.Add([pscustomobject]@{ Check = $name; State = $state; Detail = $detail })
    $colour = switch ($state) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
    Write-Host ("  {0,-4} {1,-52} {2}" -f $state, $name, $detail) -ForegroundColor $colour
}

Write-Host "=== Phase 3 session ===" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# The experiment commands
# ---------------------------------------------------------------------------
function Get-SteerArgument([string] $which) {
    switch ($which) {
        'Sweep'  { return @('--move-steer', "sweep:240") }
        'Hold'   { return @('--move-steer', "hold:${HoldDegrees}:${HoldFrames}") }
        'Escape' { return @('--escape-test', "$EscapeAfterSeconds") }
        default  { throw "no steer argument for '$which'" }
    }
}

function Get-ExperimentNote([string] $which) {
    switch ($which) {
        'Sweep'  { return "the character will WALK, turning a quarter-turn every 240 frames (~4 s). Let it." }
        'Hold'   { return "the character will WALK at $HoldDegrees deg for about $([math]::Round($HoldFrames / 60.0, 1)) s, then stop." }
        'Escape' { return "nothing moves; the hook will press the nexus ONCE, about $EscapeAfterSeconds s after the world loads." }
        default  { return "" }
    }
}

$toRun = if ($Experiment -eq 'All') { @('Sweep', 'Hold', 'Escape') } else { @($Experiment) }

# ---------------------------------------------------------------------------
# Launch, or analyse
# ---------------------------------------------------------------------------
$launchedRuns = New-Object System.Collections.Generic.List[string]

if ($AnalyseOnly) {
    if (-not (Test-Path -LiteralPath $AnalyseOnly)) { throw "no run directory at $AnalyseOnly" }
    $launchedRuns.Add((Get-Item -LiteralPath $AnalyseOnly).FullName) | Out-Null
    Write-Host "analysing     : $AnalyseOnly" -ForegroundColor Yellow
} else {
    if (-not (Test-Path -LiteralPath $launcher)) { throw "no launcher at $launcher -- run hook\build.ps1 first" }

    foreach ($which in $toRun) {
        Write-Host ""
        Write-Host "--- $which ---" -ForegroundColor Cyan
        Write-Host "  launch      : $launcher" -ForegroundColor DarkGray
        Write-Host "  experiment  : $which" -ForegroundColor DarkGray
        Write-Host ("  NOTE        : {0}" -f (Get-ExperimentNote $which)) -ForegroundColor Yellow
        Write-Host "  Get into a world and stay there. Closing the game early is the one way to" -ForegroundColor Yellow
        Write-Host "  waste the run: the phases need the seconds they ask for." -ForegroundColor Yellow

        # THE RUN ROOT IS PASSED, NOT INHERITED. A harness-launched run cannot create its own
        # directory under %LOCALAPPDATA% (the agent sandbox confines child processes to the
        # workspace), and the launcher already has the switch for it.
        $argv = @('--game', $GamePath, '--live-seconds', "$Seconds", '--runroot', $RunRoot)
        $argv += Get-SteerArgument $which
        Write-Host "  command     : Drelay.Launcher.exe $($argv -join ' ')" -ForegroundColor DarkGray

        # `--no-wait` would return before the evidence exists; the launcher waits for the game
        # and prints the tail itself, which is also the operator's live view of the hook.
        $before = @(Get-ChildItem -LiteralPath $RunRoot -Directory -ErrorAction SilentlyContinue |
                    Select-Object -ExpandProperty Name)
        & $launcher @argv
        $after = @(Get-ChildItem -LiteralPath $RunRoot -Directory -ErrorAction SilentlyContinue |
                   Select-Object -ExpandProperty Name)
        $new = @($after | Where-Object { $_ -notin $before })
        if ($new.Count -ge 1) {
            $newest = $new | Sort-Object -Descending | Select-Object -First 1
            $launchedRuns.Add((Join-Path $RunRoot $newest)) | Out-Null
            Write-Host "  run dir     : $(Join-Path $RunRoot $newest)" -ForegroundColor DarkGray
        } else {
            Add-Check "$which produced a run directory" 'FAIL' 'no new directory under the run root'
        }
    }
}

# ---------------------------------------------------------------------------
# The report, once per run
# ---------------------------------------------------------------------------
$allText = New-Object System.Collections.Generic.List[string]
foreach ($dir in $launchedRuns) {
    Write-Host ""
    Write-Host "--- report: $dir ---" -ForegroundColor Cyan
    $argv = @($report, $dir)
    if ($RelayLog) { $argv += $RelayLog }
    $text = & python @argv 2>&1 | Out-String
    Write-Host $text
    $allText.Add($text) | Out-Null
}

# ---------------------------------------------------------------------------
# Verdict
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=== verdict ===" -ForegroundColor Cyan

$joined = ($allText -join "`n")

# The three things the experiment is for, in the order the plan states them.  Each is read from
# the report's own PASS/FAIL lines rather than recomputed here, so there is one place that
# decides what a run means and this script only states it.
$steered = $joined -match 'PASS A the input landed'
$moved = $joined -match 'PASS B the integrator acted'
$direction = $joined -match "PASS C the direction was obeyed" -or $joined -match "PASS C'"
$patched = $joined -match 'PASS the movement target was PATCHED'
$noKick = $joined -match 'PASS D the server did not object'
$escapeCalled = $joined -match 'PASS F the escape call was MADE and RETURNED'
$escapeWire = $joined -match "PASS F'' the escape reached the wire"
$escapeAsked = $joined -notmatch 'PASS F the escape floor \(not requested this run\)'

Add-Check 'the movement replacement was patched' $(if ($patched) { 'PASS' } else { 'FAIL' }) `
    'the hook wrote GameScene.GameInput from the game''s own call'

Add-Check 'the input write landed' $(if ($steered) { 'PASS' } else { 'FAIL' }) `
    'the value read back from MoveAngle equals the value written'

Add-Check 'the client acted on it' $(if ($moved) { 'PASS' } else { 'FAIL' }) `
    'the player moved on a majority of the phase''s frames'

Add-Check 'it moved in the commanded direction' $(if ($direction) { 'PASS' } else { 'FAIL' }) `
    'the per-phase observed headings follow the commanded ones'

Add-Check 'the server did not object' $(if ($noKick) { 'PASS' } else { 'FAIL' }) `
    $(if ($RelayLog) { 'no GmKicked in the relay log' } else { 'no relay log given -- cannot be witnessed in-process' })

if ($escapeAsked) {
    Add-Check 'the escape call was reached and returned' $(if ($escapeCalled) { 'PASS' } else { 'FAIL' }) `
        'World.RequestEscape() ran the client''s own gates'
    Add-Check 'the escape left the machine' $(if ($escapeWire) { 'PASS' } else { 'FAIL' }) `
        'GmEscape or the escape-cast-state pair on the wire, with EscapeAck back'
}

$failed = @($checks | Where-Object { $_.State -eq 'FAIL' }).Count
Write-Host ""
if ($failed -eq 0) {
    Write-Host "VERDICT: the movement question is answered YES for this run." -ForegroundColor Green
    if ($escapeAsked) { Write-Host "         and the escape floor was reached and acknowledged." -ForegroundColor Green }
} else {
    Write-Host "VERDICT: $failed check(s) did not pass. Read them in order -- the first failure is" -ForegroundColor Red
    Write-Host "         almost always the cause of the rest." -ForegroundColor Red
}

# The two things a reader needs and neither the report nor the checks carry.
Write-Host ""
Write-Host "  NOT a result of this run:" -ForegroundColor DarkGray
Write-Host "    a crash.txt and an event-log 0x80131506 at the moment you close the game are" -ForegroundColor DarkGray
Write-Host "    expected here. A control run with steering off reproduced them, so they belong to" -ForegroundColor DarkGray
Write-Host "    the hook's close behaviour (PHASE3-SCOPE.md section 5.1), not to Phase 3." -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
# Write it down
# ---------------------------------------------------------------------------
$verdictPath = Join-Path $CaseDir 'verdict.txt'
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("drelay hook -- Phase 3 session verdict")
$lines.Add("=====================================")
$lines.Add("")
$lines.Add("written       : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
$lines.Add("experiment    : $Experiment")
$lines.Add("seconds asked : $Seconds")
$lines.Add("relay log     : $(if ($RelayLog) { $RelayLog } else { '(none given)' })")
$lines.Add("")
$lines.Add("runs analysed :")
foreach ($dir in $launchedRuns) { $lines.Add("  $dir") }
$lines.Add("")
$lines.Add("checks")
foreach ($c in $checks) { $lines.Add(("  {0,-4} {1,-52} {2}" -f $c.State, $c.Check, $c.Detail)) }
$lines.Add("")
$lines.Add("WHAT THIS RUN ESTABLISHES, AND WHAT IT DOES NOT")
$lines.Add("  Establishes : " + $(if ($failed -eq 0) {
        "the hook steered the player through the client's own input path and the client obeyed" }
        else { "nothing on its own -- $failed check(s) did not pass; see above" }))
$lines.Add("  Does not    : that the dodge is safe or tuned. This is Spike 3.1: the actuator and the")
$lines.Add("                escape floor, with no solver behind them (DODGE-ALGORITHM.md sections 5-7).")
$lines.Add("")
$lines.Add("  A crash.txt in a run directory is NOT a result about that run: the reporter cannot see")
$lines.Add("  COR_E_EXECUTIONENGINE (0x80131506), and the close-time fault reproduces with steering")
$lines.Add("  off and the movement target unpatched. See PHASE3-SCOPE.md section 5.1.")
$lines.Add("")
$lines.Add("raw reports")
$lines.Add("-----------")
foreach ($t in $allText) {
    foreach ($l in ($t -split "`r?`n")) { $lines.Add("  " + $l) }
}
Set-Content -LiteralPath $verdictPath -Value $lines -Encoding utf8
Write-Host ""
Write-Host "verdict written to $verdictPath" -ForegroundColor Yellow

exit $(if ($failed -eq 0) { 0 } else { 1 })
