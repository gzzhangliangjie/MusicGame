# -*- coding: utf-8 -*-
"""独立 BPM 交叉验证：onset envelope 自相关 + 频谱流两种口径。"""
import sys
import numpy as np
import subprocess


def load_mono_44k(path):
    import imageio_ffmpeg
    exe = imageio_ffmpeg.get_ffmpeg_exe()
    cmd = [exe, "-v", "quiet", "-i", path, "-f", "s16le", "-ac", "1", "-ar", "22050", "-"]
    raw = subprocess.run(cmd, capture_output=True, timeout=120).stdout
    x = np.frombuffer(raw, dtype=np.int16).astype(np.float64) / 32768.0
    return x, 22050


def bpm_by_autocorr(mono, sr):
    # 全带 onset envelope：谱流 -> 正值 -> 平滑
    n = len(mono)
    hop, win = 256, 1024
    num = 1 + (n - win) // hop
    idx = np.arange(win)[None, :] + hop * np.arange(num)[:, None]
    frames = mono[idx] * (0.5 - 0.5 * np.cos(2 * np.pi * np.arange(win) / win))[None, :]
    mags = np.abs(np.fft.rfft(frames, axis=1))
    flux = np.maximum(np.diff(mags, axis=0), 0).sum(axis=1)
    flux -= flux.mean()
    ac = np.correlate(flux, flux, "full")[len(flux) - 1:]
    lag_min, lag_max = int(60 / 200 * sr / hop), int(60 / 55 * sr / hop)
    best = {}
    for bpm_lo, bpm_hi, tag in [(55, 95, "慢"), (95, 140, "中"), (140, 200, "快")]:
        lo = int(60 / bpm_hi * sr / hop)
        hi = int(60 / bpm_lo * sr / hop)
        seg = ac[lo:hi + 1]
        k = int(np.argmax(seg))
        best[tag] = (60.0 * sr / hop) / (lo + k), seg[k] / (ac[0] + 1e-9)
    return best


def main():
    path = sys.argv[1]
    mono, sr = load_mono_44k(path)
    for tag, (bpm, score) in bpm_by_autocorr(mono, sr).items():
        print("%s段候选: BPM=%.1f (相关峰强度 %.2f)" % (tag, bpm, score))


if __name__ == "__main__":
    main()
