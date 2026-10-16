# Probe-DodgeDryRun.ps1 -- does the dodge adapter actually run on a live frame path?
#
# THIS IS THE ONE THING NO OFFLINE HARNESS COULD CHECK. `SolverHarness` never runs
# `DodgeAdapter.OnFrame`; it re-derives a snapshot from a recorded trace with its own reader. So
# "the adapter resolves, arms, decides on real frames and writes its report" is a claim about the
# HOOK, and the only instrument that can test it without the game is the synthetic host -- which
# has a World, a Player and a frame tick, and none of the fields the adapter treats as optional.
#
# It is deliberately narrow. It does not test the dodge's quality (that is the synthetic scenarios
# and, eventually, recorded play) and it does not test the actuator (that is Run-Phase3Session).
# What it tests is the wiring: the switch, the resolve, the frame path, the log line.
#
# Usage: powershell -File .\Probe-DodgeDryRun.ps1

$ErrorActionPreference = 'Continue'

$hookRoot  = Split-Path -Parent $PSScriptRoot
$repoRoot  = Split-Path -Parent $hookRoot
$outDir    = Join-Path $hookRoot 'out'
$testRoot  = Join-Path $hookRoot 'tests\SyntheticHost\bin\Release'
$launcher  = Join-Path $outDir 'Drelay.Launcher.exe'
$hostDll   = Join-Path $testRoot 'SyntheticHost.dll'
$hashTool  = Join-Path $repoRoot 'hook\tools\il_method_hash.py'

$failures = New-Object System.Collections.Generic.List[string]
function Check([string] $name, [bool] $ok, [string] $detail) {
    $state = if ($ok) { 'PASS' } else { 'FAIL' }
    $colour = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("  {0,-4} {1,-56} {2}" -f $state, $name, $detail) -ForegroundColor $colour
    if (-not $ok) { $failures.Add($name) | Out-Null }
}

Write-Host '=== dodge dry-run probe (synthetic host) ===' -ForegroundColor Cyan

foreach ($required in @($launcher, $hostDll)) {
    if (-not (Test-Path -LiteralPath $required)) {
        Write-Host "missing: $required -- run hook\build.ps1 and build SyntheticHost first" -ForegroundColor Red
        exit 2
    }
}

# The frame tick has to be published as a test target, because the synthetic host is not the game
# and the pinned table holds the game's four methods. Same recipe as Test-Injection.ps1.
$hashText = (& python $hashTool --assembly $hostDll `
        --type 'SyntheticHostFixture.Cadence' --name 'FrameTick' --arity 1 2>&1 | Out-String)
$ilSize = $null
$ilHash = $null
if ($hashText -match 'ilSize=(\d+)') { $ilSize = [int]$Matches[1] }
if ($hashText -match 'ilHash=([0-9A-Fa-f]{64})') { $ilHash = $Matches[1].ToUpperInvariant() }
if (-not $ilSize -or -not $ilHash) {
    Write-Host "could not hash the host's frame tick: $hashText" -ForegroundColor Red
    exit 2
}
$testSpec = "frameTick|SyntheticHostFixture.Cadence|FrameTick|$ilSize|$ilHash"

$runRoot = Join-Path $hookRoot 'obj\cases\dodge-dryrun'
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null

# The host's own lifetime and the delay before it calls its frame tick. The delay matters for the
# reason Test-Injection.ps1 records: a method compiled before the watching window opens is
# unwatched and can never be patched.
$env:DRELAY_TEST_HOLD_MS = '30000'
$env:DRELAY_TEST_CADENCE_DELAY_MS = '5000'

$before = @(Get-ChildItem -LiteralPath $runRoot -Directory -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty Name)

$argv = @('--game', $testRoot,
          '--exe', (Join-Path $testRoot 'SyntheticHost.exe'),
          '--assembly', $hostDll,
          '--runroot', $runRoot,
          '--collect-delay', '500',
          '--live-seconds', '15',
          '--test-target', $testSpec,
          '--dodge')
Write-Host "  launch: Drelay.Launcher.exe $($argv -join ' ')" -ForegroundColor DarkGray

$text = (& $launcher @argv 2>&1 | Out-String)
$exit = $LASTEXITCODE

$after = @(Get-ChildItem -LiteralPath $runRoot -Directory -ErrorAction SilentlyContinue |
           Select-Object -ExpandProperty Name)
$new = @($after | Where-Object { $_ -notin $before } | Sort-Object -Descending)
$runDir = if ($new.Count -ge 1) { Join-Path $runRoot $new[0] } else { $null }

Check 'the launcher exited cleanly' ($exit -eq 0) "exit=$exit"
if (-not $runDir) {
    Write-Host 'no run directory was produced; the launcher output follows' -ForegroundColor Red
    Write-Host $text
    exit 1
}
Write-Host "  run dir: $runDir" -ForegroundColor DarkGray

$boot = Join-Path $runDir 'bootstrap.log'
$bootText = if (Test-Path -LiteralPath $boot) { Get-Content -LiteralPath $boot -Raw } else { '' }
$hook = Join-Path $runDir 'hook.log'
$hookText = if (Test-Path -LiteralPath $hook) { Get-Content -LiteralPath $hook -Raw } else { '' }

Check 'the adapter armed' ($bootText -match 'dodge ARMED') `
    'the resolve line names the mode, the grid, the intent and the health source'

Check 'the sensor resolved against this host' ($bootText -match 'sensor read path resolved') `
    'the adapter runs on the sensor''s frame path, so this is its prerequisite'

# ---------------------------------------------------------------------------
# DID THE PROBE'S SUBJECT RUN AT ALL?  This is assertion F's question, asked
# before any statement about the adapter is believed.
#
# The adapter is called from the managed frame replacement, so it needs the host's frame tick to be
# MATCHED AND PATCHED.  If that did not happen, no frame reaches `DodgeAdapter.OnFrame` and every
# downstream check below reports the absence of something that was never given a chance -- which is
# exactly the failure shape this project keeps writing down, and it must not be reported as a dodge
# defect.  `hook.log` says which it was: a match logs a patch, a near miss logs the hash warning.
# ---------------------------------------------------------------------------
$frameTickPatched = ($hookText -match "PATCHED 'frameTick'")
$hashNearMiss = ($hookText -match "matches target 'frameTick' but the hash does not")
$warmupEntered = ($bootText -match 'MANAGED REPLACEMENT entered \(call 1\)')

Check 'the host frame tick was matched and patched' $frameTickPatched `
    $(if ($frameTickPatched) {
        'the managed frame replacement is in the game''s own call path'
      } elseif ($hashNearMiss) {
        'NO -- a size-23 method was hashed and did not match, so this host''s frame tick was never' +
        ' patched and NOTHING below is a statement about the adapter. This is a test-target problem' +
        ' (Test-Injection.ps1 run 7/8), not a dodge problem.'
      } else {
        'NO -- no frameTick match and no hash warning in hook.log'
      })

if (-not $frameTickPatched) {
    Write-Host ''
    Write-Host '  The probe could not run its subject. Everything past this point is unmeasurable,' -ForegroundColor Yellow
    Write-Host '  so it is reported as one finding rather than as a row of adapter failures.' -ForegroundColor Yellow
    if ($warmupEntered) {
        Write-Host '  The only entry the adapter ever got was the resolve-time warm-up call, which' -ForegroundColor DarkGray
        Write-Host '  runs before the host has a world -- so it correctly counted one frame with no world.' -ForegroundColor DarkGray
    }
    Write-Host ''
    Write-Host 'PROBE VERDICT: INCONCLUSIVE -- the subject never ran (see above).' -ForegroundColor Yellow
    exit 3
}

$numbers = [regex]::Match($bootText, 'PHASE 3 DODGE NUMBERS \| mode ([A-Z ]+) \| frames (\d+)')
Check 'the dodge reported its own numbers' $numbers.Success `
    $(if ($numbers.Success) { "mode '$($numbers.Groups[1].Value.Trim())' over $($numbers.Groups[2].Value) frame(s)" } else { 'no PHASE 3 DODGE NUMBERS line in bootstrap.log' })

# THE EMPTY-TRACE CHECK, IN THIS PROBE'S OWN TERMS. `frames 0` satisfies every other statement on
# the report line vacuously -- defect 3.8, and the reason `SolverHarness` grew assertion F.
$frames = if ($numbers.Success) { [int]$numbers.Groups[2].Value } else { -1 }
Check 'it decided on more than zero frames' ($frames -gt 0) "frames=$frames"

Check 'it reported in DRY RUN' ($bootText -match 'PHASE 3 DODGE NUMBERS \| mode DRY RUN') `
    '--dodge without --dodge-act must never act'

Check 'the detail line was written' ($bootText -match 'PHASE 3 DODGE DETAIL') `
    'candidates, threats, speed and precedence'

# The host has no byte[,] on World and no Player.Totems, so these two are the DEGRADED paths
# being exercised rather than a defect -- and each has to SAY so rather than pass as resolved.
Check 'the missing grid was named, not assumed' ($bootText -match 'collision grid did NOT resolve') `
    'the synthetic host has no byte[,]; the adapter must say walls are ignored'

Check 'the missing totem collection was named' ($bootText -match 'precedence UNRESOLVED') `
    'the host has no Player.Totems, so the precedence rule reports itself unresolved'

# The report line is the one the runner parses, so a silent truncation would break the check above
# in a way that looks like a missing run. 256 is the native buffer.
$lineLengths = @()
foreach ($line in ($bootText -split "`r?`n")) {
    if ($line -match 'dodge ARMED|dodge config|PHASE 3 DODGE|dodge WOULD|steer dodge') { $lineLengths += $line.Length }
}
$longest = if ($lineLengths.Count -gt 0) { ($lineLengths | Measure-Object -Maximum).Maximum } else { 0 }
Check 'no dodge line exceeds the native 256-byte log buffer' ($longest -le 256) `
    "longest dodge line: $longest character(s), including the [level] prefix"

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host 'PROBE VERDICT: the dodge adapter resolves, arms, decides and reports on a live frame path.' -ForegroundColor Green
    exit 0
}
Write-Host "PROBE VERDICT: $($failures.Count) check(s) failed: $($failures -join '; ')" -ForegroundColor Red
exit 1
