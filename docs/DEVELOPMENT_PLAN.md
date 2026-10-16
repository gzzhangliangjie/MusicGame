# MusicGame 开发计划

> 目标：一款 Unity 音游，玩家可导入本地任意音乐，自动生成谱面，支持多难度。
> 玩法参考：节奏大师（4 轨下落式）为主，数据模型预留太鼓模式扩展。
> 本文档创建于 2026-10-15，随项目推进持续更新。

---

## 一、参考项目调研结论

### 完整游戏（架构参考）

| 项目 | 技术 | 对我们的参考价值 |
|---|---|---|
| [YARG](https://github.com/YARC-Official/YARG) | Unity，活跃开源 | 大型 Unity 音游整体架构：歌曲库加载、判定系统、菜单流程、设置/校准 |
| [OpenTaiko](https://github.com/0auBSQ/OpenTaiko)（[TJAPlayer3](https://github.com/twopointzero/TJAPlayer3) 的后继） | C#（非 Unity） | 太鼓玩法完整实现：`.tja` 谱面格式、红/蓝/大音符/连打/气球、判定窗口设计。若做太鼓模式直接参考 |
| [beat-smash](https://github.com/fujinyumi/beat-smash) | Unity 游戏 + Python(madmom) 生成器 | **与本项目需求最接近**：导入音频→自动谱面→4 档难度(easy/medium/hard/impossible)。验证了"分析器 + 游戏本体"分离架构，但它依赖 Python 命令行，我们改为游戏内 C# 实现 |

### 自动谱面生成（算法参考）

| 项目 | 技术 | 参考价值 |
|---|---|---|
| [algorithmic-beat-mapping-unity](https://github.com/jesse-scam/algorithmic-beat-mapping-unity)（MIT） | Unity C#，频谱流(spectral flux) onset 检测 | **C# 分析管线的直接参考**：支持实时 + 整曲离线预处理；配套[讲解文章](https://medium.com/giant-scam/algorithmic-beat-mapping-in-unity-real-time-audio-analysis-using-the-unity-api-6e9595823ce4) |
| [BeatLearning](https://github.com/sedthh/BeatLearning)（Apache-2.0） | Python/PyTorch transformer | AI 生成谱面（目前仅 osu! circle），需 GPU，CPU 极慢。**远期可选后端**，不进 MVP |
| [Mapperatorinator](https://github.com/OliBomby/Mapperatorinator) | Python transformer | 同上，更新更强的 osu! AI mapper |
| [renpy-rhythm](https://github.com/RuolinZheng08/renpy-rhythm) | Python + aubio CLI | 备选离线方案：aubio onset 检测的用法示例 |

### DSP 理论/库
- Onset detection（频谱流）与 beat tracking 综述：[audioXpress: Beat Tracking Explained](https://audioxpress.com/article/audio-processing-beat-tracking-explained)
- [librosa](https://librosa.org/) / [Essentia](https://essentia.upf.edu/tutorial_rhythm_beatdetection.html) / aubio：Python 侧金标准，算法命名（spectral flux、DBN beat tracking）和参数可借鉴到 C# 实现
- [BTrack](https://github.com/adamstark/BTrack)：C++ beat tracker，可作原生插件备选

### 调研结论
1. "音频→谱面"的成熟管线 = **onset 检测 → BPM/节拍估计 → 网格量化 → 按强度+规则放音符**。业界（madmom/aubio/librosa）均走这条路，AI 方案是锦上添花。
2. 已有项目要么依赖 Python 环境（beat-smash），要么玩法形态不同（YARG/OpenTaiko 不自动生成谱面）。**"纯游戏内 C# 生成、导入即玩"是一个空档位**，技术上由 algorithmic-beat-mapping-unity 证明可行。
3. 因此技术路线定为：**全部用 C# 在游戏内离线分析**（无 Python 依赖、可打包到任意平台），分析结果缓存。

---

## 二、关键决策

| 决策点 | 选择 | 理由 |
|---|---|---|
| 玩法形态 | 4 轨下落式（类节奏大师），音符类型 Tap / Hold（后续可加双押、面） | 天然支持按密度调难度；PC 键盘(DFJK)与移动端触摸通用；数据模型 `lane 0-3` 可平滑扩展出太鼓模式（lane 映射红/蓝音色） |
| 分析实现 | 游戏内 C#：`AudioClip.GetData` 全量采样 → 自带 FFT → 分频带频谱流 onset → BPM 估计 | 用户导入即玩、零外部依赖；`GetSpectrumData` 只能实时听，不满足"先分析整曲再生成"的需求，所以离线路径用 GetData |
| 时钟 | 一切判定以 `AudioSettings.dspTime` 为准 | 与帧率、音频驱动对齐，音游铁律 |
| 谱面格式 | JSON（带版本号），按 `(歌曲哈希, 难度)` 缓存 | 可调试、可手改；同一首歌换难度不必重新分析音频（onset/BPM 中间结果单独缓存） |
| Unity 版本 | Unity 6 LTS + URP，2D 渲染 | 长期支持 |

---

## 三、谱面与中间数据格式（草案）

```
Songs/<hash>/
  meta.json        # 原始音频路径、标题、时长、分析参数版本
  analysis.json    # 中间结果：bpm、offset、onsets[] {t(ms), strength, band}
  chart_easy.json  # 各难度谱面
  chart_normal.json
  chart_hard.json
  chart_expert.json

note: { "t": 秒, "lane": 0-3, "type": "tap"|"hold", "dur": 秒(hold) }
```

难度由同一份 `analysis.json` 派生，参数化生成（见 Phase 4）。

---

## 四、阶段计划

### Phase 0 — 项目初始化（0.5 天）
- [ ] git init；Unity 6 LTS 新建工程（URP）
- [ ] 目录结构：`Scripts/{Audio,Analysis,Chart,Gameplay,UI}`、`Resources`、`Songs`
- [ ] 接入基础输入（键盘 DFJK + 触摸）

### Phase 1 — 核心玩法骨架（用手工测试谱，1 周）
- [ ] `Conductor`：dspTime 时钟、BPM、谱面时间轴换算
- [ ] `NoteSpawner` + 对象池：按提前量(NOTE_TRAVEL_TIME)生成下落音符
- [ ] 判定器：按键→查最近 note→窗口判定 Perfect(±40ms)/Great(±80ms)/Miss；Hold 头判+尾判
- [ ] Miss 自动回收、音符到判定线的视觉对齐
- **验收：挂一张手写 JSON 谱，DFJK 可打，判定时间误差稳定**

### Phase 2 — 计分与反馈（3~4 天）
- [ ] Combo、加权得分、准确率统计、结算界面
- [ ] 判定文字/粒子反馈、打击音效（可选 hitsound）
- [ ] 暂停/重开、死亡/HP（先做无限模式）
- **验收：完整打完一屏结算**

### Phase 3 — 音频分析管线（核心，1~1.5 周）
- [ ] `AudioImporter`：运行时读本地 mp3/ogg/wav → AudioClip（`UnityWebRequestMultimedia`）
- [ ] 分帧 FFT（窗口 2048，hop 512，Hann 窗；FFT 参考 DSPLib 思路自实现）
- [ ] 分频带频谱流 onset 检测：低频(鼓)/中频(旋律)/高频(镲)三路各自 onset，带自适应阈值(均值+k·标准差)
- [ ] onset 强度归一化；BPM 估计：onset 间隔直方图 + 自相关（60–200 BPM 区间，八度歧义取接近 120 的分支）
- [ ] 全程后台线程 + `CancellationToken`；进度回调
- [ ] 调试场景：波形 + onset 强度曲线 + 检出点叠加显示（**调参必备**）
- **验收：任选 5 首风格不同的歌，onset 与人耳听感拍点基本吻合，BPM 估计正确**

### Phase 4 — 谱面生成器 + 难度系统（1 周）
- [ ] onset 量化到节拍网格（按难度用 1/4、1/8、1/16 网格；量化容差随难度收紧）
- [ ] 难度参数表（每档：最大音符密度 nps、最小同键间隔、hold 比例、双押概率、允许的 lane 跳跃距离）
- [ ] 选点策略：onset 强度排序 + 密度预算（强拍优先，弱拍按难度预算裁剪）
- [ ] 可玩性后处理：同 lane 最小间隔、避免反人类交叉配置、hold 期间不出新 note
- [ ] 四档：Easy / Normal / Hard / Expert（Expert+ 后续）
- **验收：同一首歌四档难度递进合理，Expert 无物理上打不了的段落**

### Phase 5 — 游戏流程与 UI（1 周）
- [ ] 主菜单 → 选曲（本地文件浏览/拖入）→ 分析进度条 → 难度选择 → 游戏 → 结算
- [ ] 已分析歌曲列表（缓存命中则秒进）
- [ ] 音频/输入延迟校准页（offset 微调）
- **验收：陌生用户不看说明能完成"导歌→玩→看成绩"闭环**

### Phase 6 — 打磨与扩展（持续）
- [ ] 打击特效、判定线演出、背景频谱可视化
- [ ] 太鼓模式皮肤/玩法（lane→红蓝映射、大音符=双押）
- [ ] 变速曲支持（分段 BPM）
- [ ] （可选）AI 生成后端：内嵌 BeatLearning/Mapperatorinator 推理或调用本地 Python 服务
- [ ] 移动端适配（触摸、性能）

---

## 五、风险与对策

| 风险 | 对策 |
|---|---|
| 谱面"手感"差（音符不像人写的） | Phase 3 的调试可视化场景贯穿始终；先保证拍点准，再调放置规则 |
| 人声/复杂混音下 onset 误检漏检 | 分频带独立阈值 + 中值滤波 + 最小间隔合并；参数对多风格曲集回归测试 |
| 变速/渐快歌曲单一 BPM 假设失效 | MVP 明确不支持并给出提示；后续做分段 BPM |
| 长歌曲分析卡 UI | 后台线程 + 进度条 + 结果缓存，二次进入不重算 |
| 判定手感因设备音频延迟不同 | dspTime 时钟 + 用户校准 offset |

## 六、里程碑

| 里程碑 | 内容 | 预估 |
|---|---|---|
| M1 | Phase 0-2：手工谱可玩、计分结算完整 | ~2 周 |
| M2 | Phase 3：任意歌曲自动分析出 onset/BPM | ~1.5 周 |
| M3 | Phase 4-5：「导入→生成→四难度游玩→结算」完整闭环 | ~2 周 |
| M4 | Phase 6：演出打磨、太鼓模式、扩展后端 | 持续 |
