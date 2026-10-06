import re, sys
p = r"G:\coding\javamapp\drelay\autododgeresearch\il\client-5.8.2.il"
pat = re.compile(r"call\s+float32\s+'.*'::'(.*)'\(int32\)")
mul = re.compile(r"^\s*IL_[0-9a-f]+:\s*(mul|div|sub|add)\s*$")
cmpz = re.compile(r"^\s*IL_[0-9a-f]+:\s*(bgt|blt|ble|bge|beq|bne\.un|ceq|cgt|clt)")
n = 0
prev = None
with open(p, "r", encoding="utf-8", errors="replace") as fh:
    buf = []
    for line in fh:
        buf.append(line.rstrip("\n"))
        if len(buf) > 6:
            buf.pop(0)
        m = pat.search(line)
        if m and len(buf) >= 3:
            # look at the instruction BEFORE the call and 2 AFTER
            before = buf[-2].strip()
            n += 1
            if n <= 25:
                print("L%-9s before: %-70s after: %s" % ("?", before[:70], buf[-1].strip()[:60]))
print("total float32 hehe calls seen:", n)