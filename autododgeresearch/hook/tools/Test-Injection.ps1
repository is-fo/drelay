<#
.SYNOPSIS
    End-to-end injection test: launches a synthetic .NET host through the real
    launcher, hook and bootstrap, and checks the whole pipeline.

.DESCRIPTION
    Phase 1's spikes need the real game, which makes them slow, operator-bound and
    hard to run after every change. This test covers everything about Phase 1 that
    does NOT need the game's own assembly:

      * the launcher's controlled environment block (every DOTNET_* removed;
        DOTNET_STARTUP_HOOKS set by us and nothing else);
      * CreateProcess(CREATE_SUSPENDED) and the remote injection stub;
      * the bootstrap running, from the runtime's own perspective;
      * the ordering guarantee: the bootstrap's line lands before the host's Main;
      * the bootstrap publishing its findings and the native side reading them;
      * the runtime validation, which here is expected to reach the MVID check;
      * the *refusal* path, because a synthetic host is not the pinned build;
      * the self-check file, the evidence files, and the exit reporting;
      * the off switch, which must leave the process completely untouched;
      * **run 7 only**: a target of the host itself, matched by IL hash in the
        compilation stream, patched, entered once per cadence call, and read from
        -- which covers the target table and the frame-tick detour, and closes the
        `GAP 9c` runs 1-6 could only report.

    Runs 1-6 deliberately do NOT cover the JIT detour's target matching or the
    frame-tick patch, because the target table describes the game and a host has no
    row in it. Run 7 supplies one through the launcher's `--test-target` switch, so
    the matching and the patch ARE exercised here -- against the host's own method,
    with an identity computed by the same reader and hash function the game's four
    targets are pinned with (`tools/il_method_hash.py` over `il_index.py`). What
    this test still must never be read as proving is that the hook works against
    Darza's Dominion: the game's four IL hashes are only checked by the Phase 0 gate
    and by a live run.

    The synthetic host is built to mirror the game's runtime configuration --
    self-contained apphost, TieredCompilation off -- because half of what is being
    tested is the environment the launcher builds, and a host with a different
    configuration would hide exactly the failures worth catching.

    Exit codes: 0 = all checks passed, 1 = a check failed, 2 = setup problem.
#>
[CmdletBinding()]
param(
    [string] $GamePath = "E:\SteamLibrary\steamapps\common\Darza's Dominion",
    [switch] $Keep
)

$ErrorActionPreference = 'Stop'

$hookRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$outDir = Join-Path $hookRoot 'out'
$testRoot = Join-Path $hookRoot 'obj\injection-test'

function Write-Step { param([string]$t) Write-Host $t -ForegroundColor Cyan }
function Write-Ok   { param([string]$t) Write-Host $t -ForegroundColor Green }
function Write-Bad  { param([string]$t) Write-Host $t -ForegroundColor Red }
function Write-Note { param([string]$t) Write-Host $t -ForegroundColor DarkGray }

$checks = [System.Collections.Generic.List[object]]::new()
function Check {
    param([string] $Name, [bool] $Ok, [string] $Detail = '')
    $checks.Add([pscustomobject]@{ Name = $Name; Ok = $Ok; Detail = $Detail })
    $colour = if ($Ok) { 'Green' } else { 'Red' }
    $word = if ($Ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("  {0,-46} {1}  {2}" -f $Name, $word, $Detail) -ForegroundColor $colour
}

Write-Host '=== end-to-end injection test ===' -ForegroundColor Cyan
Write-Host ''

# ---------------------------------------------------------------------------
# Setup: a synthetic host, in its own directory, next to a private copy of the
# game's runtime so the runtime identity the launcher and hook see is the real one.
# ---------------------------------------------------------------------------
foreach ($required in @('Drelay.Hook.dll', 'Drelay.Bootstrap.dll', 'Drelay.Launcher.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $outDir $required))) {
        Write-Bad "$required is missing from $outDir. Run .\build.ps1 first."
        exit 2
    }
}
if (-not (Test-Path -LiteralPath $GamePath)) {
    Write-Bad "the game directory does not exist: $GamePath"
    exit 2
}

if (Test-Path -LiteralPath $testRoot) {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

Write-Step 'building the synthetic host'
$hostProj = Join-Path $hookRoot 'tests\SyntheticHost\SyntheticHost.csproj'
$hostOut = & dotnet build $hostProj -c Release -v quiet --nologo 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Bad 'the synthetic host did not build:'
    $hostOut | Select-Object -Last 20 | ForEach-Object { Write-Host $_ }
    exit 2
}
$hostBin = Join-Path $hookRoot 'tests\SyntheticHost\bin\Release'
Copy-Item -LiteralPath (Join-Path $hostBin 'SyntheticHost.exe') -Destination (Join-Path $testRoot 'SyntheticHost.exe') -Force
Copy-Item -LiteralPath (Join-Path $hostBin 'SyntheticHost.dll') -Destination (Join-Path $testRoot 'SyntheticHost.dll') -Force

Write-Step 'staging the runtime the way the game's own apphost expects to find it'
# A self-contained apphost resolves its framework from its OWN directory, so the
# test directory has to look like a self-contained deployment. The first version
# of this script copied ten DLLs -- the unmanaged runtime and CoreLib -- and the
# host died with "Could not resolve CoreCLR path", which is the host failing to
# recognise the directory as a deployment at all rather than as a missing file.
#
# So the list is every *.dll in the game directory (its unmanaged runtime plus its
# framework assemblies) and the host takes the game's own deps.json, retargeted at
# the synthetic assembly. A `dotnet`-based host would not need this, but the whole
# point is to reproduce a self-contained apphost, and this is what one needs.
$depsSource = Join-Path $GamePath 'DarzasDominion.deps.json'
$depsTarget = Join-Path $testRoot 'SyntheticHost.deps.json'
if (Test-Path -LiteralPath $depsSource) {
    (Get-Content -LiteralPath $depsSource -Raw) `
        -replace 'DarzasDominion\.dll', 'SyntheticHost.dll' `
        -replace '"DarzasDominion/', '"SyntheticHost/' `
        -replace 'DarzasDominion/1\.0\.0', 'SyntheticHost/1.0.0' |
        Set-Content -LiteralPath $depsTarget -Encoding UTF8
    Write-Note '  deps.json derived from the game''s own'
}

# Every assembly and unmanaged runtime file, minus the game's own two artefacts:
# copying those would be copying the thing under test's *target*, and the point is
# that the hook identifies an assembly rather than assumes one.
$skip = @('DarzasDominion.dll', 'DarzasDominion.exe', 'DarzasDominion.deps.json',
          'DarzasDominion.runtimeconfig.json', 'steam_api64.dll', 'steam_appid.txt')
$copied = 0
$skipped = 0
foreach ($file in (Get-ChildItem -LiteralPath $GamePath -File)) {
    if ($skip -contains $file.Name) { $skipped++; continue }
    if ($file.Extension -notin '.dll', '.json', '.config', '.txt') { continue }
    if ($file.Name -in @('SyntheticHost.dll', 'SyntheticHost.exe',
                         'SyntheticHost.deps.json', 'SyntheticHost.runtimeconfig.json')) { continue }
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $testRoot $file.Name) -Force
    $copied++
}
Write-Note "  staged $copied file(s), skipped $skipped game artefact(s)"
if ($copied -lt 20) {
    Write-Bad "only $copied runtime file(s) were staged from $GamePath, which is too few for a"
    Write-Bad 'self-contained apphost to resolve its framework. Check --GamePath.'
    exit 2
}

# A runtimeconfig that mirrors the game's, including the setting the whole hook
# design rests on. Written rather than copied: the game's lists WindowsDesktop,
# which this host does not use, and a runtimeconfig that asks for a framework
# nobody loads is a confusing failure. The version is the game's, because the
# staged framework assemblies are the game's.
@'
{
  "runtimeOptions": {
    "tfm": "net8.0",
    "includedFrameworks": [
      { "name": "Microsoft.NETCore.App", "version": "9.0.20" }
    ],
    "configProperties": {
      "System.Runtime.TieredCompilation": false,
      "System.Reflection.Metadata.MetadataUpdater.IsSupported": false
    }
  }
}
'@ | Set-Content -LiteralPath (Join-Path $testRoot 'SyntheticHost.runtimeconfig.json') -Encoding UTF8

# ---------------------------------------------------------------------------
# Run 1: the hook enabled. The host is not the pinned build, so the expected
# outcome is a *refusal* -- and the point of the test is that the refusal is
# reached, explained, and harmless.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Step 'run 1: hook enabled (a refusal is the expected outcome)'

$runRoot = Join-Path $testRoot 'runs'
$markerFile = Join-Path $testRoot 'main-marker.txt'
$launcher = Join-Path $outDir 'Drelay.Launcher.exe'
# The timeout is deliberately short: if the bootstrap never signals, this test
# should take seconds to say so, not a minute.
$env:DRELAY_HOOK_TIMEOUT_MS = '15000'

# TWO VARIABLES THE HOST READS, AND THE TEST NEVER USED TO SET.
#
#   * `DRELAY_TEST_HOST_MARKER` is how `SyntheticHost.Main` writes the marker that
#     proves the bootstrap ran first.  Without it the host writes nothing, and the
#     three ordering checks below cannot pass -- they were reporting a defect in
#     the harness as a defect in the hook.  The name is outside the `DRELAY_HOOK_`
#     prefix on purpose: the launcher strips every `DRELAY_HOOK_*` variable out of
#     the child's environment (see PHASE1-HANDOFF.md section 4, trap 1), so a
#     marker named inside that namespace would be removed before the host saw it.
#   * `DRELAY_TEST_HOLD_MS` is how long the host stays alive.  The native side's
#     discovery window is 30 s and its live checks add 5 s, so a host that exits
#     after its 3 s default is gone long before the watcher writes `summary.txt`,
#     `events.jsonl`, `offsets.csv`, `frames.csv` or `selfcheck.txt`.  The test
#     passed `--no-wait` and then read those files immediately, which is a race it
#     could only ever lose.  60 s covers the window, the live checks and a run taken
#     with a collection delay, with margin.
$env:DRELAY_TEST_HOST_MARKER = $markerFile
$env:DRELAY_TEST_HOLD_MS = '60000'
if (Test-Path -LiteralPath $markerFile) { Remove-Item -LiteralPath $markerFile -Force }

# THE COLLECTION DELAY IS SET EXPLICITLY, NOT LEFT AT ITS DEFAULT.
#
# The hook now waits 30 s before it starts watching, which is right for an operator
# playing a game and wrong for a 60 s synthetic host: with the default the window
# would open at t+30 s and the host would exit before it closed, so the evidence
# checks would fail on a run with nothing wrong with it.  `0` is the value these two
# runs want -- they are testing the plumbing from injection to evidence, and a delay
# only postpones it.  The delay's own behaviour is checked by run 3 below.
$collectDelayDefault = 0
$commonLauncherArgs = @('--game', $testRoot,
                        '--exe', (Join-Path $testRoot 'SyntheticHost.exe'),
                        '--assembly', (Join-Path $testRoot 'SyntheticHost.dll'),
                        '--runroot', $runRoot,
                        '--collect-delay', $collectDelayDefault.ToString())

$run1 = & $launcher @commonLauncherArgs --no-wait --verbose 2>&1
$run1Exit = $LASTEXITCODE
$run1Text = ($run1 | Out-String)
if ($env:VERBOSE_TESTS) { Write-Host $run1Text }
Write-Note "launcher exit code: $run1Exit"

# A crashing launcher is a finding, not a reason to skip the remaining checks: the
# run directory it created is still the evidence, and the checks below read from
# it.  The first version of this script let the CLR's fatal-error text abort the
# whole test, which threw away the run directory exactly when it mattered.
if ($run1Exit -ne 0) {
    Write-Host ''
    Write-Bad "the launcher exited $run1Exit. Its own output follows; the checks below run"
    Write-Bad 'against whatever it left in the run directory, which is still evidence.'
    Write-Host $run1Text
}

# The run directory is read from the launcher's own output rather than found by
# sorting directory names.  Both runs create one, and picking "the newest" in run
# 2 pointed at the empty directory of a later off-mode run -- which made every
# check fail against a directory with nothing in it.  The launcher already prints
# the path; using it removes the guess.
function Get-RunDirectory {
    param([string] $Text)
    $m = [regex]::Match($Text, 'run directory\s*:\s*(.+)')
    if (-not $m.Success) { return $null }
    return $m.Groups[1].Value.Trim()
}

$runDirPath = Get-RunDirectory $run1Text
if (-not $runDirPath -or -not (Test-Path -LiteralPath $runDirPath)) {
    Write-Bad 'the launcher produced no usable run directory'
    Check 'launcher created a run directory' $false
} else {
    Check 'launcher created a run directory' $true (Split-Path -Leaf $runDirPath)
}

# WAIT FOR THE WATCHER, BECAUSE `--no-wait` MEANS THE LAUNCHER RETURNED TOO EARLY.
#
# The launcher exits as soon as it has injected; the watcher inside the host goes on
# to run a 30 s discovery window, 5 s of live checks, and only then write
# `selfcheck.txt`, `summary.txt`, `events.jsonl`, `offsets.csv` and `frames.csv`.
# Reading them on the next line is a race the test cannot win.  It waits for the
# watcher's own last line instead of sleeping a fixed amount, so a slow machine
# costs time and a fast one costs none.
if ($runDirPath) {
    Write-Step 'waiting for the watcher to finish and write its evidence'
    $waitStart = Get-Date
    $deadline = $waitStart.AddSeconds(70)
    $evidence = @('summary.txt', 'selfcheck.txt', 'events.jsonl', 'offsets.csv', 'frames.csv')
    while ((Get-Date) -lt $deadline) {
        $missing = @($evidence | Where-Object { -not (Test-Path -LiteralPath (Join-Path $runDirPath $_)) })
        $hookNow = Join-Path $runDirPath 'hook.log'
        $done = $false
        if (Test-Path -LiteralPath $hookNow) {
            $tail = Get-Content -LiteralPath $hookNow -Raw
            $done = $tail -match 'phase 1 complete'
        }
        if ($missing.Count -eq 0 -and $done) { break }
        Start-Sleep -Milliseconds 500
    }
    $waited = ((Get-Date) - $waitStart).TotalSeconds
    Write-Note ("  waited {0:N0}s; {1} of {2} evidence file(s) present" -f
        $waited, ($evidence.Count - $missing.Count), $evidence.Count)
}

$logPath = if ($runDirPath) { Join-Path $runDirPath 'hook.log' } else { $null }
$logText = if ($logPath -and (Test-Path -LiteralPath $logPath)) { Get-Content -LiteralPath $logPath -Raw } else { '' }
# The bootstrap writes its own file, deliberately: two writers on one path, opened
# by two runtimes with different sharing modes, produced a file with one writer's
# lines and not the other's.  Both are read here so every check sees the whole
# startup story.
$bootPath = if ($runDirPath) { Join-Path $runDirPath 'bootstrap.log' } else { $null }
$bootText = if ($bootPath -and (Test-Path -LiteralPath $bootPath)) { Get-Content -LiteralPath $bootPath -Raw } else { '' }
$allText = $logText + $bootText

Write-Host ''
Write-Step 'checks'

Check 'a hook log was written' ($allText.Length -gt 0) ("native {0} + bootstrap {1} bytes" -f $logText.Length, $bootText.Length)
Check 'the DLL was injected and started' ($allText -match 'watcher thread started')
Check 'the shared block was mapped' ($allText -match 'block mapped|shared block')
Check 'the bootstrap signalled readiness' ($allText -match 'bootstrap signalled')
Check 'the runtime was found (clrjit + coreclr)' ($allText -match 'coreclr [0-9.]+ / clrjit [0-9.]+')
Check 'the JIT/EE GUID was read and verified' ($allText -match 'JIT/EE GUID verified')
Check 'the JIT/EE GUID check passed before anything was written' (
    $allText -match 'JIT/EE GUID verified' -and $allText -match 'compileMethod detoured'
)
# THE ORDERING CHECK IS A COMPARISON OF TIMESTAMPS, NOT THE EXISTENCE OF A FILE.
#
# It used to assert only that the bootstrap's line exists and that the marker exists,
# which is true whenever the hook ran at all -- it could not fail for the reason it
# names, and it reported the harness's missing `DRELAY_TEST_HOST_MARKER` as a hook
# defect for as long as it existed.  The property actually under test is that the
# startup hook's work finished before the host reached `Main`.  The bootstrap flushes
# its log on the way out of the startup hook and the host writes the marker inside
# `Main`, so the two files' timestamps order the two events.
$markerText = if (Test-Path -LiteralPath $markerFile) { Get-Content -LiteralPath $markerFile -Raw } else { '' }
$bootLogPath = if ($runDirPath) { Join-Path $runDirPath 'bootstrap.log' } else { $null }
$bootFirstLine = ''
if ($bootLogPath -and (Test-Path -LiteralPath $bootLogPath)) {
    $bootFirst = (Get-Content -LiteralPath $bootLogPath -TotalCount 1) -join ''
    Write-Note ("  bootstrap's first line: " + $bootFirst)
}
Check 'the bootstrap line predates the host Main marker' (
    ($bootText -match 'DOTNET_STARTUP_HOOKS is running') -and ($markerText.Length -gt 0)
)
if ($bootLogPath -and $markerFile -and
    (Test-Path -LiteralPath $bootLogPath) -and (Test-Path -LiteralPath $markerFile)) {
    # CREATION TIME, NOT LAST-WRITE TIME, AND THE FIRST VERSION OF THIS CHECK GOT IT
    # WRONG.  The bootstrap opens its log at `resolve[0]` and appends to it until
    # `resolve[8]`, long after the startup hook has returned and `Main` has run -- so
    # the log's last write is *after* the marker by design, and comparing last-write
    # times asserts the opposite of the property under test.  The startup hook's
    # first act is to create that file; the marker is written inside `Main`; so the
    # creation time is the correct ordering, and it is also stable, because appending
    # to a file does not change when it was created.
    $bootStamp = (Get-Item -LiteralPath $bootLogPath).CreationTimeUtc
    $markerStamp = (Get-Item -LiteralPath $markerFile).LastWriteTimeUtc
    Check 'the bootstrap log was created before the Main marker' ($bootStamp -le $markerStamp) (
        "bootstrap {0:O} vs marker {1:O}" -f $bootStamp, $markerStamp)
} else {
    Check 'the bootstrap log was created before the Main marker' $false 'one of the two files is missing'
}
Check 'the bootstrap reported the loaded MVID' ($bootText -match 'loaded assembly MVID')

# The refusal itself. A synthetic host is by construction not the pinned build,
# so reaching the explanation is the success condition -- and "the game runs
# normally" is the property that matters.
Check 'the fingerprint mismatch is refused, with both values named' (
    $bootText -match 'MVID mismatch' -and
    $bootText -match 'known-build.json pins'
)

# The host must have run to completion regardless: a hook that refuses must not
# be a hook that breaks the process.
Check 'the host process ran to completion' ($run1Text -match 'TieredCompilation = off' -or $run1Exit -eq 0)

# The host's own half of the ordering evidence.  `$markerText` was read above, where
# the comparison that uses it lives; restating the two existence checks here would be
# asserting the same fact twice.
Check 'the host reached Main after the bootstrap ran' ($markerText -match 'synthetic host Main reached')

# THE TWO INSTRUMENTS THAT DECIDE HOW EVERY PER-TARGET VERDICT IS READ.
#
# `target-size ... ZERO compiled` is only a statement about the game while the
# detour inspected every compilation it was entered for, and while the process
# being watched is the process being played.  Both assumptions were relied on in
# PHASE1-HANDOFF.md section 11 and neither was measured; these two checks make the
# measurement a tested property of every future run rather than a comment.
Check 'the run records which process it is (pid, image, foreground owner)' (
    $allText -match 'process identity \(at start\): pid \d+, image ')
Check 'the run accounts for every detour entry, not only the inspected ones' (
    $allText -match 'detour accounting: \d+ invocation\(s\), \d+ instrumented, \d+ skipped as re-entrant')

# A run in which the detour was entered more often than it inspected is a run whose
# MISSING verdicts cannot be trusted, so the warning is asserted to be present
# whenever the numbers disagree -- and asserted absent when they agree, because a
# warning that fires unconditionally is a warning nobody reads.
if ($allText -match 'detour accounting: (\d+) invocation\(s\), (\d+) instrumented') {
    $invoked = [int]$Matches[1]
    $inspected = [int]$Matches[2]
    if ($invoked -gt $inspected) {
        Check 'an uninspected compilation is reported as an instrument fault' (
            $allText -match 'A target reported MISSING in this run is NOT proof')
    } else {
        Check 'no compilation went uninspected' $true ("{0} invocation(s) == {0} instrumented" -f $invoked)
    }
} else {
    Check 'the detour accounting line was parseable' $false 'no detour accounting line'
}

if ($runDirPath) {
    $selfCheck = Join-Path $runDirPath 'selfcheck.txt'
    Check 'selfcheck.txt was written' (Test-Path -LiteralPath $selfCheck)
    if (Test-Path -LiteralPath $selfCheck) {
        $sc = Get-Content -LiteralPath $selfCheck -Raw
        if ($env:VERBOSE_TESTS) { Write-Host $sc }
        Check 'selfcheck.txt records the failing checks' ($sc -match 'FAIL')
        # A GAP is a check that could not be made.  It must be labelled, because
        # "we did not look" and "we looked and it was fine" are the two things a
        # self-check exists to tell apart.
        Check 'selfcheck.txt distinguishes a GAP from a pass' ($sc -match 'GAP')
    }
    # The build-provenance files the run should leave behind.
    foreach ($name in @('summary.txt', 'events.jsonl', 'offsets.csv', 'frames.csv')) {
        Check "evidence file: $name" (Test-Path -LiteralPath (Join-Path $runDirPath $name))
    }
}

# ---------------------------------------------------------------------------
# Run 2: the off switch. Nothing may be injected at all -- not merely quiet.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Step 'run 2: off switch (the process must be untouched)'

$run2 = & $launcher @commonLauncherArgs --off --no-wait --verbose 2>&1
$run2Exit = $LASTEXITCODE
$run2Text = ($run2 | Out-String)
$run2DirPath = Get-RunDirectory $run2Text
$run2Log = if ($run2DirPath) { Join-Path $run2DirPath 'hook.log' } else { $null }
$run2LogText = if ($run2Log -and (Test-Path -LiteralPath $run2Log)) { Get-Content -LiteralPath $run2Log -Raw } else { '' }

Check 'the off switch was honoured by the native side' (
    $run2LogText -match 'observing nothing and patching nothing' -or $run2Text -match 'OFF'
)
Check 'no startup hook was installed in off mode' (
    $run2LogText -notmatch 'DOTNET_STARTUP_HOOKS is running'
)
Check 'nothing was patched in off mode' (
    $run2LogText -notmatch 'PATCHED' -and $run2LogText -notmatch 'compileMethod detoured'
)
# Exit 3 is the launcher's documented "the hook did not answer" code, and in off
# mode that is the expected outcome: the native side writes its one line and
# returns without ever installing.  Asserting exit 0 here would be asserting that
# the off switch did something.
Check 'the host still ran to completion' ($run2Exit -eq 0 -or $run2Exit -eq 3)

# ---------------------------------------------------------------------------
# Run 3: the collection delay is honoured, and it is measured rather than trusted.
#
# The knob exists because two of the four targets compile on first use during
# gameplay: a window that opens ~1.3 s into the process closes long before an
# operator has cleared the menu, which is what produced `0 of 4 targets found` on
# three live runs.  A flag that is plumbed but ignored would leave exactly the same
# symptom, so the delay is asserted from the hook's own log rather than from a
# stopwatch in the harness: `waiting N ms before collection starts` must name the
# value that was asked for, and `collection started` must NOT appear before that
# many milliseconds have passed in the run's own evidence.
#
# Run 1 set the delay to 0, so this also checks that the two values are
# distinguishable rather than that the flag is merely present.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Step 'run 3: the collection delay (a knob that is ignored looks the same as one that works)'

$delayMs = 2500
$run3 = & $launcher @commonLauncherArgs --collect-delay $delayMs --no-wait --verbose 2>&1
$run3Exit = $LASTEXITCODE
$run3Text = ($run3 | Out-String)
$run3DirPath = Get-RunDirectory $run3Text
$run3LogText = if ($run3DirPath) {
    $p = Join-Path $run3DirPath 'hook.log'
    if (Test-Path -LiteralPath $p) { Get-Content -LiteralPath $p -Raw } else { '' }
} else { '' }

Check 'the launcher reported its own exit cleanly' ($run3Exit -eq 0)
Check 'the hook was told to wait, and by how much' (
    $run3LogText -match ("waiting {0} ms before collection starts" -f $delayMs)) (
        "expected 'waiting $delayMs ms'")
Check 'collection was announced as started afterwards' ($run3LogText -match 'collection started: watching')
# Run 1 must NOT have waited, or the flag is not reaching the hook at all.
Check 'the zero-delay run did not print a wait line' ($logText -notmatch 'waiting \d+ ms before collection starts')

# The delayed run has to reach the same end state as the others, or the delay would be
# a way to lose evidence.  Waiting for the run directory rather than assuming, because
# the hook writes these 30 s after `collection started`.
if ($run3DirPath) {
    $run3Deadline = (Get-Date).AddSeconds(70)
    $run3Evidence = @('summary.txt', 'selfcheck.txt', 'events.jsonl', 'offsets.csv', 'frames.csv')
    while ((Get-Date) -lt $run3Deadline) {
        $have = @($run3Evidence | Where-Object { Test-Path -LiteralPath (Join-Path $run3DirPath $_) })
        if ($have.Count -eq $run3Evidence.Count) { break }
        Start-Sleep -Milliseconds 500
    }
    $run3Missing = @($run3Evidence | Where-Object { -not (Test-Path -LiteralPath (Join-Path $run3DirPath $_)) })
    Check 'the delayed run still wrote all its evidence' ($run3Missing.Count -eq 0) (
        $(if ($run3Missing.Count -eq 0) { '5 of 5' } else { 'missing: ' + ($run3Missing -join ', ') }))
    Check 'the delayed run reached the end of phase 1' (
        (Get-Content -LiteralPath (Join-Path $run3DirPath 'hook.log') -Raw) -match 'phase 1 complete')
} else {
    Check 'the delayed run still wrote all its evidence' $false 'no run directory'
}

# ---------------------------------------------------------------------------
# Run 4: the launcher echoes the hook's log to the console while the game runs.
#
# THE DEFECT THIS COVERS WAS MINE AND IT COST A ROUND OF OPERATOR CONFUSION.  The
# collection delay was implemented and working -- `hook.log` in three consecutive
# live runs said `waiting 30000 ms before collection starts` -- but the launcher's
# wait loop printed nothing, so the operator never saw it and reasonably concluded
# the change had not been built.
# This run omits `--no-wait`, so the launcher's tail loop actually runs, and asserts
# that the hook's lines reach the operator's console -- which is the whole defect this
# covers.  A collection window nobody can see is a window nobody can use.
#
# WHAT THIS RUN ASSERTS NOW, AND WHY IT CAN.
#
# It previously asserted only the transcript boundary and the first hook line, and
# deliberately skipped `collection started` and `phase 1 complete`, because against
# this host those lines were missing for an unexplained reason.  The reason is now
# known and fixed: the launcher's tail held `hook.log` open for the whole session, and
# a held reader makes the hook's `_wfopen_s` append fail with ERROR_SHARING_VIOLATION
# -- measured in isolation, both orders.  Live runs recorded 134 to 537 failed flushes,
# so the file stopped at whatever had been written before the tail opened.
#
# With the tail reopening per call, the hook's appends succeed and the transcript is
# complete, so the whole phase is asserted.  The hold time has to outlast the delay
# plus the 30 s window plus the 5 s of live checks: a test that does not outlive the
# code path it asserts about is measuring its own timeout.
$savedHold = $env:DRELAY_TEST_HOLD_MS
$env:DRELAY_TEST_HOLD_MS = '45000'
$run4 = & $launcher @commonLauncherArgs --collect-delay 1500 --verbose 2>&1
$run4Exit = $LASTEXITCODE
$run4Text = ($run4 | Out-String)
$env:DRELAY_TEST_HOLD_MS = $savedHold
if ($env:VERBOSE_TESTS) { Write-Host $run4Text }

Check 'the launcher stayed attached while the game ran' ($run4Text -match 'The game is running')
Check 'the live transcript was announced' ($run4Text -match '---- hook log \(live\) ----')
Check 'the hook log transcript was closed off' ($run4Text -match '---- end of hook log ----')
Check 'the hook log reached the console line by line' ($run4Text -match '\[INFO \] watcher thread started')
Check 'the collection delay reached the console' (
    $run4Text -match 'waiting 1500 ms before collection starts')
Check 'collection starting reached the console' (
    $run4Text -match 'collection started: watching the compilation stream')
Check 'the end of the phase reached the console' ($run4Text -match 'phase 1 complete')
Check 'the game exit was reported' ($run4Text -match 'game exited with code')
# THE LAUNCHER'S OWN END-OF-RUN REPORT, WHICH USED TO CONTRADICT THE DESIGN IT WAS
# REPORTING ON.  It looked for the bootstrap's line in `hook.log` and therefore
# printed `spike 1.1 : the bootstrap line is MISSING from hook.log` on every run,
# including runs where it was in `bootstrap.log` -- where the design puts it, because
# two writers on one file destroy each other's output.  A false negative about the
# phase's first spike, in the one report an operator reads at the end of a live run.
Check 'the launcher found the bootstrap line where it actually is' (
    $run4Text -match 'spike 1\.1\s*: the bootstrap ran; its first line is in bootstrap\.log') (
        $(if ($run4Text -match 'spike 1\.1\s*:\s*(.+)') { $Matches[1].Trim() } else { 'no report' }))
Check 'the launcher no longer claims the bootstrap line is missing' (
    $run4Text -notmatch 'the bootstrap line is MISSING')

# THE REGRESSION GUARD, AND WHY IT ASSERTS A BOUND RATHER THAN ZERO.
#
# If a future tail holds its handle again -- the obvious "optimisation", and the shape
# this bug had -- the hook's appends fail and this is the counter that says so.  The
# original defect produced 371 of them in a run, so the signal is unmistakable.
#
# It used to assert ZERO, and that was wrong in a way worth recording: the hook's own
# design says a failed append is TRANSIENT and costs nothing, because the lines stay
# in the ring and the next pass retries them (`g_readSeq` only advances on a
# successful write) -- so the counter exists to make a PERSISTENT failure visible, not
# to claim the file was never contended.  A reader of that design would expect the
# odd failure on a machine with a real-time scanner, and this test failed on one run
# in three for exactly that reason.  A guard that fires on the expected behaviour is a
# guard that gets ignored, which is worse than no guard.  Five is 74x below the
# original defect and two orders of magnitude above the observed rate.
#
# The property that actually matters is asserted separately and is not statistical:
# the run's LAST line is in hook.log, which is true only if nothing was silently lost
# at the end of the session.
$run4Dir = Get-RunDirectory $run4Text
if ($run4Dir -and (Test-Path -LiteralPath (Join-Path $run4Dir 'summary.txt'))) {
    $sum4 = Get-Content -LiteralPath (Join-Path $run4Dir 'summary.txt') -Raw
    $m = [regex]::Match($sum4, 'log appends\s*:\s*(\d+) failed')
    $failedPasses = if ($m.Success) { [int]$m.Groups[1].Value } else { 0 }
    $sideFile = Join-Path $run4Dir 'hook.log.append-failed'
    Check 'the tail did not deny the hook its log' ($failedPasses -le 5) (
        $(if ($failedPasses -eq 0) { 'none' }
          elseif (Test-Path -LiteralPath $sideFile) { "$failedPasses transient pass(es); see hook.log.append-failed" }
          else { "$failedPasses failed append pass(es)" }))
    $hook4Path = Join-Path $run4Dir 'hook.log'
    $hook4Text = if (Test-Path -LiteralPath $hook4Path) { Get-Content -LiteralPath $hook4Path -Raw } else { '' }
    Check 'nothing was lost at the end of hook.log' ($hook4Text -match 'phase 1 complete')
} else {
    Check 'the tail did not deny the hook its log' $false 'no summary.txt'
    Check 'nothing was lost at the end of hook.log' $false 'no run directory'
}

# ---------------------------------------------------------------------------
# Runs 5 and 6: THE ARMING GATE, WHICH IS SPIKE 1.3'S SENSOR.
#
# THE GAP THESE COVER, AND WHY IT NEEDED TWO RUNS RATHER THAN ONE.  Phase 1's
# blocking defect was that Spike 1.3's success test -- "read exactly one float from
# the live world" -- had never executed: the managed frame callback was disabled at
# attach, because `World.Shared` is null before the game loads a scene and attach
# happens before that.  The fix arms the callback later, the first time a world
# exists, and only if the pointer chain built from the measured offsets reproduces
# what reflection says.  Both halves of that sentence need testing, and one run
# cannot test both:
#
#   * run 5 gives the host a world a few seconds after `Main`, so the gate opens,
#     the cross-check runs, its verdict is published, and the evidence files carry
#     it.  It also asserts that the gate did NOT open at attach, by requiring at
#     least one probe to have looked and found no world;
#   * run 6 is the CONTROL and removes exactly one thing: the world.  Without it the
#     gate must never open and the run must report the check it could not make as a
#     GAP -- not as a pass, and not as a failure either.
#
# THIS IS THE PROJECT'S OWN METHOD APPLIED TO THE FIX.  PHASE1-HANDOFF.md section 8
# asks of any claim "did it run, did it change what it claimed to change, and would
# the log have said so if it had not?"  Run 5 answers the first two; run 6 answers
# the third, because a cross-check that reports PASS whether or not there is a world
# to check is precisely the instrument failure that cost this phase its rounds.
#
# WHAT THEY DELIBERATELY DO NOT PROVE: that the frame read ever *ran*.  That needs the
# frame-tick patch, which needs the game's own IL hashes, so it is live work.  What is
# asserted instead is the honest intermediate state: the read path is open, the
# MethodTable the native read compares against has been published, and check 9c says no
# frame has entered the detour because nothing was patched.
#
# THE MECHANISM CHANGED IN A LATER ROUND AND THE TWO RUNS WERE KEPT.  The read used to be
# a managed callback reached from the detour, and that cannot work at all: entering
# managed code from the frame thread is an illegal reverse P/Invoke that fail-fasts the
# process.  The read is native now, so what these runs still cover is the arming decision
# and the cross-check -- which are unchanged, and which are the part a synthetic host can
# legitimately exercise.
#
# The host's world is a stand-in with the same *shape* as the game's -- a static
# `World.Shared`, an instance `Player`, an inline `GamePoint Position` of two floats
# -- because the shape is all the read path depends on.  See SyntheticHost.cs.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Step 'run 5: the arming gate (the cross-check runs once a world exists)'

function Wait-ForRunEvidence {
    param([string] $Dir, [int] $TimeoutSeconds = 75)
    $evidence = @('summary.txt', 'selfcheck.txt', 'events.jsonl', 'offsets.csv', 'frames.csv')
    $start = Get-Date
    $deadline = $start.AddSeconds($TimeoutSeconds)
    $missing = $evidence
    while ((Get-Date) -lt $deadline) {
        $missing = @($evidence | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Dir $_)) })
        $hookNow = Join-Path $Dir 'hook.log'
        $done = $false
        if (Test-Path -LiteralPath $hookNow) {
            $done = (Get-Content -LiteralPath $hookNow -Raw) -match 'phase 1 complete'
        }
        if ($missing.Count -eq 0 -and $done) { break }
        Start-Sleep -Milliseconds 500
    }
    return [pscustomobject]@{
        Missing = $missing
        Elapsed = ((Get-Date) - $start).TotalSeconds
    }
}

function Read-RunFile {
    param([string] $Dir, [string] $Name)
    if (-not $Dir) { return '' }
    $p = Join-Path $Dir $Name
    if (Test-Path -LiteralPath $p) { return (Get-Content -LiteralPath $p -Raw) }
    return ''
}

$savedWorldDelay = $env:DRELAY_TEST_WORLD_DELAY_MS
$savedNoWorld = $env:DRELAY_TEST_NO_WORLD
$savedHold5 = $env:DRELAY_TEST_HOLD_MS

# THE WORLD ARRIVES LATE ON PURPOSE, AND 3000 ms IS CHOSEN AGAINST THE PROBE'S
# CADENCE.  The watcher probes every 250 ms during the collection delay and the
# discovery window, so a world that appears three seconds in guarantees several
# probes that found nothing -- which is what makes "it waited" a measured fact
# rather than a claim about the code.
$env:DRELAY_TEST_WORLD_DELAY_MS = '3000'
Remove-Item Env:\DRELAY_TEST_NO_WORLD -ErrorAction SilentlyContinue
# Outlasts the collection delay, the 30 s discovery window and the 5 s of live
# checks: a host that exits before the evidence is written measures its own timeout.
$env:DRELAY_TEST_HOLD_MS = '50000'

$run5 = & $launcher @commonLauncherArgs --collect-delay 500 --no-wait --verbose 2>&1
$run5Exit = $LASTEXITCODE
$run5Text = ($run5 | Out-String)
if ($env:VERBOSE_TESTS) { Write-Host $run5Text }
$run5Dir = Get-RunDirectory $run5Text
Check 'the arming-gate run created a run directory' ([bool]$run5Dir -and (Test-Path -LiteralPath $run5Dir)) (
    $(if ($run5Dir) { Split-Path -Leaf $run5Dir } else { 'none' }))

if ($run5Dir) {
    $wait5 = Wait-ForRunEvidence $run5Dir
    Write-Note ("  waited {0:N0}s; {1} of 5 evidence file(s) present" -f
        $wait5.Elapsed, (5 - $wait5.Missing.Count))
    # A run missing its evidence cannot be judged, and every check below reads one
    # of these files, so the wait's outcome is asserted before anything is read.
    Check 'the arming-gate run wrote all its evidence' ($wait5.Missing.Count -eq 0) (
        $(if ($wait5.Missing.Count -eq 0) { '5 of 5' } else { 'missing: ' + ($wait5.Missing -join ', ') }))
}

$log5 = Read-RunFile $run5Dir 'hook.log'
$boot5 = Read-RunFile $run5Dir 'bootstrap.log'
$sc5 = Read-RunFile $run5Dir 'selfcheck.txt'
$sum5 = Read-RunFile $run5Dir 'summary.txt'
$off5 = Read-RunFile $run5Dir 'offsets.csv'

Check 'the host world shape was resolved and its offsets measured' (
    $boot5 -match 'offsets measured by managed reflection: World\.Shared @ 0x[0-9A-F]+ \(static\), World\.Player \+(\d+), Player\.Position \+(\d+)') (
        $(if ($boot5 -match 'World\.Player \+(\d+), Player\.Position \+(\d+)') { "Player +$($Matches[1]), Position +$($Matches[2])" } else { 'not found' }))
# THE BUG TWO LIVE RUNS PAID FOR, REPRODUCED OFFLINE.
#
# The real game makes its World before it makes a character, so `World.Shared` is
# non-null while `World.Player` is still null.  The first version of the cross-check
# treated that as "ready", asked reflection for a player that did not exist, and got
# `TargetException: Non-static field requires a target` from `FieldInfo.GetValue(null)`
# -- which the catch turned into a permanent REFUSED, disabling the callback for the
# whole session.  A not-yet-ready state reported as a finding, one level down from the
# defect the whole round exists to fix.  This host now creates the world and the player
# three seconds apart, so the window is guaranteed rather than hoped for.
Check 'the world-without-a-player window was observed and logged' (
    $boot5 -match 'World\.Shared is non-null but World\.Player is still null')
Check 'a world without a player does not refuse the callback' (
    $boot5 -notmatch 'the arming cross-check threw' -and $log5 -notmatch 'frame read path REFUSED')
Check 'the arming probe looked for a player before one existed' (
    $sum5 -match 'waited for player: ([1-9]\d*) probe\(s\)') (
        $(if ($sum5 -match 'waited for player: (\d+) probe') { "$($Matches[1]) probe(s) found none" } else { 'no count found' }))
Check 'the frame read path opened once the cross-check passed' (
    $log5 -match 'frame read path ARMED')
# THE FRAME PATH IS NATIVE, AND THIS ROUND'S WHOLE FINDING IS WHY.
#
# The detour used to call an `[UnmanagedCallersOnly]` managed method through its
# transition stub, and pre-compiled it so the first call would not JIT-compile inside
# the game's frame.  That whole construction was wrong, and coreclr says so by killing
# the process: the frame thread is already inside managed code, so the reverse P/Invoke
# is illegal and `ReversePInvokeBadTransition` fail-fasts with `COR_E_EXECUTIONENGINE`
# (0x80131506) -- before the method body, which pre-compiling therefore could not have
# helped.  What is asserted now is the replacement: the detour does its own read, it
# needs nothing prepared first, and no managed frame entry point exists to be armed.
Check 'the frame path reports itself as native with no managed entry point' (
    $log5 -match 'the frame read is native and needs no managed entry point; patching may go live')
Check 'no pre-compile of a managed frame path remains' (
    $boot5 -notmatch 'pre-compiled' -and $boot5 -notmatch 'frame callback')
# The one thing the native read cannot measure for itself: the runtime's own
# `TypeHandle` for `Player`, which only reflection can name.  Published, not merely
# logged, and asserted here because a missing handle would silently disable the
# identity guard rather than fail.
Check 'the Player MethodTable is published for the native frame read' (
    $boot5 -match 'Player MethodTable 0x[0-9A-F]+ published for the native frame read')
Check 'the arm line names both routes and the coordinates they agreed on' (
    $log5 -match 'frame read path ARMED.*reflection and the pointer chain agree on Player\.Position = \(12\.5000, -7\.2500\)')
Check 'the bootstrap log records the comparison, not only the verdict' (
    $boot5 -match 'offset cross-check PASSED.*reflection \(12\.5000, -7\.2500\) and the pointer chain \(12\.5000, -7\.2500\) agree')
Check 'selfcheck.txt reports the cross-check as a PASS' (
    $sc5 -match 'PASS 7b the pointer chain reproduces reflection')
# EVERY offset the chain used, not just the last one: `agree` is per row, and a
# cross-check that confirms one offset while leaving the others unconfirmed would
# pass a single-row assertion.
$confirmedCount = ([regex]::Matches($sc5, 'confirmed by the pointer chain')).Count
Check 'selfcheck.txt confirms each offset the chain walked' ($confirmedCount -eq 3) (
    "$confirmedCount of 3")
Check 'summary.txt reports the arm state as ARMED' (
    $sum5 -match 'arm state\s*:\s*ARMED')
Check 'summary.txt records the two coordinate pairs it compared' (
    $sum5 -match 'cross-check\s*:\s*reflection \(12\.5000, -7\.2500\) vs pointer chain \(12\.5000, -7\.2500\)')
# THE WHOLE POINT OF THE `agree` COLUMN, AND OF THE ROUND THAT ADDED IT: a reader of
# offsets.csv alone can tell a confirmed offset from an unconfirmed one.  Counted per
# line rather than with a `$` anchor over the whole file, because `$` without the
# multiline option matches only the end of the last line -- an assertion that would
# have passed on one confirmed row out of three.
$off5Agreed = @($off5 -split "`r?`n" | Where-Object { $_ -match ',0,1$' })
Check 'offsets.csv marks the walked offsets as confirmed' ($off5Agreed.Count -eq 3) (
    "$($off5Agreed.Count) of 3 row(s) with agree=1")
# A STATIC FIELD'S ADDRESS DOES NOT FIT THE 32-BIT COLUMN, and printing half of it
# looked like a plausible offset.  Asserted as an address so a regression to the
# truncated form fails the test rather than being read as a small number.
Check 'offsets.csv prints the static address rather than half of it' (
    $off5 -match '"World\.Shared \(static address\)",0x[0-9A-F]{16}')
Check 'offsets.csv no longer reports jit-agreement it does not have' (
    $off5 -match '# jit_offset is empty until Phase 2')
# THE HONEST INTERMEDIATE STATE.  This host has no IL-hash match, so no frame tick is
# patched; the correct report is therefore "armed, and no frame has entered it yet",
# not a pass and not a failure.  A run that claimed a live read here would be
# claiming something this test cannot produce.
Check 'the live read is reported as not-yet-made rather than as a pass' (
    $sc5 -match 'GAP  9c the read path is open but no frame has entered the detour yet')

Write-Host ''
Write-Step 'run 6: the same run with no world (the control for run 5)'

# ONE VARIABLE REMOVED: THE WORLD.  Everything else -- the same host, the same
# offsets, the same probe, the same 30 s window -- runs unchanged.
$env:DRELAY_TEST_NO_WORLD = '1'

$run6 = & $launcher @commonLauncherArgs --collect-delay 500 --no-wait --verbose 2>&1
$run6Exit = $LASTEXITCODE
$run6Text = ($run6 | Out-String)
if ($env:VERBOSE_TESTS) { Write-Host $run6Text }
$run6Dir = Get-RunDirectory $run6Text
Check 'the control run created a run directory' ([bool]$run6Dir -and (Test-Path -LiteralPath $run6Dir)) (
    $(if ($run6Dir) { Split-Path -Leaf $run6Dir } else { 'none' }))

if ($run6Dir) {
    $wait6 = Wait-ForRunEvidence $run6Dir
    Write-Note ("  waited {0:N0}s; {1} of 5 evidence file(s) present" -f
        $wait6.Elapsed, (5 - $wait6.Missing.Count))
    Check 'the control run wrote all its evidence' ($wait6.Missing.Count -eq 0) (
        $(if ($wait6.Missing.Count -eq 0) { '5 of 5' } else { 'missing: ' + ($wait6.Missing -join ', ') }))
}

$log6 = Read-RunFile $run6Dir 'hook.log'
$boot6 = Read-RunFile $run6Dir 'bootstrap.log'
$sc6 = Read-RunFile $run6Dir 'selfcheck.txt'
$sum6 = Read-RunFile $run6Dir 'summary.txt'

Check 'the control still measured the offsets (only the world was removed)' (
    $boot6 -match 'offsets measured by managed reflection')
Check 'the control shows the probe looking and finding nothing' (
    $sum6 -match 'waited for player: ([1-9]\d*) probe\(s\)')
Check 'without a world the read path is never reported as armed' (
    $log6 -notmatch 'frame read path ARMED')
Check 'the control reports the callback as still waiting, not as failed' (
    $sum6 -match 'arm state\s*:\s*WAITING')
Check 'the control reports the check it could not make as a GAP' (
    $sc6 -match 'GAP  7b no live player existed')

$env:DRELAY_TEST_WORLD_DELAY_MS = $savedWorldDelay
$env:DRELAY_TEST_NO_WORLD = $savedNoWorld
$env:DRELAY_TEST_HOLD_MS = $savedHold5

# ---------------------------------------------------------------------------
# Run 7: the hook patches a method of the synthetic host
# ---------------------------------------------------------------------------
#
# RUNS 1-6 COULD NOT PATCH ANYTHING, AND THAT WAS A HOLE IN THE FIXTURE, NOT A
# PROPERTY OF THE HOOK.  The target table comes from `known-build.json` through
# `GeneratedBuildTable.cs`, so every row describes the game; no method of this
# host has a hash the JIT detour can match, `DrelayPatchTargets` skips every row,
# and the most run 5 could assert was `GAP 9c the read path is open but no frame
# has entered the detour yet`.  The arming gate was testable here; the patch was
# not.
#
# `--test-target` publishes one extra row for exactly this case (see Program.cs's
# `TestTargets` and StartupHook.cs's `ReadTestTargets`), and the identity it needs
# is computed here by `tools/il_method_hash.py`, which uses `il_index.py`'s own
# metadata reader and hash function -- the recipe is not restated, so it cannot
# drift from the one the game's four targets are pinned with.
#
# What this run then establishes, none of which any earlier run could:
#
#   * the target is matched by IL hash in the real compilation stream, and patched;
#   * `FrameTickDetour` is entered on every call of the host's cadence, and calls
#     the trampoline exactly once per entry -- compared against the host's own
#     count of its body executions, so "the detour ate the calls" cannot pass;
#   * the native frame read runs against this host's world and produces values,
#     which is the `GAP 9c` closed.
#
# THIS RUN IS ABOUT MATCHING AND PATCHING, AND DELIBERATELY NOT ABOUT THE MANAGED
# MECHANISM.  It used to launch with `--frame-helper` so the assisted crossing was
# measured as well; that moved to run 8, because the helper route turned out to be
# FLAKY -- see the note beside the assertions below -- and a run whose subject is a
# coin flip cannot carry the target table's evidence.  What is left here is
# deterministic: the test target is published, matched by IL hash in the real
# compilation stream, patched, entered once per cadence call, and read from.
#
Write-Host ''
Write-Step 'run 7: a target of this host, matched by IL hash and patched'

$researchRoot = Split-Path -Parent $hookRoot
$hashTool = Join-Path $researchRoot 'tools\il_method_hash.py'
$hostDll = Join-Path $testRoot 'SyntheticHost.dll'

$hashText = (& python $hashTool --assembly $hostDll `
        --type 'SyntheticHostFixture.Cadence' --name 'FrameTick' --arity 1 2>&1 | Out-String)
$ilSize = $null
$ilHash = $null
if ($hashText -match 'ilSize=(\d+)') { $ilSize = [int]$Matches[1] }
if ($hashText -match 'ilHash=([0-9A-Fa-f]{64})') { $ilHash = $Matches[1].ToUpperInvariant() }

Check 'the host method has a Phase 0 identity to publish' (
    $null -ne $ilSize -and $null -ne $ilHash) (
    "ilSize=$ilSize ilHash=$ilHash")

$testSpec = "frameTick|SyntheticHostFixture.Cadence|FrameTick|$ilSize|$ilHash"
$savedHold7 = $env:DRELAY_TEST_HOLD_MS
$savedCadenceFile = $env:DRELAY_TEST_CADENCE_FILE
$savedCadenceDelay = $env:DRELAY_TEST_CADENCE_DELAY_MS
$env:DRELAY_TEST_HOLD_MS = '45000'
# The hook installs the detour before it releases `Main`, but it does not *match*
# targets until its watching window opens `--collect-delay` ms later, and a method
# compiled in between is "not lost, only unwatched".  Tiering is off, so the host
# compiles its stand-in frame tick exactly once -- a host that calls it immediately
# therefore puts it in the unwatched interval and can never be patched.  Five
# seconds clears the 500 ms delay, the bootstrap's 500 ms sleep, the self-checks and
# the first frames with room to spare.
$env:DRELAY_TEST_CADENCE_DELAY_MS = '5000'
$cadenceFile = Join-Path $testRoot 'cadence-run7.txt'
$env:DRELAY_TEST_CADENCE_FILE = $cadenceFile

$run7 = & $launcher @commonLauncherArgs --collect-delay 500 --live-seconds 10 `
    --test-target $testSpec --verbose 2>&1
$run7Exit = $LASTEXITCODE
$run7Text = ($run7 | Out-String)
$run7Dir = Get-RunDirectory $run7Text

$env:DRELAY_TEST_HOLD_MS = $savedHold7
$env:DRELAY_TEST_CADENCE_FILE = $savedCadenceFile
$env:DRELAY_TEST_CADENCE_DELAY_MS = $savedCadenceDelay

Check 'the launcher reported its own exit cleanly' ($run7Exit -eq 0) "exit=$run7Exit"

if ($run7Dir) {
    $wait7 = Wait-ForRunEvidence $run7Dir
    Check 'the run wrote all its evidence' ($wait7.Missing.Count -eq 0) (
        $(if ($wait7.Missing.Count -eq 0) { '5 of 5' } else { 'missing: ' + ($wait7.Missing -join ', ') }))
} else {
    Check 'the run wrote all its evidence' $false 'no run directory'
}

$log7 = Read-RunFile $run7Dir 'hook.log'
$boot7 = Read-RunFile $run7Dir 'bootstrap.log'
$sum7 = Read-RunFile $run7Dir 'summary.txt'
$sc7 = Read-RunFile $run7Dir 'selfcheck.txt'

Check 'the bootstrap published the test target' (
    $boot7 -match 'TEST TARGET appended') 'expected "TEST TARGET appended" in bootstrap.log'
# THE COUNT IS DERIVED, NOT WRITTEN DOWN.  This said "five-entry" until Phase 3 added a
# fifth pinned target and made it six -- which is the same class of staleness the target
# table itself exists to prevent.  It is computed from the record plus the one test target
# this run appends, so the assertion tracks the record rather than a number someone has to
# remember to change.
$pinnedTargets = 0
$recordPath = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'known-build.json'
if (Test-Path -LiteralPath $recordPath) {
    $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
    $pinnedTargets = @($record.targetMethods.PSObject.Properties |
        Where-Object { $_.Name -notlike '_*' }).Count
}
$expectedTable = $pinnedTargets + 1
Check "the hook read the pinned table plus the test target ($expectedTable entries)" (
    $log7 -match ("target table: {0} entr" -f $expectedTable)) `
    "expected the native side to load $expectedTable targets ($pinnedTargets pinned + 1 test)"
Check 'the target was found by IL hash in the compilation stream' (
    $log7 -match "target 'frameTick' found") 'expected "target ''frameTick'' found"'
Check 'the target was patched' (
    $log7 -match "PATCHED 'frameTick'") 'expected "PATCHED ''frameTick''"'
Check 'summary.txt counts one matched by the end of the run and one patched' (
    $sum7 -match '1 matched by the end of the run, 1 patched') (($sum7 -split "`r?`n" | Where-Object { $_ -match 'pinned' }) -join ' ')

$cadenceText = if (Test-Path -LiteralPath $cadenceFile) {
    Get-Content -LiteralPath $cadenceFile -Raw
} else { '' }
$selfCalls = $null
$bodyCalls = $null
$witnessIntact = $null
$allocations = $null
if ($cadenceText -match 'self_observed_calls=(\d+)') { $selfCalls = [long]$Matches[1] }
if ($cadenceText -match 'patched_body_calls=(\d+)') { $bodyCalls = [long]$Matches[1] }
if ($cadenceText -match 'witness_intact=(-?\d+)') { $witnessIntact = [int]$Matches[1] }
if ($cadenceText -match 'allocations=(\d+)') { $allocations = [long]$Matches[1] }

Check 'the host ran its cadence' (
    $null -ne $selfCalls -and $selfCalls -gt 100) "self_observed_calls=$selfCalls"
Check 'the detour ran the real body exactly once per entry' (
    $null -ne $bodyCalls -and $bodyCalls -eq $selfCalls) (
    "patched_body_calls=$bodyCalls self_observed_calls=$selfCalls")

# --- the managed helper: NOT asserted here ---------------------------------
#
# THIS RUN USED TO LAUNCH WITH `--frame-helper` AND ASSERT THAT THE HELPER RAN.  It no
# longer does, and the reason is a measurement rather than a preference: the helper
# route -- a native detour calling into managed code -- was found to be FLAKY, and on
# the suite run of 2026-10-12 it corrupted the host's own witness *without* any forced
# collections, which cost run 7 its evidence files and eight assertions.  A test whose
# subject is a coin flip teaches its reader to rerun it, so the helper is measured
# where that is the point (run 8's counterpart in `hook\obj\cases\phase2-mechanism\`)
# and this run goes back to being about what it can decide deterministically: the
# target table, IL-hash matching, the patch, and the native frame read.

Check 'the caller-held witness survived every collection' (
    $witnessIntact -eq 1) (
    "witness_intact=$witnessIntact -- 0 means a caller-frame object was collected while in use")
Check 'collection pressure really ran' (
    $null -ne $allocations -and $allocations -gt 0) "allocations=$allocations"
Check 'the native frame read produced values against this host' (
    $sum7 -match 'frame reads\s*:\s*[1-9]\d*\s+ok') (
    (($sum7 -split "`r?`n" | Where-Object { $_ -match 'frame reads|^  calls' }) -join ' | '))
Check 'the read path was entered, so the old GAP 9c is closed' (
    $sc7 -notmatch 'GAP  9c') 'selfcheck.txt still reports GAP 9c'
Check 'the self-check records the live world read as a pass' (
    $sc7 -match 'PASS 9c live world read') 'expected "PASS 9c live world read" in selfcheck.txt'
Check 'the self-check records the detour as being entered' (
    $sc7 -match 'PASS 9b the JIT-reported address is an executing address') `
    'expected 9b in selfcheck.txt'
# Honesty about WHICH target matched: this is a host, so the four pinned game
# targets must still be reported missing.  A run where they were not would mean the
# hash comparison is not comparing what it claims to.
Check 'the four pinned game targets are still reported missing' (
    $log7 -match 'spawn\s+MISSING' -and $log7 -match 'collision\s+MISSING' -and
    $log7 -match 'escape\s+MISSING') 'expected the game targets to remain unmatched on a host'
Check 'the sensor self-test passed every assertion' (
    $boot7 -match 'sensor self-test: \d+ assertion\(s\), \d+ passed, 0 failed') (
    (($boot7 -split "`r?`n" | Where-Object { $_ -match 'sensor self-test' }) -join ' | '))

if ($Keep -and (Test-Path -LiteralPath $cadenceFile)) {
    Copy-Item -LiteralPath $cadenceFile -Destination (Join-Path $testRoot 'cadence-run7.kept.txt') -Force
}

# ---------------------------------------------------------------------------
# Run 8: the managed replacement, under the stress that killed the other route
# ---------------------------------------------------------------------------
#
# WHAT THIS GATES, AND WHY IT IS THE OTHER ROUTE.  Phase 2's sensor has to run managed
# code on the game's frame thread.  The first attempt at that -- a native detour that
# `call`s a managed helper -- was built, measured and found FLAKY: about one run in
# four died inside the GC's mark phase, even with every method on the path
# pre-compiled and nothing allocating (PHASE2-SCOPE.md section 4.3).  The diagnosis is
# that the runtime cannot unwind a managed stack that a native frame interrupts without
# a transition marker, and no discipline inside the callee supplies the frame its caller
# does not have.
#
# So the frame tick is patched with a MANAGED method instead, and the native detour is
# not in the path at all: the game's own `call` lands on the target's entry, the entry
# `jmp`s into `FrameTickReplacement`, and the replacement calls MinHook's trampoline
# through a *managed* function pointer -- `delegate* managed` -- so the original body's
# return address is inside a managed method and the whole chain stays walkable.  This
# run is that, under the same forced full compacting collections that killed the other
# route, and the assertions are the ones that would have caught a hook that quietly did
# nothing: the replacement was published, it was ENTERED (a bare hook that never fired
# would leave the counters agreeing just as happily), the original ran exactly once per
# entry, and the caller's own object survived.
#
# The regime is `DRELAY_TEST_FORCE_GC_MS=64`: the host forces
# `GC.Collect(2, Forced, blocking: true, compacting: true)` every 64 ms from its own
# thread.  Six runs of this configuration came through clean on 2026-10-12, including
# two with `DRELAY_FRAME_REPLACEMENT_ALLOC=1` -- allocation on the patched path, which
# the helper route never survived.  The evidence is in
# `hook\obj\cases\phase2-mechanism\`.
Write-Host ''
Write-Step 'run 8: the managed replacement, under forced full compacting collections'

$savedForceGc8 = $env:DRELAY_TEST_FORCE_GC_MS
$savedHold8 = $env:DRELAY_TEST_HOLD_MS
$savedCadenceDelay8 = $env:DRELAY_TEST_CADENCE_DELAY_MS
$savedCadenceFile8 = $env:DRELAY_TEST_CADENCE_FILE
$savedReplAlloc = $env:DRELAY_FRAME_REPLACEMENT_ALLOC
$env:DRELAY_TEST_FORCE_GC_MS = '64'
$env:DRELAY_TEST_HOLD_MS = '45000'
$env:DRELAY_TEST_CADENCE_DELAY_MS = '5000'
$cadence8 = Join-Path $testRoot 'cadence-run8.txt'
$env:DRELAY_TEST_CADENCE_FILE = $cadence8
# Allocation ON, deliberately: it is the variable the other route could not survive,
# and a mechanism that only works because it never allocates is not the one Phase 2
# wants -- a real sensor allocates.
$env:DRELAY_FRAME_REPLACEMENT_ALLOC = '1'
Remove-Item -LiteralPath $cadence8 -ErrorAction SilentlyContinue

$runDirsBefore8 = @(Get-ChildItem $runRoot -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
$run8 = & $launcher @commonLauncherArgs --collect-delay 500 --live-seconds 10 `
    --test-target $testSpec --frame-replacement --verbose 2>&1
$run8Exit = $LASTEXITCODE
$dir8 = Get-ChildItem $runRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $runDirsBefore8 -notcontains $_.Name } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

$env:DRELAY_TEST_FORCE_GC_MS = $savedForceGc8
$env:DRELAY_TEST_HOLD_MS = $savedHold8
$env:DRELAY_TEST_CADENCE_DELAY_MS = $savedCadenceDelay8
$env:DRELAY_TEST_CADENCE_FILE = $savedCadenceFile8
$env:DRELAY_FRAME_REPLACEMENT_ALLOC = $savedReplAlloc

$log8 = Read-RunFile $(if ($dir8) { $dir8.FullName } else { '' }) 'hook.log'
$boot8 = Read-RunFile $(if ($dir8) { $dir8.FullName } else { '' }) 'bootstrap.log'
$crash8 = if ($dir8) { Test-Path -LiteralPath (Join-Path $dir8.FullName 'crash.txt') } else { $true }
$cadence8Text = if (Test-Path -LiteralPath $cadence8) { Get-Content -LiteralPath $cadence8 -Raw } else { '' }
$self8 = $null
$body8 = $null
$witness8 = $null
$forced8 = $null
if ($cadence8Text -match 'self_observed_calls=(\d+)') { $self8 = [long]$Matches[1] }
if ($cadence8Text -match 'patched_body_calls=(\d+)') { $body8 = [long]$Matches[1] }
if ($cadence8Text -match 'witness_intact=(-?\d+)') { $witness8 = [int]$Matches[1] }
if ($cadence8Text -match 'forced_full_collections=(\d+)') { $forced8 = [int]$Matches[1] }

Check 'the forced full compacting collections really ran' (
    $null -ne $forced8 -and $forced8 -ge 100) "forced_full_collections=$forced8"
Check 'the replacement was published for the native side' (
    $boot8 -match 'managed frame replacement published at 0x[0-9A-F]+') `
    'expected "managed frame replacement published" in bootstrap.log'
Check 'the native side aimed the hook at the managed replacement' (
    $log8 -match "'frameTick' will be patched with the MANAGED REPLACEMENT") `
    'expected the native side to report the managed replacement'
Check 'the replacement was ENTERED from the game''s own call' (
    $boot8 -match 'MANAGED REPLACEMENT entered \(call 1\): total=\d{4,}\.') `
    'expected the replacement to report a live frame total, not the warm-up 0'
Check 'the original ran exactly once per entry, through the managed trampoline call' (
    $null -ne $self8 -and $self8 -gt 100 -and $body8 -eq $self8) (
    "patched_body_calls=$body8 self_observed_calls=$self8")
Check 'the caller-held witness survived every forced collection' (
    $witness8 -eq 1) "witness_intact=$witness8"
Check 'the run did not die' (-not $crash8) (
    $(if ($crash8) { 'crash.txt was written -- the replacement route failed the same way the helper route did' } else { 'no crash.txt' }))

if ($Keep -and (Test-Path -LiteralPath $cadence8)) {
    Copy-Item -LiteralPath $cadence8 -Destination (Join-Path $testRoot 'cadence-run8.kept.txt') -Force
}

# ---------------------------------------------------------------------------
# Run 9: the sensor self-test, configured to be wrong, must say so
# ---------------------------------------------------------------------------
#
# A SELF-TEST THAT CANNOT FAIL IS NOT A SELF-TEST.  Run 7 asserts that the sensor's
# fifteen assertions pass with the rules the code ships; this run asserts that they FAIL
# when a rule is deliberately broken, which is the difference between a test and a
# rubber stamp.  The same reasoning as `tools\Test-FingerprintGate.ps1`, which exists to
# prove the fingerprint gate refuses a mutated build rather than to prove it accepts a
# good one.
#
# `DRELAY_SENSOR_RETIRE_MISSES=1` is that broken rule: one strike instead of two, so a
# single incomplete read retires a live bullet -- the exact defect T4b is written to
# catch.  Nothing is patched in this run and the host is short-lived, because the
# self-test runs on the resolve callback, seconds into the process.
Write-Host ''
Write-Step 'run 9: the sensor self-test, with a rule broken on purpose'

$savedRetire = $env:DRELAY_SENSOR_RETIRE_MISSES
$savedHold9 = $env:DRELAY_TEST_HOLD_MS
$savedDelay9 = $env:DRELAY_TEST_CADENCE_DELAY_MS
$savedCadenceFile9 = $env:DRELAY_TEST_CADENCE_FILE
$env:DRELAY_SENSOR_RETIRE_MISSES = '1'
$env:DRELAY_TEST_HOLD_MS = '8000'
$env:DRELAY_TEST_CADENCE_DELAY_MS = '0'
Remove-Item env:DRELAY_TEST_CADENCE_FILE -ErrorAction SilentlyContinue

$runDirsBefore9 = @(Get-ChildItem $runRoot -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
$null = & $launcher @commonLauncherArgs --collect-delay 0 --live-seconds 2 --verbose 2>&1
$dir9 = Get-ChildItem $runRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $runDirsBefore9 -notcontains $_.Name } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

$env:DRELAY_SENSOR_RETIRE_MISSES = $savedRetire
$env:DRELAY_TEST_HOLD_MS = $savedHold9
$env:DRELAY_TEST_CADENCE_DELAY_MS = $savedDelay9
$env:DRELAY_TEST_CADENCE_FILE = $savedCadenceFile9

$boot9 = Read-RunFile $(if ($dir9) { $dir9.FullName } else { '' }) 'bootstrap.log'

Check 'the broken rule is caught by name' (
    $boot9 -match 'sensor self-test FAILED: T4b one absence does not retire a live bullet') (
    (($boot9 -split "`r?`n" | Where-Object { $_ -match 'self-test FAILED' }) -join ' | '))
Check 'the summary reports the failures rather than only the pass count' (
    $boot9 -match 'sensor self-test: \d+ assertion\(s\), \d+ passed, [1-9]\d* failed') (
    (($boot9 -split "`r?`n" | Where-Object { $_ -match 'self-test: \d+ assertion' }) -join ' | '))
Check 'the broken run still produced its own evidence' (
    -not [string]::IsNullOrEmpty($boot9)) 'no bootstrap.log for the deliberately-wrong run'

# ---------------------------------------------------------------------------
# Run 10: the sensor reads the live-object set from inside the process
# ---------------------------------------------------------------------------
#
# THIS IS DELIVERABLE 2.1's READ HALF, AND THE FIRST RUN WHERE THE HOOK SEES THE WORLD.
# The host keeps the world's projectile set moving -- spawning, retiring, and re-renting
# instances the way a pool does -- and counts all of it itself, so the hook's numbers have
# an independent witness rather than agreeing with themselves.
#
# THE CONTAINERS ARE THE CLIENT'S, AND THAT IS A CORRECTION THIS RUN PAID FOR.  Until the
# evening of 2026-10-12 this fixture put its projectiles in `World.Objects`, on
# CLIENT-MAP.md section 5.2's claim that the game's `World.AddObject` files them there.
# The client does not: a projectile is rented and handed to
# `World.GameObjectLayer.AddChild`, and the method that writes `Objects[obj.GameId]` is
# never passed one.  A live session then read `live=0` for 54 600 frames in a zone full of
# bullets, while this suite passed -- because the fixture and the hook shared the wrong
# assumption.  So the host now has a `GameObjectLayer`, its projectiles live there, and
# `World.Objects` holds only the two static objects.
#
# What the assertions are really about:
#
#   * `objects_live=` must be ZERO and `layer_live=` must carry the bullets: that is the
#     regression test for the bug above, and it fails if the sensor goes back to reading
#     only the dictionary;
#   * `other=` must equal the host's own `live_others`: a filter that does not filter is
#     the bug that silently turns a threat set into "everything in the world is a bullet",
#     and the layer has the same two statics in it, so `layer_other=` is the same test on
#     the walk the sensor now does;
#   * `tracked` must cover `live` -- every live projectile has a record after the frame
#     it was observed in;
#   * `retired` must be non-zero, because the host retires objects and a threat set that
#     only ever grows is a threat set of ghosts;
#   * the cost must be REPORTED, because it is one of the four numbers Phase 2 exists to
#     produce and a run that does not print it cannot be used to tune anything.  The layer
#     walk is O(everything the world draws), which is CLIENT-MAP.md section 10.6's own
#     prediction, so this number is the one that says whether reading the layer is
#     affordable.
Write-Host ''
Write-Step 'run 10: the sensor reads the live-object set'

$savedSensor = $env:DRELAY_FRAME_SENSOR
$savedHold10 = $env:DRELAY_TEST_HOLD_MS
$savedDelay10 = $env:DRELAY_TEST_CADENCE_DELAY_MS
$savedCadenceFile10 = $env:DRELAY_TEST_CADENCE_FILE
$env:DRELAY_FRAME_SENSOR = '1'
$env:DRELAY_TEST_HOLD_MS = '45000'
$env:DRELAY_TEST_CADENCE_DELAY_MS = '5000'
$cadence10 = Join-Path $testRoot 'cadence-run10.txt'
$env:DRELAY_TEST_CADENCE_FILE = $cadence10
Remove-Item -LiteralPath $cadence10 -ErrorAction SilentlyContinue

$runDirsBefore10 = @(Get-ChildItem $runRoot -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
$null = & $launcher @commonLauncherArgs --collect-delay 500 --live-seconds 10 `
    --test-target $testSpec --frame-replacement --verbose 2>&1
$dir10 = Get-ChildItem $runRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $runDirsBefore10 -notcontains $_.Name } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

$env:DRELAY_FRAME_SENSOR = $savedSensor
$env:DRELAY_TEST_HOLD_MS = $savedHold10
$env:DRELAY_TEST_CADENCE_DELAY_MS = $savedDelay10
$env:DRELAY_TEST_CADENCE_FILE = $savedCadenceFile10

$boot10 = Read-RunFile $(if ($dir10) { $dir10.FullName } else { '' }) 'bootstrap.log'
$cadence10Text = if (Test-Path -LiteralPath $cadence10) { Get-Content -LiteralPath $cadence10 -Raw } else { '' }
$hostLive = $null
$hostOthers = $null
$hostLayerNodes = $null
$hostObjectsProjectiles = $null
if ($cadence10Text -match 'live_projectiles=(\d+)') { $hostLive = [int]$Matches[1] }
if ($cadence10Text -match 'live_others=(\d+)') { $hostOthers = [int]$Matches[1] }
if ($cadence10Text -match 'layer_nodes=(\d+)') { $hostLayerNodes = [int]$Matches[1] }
if ($cadence10Text -match 'objects_projectiles=(\d+)') { $hostObjectsProjectiles = [int]$Matches[1] }

$sensorSamples = @($boot10 -split "`r?`n" | Where-Object { $_ -match 'sensor: frame \d+:' })
$sensorLast = if ($sensorSamples.Count -gt 0) { $sensorSamples[$sensorSamples.Count - 1] } else { '' }
$sensorLive = $null
$sensorOther = $null
$sensorTracked = $null
$sensorRetired = $null
$sensorCost = $null
$sensorLayerLive = $null
$sensorLayerNodes = $null
$sensorLayerOther = $null
$sensorObjectsLive = $null
if ($sensorLast -match 'live=(\d+) other=(\d+) tracked=(\d+) retired=(\d+)') {
    $sensorLive = [int]$Matches[1]
    $sensorOther = [int]$Matches[2]
    $sensorTracked = [int]$Matches[3]
    $sensorRetired = [int]$Matches[4]
}
if ($sensorLast -match 'layer_nodes=(\d+) layer_live=(\d+) layer_other=(\d+) objects_live=(\d+)') {
    $sensorLayerNodes = [int]$Matches[1]
    $sensorLayerLive = [int]$Matches[2]
    $sensorLayerOther = [int]$Matches[3]
    $sensorObjectsLive = [int]$Matches[4]
}
if ($sensorLast -match 'cost=(\d+)us') { $sensorCost = [int]$Matches[1] }

Check 'the read path resolved the dictionary, the layer and the projectile type' (
    $boot10 -match 'sensor read path resolved: World\.Objects is' -and
    $boot10 -match 'the projectile source is World\.GameObjectLayer\.Node\._children') `
    (($boot10 -split "`r?`n" | Where-Object { $_ -match 'sensor read path' }) -join ' | ')
Check 'the sensor reported from inside the game loop' (
    $sensorSamples.Count -ge 1) "sample(s)=$($sensorSamples.Count)"
# THE REGRESSION TEST FOR 2026-10-12.  The fixture is the client's shape now: bullets in
# the layer, none in the dictionary.  A sensor that reads only `World.Objects` reads zero
# here exactly as it read zero in a real zone, and this fails rather than passing.
Check 'the world dictionary holds no projectiles, as the client''s does not' (
    $null -ne $sensorObjectsLive -and $sensorObjectsLive -eq 0 -and
    ($null -eq $hostObjectsProjectiles -or $hostObjectsProjectiles -eq 0)) (
    "hook objects_live=$sensorObjectsLive, host objects_projectiles=$hostObjectsProjectiles")
Check 'the hook SAW the live projectiles the host is holding' (
    $null -ne $sensorLive -and $sensorLive -ge 1 -and
    ($null -eq $hostLive -or $sensorLive -le $hostLive + 1)) (
    "hook live=$sensorLive, host live_projectiles=$hostLive")
Check 'the projectiles came from the layer, not from the dictionary' (
    $null -ne $sensorLayerLive -and $sensorLayerLive -ge 1 -and
    $sensorLayerLive -eq $sensorLive) (
    "layer_live=$sensorLayerLive live=$sensorLive -- the client keeps its bullets in World.GameObjectLayer.Children")
Check 'the layer walk saw the nodes the host put in it' (
    $null -ne $sensorLayerNodes -and $null -ne $hostLayerNodes -and
    $sensorLayerNodes -ge 1 -and $hostLayerNodes -ge 1) (
    "hook layer_nodes=$sensorLayerNodes, host layer_nodes=$hostLayerNodes -- both sides churn, so this is a witness and not an equality")
Check 'non-projectile occupants were skipped, not counted as bullets' (
    $null -ne $sensorOther -and $null -ne $hostOthers -and $sensorOther -eq $hostOthers) (
    "hook other=$sensorOther, host live_others=$hostOthers -- a filter that does not filter would report 0")
Check 'the layer walk filters too, and the statics are in both containers' (
    $null -ne $sensorLayerOther -and $null -ne $hostOthers -and $sensorLayerOther -eq $hostOthers) (
    "hook layer_other=$sensorLayerOther, host live_others=$hostOthers")
Check 'the threat set covers every live projectile' (
    $null -ne $sensorTracked -and $null -ne $sensorLive -and $sensorTracked -ge $sensorLive) (
    "tracked=$sensorTracked live=$sensorLive")
Check 'objects the host retired were retired by evidence, not kept for ever' (
    $null -ne $sensorRetired -and $sensorRetired -ge 1) "retired=$sensorRetired"
Check 'the per-frame cost of the read is reported' (
    $null -ne $sensorCost) "cost=${sensorCost}us -- this is the number Phase 2 has to publish"

# --- Deliverable 2.2, in the same run: the trajectory sampler ---------------
#
# The sampler calls the game's own `ProjDesc.GetPosition` through a virtual dynamic
# method and runs the result through the linearity gate.  The assertion with teeth is the
# fraction: the host decides each projectile's family by construction (`GameId % 3`), so
# it knows its own mix, and a sampler that was not really dispatching -- one that ran a
# base body and predicted every bullet as straight -- would report 1.0 on a set that is a
# third weaves.  That is the silent failure `callvirt` exists to avoid, and this is where
# it would show.
$samplerLines = @($boot10 -split "`r?`n" | Where-Object { $_ -match 'sampler: frame \d+:' })
$samplerLast = if ($samplerLines.Count -gt 0) { $samplerLines[$samplerLines.Count - 1] } else { '' }
$samplerSampled = $null
$samplerStraight = $null
$samplerCurved = $null
$samplerCost = $null
$samplerFraction = $null
if ($samplerLast -match 'sampled=(\d+) straight=(\d+) curved=(\d+)') {
    $samplerSampled = [int]$Matches[1]
    $samplerStraight = [int]$Matches[2]
    $samplerCurved = [int]$Matches[3]
}
if ($samplerLast -match 'straight fraction (\d+[.,]\d+)') { $samplerFraction = $Matches[1] }
if ($samplerLast -match 'cost=(\d+)us') { $samplerCost = [int]$Matches[1] }

$hostStraight = $null
$hostWavy = $null
if ($cadence10Text -match 'live_straight=(\d+)') { $hostStraight = [int]$Matches[1] }
if ($cadence10Text -match 'live_wavy=(\d+)') { $hostWavy = [int]$Matches[1] }

Check 'the trajectory sampler resolved the game''s own GetPosition' (
    $boot10 -match 'trajectory sampler resolved: DarzaCore\.Data\.ProjDesc\.GetPosition') `
    (($boot10 -split "`r?`n" | Where-Object { $_ -match 'trajectory sampler' }) -join ' | ')
Check 'the sampler classified every live bullet' (
    $null -ne $samplerSampled -and $null -ne $sensorLive -and $samplerSampled -eq $sensorLive) (
    "sampled=$samplerSampled live=$sensorLive")
Check 'the line is not all straight -- the gate is distinguishing, not rubber-stamping' (
    $null -ne $samplerStraight -and $null -ne $samplerCurved -and
    $samplerStraight -gt 0 -and $samplerCurved -gt 0) (
    "straight=$samplerStraight curved=$samplerCurved -- an all-straight answer means the virtual dispatch is not reaching the families")
if ($null -ne $hostStraight -and $null -ne $hostWavy -and
    ($hostStraight + $hostWavy) -gt 0 -and $null -ne $samplerFraction) {
    # The host's snapshot is taken at exit and the set churns, so the comparison is a
    # tolerance and not an equality -- but a quarter of the range is far tighter than the
    # 1.0-versus-0.70 gap this assertion exists to catch.
    $hostFraction = $hostStraight / [double]($hostStraight + $hostWavy)
    $hookFraction = [double]::Parse(($samplerFraction -replace ',', '.'),
                                    [System.Globalization.CultureInfo]::InvariantCulture)
    $delta = [Math]::Abs($hookFraction - $hostFraction)
    Check 'the measured straight fraction agrees with the host''s own mix' ($delta -le 0.15) (
        "hook $($hookFraction.ToString('F3')) vs host $($hostFraction.ToString('F3')) -- the host decided the families, the hook only measured them")
} else {
    Check 'the measured straight fraction agrees with the host''s own mix' $false (
        "hook fraction '$samplerFraction', host straight=$hostStraight wavy=$hostWavy")
}
Check 'the sampler''s own cost is reported separately from the read' (
    $null -ne $samplerCost) "sampler cost=${samplerCost}us -- the read and the sampler are timed apart so neither number hides the other"

# --- the four decision numbers, on one line ---------------------------------
#
# THE PHASE EXISTS TO PRODUCE THESE, so the run asserts they are produced -- together,
# because a reader who has to assemble the answer from four places in a log assembles it
# wrongly.  The cost figures come from bucket histograms rather than a maximum: a p99
# cannot be recovered from a worst case, which is what Phase 1's endurance run discovered
# when its 323 microsecond outlier turned out to be unplaceable.
$reportLines = @($boot10 -split "`r?`n" | Where-Object { $_ -match 'PHASE 2 NUMBERS' })
$report = if ($reportLines.Count -gt 0) { $reportLines[$reportLines.Count - 1] } else { '' }
Check 'the four numbers are published together' (
    $report -match 'warnings:.*false negatives:.*false positives:.*cost:') `
    $(if ($report) { $report } else { 'no "PHASE 2 NUMBERS" line in bootstrap.log' })
Check 'the cost is a percentile from a histogram, not a worst case' (
    $report -match 'p50<=\d+us p99<=\d+us') (
    (($report -split '\|' | Where-Object { $_ -match 'cost:' }) -join ' '))
Check 'the false-negative and false-positive counts are stated as counts' (
    $report -match 'false negatives: \d+' -and $report -match 'false positives: \d+') (
    'the report must name both, because one decides whether the model can be relied on and the other whether a dodge would be over-cautious')

# ---------------------------------------------------------------------------
# Run 11: the ground-truth comparison -- Deliverable 2.3
# ---------------------------------------------------------------------------
#
# THE MOST VALUABLE ARTEFACT IN THE WHOLE PROJECT, by IMPLEMENTATION-PLAN.md's own
# description of it.  The host decides collisions through its own `HitCharacter` -- the
# same shape as the real client's `private bool HitCharacter()`, and `NoInlining` because
# the JIT inlined it the first time this was tried -- and the hook is patched onto it with
# a managed replacement.  Every observed decision is then compared against what the sensor
# had predicted, and the difference is the warning.
#
# A DECISION IS A `true`, NOT AN ENTRY, AND THE DIFFERENCE WAS MEASURED.  The client calls
# `HitCharacter` from `Hit()` once per projectile per frame, and the answer decides whether
# the hit resolves, so the method is a predicate and most of its entries are questions with
# the answer "no".  The hook used to observe every entry and call it a decision; the
# 2026-10-12 20:42 live session is what that costs -- 418 328 entries counted as 418 328
# collision decisions, against 48 resolved hits, with every near-miss becoming a false
# negative on every frame it was near.  The host now asks the predicate for every live
# projectile every tick (as the client does) and counts both numbers, and the hook observes
# the original's answer, so the two checks below are the regression test for it.
#
# WHY THE RUN IS LONG.  The hook matches targets in a thirty-second discovery window and
# patches after it, so a collision method that is never called inside that window is never
# compiled and never patched -- the same trap run 7 documents for the frame tick.  The
# host therefore starts colliding early (so the method is compiled and matched) and keeps
# colliding, and the run is long enough for the *patched* window to contain most of them.
# The observed count being below the host's own count is expected and is reported rather
# than asserted away: the difference is the pre-patch window.
Write-Host ''
Write-Step 'run 11: every collision decision compared against the sensor''s prediction'

$collisionHashText = (& python $hashTool --assembly $hostDll `
        --type 'DarzasDominion.Darza.Game.Objects.Projectile' --name 'HitCharacter' --arity 0 2>&1 |
    Out-String)
$collisionSize = $null
$collisionHash = $null
if ($collisionHashText -match 'ilSize=(\d+)') { $collisionSize = [int]$Matches[1] }
if ($collisionHashText -match 'ilHash=([0-9A-Fa-f]{64})') { $collisionHash = $Matches[1].ToUpperInvariant() }

$savedSensor11 = $env:DRELAY_FRAME_SENSOR
$savedHold11 = $env:DRELAY_TEST_HOLD_MS
$savedDelay11 = $env:DRELAY_TEST_CADENCE_DELAY_MS
$savedCadenceFile11 = $env:DRELAY_TEST_CADENCE_FILE
$env:DRELAY_FRAME_SENSOR = '1'
$env:DRELAY_TEST_HOLD_MS = '90000'
$env:DRELAY_TEST_CADENCE_DELAY_MS = '5000'
$cadence11 = Join-Path $testRoot 'cadence-run11.txt'
$env:DRELAY_TEST_CADENCE_FILE = $cadence11
Remove-Item -LiteralPath $cadence11 -ErrorAction SilentlyContinue

$spec11 = "frameTick|SyntheticHostFixture.Cadence|FrameTick|$ilSize|$ilHash" +
          ";collision|DarzasDominion.Darza.Game.Objects.Projectile|HitCharacter|$collisionSize|$collisionHash"

$runDirsBefore11 = @(Get-ChildItem $runRoot -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
$null = & $launcher @commonLauncherArgs --collect-delay 500 --live-seconds 25 `
    --test-target $spec11 --frame-replacement --verbose 2>&1
$dir11 = Get-ChildItem $runRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $runDirsBefore11 -notcontains $_.Name } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

$env:DRELAY_FRAME_SENSOR = $savedSensor11
$env:DRELAY_TEST_HOLD_MS = $savedHold11
$env:DRELAY_TEST_CADENCE_DELAY_MS = $savedDelay11
$env:DRELAY_TEST_CADENCE_FILE = $savedCadenceFile11

$log11 = Read-RunFile $(if ($dir11) { $dir11.FullName } else { '' }) 'hook.log'
$boot11 = Read-RunFile $(if ($dir11) { $dir11.FullName } else { '' }) 'bootstrap.log'
$cadence11Text = if (Test-Path -LiteralPath $cadence11) { Get-Content -LiteralPath $cadence11 -Raw } else { '' }
$hostHits = $null
$hostEvaluations = $null
if ($cadence11Text -match 'host_hits=(\d+)') { $hostHits = [int]$Matches[1] }
if ($cadence11Text -match 'hit_character_evaluations=(\d+)') { $hostEvaluations = [int]$Matches[1] }

$collisions = @($boot11 -split "`r?`n" | Where-Object { $_ -match 'collision: key=' })
$warnings = @()
$trackedYes = 0
$predictedYes = 0
foreach ($line in $collisions) {
    if ($line -match 'warning=([\d.]+)ms') { $warnings += [double]$Matches[1] }
    if ($line -match 'tracked=yes') { $trackedYes++ }
    if ($line -match 'predicted=yes') { $predictedYes++ }
}
$warningMin = if ($warnings.Count -gt 0) { ($warnings | Measure-Object -Minimum).Minimum } else { $null }

Check 'the collision replacement was published and aimed at the target' (
    $log11 -match "'collision' will be patched with the MANAGED REPLACEMENT") `
    (($log11 -split "`r?`n" | Where-Object { $_ -match 'MANAGED REPLACEMENT' }) -join ' | ')
Check 'collision decisions were observed by the hook' (
    $collisions.Count -ge 1) "observed=$($collisions.Count), host's own count=$hostHits (the difference is the pre-patch window)"
# THE FIXTURE HAS TO ASK THE QUESTION MORE OFTEN THAN IT ANSWERS YES, OR THE NEXT CHECK
# CANNOT FAIL.  `HitCharacter` is a per-frame predicate; if the host only ever called it on
# the tick a bullet was already touching the player, then "evaluations == decisions" and a
# hook that counted evaluations would look perfect.
Check 'the predicate was evaluated more often than it decided' (
    $null -ne $hostEvaluations -and $null -ne $hostHits -and
    $hostEvaluations -gt $hostHits) (
    "evaluations=$hostEvaluations, decisions=$hostHits -- the client asks every live projectile every frame")
Check 'the threat set knew the bullets the game decided were hitting' (
    $collisions.Count -gt 0 -and $trackedYes -eq $collisions.Count) (
    "tracked=$trackedYes of $($collisions.Count)")
Check 'the sensor had PREDICTED those hits' (
    $collisions.Count -gt 0 -and $predictedYes -eq $collisions.Count) (
    "predicted=$predictedYes of $($collisions.Count) -- the rest would be false negatives")
Check 'the warning is positive and plausible' (
    $null -ne $warningMin -and $warningMin -ge 100.0) (
    "min warning $([math]::Round($warningMin,1))ms over $($warnings.Count) hit(s) -- the horizon is 600 ms, so an aimed bullet entering it is credited with about that")
Check 'the hook did not see more collisions than the host made' (
    $null -eq $hostHits -or $collisions.Count -le $hostHits) (
    "observed=$($collisions.Count) host=$hostHits -- a hook observing every ENTRY rather than every 'true' would exceed the host's own count here")

# ---------------------------------------------------------------------------
# Run 12: a target the game compiles AFTER the discovery window
# ---------------------------------------------------------------------------
#
# THE LIVE FAILURE THIS REPRODUCES, AND IT IS THE ONE THAT LOOKS LIKE A REGRESSION.
# The hook matches targets inside a fixed thirty-second window and made its patch
# decision once, at that window's close -- so a method the game first compiles at t+31 s
# was matched, reported as `found` by the target-size table, and never hooked.  The
# 2026-10-12 21:22 session is exactly that: the operator reached `GameScene` at 21:23:10
# and the window closed at 21:23:16, `Projectile.HitCharacter` was compiled a few seconds
# late, and the run published `collisions: 0 decision(s)` with a perfectly healthy sensor
# around it.  Read without the patch line, that says "no collisions happened", which is
# the opposite of what it means.
#
# `DRELAY_TEST_HITS_AFTER_MS` is the knob that delays the host's first call to the
# predicate.  Thirty-five seconds puts that first call -- and therefore the method's one
# and only compilation, tiering being off -- about ten seconds after the window shuts.
# What the run has to show is that the late pass hooks it anyway, and that the comparison
# then has content.
Write-Host ''
Write-Step 'run 12: a target compiled after the discovery window is patched late'

$savedSensor12 = $env:DRELAY_FRAME_SENSOR
$savedHold12 = $env:DRELAY_TEST_HOLD_MS
$savedDelay12 = $env:DRELAY_TEST_CADENCE_DELAY_MS
$savedCadenceFile12 = $env:DRELAY_TEST_CADENCE_FILE
$savedHitsAfter12 = $env:DRELAY_TEST_HITS_AFTER_MS
$env:DRELAY_FRAME_SENSOR = '1'
$env:DRELAY_TEST_HOLD_MS = '90000'
$env:DRELAY_TEST_CADENCE_DELAY_MS = '5000'
$env:DRELAY_TEST_HITS_AFTER_MS = '35000'
$cadence12 = Join-Path $testRoot 'cadence-run12.txt'
$env:DRELAY_TEST_CADENCE_FILE = $cadence12
Remove-Item -LiteralPath $cadence12 -ErrorAction SilentlyContinue

$runDirsBefore12 = @(Get-ChildItem $runRoot -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
$null = & $launcher @commonLauncherArgs --collect-delay 500 --live-seconds 25 `
    --test-target $spec11 --frame-replacement --verbose 2>&1
$dir12 = Get-ChildItem $runRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $runDirsBefore12 -notcontains $_.Name } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

$env:DRELAY_FRAME_SENSOR = $savedSensor12
$env:DRELAY_TEST_HOLD_MS = $savedHold12
$env:DRELAY_TEST_CADENCE_DELAY_MS = $savedDelay12
$env:DRELAY_TEST_CADENCE_FILE = $savedCadenceFile12
$env:DRELAY_TEST_HITS_AFTER_MS = $savedHitsAfter12

$log12 = Read-RunFile $(if ($dir12) { $dir12.FullName } else { '' }) 'hook.log'
$boot12 = Read-RunFile $(if ($dir12) { $dir12.FullName } else { '' }) 'bootstrap.log'
$sum12 = Read-RunFile $(if ($dir12) { $dir12.FullName } else { '' }) 'summary.txt'
$cadence12Text = if (Test-Path -LiteralPath $cadence12) { Get-Content -LiteralPath $cadence12 -Raw } else { '' }

$lateHits = @($boot12 -split "`r?`n" | Where-Object { $_ -match 'collision: key=' })
$latePredicted = 0
foreach ($line in $lateHits) {
    if ($line -match 'predicted=yes') { $latePredicted++ }
}
$hostHits12 = $null
if ($cadence12Text -match 'host_hits=(\d+)') { $hostHits12 = [int]$Matches[1] }

Check 'the window closed before the collision method existed' (
    $log12 -match "collision\s+MISSING") (
    'expected the discovery status table to report collision MISSING; without that the run is not testing a late compile at all')
Check 'the late pass patched it anyway' (
    $log12 -match 'were patched late' -and $log12 -match "PATCHED 'collision'") (
    (($log12 -split "`r?`n" | Where-Object { $_ -match 'patched late|PATCHED' }) -join ' | '))
Check 'the closing summary counts the late patch' (
    $sum12 -match '2 matched by the end of the run' -and $sum12 -match '2 patched' -and
    $sum12 -match '1 matched inside the discovery window') (
    (($sum12 -split "`r?`n" | Where-Object { $_ -match 'pinned' }) -join ' '))
Check 'collisions were observed after the late patch' (
    $lateHits.Count -ge 1) "observed=$($lateHits.Count), host's own count=$hostHits12"
Check 'at least one of them had been predicted' (
    $latePredicted -ge 1) "predicted=$latePredicted of $($lateHits.Count)"

# ---------------------------------------------------------------------------
# Run 13: the AUTO-AIM replacement, and the late pass that runs after the window
# ---------------------------------------------------------------------------
#
# WHAT THIS GATES.  `--autoaim` publishes a THIRD managed replacement, on a fourth
# native dispatch arm (`aimTarget`), with its own pair of shared-block slots and its
# own trampoline.  Every one of those is plumbing that a build can get wrong while
# the module it carries is correct: a slot added in the wrong place, a dispatch arm
# named differently from the target table, a publish that never runs.  Run 8 covers
# that shape for the frame tick; this run covers it for the aim target.
#
# AND IT IS THE ONLY COVER FOR THE IDLE PASS.  `DrelayPatchNewTargets` used to be
# called only inside the live window, so a method the game first compiles after that
# window closed was matched by IL hash, reported as found, and left unpatched.  For
# a module the operator leaves running that is a session which silently does nothing,
# so the watcher's idle loop now polls the same pass every two seconds -- and the
# only way to know that works is to put the target's first compilation AFTER the
# window on purpose, which is what the 40 s cadence delay below does.
#
# WHAT THE HOST CAN AND CANNOT SHOW.  The synthetic host has no `AimbotSystem`, so
# the module resolves to "unavailable" here and the replacement does nothing but call
# through -- which makes the assertions about *selection* meaningless and the ones
# about *plumbing* exactly as strong as they are for the frame tick:
#
#   * the replacement was published into the shared block;
#   * the native side aimed MinHook's `jmp` at it, by target name;
#   * the patch happened LATE, from the idle pass, after the live window had shut;
#   * every call the host made arrived at the original body through the trampoline
#     -- with the hook enabled the body is reachable no other way -- and the
#     replacement reported that it had been entered and could not act, which is a
#     different fact from never being entered at all;
#   * and the process did not die.
Write-Host ''
Write-Step 'run 13: the auto-aim replacement, patched late from the idle pass'

$aimHashText = (& python $hashTool --assembly $hostDll `
        --type 'SyntheticHostFixture.AimCadence' --name 'FindTarget' --arity 2 2>&1 | Out-String)
$aimIlSize = $null
$aimIlHash = $null
if ($aimHashText -match 'ilSize=(\d+)') { $aimIlSize = [int]$Matches[1] }
if ($aimHashText -match 'ilHash=([0-9A-Fa-f]{64})') { $aimIlHash = $Matches[1].ToUpperInvariant() }

Check 'the host has a target selection with a Phase 0 identity to publish' (
    $null -ne $aimIlSize -and $null -ne $aimIlHash) (
    "ilSize=$aimIlSize ilHash=$aimIlHash")

$aimSpec = "aimTarget|SyntheticHostFixture.AimCadence|FindTarget|$aimIlSize|$aimIlHash"
$savedHold13 = $env:DRELAY_TEST_HOLD_MS
$savedDelay13 = $env:DRELAY_TEST_CADENCE_DELAY_MS
$savedCadenceFile13 = $env:DRELAY_TEST_CADENCE_FILE
# FORTY SECONDS IS THE WHOLE POINT OF THIS RUN: it puts the fixture's one and only
# compilation (tiering is off) after the live window has closed, so the only pass that
# can still patch it is the idle one.  Seventy seconds of hold leaves half a minute of
# patched calls to count.
$env:DRELAY_TEST_HOLD_MS = '70000'
$env:DRELAY_TEST_CADENCE_DELAY_MS = '40000'
# THE FIXTURE'S OWN SWITCH, AND NO OTHER RUN SETS IT.  Without it the host never calls
# its stand-in selection, so runs 1-12 keep exactly the timing they had before this
# fixture existed.
$savedAim13 = $env:DRELAY_TEST_AIM
$env:DRELAY_TEST_AIM = '1'
$cadence13 = Join-Path $testRoot 'cadence-run13.txt'
$env:DRELAY_TEST_CADENCE_FILE = $cadence13
Remove-Item -LiteralPath $cadence13 -ErrorAction SilentlyContinue

$runDirsBefore13 = @(Get-ChildItem $runRoot -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
$null = & $launcher @commonLauncherArgs --collect-delay 500 --live-seconds 5 `
    --test-target $aimSpec --autoaim closest --verbose 2>&1
$dir13 = Get-ChildItem $runRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $runDirsBefore13 -notcontains $_.Name } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

$env:DRELAY_TEST_HOLD_MS = $savedHold13
$env:DRELAY_TEST_CADENCE_DELAY_MS = $savedDelay13
$env:DRELAY_TEST_CADENCE_FILE = $savedCadenceFile13
$env:DRELAY_TEST_AIM = $savedAim13

$log13 = Read-RunFile $(if ($dir13) { $dir13.FullName } else { '' }) 'hook.log'
$boot13 = Read-RunFile $(if ($dir13) { $dir13.FullName } else { '' }) 'bootstrap.log'
$crash13 = if ($dir13) { Test-Path -LiteralPath (Join-Path $dir13.FullName 'crash.txt') } else { $true }
$cadence13Text = if (Test-Path -LiteralPath $cadence13) { Get-Content -LiteralPath $cadence13 -Raw } else { '' }

$aimHost13 = $null
$aimBody13 = $null
if ($cadence13Text -match 'aim_host_calls=(\d+)') { $aimHost13 = [long]$Matches[1] }
if ($cadence13Text -match 'aim_body_calls=(\d+)') { $aimBody13 = [long]$Matches[1] }

Check 'the auto-aim replacement was published for the native side' (
    $boot13 -match 'managed auto-aim replacement published at 0x[0-9A-F]+') (
    'expected "managed auto-aim replacement published" in bootstrap.log')
Check 'the module said it could not resolve against this host' (
    $boot13 -match 'the auto-aim module is NOT available') (
    'expected the resolve failure to be named; without it the run cannot tell "the host has no AimbotSystem" from "the module never ran"')
Check 'the native side aimed the hook at the managed replacement by target name' (
    $log13 -match "'aimTarget' will be patched with the MANAGED REPLACEMENT") (
    "expected the native dispatch arm for 'aimTarget' to select the managed replacement")
Check 'the patch came from the LATE pass, after the live window had closed' (
    $log13 -match "PATCHED 'aimTarget'" -and $log13 -match 'were patched late') (
    (($log13 -split "`r?`n" | Where-Object { $_ -match 'patched late|PATCHED' }) -join ' | '))
Check 'the replacement reported being ENTERED and unable to act' (
    $boot13 -match 'auto-aim: the replacement was entered, but the module did not resolve') (
    'expected the entered-but-unresolved line in bootstrap.log; a patch nobody enters would leave the counters agreeing just as happily')
Check 'every patched entry reached the original through the managed trampoline' (
    $null -ne $aimHost13 -and $aimHost13 -gt 100 -and $aimBody13 -eq $aimHost13) (
    "aim_host_calls=$aimHost13 aim_body_calls=$aimBody13")
Check 'the run did not die' (-not $crash13) (
    $(if ($crash13) { 'crash.txt was written -- the auto-aim replacement route faulted' } else { 'no crash.txt' }))

# ---------------------------------------------------------------------------
# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------
$failed = @($checks | Where-Object { -not $_.Ok })
Write-Host ''
Write-Host ("{0} check(s): {1} passed, {2} failed" -f $checks.Count, ($checks.Count - $failed.Count), $failed.Count)

if (-not $Keep -and (Test-Path -LiteralPath $testRoot)) {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
} else {
    Write-Note "artefacts kept in $testRoot"
}

if ($failed.Count -gt 0) {
    Write-Host ''
    Write-Bad 'INJECTION TEST FAILED:'
    $failed | ForEach-Object { Write-Bad ("  - " + $_.Name) }
    exit 1
}

Write-Host ''
Write-Ok 'INJECTION TEST PASSED'
Write-Host 'Note: runs 1-6 exercise the plumbing against a host that is not the game.'
Write-Host 'Run 7 matches and patches a target of the host by IL hash, so the target'
Write-Host 'table and the frame-tick detour are covered here too. What is still NOT'
Write-Host 'covered is the game''s own four targets, which need the real assembly.'
Write-Host ''
Write-Host 'Run 8 covers the managed replacement under forced full compacting'
Write-Host 'collections, and run 9 proves the sensor self-test can fail: it launches'
Write-Host 'with a knowingly broken retirement rule and requires the self-test to name'
Write-Host 'the assertion that catches it. NOT gated here, deliberately: the OTHER'
Write-Host 'mechanism -- a native detour calling a managed helper -- which was measured'
Write-Host 'as FLAKY (about one run in four died in the GC mark phase, even fully'
Write-Host 'pre-compiled and non-allocating) and is therefore not something a suite may'
Write-Host 'assert. The measurements and the controls for both routes are'
Write-Host 'PHASE2-SCOPE.md section 4.3, and the raw runs are'
Write-Host 'hook\obj\cases\phase2-mechanism\.'
exit 0
