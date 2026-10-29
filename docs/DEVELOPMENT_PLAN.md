# MusicGame 开发计划

> 目标：一款 Unity 音游，玩家可导入本地任意音乐，自动生成谱面，支持多难度。
> 玩法参考：节奏大师（4 轨下落式）为主，数据模型预留太鼓模式扩展。
> 本文档创建于 2026-10-15，随项目推进持续更新。

> **当前状态（2026-10-29 对齐）**：Phase 0–5 全部完成；Phase 6 完成 5/8。
> 已上线：四轨下落（判定线演出/皮肤/歌词卡拉OK/MV 背景）、太鼓咚咔（气球连击条）、
> osu! 缩圈（确定性走位）、AUTO 观赏（三模式通用、暂停热切换、不计纪录）、
> 下落速度五档、延迟校准、本地纪录。
> 频谱语义谱面：onset 按频段落轨（低频→左/咚）、持续音段驱动音乐 hold、双押跨频段。
> 剩余：变速曲（分段 BPM）、可选 AI 生成后端、移动端适配、分析调试可视化场景。

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
| 玩法形态 | 4 轨下落式（类节奏大师）为主，已扩展太鼓/缩圈模式；音符 Tap / Hold（hold=头+尾两次判定） | 天然支持按密度调难度；PC 键盘(DFJK)/鼠标/触屏通用；`lane 0-3` 数据模型平滑重映射出太鼓（低频→咚 高频→咔）；缩圈模式复用同一谱面时间轴摆位 |
| 分析实现 | 游戏内 C#：`AudioClip.GetData` 全量采样 → 自带 FFT → 分频带频谱流 onset + 持续音段 → BPM 估计 | 用户导入即玩、零外部依赖；`GetSpectrumData` 只能实时听，不满足"先分析整曲再生成"的需求，所以离线路径用 GetData |
| 时钟 | 一切判定以 `AudioSettings.dspTime` 为准 | 与帧率、音频驱动对齐，音游铁律 |
| 谱面格式 | JSON（`ChartIO`），按 `(歌曲哈希, 难度)` 缓存 | 可调试、可手改；同一首歌换难度不必重新分析音频（analysis.json 中间结果单独缓存） |
| Unity 版本 | **实际 Unity 2022.3 LTS，内置渲染 2D**（修订：原计划 Unity 6 + URP，实际工程创建时落在 2022.3，未构成瓶颈） | 长期支持；纯 2D 程序化贴图渲染，无需 SRP 特性 |

---

## 三、谱面与中间数据格式（草案）

```
Songs/<hash>/
  meta.json        # 原始音频路径、标题、时长、分析参数版本
  analysis.json    # 中间结果：bpm、offset、onsets[] {t(ms), strength, band}、sustains[] {t, duration, band}
  chart_easy.json  # 各难度谱面
  chart_normal.json
  chart_hard.json
  chart_expert.json

note: { "t": 秒, "lane": 0-3, "type": "tap"|"hold", "dur": 秒(hold) }
```

难度由同一份 `analysis.json` 派生，参数化生成（见 Phase 4）。

---

## 四、阶段计划

### Phase 0 — 项目初始化（0.5 天）【已完成】
- [x] git init；Unity 新建工程（实际落地 2022.3 LTS，2D 内置渲染——2026-10 更新：未用 Unity 6/URP，见下「关键决策」修订）
- [x] 目录结构：`Scripts/{Analysis,Core,Gameplay,UI,Pure}`、`Resources`、`Songs`（实际按模块细分，Pure/ 为不依赖 UnityEngine 的纯逻辑层）
- [x] 接入基础输入（键盘 DFJK + 鼠标点击 + 触摸，新旧 Input System 双分支）

### Phase 1 — 核心玩法骨架（用手工测试谱，1 周）【已完成】
- [x] `Conductor`：dspTime 时钟、BPM、谱面时间轴换算（暂停冻结/恢复、停滞兜底）
- [x] `NoteSpawner` + 对象池：按提前量生成下落音符（下落时间五档可调 0.5–1.6s）
- [x] 判定器：按键→查最近 note→窗口判定 Perfect(±40ms)/Good(±80ms)/Bad(±140ms)/Miss；Hold 头判+尾判
- [x] Miss 自动回收、音符到判定线的视觉对齐
- **验收：sample 手写 JSON 谱 DFJK 可打；HeadlessSmokeTest 无头回归常驻（Auto 全 Perfect / 无输入全 Miss 双口径）**

### Phase 2 — 计分与反馈（3~4 天）【已完成】
- [x] Combo（六档形态/断连消散/彩虹流彩）、百万分制加权得分、准确率统计、结算界面（评级美术字+判定统计列+NEW RECORD 横幅）
- [x] 判定艺术字弹窗/误差条/打击特效（osu!lazer 参数）；hitsound 未做（可选）
- [x] 暂停/重开（Esc 菜单含 AUTO 热切换）、无限模式（无 HP）
- **验收：完整打完一屏结算，纪录按 歌曲×模式×难度 落地 records.json**

### Phase 3 — 音频分析管线（核心，1~1.5 周）【已完成，含持续音段扩展 2026-10-29】
- [x] `AudioImporter`：运行时读本地 mp3/ogg/wav → AudioClip（`UnityWebRequestMultimedia`）
- [x] 分帧 FFT（窗口 2048，hop 512，Hann 窗；FFT 参考 DSPLib 思路自实现）
- [x] 分频带频谱流 onset 检测：低频(鼓)/中频(旋律)/高频(镲)三路各自 onset，带自适应阈值(均值+k·标准差)
- [x] onset 强度归一化；BPM 估计：onset 间隔直方图 + 自相关（60–200 BPM 区间，八度歧义取接近 120 的分支）
- [x] 全程后台线程 + 进度回调
- [x] **持续音段检测（2026-10-29 扩展）**：每频带能量曲线 + 全曲自适应阈值（均值+0.7σ），连续超阈 ≥0.3s 记为 Sustain（<0.12s 跌落合并，截断 6s），产出 `analysis.json#sustains` 供谱面 hold
- [ ] 调试场景：波形 + onset 强度曲线 + 检出点叠加显示（**调参必备**；暂以 AnalysisCore 纯逻辑自检替代）
- **验收（已完成）**：合成信号（440Hz 正弦/120BPM 脉冲轨）+ 真歌批量验证（11 首 onset/BPM/频段/持续段全过）

### Phase 4 — 谱面生成器 + 难度系统（1 周）【已完成，含频谱语义扩展 2026-10-29】
- [x] onset 量化到节拍网格（按难度用 1/4、1/8、1/16 网格；量化容差随难度收紧）
- [x] 难度参数表（每档：最大音符密度 nps、最小同键间隔、hold 比例、双押概率）
- [x] 选点策略：onset 强度排序 + 密度预算（强拍优先，弱拍按难度预算裁剪）
- [x] 可玩性后处理：同 lane 最小间隔、避免反人类交叉配置、hold 期间同 lane 不出重叠 note
- [x] 四档：Easy / Normal / Hard / Expert
- [x] **频谱语义（2026-10-29 扩展）**：分析器额外产出持续音段 `Sustains`（各频带能量持续超自适应阈值 ≥0.3s，用于 hold）；lane 按 onset 频段映射——**低音(底鼓/贝斯)→lane 0/1、中频(人声/旋律)→lane 1/2、高频(军鼓/踩镲)→lane 2/3**；音乐 hold 由同频段持续段驱动（时长跟随能量，允许跨 lane 出音符，并发 ≤2），无匹配回退间隙启发式；双押跨频段配对（低+高同拍）。太鼓模式经 lane 重映射后低频=咚(红)、高频=咔(蓝)，谱面获得乐器语义
- **验收（已完成）**：纯逻辑自检 13/13 PASS（合成信号频段判定/持续段提取/落轨断言）；真歌批量重建 11/11，sustain-onset 对齐率 100%，不同曲风落轨分布差异显著
- **工具**：`OfflineRebuildAll`（batchmode 批量重建全曲库，删旧缓存强制重分析）


### Phase 5 — 游戏流程与 UI（1 周）【已完成】
- [x] 主菜单（主题/皮肤切换、动效）→ 选曲（文件对话框/路径粘贴导入 + 缓存列表）→ 分析进度条 → 难度选择（三模式 + 速度/AUTO 开关 + 纪录面板）→ 游戏 → 结算
- [x] 已分析歌曲列表（缓存命中秒进难度选择；`OfflineRebuildAll` 批量重建）
- [x] 音频延迟校准页（offset 微调，PlayerPrefs 持久化）
- [x] 歌词（KRC→LRC 解析 + 卡拉OK 逐字渐亮/整字消散）；MV 背景打底（mv.mp4 存在自动挂载）
- **验收：导入→分析→游玩→结算闭环可用，全程鼠标可玩**

### Phase 6 — 打磨与扩展（持续）
- [x] 打击特效、判定线演出、背景频谱可视化
- [x] 太鼓模式（lane 重映射咚咔、气球连击条、鼓垫反馈；皮肤包）
- [x] osu! 缩圈模式（确定性走位、缩圈环点击判定，2026-10-29）
- [x] 缩圈圆环调大（视觉半径 0.7→0.9、点击半径 0.95→1.2、相邻圈最小间距 1.7→2.0 防重叠，2026-10-29）
- [x] 缩圈命中反馈/数字字体对齐太鼓（0.18s 敲扁回弹+大爆淡出 Perfect 2.1x；Impact 键帽字体+黑投影双层白字，2026-10-29）
- [x] 连击 HUD 加强存在感（数字 88→100 号+双层黑描边+投影，COMBO 小字换 Impact 26 号+描边+提亮；缩圈截图验收工具 CaptureOsuFlow 等 COMBO≥5 自动拍 vis_osu.png，2026-10-29）
- [x] 开局/暂停继续 3-2-1 倒计时（美术字数字+合成音效）：Conductor 增负时间预备区（leadIn，0 点才出声，冒烟测试直连 PlaySong 默认 0 不受影响），开局按乐曲时间对齐 3/2/1/GO（实测 -2.99/-1.99/-0.99/+0.01），恢复用挂钟 3s 倒数后真正解除暂停；Esc 在倒计时中=取消回暂停菜单；CaptureCountdownFlow 截图验收（vis_countdown.png，2026-10-29）
- [x] 修复 AUTO 热切换回归（倒计时中 Esc 只弹菜单未冻结游戏，热切 AUTO 会在背后偷跑整首谱面）：取消倒计时时同步 TryCallPauseApi(true)；CaptureAutoSwitchFlow 全链路回归（倒计时中暂停→时钟冻结断言→AUTO 开/关/开来回切→无偷跑→继续后接管 combo=2，vis_autoswitch.png，2026-10-29）；syntax_check.sh 补齐 Editor 程序集检查（SafeMode 事故：批量 sed 改 case 编号引入 CS0152 重复标签，运行时检查覆盖不到 Assets/Editor）
- [x] AUTO 观赏模式（三模式通用，暂停菜单热切换，成绩不计纪录，2026-10-28/29）
- [x] 下落速度五档设置（慢速 1.6s → 疾风 0.5s，缩圈合拢时间跟随，2026-10-29）
- [ ] 变速曲支持（分段 BPM）
- [ ] （可选）AI 生成后端：内嵌 BeatLearning/Mapperatorinator 推理或调用本地 Python 服务
- [ ] 移动端适配（触摸、性能）

---

## 五、风险与对策

| 风险 | 对策 |
|---|---|
| 谱面"手感"差（音符不像人写的） | ~~调试可视化场景~~（暂以 AnalysisCore 纯逻辑自检 + 真歌批量重建抽查替代）；先保证拍点准，再调放置规则 |
| 人声/复杂混音下 onset 误检漏检 | 分频带独立阈值 + 最小间隔合并；11 首风格各异真歌批量回归通过（sustain-onset 对齐率 100%） |
| 变速/渐快歌曲单一 BPM 假设失效 | MVP 明确不支持并给出提示；后续做分段 BPM（**唯一剩余的谱面质量项**） |
| 长歌曲分析卡 UI | 后台线程 + 进度条 + 结果缓存，二次进入不重算（274s 歌实测 4s 分析完） |
| 判定手感因设备音频延迟不同 | dspTime 时钟 + 用户校准 offset（校准页已交付） |

## 六、里程碑

| 里程碑 | 内容 | 预估 |
|---|---|---|
| M1 | Phase 0-2：手工谱可玩、计分结算完整 | ~2 周 |
| M2 | Phase 3：任意歌曲自动分析出 onset/BPM | ~1.5 周 |
| M3 | Phase 4-5：「导入→生成→四难度游玩→结算」完整闭环 | ~2 周 |
| M4 | Phase 6：演出打磨、太鼓模式、扩展后端 | 持续 |
