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
$exp = U32 $dd
if ($exp -eq 0) { Write-Output "$([System.IO.Path]::GetFileName($Path)): no exports"; return }
$eo=R2O $exp
$nn=U32 ($eo+24); $af=U32 ($eo+28); $an=U32 ($eo+32); $ao=U32 ($eo+36)
Write-Output "== $([System.IO.Path]::GetFileName($Path))  ($nn exported names)"
$names=@()
for($i=0;$i -lt $nn;$i++){
  $nr=U32 ((R2O $an)+$i*4); $no=R2O $nr; $n=''; $k=$no
  while($b[$k] -ne 0){$n+=[char]$b[$k];$k++}
  $ord=U16 ((R2O $ao)+$i*2); $rva=U32 ((R2O $af)+$ord*4)
  $names += [pscustomobject]@{N=$n;RVA=$rva}
}
$want = @('getCLRRuntimeHost','coreclr_initialize','coreclr_create_delegate','coreclr_execute_assembly','CLRDataCreateInstance','CLRDataCreateInstance2','MiniDumpWriteDump','getJit','jitStartup','hostfxr_initialize_for_runtime_config','hostfxr_get_runtime_delegate','DllGetClassObject','CoreCLRCreateCppCliType','coreclr_shutdown')
foreach($w in $want){ $m = $names | Where-Object { $_.N -eq $w }; if($m){ foreach($mm in $m){ Write-Output ("   {0,-40} RVA=0x{1:X}" -f $mm.N,$mm.RVA) } } }
if ($names.Count -gt 0) { Write-Output ("   (first 12 exports: " + (($names | Select-Object -First 12 | ForEach-Object { $_.N }) -join ', ') + ")") }
