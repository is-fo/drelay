<#
.SYNOPSIS
    A Phase 2 session: play the game with the sensor attached, then publish the four
    decision numbers.

.DESCRIPTION
    IMPLEMENTATION-PLAN.md's Phase 2 exit criteria, as a runnable session.  The phase is
    read-only -- it changes nothing about the game -- and what it produces is four numbers
    that decide whether a dodge is worth building at all:

      * the WARNING DISTRIBUTION over every collision the client resolved,
      * the FALSE NEGATIVES   (hits the sensor never predicted),
      * the FALSE POSITIVES   (predictions for bullets that never hit), and
      * the PER-FRAME COST    p50 and p99, from bucket histograms rather than a maximum.

    WHY A SCRIPT AND NOT A COMMAND LINE.  The hook writes its evidence into the shared
    block and its logs into the run directory, and the numbers only become the phase's
    numbers once someone has read them out and said which run they came from.  Phase 1
    learned this twice: `Run-Endurance.ps1` and `Run-Exit2a-LogDenial.ps1` exist because a
    run whose assertions live in prose is a run nobody re-checks.  This is the same shape,
    for the same reason.

    IT LAUNCHES THE GAME AND WAITS FOR A PERSON.  Unlike the Phase 1 runners this one
    cannot drive itself: the sensor needs a character in a zone, and the client's menus
    are not automated here.  So it starts the game, prints what to do, waits the requested
    time, kills the game, and analyses.

.PARAMETER Minutes
    How long to play.  Thirty minutes is enough to see collisions; the plan asks for
    several hours before the numbers are treated as final.

.PARAMETER AnalyseOnly
    Skip the launch and analyse a run directory that already exists.  This is how the
    analysis is verified without a game, and how a session that has already been played is
    re-read.

.EXAMPLE
    .\Run-Phase2Session.ps1 -Minutes 60

.EXAMPLE
    # Re-read a session that has already been played.
    .\Run-Phase2Session.ps1 -AnalyseOnly "$env:LOCALAPPDATA\drelay\hook\20261012-065927-p21968"
#>
[CmdletBinding()]
param(
    [string] $GamePath = "E:\SteamLibrary\steamapps\common\Darza's Dominion",
    [string] $RunRoot = (Join-Path $env:LOCALAPPDATA 'drelay\hook'),
    [int]    $Minutes = 30,
    [int]    $CollectDelayMs = 25000,
    [string] $CaseDir,
    [string] $AnalyseOnly
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$out = Join-Path $repo 'autododgeresearch\hook\out'
$launcher = Join-Path $out 'Drelay.Launcher.exe'
if (-not $CaseDir) { $CaseDir = Join-Path $repo 'autododgeresearch\hook\obj\cases\phase2-session' }
New-Item -ItemType Directory -Force -Path $CaseDir | Out-Null

$checks = New-Object System.Collections.Generic.List[object]
function Add-Check([string] $name, [string] $state, [string] $detail) {
    $checks.Add([pscustomobject]@{ Check = $name; State = $state; Detail = $detail })
    $colour = switch ($state) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
    Write-Host ("  {0,-4} {1,-46} {2}" -f $state, $name, $detail) -ForegroundColor $colour
}

Write-Host "=== Phase 2 session ===" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# Launch, or analyse
# ---------------------------------------------------------------------------
$runDir = $null

if ($AnalyseOnly) {
    $runDir = Get-Item -LiteralPath $AnalyseOnly -ErrorAction SilentlyContinue
    if (-not $runDir) { throw "no run directory at $AnalyseOnly" }
    Write-Host "analysing     : $($runDir.FullName)" -ForegroundColor Yellow
} else {
    if (-not (Test-Path -LiteralPath $launcher)) { throw "no launcher at $launcher -- run build.ps1 first" }
    if (-not (Test-Path -LiteralPath $GamePath)) { throw "no game at $GamePath" }

    # The game must not already be running: a stale process holds the hook DLL and the
    # next launch's injection fails in a way that looks like the hook refusing to install.
    Get-Process -Name DarzasDominion, 'Drelay.Launcher' -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host "killing stale $($_.ProcessName) $($_.Id)"; $_.Kill() }
    Start-Sleep -Milliseconds 500

    $runRootPath = $RunRoot
    # A run started from an agent harness cannot create its directory under
    # %LOCALAPPDATA% -- the harness confines its children to the workspace -- so the
    # runroot moves inside the repository when the default is not writable.  From an
    # operator's own shell the default is fine.
    if (-not (Test-Path -LiteralPath $runRootPath)) {
        try { New-Item -ItemType Directory -Force -Path $runRootPath | Out-Null }
        catch {
            $runRootPath = Join-Path $repo 'autododgeresearch\hook\obj\live-runs'
            New-Item -ItemType Directory -Force -Path $runRootPath | Out-Null
            Write-Host "runroot moved : $runRootPath" -ForegroundColor Yellow
        }
    }

    $dirsBefore = @(Get-ChildItem $runRootPath -Directory -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Name)
    $startedLocal = Get-Date

    Write-Host ""
    Write-Host "WHAT TO DO WHILE IT RUNS" -ForegroundColor Cyan
    Write-Host "  1. get past the menu and into a zone with a character"
    Write-Host "  2. play normally -- the sensor is read-only and changes nothing"
    Write-Host "  3. let bullets come at you; that is the whole point of the session"
    Write-Host "  4. do not close the game until this script kills it"
    Write-Host ""
    Write-Host "THE WINDOW IS $Minutes MINUTE(S)." -ForegroundColor Yellow
    Write-Host ""

    $argv = @(
        '--game', "`"$GamePath`"",
        '--runroot', "`"$runRootPath`"",
        '--collect-delay', $CollectDelayMs,
        '--live-seconds', ($Minutes * 60),
        '--frame-replacement',          # the sensor is a managed replacement, not a native detour
        '--verbose'
    )

    # `DRELAY_FRAME_SENSOR` is a plain `DRELAY_` name, which is what lets it survive the
    # launcher's environment filter -- PHASE1-HANDOFF.md section 4 trap 1.
    $savedSensor = $env:DRELAY_FRAME_SENSOR
    $env:DRELAY_FRAME_SENSOR = '1'

    $stdout = Join-Path $CaseDir 'launcher.stdout.txt'
    $stderr = Join-Path $CaseDir 'launcher.stderr.txt'
    $child = Start-Process -FilePath $launcher -ArgumentList $argv -PassThru -NoNewWindow `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    # Touch `.Handle` before waiting: Windows PowerShell 5.1 returns a Process whose exit
    # code is unreachable otherwise, which cost Phase 1's acceptance runs a false FAIL.
    $null = $child.Handle

    # Wait for the run directory, then for the session.
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline -and -not $runDir) {
        $runDir = Get-ChildItem $runRootPath -Directory -ErrorAction SilentlyContinue |
            Where-Object { $dirsBefore -notcontains $_.Name } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $runDir) { Start-Sleep -Milliseconds 250 }
    }
    if (-not $runDir) { throw "the launcher never created a run directory under $runRootPath" }
    Write-Host "run directory : $($runDir.FullName)" -ForegroundColor Yellow

    # The session's clock starts when the evidence starts, not when the process did: the
    # collection delay and the discovery window are the hook's, not the player's.
    $sessionSeconds = ($Minutes * 60) + 90
    Write-Host "playing for   : $sessionSeconds s" -ForegroundColor Yellow
    Start-Sleep -Seconds $sessionSeconds

    Get-Process -Name DarzasDominion -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host "closing the game ($($_.Id))"; $_.Kill() }
    Start-Sleep -Seconds 5
    if (-not $child.HasExited) { $child.Kill(); $child.WaitForExit(10000) | Out-Null }
    if (-not $AnalyseOnly) { $env:DRELAY_FRAME_SENSOR = $savedSensor }

    Write-Host "session ended : $([int]((Get-Date) - $startedLocal).TotalSeconds) s" -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# Evidence
# ---------------------------------------------------------------------------
$boot = if (Test-Path (Join-Path $runDir.FullName 'bootstrap.log')) {
    Get-Content (Join-Path $runDir.FullName 'bootstrap.log') -Raw
} else { '' }
$hook = if (Test-Path (Join-Path $runDir.FullName 'hook.log')) {
    Get-Content (Join-Path $runDir.FullName 'hook.log') -Raw
} else { '' }
$boot | Set-Content (Join-Path $CaseDir 'bootstrap.log')
$hook | Set-Content (Join-Path $CaseDir 'hook.log')

$bootLines = $boot -split "`r?`n"
function Find-Line([string] $pattern) {
    $m = $bootLines | Where-Object { $_ -match $pattern } | Select-Object -Last 1
    if ($m) { $m.Trim() } else { '' }
}

Write-Host ""
Write-Host "--- the sensor came up ---" -ForegroundColor Cyan
$readLine = Find-Line 'sensor read path resolved'
$samplerLine = Find-Line 'trajectory sampler resolved'
$collisionLine = Find-Line 'collision observation resolved'
Add-Check 'the read path resolved against this assembly' $(if ($readLine) { 'PASS' } else { 'FAIL' }) `
    $(if ($readLine) { ($readLine -replace '^\[INFO\] boot: ', '') } else { 'no "sensor read path resolved" line' })
Add-Check 'the trajectory sampler resolved' $(if ($samplerLine) { 'PASS' } else { 'GAP' }) `
    $(if ($samplerLine) { ($samplerLine -replace '^\[INFO\] boot: ', '') } else { 'no "trajectory sampler resolved" line' })
Add-Check 'the collision observation resolved' $(if ($collisionLine) { 'PASS' } else { 'GAP' }) `
    $(if ($collisionLine) { ($collisionLine -replace '^\[INFO\] boot: ', '') } else { 'no "collision observation resolved" line' })
$entered = @($bootLines | Where-Object { $_ -match 'MANAGED REPLACEMENT entered \(call 1\): total=\d{4,}' })
Add-Check 'the managed replacement was entered by the game' $(if ($entered.Count -gt 0) { 'PASS' } else { 'FAIL' }) `
    $(if ($entered.Count -gt 0) { $entered[0].Trim() } else { 'the replacement was never entered -- the patch did not take effect' })

Write-Host ""
Write-Host "--- the phase's four numbers ---" -ForegroundColor Cyan
$report = Find-Line 'PHASE 2 NUMBERS'
if ($report) {
    Write-Host ("  {0}" -f ($report -replace '^\[INFO\] boot: ', '')) -ForegroundColor White
} else {
    Add-Check 'the four numbers were published' 'FAIL' 'no "PHASE 2 NUMBERS" line -- the sensor did not run'
}

$warnings = @()
foreach ($line in ($bootLines | Where-Object { $_ -match 'collision: key=' })) {
    if ($line -match 'warning=([\d.]+)ms') { $warnings += [double]$Matches[1] }
}
$predictedHits = 0
$unpredictedHits = 0
foreach ($line in ($bootLines | Where-Object { $_ -match 'collision: key=' })) {
    if ($line -match 'predicted=yes') { $predictedHits++ } else { $unpredictedHits++ }
}

$warningP50 = $null
$warningP90 = $null
if ($warnings.Count -gt 0) {
    $sorted = @($warnings | Sort-Object)
    $warningP50 = $sorted[[int][math]::Floor(($sorted.Count - 1) * 0.50)]
    $warningP90 = $sorted[[int][math]::Floor(($sorted.Count - 1) * 0.90)]
}

Add-Check 'collisions were resolved by the client during the session' `
    $(if ($warnings.Count -ge 1) { 'PASS' } elseif ($report) { 'FAIL' } else { 'GAP' }) `
    "$($warnings.Count) collision decision(s) observed"

# --- WAS THE SESSION EVEN IN THE GAME? -------------------------------------
#
# THE ONE DIAGNOSIS THIS ANALYSIS WAS MISSING, and the first live sessions produced it.
# "0 collisions observed" is the correct verdict both for a session that fought for an hour
# and never got hit and for one that sat at the menu, and those need different responses
# from whoever reads it.  The sensor distinguishes them itself: `frames_read=0` with
# `unreconciled` equal to the frame count means every frame found no world, which is the
# menu -- `World.Shared` is null until a zone loads.  A session that was in the game and
# merely saw no collisions has `frames_read` in the thousands.
$lastSensor = $bootLines | Where-Object { $_ -match 'sensor: frame \d+:' } | Select-Object -Last 1
$framesRead = $null
$unreconciled = $null
$bulletsRead = $null
$bulletsFromLayer = $null
$bulletsFromObjects = $null
$layerNodesWalked = $null
if ($lastSensor -match 'unreconciled=(\d+)') { $unreconciled = [int]$Matches[1] }
if ($lastSensor -match 'frames_read=(\d+)') { $framesRead = [int]$Matches[1] }
if ($lastSensor -match 'live=(\d+) other=') { $bulletsRead = [int]$Matches[1] }
if ($lastSensor -match 'layer_live=(\d+)') { $bulletsFromLayer = [int]$Matches[1] }
if ($lastSensor -match 'objects_live=(\d+)') { $bulletsFromObjects = [int]$Matches[1] }
if ($lastSensor -match 'layer_nodes=(\d+)') { $layerNodesWalked = [int]$Matches[1] }
if ($null -ne $framesRead) {
    Add-Check 'the client produced a world during the session' `
        $(if ($framesRead -gt 0) { 'PASS' } else { 'FAIL' }) `
        $(if ($framesRead -gt 0) {
            "$framesRead frame(s) read the world; $unreconciled had no world"
        } else {
            "EVERY frame found no world ($unreconciled of them): the client never left the menu, so this session produced no numbers. The game needs a character in a zone -- the client's own window cannot be captured (BitBlt and PrintWindow both return black), so this cannot be driven from outside; it needs a person."
        })
}

# --- WAS THERE ANYTHING TO READ? -------------------------------------------
#
# THE SECOND DIAGNOSIS THIS ANALYSIS WAS MISSING, AND IT COST A SESSION.  The world check
# above passes on a Nexus, on a menu with a scene loaded, and on any frame where
# `World.Shared` is non-null -- and the evening session of 2026-10-12 passed it with
# 54 599 frames read, hours of gameplay in the game's own log, and not one bullet in the
# sensor, because the sensor was reading `World.Objects` and the client keeps its
# projectiles in `World.GameObjectLayer.Children`.  "In a world" and "reading the thing
# this phase is about" are different facts and they need different verdict lines.
Add-Check 'the sensor read projectiles from the world' `
    $(if ($null -ne $bulletsRead -and $bulletsRead -gt 0) { 'PASS' }
      elseif ($null -ne $framesRead -and $framesRead -gt 0) { 'FAIL' }
      else { 'GAP' }) `
    $(if ($null -ne $bulletsRead -and $bulletsRead -gt 0) {
        "$bulletsRead live projectile(s) on the last sensor frame: $bulletsFromLayer from the layer's $layerNodesWalked node(s), $bulletsFromObjects from World.Objects"
    } elseif ($null -ne $framesRead -and $framesRead -gt 0) {
        "0 projectile(s) on the last sensor frame after $framesRead frame(s) with a world. A session that fought and never got shot is possible, but a session that never SAW a bullet cannot produce a warning distribution, a false-negative rate or a false-positive rate -- check `objects_live`/`layer_live` above, and the game's own log at %LOCALAPPDATA%\RippleStudio\Darza\logs\darza.log for whether bullets were on screen."
    } else {
        "no sensor line carried a projectile count -- the read path did not resolve"
    })

if ($warnings.Count -gt 0) {
    Add-Check 'the warning distribution' 'PASS' `
        ("min {0:N1} ms / p50 {1:N1} / p90 {2:N1} / max {3:N1} over {4} hit(s)" -f `
            ($warnings | Measure-Object -Minimum).Minimum, $warningP50, $warningP90, `
            ($warnings | Measure-Object -Maximum).Maximum, $warnings.Count)
    Add-Check 'the false negatives' 'PASS' "$unpredictedHits hit(s) the sensor had not predicted"
}

# The cost, from the report line's own percentiles.  The phase's criterion is that the
# sensor fits the frame with room for the solver, and HOOK-ARCHITECTURE.md section 7 gives
# the frame tick 150 us of a 16.7 ms frame.
if ($report -match 'read n=(\d+) p50<=(\d+)us p99<=(\d+)us') {
    $readN = [int]$Matches[1]; $readP50 = [int]$Matches[2]; $readP99 = [int]$Matches[3]
    # The histogram's edges include the 150 us slice and the 300 us ceiling, so this is a
    # decision and not an interval: a run whose p99 lands above 150 has said so.
    Add-Check 'the read cost is inside the frame-tick slice' `
        $(if ($readP99 -le 150) { 'PASS' } elseif ($readP99 -le 300) { 'FAIL' } else { 'FAIL' }) `
        "read p50 <= $readP50 us, p99 <= $readP99 us over $readN frame(s); the slice is 150 us and the ceiling 300"
} else {
    Add-Check 'the read cost is inside the frame-tick slice' 'GAP' 'no cost percentiles in the report line'
}
if ($report -match 'sampler n=(\d+) p50<=(\d+)us p99<=(\d+)us') {
    $sampN = [int]$Matches[1]; $sampP50 = [int]$Matches[2]; $sampP99 = [int]$Matches[3]
    Add-Check 'the sampler cost is inside the frame-tick slice' `
        $(if ($sampP99 -le 150) { 'PASS' } elseif ($sampP99 -le 300) { 'FAIL' } else { 'FAIL' }) `
        "sampler p50 <= $sampP50 us, p99 <= $sampP99 us over $sampN frame(s)"
} else {
    Add-Check 'the sampler cost is inside the frame-tick slice' 'GAP' 'no sampler percentiles in the report line'
}

# THE FIFTH NUMBER, AND THE PROVENANCE OF THE OTHER FOUR.  PHASE2-SCOPE.md section 2 names
# the straight fraction separately from the four, and the source split is what makes the
# four believable: `warnings: 0` is a very different statement when the sensor read no
# bullets at all, and the 2026-10-12 evening run is exactly that case.
if ($report -match 'projectiles: (\d+) observation\(s\) from the layer.s (\d+) node\(s\), (\d+) from World\.Objects') {
    Add-Check 'the report names the projectile source' 'PASS' `
        "$($Matches[1]) from the layer's $($Matches[2]) node(s), $($Matches[3]) from World.Objects"
} else {
    Add-Check 'the report names the projectile source' 'GAP' 'no "projectiles:" field in the report line'
}
if ($report -match 'linearity: (\d+) linear, (\d+) not, straight fraction (\d+[.,]\d+)') {
    Add-Check 'the straight fraction is published' 'PASS' `
        "$($Matches[1]) linear, $($Matches[2]) not, straight fraction $($Matches[3]) -- the fifth measurement the plan names"
} else {
    Add-Check 'the straight fraction is published' 'GAP' 'no "linearity:" field in the report line'
}

if ($report -match 'false positives: (\d+)') {
    Add-Check 'the false positives' 'PASS' "$($Matches[1]) prediction(s) for bullets that never hit"
}

# ---------------------------------------------------------------------------
# Verdict
# ---------------------------------------------------------------------------
$failed = @($checks | Where-Object { $_.State -eq 'FAIL' })
$gaps = @($checks | Where-Object { $_.State -eq 'GAP' })
$verdict = if ($failed.Count -eq 0) { "PHASE 2 SESSION OK" } else { "PHASE 2 SESSION INCOMPLETE" }
Write-Host ""
Write-Host ("{0} -- {1} passed, {2} failed, {3} gap(s)" -f $verdict, `
    ($checks.Count - $failed.Count - $gaps.Count), $failed.Count, $gaps.Count) `
    -ForegroundColor $(if ($failed.Count -eq 0) { 'Green' } else { 'Red' })

$header = @(
    $verdict,
    "run       : $($runDir.FullName)",
    "analysed  : $(Get-Date -Format o)",
    "warnings  : $($warnings.Count) hit(s), min $([math]::Round(($warnings | Measure-Object -Minimum).Minimum,1)) ms, p50 $warningP50 ms, p90 $warningP90 ms, max $([math]::Round(($warnings | Measure-Object -Maximum).Maximum,1)) ms",
    "predicted : $predictedHits hit(s) predicted, $unpredictedHits not (false negatives)",
    "",
    "the four numbers, as the hook published them:",
    ($report -replace '^\[INFO\] boot: ', ''),
    "",
    "checks:"
)
$header + ($checks | Format-Table -AutoSize | Out-String) | Set-Content (Join-Path $CaseDir 'verdict.txt')
Write-Host "artifacts in $CaseDir"

if ($failed.Count -gt 0) { exit 1 }
