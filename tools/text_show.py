"""Prints rows of the work file for translating: id, box, English (untranslated rows only unless -a).
Usage: py -3 tools/text_show.py <kind> [first_script last_script] [-a]"""
import sys, re
a = sys.argv[1:]; allrows = '-a' in a; a = [x for x in a if x != '-a']
kind = a[0]; lo = int(a[1]) if len(a) > 1 else -1; hi = int(a[2]) if len(a) > 2 else 10**9
sys.stdout.reconfigure(encoding='utf-8')
for l in open('translation/zh-Hant.work.tsv', encoding='utf-8').read().split('\n')[1:]:
    r = l.split('\t')
    if len(r) < 6 or r[1] != kind: continue
    if kind in ('DG', 'MAPHEAD'):
        sid = int(r[2].split()[1])
        if not lo <= sid <= hi: continue
        box = r[2].split()[-1]; tag = '%d %s' % (sid, box if box != 'dlg' else '')
    else: tag = r[2]
    if r[5] and not allrows: continue
    print('%s|%s|%s' % (r[0], tag.strip(), r[4]))
