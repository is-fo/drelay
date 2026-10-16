$root = Join-Path $env:LOCALAPPDATA 'drelay\hook'
$targets = @{ frameTick = 1286; spawn = 2014; collision = 989; escape = 335 }
$rows = @()
foreach ($d in (Get-ChildItem $root -Directory | Sort-Object Name)) {
    $log = Join-Path $d.FullName 'hook.log'
    $sizes = Join-Path $d.FullName 'hook.log.sizes'
    if (-not (Test-Path $log)) { continue }
    $txt = Get-Content $log -Raw
    $pid_ = if ($txt -match 'watcher thread started \(pid (\d+)') { $Matches[1] } else { '?' }
    $seen = if ($txt -match 'closed after \d+ ms: (\d+) compilation') { $Matches[1] } else { '?' }
    $found = if ($txt -match '(\d+) of (\d+) target\(s\) patched') { "$($Matches[1])/$($Matches[2])" } else { '?' }
    $h = ''
    foreach ($k in $targets.Keys) {
        if (Test-Path $sizes) {
            if ((Get-Content $sizes -Raw) -match "(?m)^$($targets[$k]),") { $h += " $k=YES" } else { $h += " $k=no" }
        }
    }
    $rows += [pscustomobject]@{
        run = $d.Name
        gamepid = $pid_
        compiles = $seen
        patched = $found
        targetsizes = $h.Trim()
        lastwrite = (Get-ChildItem $d.FullName | Sort-Object LastWriteTime | Select-Object -Last 1).LastWriteTime.ToString('HH:mm:ss')
    }
}
$rows | Format-Table -AutoSize