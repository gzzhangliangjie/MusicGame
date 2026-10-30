# -*- coding: utf-8 -*-
"""复现 BpmEstimator，对比 onset 包络权重（linear/sqrt/unit）对 BPM 的影响。
参考真值：独立 autocorr 候选 112.3（与修复前 Unity 输出 112.25 吻合）。
"""
import sys
import numpy as np
import subprocess
import collections

FFT = 2048
HOP = 512
K = 1.5
HALF_WIN = 10
MIN_GAP = 0.05

ENVELOPE_STEP = 0.01
MIN_BPM, MAX_BPM = 60.0, 200.0


def load_mono_44k(path):
    import imageio_ffmpeg
    exe = imageio_ffmpeg.get_ffmpeg_exe()
    cmd = [exe, "-v", "quiet", "-i", path, "-f", "s16le", "-ac", "1", "-ar", "44100", "-"]
    raw = subprocess.run(cmd, capture_output=True, timeout=180).stdout
    return np.frombuffer(raw, dtype=np.int16).astype(np.float64) / 32768.0, 44100


def detect(mono, sr, mode="mean"):
    n = len(mono)
    num_frames = 1 + (n - FFT) // HOP
    spec_bins = FFT // 2 + 1
    bin_hz = sr / FFT

    def band(lo, hi):
        l = max(1, int(np.ceil(lo / bin_hz)))
        h = min(spec_bins - 1, int(hi / bin_hz))
        return (l, max(l, h))

    bands = [("low", band(20, 150)), ("mid", band(150, 2000)), ("high", band(2000, sr * 0.5))]
    window = 0.5 - 0.5 * np.cos(2 * np.pi * np.arange(FFT) / FFT)
    idx = np.arange(FFT)[None, :] + HOP * np.arange(num_frames)[:, None]
    mags = np.abs(np.fft.rfft(mono[idx] * window[None, :], axis=1))

    flux = np.zeros((3, num_frames))
    for b, (_, (lo, hi)) in enumerate(bands):
        d = np.diff(mags[:, lo:hi + 1], axis=0)
        np.maximum(d, 0.0, out=d)
        s = d.sum(axis=1)
        if mode == "mean":
            s = s / (hi - lo + 1)
        flux[b, 1:] = s

    cands = []
    for b, (name, _) in enumerate(bands):
        fl = flux[b]
        for t in range(1, num_frames - 1):
            v = fl[t]
            if v <= 0:
                continue
            wa, wb = max(0, t - HALF_WIN), min(num_frames - 1, t + HALF_WIN)
            w = fl[wa:wb + 1]
            if v <= w.mean() + K * w.std():
                continue
            if v >= fl[t - 1] and v > fl[t + 1]:
                cands.append(((t * HOP + FFT * 0.5) / sr, v, name))

    cands.sort(key=lambda c: c[0])
    merged, i = [], 0
    while i < len(cands):
        j, best = i, cands[i]
        while j + 1 < len(cands) and cands[j + 1][0] - cands[i][0] <= MIN_GAP:
            j += 1
            if cands[j][1] > best[1]:
                best = cands[j]
        merged.append(best)
        i = j + 1
    mx = max(m[1] for m in merged) or 1.0
    return [(t, v / mx, b) for t, v, b in merged]


def estimate_bpm(onsets, weight):
    last = max(t for t, _, _ in onsets)
    length = int(last / ENVELOPE_STEP) + 2
    env = np.zeros(length)
    for t, s, b in onsets:
        pos = t / ENVELOPE_STEP
        i0 = int(np.floor(pos))
        frac = pos - i0
        w = {"linear": s, "sqrt": np.sqrt(s), "unit": 1.0}[weight]
        if 0 <= i0 < length:
            env[i0] += w * (1 - frac)
        if 0 <= i0 + 1 < length:
            env[i0 + 1] += w * frac

    min_lag = round(60.0 / MAX_BPM / ENVELOPE_STEP)
    max_lag = round(60.0 / MIN_BPM / ENVELOPE_STEP)
    acf = np.zeros(max_lag + 1)
    for lag in range(min_lag, max_lag + 1):
        seg = env[:len(env) - lag] * env[lag:]
        acf[lag] = seg.mean() if len(seg) else 0.0
    acf_max = acf.max() or 1.0

    def acf_at(bpm):
        lag = 60.0 / bpm / ENVELOPE_STEP
        l0 = int(np.floor(lag))
        frac = lag - l0
        v0 = acf[max(min_lag, min(max_lag, l0))] / acf_max
        v1 = acf[max(min_lag, min(max_lag, l0 + 1))] / acf_max
        return v0 * (1 - frac) + v1 * frac

    times = sorted(t for t, _, _ in onsets)
    bins = 561
    hist = np.zeros(bins)
    for i in range(1, len(times)):
        iv = times[i] - times[i - 1]
        if iv < 0.15 or iv > 2.0:
            continue
        bpm = 60.0 / iv
        while bpm > MAX_BPM + 1e-6:
            bpm *= 0.5
        while bpm < MIN_BPM - 1e-6:
            bpm *= 2.0
        k = int((bpm - MIN_BPM) / 0.25)
        if 0 <= k < bins:
            hist[k] += 1.0
    kernel = np.array([1, 2, 3, 4, 3, 2, 1], float)
    smooth = np.convolve(hist, kernel / kernel.sum(), "same")
    hist_max = smooth.max() or 1.0

    def hist_at(bpm):
        pos = (bpm - MIN_BPM) / 0.25
        i0 = int(np.floor(pos))
        frac = pos - i0
        h0 = smooth[i0] / hist_max if 0 <= i0 < bins else 0.0
        h1 = smooth[i0 + 1] / hist_max if 0 <= i0 + 1 < bins else 0.0
        return h0 * (1 - frac) + h1 * frac

    def prior(bpm):
        o = np.log2(bpm / 120.0)
        return np.exp(-0.5 * o * o)

    def score(bpm):
        return acf_at(bpm) * prior(bpm) + 0.6 * hist_at(bpm)

    grid = np.linspace(MIN_BPM, MAX_BPM, 1401)
    scores = np.array([score(b) for b in grid])
    best_bpm = float(grid[np.argmax(scores)])
    best_score = scores.max()

    chosen = best_bpm
    for alt0 in (best_bpm * 2, best_bpm * 0.5):
        alt = alt0
        while alt > MAX_BPM + 1e-6:
            alt *= 0.5
        while alt < MIN_BPM - 1e-6:
            alt *= 2.0
        if not (MIN_BPM - 1e-3 <= alt <= MAX_BPM + 1e-3) or abs(alt - best_bpm) <= 0.2:
            continue
        if score(alt) >= 0.9 * best_score and abs(alt - 120.0) < abs(chosen - 120.0):
            chosen = alt

    fine = np.arange(max(MIN_BPM, chosen - 1.0), min(MAX_BPM, chosen + 1.0) + 1e-9, 0.05)
    fs = np.array([score(b) for b in fine])
    return round(float(fine[np.argmax(fs)]), 3)


def main():
    mono, sr = load_mono_44k(sys.argv[1])
    onsets = detect(mono, sr)
    c = collections.Counter(b for _, _, b in onsets)
    print("onsets=%d %s" % (len(onsets), dict(c)))
    for w in ("linear", "sqrt", "unit"):
        print("权重 %-6s => BPM %.2f" % (w, estimate_bpm(onsets, w)))
    print("参照：独立 autocorr 112.3 / 修复前 Unity 112.25")


if __name__ == "__main__":
    main()
