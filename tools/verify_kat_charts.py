# -*- coding: utf-8 -*-
"""验证 v2 定标结果：逐曲逐难度统计太鼓蓝(kat)占比与跑酷空中占比、最长同色连打、BPM。"""
import json
import glob
import os
import collections

NAMES = {}


def longest_run(cols):
    best = cur = 0
    prev = None
    for c in cols:
        cur = cur + 1 if c == prev and prev is not None else 1
        prev = c
        best = max(best, cur)
    return best


def chart_stats(path, remap_cols):
    d = json.load(open(path, encoding='utf-8'))
    # hold（太鼓=连击条/跑酷=长条怪）本身就是断开同色连打的元素，统计口径按断开计
    best = cur = 0
    prev = None
    kat = total = 0
    for n in d['Notes']:
        if str(n.get('Type', 'tap')) != 'tap':
            prev = None
            cur = 0
            continue
        c = remap_cols(n)
        total += 1
        kat += c
        cur = cur + 1 if c == prev else 1
        prev = c
        best = max(best, cur)
    return d['Bpm'], kat, total, best


def main():
    rows = []
    for meta_path in sorted(glob.glob('Songs/song_*/meta.json')):
        sid = os.path.basename(os.path.dirname(meta_path))
        meta = json.load(open(meta_path, encoding='utf-8'))
        NAMES[sid] = meta.get('Title', sid)
        diffs = [x for x in ('easy', 'normal', 'hard', 'expert')
                 if os.path.isfile('Songs/%s/chart_%s.json' % (sid, x))]
        line = []
        for diff in diffs:
            path = 'Songs/%s/chart_%s.json' % (sid, diff)
            bpm, kat, total, run = chart_stats(path, lambda n, dd=diff: 1 if n.get('Kat') else 0)
            line.append('%s:%2.0f%%(r%d)' % (diff[0].upper(), kat / max(total, 1) * 100, run))
        bpms = set()
        for diff in diffs:
            bpms.add(round(json.load(open('Songs/%s/chart_%s.json' % (sid, diff), encoding='utf-8'))['Bpm'], 1))
        rows.append((NAMES[sid], line, sorted(bpms)))

    for name, line, bpms in rows:
        print('%-28s %s  BPM=%s' % (name[:28], ' '.join(line), ','.join(str(b) for b in bpms)))
    print()
    print('U/N/H/E = easy/normal/hard/expert 的太鼓蓝占比（连打段交替+45%闸门后预期 ~35-45%，easy 偏低）')


if __name__ == '__main__':
    main()
