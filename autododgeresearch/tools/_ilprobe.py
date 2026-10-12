import re
import sys

p = sys.argv[1] if len(sys.argv) > 1 else r"E:\SteamLibrary\steamapps\common\Darza's Dominion\DarzasDominion.dll"
b = open(p, 'rb').read()
print("file size:", len(b))

keys = ['RestartAppIfNecessary', 'SteamAPI', 'Steamworks', 'InitSteam',
        'CreateProcess', 'ShellExecute', 'ProcessStartInfo', 'Process.Start',
        'StartProcess', 'LaunchProcess', 'Relaunch', 'relaunch', 'Restart',
        'restart', 'WinExec', 'apphost', 'hostfxr', 'applaunch',
        'RunningOnSteamDeck', 'steam_appid', 'SoloMode', 'EdgeMode']

# The #Strings metadata heap stores UTF-8 identifiers; a raw scan finds them.
strs = set(s.decode('utf-8', 'replace')
           for s in re.findall(rb'[\x20-\x7e]{4,}', b))
print("\n=== identifier-ish hits ===")
for k in keys:
    hits = sorted(s for s in strs if k in s)
    if hits:
        print("-- %s (%d)" % (k, len(hits)))
        for h in hits[:40]:
            print("   ", h[:160])
