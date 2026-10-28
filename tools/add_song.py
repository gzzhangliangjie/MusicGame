#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""add_song.py — MusicGame 一键入库新歌（酷狗格式固化流程，2026-10-27 定稿）

把酷狗下载的加密音频/歌词/MV 一路做成可玩的歌曲目录，并可选自动出谱+验收：
  解密(kgm/kgma→mp3/flac) → flac 归一为 320k mp3 → KRC 转 LRC →
  MV 转 H.264 mp4（HEVC 必须重编码；奇数宽修正；SAR 归一；码率封顶；一律去音轨）→
  MV↔歌曲对齐量测（能量包络互相关，分段一致性校验，漂移告警）→
  Songs/song_<md5前10>/ 落库（audio + <Title>.lrc + mv.mp4 + meta.json）→
  Unity 批处理离线出四档谱面 → MV 播放无头自检。

用法（默认全链：落库 + Unity 出谱 + MV 播放自检）：
  python tools/add_song.py --audio "C:\\Kugou\\KugouMusic\\王力宏 - 改变自己.kgma"
可选：
  --mv <mkv|mp4>       缺省自动在 C:\\Kugou 找 <标题>*.mkv/*.mp4
  --krc <krc> / --lrc <lrc>  缺省自动在 C:\\Kugou\\Lyric 找 <标题>-*.krc
  --title <显示名>      缺省 = 音频文件名去扩展名（同时是 LRC 匹配名，改了要连 LRC 一起对齐）
  --no-mv / --skip-sync / --no-import / --no-verify   跳过对应环节
  --kugou <目录>        缺省 C:\\Kugou（MV/歌词搜索根）
退出码：0=全绿；非 0=对应步骤失败（meta 仍会落盘，重跑幂等）。
"""
import argparse
import glob
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

import numpy as np

SONGS_ROOT = r"E:\MusicGame\Songs"
SKILL_SCRIPTS = r"C:\Users\zlj10\.agents\skills\kgm2mp3\scripts"
UNITY_GLOB = r"D:\Unity\2022.3.*/Editor/Unity.exe"
UNITY_FALLBACK = r"D:\Unity\2022.3.57f1c2\Editor\Unity.exe"
SR = 8000       # 对齐量测采样率
BIN = 400       # 包络 bin = 50ms
SEG_S = 45      # 分段长度
TOL = 0.15      # 段间一致性容差（秒）


def ffmpeg_exe():
    try:
        import imageio_ffmpeg
        return imageio_ffmpeg.get_ffmpeg_exe()
    except Exception:
        sys.exit("FAIL: 找不到 ffmpeg（pip install imageio-ffmpeg）")


FF = None


def run(cmd, what, caps=True):
    p = subprocess.run(cmd, capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    if p.returncode != 0 and caps:
        sys.exit("FAIL %s rc=%d\n%s" % (what, p.returncode, (p.stderr or "")[-800:]))
    return p


def ffmpeg(args, what):
    return run([FF] + args, what)


def md5_10(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()[:10]


def probe_duration(path):
    p = run([FF, "-i", path], "probe " + path, caps=False)
    m = re.search(r"Duration: (\d+):(\d+):([\d.]+)", p.stderr)
    return round(int(m.group(1)) * 3600 + int(m.group(2)) * 60 + float(m.group(3)), 3) if m else 0.0


def probe_video(path):
    """返回 (codec, w, h, sar_w, sar_h)。"""
    p = run([FF, "-i", path], "probe " + path, caps=False)
    m = re.search(r"Video: (\w+).*?, (\d+)x(\d+)", p.stderr)
    if not m:
        sys.exit("FAIL: MV 无视频流 " + path)
    sar = re.search(r"SAR (\d+):(\d+)", p.stderr)
    sw, sh = (int(sar.group(1)), int(sar.group(2))) if sar else (1, 1)
    return m.group(1), int(m.group(2)), int(m.group(3)), sw, sh


# ---------------------------------------------------------------- 音频

def prepare_audio(src, title):
    """解密/归一 → 返回工程内 audio 文件路径。"""
    ext = os.path.splitext(src)[1].lower()
    if ext == ".flac":
        print("[audio] FLAC → 320k mp3")
        dst = os.path.join(tempfile.gettempdir(), "addsong_" + md5_10(src) + ".mp3")
        ffmpeg(["-i", src, "-c:a", "libmp3lame", "-b:a", "320k", "-y", dst], "flac转码")
        return dst
    if ext in (".mp3", ".ogg", ".wav"):
        return src
    if ext in (".kgm", ".kgma", ".vpr"):
        script = os.path.join(SKILL_SCRIPTS, "decrypt.py")
        if not os.path.isfile(script):
            sys.exit("FAIL: 找不到解密脚本 " + script)
        print("[audio] 解密 " + ext + " …")
        run([sys.executable, script, src], "解密")
        stem = os.path.splitext(src)[0]
        outs = [p for e in (".mp3", ".flac") for p in glob.glob(glob.escape(stem) + e)]
        if not outs:
            sys.exit("FAIL: 解密后未发现产物")
        return prepare_audio(max(outs, key=os.path.getmtime), title)
    sys.exit("FAIL: 不认识的音频格式 " + src)


# ---------------------------------------------------------------- 歌词

def prepare_lrc(args, title, song_dir):
    if args.lrc:
        shutil.copyfile(args.lrc, os.path.join(song_dir, title + ".lrc"))
        return "指定 LRC"
    krc = args.krc
    if not krc:
        hits = glob.glob(os.path.join(args.kugou, "Lyric", glob.escape(title) + "-*.krc"))
        if not hits:
            print("[lrc] 没找到歌词（酷狗客户端里补一下下载再重跑，或 --krc 指定）")
            return None
        krc = hits[0]
    script = os.path.join(SKILL_SCRIPTS, "krc2lrc.py")
    if not os.path.isfile(script):
        sys.exit("FAIL: 找不到 krc2lrc " + script)
    out = os.path.splitext(krc)[0] + ".lrc"
    # krc2lrc 剥掉 -hash-id-序号 后缀，产物是 <清洗后标题>.lrc（与带后缀的预期路径不同）
    cleaned = re.sub(r"-[0-9a-f]{32}-\d+-\d+$", "", os.path.splitext(krc)[0]) + ".lrc"
    for attempt in (1, 2):  # 酷狗可能还在写 krc，重试一次
        p = run([sys.executable, script, krc], "KRC转LRC", caps=False)
        out = cleaned if os.path.isfile(cleaned) else out
        if os.path.isfile(out) and os.path.getsize(out) > 0:
            break
        print("[lrc] 第 %d 次转换未产出，输出如下，10s 后重试" % attempt)
        print((p.stdout or "")[-500:])
        print((p.stderr or "")[-500:])
        import time
        time.sleep(10)
    if not os.path.isfile(out):
        sys.exit("FAIL: krc2lrc 两次均未产出 " + out)
    shutil.copyfile(out, os.path.join(song_dir, title + ".lrc"))
    return "KRC→LRC"


# ---------------------------------------------------------------- MV

def prepare_mv(src, dst):
    codec, w, h, sw, sh = probe_video(src)
    square_sar = abs(sw / max(1, sh) - 1.0) < 0.01
    if codec in ("h264",) and square_sar:
        print("[mv] H.264 + 方像素 → 无损转封装")
        ffmpeg(["-i", src, "-c:v", "copy", "-an", "-movflags", "+faststart", "-y", dst], "mv转封装")
        return
    print("[mv] %s %dx%d SAR %d:%d → 重编码 H.264（≤960x540、码率封顶、去音轨）" % (codec, w, h, sw, sh))
    f = min(1.0, 960.0 / w, 540.0 / h)  # python 里算好等比目标宽高：
    tw = max(2, int(w * f) // 2 * 2)    # 滤镜串不用 min()/逗号（会被 filtergraph 当分隔符）
    th = max(2, int(h * f) // 2 * 2)
    vf = "scale=%d:%d" % (tw, th) + ("" if square_sar else ",setsar=1")
    ffmpeg(["-i", src, "-vf", vf, "-c:v", "libx264", "-preset", "medium", "-crf", "21",
            "-maxrate", "1000k", "-bufsize", "1500k", "-an", "-movflags", "+faststart",
            "-y", dst], "mv重编码")


# ---------------------------------------------------------------- 对齐量测

def pcm(path):
    p = subprocess.run([FF, "-i", path, "-vn", "-ac", "1", "-ar", str(SR), "-f", "s16le", "-"],
                       capture_output=True)  # 二进制 stdout（喂 numpy，不能 text 模式）
    if p.returncode != 0 or not p.stdout:
        sys.exit("FAIL decode: " + path)
    x = np.frombuffer(p.stdout, dtype=np.int16).astype(np.float32)
    return x - x.mean()


def envelope(x):
    bins = len(x) // BIN
    e = np.sqrt((x[:bins * BIN].reshape(bins, BIN) ** 2).mean(axis=1))
    e -= e.mean()
    return e / (e.std() or 1.0)


def measure_offset(mv_audio_path, song_audio_path):
    """返回 (offset|None, 描述)。offset = 歌曲 t=0 在 MV 内的秒数。"""
    mv = envelope(pcm(mv_audio_path))
    sg = envelope(pcm(song_audio_path))
    seg_bins = int(SEG_S / 0.05)
    ests = []
    for s in range(0, int(len(sg) * 0.05) - SEG_S, SEG_S):
        seg = sg[int(s / 0.05):int(s / 0.05) + seg_bins]
        if len(seg) < seg_bins * 0.8:
            break
        size = 1
        while size < len(mv) + len(seg):
            size <<= 1
        xc = np.fft.irfft(np.fft.rfft(mv, size) * np.conj(np.fft.rfft(seg, size)), size)
        idx = int(np.argmax(xc))
        lag = idx if idx < len(xc) // 2 else idx - len(xc)
        overlap = min(len(seg), len(mv) - lag) - max(0, -lag)
        coef = float(xc[idx]) / max(1, overlap)
        est = lag * 0.05 - s
        ests.append((s, est, coef))
        print("    song %3ds: mv_t0=%+.2fs coef=%.3f" % (s, est, coef))
    good = []
    for _, e, c in ests:
        if c > 2.0:
            continue  # coef 爆表 = 重叠退化（片尾碎段），纯废行
        if c >= 0.30:
            good.append(e)
    if len(good) < 2:
        return None, "有效段不足（MV 音轨可能不是同一版本），不设偏移"
    best_cluster = []  # 最大簇（防个别段的高相关伪峰，如副歌重复）
    for center in good:
        cluster = [e for e in good if abs(e - center) <= TOL]
        if len(cluster) > len(best_cluster):
            best_cluster = cluster
    if len(best_cluster) < max(2, len(good) - 1):
        return None, "段间不一致（疑似 MV 内部剪辑），不设偏移、请人工复核"
    return round(float(np.median(best_cluster)), 2), "恒定偏移"


# ---------------------------------------------------------------- Unity

def unity_exe():
    hits = sorted(glob.glob(r"D:\Unity\2022.3*\Editor\Unity.exe"))
    return hits[0] if hits else UNITY_FALLBACK


def run_unity(method, import_dir, tag):
    log = os.path.join(tempfile.gettempdir(), "musicgame_addsong_%s.log" % tag)
    exe = unity_exe()
    print("[unity] %s → %s" % (method, os.path.basename(import_dir)))
    p = subprocess.run([exe, "-batchmode", "-projectPath", "E:/MusicGame",
                        "-executeMethod", method, "-importDir", import_dir,
                        "-logFile", log], capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    lines = [l.strip() for l in open(log, encoding="utf-8", errors="replace")
             if ("PASS" in l or "FAIL" in l or "BPM=" in l or "MvCheck]" in l
                 or "error CS" in l or "OfflineImport]" in l)]
    for l in lines:
        print("    " + l)
    if p.returncode != 0:
        sys.exit("FAIL: Unity %s 退出码 %d（日志 %s）" % (method, p.returncode, log))


# ---------------------------------------------------------------- 主流程

def main():
    global FF
    FF = ffmpeg_exe()
    ap = argparse.ArgumentParser()
    ap.add_argument("--audio", required=True)
    ap.add_argument("--mv")
    ap.add_argument("--krc")
    ap.add_argument("--lrc")
    ap.add_argument("--title")
    ap.add_argument("--kugou", default=r"C:\Kugou")
    ap.add_argument("--no-mv", action="store_true")
    ap.add_argument("--skip-sync", action="store_true")
    ap.add_argument("--no-import", action="store_true")
    ap.add_argument("--no-verify", action="store_true")
    args = ap.parse_args()

    title = args.title or os.path.splitext(os.path.basename(args.audio))[0]
    print("== 入库：%s" % title)

    # 1) 音频
    audio_src = prepare_audio(args.audio, title)
    song_id = "song_" + md5_10(audio_src)
    song_dir = os.path.join(SONGS_ROOT, song_id)
    os.makedirs(song_dir, exist_ok=True)
    audio_ext = os.path.splitext(audio_src)[1].lower()
    audio_dst = os.path.join(song_dir, "audio" + audio_ext)
    if not os.path.isfile(audio_dst):
        shutil.copyfile(audio_src, audio_dst)
    print("[audio] %s (%.2fs) id=%s" % (os.path.basename(audio_dst),
                                        probe_duration(audio_dst), song_id))

    # 2) 歌词
    lrc_note = prepare_lrc(args, title, song_dir)
    print("[lrc] " + (lrc_note or "无"))

    # 3) MV
    mv_src = args.mv
    if not args.no_mv:
        if not mv_src:
            for pat in ("*.mkv", "*.mp4", "*.flv"):
                hits = sorted(glob.glob(os.path.join(args.kugou, glob.escape(title) + pat)))
                if hits:
                    mv_src = hits[0]
                    break
        if mv_src:
            mv_dst = os.path.join(song_dir, "mv.mp4")
            if not os.path.isfile(mv_dst):
                prepare_mv(mv_src, mv_dst)
            print("[mv] mv.mp4 %.1fMB (%.2fs)" % (os.path.getsize(mv_dst) / 1e6,
                                                  probe_duration(mv_dst)))
        else:
            print("[mv] 没找到 MV（--mv 指定或 --no-mv 显式跳过）")

    # 4) 对齐量测（用源 MV 的音轨；转封装/重编码不改时间轴）
    mv_start = 0.0
    mv_path_in_dir = os.path.join(song_dir, "mv.mp4")
    if not args.no_mv and not args.skip_sync and os.path.isfile(mv_path_in_dir):
        src_audio_for_sync = mv_src or mv_path_in_dir
        print("[sync] 量测 MV↔歌曲偏移（%s）" % os.path.basename(src_audio_for_sync))
        offset, note = measure_offset(src_audio_for_sync, audio_dst)
        print("[sync] %s" % note)
        if offset is None:
            pass  # note 已说明原因：不设偏移
        elif offset <= -0.20:
            # MV 抢跑（缺歌头）：冻结首帧垫 |offset| 秒到片头，文件即天然对齐（负偏移没法 seek）
            pad = -offset
            tmp = mv_path_in_dir + ".pad.mp4"
            ffmpeg(["-i", mv_path_in_dir, "-vf",
                    "tpad=start_duration=%.2f:start_mode=clone" % pad,
                    "-c:v", "libx264", "-preset", "medium", "-crf", "21",
                    "-maxrate", "1000k", "-bufsize", "1500k", "-an",
                    "-movflags", "+faststart", "-y", tmp], "mv头部垫帧")
            os.replace(tmp, mv_path_in_dir)
            print("[sync] MV 缺歌头 %.2fs：已冻结首帧垫齐，MvStartSeconds=0" % pad)
        elif offset >= 0.20:
            mv_start = offset
            print("[sync] MvStartSeconds = %.2f" % mv_start)
        else:
            print("[sync] 偏移 %.2fs < 0.20s 阈值，不设" % offset)

    # 5) meta
    meta = {
        "Id": song_id,
        "Title": title,
        "AudioPath": audio_dst,
        "DurationSeconds": probe_duration(audio_dst),
        "Difficulties": [],
        "MvStartSeconds": mv_start,
    }
    with open(os.path.join(song_dir, "meta.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False, indent=4)
    for name in sorted(os.listdir(song_dir)):
        print("%12d  %s" % (os.path.getsize(os.path.join(song_dir, name)), name))

    # 6) 出谱 + 验收
    if not args.no_import:
        run_unity("MusicGame.EditorTools.OfflineImport.Run", song_dir, song_id + "_import")
    if not args.no_verify and not args.no_mv and os.path.isfile(mv_path_in_dir):
        run_unity("MusicGame.EditorTools.MvPlaybackCheck.Run", song_dir, song_id + "_mv")
    print("== 完成：%s" % song_dir)


if __name__ == "__main__":
    main()
