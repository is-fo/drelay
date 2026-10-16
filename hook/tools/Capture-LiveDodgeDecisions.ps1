# ============================================================================
#  Capture-LiveDodgeDecisions.ps1 -- replay the three ACTING live sessions
#  through the current SolverHarness, with a decision dump.
#
#  WHY THIS IS A SEPARATE SCRIPT FROM Capture-DodgeDecisions.ps1. That one covers
#  the five *recordings*, which are the project's long-standing invariant and
#  direction-split corpus. This one covers the live acting sessions of
#  SPIKE3.2-OFFLINE-SOLVER.md section 15.9.3c, which are the only traces where
#  the operator was actually playing against the dodge -- and therefore the only
#  ones that can say anything about the halt defect, whose measurement is the
#  player's own `moving` column.
#
#  `p6412`'s trace is the one section 15.12 is about: its `H` record carries an
#  unescaped newline from before the writer was fixed. The repaired copy is used
#  here (tools\repair-trace.py), and the run is named in the output so a reader
#  knows which file the numbers came from.
#
#  Usage:
#    .\tools\Capture-LiveDodgeDecisions.ps1 -Out .\work\live-after
# ============================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Harness,
    [string]$P6412Trace,
    [string]$P32752Trace
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not [System.IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $root $Out }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
if (-not $Harness) {
    $Harness = Join-Path $root 'tools\SolverHarness\bin\Release\SolverHarness.exe'
}
if (-not (Test-Path -LiteralPath $Harness)) { Write-Error "no harness at $Harness"; exit 2 }
$Harness = (Resolve-Path -LiteralPath $Harness).Path
if (-not $P6412Trace) { $P6412Trace = Join-Path $root 'work\repair\trace-fixed.jsonl' }
# `p32752` IS THE SECOND SESSION WITH THE SAME WRITER DEFECT, and it failed the same way the first
# version of this script did: the raw trace throws `expected a number at offset 0` at line 2. The
# repaired copy is one byte larger than the original -- `repair-trace.py` writes a trailing newline
# the recorder did not -- and is otherwise the same recording.
if (-not $P32752Trace) { $P32752Trace = Join-Path $root 'work\repair2\run\trace.jsonl' }
# THE DEFAULT ABOVE NO LONGER EXISTS. `work/` is scratch and is not versioned, and the raw traces
# named `trace.jsonl` were pruned as stale -- so on a fresh checkout, and on this one, the p32752
# session has no default trace to read. Say that here rather than letting a JSON parser fail on a
# missing file, and point at the surviving repaired copies for this session and its sibling.
foreach ($pair in @(
    @{ Path = $P6412Trace;  What = 'the p6412 trace' },
    @{ Path = $P32752Trace; What = 'the p32752 trace' }
)) {
    if (-not (Test-Path -LiteralPath $pair.Path)) {
        Write-Error @"
$($pair.What) is not at $($pair.Path).
The raw traces named trace.jsonl were pruned from work/ as stale, so a repaired copy has to be
supplied: pass -P6412Trace <file> and -P32752Trace <file>. Surviving repaired traces in this tree:
  $(Join-Path $root 'work\repair\trace-fixed.jsonl')
  $(Join-Path $root 'work\repair\trace-repaired.jsonl')
  $(Join-Path $root 'work\repair2\check.jsonl')
Repair a raw trace with: python tools\repair-trace.py <in.jsonl> <out.jsonl>
"@
        exit 2
    }
}
$hookRuns = Join-Path $env:LOCALAPPDATA 'drelay\hook'

$sessions = @(
    @{ Name = 'p34740'; Run = '20261015-212859-p34740'; Trace = $null;                               Note = 'acting, the axial build' },
    @{ Name = 'p6412';  Run = '20261015-214418-p6412';  Trace = $P6412Trace;                          Note = 'acting, trace repaired per 15.12' },
    @{ Name = 'p32752'; Run = '20261015-214927-p32752'; Trace = $P32752Trace;                         Note = 'acting, third session, trace repaired' },
    @{ Name = 'p440';   Run = '20261015-212602-p440';   Trace = $null;                               Note = 'DRY RUN, the field test' }
)

foreach ($s in $sessions) {
    $runDir = Join-Path $hookRuns $s.Run
    $trace = $s.Trace
    if (-not $trace) { $trace = Join-Path $runDir 'trace.jsonl' }
    if (-not (Test-Path -LiteralPath $trace)) {
        Write-Warning "no trace at $trace -- skipped"
        continue
    }
    $collisions = Join-Path $runDir 'bootstrap.log'
    Write-Host ("{0}: {1} [{2}]" -f $s.Name, $trace, $s.Note)
    & $Harness --trace $trace --collisions $collisions `
               --decisions (Join-Path $Out "dec-$($s.Name).txt") *> (Join-Path $Out "rec-$($s.Name).txt")
    Write-Host ("    exit {0}" -f $LASTEXITCODE)
}
Write-Host "captured into $Out" -ForegroundColor Green
exit 0
