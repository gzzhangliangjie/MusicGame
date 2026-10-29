# 自动化自测体系

> 本项目没有使用现成测试框架（无 Unity Test Framework / NUnit），整套自测是围绕
> Unity 命令行 + 手写状态机 + Win32 输入驱动搭的轻量流程。本文档沉淀全部工具、
> 用法、判定循环与踩坑教训，供后续维护与新增用例参考。
> 创建于 2026-10-29。

## 总览：四层防线

| 层 | 工具 | 运行模式 | 测什么 | 单次耗时 |
|---|---|---|---|---|
| 0. 编译检查 | `tools/syntax_check.sh` | 纯命令行，不起 Unity | 全部 C# 源码能否编译（含 Editor 程序集） | ~10s |
| 1. 无头冒烟 | `HeadlessSmokeTest.cs` | `-batchmode` 无渲染 | 核心玩法链路：判定/时钟/结算/AUTO 全 Perfect | 1–3min |
| 2. 真实路径视觉验收 | `VisualCaptureTool.cs` | **开窗**（不 batchmode） | 用户完整路径：菜单→选曲→难度→玩法→截图+布局诊断 | 1–3min |
| 3. OS 级真实输入 | `tools/win_input.ps1` | 操作已开的窗口 | 输入链路本身：真实鼠标点击/键盘按键/截屏 | 秒级 |

辅助：数据管线工具见 §5（OfflineImport / OfflineRebuildAll / MV 自检）。

**通用判定循环**：Bash 起 Unity 进程 → 等退出码（0=过 / 1=挂）→ grep 日志标记
（`[Smoke] PASS|FAIL`、`[VisCap] ...`）→ 需要时打开截图 PNG 亲眼验。
日志命名惯例：`unity-<场景名>.log` 落工程根，一次跑一个名字。

Unity 路径：`D:\Unity\2022.3.57f1c2\Editor\Unity.exe`。

---

## 1. 离线编译检查（tools/syntax_check.sh）

**为什么存在**：工程锁互斥——编辑器或任一 Unity 实例开着时，第二个实例起不来，
改完代码没法立即编译验证。此脚本绕开 Unity 进程，直接调 Unity 自带 Roslyn
（`NetCoreRuntime/dotnet.exe` + `DotNetSdkRoslyn/csc.dll`）编译源码，引用对准
`Library/ScriptAssemblies/` 里的 DLL。

```bash
bash tools/syntax_check.sh   # 退出码 0=编译通过；错误输出截前 40 行
```

- 运行时（`Assets/Scripts`）与编辑器（`Assets/Editor`）**两个程序集分开编译**。
  教训：SafeMode 事故（CS0155 重复 case）只发生在 Editor 程序集，只查运行时会漏。
- 新引用了 Unity 模块（如新增 VideoModule）时，脚本里 REFS/EREFS 要补对应 `-r:`。

## 2. 无头冒烟（Assets/Editor/HeadlessSmokeTest.cs）

batchmode 下进 Play 模式真实跑完一首谱（Songs/sample 无声谱，44 音符含 3 hold，
判定总数 47），断言：判定事件数走满、时钟推进 >30s、OnSongFinished 回调且
Completed=true；AUTO 入口额外断言全 Perfect 零 Miss。

```bash
"D:\Unity\2022.3.57f1c2\Editor\Unity.exe" -batchmode -projectPath E:\MusicGame \
  -executeMethod MusicGame.EditorTools.HeadlessSmokeTest.AutoRun \
  -logFile unity-autosmoke.log
echo $?   # 0=PASS 1=FAIL
```

**注意不能加 `-quit`**：`Run` 只是请求进 Play 就返回了，`-quit` 会在进入 Play 前杀掉
进程；结束由测试自己 `EditorApplication.Exit(0/1)` 报码。

| 入口 | 场景 | 预期 |
|---|---|---|
| `Run` | 四轨，无输入 | 全自动 Miss，判定事件恰好 47 |
| `AutoRun` | 四轨 + AUTO | 47 全 Perfect、零 Miss |
| `AutoTaikoRun` | 太鼓 + AUTO | 覆盖轨道重映射、气球连击条（hold→lane2）充气尾判 |
| `OsuRun` | 缩圈，无输入 | 全 Miss，扫尾回收链路走满 |
| `AutoOsuRun` | 缩圈 + AUTO | 圆圈自动命中（含 hold 头/尾两圈）全 Perfect |
| `MuseRun` | 跑酷，无输入 | 全 Miss；判定总数按 `RemapChartForMuse` 去重后动态计算（Bootstrap 里重算，非固定 47） |
| `AutoMuseRun` | 跑酷 + AUTO | 地/空两轨自动命中 + 长条充气尾判，全 Perfect 零 Miss |

batchmode 要点：每帧手动 `EditorApplication.QueuePlayerLoopUpdate()` 推进播放循环
（协程/时间依赖它）；进 Play 前设 `DisableDomainReload`；测试对象（Camera、
GameManager）必须**进 Play 之后**创建——场景重载会销毁编辑期对象。

物理键盘无法无头模拟：DFJK 手感、真实点击的验收归 §3/§4。

## 3. 真实路径视觉验收（Assets/Editor/VisualCaptureTool.cs）

**为什么存在**：无头冒烟测的是 GameManager 链路，但连续 3 轮"已修复"假阳性的教训
表明：合成场景（代码 new Canvas）跑通 ≠ 用户路径跑通（Main.unity + GameFlow 有
主菜单盖层、Canvas 挂载差异）。可见性/布局/特效验收必须走用户完整路径，且
**开窗跑**（batchmode 不渲染，无画面可言）。

```bash
"D:\Unity\2022.3.57f1c2\Editor\Unity.exe" -projectPath E:\MusicGame \
  -executeMethod MusicGame.EditorTools.VisualCaptureTool.CaptureFlow \
  -logFile unity-flow.log
# 同样不加 -quit；结束自己 Exit 报码
```

| 入口 | 场景 / 产出 |
|---|---|
| `CaptureFlow` | 四轨全流程（点开始→选曲→easy→玩法中段），`vis_flow.png` + 几何诊断 |
| `CaptureCountdownFlow` | 开局 3-2-1 倒计时，`vis_countdown.png` |
| `CaptureTaikoFlow` | 太鼓模式全流程（含 Skin 预置 OpenTaiko） |
| `CaptureOsuFlow` | 缩圈 + 临时开 AUTO 等 COMBO≥5 再拍，结束**恢复用户 AUTO 设置** |
| `CaptureMuseFlow` | 跑酷模式 + 临时开 AUTO 等 COMBO≥5 再拍（地/空双锚点弹窗、长条充气条、角色跳斩），`vis_muse.png` + MuseRunner 视口诊断，结束恢复 AUTO 设置 |
| `CaptureAutoSwitchFlow` | 回归：倒计时中暂停→AUTO 来回切两次→断言时钟冻结/COMBO 不偷跑→继续后 AUTO 接管 |
| `CaptureMenu` | 主菜单入场动效后截图 |
| `CaptureDiffPage` | 窄窗 832x907 复现竖长 Game 视图，难度页布局诊断 |
| `CaptureRetryResidue` | 强制弹结算（NEW RECORD）→再点一次→断言 ResultsPanel 无残留 |
| `Capture` | 干净场景（不进 Main.unity）触发打击特效/弹窗/误差条截图 |
| `OpenForUser` | 打开 Main.unity 直接进 Play 交给用户试玩，不退出 |

实现骨架：`EditorApplication.update` 挂每帧泵跑显式状态机（`flowStep`），点按钮 =
`button.onClick.Invoke()`（与真实点击同 handler 入口，确定性、不依赖坐标）；诊断用
`GetWorldCorners` / `isVisible` / `WorldToViewportPoint` 输出数值进日志；截图用
`ScreenCapture.CaptureScreenshot`。够不着的私有成员用反射（`SetPaused`、`results`）。

## 4. OS 级真实输入（tools/win_input.ps1）

**为什么存在**：`onClick.Invoke()` 直达 handler，测不到 raycast、输入系统、键位绑定
本身——"按键没反应"类 bug 必须真实输入才能抓到。纯 Win32 P/Invoke
（`SetCursorPos`+`mouse_event`、`keybd_event`），编辑器 Game 视图与独立包通用。

```powershell
powershell -File tools/win_input.ps1 -cmd restore -pid2 <pid>  # 窗口钉 (0,0) 1707x1067
powershell -File tools/win_input.ps1 -cmd focus  -pid2 <pid>   # 抢前台
powershell -File tools/win_input.ps1 -cmd click  -x 640 -y 500 # 真实鼠标点击
powershell -File tools/win_input.ps1 -cmd key    -vk 44        # VK 0x44 = D 键
powershell -File tools/win_input.ps1 -cmd shotwin -pid2 <pid> -out shot.png  # 截 Unity 窗口
powershell -File tools/win_input.ps1 -cmd shot   -out full.png              # 截全屏
```

**坐标确定性三件套**（缺一不可）：

1. `SetProcessDPIAware()`——否则 DPI 缩放让所有坐标错位；
2. `restore` 把窗口 `MoveWindow` 钉到 (0,0) 固定尺寸——配合游戏内
   `Screen.SetResolution`，屏幕坐标才算得出来；
3. `focus` 里 `SetForegroundWindow` 前先发一次 Alt 按下/抬起（keybd_event 0x12）——
   绕过 Windows 前台锁限制。

查 Unity 实例 PID：`Get-CimInstance Win32_Process -Filter "Name='Unity.exe'"`
看命令行 `-projectPath`（wmic 已废弃）。

## 5. 数据管线工具（OfflineImport.cs 内五个类）

| 类 | 用途 |
|---|---|
| `OfflineImport.Run` | 单曲离线入库：`-importDir <歌曲目录>` 跑整曲分析+四档谱面，写 analysis.json / chart_*.json / 回填 meta（与游戏内导入同链路） |
| `OfflineRebuildAll.Run` | 曲库批量重建：扫描 Songs/ 全目录，**先删 analysis.json 缓存**再重分析（旧缓存无 Sustains 字段会静默走老逻辑，必须删缓存强制重跑） |
| `MvPlaybackCheck.Run` | MV 组件级自检：解码就绪/帧推进/RT 非单色/Pause/Resume |
| `GameplayMvIntegrationCheck.Run` | MV 链路级自检：走真 PlaySong 全链，抓组件检抓不住的"创建时机/挂靠层级"回归 |
| `SeekExperiment.Run` | 一次性诊断：VideoPlayer 四种 seek 手段逐 tick 打帧号，找出 2022.3 生效姿势 |

## 6. 踩坑教训（新增用例前先读）

1. **工程锁互斥**：任何 Unity 实例开着时起不了第二个。快速编译验证走 `syntax_check.sh`，
   别等编辑器。删 `Library/EditorInstance.json` 之类锁文件前必须先用
   `Get-CimInstance` 核对进程确实不在了。
2. **batchmode 不渲染、不能加 `-quit`**：视觉验收必须开窗；退出码由测试自己 `Exit` 报。
3. **真实路径假阳性**（3 轮教训）：合成环境跑通≠用户路径跑通。可见性验收必须
   Main.unity + GameFlow 全路径 + 数值诊断（viewport/isVisible），不能只看"没报错"。
4. **域重载**：进 Play 前设 `DisableDomainReload`，否则静态状态和 update 订阅被重置；
   测试对象必须进 Play 后创建。
5. **别等帧数，等游戏时钟**：编辑器帧率无上限，"等 N 帧"跨机器差几倍。状态机条件用
   `Conductor.SongTimeSeconds`，帧数只做超时兜底。
6. **截图时机**：`CaptureScreenshot` 是请求制，帧末才落盘——触发后多等 30 帧再退；
   拍动画中段提前 ~12 帧触发（如 COMBO≥5 后等 12 帧拍跳动中段）。
7. **失焦副作用**：编辑器窗口失焦可能误触 Esc 弹暂停——流程中检测到暂停菜单自动点继续。
8. **onClick.Invoke ≠ 真实输入**：输入链路（raycast/键位绑定）的回归必须 OS 级真实输入。
   桌面端音游鼠标路径与键盘路径都要覆盖。
9. **测试污染要恢复**：临时改 PlayerPrefs（AutoMode/Skin）的用例结束时必须存原值恢复
   （参考 `CaptureOsuFlow` 的 `flowAutoPrefRestore`）。
10. **分析缓存会骗人**：改了分析/生成逻辑，旧 analysis.json 不带新字段会静默走老路——
    验收用例要么删缓存重跑，要么用 `OfflineRebuildAll` 全量重建后再验。

### 居中与坐标专题（缩圈模式连环案，2026-10-29）

11. **算出来的坐标必须落盘到对象**（最大总根源）：走位算法辛苦算出圈心 `pos`，
    但 Entry 没有位置字段、Spawn 也不设置 transform.position——所有圈自首日起
    同心叠在原点生成。数字互叠、"偏左上/偏右下"、居中争议全是它的下游症状，
    修了多轮表象才挖到根因。教训：新摆位/布局逻辑落地时，第一步就验证
    "位置数据从计算端流动到了渲染端"（临时 Log 圈心坐标即可秒判）。
12. **TextMesh MiddleCenter 是步进盒居中，不是墨迹居中**：数字墨迹在步进盒内
    不居中（"1" 墨迹偏右）。且 `MeshRenderer.bounds` 量的也是步进盒——拿它做
    居中探针会得到"偏差 0,0"的假阳性。像素级居中两条正路：CharacterInfo 水平
    精确补偿（垂直行盒度量不公开，别硬猜），或墨迹裁剪 Sprite（pivot=墨迹中心，
    构造性居中）。
13. **补偿参数先写方向语义再动手**：垂直"偏下 13px"应向上补偿（Unity +Y 向上），
    符号写反会越调越低、来回震荡。改补偿常量前注明"实测偏 X → 沿反方向调"。
14. **跨层级复用偏移常量要除以累计父级缩放**：太鼓 1.0x 空间的投影偏移
    (0.025,-0.035) 照抄进 1.8x 圈体子级，实际偏移放大 1.8 倍——"偏右下"元凶之一。
15. **逐字（per-glyph）补偿会引入左右乱偏**：CharacterInfo 水平补偿把不同数字
    推向不同方向，用户观感"1 2 3 4 位置有左有右"。优先统一基准位（所有数字
    同一点），残差超阈值才升级逐字方案。
16. **密集谱面下动画必然叠**：命中爆开 2.1x/0.18s、判定弹窗压圈心 0.75s，
    密集段必然糊住下一个圈。判定即消失（无动画）+ 弹窗抬顶缩短命，是用户
    拍板的终态——给"看得见的内容"加演出前先想清楚它消隐时会不会砸到后面。
17. **Play 中域重载失败 = 输入系统瘫痪**：自动刷新在改到一半的文件上触发
    重编译会失败，之后旧程序集带着坏 EventSystem 状态继续跑（所有按钮不响应，
    必须重启编辑器）。改代码要一批完整提交，别让编辑器在半成品上刷新；
    批量重构（sed 改 switch case 等）前先停掉可能自动刷新的编辑器实例。
18. **跨程序集 `internal` 不可见报的是 CS0117 不是 CS0122**（跑酷模式撞上）：
    `Assembly-CSharp-Editor`（冒烟/截图工具）引用 `Assembly-CSharp`，运行时类里
    `internal static` 成员在 Editor 侧报"类型未包含成员定义"。Editor 测试要调的
    运行时 API（如 `GameManager.RemapChartForMuse`）必须 `public`。
    另外 `syntax_check.sh` 的 Editor 程序集引用的是 `Library/ScriptAssemblies/` 里的
    **旧 DLL**——刚改完运行时代码还没让 Unity 重编过时，Editor 段会报假 CS0117；
    此时以运行时段结果为准，跑一次 batchmode 冒烟（顺带重编 DLL）后再复查。
19. **"创建即赋值"字段漏赋 → 判定数莫名少 N**（MuseNote.EnsureVisuals 创建了
    headGo 却忘写 `head = headGo.transform`）：每次租用 NRE 只炸视觉装配，
    判定/计分链路与 Spawner 解耦照常走——长条怪永远进不了 Holding，尾判丢失，
    表现为"47 判定只走了 44"且零异常日志级别报错。冒烟测试对判定总数的
    精确断言正是抓这类 bug 的关键，别放宽成"≥某数"。
20. **重映射会改变判定总数**（跑酷去重）：`GameFlow` 结算总数与冒烟测试期望值
    都必须按**重映射后**的谱面统计，否则 FULL COMBO 判定失真、冒烟假失败。
    把重映射做成运行时类的 `public static` 纯函数，两端共用同一份实现。
21. **离线预览渲染必须对齐 Unity 的行序约定**（跑酷 CHR 预览踩坑）：`SetPixels` 的
    颜色数组行 0 = 贴图**底部**，而 PIL/常见图像库行 0 = 顶部——预览工具直接按数组序
    画会整体上下颠倒，误判成"游戏贴图翻转"。绘制时按 `(h-1-y)` 翻转。同类坑：
    TextMesh 的 MiddleCenter 按**行盒**居中（含降部空间），Impact 字形实测偏低
    ~0.1 单位，需经验偏移补偿；改文本位置先截图实测再调，别按想象猜方向。
22. **Git Bash 双引号内 `$_` 会被 bash 展开**（PowerShell 单行 `ForEach-Object {
    Stop-Process -Id $_ }` 直接报废）：批处理管道用 `Get-Process Unity | Stop-Process
    -Force` 这类免 `$_` 的写法，或整个命令用单引号包裹。

## 7. 新增验收场景怎么做

照 `VisualCaptureTool` 的模式抄一个入口 + 状态机：

```csharp
public static void CaptureMyCase()
{
    // ① 初始化全局状态 / PlayerPrefs（要恢复的先存原值）
    EditorSceneManager.OpenScene("Assets/Scenes/Main.unity");       // 走用户真实路径
    EditorSettings.enterPlayModeOptionsEnabled = true;
    EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
    Screen.SetResolution(1280, 720, false);                          // 固定分辨率
    EditorApplication.isPlaying = true;
    EditorApplication.update += MyPump;                              // 挂每帧泵
}

private static void MyPump()
{
    if (!EditorApplication.isPlaying) return;
    myTicks++;
    try
    {
        switch (myStep)
        {
            case 0: // 等 UI 就绪（GameObject.Find）或时钟到达（SongTimeSeconds）
                if (GameObject.Find("btnStart") != null && myTicks > 60)
                    { ClickButton("btnStart"); Advance("进选曲"); }   // Advance: step++/ticks=0/留日志
                else if (myTicks > 2000) { Debug.LogError("[VisCap] 超时"); EditorApplication.Exit(1); }
                break;
            // ... 每步一个 case：操作 → 断言 → 推进
        }
    }
    catch (System.Exception e) { Debug.LogError("[VisCap] 异常: " + e); EditorApplication.Exit(1); }
}
```

守则：条件优先游戏时钟、`ticks` 只做超时兜底；每步 `Debug.Log("[VisCap] ...")` 留轨迹；
断言失败立刻 `Exit(1)`；截图后多等 30 帧再退；动过 PlayerPrefs 一定恢复；日志文件名
`unity-<场景>.log` 用完即弃可随时删（已列入 .gitignore 视情况）。
