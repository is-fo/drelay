<#
.SYNOPSIS
    Checks Drelay.Hook.dll's ABI in a throwaway process, without touching the game.

.DESCRIPTION
    The hook is injected into a suspended game process, which makes it awkward to
    test in isolation: a broken export, or a DllMain that does work, would show up
    as a game that fails to start -- a bad way to find out.

    So this loads the DLL into *this* PowerShell process and checks what can be
    checked without the .NET runtime being present:

      1. the DLL loads at all, so DllMain completed without doing anything fatal.
         DllMain was written to do nothing but DisableThreadLibraryCalls; if that
         ever changes, this is where it will be noticed.
      2. the three exported entry points resolve, found the way the launcher finds
         them (GetProcAddress on the loaded module).
      3. DrelayHook_AbiVersion returns the version the launcher was built against.
         This is the check that stops a stale DLL being injected by a new launcher,
         or the reverse.
      4. DrelayHook_Init *without* the launcher's session returns 0 rather than
         crashing. "Fail closed" is the hook's contract when it cannot find its
         shared block, and this is the only negative test that is safe in-process.

    Exit codes: 0 = all checks passed, 1 = a check failed, 2 = the DLL is missing.
#>
[CmdletBinding()]
param(
    [string] $HookDll,
    [uint32] $ExpectedAbi = 1
)

$ErrorActionPreference = 'Stop'

# The default is derived from the script's own location rather than from
# $PSScriptRoot: this script lives in a subdirectory, and when it is invoked in a
# way that does not populate that automatic variable the parameter default is
# evaluated against an empty string and throws before a single check runs --
# which is a confusing failure for a self-test to have.
if (-not $HookDll) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    if (-not $scriptDir) { $scriptDir = $PWD.Path }
    $HookDll = Join-Path (Split-Path -Parent $scriptDir) 'out\Drelay.Hook.dll'
}
$hookDll = [System.IO.Path]::GetFullPath($HookDll)

function Write-Check {
    param([string] $Name, [bool] $Ok, [string] $Detail = '')
    $word = if ($Ok) { 'PASS' } else { 'FAIL' }
    $colour = if ($Ok) { 'Green' } else { 'Red' }
    Write-Host ("  {0,-40} {1}  {2}" -f $Name, $word, $Detail) -ForegroundColor $colour
}

Write-Host "ABI self-test of $hookDll"
Write-Host ''

if (-not (Test-Path -LiteralPath $hookDll)) {
    Write-Host 'FAIL: the DLL does not exist. Build it with .\build.ps1 -Native' -ForegroundColor Red
    exit 2
}

# The P/Invoke surface, compiled once into this session.  Add-Type is used rather
# than a prebuilt assembly so this script stays a single file with no build step.
if (-not ('DrelayAbiProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class DrelayAbiProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);

    public static IntPtr Load(string path) { return LoadLibraryW(path); }

    public static IntPtr Resolve(IntPtr module, string name) { return GetProcAddress(module, name); }

    public static void Unload(IntPtr module) { if (module != IntPtr.Zero) FreeLibrary(module); }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint AbiVersionFn();
    public static uint CallAbiVersion(IntPtr fn)
    {
        return Marshal.GetDelegateForFunctionPointer<AbiVersionFn>(fn)();
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitFn();
    public static int CallInit(IntPtr fn)
    {
        return Marshal.GetDelegateForFunctionPointer<InitFn>(fn)();
    }
}
'@ -ErrorAction Stop
}

$failed = 0

# 1. Load.  LoadLibraryW directly rather than Add-Type -Path: Add-Type would also
#    work, but it inspects the assembly for managed types first and this is not a
#    managed assembly, so the error it produces when something is wrong is about
#    reflection rather than about loading.
$module = [DrelayAbiProbe]::Load($hookDll)
if ($module -eq [IntPtr]::Zero) {
    $code = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
    Write-Check 'DLL loads (DllMain is inert)' $false "LoadLibraryW failed, win32 error $code"
    Write-Host ''
    Write-Host 'ABI SELF-TEST FAILED' -ForegroundColor Red
    exit 1
}
Write-Check 'DLL loads (DllMain is inert)' $true ("module 0x{0:X}" -f $module.ToInt64())

try {
    # 2. Exports.
    $addresses = @{}
    foreach ($name in @('DrelayHook_AbiVersion', 'DrelayHook_Init', 'DrelayHook_Shutdown')) {
        $addr = [DrelayAbiProbe]::Resolve($module, $name)
        $addresses[$name] = $addr
        $ok = $addr -ne [IntPtr]::Zero
        if (-not $ok) { $failed++ }
        Write-Check "export $name" $ok ("0x{0:X}" -f $addr.ToInt64())
    }

    # 3. The ABI version, by calling the export for real.
    if ($addresses['DrelayHook_AbiVersion'] -ne [IntPtr]::Zero) {
        $version = [DrelayAbiProbe]::CallAbiVersion($addresses['DrelayHook_AbiVersion'])
        $ok = $version -eq $ExpectedAbi
        if (-not $ok) { $failed++ }
        Write-Check 'DrelayHook_AbiVersion' $ok "returned $version, expected $ExpectedAbi"
    }

    # 4. Init with no session must fail closed, not crash.
    if ($addresses['DrelayHook_Init'] -ne [IntPtr]::Zero) {
        $init = [DrelayAbiProbe]::CallInit($addresses['DrelayHook_Init'])
        $ok = $init -eq 0
        if (-not $ok) { $failed++ }
        Write-Check 'DrelayHook_Init without a session returns 0' $ok "returned $init"
    }
}
finally {
    [DrelayAbiProbe]::Unload($module)
}

Write-Host ''
if ($failed -gt 0) {
    Write-Host "ABI SELF-TEST FAILED ($failed check(s))" -ForegroundColor Red
    exit 1
}
Write-Host 'ABI SELF-TEST OK' -ForegroundColor Green
exit 0
