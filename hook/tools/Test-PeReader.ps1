# Test-PeReader.ps1 -- exercise the native PE/MVID reader against real files.
#
# WHY THIS EXISTS
# ---------------
# `DrPeReadMvid` reported "PE read failed at DrelayPe.cpp:181" against the game and
# returned a zero MVID, while the same assembly read fine through the runtime.  The
# synthetic host that the injection test uses is PE32, so that test cannot exercise
# the PE32+ path at all -- the branch that was wrong.
#
# This test calls the reader directly, in-process, against every managed assembly
# it is given, and checks the answer against the file's own metadata read by an
# independent route (`il_index.py`'s offsets are not reused here on purpose: the
# point is a second opinion, not the same code twice).
[CmdletBinding()]
param(
    [string] $HookDll = (Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) 'out\Drelay.Hook.dll'),
    [string] $GamePath = "E:\SteamLibrary\steamapps\common\Darza's Dominion"
)

$ErrorActionPreference = 'Stop'
$failed = 0

function Check {
    param([string] $Name, [bool] $Ok, [string] $Detail = '')
    $colour = if ($Ok) { 'Green' } else { 'Red' }
    $word = if ($Ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("  {0,-52} {1}  {2}" -f $Name, $word, $Detail) -ForegroundColor $colour
    if (-not $Ok) { $script:failed++ }
}

# The reader is exported so it can be checked without the game running.  The path
# is escaped for C#: a literal backslash in a DllImport string is an unrecognised
# escape sequence, and the compiler error names the character rather than the fact.
$HookDllLiteral = $HookDll.Replace('\', '\\')
Add-Type -Namespace PeTest -Name Reader -MemberDefinition @"
    [System.Runtime.InteropServices.DllImport("$HookDllLiteral", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    public static extern int DrelayHook_PeReadMvid([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string path, byte[] mvidOut);
"@ -ErrorAction Stop

function Read-MvidNative {
    param([string] $Path)
    $buffer = New-Object byte[] 16
    $rc = [PeTest.Reader]::DrelayHook_PeReadMvid($Path, $buffer)
    return @{ Rc = $rc; Bytes = $buffer }
}

function Format-Mvid {
    param([byte[]] $Bytes)
    # The CLI stores a GUID with the first three components little-endian.  The
    # bootstrap's string is the canonical form, which is what this compares to.
    $g = New-Object System.Guid -ArgumentList @(,$Bytes)
    return $g.ToString('D')
}

# An independent reader: walk the metadata by hand in PowerShell.  Deliberately
# written from the spec rather than ported from the C++, so agreement means
# something.
function Read-MvidManaged {
    param([string] $Path)
    $b = [System.IO.File]::ReadAllBytes($Path)
    $e = [BitConverter]::ToInt32($b, 0x3C)
    $opt = $e + 4 + 20
    $magic = [BitConverter]::ToUInt16($b, $opt)
    $soh = [BitConverter]::ToUInt16($b, $e + 20)
    $nsec = [BitConverter]::ToUInt16($b, $e + 6)
    $dd = if ($magic -eq 0x20B) { $opt + 112 } else { $opt + 96 }
    $comRva = [BitConverter]::ToUInt32($b, $dd + 14 * 8)
    $hdrEnd = $opt + $soh
    $cliAt = -1
    for ($i = 0; $i -lt $nsec; $i++) {
        $s = $hdrEnd + $i * 40
        $vs = [BitConverter]::ToUInt32($b, $s + 8)
        $va = [BitConverter]::ToUInt32($b, $s + 12)
        $rs = [BitConverter]::ToUInt32($b, $s + 16)
        $raw = [BitConverter]::ToUInt32($b, $s + 20)
        $span = [Math]::Max($vs, $rs)
        if ($comRva -ge $va -and $comRva -lt ($va + $span)) { $cliAt = $raw + ($comRva - $va); break }
    }
    if ($cliAt -lt 0) { throw "no section contains the COM directory" }
    $metaRva = [BitConverter]::ToUInt32($b, $cliAt + 8)
    $metaSize = [BitConverter]::ToUInt32($b, $cliAt + 12)
    $rootAt = -1
    for ($i = 0; $i -lt $nsec; $i++) {
        $s = $hdrEnd + $i * 40
        $vs = [BitConverter]::ToUInt32($b, $s + 8)
        $va = [BitConverter]::ToUInt32($b, $s + 12)
        $rs = [BitConverter]::ToUInt32($b, $s + 16)
        $raw = [BitConverter]::ToUInt32($b, $s + 20)
        $span = [Math]::Max($vs, $rs)
        if ($metaRva -ge $va -and $metaRva -lt ($va + $span)) { $rootAt = $raw + ($metaRva - $va); break }
    }
    if ($rootAt -lt 0) { throw "no section contains the metadata root" }
    $versionLen = [BitConverter]::ToUInt32($b, $rootAt + 12)
    $p = $rootAt + 16 + $versionLen
    $streamCount = [BitConverter]::ToUInt16($b, $p + 2)
    $p += 4
    $tablesAt = -1; $guidAt = -1; $guidSize = 0
    for ($i = 0; $i -lt $streamCount; $i++) {
        $off = [BitConverter]::ToUInt32($b, $p)
        $size = [BitConverter]::ToUInt32($b, $p + 4)
        $nameEnd = $p + 8
        while ($b[$nameEnd] -ne 0) { $nameEnd++ }
        $name = [System.Text.Encoding]::ASCII.GetString($b, $p + 8, $nameEnd - ($p + 8))
        $nameLen = $name.Length + 1
        $p += 8 + (($nameLen + 3) -band (-bnot 3))
        if ($name -eq '#~' -or $name -eq '#-') { $tablesAt = $rootAt + $off }
        if ($name -eq '#GUID') { $guidAt = $rootAt + $off; $guidSize = $size }
    }
    if ($tablesAt -lt 0) { throw "no #~ stream" }
    if ($guidAt -lt 0) { throw "no #GUID stream" }
    $heapSizes = $b[$tablesAt + 6]
    $valid = [BitConverter]::ToUInt64($b, $tablesAt + 8)
    $present = 0
    for ($bit = 0; $bit -lt 64; $bit++) { if ((($valid -shr $bit) -band 1) -eq 1) { $present++ } }
    $rowsAt = 24 + 4 * $present
    $wideString = ($heapSizes -band 0x01) -ne 0
    $wideGuid = ($heapSizes -band 0x02) -ne 0
    $nameAt = $tablesAt + $rowsAt + 2
    $mvidAt = $nameAt + $(if ($wideString) { 4 } else { 2 })
    $mvidIndex = if ($wideGuid) { [BitConverter]::ToUInt32($b, $mvidAt) } else { [BitConverter]::ToUInt16($b, $mvidAt) }
    if ($mvidIndex -eq 0) { throw "the Module row has no MVID index" }
    $g = $guidAt + ($mvidIndex - 1) * 16
    return $b[$g..($g + 15)]
}

Write-Host '=== native PE/MVID reader ===' -ForegroundColor Cyan
Write-Host ''
Write-Host "hook DLL : $HookDll"
Write-Host ''

if (-not (Test-Path -LiteralPath $HookDll)) {
    Write-Host "the hook DLL is missing. Run .\build.ps1 first." -ForegroundColor Red
    exit 2
}

$targets = [System.Collections.Generic.List[object]]::new()
function Add-Target {
    param([string] $Path, [string] $Expected, [string] $Label)
    if (Test-Path -LiteralPath $Path) {
        $targets.Add([pscustomobject]@{ Path = $Path; Expected = $Expected; Label = $Label })
    } else {
        Write-Host "  (skipped, not present) $Label : $Path" -ForegroundColor DarkGray
    }
}

# The game assembly is a PE32+ image with two sections -- the shape that exposed
# the bug.  Its MVID is pinned in known-build.json.
$known = Get-Content -LiteralPath (Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) 'known-build.json') -Raw | ConvertFrom-Json
$pinned = $known.mvid
if (-not $pinned) { $pinned = $known.assembly.mvid }
Add-Target -Path (Join-Path $GamePath 'DarzasDominion.dll') -Expected $pinned -Label 'game assembly (PE32+)'

# The synthetic host is PE32 -- the shape that always worked.  It must keep working.
$synthetic = Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) 'tests\SyntheticHost\bin\Release\SyntheticHost.dll'
$syntheticMvid = $null
if (Test-Path -LiteralPath $synthetic) { $syntheticMvid = (Format-Mvid (Read-MvidManaged $synthetic)) }
Add-Target -Path $synthetic -Expected $syntheticMvid -Label 'synthetic host (PE32)'

# The bootstrap DLL is also a managed image and is always present.
$bootstrap = Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) 'out\Drelay.Bootstrap.dll'
$bootstrapMvid = $null
if (Test-Path -LiteralPath $bootstrap) { $bootstrapMvid = (Format-Mvid (Read-MvidManaged $bootstrap)) }
Add-Target -Path $bootstrap -Expected $bootstrapMvid -Label 'Drelay.Bootstrap.dll'

Write-Host ''
foreach ($t in $targets) {
    $expected = if ($t.Expected) { $t.Expected.ToString().ToUpperInvariant() } else { $null }
    $managed = $null
    try { $managed = (Format-Mvid (Read-MvidManaged $t.Path)).ToUpperInvariant() } catch { }
    $result = Read-MvidNative -Path $t.Path
    $native = if ($result.Rc -ne 0) { Format-Mvid $result.Bytes } else { '(refused)' }
    if ($native -ne '(refused)') { $native = $native.ToUpperInvariant() }

    Write-Host ("  {0}  [{1}]" -f $t.Label, (Split-Path -Leaf $t.Path)) -ForegroundColor Cyan
    Check '  the native reader returned a value' ($result.Rc -ne 0)
    if ($managed) { Check '  the native value matches an independent reader' ($native -eq $managed) "native $native / independent $managed" }
    if ($expected -and $managed) { Check '  the independent reader matches the pinned build' ($managed -eq $expected) $expected }
    if ($expected -and -not $managed) { Check '  the native value matches the pinned build' ($native -eq $expected) $expected }
    Write-Host ''
}

Write-Host ''
if ($failed -gt 0) {
    Write-Host "PE READER TEST FAILED: $failed check(s)" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------
# The refusal path against a DELIBERATELY tampered copy.
#
# PHASE1-HANDOFF.md section 6 lists "the refusal path against a *deliberately*
# tampered install" as not verified.  The reason it matters is that a check which
# can only ever pass is not a check: `DrPeReadMvid` returning a value proves it can
# read, not that it can read *correctly*, and the equality test that consumes it is
# only exercised when the two values differ.
#
# So: copy a real managed image, overwrite the 16 MVID bytes in its metadata with a
# different GUID, and require the reader to report the new value.  That proves the
# reader is reading the MVID field rather than something adjacent that happens to
# have the right value in the untampered file.  Comparing every other byte of the
# two files proves the MVID is the ONLY difference, so a changed answer cannot be
# an artefact of the copy.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== the refusal path: a tampered copy must report the tampered MVID ===' -ForegroundColor Cyan
Write-Host ''

$sourceForTamper = $synthetic
if (-not (Test-Path -LiteralPath $sourceForTamper)) { $sourceForTamper = $bootstrap }
if (-not (Test-Path -LiteralPath $sourceForTamper)) {
    Write-Host '  (skipped: no managed image to copy)' -ForegroundColor DarkGray
} else {
    $tamper = Join-Path $env:TEMP 'drelay-mvid-tamper.dll'
    Copy-Item -LiteralPath $sourceForTamper -Destination $tamper -Force

    $bytes = [System.IO.File]::ReadAllBytes($tamper)
    $originalMvid = Read-MvidManaged $sourceForTamper
    $originalText = (Format-Mvid $originalMvid).ToUpperInvariant()

    # Locate the MVID by searching for the exact 16 bytes the untampered file has.
    # This is a second route to the same bytes -- no metadata walk -- so finding
    # them here also corroborates `Read-MvidManaged`'s walk.
    $needle = $originalMvid
    $hits = [System.Collections.Generic.List[int]]::new()
    for ($i = 0; $i -le $bytes.Length - 16; $i++) {
        $same = $true
        for ($j = 0; $j -lt 16; $j++) { if ($bytes[$i + $j] -ne $needle[$j]) { $same = $false; break } }
        if ($same) { $hits.Add($i) }
    }
    Check 'the MVID bytes appear in the image (an independent route to the same offset)' ($hits.Count -ge 1) (
        "$($hits.Count) occurrence(s)")

    if ($hits.Count -ge 1) {
        $at = $hits[0]
        for ($j = 0; $j -lt 16; $j++) { $bytes[$at + $j] = $bytes[$at + $j] -bxor 0xFF }
        [System.IO.File]::WriteAllBytes($tamper, $bytes)

        # The tamper must be the only difference, or the comparison below would be
        # measuring the copy rather than the edit.
        $original = [System.IO.File]::ReadAllBytes($sourceForTamper)
        $diff = 0
        for ($i = 0; $i -lt $original.Length; $i++) { if ($original[$i] -ne $bytes[$i]) { $diff++ } }
        Check 'the tamper changed exactly the 16 MVID bytes and nothing else' ($diff -eq 16) (
            "$diff byte(s) differ")

        $result = Read-MvidNative -Path $tamper
        $tamperedText = if ($result.Rc -ne 0) { (Format-Mvid $result.Bytes).ToUpperInvariant() } else { '(refused)' }
        Check 'the reader still returned a value for the tampered image' ($result.Rc -ne 0)
        Check 'the reader reports the TAMPERED MVID, not the original' (
            $tamperedText -ne $originalText) ("tampered $tamperedText vs original $originalText")
        Check 'the tampered value is the bitwise inverse of the original' (
            $tamperedText -eq (Format-Mvid ([byte[]]($needle | ForEach-Object { $_ -bxor 0xFF }))).ToUpperInvariant()) (
                $tamperedText)
    }

    Remove-Item -LiteralPath $tamper -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($failed -gt 0) {
    Write-Host "PE READER TEST FAILED: $failed check(s)" -ForegroundColor Red
    exit 1
}
Write-Host 'PE READER TEST OK' -ForegroundColor Green
exit 0
