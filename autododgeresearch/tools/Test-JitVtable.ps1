param([Parameter(Mandatory=$true)][string]$Path)

$bytes = [System.IO.File]::ReadAllBytes($Path)
function U16($o){ [BitConverter]::ToUInt16($bytes,$o) }
function U32($o){ [BitConverter]::ToUInt32($bytes,$o) }
function U64($o){ [BitConverter]::ToUInt64($bytes,$o) }

$e_lfanew = [int](U32 0x3C)
$sig = [System.Text.Encoding]::ASCII.GetString($bytes, $e_lfanew, 4)
$coff = $e_lfanew + 4
$machine = U16 $coff
$numSections = U16 ($coff + 2)
$sizeOpt = U16 ($coff + 16)
$opt = $coff + 20
$magic = U16 $opt
$imageBase = if ($magic -eq 0x20B) { U64 ($opt + 24) } else { [uint64](U32 ($opt + 28)) }
# SizeOfImage is the last field of the optional header's standard windows fields; it is what
# bounds "is this pointer inside the image", which is how the data slots past the four
# interface virtuals are told apart from code pointers.
$imageSize = if ($magic -eq 0x20B) { U32 ($opt + 0x38) } else { U32 ($opt + 0x38) }
$ddOff = if ($magic -eq 0x20B) { $opt + 112 } else { $opt + 96 }
$exportRva = U32 $ddOff
$secOff = $opt + $sizeOpt

$sections = @()
for ($i = 0; $i -lt $numSections; $i++) {
  $s = $secOff + ($i * 40)
  $sections += [pscustomobject]@{
    Name = ([System.Text.Encoding]::ASCII.GetString($bytes,$s,8)).Trim([char]0)
    VA   = U32 ($s + 12)
    VSize= U32 ($s + 8)
    Raw  = U32 ($s + 20)
  }
}

function RvaToOff([uint32]$rva) {
  foreach ($s in $sections) {
    if ($rva -ge $s.VA -and $rva -lt ($s.VA + $s.VSize)) { return [int]($rva - $s.VA + $s.Raw) }
  }
  return -1
}

# parse exports
$eo = RvaToOff $exportRva
$numNames = U32 ($eo + 24)
$addrFuncs = U32 ($eo + 28)
$addrNames = U32 ($eo + 32)
$addrOrds  = U32 ($eo + 36)
$getJitRva = $null
for ($i = 0; $i -lt $numNames; $i++) {
  $nRva = U32 ((RvaToOff $addrNames) + ($i*4))
  $nOff = RvaToOff $nRva
  $name = ""
  $k = $nOff
  while ($bytes[$k] -ne 0) { $name += [char]$bytes[$k]; $k++ }
  if ($name -eq 'getJit') {
    $ord = U16 ((RvaToOff $addrOrds) + ($i*2))
    $getJitRva = U32 ((RvaToOff $addrFuncs) + ($ord*4))
    break
  }
}

Write-Output "FILE        : $Path"
Write-Output ("Machine     : 0x{0:X4}  PE32+={1}" -f $machine, ($magic -eq 0x20B))
Write-Output ("ImageBase   : 0x{0:X}" -f $imageBase)
Write-Output ("getJit RVA  : 0x{0:X}" -f $getJitRva)

$foff = RvaToOff $getJitRva
Write-Output ("getJit bytes: " + (($bytes[$foff..($foff+47)] | ForEach-Object { $_.ToString('X2') }) -join ' '))

# find RIP-relative lea to the static object
$cands = @()
for ($j = 0; $j -lt 40; $j++) {
  if ($bytes[$foff+$j] -eq 0x48 -and $bytes[$foff+$j+1] -eq 0x8D) {
    $modrm = $bytes[$foff+$j+2]
    if (($modrm -band 0xC7) -eq 0x05) {
      $disp = [BitConverter]::ToInt32($bytes, $foff+$j+3)
      $nextRva = $getJitRva + $j + 7
      $targetRva = [int64]$nextRva + $disp
      $cands += [pscustomobject]@{ Reg = (($modrm -shr 3) -band 7); Disp = $disp; TargetRva = $targetRva }
    }
  }
}
Write-Output "SECTIONS:"
$sections | ForEach-Object { Write-Output ("  {0} VA=0x{1:X} VSize=0x{2:X} Raw=0x{3:X}" -f $_.Name,$_.VA,$_.VSize,$_.Raw) }
Write-Output ("CANDIDATES: {0}" -f $cands.Count)
foreach ($c in $cands) {
  Write-Output ("  cand reg{0} targetRva=0x{1:X} fileOff={2}" -f $c.Reg, $c.TargetRva, (RvaToOff ([uint32]$c.TargetRva)))
}
foreach ($c in $cands) {
  $tOff = RvaToOff ([uint32]$c.TargetRva)
  if ($tOff -lt 0) { continue }
  $vtVa = U64 $tOff
  $vtRva = [uint32]($vtVa - $imageBase)
  $vtOff = RvaToOff $vtRva
  if ($vtOff -lt 0) { Write-Output ("    (vptr 0x{0:X} -> rva 0x{1:X} unresolvable)" -f $vtVa,$vtRva); continue }
  Write-Output ("--- g_CILJit RVA 0x{0:X}  vptr=0x{1:X}  vtableRVA=0x{2:X}" -f $c.TargetRva, $vtVa, $vtRva)
  # The interface has four virtuals; the entries past them are ordinary data, not code
  # pointers, so anything outside the image is reported as "not code" rather than converted.
  for ($k = 0; $k -lt 6; $k++) {
    $slotVa = U64 ($vtOff + $k*8)
    if ($slotVa -lt $imageBase -or $slotVa -ge ($imageBase + $imageSize)) {
      Write-Output ("    slot[{0}] VA=0x{1:X}  (not a code pointer)" -f $k, $slotVa)
      continue
    }
    $slotRva = [uint32]($slotVa - $imageBase)
    $sec = ($sections | Where-Object { $slotRva -ge $_.VA -and $slotRva -lt ($_.VA + $_.VSize) } | Select-Object -First 1).Name
    Write-Output ("    slot[{0}] VA=0x{1:X}  rva=0x{2:X}  section={3}" -f $k, $slotVa, $slotRva, $sec)
  }
}
