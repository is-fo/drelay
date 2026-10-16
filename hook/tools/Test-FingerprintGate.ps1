# Prove the Phase 0 gate can fail at the METHOD level, not just the file level.
#
# The first version of Fingerprint.ps1 checked the assembly's *size*, and a mutation test
# found the hole: flipping one byte left every check passing. The SHA-256 closed that. This
# test closes the next one down: a single-byte edit inside a target's IL, with the file
# length unchanged, must make the gate name that target's IL hash and exit non-zero.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$recordPath = Join-Path $root 'known-build.json'
$known = Get-Content $recordPath -Raw | ConvertFrom-Json

$gamePath = $known.recordedFrom
$assembly = Join-Path $gamePath $known.assembly.file
$sandbox = Join-Path $env:TEMP ("drelay_gate_test_{0}" -f $PID)

Write-Output "Gate mutation test"
Write-Output "  source   : $assembly"
Write-Output "  sandbox  : $sandbox"
Write-Output ''

New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
try {
    # The gate needs the assembly, its runtimeconfig and the two runtime DLLs beside it.
    foreach ($f in @($known.assembly.file, 'DarzasDominion.runtimeconfig.json',
                     'coreclr.dll', 'clrjit.dll')) {
        $src = Join-Path $gamePath $f
        if (Test-Path $src) { Copy-Item $src (Join-Path $sandbox $f) -Force }
    }
    $mutated = Join-Path $sandbox $known.assembly.file

    $target = $known.targetMethods.collision
    $rva = [uint32]$target.rva

    # Map the RVA to a file offset the same way the gate does.
    $bytes = [System.IO.File]::ReadAllBytes($mutated)
    $pe = [int][BitConverter]::ToUInt32($bytes, 0x3C)
    $coff = $pe + 4
    $opt = $coff + 20
    $sizeOpt = [BitConverter]::ToUInt16($bytes, $coff + 16)
    $nsec = [BitConverter]::ToUInt16($bytes, $coff + 2)
    $sec0 = $opt + $sizeOpt
    $fileOff = -1
    for ($i = 0; $i -lt $nsec; $i++) {
        $s = $sec0 + $i * 40
        $va = [BitConverter]::ToUInt32($bytes, $s + 12)
        $vs = [BitConverter]::ToUInt32($bytes, $s + 8)
        $raw = [BitConverter]::ToUInt32($bytes, $s + 20)
        if ($rva -ge $va -and $rva -lt ($va + $vs)) { $fileOff = $rva - $va + $raw; break }
    }
    if ($fileOff -lt 0) { throw "RVA 0x$($rva.ToString('X')) is not mapped" }

    $headerSize = ([BitConverter]::ToUInt16($bytes, $fileOff) -shr 12) * 4
    $flipAt = $fileOff + $headerSize + [int]($target.ilSize / 2)
    $original = $bytes[$flipAt]
    $bytes[$flipAt] = $original -bxor 0x01
    [System.IO.File]::WriteAllBytes($mutated, $bytes)

    Write-Output ("  flipped byte {0:X2} -> {1:X2} at file offset 0x{2:X}, inside {3}::{4} IL" -f `
                  $original, ($original -bxor 1), $flipAt,
                  $target.declaringType, $target.name)
    Write-Output ("  file size unchanged: {0}" -f `
                  ((Get-Item $mutated).Length -eq $known.assembly.size))
    Write-Output ''

    $output = & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $here 'Fingerprint.ps1') -GamePath $sandbox 2>&1
    $code = $LASTEXITCODE
    $output | Where-Object { $_ -match 'target ' -or $_ -match 'FINGERPRINT' -or $_ -match 'SHA-256' } |
        ForEach-Object { Write-Output ("    " + $_) }
    Write-Output ''
    if ($code -ne 0) {
        Write-Output "GATE MUTATION TEST PASSED -- the gate refused the mutated build (exit $code)"
        exit 0
    }
    Write-Output 'GATE MUTATION TEST FAILED -- the gate accepted a build with mutated target IL'
    exit 1
} finally {
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}
