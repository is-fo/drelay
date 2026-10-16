# drive-window.ps1 -- send keystrokes to a window that cannot be seen.
#
# WHY THIS EXISTS: the client's client-area renders black to BitBlt AND to PrintWindow with
# PW_RENDERFULLCONTENT, so its menus cannot be read from outside and cannot therefore be
# clicked deliberately.  But the hook can see the game's own state -- `World.Shared`
# becomes non-null the moment a zone loads, and the sensor reports it every 300 frames --
# so a blind keystroke has an observable outcome even though the UI it drives is invisible.
#
# Keyboard only, and only Enter/Space/Escape.  A blind MOUSE click at guessed coordinates is
# a different proposition: it can land on anything on the screen, including something
# destructive in a live account, and there is no way to check before it happens.
param([string] $Title = "Darza's Dominion", [string[]] $Keys = @("{ENTER}"))

Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Fg {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);

    // SETFOREGROUNDWINDOW IS REFUSED FOR A PROCESS THAT IS NOT ALREADY THE FOREGROUND ONE,
    // which is a deliberate Windows restriction and not a bug.  The supported workaround is
    // to attach this thread's input queue to the foreground window's, set the foreground
    // while they share one, and detach again.
    public static bool Force(IntPtr target) {
        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
        uint me = GetCurrentThreadId();
        bool attached = false;
        if (fgThread != 0 && fgThread != me) { attached = AttachThreadInput(fgThread, me, true); }
        ShowWindow(target, 9);
        BringWindowToTop(target);
        bool ok = SetForegroundWindow(target);
        if (attached) { AttachThreadInput(fgThread, me, false); }
        return ok && GetForegroundWindow() == target;
    }
}
'@

$proc = Get-Process -Name DarzasDominion -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output "no game process"; exit 1 }

$focused = [Fg]::Force($proc.MainWindowHandle)
Write-Output ("foreground is the game: {0}" -f $focused)
Start-Sleep -Milliseconds 800
if (-not $focused) { Write-Output "could not take the foreground; not sending keys blind"; exit 2 }

foreach ($k in $Keys) {
    [System.Windows.Forms.SendKeys]::SendWait($k)
    Write-Output ("sent {0}" -f $k)
    Start-Sleep -Milliseconds 1500
}
