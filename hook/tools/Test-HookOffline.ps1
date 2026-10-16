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
#  exported ABI, the metadata reader against signatures the documents state independently, the two
#  generated files against the record they are generated from, the two report tools against their
#  fixtures, and the launcher's configuration invariants (the four sources, their per-field
#  precedence, the discovery walk and the environment the child is given; its discovery runs against
#  a fake Steam tree, so it needs no game and no registry either).
#
#  The game-pinned checks are NOT silently skipped. They are named at the end of every run, so a
#  green CI result is never read as "the hook was verified against the game".
#
#  Usage:
#    .\Test-HookOffline.ps1            # run everything, exit 1 on the first failure
#    .\Test-HookOffline.ps1 -Quiet     # suppress the per-check output
#
#  Exit codes: 0 = every check passed, 1 = a check failed, 2 = a toolchain is missing.
# ============================================================================

[CmdletBinding()]
param(
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$tools = Split-Path -Parent $MyInvocation.MyCommand.Path
$hookRoot = Split-Path -Parent $tools
$outDir = Join-Path $hookRoot 'out'

function Write-Step { param([string]$t) if (-not $Quiet) { Write-Host $t -ForegroundColor Cyan } }
function Write-Ok   { param([string]$t) Write-Host $t -ForegroundColor Green }
function Write-Bad  { param([string]$t) Write-Host $t -ForegroundColor Red }
function Write-Note { param([string]$t) Write-Host $t -ForegroundColor DarkGray }

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    Write-Bad 'python is not on PATH; the generators and the metadata reader need it.'
    exit 2
}
foreach ($required in 'Drelay.Hook.dll', 'Drelay.Bootstrap.dll', 'Drelay.Launcher.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $outDir $required))) {
        Write-Bad "missing $outDir\$required -- run build.ps1 first."
        exit 2
    }
}

$failures = @()
$ran = @()

function Invoke-Check {
    param([string]$Name, [scriptblock]$Body)
    Write-Step "--- $Name"
    & $Body
    if ($LASTEXITCODE -ne 0) {
        Write-Bad "FAIL: $Name"
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

# The PE/CLI metadata reader against signatures the documents state independently of the metadata.
# It reads neither known-build.json nor the IL dump, so it is meaningful on any machine.
Invoke-Check 'metadata reader self-test' {
    & python (Join-Path $tools 'il_index.py') --self-test
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
    Write-Bad ("OFFLINE CHECKS FAILED: " + ($failures -join ', '))
    exit 1
}

Write-Ok "OFFLINE CHECKS OK -- $($ran.Count) check(s): $($ran -join ', ')"
Write-Host ''
Write-Note 'NOT RUN HERE, and deliberately so -- each needs an installed copy of the game:'
Write-Note '  tools\Fingerprint.ps1          the static gate against the pinned build'
Write-Note '  tools\il_index.py --check      re-derives the target table from the installed assembly'
Write-Note '  tools\il_index.py --mutation-test'
Write-Note '  tools\Test-FingerprintGate.ps1 the gate against a mutant copy'
Write-Note '  tools\Test-PeReader.ps1        compares the native reader against the game assembly'
Write-Note '  tools\Test-Injection.ps1       stages the game runtime beside the synthetic host'
Write-Note '  tools\check_opcode_table.py    walks the IL dump, which is not in the repository'
Write-Note 'Run them there; docs/hook/HOOK-BUILD-MATRIX.md is the procedure and the order.'
exit 0
