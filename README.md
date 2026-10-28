# MusicGame

一款 Unity 音游：导入本地任意音乐（mp3 / ogg / wav），游戏内自动分析并生成谱面，四档难度即选即玩。玩法为 4 轨下落式（类节奏大师），键盘 `D / F / J / K` 或触屏四轨输入。开发计划见 [docs/DEVELOPMENT_PLAN.md](docs/DEVELOPMENT_PLAN.md)。

---

## 1. 用 Unity 打开本工程

本目录就是 Unity 工程根（已含 `Assets/`）。首次打开：

1. 安装 [Unity Hub](https://unity.com/download)，通过 Hub 安装 **Unity 2022.3 LTS 或更新**（本工程已在 Unity 2022.3.57f1c2 下完成 batchmode 编译验证与无头冒烟测试；Unity 6 亦可，代码仅用标准 API）。
2. Hub → **Add**（添加本地工程）→ 选择本目录（`E:\MusicGame` 这一层，即包含 `Assets/` 的目录）。
3. 双击打开。首次打开 Unity 会自动生成本地缓存目录（`Library/`、`Temp/`、`Packages/`、`ProjectSettings/` 等）并导入脚本，耐心等待编译完成即可——这些生成物均不入库（见 `.gitignore`）。

## 2. 一键创建游戏场景

顶部菜单：**Tools → MusicGame → 一键创建游戏场景**

该菜单（`Assets/Editor/SceneBuilder.cs`）会自动完成：

- 新建场景：主相机（正交 2D）+ `EventSystem`
- `Canvas`：Screen Space Overlay + `CanvasScaler`（Scale With Screen Size，参考分辨率 **1080×1920** 竖屏）
- 挂载 `GameFlow`（完整流程 UI）、`GameManager`（对局逻辑）、`SongAnalyzer`（音频分析）
- 保存为 `Assets/Scenes/Main.unity` 并加入 Build Settings

成功后 Console 会输出 `[SceneBuilder] 场景创建完成...` 提示。之后直接打开 `Main` 场景点 **Play** 即可游玩。

## 3. 试玩内置示例谱（无声模式）

仓库自带手写测试谱 `Assets/Songs/sample/`（BPM 120，四档难度，无音频），用于验证判定与流程：

1. （首次）把 `Assets/Songs/sample/` 整个文件夹复制到工程根的 `Songs/` 下，即 `Songs/sample/`——游戏运行时的选曲列表扫描的是工程根 `Songs/` 目录（**当前仓库已复制好**，此步可跳过）。
2. 打开 `Assets/Scenes/Main.unity`，点击 **Play**。
3. 主菜单 → 选曲界面选择 **sample** → 选择难度（easy / normal / hard / expert）→ 开始。
4. 用 `D / F / J / K`（或触屏）击打四轨下落音符，长条（hold）需按住到尾部。

> **无声模式说明**：sample 谱的 `meta.json` 中 `AudioPath` 为空，游戏检测到后自动改用静音时钟驱动（`AudioSettings.dspTime` 基准不变）——**没有音乐声是正常的**，音符照常下落、判定与计分照常工作。判定窗口：Perfect ±40ms，Great ±80ms。

## 4. 导入自己的歌曲

1. Play 进入游戏：主菜单 → 选曲界面 → **导入本地音频**，选择一首 mp3 / ogg / wav。
2. 等待分析进度条完成（全曲频谱流 onset 检测 + BPM 估计，在后台线程进行，长歌约需数十秒，仅首次）。
3. 选择难度 Easy / Normal / Hard / Expert → 开始游玩。
4. 结算界面显示准确率 / 最大连击 / 得分 / 评级（S / A / B / C）。

分析结果与生成的谱面会缓存在工程根 `Songs/<歌曲ID>/`（`meta.json`、`analysis.json`、`chart_*.json`），同一首歌二次进入直接选难度秒开，不会重复分析。想重新生成，删除该歌曲的缓存目录再导入即可。

> 手感偏早/偏晚？主菜单进入**延迟校准**（Calibration），按节拍点击数次自动求平均 offset，保存在本地（PlayerPrefs 键 `MusicGame.AudioOffset`）。

### 4.1 一键入库新歌（酷狗格式，固化流程）

游戏内导入之外，工程自带批量入库工具 `tools/add_song.py`——把酷狗下载的加密音频/歌词/MV 一条命令做成可玩歌曲（含谱面与 MV 背景验收）：

```bash
python tools/add_song.py --audio "C:\Kugou\KugouMusic\王力宏 - 改变自己.kgma"
```

默认全链执行，幂等可重跑：解密（kgm/kgma→mp3/flac，flac 自动归一 320k mp3）→ KRC 转 LRC（自动在 `C:\Kugou\Lyric` 找同名）→ MV 转 H.264 mp4（HEVC 必须重编码、≤960×540、码率封顶、去音轨）→ **MV↔歌曲对齐量测**（能量包络互相关、分段一致性校验：偏移 ≥0.2s 写入 `meta.json` 的 `MvStartSeconds` 由播放端起播 seek；MV 缺歌头（负偏移）则冻结首帧垫齐；段间不一致判内部剪辑并告警人工复核）→ `Songs/song_<md5前10>/` 落库 → Unity 批处理出四档谱面 → MV 播放无头自检。

常用参数：`--mv` 指定 MV（缺省自动找）、`--title` 改显示名（需与 LRC 同名匹配）、`--no-mv`/`--skip-sync`/`--no-import`/`--no-verify` 跳过环节、`--kugou` 改搜索根。依赖：`pip install imageio-ffmpeg numpy`（解密用 `%USERPROFILE%\.agents\skills\kgm2mp3`，缺失时先手动解密再传 mp3）。

## 5. 目录结构

```
MusicGame/                      # Unity 工程根（用 Unity Hub 直接打开本目录）
├─ Assets/
│  ├─ Editor/
│  │  └─ SceneBuilder.cs       # 菜单一键创建场景（Tools/MusicGame/...）
│  ├─ Scripts/
│  │  ├─ Pure/                 # 纯 C# 层（禁止引用 UnityEngine，可被 dotnet 工程编译）
│  │  │  ├─ Model/ChartTypes.cs    # NoteData/ChartData/SongMeta/Onset/AnalysisResult
│  │  │  ├─ Dsp/                   # FFT、频谱流 onset 检测、BPM 估计
│  │  │  └─ Generator/             # 难度参数表 + 自动谱面生成器
│  │  ├─ Core/                 # ChartIO（JSON 读写）、Conductor（dspTime 时钟）、NotePool
│  │  ├─ Analysis/             # AudioImportService（本地音频导入）、SongAnalyzer
│  │  ├─ Gameplay/             # 判定/计分/音符生成/对局管理（GameManager）
│  │  └─ UI/                   # GameFlow 流程、ResultsScreen 结算、CalibrationScreen 校准
│  └─ Songs/sample/            # 内置手写测试谱（无声模式，四档难度）
├─ AnalysisCore/               # dotnet 控制台自检工程（编译/测试纯 C# 层，非 Unity 依赖）
├─ Songs/                      # 运行时谱面缓存 Songs/<songId>/（入库，勿删除）
├─ docs/DEVELOPMENT_PLAN.md    # 开发计划与阶段验收标准
├─ .gitignore                  # 忽略 Unity/dotnet 生成物，保留 Songs/ 与 AnalysisCore 源码
└─ README.md
```

## 6. 谱面 JSON 格式（简）

`Songs/<songId>/chart_<difficulty>.json`（与 `ChartData` 契约一致）：

```json
{
  "SongId": "sample",
  "Difficulty": "easy",
  "Bpm": 120.0,
  "OffsetSeconds": 0.0,
  "Notes": [
    { "Time": 2.0, "Lane": 0, "Type": "tap",  "Duration": 0.0 },
    { "Time": 4.0, "Lane": 1, "Type": "hold", "Duration": 2.0 }
  ]
}
```

- `Time`：音符判定时刻（秒，相对歌曲起点，升序）；`Lane`：0–3（左→右）。
- `Type`：`tap` | `hold`；`Duration` 仅 hold 有效（按住时长，秒）。
