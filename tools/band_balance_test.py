# -*- coding: utf-8 -*-
"""复现 SpectralFluxOnsetDetector 的频带判定，对比 sum(现状) vs mean(按bin归一) 的频段分布。
只关注 onset 的 Band 归属链路：分带 flux -> 自适应阈值 -> 局部极大 -> 50ms 合并取最强。
"""
import sys
import numpy as np

import subprocess

def load_mono_44k(path):
    import imageio_ffmpeg
    exe = imageio_ffmpeg.get_ffmpeg_exe()
    cmd = [exe, "-v", "quiet", "-i", path, "-f", "s16le", "-ac", "1", "-ar", "44100", "-"]
    raw = subprocess.run(cmd, capture_output=True, timeout=120).stdout
    x = np.frombuffer(raw, dtype=np.int16).astype(np.float64) / 32768.0
    return x, 44100

FFT = 2048
HOP = 512
K = 1.5
HALF_WIN = 10
MIN_GAP = 0.05


def make_band(lo_hz, hi_hz, bin_hz, spec_bins):
    lo = max(1, int(np.ceil(lo_hz / bin_hz)))
    hi = min(spec_bins - 1, int(hi_hz / bin_hz))
    return (lo, max(lo, hi))


def detect(mono, sr, mode, high_lo_hz=2000.0):
    n = len(mono)
    if n < FFT:
        return []
    num_frames = 1 + (n - FFT) // HOP
    spec_bins = FFT // 2 + 1
    bin_hz = sr / FFT
    bands = [
        ("low", make_band(20.0, 150.0, bin_hz, spec_bins)),
        ("mid", make_band(150.0, 2000.0, bin_hz, spec_bins)),
        ("high", make_band(high_lo_hz, sr * 0.5, bin_hz, spec_bins)),
    ]
    window = 0.5 - 0.5 * np.cos(2 * np.pi * np.arange(FFT) / FFT)

    # 分帧 FFT（批量）
    idx = np.arange(FFT)[None, :] + HOP * np.arange(num_frames)[:, None]
    frames = mono[idx] * window[None, :]
    mags = np.abs(np.fft.rfft(frames, axis=1))  # (num_frames, spec_bins)

    flux = np.zeros((len(bands), num_frames))
    for b, (_, (lo, hi)) in enumerate(bands):
        d = np.diff(mags[:, lo:hi + 1], axis=0)
        np.maximum(d, 0.0, out=d)
        s = d.sum(axis=1)
        if mode == "mean":
            s = s / (hi - lo + 1)
        flux[b, 1:] = s
        # 帧间对齐：detector 的 flux[f] 是 mag[f]-mag[f-1]，f=0 无前帧记 0

    cands = []
    for b, (name, _) in enumerate(bands):
        fl = flux[b]
        for t in range(num_frames):
            v = fl[t]
            if v <= 0:
                continue
            wa, wb = max(0, t - HALF_WIN), min(num_frames - 1, t + HALF_WIN)
            w = fl[wa:wb + 1]
            mean = w.mean()
            std = w.std()
            if v <= mean + K * std:
                continue
            prev = fl[t - 1] if t > 0 else 0.0
            nxt = fl[t + 1] if t < num_frames - 1 else 0.0
            if v >= prev and v > nxt:
                cands.append(((t * HOP + FFT * 0.5) / sr, v, name))

    cands.sort(key=lambda c: c[0])
    merged = []
    i = 0
    while i < len(cands):
        j = i
        best = cands[i]
        while j + 1 < len(cands) and cands[j + 1][0] - cands[i][0] <= MIN_GAP:
            j += 1
            if cands[j][1] > best[1]:
                best = cands[j]
        merged.append(best)
        i = j + 1
    return merged


def main():
    path = sys.argv[1]
    samples, sr = load_mono_44k(path)

    # 只取中段 90 秒，控制耗时
    mid = len(samples) // 2
    seg_samples = samples[mid - 45 * sr: mid + 45 * sr]

    for label, mode, hi in [
        ("现状 sum/high>=2k", "sum", 2000.0),
        ("修复 mean/high>=2k", "mean", 2000.0),
        ("mean+high>=4k", "mean", 4000.0),
    ]:
        merged = detect(seg_samples, sr, mode, hi)
        cnt = {"low": 0, "mid": 0, "high": 0}
        for _, _, band in merged:
            cnt[band] += 1
        total = max(1, len(merged))
        blue = cnt["high"] + cnt["mid"] * 0.5
        red = cnt["low"] + cnt["mid"] * 0.5
        print(f"{label}: total={len(merged)} low={cnt['low']}({cnt['low']/total:.0%}) "
              f"mid={cnt['mid']}({cnt['mid']/total:.0%}) high={cnt['high']}({cnt['high']/total:.0%}) "
              f"=> 太鼓红:蓝 = {red:.0f}:{blue:.0f} ({red/(red+blue):.0%}:{blue/(red+blue):.0%})")


if __name__ == "__main__":
    main()
