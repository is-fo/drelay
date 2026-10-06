param([Parameter(Mandatory=$true)][string]$Path)
$b = [System.IO.File]::ReadAllBytes($Path)
function U16($o){ [BitConverter]::ToUInt16($b,$o) }
function U32($o){ [BitConverter]::ToUInt32($b,$o) }
$e=[int](U32 0x3C); $coff=$e+4; $nsec=U16 ($coff+2); $so=U16 ($coff+16); $opt=$coff+20
$magic=U16 $opt
$dd = if ($magic -eq 0x20B) { $opt+112 } else { $opt+96 }
$sec0=$opt+$so
$secs=@(); for($i=0;$i -lt $nsec;$i++){$s=$sec0+$i*40; $secs+=[pscustomobject]@{Name=([System.Text.Encoding]::ASCII.GetString($b,$s,8)).Trim([char]0);VA=U32 ($s+12);VS=U32 ($s+8);Raw=U32 ($s+20)}}
function R2O([uint32]$r){foreach($s in $secs){if($r -ge $s.VA -and $r -lt ($s.VA+$s.VS)){return [int]($r-$s.VA+$s.Raw)}};return -1}
# directory 14 = CLR runtime header
$corRva = U32 ($dd + 14*8)
if ($corRva -eq 0) { Write-Output "$([System.IO.Path]::GetFileName($Path)): NOT a managed PE (no CLI header)"; return }
$co = R2O $corRva
$mnh = U32 ($co + 0x40)
$name = [System.IO.Path]::GetFileName($Path)
if ($mnh -eq 0) { Write-Output ("{0,-34} CLI header yes, ManagedNativeHeader=0  => IL-only (no ReadyToRun)" -f $name); return }
$mo = R2O $mnh
# The ReadyToRun header is an R2RHeader whose first field is a 4-byte magic. In little-endian
# on-disk order that magic is 52 54 52 00 ("RTR\0"), which decodes to the string 'RTR ' once the
# trailing NUL is trimmed -- comparing against 'RTR0' is wrong and silently reports "unknown".
$magic = if ($mo -ge 0) { U32 $mo } else { 0 }
$sig   = if ($mo -ge 0) { [System.Text.Encoding]::ASCII.GetString($b,$mo,4).Trim([char]0) } else { '??' }
$verdict = if ($magic -eq 0x00525452) { 'READY-TO-RUN (has AOT native code)' } else { 'unknown' }
Write-Output ("{0,-34} ManagedNativeHeader RVA=0x{1:X} magic=0x{2:X8} ('{3}')  => {4}" -f $name,$mnh,$magic,$sig,$verdict)
