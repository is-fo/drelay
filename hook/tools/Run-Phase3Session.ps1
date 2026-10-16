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
      * `Dodge`  -- `--dodge-act`.  DELIVERABLE 3.3, AND THE RUN THAT IS NOT A CALIBRATION.
                    The solver decides at the frame tick and the pilot writes its answer at the
                    movement integrator's entry; on every frame the adapter publishes no command
                    the pilot writes nothing and the player's own input stands.  This is the first
                    session in which the decision and the game share a frame.

    `-Dodge` IS A SEPARATE SWITCH, NOT AN EXPERIMENT.  It adds the DRY RUN (`--dodge`) to whatever
    else the run is doing -- the solver decides on every frame and logs what it would have done,
    and nothing moves on its word.  That is the mode to run first, it is the paired-trial evidence
    IMPLEMENTATION-PLAN.md asks for, and it is the only shape in which the decision can be lined up
    against a session's health trace before anything acts on it.

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

.PARAMETER Trace
    SPIKE 3.2: record `trace.jsonl` in the run directory -- one line per frame with the player, the
    collision grid and every threat's sampled path.  It is OFF BY DEFAULT because it is expensive:
    about a kilobyte per bullet per frame, so a busy thirty-second session is tens of megabytes.
    The recorder turns the Phase 2 sensor on for itself, because the trace's input is that sensor's
    output.

    The file is what `tools\SolverHarness` reads.  Without a recorded trace, T7 -- "predicted and
    actual collision agree" -- cannot be evaluated at all: it is the one invariant that needs a
    session somebody actually played.

.EXAMPLE
    .\Run-Phase3Session.ps1 -Experiment Sweep -Seconds 240

.EXAMPLE
    # A recording session, then the offline solver over it.  The character will walk.
    .\Run-Phase3Session.ps1 -Experiment Hold -Seconds 300 -Trace
    tools\SolverHarness\bin\Release\SolverHarness.exe --trace <run>\trace.jsonl --collisions <run>\bootstrap.log

.EXAMPLE
    .\Run-Phase3Session.ps1 -Experiment Sweep -Seconds 240

.EXAMPLE
    # All three, one after another -- the operator stays in the world between them.
    .\Run-Phase3Session.ps1 -Experiment All -Seconds 240 -RelayLog .\logs\relay-....log

.EXAMPLE
    # THE DRY RUN.  The solver decides on every frame and logs every move it would have made;
    # nothing is written to the input.  Beat on by some bullets and read the DODGE NUMBERS line.
    .\Run-Phase3Session.ps1 -Experiment Hold -Seconds 180 -Dodge

.EXAMPLE
    # THE LIVE DODGE.  The solver steers.  Somewhere it does not matter.
    .\Run-Phase3Session.ps1 -Experiment Dodge -Seconds 180 -RelayLog .\logs\relay-....log

.EXAMPLE
    # Re-read a run that has already been played.
    .\Run-Phase3Session.ps1 -AnalyseOnly "$env:LOCALAPPDATA\drelay\hook\20261014-023248-p5392"
#>
[CmdletBinding()]
param(
    [ValidateSet('Sweep', 'Hold', 'Escape', 'Dodge', 'All')]
    [string] $Experiment = 'Sweep',
    [string] $GamePath = "E:\SteamLibrary\steamapps\common\Darza's Dominion",
    [string] $RunRoot = (Join-Path $env:LOCALAPPDATA 'drelay\hook'),
    [int]    $Seconds = 240,
    [int]    $HoldDegrees = 90,
    [int]    $HoldFrames = 600,
    [int]    $EscapeAfterSeconds = 25,
    [string] $RelayLog,
    [string] $CaseDir,
    [string] $AnalyseOnly,
    [string] $DrelayRoot,
    [switch] $Trace,
    # DELIVERABLE 3.3'S DRY RUN, ON ANY EXPERIMENT. `-Dodge` adds `--dodge` to the launch
    # whatever else the run is doing, so a `Hold` run can carry the decision stream beside the
    # steering evidence -- which is exactly the shape SPIKE3.2-OFFLINE-SOLVER.md's section 6
    # keeps asking for and the only way the dry run gets a real session.
    [switch] $Dodge,
    # NOT A FEATURE: A NAMED REFUSAL. `-Dodge` is the dry run and `-Experiment Dodge` is the
    # acting one, and that pair was confused by a caller who reached for `-DodgeAct` -- which
    # PowerShell refused with "A parameter cannot be found", a message that names what is
    # missing and not what to type. The switch exists so the mistake binds and gets an answer.
    [switch] $DodgeAct
)

# ---------------------------------------------------------------------------
# THIS SCRIPT MUST SURVIVE A FAILURE, WHICH IT DID NOT.
#
# The first version ran with `$ErrorActionPreference = 'Stop'` and wrapped the launch in
# `& $launcher @argv`. The launcher writes its own errors and returns non-zero, and a
# *native* non-zero exit under `Stop` is raised as a terminating error -- so the script
# died on the launch line, printed nothing about why, and stopped before it analysed
# anything. That is exactly what the operator saw: "prints stuff but exits very quickly".
# A diagnostic tool that quits at the first failure cannot diagnose one, so failures are
# collected and reported here instead.
# ---------------------------------------------------------------------------
$ErrorActionPreference = 'Continue'

# ---------------------------------------------------------------------------
# THE TWO NAMES THAT DO NOT EXIST, NAMED SO THE CALLER LEARNS THE REAL ONES.
#
# `-Dodge` is the DRY RUN (`--dodge`): the solver decides and logs and MOVES NOBODY.
# `--dodge-act` -- the solver actually steering -- is the `Dodge` EXPERIMENT
# (`-Experiment Dodge`). Two things named "dodge" that do opposite things is a trap,
# and it was walked into: a session was launched with `-DodgeAct`, PowerShell had no
# parameter of that name, and the command died before the game started. The message
# then was "A parameter cannot be found that matches parameter name 'DodgeAct'",
# which says what is missing and not what to type.
#
# A near-miss is REFUSED rather than ignored, because `-DodgeAct` is close enough to a
# real switch that accepting it silently would be worse than stopping: a run that
# looks like it acted and only decided is exactly the shape of mistake section 6 of
# HANDOFF-DIRECTED-DODGE.md keeps recording.
# ---------------------------------------------------------------------------
foreach ($key in $MyInvocation.BoundParameters.Keys) {
    if ($key -eq 'DodgeAct') {
        Write-Host "there is no -$key switch, and this is the pair that gets confused:" -ForegroundColor Red
        Write-Host "  -Dodge                  the DRY RUN: the solver decides, logs, and moves NOBODY" -ForegroundColor Yellow
        Write-Host "  -Experiment Dodge       the ACTING run: the solver steers the character" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "for a live acting session, recorded so it can be measured afterwards:" -ForegroundColor Cyan
        Write-Host "  .\hook\tools\Run-Phase3Session.ps1 -Experiment Dodge -Seconds 300 -Trace" -ForegroundColor Cyan
        exit 2
    }
}

# AND THE OTHER HALF OF THAT MISTAKE, WHICH COSTS EVIDENCE RATHER THAN A RUN: an ACTING session
# with no `-Trace` decides correctly and leaves nothing behind to measure. `20261015-212859-p34740`
# is that run -- `mode=LIVE`, `dryRun=False`, the right build, and `trace.jsonl` at 0 bytes.
if ($Experiment -eq 'Dodge' -and -not $Trace -and -not $AnalyseOnly) {
    Write-Host "this is the ACTING experiment with no -Trace: the dodge will steer, and the run" -ForegroundColor Yellow
    Write-Host "will leave no trace.jsonl to measure it against afterwards." -ForegroundColor Yellow
    Write-Host "  add -Trace to record it, or press on if you only want to feel it." -ForegroundColor Yellow
    Write-Host ""
}

# A LAUNCHER CANNOT CREATE A RUN DIRECTORY IN A CONFINED SHELL, AND THE FAILURE LOOKS LIKE
# NOTHING AT ALL. When this script is run from the agent harness, `Directory.CreateDirectory`
# for the run directory throws `UnauthorizedAccessException` inside the launcher, which
# prints one `FAILED:` line and exits 1 -- before the game starts. The check below is the
# difference between that and a verdict that says so.
function Test-RunRoot {
    param([string] $Path)
    try {
        if (-not (Test-Path -LiteralPath $Path)) {
            New-Item -ItemType Directory -Force -Path $Path -ErrorAction Stop | Out-Null
        }
        $probe = Join-Path $Path ('.phase3-write-probe-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $probe -ErrorAction Stop | Out-Null
        Remove-Item -LiteralPath $probe -Force -ErrorAction Stop
        return $null
    } catch {
        return $_.Exception.Message
    }
}

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$out = Join-Path $repo 'hook\out'
$launcher = Join-Path $out 'Drelay.Launcher.exe'
$report = Join-Path $repo 'hook\tools\steering_report.py'
if (-not $CaseDir) { $CaseDir = Join-Path $repo 'hook\obj\cases\phase3-session' }
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
        # THE ONE EXPERIMENT THAT IS NOT A CALIBRATION. `--dodge-act` puts the pilot in dodge mode
        # and lets the solver write the heading; every frame it publishes nothing, the player's own
        # input stands. It carries its own actuator flags, so no `--move-steer` is added beside it.
        'Dodge'  { return @('--dodge-act') }
        default  { throw "no steer argument for '$which'" }
    }
}

function Get-ExperimentNote([string] $which) {
    switch ($which) {
        'Sweep'  { return "the character will WALK, turning a quarter-turn every 240 frames (~4 s). Let it." }
        'Hold'   { return "the character will WALK at $HoldDegrees deg for about $([math]::Round($HoldFrames / 60.0, 1)) s, then stop." }
        'Escape' { return "nothing moves; the hook will press the nexus ONCE, about $EscapeAfterSeconds s after the world loads." }
        'Dodge'  { return "THE SOLVER STEERS. The character moves only where the decision sends it, and stands still where the player's own input is left alone. Run it somewhere it does not matter." }
        default  { return "" }
    }
}

$toRun = if ($Experiment -eq 'All') { @('Sweep', 'Hold', 'Escape') } else { @($Experiment) }

# ---------------------------------------------------------------------------
# Launch, or analyse
# ---------------------------------------------------------------------------
$launchedRuns = New-Object System.Collections.Generic.List[string]

if ($AnalyseOnly) {
    if (-not (Test-Path -LiteralPath $AnalyseOnly)) {
        Write-Host "no run directory at $AnalyseOnly" -ForegroundColor Red
        exit 2
    }
    $launchedRuns.Add((Get-Item -LiteralPath $AnalyseOnly).FullName) | Out-Null
    Write-Host "analysing     : $AnalyseOnly" -ForegroundColor Yellow
} else {
    if (-not (Test-Path -LiteralPath $launcher)) {
        Write-Host "no launcher at $launcher -- run hook\build.ps1 first" -ForegroundColor Red
        exit 2
    }

    # PRE-FLIGHT, AND IT IS THE CHECK THAT WAS MISSING. A run root this process cannot write
    # to makes the launcher fail before the game starts, and the only symptom is one line the
    # caller may not read. Catching it here names the problem instead.
    $rootProblem = Test-RunRoot $RunRoot
    if ($rootProblem) {
        Write-Host "the run root is not writable: $RunRoot" -ForegroundColor Red
        Write-Host "  $rootProblem" -ForegroundColor Red
        Write-Host "  Pass -RunRoot <a directory this shell can create in>, e.g." -ForegroundColor Yellow
        Write-Host "  -RunRoot (Join-Path `$env:LOCALAPPDATA 'drelay\hook')" -ForegroundColor Yellow
        exit 2
    }

    foreach ($which in $toRun) {
        Write-Host ""
        Write-Host "--- $which ---" -ForegroundColor Cyan
        Write-Host "  launch      : $launcher" -ForegroundColor DarkGray
        Write-Host "  experiment  : $which" -ForegroundColor DarkGray
        Write-Host ("  NOTE        : {0}" -f (Get-ExperimentNote $which)) -ForegroundColor Yellow
        Write-Host "  Get into a world and stay there. Closing the game early is the one way to" -ForegroundColor Yellow
        Write-Host "  waste the run: the phases need the seconds they ask for." -ForegroundColor Yellow

        # THE RUN ROOT IS PASSED, NOT INHERITED. A confined shell cannot let the launcher create
        # its own directory under %LOCALAPPDATA%, and the launcher already has the switch.
        $argv = @('--game', $GamePath, '--live-seconds', "$Seconds", '--runroot', $RunRoot)
        $argv += Get-SteerArgument $which
        # SPIKE 3.2's RECORDING, AND IT RIDES ON THE RUN RATHER THAN REPLACING IT.
        #
        # ONE FLAG, BECAUSE THE THREE DEPENDENCIES BELONG TO THE LAUNCHER. A trace needs the
        # recorder, the recorder is called from the MANAGED frame replacement, and its input is the
        # Phase 2 sensor's threat set. Miss any one of the three and the run writes a valid, empty
        # trace -- indistinguishable from a session in which no bullet came near. That is what the
        # run of `20261014-164314-p12060` produced, so `--trace` now sets all three, and this runner
        # passes the switch rather than setting an environment variable and hoping.
        if ($Trace) {
            $argv += '--trace'
            Write-Host "  trace       : the run will write trace.jsonl into its run directory" -ForegroundColor Yellow
        }
        # THE DRY RUN RIDES ON WHATEVER EXPERIMENT WAS ASKED FOR. `-Experiment Dodge` already asked
        # for the acting form, so it is not doubled here.
        if ($Dodge -and $which -ne 'Dodge') {
            $argv += '--dodge'
            Write-Host "  dodge       : the solver will DECIDE on every frame and log what it would" -ForegroundColor Yellow
            Write-Host "                have done. It will not write the input." -ForegroundColor Yellow
        }
        Write-Host "  command     : Drelay.Launcher.exe $($argv -join ' ')" -ForegroundColor DarkGray
        Write-Host "  The launcher stays attached and streams the hook's log. Close the game when" -ForegroundColor DarkGray
        Write-Host "  the character has finished walking." -ForegroundColor DarkGray

        $before = @(Get-ChildItem -LiteralPath $RunRoot -Directory -ErrorAction SilentlyContinue |
                    Select-Object -ExpandProperty Name)

        # THE LAUNCHER IS ALLOWED TO FAIL WITHOUT TAKING THIS SCRIPT WITH IT. It returns
        # non-zero on a refusal (a fingerprint mismatch, a denied run root), and that refusal is
        # the most useful thing in the transcript.
        & $launcher @argv 2>&1 | Tee-Object -Variable launcherOutput | Out-Null
        $launcherExit = $LASTEXITCODE
        foreach ($line in $launcherOutput) { Write-Host "  | $line" -ForegroundColor DarkGray }
        if ($launcherExit -ne 0) {
            Add-Check "$which launched" 'FAIL' "the launcher exited $launcherExit (see its own lines above)"
        } else {
            Add-Check "$which launched and the game was closed" 'PASS' "launcher exit 0"
        }

        $after = @(Get-ChildItem -LiteralPath $RunRoot -Directory -ErrorAction SilentlyContinue |
                   Select-Object -ExpandProperty Name)
        $new = @($after | Where-Object { $_ -notin $before })
        if ($new.Count -ge 1) {
            $newest = $new | Sort-Object -Descending | Select-Object -First 1
            $launchedRuns.Add((Join-Path $RunRoot $newest)) | Out-Null
            Write-Host "  run dir     : $(Join-Path $RunRoot $newest)" -ForegroundColor DarkGray
        } else {
            Add-Check "$which produced a run directory" 'FAIL' "no new directory under $RunRoot"
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
# NOT-ASKED IS ONLY A FINDING WHEN THERE WAS A RUN TO ASK OF. This read
# `$joined -notmatch '...not requested...'`, so an EMPTY report -- a launch that failed, or no run
# directory at all -- matched nothing and was therefore taken as "the escape was requested", and
# the verdict grew two escape checks that could only fail. A negative test against an empty set is
# not a negative result; it is no result.
$escapeAsked = ($launchedRuns.Count -gt 0) -and
               ($joined -notmatch 'PASS F the escape floor \(not requested this run\)')

# ---------------------------------------------------------------------------
# DELIVERABLE 3.3'S OWN CHECK, AND IT IS ASSERTION F ALL OVER AGAIN.
#
# The adapter's report line is read from `bootstrap.log` directly rather than through the Python
# report, because the report is the ACTUATOR's instrument and this is the solver's. And it is read
# for the same reason `SolverHarness` grew assertion F after defect 3.8: a dodge that never ran, a
# dodge whose sensor never resolved and a dodge that ran and found nothing are three different
# findings, and only the frame count separates them. `frames 0` is a failure even though every
# other number on the line is a perfectly valid zero.
# ---------------------------------------------------------------------------
$dodgeAsked = $Dodge -or ($Experiment -eq 'Dodge')
$dodgeFrames = -1
$dodgeMode = 'none'
$dodgeLine = $null
foreach ($dir in $launchedRuns) {
    $boot = Join-Path $dir 'bootstrap.log'
    if (-not (Test-Path -LiteralPath $boot)) { continue }
    $hit = Select-String -LiteralPath $boot -Pattern 'PHASE 3 DODGE NUMBERS \| mode ([A-Z ]+) \| frames (\d+)' |
           Select-Object -Last 1
    if ($hit) {
        $dodgeMode = $hit.Matches[0].Groups[1].Value.Trim()
        $dodgeFrames = [int]$hit.Matches[0].Groups[2].Value
        $dodgeLine = $hit.Line
    }
}

if ($launchedRuns.Count -eq 0) {
    Add-Check 'at least one run was produced and analysed' 'FAIL' `
        'nothing was launched or nothing was found to analyse -- the checks below cannot be read'
    Add-Check 'the launcher ran the game' 'FAIL' 'see the launcher output above'
}

Add-Check 'the movement replacement was patched' $(if ($patched) { 'PASS' } else { 'FAIL' }) `
    'the hook wrote GameScene.GameInput from the game''s own call'

# THE PHASE-BASED CHECKS BELONG TO A PLANNED WALK, AND A DODGE RUN HAS NO PLAN. `-Experiment Dodge`
# runs no phases -- the heading comes from the solver frame by frame -- so `PASS A` has nothing to
# read its `steer frame` lines from unless the solver happened to move, and `PASS B`/`C` need phase
# summaries that will never exist. Asking for them would fail a successful run for being the wrong
# experiment, which is the "negative test against an empty set" this script's own comment warns
# about. `-Dodge` BESIDE another experiment keeps them, because that run really is a planned walk.
if ($Experiment -ne 'Dodge') {
    Add-Check 'the input write landed' $(if ($steered) { 'PASS' } else { 'FAIL' }) `
        'the value read back from MoveAngle equals the value written'

    Add-Check 'the client acted on it' $(if ($moved) { 'PASS' } else { 'FAIL' }) `
        'the player moved on a majority of the phase''s frames'

    Add-Check 'it moved in the commanded direction' $(if ($direction) { 'PASS' } else { 'FAIL' }) `
        'the per-phase observed headings follow the commanded ones'
} else {
    Add-Check 'the movement replacement was exercised in dodge mode' `
        $(if ($dodgeAsked -and $dodgeFrames -ge 0) { 'PASS' } else { 'FAIL' }) `
        'the pilot writes only what DodgeAdapter publishes'
}

if ($dodgeAsked) {
    Add-Check 'the dodge decided on live frames' $(if ($dodgeFrames -gt 0) { 'PASS' } else { 'FAIL' }) `
        $(if ($dodgeLine) {
            "the adapter's own line says mode '$dodgeMode' over $dodgeFrames frame(s)"
        } else {
            'no PHASE 3 DODGE NUMBERS line in bootstrap.log -- the adapter never ran (see hook.log for why)'
        })
}

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
