# -*- coding: utf-8 -*-
"""A/B：定色规则里频带条件值不值。
R1 = high带强制蓝 + 相对强度(<0.5x局部最强) + 40%上限
R2 = 纯相对强度 + 40%上限（完全不用频带）
R3 = 纯频带（当前已发布方案，对照）
看蓝占比 / 蓝强度中位 vs 红强度中位 / 蓝落反拍占比。
"""
import json
import sys
import numpy as np

sys.path.insert(0, 'tools')
from donkat_strategy_sim import SONGS, load


def decide(times, strg, band, bpm, offset, use_band=True, alpha=0.5, cap=0.40):
    n = len(band)
    local_max = np.zeros(n)
    win = 60.0 / bpm  # ±半拍
    for i in range(n):
        lo = np.searchsorted(times, times[i] - win / 2)
        hi = np.searchsorted(times, times[i] + win / 2)
        local_max[i] = strg[lo:hi].max()
    kat = strg < alpha * local_max
    if use_band:
        kat = kat | (band == 'high')
    max_kat = int(cap * n)
    if kat.sum() > max_kat:
        cand = np.where(kat)[0]
        by_str = cand[np.argsort(-strg[cand])]
        kat[by_str[:kat.sum() - max_kat]] = False
    return kat


def stats(kat, strg, times, bpm, offset):
    half = 30.0 / bpm
    phase = np.mod(times - offset, half) / half
    offbeat = np.minimum(phase, 1 - phase) * half > 0.045
    ks = np.median(strg[kat]) if kat.sum() else 0
    rs = np.median(strg[~kat]) if (~kat).sum() else 0
    off = (kat & offbeat).sum() / max(1, kat.sum()) * 100
    return kat.sum() / len(strg) * 100, ks, rs, off


def main():
    for sid, name in SONGS:
        d = json.load(open('Songs/' + sid + '/analysis.json', encoding='utf-8'))
        bpm, times, strg, band = load(sid)
        offset = d.get('OffsetSeconds', 0)
        r1 = decide(times, strg, band, bpm, offset, use_band=True)
        r2 = decide(times, strg, band, bpm, offset, use_band=False)
        a = stats(r1, strg, times, bpm, offset)
        b = stats(r2, strg, times, bpm, offset)
        print('%-13s R1(带频带): 蓝%%=%2.0f 蓝强=%.2f 红强=%.2f 反拍=%.0f%%' % (name, *a))
        print('%-13s R2(纯强度): 蓝%%=%2.0f 蓝强=%.2f 红强=%.2f 反拍=%.0f%%' % ('', *b))


if __name__ == '__main__':
    main()
