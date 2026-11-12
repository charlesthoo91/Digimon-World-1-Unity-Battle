"""translation/zh-Hant.work.tsv -> the shipped files (docs/TRANSLATION-PLAN.md):
    DW1Remake/Assets/Resources/Translation/zh-Hant.txt          fingerprint <tab> Chinese   (no English)
    DW1Remake/Assets/Resources/Translation/zh-Hant-glyphs.bytes  the Chinese characters used, drawn from the Cubic 11 pixel font

Usage:  py -3.11 tools/text_build.py       (needs Pillow: py -3.11 -m pip install pillow, or PYTHONPATH to a folder that has it)

Checks: every translated line keeps the English line's codes ({cN}, {XX:YY}, {jXXXX}); a selection keeps its number of rows; no
line is made only of characters the game's own font uses as Shift-JIS codes (GameFont would draw the game's letters). A line that fails is left out
(the game shows the English) and listed.
The glyphs also cover the Chinese of our own texts (the C# files: OPTIONS page, notices).
"""
import os, re, sys, struct, glob
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import tr_common as tc

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORK = os.path.join(ROOT, 'translation', 'zh-Hant.work.tsv')
OUT = os.path.join(ROOT, 'DW1Remake', 'Assets', 'Resources', 'Translation')
FONT = os.path.join(ROOT, 'DW1Remake', 'Assets', 'Fonts', 'Cubic_11.ttf')
EXE = 'D:/Digimon World 1/dump/SLUS_010.32'

def is_cjk(c):
    o = ord(c)
    return (0x2E80 <= o < 0xE000) or (0xFF00 <= o <= 0xFFEF) or c in '\u2026\u2014' or (0x3000 <= o <= 0x303F)

def game_codes():
    """the 79 Shift-JIS codes of the game font (CHAR_TO_GLYPH_TABLE 0x801307E0)"""
    try: exe = open(EXE, 'rb').read()
    except OSError: return set()
    load = struct.unpack_from('<I', exe, 0x18)[0]
    o = 0x801307E0 - load + 0x800
    return set(struct.unpack_from('<79H', exe, o))

def main():
    rows = [l.rstrip('\n').split('\t') for l in open(WORK, encoding='utf-8')][1:]
    codes = game_codes()
    out = []; bad = []; chars = set()
    for r in rows:
        if len(r) < 7 or not r[5].strip(): continue
        _id, kind, ctx, fp, en, zh = r[:6]
        zh = zh.strip()
        if tc.tokens(en) != tc.tokens(zh): bad.append((_id, 'codes differ', ctx)); continue
        m = re.search(r'\bsel(\d)', ctx)
        if m and en.count('{br}') != zh.count('{br}'): bad.append((_id, 'selection rows differ', ctx)); continue
        # characters that are also game-font codes (良 0x826F, 色 0x8272...) are drawn as Chinese only in a text with other Chinese (GameFont.ChineseText)
        cj = [c for c in zh if is_cjk(c)]
        if cj and all(ord(c) in codes for c in cj): bad.append((_id, 'only characters that are game-font codes: ' + ''.join(cj), ctx)); continue
        runtime = re.sub(r'\{j([0-9A-F]{4})\}', lambda mm: chr(int(mm.group(1), 16)), zh)
        out.append('%s\t%s' % (fp, runtime))
        chars.update(c for c in zh if is_cjk(c))
    # our own texts (translation/ours.tsv: the English as the code has it, \n = line break), keyed the same way
    ours = os.path.join(ROOT, 'translation', 'ours.tsv')
    if os.path.exists(ours):
        for l in open(ours, encoding='utf-8-sig'):
            l = l.rstrip('\r\n')
            if not l or l.startswith('#') or l.startswith('english\t') or '\t' not in l: continue
            en, zh = l.split('\t', 1)
            en = en.replace('\\n', '\n'); zh = zh.replace('\\n', '{br}')
            if re.findall(r'\{\d+\}', en) != re.findall(r'\{\d+\}', zh.replace('{br}', '')) and sorted(re.findall(r'\{\d+\}', en)) != sorted(re.findall(r'\{\d+\}', zh)):
                bad.append(('ours', 'values differ', en)); continue
            out.append('%s\t%s' % (tc.fingerprint(en), zh))
            chars.update(c for c in zh if is_cjk(c))
    # our own Chinese texts in the C# files
    for p in glob.glob(os.path.join(ROOT, 'DW1Remake', 'Assets', 'Scripts', '**', '*.cs'), recursive=True):
        for lit in re.findall(r'"((?:[^"\\]|\\.)*)"', open(p, encoding='utf-8-sig').read()):
            chars.update(c for c in lit if is_cjk(c))
    chars = sorted(chars)
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, 'zh-Hant.txt'), 'w', encoding='utf-8', newline='\n') as w:
        w.write('# Digimon World 1 Remake: Traditional Chinese, keyed by the fingerprint of the English line (tools/text_build.py). No disc text here.\n')
        for line in sorted(out): w.write(line + '\n')
    glyphs(chars)
    print('lines shipped: %d of %d rows; characters: %d' % (len(out), len(rows), len(chars)))
    for b in bad: print('  left out: id %s: %s (%s)' % b)

def glyphs(chars):
    from PIL import Image, ImageFont, ImageDraw
    f = ImageFont.truetype(FONT, 12)
    blob = bytearray(b'DWG1' + struct.pack('<H', len(chars)))
    missing = []
    for c in chars:
        if f.getmask(c).getbbox() is None and c not in '\u3000': missing.append(c)
        im = Image.new('1', (16, 16), 0); d = ImageDraw.Draw(im); d.fontmode = '1'; d.text((0, 0), c, font=f, fill=1)
        blob += struct.pack('<HB', ord(c), 12)
        for y in range(1, 12):     # the font's 11 rows of ink sit at y 1-11 at this size
            bits = 0
            for x in range(12):
                if im.getpixel((x, y)): bits |= 0x8000 >> x
            blob += struct.pack('<H', bits)
    open(os.path.join(OUT, 'zh-Hant-glyphs.bytes'), 'wb').write(blob)
    if missing: print('  not in Cubic 11 (drawn blank):', ''.join(missing))

if __name__ == '__main__':
    main()
