import re
import sys

p = sys.argv[1] if len(sys.argv) > 1 else r"E:\SteamLibrary\steamapps\common\Darza's Dominion\DarzasDominion.exe"
b = open(p, 'rb').read()
asc = set(re.findall(rb'[\x20-\x7e]{5,}', b))
u16 = set(m.decode('utf-16le', 'replace')
          for m in re.findall(rb'(?:[\x20-\x7e]\x00){4,}', b))
keys = ['apphost', 'hostfxr', 'hostpolicy', 'coreclr', 'DarzasDominion',
        '.exe', '.dll', 'OpenGL', 'steam', 'Steam', 'cmd', '/c', 'shell',
        'exec', 'launch', 'Launch', 'CreateProcess', 'argv', 'command',
        'Command', 'runtimeconfig', 'deps.json']
print("=== ASCII strings (filtered) ===")
for s in sorted(asc):
    t = s.decode('ascii', 'replace')
    if any(k in t for k in keys):
        print(" ", t[:220])
print()
print("=== UTF-16 strings (filtered) ===")
for t in sorted(u16):
    if any(k in t for k in keys):
        print(" ", t[:220])
