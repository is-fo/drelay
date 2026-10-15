# ============================================================================
#  Capture-DodgeDecisions.ps1 -- replay every recording and every scenario
#  through the current SolverHarness, and write the whole surface out.
#
#  WHY THIS EXISTS AS A SCRIPT. HANDOFF-DIRECTED-DODGE.md section 7's baseline
#  table is nine commands and it has to be re-run identically for every
#  experiment, or the before and after columns describe two different things.
#  The decision dumps it writes are what makes an experiment a *diff of two
#  files* rather than a mean over a 400-line sample -- section 6.10 is the
#  reason, and `tools/decisions_report.py` reads them.
#
#  Usage:
#    .\hook\tools\Capture-DodgeDecisions.ps1 -Out ..\work\dd-before
#    .\hook\tools\Capture-DodgeDecisions.ps1 -Out ..\work\dd-after -Harness <exe>
#
#  Exit codes: 0 = captured, 2 = the harness was not found or a run failed.
# ============================================================================

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Harness,
    [string[]]$Recordings = @(
        '20261015-021911-p20152',
        '20261015-024717-p5824',
        '20261015-032549-p26684',
        '20261015-151232-p5424',
        '20261015-153805-p26156'
    ),
    [string[]]$Scenarios = @('peaceful','headon','dodge','volley','corridor','passingby'),
    [switch]$SkipCost
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
if (-not [System.IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $root $Out }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

if (-not $Harness) {
    $Harness = Join-Path $root 'hook\tools\SolverHarness\bin\Release\SolverHarness.exe'
}
if (-not (Test-Path -LiteralPath $Harness)) { Write-Error "no harness at $Harness"; exit 2 }
$Harness = (Resolve-Path -LiteralPath $Harness).Path

$hookRuns = Join-Path $env:LOCALAPPDATA 'drelay\hook'
$failed = 0
$costRecording = '20261015-153805-p26156'

foreach ($d in $Recordings) {
    $runDir = Join-Path $hookRuns $d
    if (-not (Test-Path -LiteralPath (Join-Path $runDir 'trace.jsonl'))) {
        Write-Warning "no trace.jsonl in $runDir -- skipped"
        continue
    }
    $name = ($d -split '-')[-1]
    & $Harness --trace (Join-Path $runDir 'trace.jsonl') `
               --collisions (Join-Path $runDir 'bootstrap.log') `
               --decisions (Join-Path $Out "dec-$name.txt") *> (Join-Path $Out "rec-$name.txt")
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1) { $failed++ }
    if (-not $SkipCost -and $d -eq $costRecording) {
        & $Harness --trace (Join-Path $runDir 'trace.jsonl') `
                   --collisions (Join-Path $runDir 'bootstrap.log') `
                   --repeat 50 *> (Join-Path $Out 'cost-p26156-repeat50.txt')
        if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1) { $failed++ }
    }
}

foreach ($s in $Scenarios) {
    & $Harness --scenario $s --frames 900 --decisions (Join-Path $Out "dec-scen-$s.txt") `
        *> (Join-Path $Out "scen-$s.txt")
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1) { $failed++ }
}

Write-Host "captured into $Out" -ForegroundColor Green
if ($failed -gt 0) { Write-Host "$failed run(s) exited abnormally" -ForegroundColor Red; exit 2 }
exit 0
