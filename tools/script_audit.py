"""Script audit: walks every script on the disc (DG.SCN entries and MAPHEAD.SCN) by recursive descent from each section start,
following jumps, and counts each command, each built-in routine (0x64 n), Digimon routine (0x49 n), wait target (0x4A n)
and IF condition kind. Usage: py -3 tools/script_audit.py [dump folder] > report.txt
Reads the disc dump only; nothing from it is written into the project."""
import struct, sys, collections

D = sys.argv[1] if len(sys.argv) > 1 else 'D:/Digimon World 1/dump/'
dg = open(D + 'SCN/DG.SCN', 'rb').read()
mh = open(D + 'SCN/MAPHEAD.SCN', 'rb').read()
T = struct.unpack_from('<2048I', dg, 0)

# command lengths in bytes, from the decompilation's include/dw/script.h argument lists
L = {0x11: 2, 0x12: 2, 0x13: 4, 0x14: 6, 0x15: 2, 0x16: 4, 0x17: 6, 0x1B: 2, 0x1C: 4, 0x1D: 4, 0x1E: 4, 0x1F: 4, 0x20: 4,
     0x21: 2, 0x22: 2, 0x23: 2, 0x24: 4, 0x25: 2, 0x26: 4, 0x27: 2, 0x28: 4, 0x29: 4, 0x2A: 6, 0x2B: 6, 0x2C: 10, 0x2D: 2,
     0x2E: 2, 0x2F: 2, 0x30: 2, 0x31: 4, 0x32: 4, 0x33: 4, 0x34: 4, 0x35: 4, 0x36: 4, 0x37: 2, 0x38: 6, 0x39: 6, 0x3A: 4,
     0x3B: 4, 0x3C: 4, 0x3D: 2, 0x3E: 2, 0x3F: 4, 0x46: 2, 0x47: 4, 0x48: 2, 0x49: 2, 0x4A: 2, 0x4B: 4, 0x4C: 4, 0x4D: 4,
     0x4E: 8, 0x4F: 6, 0x50: 4, 0x51: 4, 0x52: 8, 0x53: 4, 0x54: 2, 0x55: 8, 0x56: 4, 0x57: 4, 0x58: 2, 0x5A: 4, 0x5B: 2,
     0x5C: 2, 0x5D: 2, 0x5E: 2, 0x5F: 2, 0x64: 2, 0x65: 2, 0x66: 2, 0x67: 4, 0x68: 4, 0x69: 2, 0x6A: 4, 0x6B: 2, 0x6C: 8,
     0x6D: 4, 0x6E: 8, 0x6F: 4, 0x70: 4, 0x71: 10, 0x72: 6, 0x73: 6, 0x74: 6, 0x75: 12, 0x76: 2, 0x77: 6, 0x78: 2, 0x79: 2,
     0x7A: 4, 0x7B: 2, 0x7C: 8, 0x7D: 10, 0x7E: 4, 0xFB: 6, 0xFC: 2, 0xFD: 2}

ops = collections.Counter(); where = collections.defaultdict(list)
builtin = collections.Counter(); routine = collections.Counter(); waits = collections.Counter(); conds = collections.Counter(); closes = collections.Counter(); textcodes = collections.Counter()
bad = []

def secs(s):
    r = []; o = 2
    while o + 4 <= len(s):
        i, v = struct.unpack_from('<HH', s, o)
        if i == 0xFFFF: break
        r.append((i, v)); o += 4
    return r

def text_end(s, j):
    while j < len(s):
        c = s[j]
        if 0x81 <= c <= 0x9F or 0xE0 <= c <= 0xFC: j += 2
        elif c == 0: return j + 2
        elif c < 0x20: textcodes[c] += 1; j += 2
        else: j += 1
    return j

def walk(name, s, start):
    todo = [start]; seen = set()
    while todo:
        p = todo.pop()
        while 0 <= p < len(s) and p not in seen:
            seen.add(p)
            op = s[p]
            ops[op] += 1
            if len(where[op]) < 6: where[op].append('%s@%d' % (name, p))
            if op in (0xFE, 0xFF): break
            if op == 0x1A: p = text_end(s, p + 2); continue
            if op == 0x10:
                n = s[p + 1]
                p += 2 + 2 * n; continue
            if op == 0x19:
                q = p + 2; g = 0
                while q + 1 < len(s) and g < 64:
                    c = s[q]; q += 2; g += 1
                    if c == 0x19: break
                    t = c & 0x38
                    if t in (16, 24):
                        todo.append(struct.unpack_from('<H', s, q)[0]); q += 2; continue
                    conds['type %d kind %d' % (t, c & 7) if t == 32 else 'type %d' % t] += 1
                    q += {0: 4, 1: 4, 2: 2, 3: 2, 4: 4, 5: 6, 6: 2, 7: 2}[c & 7] if t == 32 else 2
                p = q; continue
            if op == 0x18:
                n = struct.unpack_from('<H', s, p + 2)[0]
                for k in range(n): todo.append(struct.unpack_from('<H', s, p + 4 + 2 * k)[0])
                p += 4 + 2 * n; continue
            if op == 0x13: todo.append(struct.unpack_from('<H', s, p + 2)[0]); p += 4; continue
            if op == 0x16: todo.append(struct.unpack_from('<H', s, p + 2)[0]); break
            if op == 0x15: break
            if op == 0x64: builtin[s[p + 1]] += 1
            if op == 0x49: routine[s[p + 1]] += 1
            if op == 0x4A: waits[s[p + 1]] += 1
            if op == 0x27: closes[s[p + 1]] += 1
            if op == 0x6B:
                while p < len(s) and s[p] != 0xFF: p += 1
                p += 1; continue
            if op not in L:
                bad.append('%s@%d: unknown 0x%02X' % (name, p, op)); break
            p += L[op]

for i in range(512):      # past 511 the table holds no scripts (they decode as junk)
    a, b = T[i], T[i + 1]
    if not a or b <= a or b > len(dg): continue
    s = dg[a:b]
    for sec, off in secs(s):
        try: walk('DG%d.%X' % (i, sec), s, off)
        except struct.error: bad.append('DG%d.%X: ran off the end' % (i, sec))
for sec, off in secs(mh): walk('MH.%X' % sec, mh, off)

print('== commands (count, first places)')
for op in sorted(ops): print('0x%02X %6d  %s' % (op, ops[op], ' '.join(where[op][:3])))
print('== 0x64 built-ins'); print(' '.join('%02X:%d' % (k, builtin[k]) for k in sorted(builtin)))
print('== 0x49 routines'); print(' '.join('%02X:%d' % (k, routine[k]) for k in sorted(routine)))
print('== 0x4A waits'); print(' '.join('%02X:%d' % (k, waits[k]) for k in sorted(waits)))
print('== 0x27 boxes'); print(' '.join('%02X:%d' % (k, closes[k]) for k in sorted(closes)))
print('== text codes'); print(' '.join('%02X:%d' % (k, textcodes[k]) for k in sorted(textcodes)))
print('== IF conditions'); print(dict(conds))
print('== problems (%d)' % len(bad)); print('\n'.join(bad[:40]))
