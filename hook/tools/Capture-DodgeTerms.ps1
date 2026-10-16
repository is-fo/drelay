# ============================================================================
#  Capture-DodgeTerms.ps1 -- one recording replayed with `--frame N` for every
#  frame the dodge moved, so the score's own terms can be aggregated.
#
#  WHY A SCRIPT RATHER THAN ONE COMMAND. `--frame N` explains exactly one frame
#  and the decision is one frame at a time, so a term breakdown over a recording
#  is 1 600 invocations of the harness. Doing that by hand is not a thing anyone
#  does twice, and a measurement that will not be repeated is a measurement that
#  will not be re-run on the next change.
#
#  Usage:
#    .\hook\tools\Capture-DodgeTerms.ps1 -Recording 20261015-153805-p26156 -Out ..\work\terms-p26156
#
#  It reads the decision dump the harness already writes to find the frames that
#  moved, then asks for each of them in full.
# ============================================================================

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Recording,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Harness,
    [string]$Tag,
    [string[]]$FrameArgs = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not [System.IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $root $Out }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
if (-not $Harness) { $Harness = Join-Path $root 'tools\SolverHarness\bin\Release\SolverHarness.exe' }
if (-not $Tag) { $Tag = ($Recording -split '-')[-1] }

$runDir = Join-Path (Join-Path $env:LOCALAPPDATA 'drelay\hook') $Recording
$trace = Join-Path $runDir 'trace.jsonl'
if (-not (Test-Path -LiteralPath $trace)) { Write-Error "no trace at $trace"; exit 2 }

# The list of frames to explain, from the decision dump. `Move` is the only action
# whose score terms describe a direction; `stand` and the intent early-out do not
# go through the candidate score at all.
$dec = Join-Path $Out "dec-$Tag.txt"
& $Harness --trace $trace --decisions $dec @FrameArgs *> (Join-Path $Out "rec-$Tag.txt")
if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1) { Write-Error 'the decision pass failed'; exit 2 }

$frames = @()
foreach ($line in Get-Content -LiteralPath $dec) {
    if ($line.StartsWith('#') -or -not $line.Trim()) { continue }
    $parts = $line -split '\|'
    if ($parts.Count -lt 4) { continue }
    if ($parts[1].Trim() -ne 'Move') { continue }
    $frames += [int]$parts[0].Trim()
}
Write-Host "explaining $($frames.Count) moving frame(s) of $Tag" -ForegroundColor Cyan

$terms = Join-Path $Out "terms-$Tag.txt"
$list = Join-Path $Out "frames-$Tag.txt"
Set-Content -LiteralPath $list -Value ($frames -join "`r`n")
# ONE PASS, NOT ONE PROCESS PER FRAME. `--frames-from` exists for this: the alternative is 1 100 to
# 5 000 harness launches, each re-parsing the same trace, which is a measurement that never gets
# re-run on the next change.
& $Harness --trace $trace --frames-from $list @FrameArgs *> $terms

Write-Host "terms into $terms" -ForegroundColor Green
python (Join-Path $root 'tools\dodge_term_report.py') $runDir $terms
