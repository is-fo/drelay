<#
.SYNOPSIS
    Resolve a `Module+RVA` from a drelay crash report to the nearest symbol in
    the hook's linker map.

.DESCRIPTION
    A crash report from Drelay.Hook.dll names addresses as `Drelay.Hook.dll+0xRVA`
    and there is no debugger on this machine to type them into.  The linker map
    that `build.ps1` now emits is the substitute: it lists every public symbol
    with the address it was placed at, so the nearest symbol at or below an RVA
    names the function that faulted.

    This is a strictly weaker answer than a debugger's -- Release code is built
    with `/OPT:REF /OPT:ICF`, so a static function may have been folded into
    another or dropped entirely, and a static function has no public symbol at
    all.  What it does give is the enclosing function for the RVAs that matter,
    which is what turns "coreclr.dll+0xD828D" into "the reverse-P/Invoke
    transition, called from DrelayInvokeResolveCallback".

    RVAs are read either from the command line or, with -CrashFile, straight out
    of a `crash.txt`.

.EXAMPLE
    .\Resolve-HookRva.ps1 -Rva 0x1A45, 0x3987, 0x2C40

.EXAMPLE
    .\Resolve-HookRva.ps1 -CrashFile "$env:LOCALAPPDATA\drelay\hook\...\crash.txt"
#>
[CmdletBinding(DefaultParameterSetName = 'Rva')]
param(
    [Parameter(ParameterSetName = 'Rva', Mandatory = $true, Position = 0)]
    [uint32[]] $Rva,

    [Parameter(ParameterSetName = 'Crash', Mandatory = $true)]
    [string] $CrashFile,

    # Which module's RVAs to resolve.  Defaults to the hook's own map, because
    # that is the only module whose symbols exist locally.
    [string] $Map = (Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) 'obj\Drelay.Hook.map'),
    [string] $ModuleName = 'Drelay.Hook.dll'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Map)) {
    Write-Error "no map file at $Map -- run build.ps1, which emits it (link /MAP)."
    exit 1
}

if ($PSCmdlet.ParameterSetName -eq 'Crash') {
    $text = Get-Content -LiteralPath $CrashFile -Raw
    $pattern = [regex]::Escape($ModuleName) + '\+0x([0-9A-Fa-f]+)'
    $Rva = [regex]::Matches($text, $pattern) |
        ForEach-Object { [uint32]::Parse($_.Groups[1].Value, 'HexNumber') } |
        Sort-Object -Unique
    if (-not $Rva -or $Rva.Count -eq 0) {
        Write-Error "no $ModuleName+0x... addresses in $CrashFile"
        exit 1
    }
}

# The map lists each symbol twice -- once as `section:offset` and once as the
# absolute address it would have at the preferred load address.  The absolute
# form is the one that becomes an RVA, and it is identified by being exactly 16
# hex digits.
$symbols = [System.Collections.Generic.List[object]]::new()
# Parsed from a string rather than written as a hex literal: Windows PowerShell
# reads a hex literal that does not fit in Int32 as a *negative* Int32, so
# `0x180000000` arrives as -6442450944 and the subtraction below overflows.
$preferredBase = [uint64]::Parse('180000000', [System.Globalization.NumberStyles]::HexNumber)
foreach ($line in (Get-Content -LiteralPath $Map)) {
    $m = [regex]::Match($line, '^\s+([0-9A-Fa-f]{4}):([0-9A-Fa-f]{8})\s+(\S+)\s+([0-9A-Fa-f]{16})\s')
    if (-not $m.Success) { continue }
    $absolute = [uint64]::Parse($m.Groups[4].Value, [System.Globalization.NumberStyles]::HexNumber)
    # The map also carries an `<absolute>` group whose addresses are zero, so the
    # subtraction below would underflow and the cast to UInt32 would throw.  They
    # are absolute symbols with no place in the image, so they are skipped.
    if ($absolute -lt $preferredBase) { continue }
    # The preferred base is 0x180000000 for a DLL; the RVA is what is left.
    $symbols.Add([pscustomobject]@{
        Name = $m.Groups[3].Value
        Rva  = [uint32]($absolute - $preferredBase)
    })
}
$symbols.Sort([System.Comparison[object]] { param($a, $b) $a.Rva.CompareTo($b.Rva) })

Write-Host ("{0} symbols from {1}" -f $symbols.Count, (Split-Path -Leaf $Map)) -ForegroundColor DarkGray
Write-Host ''

foreach ($r in $Rva) {
    # The nearest symbol at or below the RVA.  Binary search rather than a scan
    # because the map has thousands of rows and this is run in a loop.
    $lo = 0; $hi = $symbols.Count - 1; $best = $null
    while ($lo -le $hi) {
        $mid = [int](($lo + $hi) / 2)
        if ($symbols[$mid].Rva -le $r) { $best = $symbols[$mid]; $lo = $mid + 1 } else { $hi = $mid - 1 }
    }
    if ($null -eq $best) {
        Write-Host ("{0}+0x{1:X}  <below the first symbol in the map>" -f $ModuleName, $r)
        continue
    }
    $delta = $r - $best.Rva
    # Undecorated is how a C++ name reads; the decorated form is kept alongside
    # because it is what the map and the PDB actually contain, and a name that
    # has been folded by /OPT:ICF only appears decorated.
    $clean = $best.Name
    if ($clean.StartsWith('?')) {
        $clean = ($clean -replace '^[?]', '') -replace '@@.*$', ''
    }
    Write-Host ("{0}+0x{1:X}  =  {2} + 0x{3:X}   [{4}]" -f $ModuleName, $r, $clean, $delta, $best.Name)
}
