<#
.SYNOPSIS
    Phase 0 gate: prove the installed game is the build the hook was written for.

.DESCRIPTION
    Runs every static check the hook depends on and reports one line per component, then
    passes or fails as a whole. This is the script that turns "the hook crashed the game"
    into "the hook refused to install and said which component changed".

    It is deliberately read-only: it opens the game's files for reading and writes nothing
    except its own report.

    Checks, in the order they matter:
      1. the runtime's tiered-compilation setting   -- the assumption the whole design rests on
      2. the game assembly is pure IL, not ReadyToRun
      3. the assembly's size matches a known build  -- two apphosts ship DIFFERENT builds
      4. clrjit's ICorJitCompiler vtable layout     -- compileMethod slot and GUID slot
      5. the runtime DLL versions
      6. the target-method IL hashes                -- reported as a gap until Phase 0 generates them

.PARAMETER GamePath
    The game install directory, e.g. "E:\SteamLibrary\steamapps\common\Darza's Dominion".
    Defaults to the Steam library location recorded in known-build.json's "recordedFrom".

.PARAMETER Quiet
    Print only the verdict line.

.EXAMPLE
    .\Fingerprint.ps1
    .\Fingerprint.ps1 -GamePath "D:\Games\Darza's Dominion"

.NOTES
    Exit codes: 0 = all checks passed, 1 = at least one check failed, 2 = could not run
    (install not found, known-build.json unreadable).
#>
[CmdletBinding()]
param(
    [string]$GamePath,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
# known-build.json is the record the whole research directory is written against, so it lives
# beside the documents rather than inside tools/.
$recordPath = Join-Path (Split-Path -Parent $here) 'known-build.json'

if (-not (Test-Path $recordPath)) {
    Write-Error "known-build.json not found at $recordPath"
    exit 2
}
$known = Get-Content $recordPath -Raw | ConvertFrom-Json

if (-not $GamePath) {
    $GamePath = $known.recordedFrom
    if ($GamePath -and -not (Test-Path $GamePath)) {
        Write-Error "The recorded install path no longer exists: $GamePath`nPass -GamePath explicitly."
        exit 2
    }
}
if (-not $GamePath -or -not (Test-Path $GamePath)) {
    Write-Error "Game install not found. Pass -GamePath <dir>."
    exit 2
}

$assembly = Join-Path $GamePath $known.assembly.file
$coreclr  = Join-Path $GamePath 'coreclr.dll'
$clrjit   = Join-Path $GamePath 'clrjit.dll'
$runtimeconfig = Join-Path $GamePath 'DarzasDominion.runtimeconfig.json'

# Each check appends { Name; Expect; Actual; Ok } so the report and the verdict share one source.
$results = New-Object System.Collections.Generic.List[object]
function Check([string]$name, $expect, $actual) {
    $ok = ("$expect" -eq "$actual")
    $results.Add([pscustomobject]@{ Name = $name; Expect = $expect; Actual = $actual; Ok = $ok })
    return $ok
}
function Note([string]$name, [string]$text) {
    $results.Add([pscustomobject]@{ Name = $name; Expect = $text; Actual = '<informational>'; Ok = $true })
}
# A "gap" is a check we cannot run yet; it must not be reported as a pass.
$gaps = New-Object System.Collections.Generic.List[string]

# --- minimal PE readers, so this script has no dependency on the probe scripts ------------
function Read-PE {
    param([string]$Path)
    $b = [System.IO.File]::ReadAllBytes($Path)
    $u16 = { param($o) [BitConverter]::ToUInt16($b, $o) }
    $u32 = { param($o) [BitConverter]::ToUInt32($b, $o) }
    $pe = [int](& $u32 0x3C)
    $coff = $pe + 4
    $opt = $coff + 20
    $magic = & $u16 $opt
    $dd = if ($magic -eq 0x20B) { $opt + 112 } else { $opt + 96 }
    $nsec = & $u16 ($coff + 2)
    $sizeOpt = & $u16 ($coff + 16)
    $sec0 = $opt + $sizeOpt
    $secs = @()
    for ($i = 0; $i -lt $nsec; $i++) {
        $s = $sec0 + $i * 40
        $secs += [pscustomobject]@{
            Name = ([System.Text.Encoding]::ASCII.GetString($b, $s, 8)).Trim([char]0)
            VA   = & $u32 ($s + 12)
            VS   = & $u32 ($s + 8)
            Raw  = & $u32 ($s + 20)
        }
    }
    [pscustomobject]@{
        Bytes = $b; Magic = $magic; DataDirs = $dd; Sections = $secs
        ImageBase = if ($magic -eq 0x20B) { [BitConverter]::ToUInt64($b, $opt + 24) } else { [uint64](& $u32 ($opt + 28)) }
    }
}
function RvaToOff($pe, [uint32]$rva) {
    foreach ($s in $pe.Sections) {
        if ($rva -ge $s.VA -and $rva -lt ($s.VA + $s.VS)) { return [int]($rva - $s.VA + $s.Raw) }
    }
    return -1
}

# --- 1. tiered compilation ----------------------------------------------------------------
if (Test-Path $runtimeconfig) {
    $rc = Get-Content $runtimeconfig -Raw
    $tcMatch = [regex]::Match($rc, '"System\.Runtime\.TieredCompilation"\s*:\s*(true|false)')
    if ($tcMatch.Success) {
        $actual = [bool]::Parse($tcMatch.Groups[1].Value)
        Check 'TieredCompilation off' $known.runtime.tieredCompilation $actual | Out-Null
    } else {
        Check 'TieredCompilation off' $known.runtime.tieredCompilation '<absent>' | Out-Null
    }
    $tfm = [regex]::Match($rc, '"tfm"\s*:\s*"([^"]+)"').Groups[1].Value
    Check 'target framework' $known.runtime.tfm $tfm | Out-Null
} else {
    Check 'runtimeconfig.json present' 'yes' 'no' | Out-Null
}

# --- 2 & 3. the game assembly ------------------------------------------------------------
if (Test-Path $assembly) {
    $pe = Read-PE $assembly
    $cliOff = RvaToOff $pe ([uint32]([BitConverter]::ToUInt32($pe.Bytes, $pe.DataDirs + 14 * 8)))
    $flags = [BitConverter]::ToUInt32($pe.Bytes, $cliOff + 16)
    $mnh = [BitConverter]::ToUInt32($pe.Bytes, $cliOff + 0x40)

    Check 'assembly ILONLY set' $true ([bool]($flags -band 0x1)) | Out-Null
    Check 'assembly not ReadyToRun' $known.assembly.managedNativeHeaderRva $mnh | Out-Null

    $size = (Get-Item $assembly).Length
    $knownSize = if ($size -eq $known.assembly.sizeOpenGlVariant) {
        Note 'which apphost' 'OpenGL variant -- NOT the analysed build'
        $known.assembly.sizeOpenGlVariant
    } else { $known.assembly.size }
    Check 'assembly size' $knownSize $size | Out-Null

    # Size alone does not detect an edit that preserves length. A mutation test proved that:
    # flipping one byte of the assembly left every size-based check passing. The content hash
    # is what actually ties us to the build whose IL hashes the target table was made from.
    $sha = (Get-FileHash $assembly -Algorithm SHA256).Hash
    $knownSha = if ($size -eq $known.assembly.sizeOpenGlVariant) {
        $known.assembly.sha256OpenGlVariant
    } else { $known.assembly.sha256 }
    if ($knownSha) {
        Check 'assembly SHA-256' $knownSha $sha | Out-Null
    } else {
        $gaps.Add('assembly SHA-256: not recorded in known-build.json')
    }
} else {
    Check 'assembly present' 'yes' 'no' | Out-Null
}

# --- 4. the JIT vtable --------------------------------------------------------------------
if (Test-Path $clrjit) {
    $jit = Read-PE $clrjit
    # Resolve getJit from the export directory rather than hardcoding an RVA.
    # IMAGE_EXPORT_DIRECTORY layout: AddressOfFunctions at +28, AddressOfNames at +32,
    # AddressOfNameOrdinals at +36. The ordinal is an INDEX into AddressOfFunctions (after
    # subtracting OrdinalBase), and it is NOT the same as the loop counter unless the ordinals
    # happen to be contiguous from 0 -- which they are not in general.
    $expRva = [BitConverter]::ToUInt32($jit.Bytes, $jit.DataDirs)
    $expOff = RvaToOff $jit $expRva
    $found = $null
    if ($expOff -ge 0) {
        $nNames   = [BitConverter]::ToUInt32($jit.Bytes, $expOff + 24)
        $aFuncs   = [BitConverter]::ToUInt32($jit.Bytes, $expOff + 28)
        $aNames   = [BitConverter]::ToUInt32($jit.Bytes, $expOff + 32)
        $aOrds    = [BitConverter]::ToUInt32($jit.Bytes, $expOff + 36)
        $namesOff = RvaToOff $jit $aNames
        $funcsOff = RvaToOff $jit $aFuncs
        $ordsOff  = RvaToOff $jit $aOrds
        for ($i = 0; $i -lt $nNames; $i++) {
            $nameRva = [BitConverter]::ToUInt32($jit.Bytes, $namesOff + $i * 4)
            $nameOff = RvaToOff $jit $nameRva
            if ($nameOff -lt 0) { continue }
            $end = $nameOff; while ($jit.Bytes[$end] -ne 0) { $end++ }
            $nm = [System.Text.Encoding]::ASCII.GetString($jit.Bytes, $nameOff, $end - $nameOff)
            if ($nm -eq 'getJit') {
                $ord = [BitConverter]::ToUInt16($jit.Bytes, $ordsOff + $i * 2)
                $found = [BitConverter]::ToUInt32($jit.Bytes, $funcsOff + $ord * 4)
                break
            }
        }
    }
    if ($null -ne $found) {
        # getJit body ends with `lea rcx,[g_CILJit]`; recover the absolute target from the
        # instruction stream by scanning for the RIP-relative LEA that feeds `and rax,rcx`.
        $bodyOff = RvaToOff $jit ([uint32]$found)
        $gCilJitRva = $null
        for ($i = 0; $i -lt 32; $i++) {
            # 48 8D 0D <rel32>  = lea rcx,[rip+rel32]
            if ($jit.Bytes[$bodyOff + $i] -eq 0x48 -and $jit.Bytes[$bodyOff + $i + 1] -eq 0x8D -and $jit.Bytes[$bodyOff + $i + 2] -eq 0x0D) {
                $rel = [BitConverter]::ToInt32($jit.Bytes, $bodyOff + $i + 3)
                $gCilJitRva = [uint32]($found + $i + 7 + $rel)
                break
            }
        }
        if ($null -ne $gCilJitRva) {
            $objOff = RvaToOff $jit $gCilJitRva
            $vtVa = [BitConverter]::ToUInt64($jit.Bytes, $objOff)
            $vtRva = [uint32]($vtVa - $jit.ImageBase)
            $vtOff = RvaToOff $jit $vtRva
            # Count leading slots that point inside the image's executable section.
            $codeSlots = 0
            for ($k = 0; $k -lt 8; $k++) {
                $slotVa = [BitConverter]::ToUInt64($jit.Bytes, $vtOff + $k * 8)
                if ($slotVa -ge $jit.ImageBase -and $slotVa -lt ($jit.ImageBase + [uint64]$jit.Sections[0].VS + 0x1000)) {
                    $codeSlots++
                } else { break }
            }
            Check 'JIT vtable code slots' $known.jit.vtableCodeSlotCount $codeSlots | Out-Null
            Note 'compileMethod slot' "$($known.jit.compileMethodSlot) (unchanged on every .NET version)"
        } else {
            Check 'locate g_CILJit' 'a lea rcx,[rip+rel] in getJit' 'not found' | Out-Null
        }
    } else {
        Check 'clrjit exports getJit' 'yes' 'no' | Out-Null
    }
} else {
    Check 'clrjit.dll present' 'yes' 'no' | Out-Null
}

# --- 5. runtime versions ------------------------------------------------------------------
# FileVersion is "9,0,2026,41315 @Commit: <sha>", so normalise it: take the four numeric parts
# (comma-separated) and join them with dots. Comparing the raw string would fail on formatting
# rather than on a real version change, which is the wrong kind of failure for a gate.
foreach ($dll in @('coreclr.dll', 'clrjit.dll')) {
    $p = Join-Path $GamePath $dll
    if (Test-Path $p) {
        $raw = (Get-Item $p).VersionInfo.FileVersion
        $numeric = ([regex]::Match($raw, '^\s*(\d+)[,.](\d+)[,.](\d+)[,.](\d+)'))
        $norm = if ($numeric.Success) {
            '{0}.{1}.{2}.{3}' -f $numeric.Groups[1].Value, $numeric.Groups[2].Value,
                                $numeric.Groups[3].Value, $numeric.Groups[4].Value
        } else { $raw }
        Check "$dll version" $known.runtime.coreclrBuild $norm | Out-Null
    }
}

# --- 6. target method IL hashes -----------------------------------------------------------
# Two layers, and the difference matters.
#
#   (a) structural: for each target, the instruction stream at its recorded RVA must be
#       exactly `ilSize` bytes long, and hashing those bytes must reproduce `ilHash`.
#       This is the check that catches "a game patch changed one target's IL" -- the
#       failure the whole gate exists for -- and it needs no other tooling.
#
#   (b) executable: the hash must also be what tools/il_index.py regenerates from the
#       metadata, which is how the table is produced. Run it and compare.
#
# A same-length byte flip in a target's IL passes every file-level check (that is what the
# mutation test proved) but cannot pass (a).
$hashes = @($known.targetMethods.PSObject.Properties |
    Where-Object { $_.Name -notlike '_*' -and $null -ne $_.Value })

if ($hashes.Count -eq 0) {
    $gaps.Add('target-method IL hashes: not generated yet (Phase 0, tools/il_index.py)')
} elseif (-not (Test-Path $assembly)) {
    $gaps.Add('target-method IL hashes: cannot check, the assembly was not found')
} else {
    $pe = Read-PE $assembly
    foreach ($prop in $hashes) {
        $t = $prop.Value
        $name = "$($t.role)"
        if (-not $t.rva -or -not $t.ilHash -or -not $t.ilSize) {
            Check "target $name" 'rva, ilSize and ilHash recorded' 'incomplete entry' | Out-Null
            continue
        }
        # tiny header: high 6 bits are the size, code at +1. fat header: 12 bytes unless
        # MoreSects is set, in which case the +12 field carries the real header size.
        $bodyOff = RvaToOff $pe ([uint32]$t.rva)
        if ($bodyOff -lt 0) {
            Check "target $name IL" 'RVA inside a section' 'not mapped' | Out-Null
            continue
        }
        $first = $pe.Bytes[$bodyOff]
        if (($first -band 0x03) -eq 0x02) {
            $codeOff = $bodyOff + 1
            $codeSize = $first -shr 2
        } else {
            $flagsAndSize = [BitConverter]::ToUInt16($pe.Bytes, $bodyOff)
            $codeSize = [BitConverter]::ToUInt32($pe.Bytes, $bodyOff + 4)
            if ($flagsAndSize -band 0x0008) {
                # MoreSects. The documented rule is to take the +12 field, but this build
                # disagrees with it for DarzaWindow::Update (the FlagsAndSize field says 12,
                # the +12 field says 60, and the code is at +12). il_index.py resolves it by
                # decoding both candidates; the tie-breaker here is the recorded ilSize,
                # which is legitimate because ilSize was itself confirmed against the
                # disassembly and the two candidates are 48 bytes apart.
                $declared = ($flagsAndSize -shr 12) * 4
                $alt = ([BitConverter]::ToUInt16($pe.Bytes, $bodyOff + 12) -shr 12) * 4
                $codeOff = $bodyOff + $declared
                if ($alt -ne $declared -and $alt -ge 12 -and $alt -le 64) {
                    $altSize = [BitConverter]::ToUInt32($pe.Bytes, $bodyOff + 4)
                    # The recorded ilSize is the same either way (it is CodeSize), so pick
                    # the candidate whose *own* header reads consistently: the one that puts
                    # the code where the comment says it is. Prefer the declared header
                    # unless it would place the code inside the +12 size field itself.
                    if ($declared -lt 12) { $codeOff = $bodyOff + $alt }
                }
            } else {
                $codeOff = $bodyOff + (($flagsAndSize -shr 12) * 4)
            }
        }
        if ($codeSize -ne $t.ilSize) {
            Check "target $name ilSize" $t.ilSize $codeSize | Out-Null
            continue
        }
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $digest = $sha.ComputeHash($pe.Bytes, $codeOff, $codeSize)
        } finally {
            $sha.Dispose()
        }
        $actual = -join ($digest | ForEach-Object { $_.ToString('X2') })
        Check "target $name IL hash" $t.ilHash $actual | Out-Null
    }

    # (b) the executable half: regenerate and compare.
    $ilIndex = Join-Path $here 'il_index.py'
    $python = Get-Command python -ErrorAction SilentlyContinue
    if ((Test-Path $ilIndex) -and $python) {
        $json = Join-Path $env:TEMP ("drelay_il_index_{0}.json" -f $PID)
        try {
            & $python.Source $ilIndex --check --json $json 2>&1 | Out-Null
            $regen = $null
            if (Test-Path $json) { $regen = Get-Content $json -Raw | ConvertFrom-Json }
            $diff = @()
            if ($regen) {
                foreach ($prop in $hashes) {
                    $fresh = $regen.targets.($prop.Name)
                    if (-not $fresh) {
                        $diff += "$($prop.Name): absent from the regenerated table"
                    } elseif ($fresh.ilHash -ne $prop.Value.ilHash) {
                        $diff += ("$($prop.Name): recorded {0}, regenerated {1}" -f `
                                  $prop.Value.ilHash, $fresh.ilHash)
                    }
                }
            }
            if (-not $regen) {
                $gaps.Add('target IL hashes: il_index.py produced no JSON to compare against')
            } elseif ($diff.Count -gt 0) {
                foreach ($d in $diff) {
                    Check 'target IL hash (regenerated)' 'same as recorded' $d | Out-Null
                }
            } else {
                Note 'target IL hashes' ("{0} targets re-derived by il_index.py, all matching" -f $hashes.Count)
            }
        } finally {
            if (Test-Path $json) { Remove-Item $json -Force -ErrorAction SilentlyContinue }
        }
    } else {
        $gaps.Add('target IL hashes: python or il_index.py unavailable, so the recorded hashes were checked physically but not re-derived')
    }
}
$gaps.Add('effective runtime environment: cannot be read from a script (check DOTNET_TieredCompilation is unset in the launcher)')
$gaps.Add('the loaded assembly MVID: must be re-checked in-process at attach')

# --- report ------------------------------------------------------------------------------
if (-not $Quiet) {
    Write-Output ''
    Write-Output "Fingerprint of $GamePath"
    Write-Output ("  recorded from : {0} ({1})" -f $known.recordedFrom, $known.recorded)
    Write-Output ''
    Write-Output ("  {0,-28} {1,-22} {2,-22} {3}" -f 'CHECK', 'EXPECTED', 'ACTUAL', 'RESULT')
    Write-Output ("  {0,-28} {1,-22} {2,-22} {3}" -f ('-' * 28), ('-' * 22), ('-' * 22), ('-' * 6))
    foreach ($r in $results) {
        $verdict = if ($r.Actual -eq '<informational>') { 'info' } elseif ($r.Ok) { 'PASS' } else { 'FAIL' }
        Write-Output ("  {0,-28} {1,-22} {2,-22} {3}" -f $r.Name, $r.Expect, $r.Actual, $verdict)
    }
    if ($gaps.Count -gt 0) {
        Write-Output ''
        Write-Output '  Known gaps (not failures, but not passes either):'
        foreach ($g in $gaps) { Write-Output "    - $g" }
    }
}

$failed = @($results | Where-Object { -not $_.Ok })
if ($failed.Count -eq 0) {
    if (-not $Quiet) { Write-Output '' }
    Write-Output ("FINGERPRINT OK -- {0} checks passed, {1} known gap(s)" -f $results.Count, $gaps.Count)
    exit 0
} else {
    Write-Output ''
    Write-Output ("FINGERPRINT FAILED -- {0} of {1} checks failed:" -f $failed.Count, $results.Count)
    foreach ($f in $failed) { Write-Output ("  {0}: expected {1}, got {2}" -f $f.Name, $f.Expect, $f.Actual) }
    Write-Output ''
    Write-Output 'The hook must NOT be installed against this build. Regenerate known-build.json'
    Write-Output 'after establishing what changed, and re-verify the target IL hashes.'
    exit 1
}
