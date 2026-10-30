# -*- coding: utf-8 -*-
"""策略 G 扫参：局部相对强度定色。
G：high 带=蓝；其余 onset 若强度 < alpha * 局部窗(1拍)最大强度 → 蓝。
观察蓝占比 + 蓝红强度分离度 + 蓝落反拍率。健康目标：蓝 25~40%。
"""
import json
import sys
import numpy as np

sys.path.insert(0, 'tools')
from donkat_strategy_sim import SONGS, load


def strat_g(times, strg, band, bpm, offset, alpha, cap=None):
    beat = 60.0 / bpm
    n = len(band)
    local_max = np.zeros(n)
    win = beat
    for i in range(n):
        lo = np.searchsorted(times, times[i] - win / 2)
        hi = np.searchsorted(times, times[i] + win / 2)
        local_max[i] = strg[lo:hi].max()
    is_kat = (band == 'high') | (strg < alpha * local_max)
    if cap is not None:
        max_kat = int(cap * n)
        if is_kat.sum() > max_kat:
            cand = np.where(is_kat)[0]
            by_str = cand[np.argsort(-strg[cand])]  # 最强者翻回红
            is_kat[by_str[:is_kat.sum() - max_kat]] = False
    return is_kat


def main():
    beat_tol = 0.045
    for alpha in (0.3, 0.4, 0.5, 0.6):
        print('== alpha=%.1f（无上限） ==' % alpha)
        for sid, name in SONGS:
            d = json.load(open('Songs/' + sid + '/analysis.json', encoding='utf-8'))
            bpm, times, strg, band = load(sid)
            kat = strat_g(times, strg, band, bpm, d.get('OffsetSeconds', 0), alpha)
            half = 30.0 / bpm
            phase = np.mod(times - d.get('OffsetSeconds', 0), half) / half
            offbeat = np.minimum(phase, 1 - phase) * half > beat_tol
            k_str = np.median(strg[kat]) if kat.sum() else 0
            r_str = np.median(strg[~kat]) if (~kat).sum() else 0
            off_pct = (kat & offbeat).sum() / max(1, kat.sum()) * 100
            print('  %-14s 蓝%%=%3.0f  蓝强中位=%.2f 红强中位=%.2f  蓝落反拍=%.0f%%' % (
                name, kat.sum() / len(band) * 100, k_str, r_str, off_pct))
    print('== alpha=0.5 + 蓝上限40% ==')
    for sid, name in SONGS:
        d = json.load(open('Songs/' + sid + '/analysis.json', encoding='utf-8'))
        bpm, times, strg, band = load(sid)
        kat = strat_g(times, strg, band, bpm, d.get('OffsetSeconds', 0), 0.5, cap=0.40)
        half = 30.0 / bpm
        phase = np.mod(times - d.get('OffsetSeconds', 0), half) / half
        offbeat = np.minimum(phase, 1 - phase) * half > beat_tol
        k_str = np.median(strg[kat]) if kat.sum() else 0
        r_str = np.median(strg[~kat]) if (~kat).sum() else 0
        off_pct = (kat & offbeat).sum() / max(1, kat.sum()) * 100
        print('  %-14s 蓝%%=%3.0f  蓝强中位=%.2f 红强中位=%.2f  蓝落反拍=%.0f%%' % (
            name, kat.sum() / len(band) * 100, k_str, r_str, off_pct))


if __name__ == '__main__':
    main()
