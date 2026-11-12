"""Shared by text_extract.py / text_build.py / text_fit.py. Must stay in step with DW1Remake/Assets/Scripts/Formats/Tr.cs
(the fingerprint, the template a script text decodes to, the key normalisation)."""
import re

def fingerprint(s):
    """FNV-1a 64 over the UTF-16 code units of the string (low byte first), as Tr.Fingerprint."""
    h = 0xCBF29CE484222325
    for ch in s:
        u = ord(ch)
        for byte in (u & 0xFF, u >> 8):
            h ^= byte
            h = (h * 0x100000001B3) & 0xFFFFFFFFFFFFFFFF
    return '%016x' % h

_PAIR = {0x8140: ' ', 0x8141: ',', 0x8143: ',', 0x8142: '.', 0x8144: '.', 0x8145: '·', 0x8146: ':', 0x8147: ';', 0x8148: '?',
         0x8149: '!', 0x8151: '_', 0x815E: '/', 0x8160: '~', 0x8165: "'", 0x8166: "'", 0x8175: "'", 0x8168: '"', 0x8176: '"',
         0x8169: '(', 0x816A: ')', 0x817B: '+', 0x815C: '-', 0x817C: '-', 0x8193: '%', 0x8195: '&', 0x8196: '*', 0x8197: '@', 0x8181: '='}

def pair(w):
    """MapScripts.Pair: a Shift-JIS code -> the character our code keeps"""
    if 0x824F <= w <= 0x8258: return chr(ord('0') + w - 0x824F)
    if 0x8260 <= w <= 0x8279: return chr(ord('A') + w - 0x8260)
    if 0x8281 <= w <= 0x829A: return chr(ord('a') + w - 0x8281)
    return _PAIR.get(w, chr(w))

def normalise(s):
    """Tr.Normalise: a character that holds a Shift-JIS code (0x8140-0x84FF) becomes what Pair makes of it"""
    return ''.join(pair(ord(c)) if 0x8140 <= ord(c) <= 0x84FF else c for c in s)

def decode(d, j, strict=False):
    """ScriptRunner.Decode with colours kept, to the template form (codes as {XX:YY} tokens, colours as {cN}).
    Returns (template, end) or None (strict: the bytes are not a clean text)."""
    out = []; after = False; hidden = False; start = j
    while j < len(d):
        if j - start > 4000: return None
        c = d[j]; was = after; after = False
        if 0x81 <= c <= 0x9F or 0xE0 <= c <= 0xFC:
            if j + 1 >= len(d): break
            if not hidden: out.append(pair(c << 8 | d[j + 1]))
            j += 2
        elif c == 0x0D: out.append('\n'); j += 2; after = True; hidden = False
        elif c == 0x00:
            j += 2
            if was: break
            hidden = True
        elif hidden: j += 2 if c < 0x20 else 1
        elif c == 0x01:
            if j + 1 >= len(d): return None
            out.append('{c%d}' % (d[j + 1] & 15)); j += 2
        elif c < 0x20:
            if j + 1 >= len(d): return None
            if strict and c not in (0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x10, 0x11, 0x12): return None
            out.append('{%02X:%02X}' % (c, d[j + 1])); j += 2
        else:
            if strict: return None
            j += 1
    else:
        if strict: return None
    return ''.join(out).strip('\n '), j

def mixed(raw):
    """an exe / overlay string with Shift-JIS pairs, read as Pair would (ExeSjis / StdString); keys are normalised anyway"""
    out = []; j = 0
    while j < len(raw):
        c = raw[j]
        if c >= 0x80 and j + 1 < len(raw): out.append(pair(c << 8 | raw[j + 1])); j += 2
        else: out.append(chr(c)); j += 1
    return ''.join(out)

def looks_like_text(s):
    """an exe / overlay string that reads as words (code bytes that happen to look like letters are left out)"""
    if any(not (0x20 <= ord(c) < 0x7F or c in '\n·') and not (0x8140 <= ord(c) <= 0x81FF) for c in s): return False
    letters = sum(1 for c in s if c.isascii() and c.isalpha())
    if letters < 2 or letters < 0.5 * len(s.replace(' ', '')): return False
    if '\\' in s or '%' in s or '_' in s: return False
    if re.fullmatch(r'[A-Za-z0-9_]+\.[A-Za-z0-9]{2,3}(;1)?', s): return False
    if len(s) < 4 and not re.fullmatch(r'[A-Z][a-zA-Z]+|[A-Z]{2,3}', s): return False
    if re.search(r'[a-z][A-Z][a-z]', s) and ' ' not in s: return False
    if not re.search(r'[AEIOUYaeiouy]', s) and not re.fullmatch(r'[A-Z0-9 .:/-]+', s): return False
    return True

# Chinese characters in 0x8140-0x81FF that are not game-font codes (the shipped file keeps symbols as raw characters)
CJK_OK = set()

def to_tokens(en):
    """template -> work-file form: line breaks {br}, Shift-JIS symbols {jXXXX}, tabs {t}"""
    s = en.replace('\n', '{br}').replace('\t', '{t}')
    return ''.join('{j%04X}' % ord(c) if 0x8140 <= ord(c) <= 0x84FF else c for c in s)

def from_tokens(t):
    """work-file form -> the runtime string (the Chinese keeps {cN} and {XX:YY} tokens: Tr expands them)"""
    t = t.replace('{br}', '\n').replace('{t}', '\t')
    return re.sub(r'\{j([0-9A-F]{4})\}', lambda m: chr(int(m.group(1), 16)), t)

TOKEN = re.compile(r'\{(?:c\d+|[0-9A-F]{2}:[0-9A-F]{2}|j[0-9A-F]{4})\}')
def tokens(t):
    """the tokens a line must keep (sorted), line breaks not counted"""
    return sorted(TOKEN.findall(t))

def plain(t):
    return TOKEN.sub(' ', t.replace('{br}', ' ').replace('\n', ' '))
