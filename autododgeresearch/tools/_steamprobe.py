import re
import sys

p = sys.argv[1]
b = open(p, 'rb').read()
print('file:', p, len(b), 'bytes')
asc = sorted(set(s.decode('ascii', 'replace')
                 for s in re.findall(rb'[\x20-\x7e]{6,}', b)))
u16 = sorted(set(m.decode('utf-16le', 'replace')
                 for m in re.findall(rb'(?:[\x20-\x7e]\x00){6,}', b)))
keys = ['SteamClientLaunch', 'SteamGameId', 'SteamAppId', 'SteamPath',
        'restart', 'Restart', 'applaunch', 'steam.exe', 'AppId']
for label, coll in (('ASCII', asc), ('UTF-16', u16)):
    print('\n=== %s strings matching restart/launch/steam-id ===' % label)
    for t in coll:
        if any(k in t for k in keys):
            print('  ', t[:200])
