# ============================================================================
#  Test-HookOffline.ps1 -- every hook check that does not need the game installed.
#
#  WHY THIS FILE EXISTS
#  --------------------
#  Most of this component's verification is *pinned to one installed build of one game*, and that is
#  the point of it: Fingerprint.ps1 compares the installed assembly against the values in
#  known-build.json, il_index.py --check re-derives each target's IL hash out of that assembly, and
#  Test-Injection.ps1 stages the game's own runtime files beside a synthetic host. None of those can
#  run on a build machine that has no copy of Darza's Dominion, so none of them run in CI.
#
#  What is left is this list: the checks whose answer depends only on the sources in this directory.
#  They are worth running in CI because they are exactly the ones a refactor breaks -- the DLL's
#  exported ABI, the two generated files against the record they are generated from, the two report
#  tools against their fixtures, and the launcher's configuration invariants (the four sources, their
#  per-field precedence, the discovery walk and the environment the child is given; its discovery
#  runs against a fake Steam tree, so it needs no game and no registry either).
#
#  THE METADATA READER'S SELF-TEST IS NOT ON THAT LIST, AND IT USED TO BE.  It reads the game's own
#  PE -- `il_index.py --self-test` builds a `PE(assembly)` and checks the reader against the method,
#  type and parameter counts the documents state for *this* build -- so on a machine without the game
#  it cannot run at all. It was listed here because its docstring says it reads neither the record nor
#  the IL dump, which is true of the function and beside the point: it needs the assembly. The first
#  hook run in CI is how that was found, and it is now conditional on the install being present, and
#  named when it is not.
#
#  The game-pinned checks are NOT silently skipped. They are named at the end of every run, so a
#  green CI result is never read as "the hook was verified against the game".
#
#  Usage:
#    .\Test-HookOffline.ps1                     # run everything, exit 1 on a failure
#    .\Test-HookOffline.ps1 -Quiet              # suppress the per-check output
#    .\Test-HookOffline.ps1 -GamePath <dir>     # an install that is not where the record says
#
#  Exit codes: 0 = every check passed, 1 = a check failed, 2 = a toolchain is missing.
# ============================================================================

[CmdletBinding()]
param(
    [switch]$Quiet,
    # The install to point the metadata reader's self-test at, when it has moved.  Default: the
    # `recordedFrom` path in known-build.json, which is where the build the record describes was.
    [string]$GamePath
)

$ErrorActionPreference = 'Stop'
$tools = Split-Path -Parent $MyInvocation.MyCommand.Path
$hookRoot = Split-Path -Parent $tools
$outDir = Join-Path $hookRoot 'out'

function Write-Step { param([string]$t) if (-not $Quiet) { Write-Host $t -ForegroundColor Cyan } }
function Write-Ok   { param([string]$t) Write-Host $t -ForegroundColor Green }
function Write-Bad  { param([string]$t) Write-Host $t -ForegroundColor Red }
function Write-Note { param([string]$t) Write-Host $t -ForegroundColor DarkGray }

# A FAILING CHECK HAS TO BE READABLE FROM THE OUTSIDE.
#
# These checks gate a release, and the first time they ran in CI the step reported nothing but
# "Process completed with exit code 1": a run's step log needs a signed-in browser, the check-run
# API carries only the annotations, and `gh` is not installed everywhere. So a failure is emitted
# as a runner ANNOTATION as well as a line of output -- `::error::` is the runner's own protocol,
# and it is what the API can read back. GITHUB_ACTIONS is set by the runner, so a local run prints
# nothing extra.
function Write-Annotation {
    param([string]$Text)
    if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host "::error title=offline checks::$Text" }
}

# WHICH CHECK WAS RUNNING, KEPT WHERE AN UNPLANNED STOP CAN REACH IT.  A terminating error -- an
# exception rather than a non-zero exit code -- used to abort the script with nothing said about
# where it happened, which is the other half of the same blind spot.
$script:currentCheck = 'startup'

trap {
    $where = "the offline suite stopped in '$script:currentCheck': " + $_.Exception.Message
    Write-Bad $where
    Write-Note "  the check's own output is above, under '--- $script:currentCheck'"
    Write-Annotation $where
    exit 1
}

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    Write-Bad 'python is not on PATH; the generators and the metadata reader need it.'
    Write-Annotation 'python is not on PATH; the generators and the metadata reader need it.'
    exit 2
}
foreach ($required in 'Drelay.Hook.dll', 'Drelay.Bootstrap.dll', 'Drelay.Launcher.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $outDir $required))) {
        $missing = "missing $outDir\$required -- run build.ps1 first."
        Write-Bad $missing
        Write-Annotation $missing
        exit 2
    }
}

$failures = @()
$ran = @()

function Invoke-Check {
    param([string]$Name, [scriptblock]$Body)
    $script:currentCheck = $Name
    Write-Step "--- $Name"
    & $Body
    if ($LASTEXITCODE -ne 0) {
        Write-Bad "FAIL: $Name"
        Write-Annotation "FAIL: $Name (exit $LASTEXITCODE)"
        $script:failures += $Name
    } else {
        if (-not $Quiet) { Write-Ok "ok: $Name" }
    }
    $script:ran += $Name
}

# The DLL's exported ABI: the three exports exist, the version constant is the one this source
# declares, and Init refuses without a session. No game, no elevation, no host process.
Invoke-Check 'ABI self-test' {
    & (Join-Path $tools 'Test-HookAbi.ps1')
}

# The PE/CLI metadata reader against the signatures the documents state for THIS BUILD.
#
# IT READS THE INSTALLED ASSEMBLY, SO IT IS GAME-PINNED -- see the header. The assembly is resolved
# the way `il_index.py` resolves it (the `recordedFrom` path plus the assembly's file name in
# known-build.json) unless -GamePath says the install has moved, and when it is not there the check
# is NAMED as not run rather than failed: a machine without the game has not discovered a defect.
$selfTestAssembly = $null
try {
    $record = Get-Content -LiteralPath (Join-Path $hookRoot 'known-build.json') -Raw | ConvertFrom-Json
    $selfTestDir = if ($GamePath) { $GamePath } else { $record.recordedFrom }
    if ($selfTestDir -and $record.assembly.file) {
        $selfTestAssembly = Join-Path $selfTestDir $record.assembly.file
    }
} catch {
    Write-Note "could not read known-build.json to locate the assembly: $($_.Exception.Message)"
}

$selfTestRan = $false
if ($selfTestAssembly -and (Test-Path -LiteralPath $selfTestAssembly)) {
    Invoke-Check 'metadata reader self-test' {
        & python (Join-Path $tools 'il_index.py') --self-test --assembly $selfTestAssembly
    }
    $selfTestRan = $true
} else {
    Write-Step '--- metadata reader self-test'
    Write-Note 'not run: the installed game assembly was not found where the record says it is.'
    Write-Note "  looked for: $selfTestAssembly"
    Write-Note '  this check reads the game''s own PE, so it needs the install; pass -GamePath <dir>'
    Write-Note '  if it is somewhere else. It is NOT counted as a pass.'
}

# The two committed generated files must be exactly what the generators produce right now. This is
# the check that catches a hand-edit of GeneratedBuildTable.cs or GeneratedSharedLayout.cs, and the
# one that catches a change to known-build.json that was never regenerated into the hook.
Invoke-Check 'generated sources are up to date' {
    & python (Join-Path $tools 'gen_target_table.py') --check
    if ($LASTEXITCODE -ne 0) { return }
    & python (Join-Path $tools 'gen_shared_layout.py') --check
}

# The report tools against their own fixtures: a run log with no movement must be reported as such,
# and a steering run must not be failed for declining to escape.
Invoke-Check 'move-budget report fixture' {
    & python (Join-Path $tools 'tests\test_move_budget.py')
}
Invoke-Check 'steering report fixture' {
    & python (Join-Path $tools 'tests\test_steering_report.py')
}

# The WORLD LOG's writer, against a fake game. It compiles the real `WorldLog.cs` and the real
# `EnemyReader.cs` and parses every line the writer emits, so this is the check that catches a
# malformed line, an unescaped portal title, or a "8 nearest" that is really "first 8" -- before the
# operator's one collection session (PLAN-AUTOFARM.md section 6) is spent on it. It found two real
# defects on its first run; `WorldLogVerify.csproj`'s header names them.
Invoke-Check 'world log writer fixture' {
    $verify = Join-Path $hookRoot 'tools\WorldLogVerify\bin\Release\net8.0\WorldLogVerify.exe'
    if (-not (Test-Path -LiteralPath $verify)) {
        Write-Bad "missing $verify -- run build.ps1 first."
        exit 2
    }
    & $verify
}

# The launcher's configuration: HANDOFF-LAUNCH-CONFIG.md section 13's invariants C1-C8, plus the
# boundary cases section 11 asks for. It compiles the launcher's own settings, config reader,
# command-line parser, discovery walk and environment builder, so this check is about the shipped
# logic rather than a copy of it -- and it needs no game, no Steam and no registry, because the
# whole discovery walk runs against a fake tree inside the harness.
#
# `--source` and `--known-build` are passed because two of the checks are about a file rather than
# about a value: C5 asserts the single `Console.ReadKey` in `Program.Main` is behind
# `FirstRun.ShouldPause`, and C8 asserts the pinned record still carries the Steam app id and the
# OpenGL assembly size that discovery names the wrong-install case with.
Invoke-Check 'launcher configuration (C1-C8)' {
    $harness = Join-Path $hookRoot 'tools\LauncherHarness\bin\Release\LauncherHarness.exe'
    if (-not (Test-Path -LiteralPath $harness)) {
        Write-Bad "missing $harness -- run build.ps1 first."
        exit 2
    }
    & $harness --source (Join-Path $hookRoot 'managed\Drelay.Launcher\Program.cs') `
               --known-build (Join-Path $hookRoot 'known-build.json')
}

Write-Host ''
if ($failures.Count -gt 0) {
    $summary = "OFFLINE CHECKS FAILED: " + ($failures -join ', ')
    Write-Bad $summary
    Write-Annotation $summary
    exit 1
}

Write-Ok "OFFLINE CHECKS OK -- $($ran.Count) check(s): $($ran -join ', ')"
Write-Host ''
Write-Note 'NOT RUN HERE, and deliberately so -- each needs an installed copy of the game:'
Write-Note '  tools\Fingerprint.ps1          the static gate against the pinned build'
Write-Note '  tools\il_index.py --check      re-derives the target table from the installed assembly'
Write-Note '  tools\il_index.py --mutation-test'
if (-not $selfTestRan) {
    Write-Note '  tools\il_index.py --self-test  the reader against this build''s own metadata'
}
Write-Note '  tools\Test-FingerprintGate.ps1 the gate against a mutant copy'
Write-Note '  tools\Test-PeReader.ps1        compares the native reader against the game assembly'
Write-Note '  tools\Test-Injection.ps1       stages the game runtime beside the synthetic host'
Write-Note '  tools\check_opcode_table.py    walks the IL dump, which is not in the repository'
Write-Note 'Run them there; docs/hook/HOOK-BUILD-MATRIX.md is the procedure and the order.'
exit 0
