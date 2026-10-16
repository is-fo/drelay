#!/usr/bin/env python3
"""Minimal PE inspector: subsystem, COM descriptor, imported DLLs and functions.

Written to answer one question about DarzasDominion.exe: is it a thin apphost, a
managed assembly, or a native bootstrapper that launches something else?  The
answer decides whether the launcher's CreateProcess target is the process that
runs the frame loop.

Deliberately dependency-free: struct/byte reads only, so it runs on the stock
Python here without pip.
"""
import struct
import sys


def u16(b, o):
    return struct.unpack_from('<H', b, o)[0]


def u32(b, o):
    return struct.unpack_from('<I', b, o)[0]


def u64(b, o):
    return struct.unpack_from('<Q', b, o)[0]


def rva_to_off(sections, rva):
    for name, va, vsize, raw, rawsize in sections:
        if va <= rva < va + max(vsize, rawsize):
            return raw + (rva - va)
    return None


def cstr(b, o, limit=4096):
    end = b.find(b'\0', o, o + limit)
    if end < 0:
        end = o + limit
    return b[o:end].decode('ascii', 'replace')


def parse(path):
    with open(path, 'rb') as fh:
        b = fh.read()

    e_lfanew = u32(b, 0x3C)
    assert b[e_lfanew:e_lfanew + 4] == b'PE\0\0', 'not a PE'
    coff = e_lfanew + 4
    machine = u16(b, coff)
    nsec = u16(b, coff + 2)
    opt_size = u16(b, coff + 16)
    opt = coff + 20
    magic = u16(b, opt)
    pe32plus = magic == 0x20B

    subsystem = u16(b, opt + (68 if pe32plus else 68))
    dllchar = u16(b, opt + 70)
    numdir = u32(b, opt + (108 if pe32plus else 92))
    dirs = opt + (112 if pe32plus else 96)

    def dirva(i):
        if i >= numdir:
            return 0, 0
        return u32(b, dirs + i * 8), u32(b, dirs + i * 8 + 4)

    secs = []
    so = opt + opt_size
    for i in range(nsec):
        s = so + i * 40
        name = b[s:s + 8].rstrip(b'\0').decode('ascii', 'replace')
        vsize = u32(b, s + 8)
        va = u32(b, s + 12)
        rawsize = u32(b, s + 16)
        raw = u32(b, s + 20)
        secs.append((name, va, vsize, raw, rawsize))

    print('file        :', path)
    print('machine     : 0x%04X' % machine)
    print('magic       : 0x%04X (%s)' % (magic, 'PE32+' if pe32plus else 'PE32'))
    print('subsystem   : %d (%s)' % (
        subsystem,
        {2: 'GUI', 3: 'CONSOLE'}.get(subsystem, '?')))
    print('dllchar     : 0x%04X' % dllchar)
    print('sections    : %d  %s' % (nsec, [s[0] for s in secs]))
    print('numdirs     : %d' % numdir)

    implib_rva, implib_size = dirva(1)
    print('import dir  : rva 0x%X size %d' % (implib_rva, implib_size))
    com_rva, com_size = dirva(14)
    print('COM dir (#14): rva 0x%X size %d %s' % (
        com_rva, com_size, '<-- MANAGED' if com_rva else '<-- native only'))
    clr_rva, clr_size = dirva(15)
    print('CLR dir (#15): rva 0x%X size %d' % (clr_rva, clr_size))

    # Imported DLLs and their named functions.
    imports = {}
    if implib_rva:
        off = rva_to_off(secs, implib_rva)
        if off is not None:
            i = 0
            while True:
                ent = off + i * 20
                oft = u32(b, ent) or u32(b, ent + 12)
                name_rva = u32(b, ent + 12)
                if oft == 0 and name_rva == 0:
                    break
                dllname = cstr(b, rva_to_off(secs, name_rva)) if name_rva else '?'
                funcs = []
                t = rva_to_off(secs, oft)
                if t is not None:
                    k = 0
                    while True:
                        val = u64(b, t + k * 8)
                        if val == 0:
                            break
                        if not (val & (1 << 63)):
                            fname = cstr(b, rva_to_off(secs, val & 0x7FFFFFFF) + 2)
                            funcs.append(fname)
                        else:
                            funcs.append('ord#%d' % (val & 0xFFFF))
                        k += 1
                        if k > 4096:
                            break
                imports[dllname] = funcs
                i += 1
                if i > 256:
                    break

    print()
    print('=== imported DLLs (%d) ===' % len(imports))
    for dll in sorted(imports):
        print('  %-28s %4d funcs' % (dll, len(imports[dll])))

    interesting = ('creat', 'shell', 'winexec', 'spawn', 'hostfxr', 'coreclr',
                   'loadlibrary', 'getprocaddress', 'start', 'process', 'exec')
    print()
    print('=== interesting imports (process creation / runtime loading) ===')
    for dll in sorted(imports):
        hits = [f for f in imports[dll]
                if any(k in f.lower() for k in interesting)]
        if hits:
            print('  %s:' % dll)
            for h in sorted(set(hits)):
                print('      %s' % h)


if __name__ == '__main__':
    for p in sys.argv[1:]:
        try:
            parse(p)
        except Exception as exc:  # noqa: BLE001 - diagnostic tool
            print('FAILED %s: %s' % (p, exc))
        print()
