# A/B one recording against one variant, and print the direction split.
#
# WHY A SCRIPT AND NOT SIX COMMANDS: each variant is two invocations (the harness, then the report)
# and the comparison is only meaningful when the invocations differ in exactly one thing. Writing
# them out by hand is how a before/after column ends up describing two different runs.
param(
    [string]$Recording = '20261015-153805-p26156',
    [string]$Tag = 'p26156',
    [string]$Harness,
    [string]$Out = 'work\ab',
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Variants = @('path=1', 'path=0')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not [System.IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $root $Out }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
if (-not $Harness) { $Harness = Join-Path $root 'hook\tools\SolverHarness\bin\Release\SolverHarness.exe' }

$runDir = Join-Path (Join-Path $env:LOCALAPPDATA 'drelay\hook') $Recording
foreach ($v in $Variants) {
    $safe = ($v -replace '[^A-Za-z0-9]', '_')
    # ONE DIRECTORY PER VARIANT, WITH THE DECISION DUMP UNDER ITS CANONICAL NAME. That is what
    # `dodge_direction_report.py` reads, and the alternative -- teaching the report about this
    # script's naming -- would be a second naming convention in the instrument that already has one.
    $dir = Join-Path $Out $safe
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $dec = Join-Path $dir "dec-$Tag.txt"
    & $Harness --trace (Join-Path $runDir 'trace.jsonl') --collisions (Join-Path $runDir 'bootstrap.log') `
               --config $v --decisions $dec *> (Join-Path $dir "rec-$Tag.txt")
    Write-Host "== config $v" -ForegroundColor Cyan
    python (Join-Path $root 'tools\dodge_direction_report.py') $dir 2>&1 |
        Select-String -Pattern "$Tag|OUTRUN|SIDESTEP|TOWARD" | ForEach-Object { $_.Line }
    Select-String -Path (Join-Path $dir "rec-$Tag.txt") -Pattern 'frames that moved|keeps its course|searches ' |
        ForEach-Object { '   ' + $_.Line.Trim() }
    Write-Host ''
}
