"""Writes translations into translation/zh-Hant.work.tsv.

Usage:  py -3 tools/text_apply.py <file> [<file> ...]
Each line of a file: id <tab> Chinese (the work file's id column; {br} for line breaks, the English line's codes kept).
A line "id <tab> =other_id" copies the Chinese of another row. Lines starting with # are skipped.
"""
import os, sys
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORK = os.path.join(ROOT, 'translation', 'zh-Hant.work.tsv')
lines = open(WORK, encoding='utf-8').read().split('\n')
head, rows = lines[0], [l.split('\t') for l in lines[1:] if l]
by = {r[0]: r for r in rows}
n = 0; unknown = []
for path in sys.argv[1:]:
    for l in open(path, encoding='utf-8-sig'):
        l = l.rstrip('\r\n')
        if not l or l.startswith('#') or '\t' not in l: continue
        i, zh = l.split('\t', 1)
        i = i.strip()
        if i not in by: unknown.append(i); continue
        if zh.startswith('=') and zh[1:].strip() in by: zh = by[zh[1:].strip()][5]
        by[i][5] = zh.strip(); n += 1
with open(WORK, 'w', encoding='utf-8', newline='\n') as w:
    w.write(head + '\n')
    for r in rows: w.write('\t'.join(r) + '\n')
print('applied', n, 'unknown ids:', unknown[:20])
