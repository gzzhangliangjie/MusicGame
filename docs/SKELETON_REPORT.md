# MusicGame 骨架交付报告（M1–M3）

- 交付日期：2026-10-16
- 工程根目录：`E:\MusicGame`（即 Unity 工程根，包含 `Assets/`）
- 面向读者：项目所有者（接下来在 Unity 中打开本工程）
- 对照文档：[DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md)（里程碑定义见其第六节，M1=Phase 0–2，M2=Phase 3，M3=Phase 4–5，M4=Phase 6）

---

## 1. 概览

本次交付为**纯代码 + 谱面数据 + 工程配置**的完整骨架：24 个 C# 脚本、1 份四难度手写测试谱、1 个 dotnet 自检工程、README 与 .gitignore。**不含任何需要 Unity 编辑器生成的资产**（场景由菜单工具一键生成，见第 4 节）。

与里程碑的对应关系：

| 里程碑 | 计划内容（DEVELOPMENT_PLAN.md:132-139） | 本次交付 |
|---|---|---|
| **M1**（Phase 0–2） | 手工谱可玩、计分结算完整 | **core** 模块（契约数据类 / JSON 读写 / dspTime 时钟 / 对象池）+ **gameplay** 模块（判定 ±40/±80ms、连击计分、下落与 hold、GameManager 串联、DFJK/触屏输入）+ **scaffold**（手写 sample 四难度谱、一键建场景工具、README、.gitignore） |
| **M2**（Phase 3） | 任意歌曲自动分析出 onset/BPM | **analysis** 模块：纯 DSP 三件套（FFT / 三频带频谱流 onset / BPM 估计）+ Unity 侧音频导入与后台分析（结果缓存 `Songs/<id>/analysis.json`）+ **AnalysisCore** dotnet 自检工程 |
| **M3**（Phase 4–5） | 导入→生成→四难度游玩→结算完整闭环 | **generator** 模块（四档难度参数表 + 参数化谱面生成管线）+ **ui** 模块（主菜单→选曲/导歌→分析进度→生成→难度选择→游玩→S/A/B/C 结算→Esc 暂停，另有延迟校准页） |

**本次未覆盖 / 未验证的内容**（如实说明）：

- **Unity 侧从未编译、从未实机运行**：生成与验证机器上没有 Unity 编辑器（见终审总评）。所有 Unity 层代码只做过静态核对与纯 C# 层等价验证。这是当前最大的不确定性来源，第 5 节的 9 项发现全部属于"未实机跑过"型问题。
- Phase 3 计划中的**调试可视化场景**（波形 + onset 强度曲线叠加，DEVELOPMENT_PLAN.md:96，标注"调参必备"）**未交付**——本次文件清单中没有对应文件。
- Phase 2 的判定文字/粒子反馈、打击音效未在交付摘要中出现，视为未做（计划中标注可选）。

---

## 2. 文件清单

以下所有文件均已确认存在于磁盘（本次报告撰写时用 `find E:/MusicGame/Assets -type f` 逐一核对，与交付清单一致）。

### Pure 层（不引用 UnityEngine，可被 dotnet 工程编译）

| 文件 | 用途 |
|---|---|
| `Assets/Scripts/Pure/Model/ChartTypes.cs` | 五个 `[Serializable]` 数据类（NoteData/ChartData/SongMeta/Onset/AnalysisResult），字段与契约逐字一致 |
| `Assets/Scripts/Pure/Dsp/Fft.cs` | 迭代 radix-2 FFT，含位反转与 twiddle 表缓存 |
| `Assets/Scripts/Pure/Dsp/SpectralFluxOnsetDetector.cs` | 三频带频谱流 onset 检测：Hann2048/hop512、均值+1.5σ 自适应阈值、跨频带 50ms 合并、强度归一化 0..1 |
| `Assets/Scripts/Pure/Dsp/BpmEstimator.cs` | BPM 估计：间隔直方图 + 包络自相关 + 120 中心 log 先验与显式八度消歧，输出范围 60–200 BPM |
| `Assets/Scripts/Pure/Generator/DifficultyProfile.cs` | 单档难度参数定义 |
| `Assets/Scripts/Pure/Generator/DifficultyProfiles.cs` | 四档参数表（easy 2nps → expert 10nps + 双押 + 更密 hold，同 lane 最小间隔单调收紧） |
| `Assets/Scripts/Pure/Generator/ChartGenerator.cs` | 生成管线：强度排序→网格量化→1 秒滑窗密度预算→时间序 lane 分配→可玩性后处理；随机带种子可复现，异常输入返回空谱不崩溃 |

### Core 层

| 文件 | 用途 |
|---|---|
| `Assets/Scripts/Core/ChartIO.cs` | JsonUtility 读写 ChartData/SongMeta/AnalysisResult（契约签名逐字保留，另三个类型经重载/命名方法支持），自动建目录、prettyPrint |
| `Assets/Scripts/Core/Conductor.cs` | 以 `AudioSettings.dspTime` 为唯一基准的时钟：Setup 记录起点、播放中换算/暂停冻结、BeatToTime 与 TimeToBeat 互逆、无 AudioSource 时静音时钟驱动 |
| `Assets/Scripts/Core/NotePool.cs` | 通用 GameObject 对象池：Rent 自动扩容、Return 防重复归还与外来对象、统一挂载与销毁清理 |

### Analysis 层

| 文件 | 用途 |
|---|---|
| `Assets/Scripts/Analysis/AudioImportService.cs` | 运行时导入本地 mp3/ogg/wav → AudioClip（UnityWebRequestMultimedia） |
| `Assets/Scripts/Analysis/SongAnalyzer.cs` | 主线程 GetData 混单声道 → Task.Run 后台分析 → ConcurrentQueue 回主线程回报进度/结果 → `Songs/<songId>/analysis.json` 缓存命中即返回 |

### Gameplay 层

| 文件 | 用途 |
|---|---|
| `Assets/Scripts/Gameplay/Judgement.cs` | Perfect / Great / Miss 判定枚举 |
| `Assets/Scripts/Gameplay/JudgeSystem.cs` | 按 lane 分队列查最近未判定音符头判（TryHit）+ 超窗自动 Miss（TryMiss）；±0.040/±0.080s 窗口常量在此 |
| `Assets/Scripts/Gameplay/ScoreSystem.cs` | Perfect=1.0/Great=0.65 权重的连击/准确率/百万分制得分（hold 记头+尾两次判定） |
| `Assets/Scripts/Gameplay/NoteSpawner.cs` | 按 travelTimeSeconds 提前生成、维护 NoteData→NoteController 映射、绘制轨道线+判定线（代码生成白色方块 sprite，hold 蓝色身体） |
| `Assets/Scripts/Gameplay/NoteController.cs` | 下落表现与 hold 尾判（早松按 |Δ| 判、按住到底 Perfect、Miss 变灰出屏回收） |
| `Assets/Scripts/Gameplay/GameManager.cs` | PlaySong 串联 ChartIO→音频导入→Conductor→生成→判定/计分→结算回调（PlayResult）；音频缺失时静音 clip 走无声谱模式；输入 DFJK + 触屏四轨（新旧 Input System 双条件编译）；判定时钟扣除 `PlayerPrefs[MusicGame.AudioOffset]` 校准偏移 |

### UI 层

| 文件 | 用途 |
|---|---|
| `Assets/Scripts/UI/UiKit.cs` | 纯代码 uGUI 构建工具（Panel/Label/按钮 + 统一主题 + Canvas/EventSystem 适配） |
| `Assets/Scripts/UI/GameFlow.cs` | 完整流程闭环：主菜单→选曲（文件对话框导入并复制到 Songs/ + 缓存列表扫描）→分析进度条→四档生成保存→难度选择→PlaySong→结算→Esc 暂停 |
| `Assets/Scripts/UI/ResultsScreen.cs` | 结算屏：准确率/最大连击/得分/S·A·B·C 评级/FULL COMBO |
| `Assets/Scripts/UI/CalibrationScreen.cs` | 120BPM 代码生成节拍的 8 次点击校准，写入 `PlayerPrefs MusicGame.AudioOffset` |

### Editor / 测试谱 / 工程配置

| 文件 | 用途 |
|---|---|
| `Assets/Editor/SceneBuilder.cs` | 菜单 **Tools → MusicGame → 一键创建游戏场景**：正交相机 + AudioListener + EventSystem + Canvas(1080×1920) + GameFlow/GameManager/SongAnalyzer，保存 `Assets/Scenes/Main.unity` 并入 Build Settings |
| `Assets/Songs/sample/meta.json` | 示例谱元数据（`AudioPath` 为空 → 无声模式；本次已读盘确认） |
| `Assets/Songs/sample/chart_{easy,normal,hard,expert}.json` | 手写四档测试谱（BPM120/Offset0，音符落在 1/16 网格、Time 严格升序） |
| `README.md` | Unity 打开步骤 / 一键建场景 / 无声试玩 / 导歌流程 / 目录与谱面格式说明 |
| `.gitignore` | 忽略 Unity/dotnet 生成物，显式保留 AnalysisCore 与 Songs/ |
| `AnalysisCore/`（csproj + Program.cs） | net9.0 零依赖自检工程：链接全部 Pure 源码，FFT/onset/BPM/生成器约束三组检查，任一失败 `Environment.Exit(1)` |
| `docs/DEVELOPMENT_PLAN.md` | 开发计划与验收标准（本报告对照基准） |

**sample 谱音符数（本次报告撰写时亲自用 python 读盘清点，与交付自报一致）**：easy 44（含 3 hold）、normal 72（3）、hard 91（6）、expert 91（6）；四档首音符均为 t=2.0s（大于 1.2s 下落时间，可完整下落）。

---

## 3. 验证结果

### 3.1 dotnet 纯 C# 层自检 —— **通过**

- **命令**：`cd AnalysisCore && dotnet run --project AnalysisCore.csproj`（dotnet SDK 9.0.304）
- **结果**：统一脚本执行时**通过**，期间**修复 1 轮**；本报告撰写时（2026-10-16）在交付机上**复跑一次，同样全部 PASS**，输出与记录一致：

```
=== MusicGame AnalysisCore self-check ===
[PASS] FFT: peak bin 654 -> 440.08 Hz (expected 440 +/- 1.35 Hz)
      detected 19 onsets, 0.499 .. 9.497 s
[PASS] Onsets: 19/19 within +/-30 ms of expected beat times (need >=18)
[PASS] BPM: 120.00 (expected 120 +/- 1)
[PASS] Chart 'easy': 40 notes, constraints hold (maxNps<=2.0, laneGap>=0.75s)
[PASS] Chart 'normal': 80 notes, constraints hold (maxNps<=4.0, laneGap>=0.35s)
[PASS] Chart 'hard': 102 notes, constraints hold (maxNps<=7.0, laneGap>=0.18s)
[PASS] Chart 'expert': 111 notes, constraints hold (maxNps<=10.0, laneGap>=0.12s)

SELF-CHECK PASSED
```

（说明：上表 easy/normal/hard/expert 的 40/80/102/111 音符数是自检中**生成器在合成数据上产出的谱**的约束校验，不是 `Assets/Songs/sample/` 手写谱的音符数，两者请勿混淆。）

**覆盖范围**：仅 Pure 层（DSP 算法正确性 + 生成器密度/间隔约束）。DSP 算法另经逐行等价的 Python 原型验证（120BPM 合成轨 19/19 onset 落 ±30ms、多噪声种子复验），期间修复了直方图边界、八度误折叠、±1bin 抖动三个真实缺陷。

### 3.2 Unity 侧编译 —— **未验证**

生成与验证机器上没有 Unity 编辑器，`Assets/` 下所有脚本（Core/Gameplay/Analysis 的 Unity 侧/UI/Editor）**从未经过 Unity 编译，也从未 Play 过**。已做的替代核对：纯层无 UnityEngine 引用 grep、花括号配平脚本、契约签名逐字比对、跨模块调用的源码交叉阅读。终审 9 项发现即源于静态审查（见 3.3），其中多数正是"未实机跑过"型问题——**首次真实编译与 Play 仍是必须由项目所有者完成的验收步骤**。

### 3.3 终审（静态代码审查）

- **发现总数：9**
- **已证实 5**：high × 2，medium × 3（均已由复核人二次独立核验成立，关键行号本次报告撰写时再次抽查确认无误）
- **未证实 4**：low × 4（静态审查提出、未经复核定论或依赖实机条件，详见 5.2）
- 终审总评要点：契约签名逐字一致、纯 C# 层质量高；两个 high（触屏分支编译失败、HUD 遮挡音符）**都会直接阻断游玩**，需在首次打开 Unity 前后优先处理；缺陷全部集中于 Unity 层。

---

## 4. 如何在 Unity 打开并跑通

> ⚠️ **2026-10-16 更新**：第 5.1 节 5 条已证实缺陷与 5.2 节 4 条 low **已全部当日修复**（见第 7 节修复记录），可直接按以下步骤打开工程。第 3 步不应再出现 CS0246。

1. **安装**：[Unity Hub](https://unity.com/download) → 安装 **Unity 6 LTS（6000.x）** 模块。
2. **打开工程**：Hub → **Add** → 选择 `E:\MusicGame`（含 `Assets/` 的这一层）→ 双击打开。首次打开 Unity 自动生成 `Library/`、`Packages/`、`ProjectSettings/` 等本地目录并导入脚本，等待编译完成即可（均不入库，见 `.gitignore`）。
3. **处理编译错误（如出现）**：若 Console 报 `CS0246: TouchControl`（GameManager.cs:262），按 5.1 第 1 条加一行 using 即可；这是终审已证实的确定性缺陷，不是环境问题。
4. **一键创建场景**：顶部菜单 **Tools → MusicGame → 一键创建游戏场景**。成功后 Console 输出 `[SceneBuilder] 场景创建完成...`，生成 `Assets/Scenes/Main.unity` 并入 Build Settings。
5. **试玩 sample 无声谱**：
   - 把 `Assets/Songs/sample/` 整个文件夹复制到工程根 `Songs/` 下（即 `Songs/sample/`——选曲列表扫描的是工程根 `Songs/`，该目录当前尚不存在，需先创建）；
   - 打开 `Main` 场景 → **Play** → 主菜单 → 选曲选 **sample** → 选难度 → 开始；
   - 用 `D / F / J / K` 击打（hold 按住到尾部）。**没有声音是正常的**：sample 的 `AudioPath` 为空，游戏自动改用静音时钟驱动（判定与计分照常，判定窗口 Perfect ±40ms / Great ±80ms）。
6. **导入自己的歌**：Play 状态下 主菜单 → 选曲 → **导入本地音频**（mp3/ogg/wav）→ 等分析进度条（后台线程，长歌数十秒，仅首次；结果缓存在 `Songs/<歌曲ID>/`，二次进入秒开）→ 选四档难度之一 → 游玩 → 结算（准确率/最大连击/得分/评级）。
7. **手感校准（可选）**：主菜单 → 延迟校准，按 120BPM 节拍点击 8 次，自动求平均写入本地偏移。

---

## 5. 已知问题与风险

### 5.1 已证实（终审核实成立，修复优先级从上到下）

| Severity | 位置 | 问题（一行） |
|---|---|---|
| **high** | `Assets/Scripts/Gameplay/GameManager.cs:262` | 触屏分支 `TouchControl` 缺 `using UnityEngine.InputSystem.Controls`，在默认 Unity 6（启用 Input System，`ENABLE_INPUT_SYSTEM` 生效）下 **CS0246 直接编译失败**；已用 dotnet 9.0.304 最小工程复现实证（加 using 后 0 错误）。修复：第 11 行处补一行 `using UnityEngine.InputSystem.Controls;`（位于 `#if` 块内） |
| **high** | `Assets/Scripts/UI/GameFlow.cs:115` | HUD 屏根 Panel 使用 `Theme.Bg`（α=0.98 近不透明，UiKit.cs:18）且画布为 ScreenSpaceOverlay+sortingOrder=100（UiKit.cs:59-60），**游戏中下落音符/判定线被完全遮住，视觉不可玩**；内层 hudBg 是全透明的（GameFlow.cs:228-229），证明本意应透明。修复：HUD 屏背景改全透明（对齐内层 hudBg 的做法） |
| medium | `Assets/Editor/SceneBuilder.cs:78` | 一键建场景无条件挂 `StandaloneInputModule`（旧输入模块）：当 Active Input Handling=仅新 Input System 时**每帧抛 InvalidOperationException 且 UI 输入失效**，且 UiKit.EnsureEventSystem 因场景已有 EventSystem 不再纠正（UiKit.cs:72-78）。修复：SceneBuilder 按 UiKit 同样的宏分支选择输入模块 |
| medium | `Assets/Scripts/Gameplay/NoteController.cs:129` | hold 头判超窗 Miss 时尾判永不结算：只记 1 次 Miss 而非声明的头+尾 2 次（GameManager.cs:139-141 口径），judgedCount 到不了 totalJudgements，**Accuracy 偏高失真**（主 Score 分母固定恰好不受影响）。修复：OnMissed 的 Falling 分支补一次尾判 Miss 登记 |
| medium | `Assets/Scripts/UI/ResultsScreen.cs:73` | FULL COMBO 判定用**音符数**对比 maxCombo，而 hold 记 2 次判定：含 hold 的谱即使中途 Miss 也可能**错误显示 FULL COMBO**（如 10 纯 hold 谱 Miss 首头判后 maxCombo=19≥10）。修复：改用 totalJudgements 口径比较 |

### 5.2 未证实（静态审查提出，severity 均 low，待实机验证后再定论）

| Severity | 位置 | 问题（一行） |
|---|---|---|
| low | `Assets/Scripts/Core/Conductor.cs:64` | 【未证实】时钟用 `Play()` 而非 `PlayScheduled(dspTime)`，出声延迟至下一音频缓冲，判定时钟或系统性偏早最多约一个 buffer（~20ms+），与"严格对齐"的自报不符 |
| low | `Assets/Scripts/UI/GameFlow.cs:897` | 【未证实】Esc 暂停为纯菜单覆盖层：音频/时钟/音符继续，暂停期间 Miss 持续累计（overlay 文案已声明，系规避主循环误判自然播完的妥协） |
| low | `Assets/Scripts/Gameplay/GameManager.cs:329` | 【未证实】BuildNotePrefab 的模板 GameObject 每局新建、从不销毁也不挂 runtimeRoot，每次 PlaySong 场景泄漏一个隐藏 NotePrefab（仅资源泄漏，无功能影响） |
| low | `Assets/Scripts/Analysis/SongAnalyzer.cs:47` | 【未证实】分析缓存目录按 CWD 相对路径解析，独立构建（CWD≠exe 目录）下与 GameFlow 按 Application.dataPath 推导的 Songs 根不一致，缓存命中失效、每次启动重分析（编辑器下两者一致，功能降级不崩溃） |

### 5.3 其他观察（本次报告撰写时直接发现，非终审项）

- 工程根存在一个 **0 字节的 `nul` 文件**（Windows 重定向误生成产物，`ls -la` 可见）：建议删除，避免误入库（`.gitignore` 未覆盖它）。
- 工程根 **`Songs/` 目录尚不存在**：首次试玩需按第 4 节第 5 步手动创建并复制 sample。
- 仓库不含 `Packages/manifest.json` 与 `ProjectSettings/`（按 README 设计，首开由 Unity 生成）：因此**工程的 Active Input Handling 实际取值无法预先确认**，5.1 第 1、3 条 medium 是否触发取决于此——但工程代码显式支持"仅新 Input System"配置，按该配置修复是安全下限。

---

## 6. 下一步建议（对照 DEVELOPMENT_PLAN.md 的 M4）

M4 = Phase 6（DEVELOPMENT_PLAN.md:113-118，持续进行）：演出打磨（打击特效/判定线演出/背景频谱可视化）、太鼓模式皮肤与玩法（lane→红蓝映射）、变速曲支持（分段 BPM）、可选 AI 生成后端、移动端适配。**在进入 M4 之前，建议按以下顺序收尾 M1–M3：**

1. **修复 5.1 的两条 high**（各一行级改动）——这是"能在 Unity 里打开并玩起来"的前置条件，不修则 M1 验收（"DFJK 可打"）无法开始。
2. **修复 5.1 三条 medium + 处理 5.3 杂项**，随后完成**首次真实编译与 Play**：跑一遍第 4 节全流程（sample 四档 + 导入一首真实歌曲），顺手核实/否证 5.2 的四条 low——它们全部需要实机才能定论。
3. **补 Phase 3 调试可视化场景**（波形 + onset 曲线叠加，计划中标注"调参必备"，本次未交付）：这也是 M4"背景频谱可视化"的直接前置。
4. 之后按 M4 优先级推进，建议顺序：打击/判定反馈（对齐 Phase 2 未做的可选项）→ 背景频谱可视化 → 变速曲（分段 BPM，计划风险表中明确 MVP 不支持需给出提示）→ 太鼓模式（数据模型已预留 lane 0-3 映射）→ 移动端适配 → 可选 AI 后端。

---

*本报告事实来源：交付模块自报摘要与统一脚本自检记录（2026-10-16）、终审发现清单（含复核记录），以及报告撰写时在交付机上的实地核验（文件存在性 `find`、5 项已证实发现的关键行号抽查、sample 谱 python 读盘清点、`dotnet run` 复跑自检）。Unity 侧编译与运行在本机上未执行，相关结论均以上述静态审查为限。*

---

## 7. 修复记录（2026-10-16，终审后主代理修复）

终审交付的 5 条已证实缺陷（5.1）与 4 条未证实 low（5.2）**已全部修复**，共 8 个文件 22 处编辑；修复后 `dotnet run --project AnalysisCore -c Release` 复跑**全部 PASS**（输出同 3.1），8 个改动文件花括号全部配平（GameManager.cs 圆括号 186 开/191 闭的差额经逐行核对为注释编号 `1)`–`5)` 所致，无害）。

| 缺陷 | 修复内容 |
|---|---|
| high：GameManager.cs TouchControl 编译失败 | `#if ENABLE_INPUT_SYSTEM` 块内补 `using UnityEngine.InputSystem.Controls;` |
| high：HUD 不透明背景遮挡音符 | `CreateScreen` 增加可选背景色参数；HUD 屏传全透明色并关闭 raycastTarget（菜单/结算等屏保持不透明） |
| medium：SceneBuilder 无条件挂 StandaloneInputModule | 改为与 `UiKit.EnsureEventSystem` 相同的宏分支（`ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER` 时用 `InputSystemUIInputModule`） |
| medium：hold 漏头丢尾判 | `NoteController.OnMissed` 的 Falling 分支对 hold 补记尾判 Miss，判定数口径对齐（hold=2） |
| medium：FULL COMBO 误判 | `GameFlow.StartGameRoutine` 的 `lastTotalNotes` 改为判定数口径（hold 计 2），ResultsScreen 比较语义随之正确 |
| low：时钟早于出声约一个 buffer | `Conductor.Setup` 改 `PlayScheduled(dspTime + 0.1s)`，时钟 0 点 = 计划出声时刻（预备期 SongTimeSeconds 为 -0.1..0，不影响判定） |
| low：Esc 暂停不冻结 | `GameManager` 新增 `SetPaused(bool)`/`IsPaused`：经 `Conductor.Pause/Resume` 同步冻结音频与时钟，并挂起判定输入、hold 尾判、自然播完/停滞判定；GameFlow 既有反射调用（TryCallPauseApi）直接命中，无需改动调用侧逻辑（仅更新注释与文案） |
| low：NotePrefab 模板泄漏 | 模板挂到 runtimeRoot 下，随 Cleanup 统一销毁 |
| low：分析缓存路径构建后漂移 | `SongAnalyzer` 缓存根改为按 `Application.dataPath` 推导，与 GameFlow/GameManager 同口径 |

**仍未验证**：Unity 侧编译与 Play（本机无 Unity 编辑器，同 3.2）。首次实机验收按第 4 节步骤执行即可。

---

## 8. 实机验证记录（2026-10-16，本机 Unity batchmode）

后续在交付机上发现已安装 **Unity 2022.3.57f1c2 LTS**，用 batchmode 完成了无需人工的实机验证（3.2 节的"未验证"至此大部分补上）：

| 验证项 | 命令（要点） | 结果 |
|---|---|---|
| **全工程真实编译** | `Unity.exe -batchmode -quit -projectPath E:/MusicGame` | **exit=0，0 编译错误**（首轮抓到 1 个真实错误：`AudioType.OGG` 不存在，已改为 `AudioType.OGGVORBIS`——正是"未实机跑过"型缺陷，静态评审未发现） |
| **场景搭建工具实跑** | `-executeMethod MusicGame.EditorTools.SceneBuilder.BuildMainScene` | exit=0，日志确认 `场景创建完成并已保存到 Assets/Scenes/Main.unity`，文件已落盘（含 .meta） |
| **Play 模式核心链路冒烟** | `-executeMethod MusicGame.EditorTools.HeadlessSmokeTest.Run`（无声模式打 sample easy，无输入） | **PASS（exit=0）**：判定事件 **47/47**（44 头判 + 3 hold 尾判，验证判定循环/生成下落/自动 Miss/尾判补记）、Conductor 时钟推进至 45.0s 跑完整谱、`OnSongFinished` 正常回调且 `Completed=True`、`Miss=47` 符合无输入预期、用时 46s |

冒烟测试脚本保留在 `Assets/Editor/HeadlessSmokeTest.cs`，可随时复跑（batchmode 进 Play 需禁用域重载，脚本内已处理；游戏对象须在进入 Play 后创建，编辑期对象会随场景重载销毁）。

**剩余需真人完成的部分**：DFJK 物理按键手感与命中反馈观感、导入真实 mp3 走 GameFlow 完整 UI 闭环（选曲点击 → 分析进度 → 难度 → 结算界面）、视觉布局——无头环境无法模拟操作系统级键盘输入与主观体验。已知工程在 2022.3 下 Active Input Handling 默认为旧 Input Manager（相关代码均为双后端条件编译，两种配置均可编译）。
