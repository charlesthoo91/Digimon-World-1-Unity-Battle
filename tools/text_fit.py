"""Measures every translated line against its box and lists the ones that do not fit (docs/TRANSLATION-PLAN.md section 4).

Usage:  py -3 tools/text_fit.py [kind ...]      (kinds: DG item tech digimon area exe ovl ...; default all)

Widths are the game's: a Chinese character 12 pixels, everything else the game font's own advance (GameFont, read from the exe).
Values the game fills in count as: the boy / partner name 6 letters, a Digimon / item / technique name its longest Chinese name,
numbers their digits. Dialogue (0x1A after 0x1B): rows 0xFC wide, at most 3 rows (the box's 4 minus the name), or the English
line's row count if that is more; DialogueBox breaks long Chinese rows by itself (Tr.Wrap) and this tool does the same, so the
check is the number of rows after breaking. A 0x26 box: cols x 12 wide, its rows. A selection: each option on one row.
Names and other short texts: no wider than the English (they sit in fixed places).
"""
import os, re, sys, struct
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import tr_common as tc
from text_build import is_cjk

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORK = os.path.join(ROOT, 'translation', 'zh-Hant.work.tsv')
exe = open('D:/Digimon World 1/dump/SLUS_010.32', 'rb').read()
LOAD = struct.unpack_from('<I', exe, 0x18)[0]
ea = lambda a: a - LOAD + 0x800
codes = struct.unpack_from('<79H', exe, ea(0x801307E0))
widths = [struct.unpack_from('<H', exe, ea(0x80130880) + g * 24 + 22)[0] for g in range(79)]
normal = struct.unpack_from('<6H', exe, ea(0x8012F39C)); special = struct.unpack_from('<66H', exe, ea(0x8012F3A8))

def to_jis(ch):
    a = ord(ch)
    if a >= 0x8000: return a
    if ch == '.': return 0x8142
    if ch == "'": return 0x8175
    if 0x30 <= a <= 0x39: return a + normal[0] - normal[1]
    if 0x41 <= a <= 0x5A: return a + normal[2] - normal[3]
    if 0x61 <= a <= 0x7A: return a + normal[4] - normal[5]
    if 0x20 <= a <= 0x2F: k = a - 0x20
    elif 0x3A <= a <= 0x40: k = a - 0x2A
    elif 0x5B <= a <= 0x60: k = a - 0x44
    elif 0x7B <= a <= 0x7E: k = a - 0x5E
    else: return 0
    return special[k * 2] if 0 <= k < 33 else 0

def cw(ch):
    if is_cjk(ch): return 12
    j = to_jis(ch)
    return widths[codes.index(j)] if j in codes[:78] else widths[78]

rows = [l.rstrip('\n').split('\t') for l in open(WORK, encoding='utf-8')][1:]
zh_of = {}
for r in rows:
    if len(r) > 5 and r[5]: zh_of[(r[1], r[2])] = r[5]
def longest(kind):
    return max([len(r[5]) for r in rows if r[1] == kind and r[5]] or [6]) * 12

FILL = {'05': 6 * 7, '06': 6 * 7, '04': 3 * 7, '0A': 6 * 7, '0B': 4 * 7, '10': 5 * 7}
def width(s):
    w = 0
    for m in re.finditer(r'\{c\d+\}|\{([0-9A-F]{2}):[0-9A-F]{2}\}|\{j[0-9A-F]{4}\}|.', s):
        t = m.group(0)
        if t.startswith('{c'): continue
        if t.startswith('{j'): w += 12; continue
        if m.group(1):
            c = m.group(1)
            w += FILL.get(c) or {'07': longest('digimon'), '08': longest('tech'), '09': longest('item'), '11': 20 * 12, '12': 10 * 12}.get(c, 6 * 7)
            continue
        w += cw(t)
    return w

def wrap(row, maxw):
    """Tr.Wrap: count rows after breaking"""
    if width(row) <= maxw or not any(is_cjk(c) for c in row): return 1
    n = 0; cur = ''
    for tok in re.findall(r'\{[^}]*\}|.', row):
        if width(cur + tok) > maxw and cur: n += 1; cur = tok
        else: cur += tok
    return n + 1

kinds = set(sys.argv[1:])
bad = 0
for r in rows:
    if len(r) < 6 or not r[5]: continue
    _id, kind, ctx, fp, en, zh = r[:6]
    if kinds and kind not in kinds: continue
    zr, er = zh.split('{br}'), en.split('{br}')
    msg = None
    if kind in ('DG', 'MAPHEAD'):
        m = re.search(r'box(\d+)x(\d+)', ctx); s = re.search(r'sel(\d)', ctx)
        if m:
            cols, nrows = int(m.group(1)), int(m.group(2))
            n = sum(wrap(x, cols * 12) for x in zr)
            if n > nrows: msg = '%d rows in a %d-row box' % (n, nrows)
        elif s:
            k = int(s.group(1)); q = zr[:-k] if k < len(zr) else []
            over = [x for x in zr[-k:] if width(x) > 0xF0]
            n = sum(wrap(x, 0xFC) for x in q) + k
            if over: msg = 'option wider than the cursor: ' + over[0]
            elif n > max(4, len(er)): msg = '%d rows with the options' % n
        else:
            n = sum(wrap(x, 0xFC) for x in zr)
            if n > max(3, len(er)): msg = '%d rows (box shows %d)' % (n, max(3, len(er)))
    else:
        ew = max(width(x) for x in er); zw = max(width(x) for x in zr)
        if zw > ew and zw > 12 * 2: msg = 'wider than the English (%d > %d px)' % (zw, ew)
    if msg:
        bad += 1
        print('%s\t%s\t%s\t%s' % (_id, ctx, msg, zh))
print('overflows:', bad, file=sys.stderr)
