<#
.SYNOPSIS
    Dumps the CILJit vtable and names each slot's own resolving module.
.DESCRIPTION
    `compileMethod` is documented as slot 0 of the vtable returned by
    `clrjit!getJit`, and HOOK-ARCHITECTURE.md verifies the four-slot layout by
    disassembly.  What no tool here has done is ask Windows, at runtime, where
    each slot *points* and which module that address belongs to -- which is the
    question that matters when a detour on slot 0 is never entered.

    It resolves `getJit`, reads the vtable, and for each of the first eight slots
    reports the address, its module, and whether it lies outside clrjit (which
    slot would then be indirection rather than an implementation).

    Exit codes: 0 = dumped, 1 = could not resolve, 2 = the game directory is wrong.
#>
[CmdletBinding()]
param([string] $GamePath = "E:\SteamLibrary\steamapps\common\Darza's Dominion")

$ErrorActionPreference = 'Stop'
Write-Host "JIT vtable dump from $GamePath"
Write-Host ''

$clrjit = Join-Path $GamePath 'clrjit.dll'
if (-not (Test-Path -LiteralPath $clrjit)) {
    Write-Host "FAIL: $clrjit not found" -ForegroundColor Red
    exit 2
}

if (-not ('JitDump' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class JitDump
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetModuleHandle(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetModuleFileNameW(IntPtr module, System.Text.StringBuilder name, int size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr GetJitFn();

    public static IntPtr GetJitExport(IntPtr clrjit) { return GetProcAddress(clrjit, "getJit"); }

    public static IntPtr CallGetJit(IntPtr fn)
    {
        return Marshal.GetDelegateForFunctionPointer<GetJitFn>(fn)();
    }

    public static IntPtr VtableSlot(IntPtr jitObject, int index)
    {
        IntPtr vtable = Marshal.ReadIntPtr(jitObject, 0);
        return Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
    }

    /// <summary>The module that contains an address, as a file name, or empty.</summary>
    public static string ModuleOf(IntPtr address)
    {
        var buffer = new System.Text.StringBuilder(1024);
        // GetModuleHandleEx with FROM_ADDRESS and UNCHANGED_REFCOUNT.
        IntPtr module;
        if (!GetModuleHandleExW(0x00000004 | 0x00000002, address, out module))
        {
            return "";
        }
        return GetModuleFileNameW(module, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);
}
'@ -ErrorAction Stop
}

$module = [JitDump]::LoadLibraryW($clrjit)
if ($module -eq [IntPtr]::Zero) {
    Write-Host "FAIL: LoadLibraryW($clrjit) failed" -ForegroundColor Red
    exit 1
}
Write-Host ("clrjit.dll    : {0} (module 0x{1:X})" -f $clrjit, $module.ToInt64())

$getJit = [JitDump]::GetJitExport($module)
if ($getJit -eq [IntPtr]::Zero) {
    Write-Host 'FAIL: clrjit!getJit is not exported' -ForegroundColor Red
    exit 1
}
$jitObject = [JitDump]::CallGetJit($getJit)
Write-Host ("getJit        : 0x{0:X}" -f $getJit.ToInt64())
Write-Host ("CILJit object : 0x{0:X}" -f $jitObject.ToInt64())

$vtable = [System.Runtime.InteropServices.Marshal]::ReadIntPtr($jitObject, 0)
Write-Host ("vtable        : 0x{0:X}" -f $vtable.ToInt64())
Write-Host ''
Write-Host 'slot  address              module'
Write-Host '----  -------------------  ------------------------------------------'
for ($i = 0; $i -lt 8; $i++) {
    $slot = [JitDump]::VtableSlot($jitObject, $i)
    $where = if ($slot -eq [IntPtr]::Zero) { '<null>' } else { [JitDump]::ModuleOf($slot) }
    if ([string]::IsNullOrEmpty($where)) { $where = '<no module: not mapped>' }
    Write-Host ("{0,4}  0x{1:X16}  {2}" -f $i, $slot.ToInt64(), $where)
}
Write-Host ''
Write-Host 'Slot 0 is what the hook detours. If its module is not clrjit.dll, the'
Write-Host 'detour is being placed on indirection rather than on the JIT itself.'
