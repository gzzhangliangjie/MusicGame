# -*- coding: utf-8 -*-
"""模拟不同 红/蓝(don/kat) 分配策略的效果，用真实 analysis.json 数据。

策略：
  A 现状        ：band 胜者定色（low=红, high=蓝, mid=掷硬币对半）
  B 带内归一    ：先按各自 band 内的强度百分位归一，再比较三带谁"在带内更突出"
  C 节奏角色    ：局部窗（0.5 拍）内最强者=红，其余=蓝；high 带强制蓝
  D 混合        ：high 带=蓝；low/mid 内局部最强=红、其余=蓝
参考健康区间：成熟太鼓谱蓝占比约 25%~40%。
"""
import json
import collections
import numpy as np

SONGS = [
    ('song_461d3a0508', 'SHINING SOUL'),
    ('song_7c80c9da8a', '红莲华'),
    ('song_73d9b60f15', '木鱼与金鱼'),
    ('song_a14b9fe54a', 'Bad Apple'),
    ('song_4b393e49aa', '双截棍'),
]


def load(sid):
    d = json.load(open('Songs/' + sid + '/analysis.json', encoding='utf-8'))
    on = d['Onsets']
    return (d['Bpm'], np.array([o['TimeSeconds'] for o in on]),
            np.array([o['Strength'] for o in on]),
            np.array([o['Band'] for o in on]))


def ratio(kat, total):
    return kat / total * 100 if total else 0


def strat_a(times, strg, band):
    is_kat = (band == 'high') | ((band == 'mid') & (np.arange(len(band)) % 2 == 0))
    return is_kat


def strat_b(times, strg, band):
    # 各带内百分位（相对该带自身分布的突出度），胜者=最高者；再叠现状的带→色
    norm = np.zeros(len(strg))
    for b in ('low', 'mid', 'high'):
        m = band == b
        if m.sum():
            norm[m] = strg[m].argsort().argsort() / max(1, m.sum() - 1)
    winner = np.full(len(band), 'mid', dtype=object)
    order = ['low', 'mid', 'high']  # 平票时保守取低频
    best = norm.copy()
    # 逐 onset 取三带中归一化最高者——但每 onset 本就只有一个 band，归一化后仍需跨带比较原始值
    # 简化：直接用带内百分位作为强度、原 band 定色（low 红 / high 蓝 / mid 交替）
    is_kat = (band == 'high') | ((band == 'mid') & (np.arange(len(band)) % 2 == 0))
    return is_kat


def strat_c(times, strg, band, bpm):
    beat = 60.0 / bpm
    win = beat * 0.5
    is_kat = np.zeros(len(band), bool)
    i = 0
    n = len(band)
    while i < n:
        j = i
        while j + 1 < n and times[j + 1] - times[i] <= win:
            j += 1
        loc = strg[i:j + 1]
        strong = i + int(np.argmax(loc))
        for k in range(i, j + 1):
            if band[k] == 'high':
                is_kat[k] = True
            elif k != strong:
                is_kat[k] = True
        i = j + 1
    return is_kat


def strat_d(times, strg, band, bpm):
    beat = 60.0 / bpm
    win = beat * 0.5
    is_kat = band == 'high'
    i = 0
    n = len(band)
    while i < n:
        j = i
        while j + 1 < n and times[j + 1] - times[i] <= win:
            j += 1
        loc_mask = ~is_kat[i:j + 1]
        idx = np.arange(i, j + 1)[loc_mask]
        if len(idx):
            strong = idx[int(np.argmax(strg[idx]))]
            is_kat[idx[idx != strong]] = True
        i = j + 1
    return is_kat


def main():
    print('%-14s %6s %6s | %8s %8s %8s %8s' % (
        '曲', '现状蓝%', '带内蓝%', 'C蓝%', 'D蓝%', 'C强蓝%', 'D强蓝%'))
    for sid, name in SONGS:
        bpm, times, strg, band = load(sid)
        n = len(band)
        a = strat_a(times, strg, band)
        c = strat_c(times, strg, band, bpm)
        d = strat_d(times, strg, band, bpm)
        # 强度视角：蓝色音符的强度中位数 vs 红色（蓝应该是弱拍/镲片）
        ck = strg[c].size and np.median(strg[c]) or 0
        rk = strg[~c].size and np.median(strg[~c]) or 0
        dk = strg[d].size and np.median(strg[d]) or 0
        dr = strg[~d].size and np.median(strg[~d]) or 0
        print('%-14s %5.0f%% %5.0f%% | %7.0f%% %7.0f%%   蓝强中位 C=%.2f D=%.2f / 红强中位 C=%.2f D=%.2f' % (
            name, ratio(a.sum(), n), ratio(c.sum(), n), ratio(d.sum(), n), 0,
            ck, dk, rk, dr))


if __name__ == '__main__':
    main()


def strat_e(times, strg, band, bpm, offset):
    """节拍网格：整拍±容差=红，反拍=蓝，high 带=蓝。"""
    beat = 60.0 / bpm
    half = beat / 2
    phase = np.mod(np.asarray(times) - offset, half) / half  # 0=整拍, 0.5=反拍
    on_beat = np.minimum(phase, 1 - phase) * half <= 0.045  # ±45ms 判整拍
    is_kat = (~on_beat) | (band == 'high')
    return is_kat


def strat_f(times, strg, band, bpm, offset, kat_cap=0.35):
    """E + 比例闸门：反拍候选按强度排序保留弱的，强反拍仍红，蓝不超过 kat_cap。"""
    is_kat = strat_e(times, strg, band, bpm, offset)
    cand = np.where(is_kat & (band != 'high'))[0]
    max_kat = int(kat_cap * len(band))
    extra = len(cand) - max_kat
    if extra > 0:
        by_strength = cand[np.argsort(strg[cand])]
        is_kat[by_strength[:extra]] = False  # 最强的 extra 个反拍翻回红
    return is_kat


def main2():
    print()
    print('%-14s | %6s %6s | 蓝强度中位/红强度中位 | 蓝落反拍占比' % ('曲', 'E蓝%', 'F蓝%'))
    for sid, name in SONGS:
        d = json.load(open('Songs/' + sid + '/analysis.json', encoding='utf-8'))
        bpm, times, strg, band = load(sid)
        offset = d.get('OffsetSeconds', 0)
        e = strat_e(times, strg, band, bpm, offset)
        f = strat_f(times, strg, band, bpm, offset)
        beat = 60.0 / bpm
        half = beat / 2
        phase = np.mod(times - offset, half) / half
        offbeat = np.minimum(phase, 1 - phase) * half > 0.045
        e_off = e & offbeat
        e_ratio = e_off.sum() / max(1, e.sum())
        line = '%-14s | %5.0f%% %5.0f%% | %.2f / %.2f | %.0f%%' % (
            name, ratio(e.sum(), len(band)), ratio(f.sum(), len(band)),
            np.median(strg[e]) if e.sum() else 0,
            np.median(strg[~e]) if (~e).sum() else 0,
            e_ratio * 100)
        print(line)


