# capture-window.ps1 -- grab one window's pixels, DirectX included.
#
# GDI's BitBlt returns black for a window whose client area is a swap chain, which is what
# a DirectX apphost renders into.  PrintWindow with PW_RENDERFULLCONTENT asks the window to
# render itself into the DC instead, which is the supported way to capture one.
param([string] $Title = "Darza's Dominion", [string] $Out = "window.png", [switch] $Focus)

Add-Type -AssemblyName System.Drawing

$sig = @'
using System;
using System.Runtime.InteropServices;
public static class Cap {
    [DllImport("user32.dll", SetLastError=true)] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
Add-Type -TypeDefinition $sig

$proc = Get-Process -Name DarzasDominion -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output "no game process"; exit 1 }
$h = $proc.MainWindowHandle
if ($h -eq 0) { Write-Output "no main window"; exit 1 }

if ($Focus) {
    [Cap]::ShowWindow($h, 9) | Out-Null      # SW_RESTORE
    [Cap]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 600
}

$r = New-Object Cap+RECT
if (-not [Cap]::GetWindowRect($h, [ref] $r)) { Write-Output "no rect"; exit 1 }
$w = $r.Right - $r.Left
$hgt = $r.Bottom - $r.Top
if ($w -le 0 -or $hgt -le 0) { Write-Output "empty rect"; exit 1 }

$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [Cap]::PrintWindow($h, $hdc, 2)         # PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output ("printwindow={0} {1}x{2} -> {3}" -f $ok, $w, $hgt, $Out)
