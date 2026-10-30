# -*- coding: utf-8 -*-
"""v2 定色规则模拟：节奏正确（时序不动）+ 游戏性（连打段咚咔交替、同色连长受限）。

R_base  = 强度 < 0.5x局部最强(±半拍) → 咔          （音乐语义：主拍重击=咚，陪衬=咔）
R_v2    = R_base
          + 连打段交替：相邻间隔 ≤0.75 拍且长度≥3 的串，按 D-K-D-K 交替（首音颜色=R_base）
          + 同色连长上限 6：超长段把段内最弱音翻成对色
          + 全局咔占比 ≤45%：超出翻回最弱的咔
观察：咔占比 / 最长同色连打 / 交替率（连打段内颜色相异相邻对占比）。
"""
import json
import sys
import numpy as np

sys.path.insert(0, 'tools')
from donkat_strategy_sim import SONGS, load


def base_kat(times, strg, bpm):
    n = len(times)
    win = 60.0 / bpm
    kat = np.zeros(n, bool)
    for i in range(n):
        lo = np.searchsorted(times, times[i] - win / 2)
        hi = np.searchsorted(times, times[i] + win / 2)
        kat[i] = strg[i] < 0.5 * strg[lo:hi].max()
    return kat


def v2(times, strg, bpm, stream=3, run_cap=6, kat_cap=0.45):
    n = len(times)
    beat = 60.0 / bpm
    color = base_kat(times, strg, bpm)

    # 连打段交替
    gap_max = beat * 0.75
    i = 0
    stream_notes = 0
    stream_alt = 0
    while i < n:
        j = i
        while j + 1 < n and times[j + 1] - times[j] <= gap_max:
            j += 1
        if j - i + 1 >= stream:
            c0 = color[i]
            for k in range(i, j + 1):
                color[k] = c0 if (k - i) % 2 == 0 else not c0
            stream_notes += j - i + 1
            stream_alt += sum(1 for k in range(i, j) if color[k] != color[k + 1])
        i = j + 1

    # 全局咔上限
    max_kat = int(kat_cap * n)
    extra = color.sum() - max_kat
    if extra > 0:
        cand = np.where(color)[0]
        color[cand[np.argsort(-strg[cand])][:extra]] = False  # 翻回最弱的咔→咚

    # 同色连长上限
    def longest_run(color):
        best = cur = 1
        for k in range(1, n):
            cur = cur + 1 if color[k] == color[k - 1] else 1
            best = max(best, cur)
        return best

    changed = True
    guard = 0
    while changed and guard < 50:
        changed = False
        guard += 1
        k = 0
        while k < n:
            m = k
            while m + 1 < n and color[m + 1] == color[k]:
                m += 1
            if m - k + 1 > run_cap:
                seg = np.arange(k, m + 1)
                # 翻段内最弱音（弱音更可能是"咔"，语义损伤最小）
                flip = seg[np.argmin(strg[seg])]
                color[flip] = not color[flip]
                changed = True
            k = m + 1

    return color, longest_run(color), (stream_alt / stream_notes if stream_notes else 0)


def main():
    print('%-14s %-24s %-24s' % ('曲', 'v1(基色) 蓝% / 最长同色', 'v2 蓝% / 最长同色 / 交替率'))
    for sid, name in SONGS:
        d = json.load(open('Songs/' + sid + '/analysis.json', encoding='utf-8'))
        bpm, times, strg, band = load(sid)
        b = base_kat(times, strg, bpm)
        # v1 最长同色
        br = 1
        cur = 1
        for k in range(1, len(b)):
            cur = cur + 1 if b[k] == b[k - 1] else 1
            br = max(br, cur)
        v, run, alt = v2(times, strg, bpm)
        print('%-14s %4.0f%% / %3d%-12s %4.0f%% / %3d / %3.0f%%' % (
            name, b.sum() / len(b) * 100, br, '', v.sum() / len(v) * 100, run, alt * 100))


if __name__ == '__main__':
    main()
