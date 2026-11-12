"""Every text source of the disc -> translation/zh-Hant.work.tsv (the working file; never shipped, never in git).

Usage:  py -3 tools/text_extract.py [dump folder]      (default D:/Digimon World 1/dump/)

Each row: id, kind, context, fingerprint, English, Chinese, notes. The English is written as the game's code makes it
(the same string the C# side builds), with control codes kept as tokens:
    {br}        line break (0D 00)
    {cN}        text colour N (01 N, setTextColor)
    {XX:YY}     a value the game fills in (05 the boy's name, 06 the partner's, 09 an item name...), code XX, argument YY
    {jXXXX}     a Shift-JIS symbol the game font draws (button marks)
The fingerprint (tr_common.fingerprint) is what the shipped file is keyed by. An existing work file keeps its Chinese and notes:
rows are matched by fingerprint, so running the extractor again never loses translations.

Script texts: every byte of every DG.SCN / MAPHEAD.SCN script is tried as the start of a text command (1A xx), and the text after
it is kept only when it decodes cleanly to its end (0D 00 00 00) with nothing but Shift-JIS pairs and control codes. This finds the
text in code reached only by jumps as well as the linear reads (it is a superset of following the jumps).
"""
import os, sys, struct, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import tr_common as tc

DUMP = sys.argv[1] if len(sys.argv) > 1 else 'D:/Digimon World 1/dump/'
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORK = os.path.join(ROOT, 'translation', 'zh-Hant.work.tsv')

def rd(p): return open(os.path.join(DUMP, p), 'rb').read()

exe = rd('SLUS_010.32')
LOAD = struct.unpack_from('<I', exe, 0x18)[0]
def ea(a): return a - LOAD + 0x800
def u32(b, o): return struct.unpack_from('<I', b, o)[0]

rows = collections.OrderedDict()   # fingerprint -> [kind, context, english]
counts = collections.Counter()

def add(kind, ctx, en):
    if en is None: return
    if kind not in ('DG', 'MAPHEAD'): en = en.strip(' ')     # Tr.Text looks plain texts up without the spaces round them
    if not en.strip(): return
    fp = tc.fingerprint(en)
    counts[kind + ' (all)'] += 1
    if fp in rows: return
    rows[fp] = [kind, ctx, en]
    counts[kind] += 1

# ---------- scripts ----------
def sections(s):
    r = []; o = 2
    while o + 4 <= len(s):
        i, v = struct.unpack_from('<HH', s, o)
        if i == 0xFFFF: break
        r.append((v, i)); o += 4
    r.sort()
    return r

def section_of(secs, off):
    best = None
    for v, i in secs:
        if v <= off: best = i
    return best

def context_box(s, start, p):
    """what kind of box this text goes in: 'sel' after 10 n offsets, 'box<cols>x<rows>' after 26 o c r, else 'dlg'"""
    for n in range(1, 9):
        q = p - 2 - 2 * n
        if q >= start and s[q] == 0x10 and s[q + 1] == n: return 'sel%d' % n
    # 26 o c r in the 24 bytes before (the command just before a text)
    for q in range(max(start, p - 24), p - 3):
        if s[q] == 0x26 and 0 < s[q + 2] <= 30 and 0 < s[q + 3] <= 12 and q + 4 <= p:
            # only if nothing that is a text start lies between
            return 'box%dx%d' % (s[q + 2], s[q + 3])
    return 'dlg'

def scan_script(kind, sid, s):
    secs = sections(s)
    p = 0; found = 0
    while p + 3 < len(s):
        if s[p] == 0x1A:
            r = tc.decode(s, p + 2, strict=True)
            if r is not None and r[0] and any(ch.isascii() and ch.isalpha() for ch in r[0]):
                t, end = r
                sec = section_of(secs, p)
                ctx = '%s %d sec %s @%04X %s' % (kind, sid, ('%02X' % sec) if sec is not None else '-', p, context_box(s, 0, p))
                add(kind, ctx, t); found += 1
                p = end; continue
        p += 1
    return found

dg = rd('SCN/DG.SCN')
T = struct.unpack_from('<2048I', dg, 0)
for i in range(2047):
    a, b = T[i], T[i + 1]
    if a == 0 or b <= a or b > len(dg): continue
    scan_script('DG', i, dg[a:b])

mh = rd('SCN/MAPHEAD.SCN')
scan_script('MAPHEAD', 0, mh)

# ---------- exe tables (read as the C# catalogs read them) ----------
def ascii_max(b, o, mx):
    n = 0
    while n < mx and 32 <= b[o + n] < 127: n += 1
    return b[o:o + n].decode('ascii')

def zstr(b, o):
    j = o
    while j < len(b) and b[j] != 0: j += 1
    return b[o:j].decode('latin-1')

for i in range(128):   # ItemCatalog: name[20], description pointers
    add('item', 'item %d' % i, ascii_max(exe, ea(0x801269DC) + 32 * i, 20))
    add('itemdesc', 'item %d desc' % i, zstr(exe, ea(u32(exe, ea(0x801279DC) + 4 * i))))
for i in range(121):   # technique names (pointer table)
    add('tech', 'tech %d' % i, zstr(exe, ea(u32(exe, ea(0x80126054) + 4 * i))))
for t in range(180):   # DIGIMON_DATA names
    n = ascii_max(exe, ea(0x8012CEB4) + 52 * t, 20)
    if n: add('digimon', 'digimon %d' % t, n)
for i in range(255):   # MAP_NAME_PTR [MAP_ENTRIES +15]
    idx = exe[ea(0x801292D4) + 16 * i + 15]
    p = u32(exe, ea(0x801291BC) + 4 * idx)
    if LOAD <= p < LOAD + len(exe):
        add('area', 'map %d' % i, zstr(exe, ea(p)).strip(' '))   # Tr looks a padded text up without its spaces
# the menu pages' label tables (pointers): tamer titles, PLAYER_VIEW_LABELS, the chart / card labels, STATUS_VIEW_LABELS, the Tech page
# labels, and the Triangle menu's "YearDay" (run-together labels the string scan's filter leaves out)
for table, n in ((0x80124918, 11), (0x80124800, 5), (0x80124814, 5), (0x801247B8, 8), (0x801247D8, 9)):
    for i in range(n):
        p = u32(exe, ea(table) + 4 * i)
        if LOAD <= p < LOAD + len(exe): add('exe', 'exe 0x%08X' % p, tc.mixed(exe[ea(p):ea(p) + len(zstr(exe, ea(p)))]))
add('exe', 'exe 0x80134298', zstr(exe, ea(0x80134298)))
for i in range(63):    # BGM_TRACK_NAMES
    p = u32(exe, ea(0x8012FEC8) + 4 * i)
    if LOAD <= p < LOAD + len(exe): add('song', 'song %d' % i, zstr(exe, ea(p)))

# ---------- every other string of the exe and the overlays ----------
def strings(b, base_name, min_len=2):
    """zero-ended strings that start after a 0 byte: ASCII, and ones with Shift-JIS pairs (read as Pair would)"""
    out = []
    o = 0
    while o < len(b):
        if b[o] == 0 or (o > 0 and b[o - 1] != 0) or o % 4: o += 1; continue
        j = o; ok = True; sjis = False
        while j < len(b) and b[j] != 0:
            c = b[j]
            if c >= 0x80:
                if j + 1 < len(b) and b[j + 1] != 0 and (0x81 <= c <= 0x9F or 0xE0 <= c <= 0xFC): sjis = True; j += 2; continue
                ok = False; break
            if c < 0x20 and c not in (0x0A,): ok = False; break
            j += 1
        if ok and j - o >= min_len:
            raw = b[o:j]
            s = tc.mixed(raw)
            if s is not None and tc.looks_like_text(s): out.append((o, s))
        o = j + 1
    return out

for o, s in strings(exe, 'exe'):
    add('exe', 'exe 0x%08X' % (o - 0x800 + LOAD), s)

OVL = ['TRN_REL.BIN', 'TRN2_REL.BIN', 'STD_REL.BIN', 'KAR_REL.BIN', 'FISH_REL.BIN', 'DGET_REL.BIN', 'EAB_REL.BIN', 'ENDI_REL.BIN',
       'BTL_REL.BIN', 'SHOP_REL.BIN', 'EVL_REL.BIN', 'MURD_REL.BIN', 'DOOA_REL.BIN', 'DOO2_REL.BIN', 'VS_REL.BIN']   # MOV_REL is the developers' object file: symbol names, no game text
for f in OVL:
    try: b = rd(f)
    except OSError: continue
    for o, s in strings(b, f):
        add('ovl', '%s +0x%X' % (f, o), s)

# ---------- labels: the pieces of packed menu strings ----------
# The menus write strings such as "Status  Tech" or "Disc.      Life  Vir. HPMPnight" into a text page and show slices of them; with
# Chinese on, each slice's English is looked up on its own (PsPage.Slice). Candidates: the pieces between runs of 2+ spaces, words joined
# without a space ("TechsetFinal", "HPMPnight"), single words and neighbouring word pairs.
import re as _re
JOINED = {'groggysleepy': ('groggy', 'sleepy'), 'poisconfstunflat': ('pois', 'conf', 'stun', 'flat')}     # labels run together with no split the rules find
VOCAB = set(w.lower() for fp, (k, c, e) in rows.items() if k == 'DG' for w in _re.findall(r'[A-Za-z]+', e))
def pieces(s):
    out = set()
    for chunk in _re.split(r' {2,}|[腀-蓿]', s):      # a button mark also ends a label ("Card List" + Cross + "Select")
        chunk = chunk.strip()
        if not chunk: continue
        parts = _re.sub(r'(?<=[a-z.])(?=[A-Z])|(?<=[A-Z]{2})(?=[a-z]{3})', '\0', chunk).split('\0')
        for p in parts:
            p = p.strip()
            if not p: continue
            out.add(p)
            if _re.fullmatch(r'[A-Z]{4}', p): out.update([p[:2], p[2:]])
            for w, parts2 in JOINED.items():
                if w in p: out.update(parts2)
            for w in _re.findall(r'[a-z]{9,}', p):      # two words run together ("groggysleepy"), split where both are words of the script
                for k in range(3, len(w) - 2):
                    if w[:k] in VOCAB and w[k:] in VOCAB: out.update([w[:k], w[k:]])
            ws = p.split(' ')
            for i in range(len(ws)):
                out.add(ws[i])
                if i + 1 < len(ws): out.add(ws[i] + ' ' + ws[i + 1])
        out.add(chunk)
    return [p for p in out if p != s and any(c.isalpha() for c in p) and len(p) >= 2]

for fp, (kind, ctx, en) in list(rows.items()):
    if kind not in ('exe', 'ovl') or '\n' in en: continue
    menu = kind == 'exe' and (0x801238A0 <= int(ctx.split()[1], 16) < 0x80123E20 or len(en.split()) <= 4)     # menu labels (game_menu.c pages, short exe labels)
    if not menu and not _re.search(r' {2,}|[a-z.][A-Z]|[A-Z]{2}[a-z]{3}|[腀-蓿]', en): continue     # a plain sentence is shown whole
    for p in pieces(tc.normalise(en)):
        add('label', 'piece of ' + ctx, p)

# ---------- merge with the existing work file ----------
old = {}
# the shipped file holds every translation too (fingerprint -> Chinese): a lost work file is rebuilt from it
SHIP = os.path.join(ROOT, 'DW1Remake', 'Assets', 'Resources', 'Translation', 'zh-Hant.txt')
if os.path.exists(SHIP):
    for line in open(SHIP, encoding='utf-8'):
        if line.startswith('#') or '\t' not in line: continue
        fp, zh = line.rstrip('\n').split('\t', 1)
        old[fp] = (''.join('{j%04X}' % ord(c) if 0x8140 <= ord(c) <= 0x81FF and c not in tc.CJK_OK else c for c in zh), '')
if os.path.exists(WORK):
    for line in open(WORK, encoding='utf-8'):
        c = line.rstrip('\n').split('\t')
        if len(c) >= 7 and c[0] != 'id' and (c[5] or c[3] not in old): old[c[3]] = (c[5], c[6])
os.makedirs(os.path.dirname(WORK), exist_ok=True)
with open(WORK, 'w', encoding='utf-8', newline='\n') as w:
    w.write('id\tkind\tcontext\tfingerprint\tenglish\tchinese\tnotes\n')
    for n, (fp, (kind, ctx, en)) in enumerate(rows.items()):
        zh, note = old.get(fp, ('', ''))
        w.write('%d\t%s\t%s\t%s\t%s\t%s\t%s\n' % (n, kind, ctx, fp, tc.to_tokens(en), zh, note))

words = collections.Counter()
for fp, (kind, ctx, en) in rows.items(): words[kind] += len(tc.plain(en).split())
print('distinct texts per source (all occurrences in brackets), words:')
for k in ['DG', 'MAPHEAD', 'item', 'itemdesc', 'tech', 'digimon', 'area', 'song', 'exe', 'ovl', 'label']:
    print('  %-9s %5d  (%5d)  %6d words' % (k, counts[k], counts[k + ' (all)'], words[k]))
print('  total     %5d          %6d words' % (len(rows), sum(words.values())))
print('kept translations from the old file:', sum(1 for fp in rows if old.get(fp, ('',))[0]))
print('->', WORK)
