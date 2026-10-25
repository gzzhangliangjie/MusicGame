# M4 参考：开源音游调研（美术效果 & 玩法逻辑）

> 2026-10-22 调研，全部仓库经 GitHub API 核实星数/许可证/活跃度。服务对象：M4（打击反馈、频谱可视化、变速曲、太鼓模式、移动端）。
> 使用原则：**玩法逻辑与视觉设计可以自由参考**；代码能否搬取决于许可证（见末节）。

## 一、最值得深读的三个

| 项目 | 技术栈 | 许可证 | 活跃度 | 为什么重要 |
|---|---|---|---|---|
| [osu!lazer](https://github.com/ppy/osu) | C#（osu!Framework，非 Unity） | **MIT** | 19.3k★，每日提交 | **mania 模式就是 4K/7K 下落式，与我们玩法完全同构**；判定管线、hold 头尾逻辑、皮肤系统是业界标杆。逻辑可 1:1 移植，许可证允许搬代码 |
| [Phira](https://github.com/TeamFlos/phira) | Rust + 自研渲染 | GPL-3.0 | 2.7k★，活跃 | Phigros 同源的判定线玩法，**特效公认最漂亮的开源音游**：判定线脉冲、打击涟漪、hold 渐变彩带、背景模糊压暗。美术设计头号参考（Rust 代码，看设计不看码） |
| [Cytoid](https://github.com/cytoid/cytoid) | **Unity** | GPL-3.0 | 1.1k★，活跃 | 开源、上架双端、触屏优先。玩法核心在 `engines/` 目录。最适合抄：多点触控处理、判定反馈 UX、Hold/Drag/Flick 多音符类型设计（Flick 是我们将来加音符类型的现成蓝本） |

## 二、分方向速查

### 玩法逻辑（判定/计分/音符类型/输入）
- **osu!lazer `osu.Game.Rulesets.Mania`**：判定窗口与加权、hold 头/尾/体内判定（对照我们的 JudgeSystem/NoteController）、按键遮罩、判定弹窗对象池（对照我们 NotePool 思路——判定 UI 也该池化）、hit-error 条（早晚误差可视化，校准页可加）。
- **Cytoid `engines/`**：触屏多点判定、扫描线模式、滑条类音符。
- **OpenTaiko**（[0auBSQ/OpenTaiko](https://github.com/0auBSQ/OpenTaiko)，MIT，565★）：M4 太鼓模式的直接参考——红/蓝/大音符/连打/气球判定与 `.tja` 谱面解析（MIT，可移植代码）。
- **YARG**（[YARC-Official/YARG](https://github.com/YARC-Official/YARG)，LGPL-3.0，1.1k★）：延迟校准引擎、歌曲库管理、群舞/舞台演出架构。

### 美术效果（VFX/演出/UI）
- **osu!lazer mania 皮肤系统**：每轨命中光柱（hit lighting）、判定爆炸（hit explosion）、hold 体发光、判定弹窗的 pop-in/out 缓动曲线、按键按压遮罩。官方 [Skinning Wiki](https://osu.ppy.sh/wiki/Skinning) 列全了所有皮肤元素——**等于一份音游美术清单规格书**，可当我们的美术需求文档用。
- **Phira**：判定线随节拍呼吸/脉冲、Perfect 涟漪、hold 渐变彩带、结算页演出、背景 blur+dim 处理。
- **Unity 实现手段**（对我们的工程最实用）：
  - 打击特效：sprite 序列帧 flipbook（移动端首选，配合对象池——复用现有 NotePool 模式建 EffectPool）或 Shuriken 粒子；
  - 判定线/轨道：URP 2D Renderer + Bloom 后处理即可出"发光"质感，不必上 VFX Graph（VFX Graph 桌面向け，参考 [vfx-particles topic](https://github.com/topics/vfx-particles)、[VFX Graph 打击特效教程](https://www.reddit.com/r/gamedev/comments/kcg80s/)）；
  - 背景频谱可视化：与 Phase 3 调试场景共用 FFT 产物（已有 `Fft`/onset 数据，M4 直接复用）。

### 谱面/生态（M4 之后）
- **节奏大师谱面抢救计划**（TapTap 社区 + GitHub 工具）：把节奏大师 `.imd` 原版谱转 4K 格式——将来加"导入经典谱面"功能的现成数据源。
- **[rhythm-game-utilities](https://github.com/rhythm-game-utilities/rhythm-game-utilities)**（MIT，131★）：Unity/Godot 等多引擎音游工具集，谱面格式与辅助函数。
- **[Mivik/prpr](https://github.com/Mivik/prpr)**（GPL-3.0，Phira 前身）：Phigros 谱面播放器，代码较小适合通读。

## 三、许可证速查（能否搬代码）

| 许可证 | 项目 | 搬代码 |
|---|---|---|
| MIT | osu!lazer、osu!framework、OpenTaiko、rhythm-game-utilities | ✅ 可移植，保留版权声明 |
| LGPL-3.0 | YARG | ⚠️ 动态链接边界内可用，整段复制需谨慎 |
| GPL-3.0 | Cytoid、Phira、prpr | ❌ 不搬代码（会传染我们仓库）；**设计与视觉可自由借鉴** |
| 无许可证 | Unity-RhythmLane | ❌ 默认保留所有权利，只看思路 |

美术资源（皮肤、贴图、字体）一律各自带版权，**只借鉴风格，不扒资源**。

## 四、对照我们的 M4 路线：谁家有现成答案

| M4 计划项 | 首选参考 | 具体抄什么 |
|---|---|---|
| 打击/判定反馈 | osu!lazer mania | hit explosion + lane lighting + 判定弹窗池化与缓动 |
| 背景频谱可视化 | Phira + 自有 FFT | 判定线呼吸、低频驱动的背景明暗/缩放（用现有 onset/Band 数据） |
| 延迟校准升级 | osu!lazer + YARG | hit-error 实时误差条、校准结果可视化 |
| 太鼓模式 | OpenTaiko（MIT） | 判定规则与 .tja 解析直接移植 |
| Flick/Drag 音符类型 | Cytoid（Unity） | 触屏手势判定与音符类型架构 |
| 变速曲（分段 BPM） | osu!lazer | Beatmap 控制点（timing point）体系 |
