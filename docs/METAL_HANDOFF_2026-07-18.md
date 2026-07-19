# Ryujinx macOS 原生 Metal 优化交接

更新时间：2026-07-18（Asia/Shanghai）

> **2026-07-18 晚间增补：v28 / v29 已完成。** 本节之后的原文描述的是 v27 停点。
> 晚间会话完成了原 P1/P2/P3 的主体工作，详见下面的增补章节 0。

## 0. v28 / v29 增补（2026-07-18 晚间）

### 0.1 根因与 v28（性能）

对照 Vulkan 后端逐行分析确认：v27 及之前的核心瓶颈**不是**拷贝路径、等待原语
或去重（这些与 Vulkan 等价，readback 直接读 unified memory 甚至更优），而是
**Metal 后端缺少 Vulkan 的 AutoFlushCounter 自适应提前提交机制**。deferred sync
的 fence 挂在正在录制的 command buffer 上，提交只发生在 sync 创建（batch=4）、
present 和内存压力处，guest 线程每次等待都要吃满"提交 + 大批量执行"的全延迟，
GPU 则在提交间隙空转（FIFO 33% 的直接解释）。

v28（commit `cbb82c7b`）把 AutoFlushCounter 移植到 Metal：

- render target 变更处：距上次提交 ≥1ms 且 ≥10 draw 时提交（与 Vulkan 同参数）。
- fast-flush 模式：近 20 帧平均 sync 等待 >1.5ms 进入，<0.1ms 退出；模式内
  draw 处每 1.5ms 提交一次。
- `RYUJINX_METAL_AUTO_FLUSH`：0=v27 行为（batch 回到 4），1=自适应（默认），
  2=强制 fast-flush。auto-flush 开启时 deferred batch 默认 16，仅作为无 draw
  段落的兜底。
- 同步统计增加等待时长分桶（<0.5 / 0.5–2 / 2–8 / ≥8ms + max）和 auto-flush
  触发计数。

**同一 v28 二进制、同配置、同场景（TOTK 自动引导到标题画面）的盲测 A/B：**

| 每 120 帧 | AUTO_FLUSH=0（v27 行为） | AUTO_FLUSH=1（v28 默认） |
| --- | --- | --- |
| 聚合等待 | 1095–1633ms | 618–750ms |
| forced flush（跨线程中断） | 90–157 | 7–10 |
| 等待时长分布 | 2–8ms 档为主（~220/370） | <0.5ms 档为主（~250/477） |
| 帧率 | 37–45 波动 | 44.6–44.8 稳定（顶满 45 mod 上限） |
| 阻塞线程（5s sample） | 3 个线程共 ~670/2375 样本在 waitUntilCompleted | 仅 MainThread 608/2576，worker 归零 |

缓存热度顺序偏袒 OFF 组（后跑、缓存更热），v28 优势只会被低估。
证据：`artifacts/diagnostics/v28-auto-flush/`（两组完整日志 + 两组 5s sample）。

注意：引导/开场动画阶段（约 0:40–5:30）等待 4–6s/120 帧属于资产流送 + 首次
PSO 编译，两组都一样，不能当作对比数据；只能比进入标题后的稳态窗口。

### 0.2 v29（画质正确性）

画质对照分析（Metal vs Vulkan 全量代码对照）结论按可信度排序：

1. **已修复（v29，commit `bd4827f1`）：CAMetalLayer 从未设置 colorspace。**
   Vulkan/MoltenVK 会声明 sRGB surface，CoreAnimation 做色彩匹配；Metal 后端
   layer.colorspace 为空 → P3 广色域屏上 sRGB 内容按 P3 原色直显 → 整体过饱和、
   偏青绿、暗部更深。这是同时解释"偏青 + 压黑"的最小根因。v29 默认设置
   kCGColorSpaceSRGB，设置里的 Color Space Passthrough 开关现在真正生效
   （开 = 清空 colorspace = 旧行为）。运行日志应有 `Metal layer color space: sRGB.`。
2. **已修复（v29）**：`B10G10R10A2Unorm` 此前映射缺失（落到 Invalid），补映射到
   `MTLPixelFormat.BGR10A2Unorm`。TOTK 当前日志未见触发，属防御性修复。
3. **已证伪**："FSR RCAS 在错误色彩空间锐化"的假设不成立——v29 的 present 日志
   显示 present 源纹理是 `R8G8B8A8Unorm`（非 sRGB），RCAS 本来就在编码空间运行，
   与 Vulkan 的 unorm-view 做法一致。present 日志现在会打印源格式以备核查。
4. **未处理（后续方向）**：草地/岩石细节"过硬/发糊"更可能来自
   `supportsTextureGatherOffsets: false` 引发的 PCF/gather 回退（Vulkan 侧 MoltenVK
   报 true）、border color 强制 OpaqueBlack 回退，以及 occlusion query 桩实现
   （`ReportCounter` 对 SamplesPassed 恒返回 1，可能影响剔除/光晕类效果）。
   sampler 无条件设置 CompareFunction 也值得核查。均在
   `src/Ryujinx.Graphics.Metal/MetalRenderer.cs` / `SamplerHolder.cs`。

### 0.3 候选与验证状态

| 目录 | 内容 | 状态 |
| --- | --- | --- |
| `artifacts/terminal/Ryujinx-metal-v28-auto-flush` | v27 + auto-flush | 无人值守冒烟通过：TOTK 引导到标题，6.5 分钟无崩溃、零 shader 错误，标题稳态 44.8 FPS |
| `artifacts/terminal/Ryujinx-metal-v29-color-correct` | v28 + sRGB colorspace + 格式映射 | 冒烟通过（见 BUILD-INFO），画质效果待用户目测确认 |

两个候选都保留 v27 的 ad-hoc 签名 apphost（CDHash `e969e75f...`，hypervisor
entitlement 不变），只替换 first-party 托管程序集，版本参数 1.3.3.0 已用
strings 验证。v25–v27 目录未动。

**尚未完成、需要用户参与的验证：**

1. MainField 同存档实测（静止/跑动/爆炸三场景）＋与 Vulkan 同场景对比 ——
   自动化只能到标题画面，MainField 需要手动读档。
2. v29 画质目测：偏青和压黑是否消失；若方向反了，切换图形设置里的
   Color Space Passthrough 再比。
3. 声音、mod、长时间稳定性按第 13 节清单复验。

**测试前必查配置**（本次会话已发现并修复过一次漂移：DRAM 被还原成 4GiB、
FSR 变回 Bilinear、各向异性变回 Auto；修复后为 8GiB / FSR 80 / 16x，
原配置已备份为 `Config.json.before-v28-restore-*`）。另注意：这个 fork 裸跑
`./Ryujinx` 会自动引导上次的游戏，`-g Metal` 是本次运行的图形后端覆盖参数。

### 0.4 提交记录

```text
cbb82c7b metal: add adaptive auto-flush submission scheduling
bd4827f1 metal: declare sRGB color space on the CAMetalLayer
bfd8e4cc metal: bound deferred sync latency during upload-heavy stretches
```

### 0.5 v29 MainField 实测与 v30（2026-07-18 深夜）

用户用 v29 实际进入 MainField 游玩（骑马夜间移动）：大部分 25–30 FPS，
跑动进入新场景时仍可掉到 ~15；截图 23.49 FPS / 42.58ms / FIFO 26.73%。
对实时会话日志（`~/Library/Logs/Ryujinx/`）和 5 秒 sample 的分析显示系统
呈**双模**：

- **安静片段：同步等待已经归零**（多个 120 帧窗口 0 waits；5s sample 中
  waitUntilCompleted 仅 9/2375 样本）。此时 host GPU ~27% 忙、后端线程
  ~70% 空闲、guest 线程主要在等游戏内部同步 —— 剩余天花板在模拟 CPU 的
  单线程关键路径，不在 Metal 后端。v28 的目标在稳态已全部达成。
- **流送突发（新场景加载）**：每 120 帧 2–5 秒等待、200+ 次 ≥8ms、
  forced flush ~99。原因：上传密集、draw 稀少，draw/attachment 两个
  auto-flush 触发器都不工作，只剩 batch-16 兜底，command buffer 又变大。

v30（commit `bfd8e4cc`，candidate `Ryujinx-metal-v30-stream-flush`）针对
后者：deferred sync 创建时若距上次提交 >1ms 且当前不在 render pass 内
（blit/compute/none encoder）则提交。正常渲染不会被此路径切分。预期
流送掉帧变浅变短；稳态行为不变。**组装时用户正在玩 v29，该候选尚未做
冒烟启动验证。**

**下一步的决定性测试（P0，用户操作）**：同一存档同一地点，Vulkan 与
Metal v30 各测一次（静止/跑动/新场景流送）。若该场景 Vulkan 也只有
25–30，则 Metal 已达平价，"Vulkan 30–45" 可能来自更轻的场景记忆；若
Vulkan 明显更高，则差距在模拟 CPU 关键路径与 Vulkan/Metal 的驱动开销
差异，需要新的 profiling 方向（GAL 命令处理、编码器重绑成本等）。

### 0.6 着色器缓存事故与共享缓存的重要事实（2026-07-18 深夜）

事故：用户在 Metal 会话尚未退出时启动了原装 Vulkan 版（22:21–22:22），
两个进程同时对共享的 `cache/shader/shared.data` 追加写入，互相踩踏，
第 23166 条记录的压缩算法字节被写成垃圾（69）。原装版每次启动加载到该
条即抛 `ArgumentException`（`Invalid compression algorithm "69"`）闪退
——该异常不在 ParallelDiskCacheLoader 的 catch 列表里，自愈路径没有机会
执行。修复：手工把 shared.toc 中该条的 offset 改为 0xFFFFFFFFFFFFFFFF，
触发 `DiskCacheLoadException`（可被接住）→ 原装版自动用已加载的前
23166 条重建缓存。修改前全套备份在
`games/0100F2C0115B6000/cache/shader-backup-corrupt-20260718/`。
commit `45d21716` 已把读侧 BeginCompression 改抛 `InvalidDataException`，
使同类损坏今后直接走自愈而不是闪退。

过程中确认的三个重要事实：

1. **CodeGenVersion 版本差**：原装 1.3.3 发行版是 7353；上游 master 在
   `c0078088`（非均匀索引支持）升到 7354，本分支继承 7354。共享的
   shared.toc 头由创建者盖章（当前为 7353）。版本不匹配的一方会跳过自己
   的 host cache 全量重编（并在完整加载时用自己的版本号重写缓存 →
   两个版本会互相反复重写）。**不要把本分支的常量改回 7353**——上游
   bump 是真实的 codegen 变更。
2. **本分支 Metal 路径实际走"受限加载"**（`RYUJINX_METAL_SHADER_CACHE_
   PRELOAD_LIMIT` 默认 0 → `IsLimitedLoad=true`）：启动时不预编译、
   不触发重建、也读不到坏记录——这就是 Metal 版一直"没事"而原装版闪退
   的原因，同时也意味着 Metal 的着色器都是游玩中按需编译的（卡顿来源
   之一，后续可评估配合 7354 host cache 做真正的预加载）。
3. **操作规矩**：绝不同时运行两个 Ryujinx 实例（共享缓存无写锁）；
   退出用界面正常退出，不要强杀进程（后台缓存写入器可能正在追加）。
   metal_apple.* 已随备份移出活动目录（重建后索引已错位且当前构建
   反正不读它）。

### 0.7 v31：同场景对测裁决与两项针对性修复（2026-07-19 凌晨）

用户完成了深穴同点位、同 mod（dFPS 45 上限）的双后端对测：

- **帧率**：Vulkan 44.8–44.9（顶满 mod 上限，FIFO 46–51%）vs
  Metal v30 30.7–32.4（FIFO 28–40%）。Metal 仍落后约 30%，未达平价。
  稳态同步等待已清零，剩余差距是新的 profiling 课题（后端线程编码成本、
  pass 切分、pacing 交互）。
- **画质**：整体色调两边已基本一致（v29 colorspace 修复被实测确认）。
  但 Metal 缺少发光蘑菇/植物，瘴气（gloom）不流动呈死红色。已排除
  FXAA（用户设置为 None）与 shader 转换失败（会话日志零错误）。

v31（candidate `Ryujinx-metal-v31-cache-indirect`）两项修复：

1. **独立着色器缓存目录**（commit `78a3bbdd`）：Metal 构建改用
   `cache/shader-metal`，与原装版的 `cache/shader` 彻底解耦，根治
   7353/7354 互相无效化和并发追加损坏两类事故；同时预加载默认改为
   完整加载（`RYUJINX_METAL_SHADER_CACHE_PRELOAD_LIMIT` 默认 -1，
   0 可回退旧的按需模式）。新缓存从空开始自然增长，首次游玩有按需
   编译卡顿，之后启动即预加载。
2. **停止谎报 indirect count 支持**（commit `e54c9a15`）：Metal 后端的
   `DrawIndexedIndirectCount` 实现忽略 GPU 写入的 count 缓冲、盲目循环
   maxDrawCount 次——TOTK 的植被/特效是 GPU 剔除 + indirect count 驱动，
   这正是蘑菇缺失/瘴气凝固的最大嫌疑。能力位改为 false 后，
   `MultiDrawElementsIndirectCount` 宏不再走 HLE，而由 Macro JIT 在模拟
   CPU 上读取真实 count 逐条绘制——与 Vulkan 在 Apple GPU 上（MoltenVK
   无 VK_KHR_draw_indirect_count）实际运行的路径完全一致，该路径已被
   证明画面正确且能到 45 帧。真正的 GPU 侧实现（compute 预处理 patch
   indirect buffer 或 ICB）留作后续优化。

v31 冒烟（无人值守）：shader-metal 目录正常创建、空缓存加载 0 条、
sRGB 生效、零致命错误。**待用户验证：深穴同点位蘑菇/瘴气是否恢复、
帧率变化（宏 JIT 路径每帧增加模拟 CPU 工作，需实测）。**

### 0.8 v31 实测、v32/v33 与绘制流取证（2026-07-19 凌晨）

v31 用户实测：植物可见性改善（indirect count 修复生效，远处可见），
但**走近或转动视角时整批闪烁/消失**；瘴气仍不流动；地面纹理仍有硬块
（部分归因于配置又被原装版回写：各向异性回到 Auto，已再次恢复 16 并
写入 0.6 节的注意事项）。个别点位帧率高于 v30（40.30 FPS @瘴气长廊），
不同点位 26–34。

v32（commit `2a0dae97`）：Metal WaitForIdle 物化恢复严格默认
（`RYUJINX_METAL_STRICT_WFI=0` 回退旧延迟模式）。理由：延迟模式是
auto-flush 之前的优化，现在收益归零，而宏 JIT 路径要在 CPU 读
GPU 写的数据。**用户实测：严格模式没有消除闪烁 → 时序理论被证伪**，
但严格默认保留（上游正确行为、开销可忽略）。

v33（commits `237f2f3f`+`16fcb1b6`，candidate `Ryujinx-metal-v33-capture`）：
触发文件式取证工具。教训：整设备 MTLCaptureManager 捕获会卡死帧管线
（16 个 draw 后停滞、gputrace 0 字节，进程空转），已改为只捕获主队列 +
新增零风险的纯日志绘制追踪（`touch /tmp/ryujinx-metal-trace` → 记录
3 帧全部绘制；gputrace 用 `/tmp/ryujinx-metal-capture` 且需
`METAL_CAPTURE_ENABLED=1` 启动）。

**双状态绘制流取证结论**（闪烁帧组 vs 稳定可见帧组，各 3 帧，证据在
`artifacts/diagnostics/v33-capture/`）：

- 植被以"4 顶点 quad × 数百至数千实例"的公告板方式绘制，采样每帧
  烘焙的 64×64 R11G11B10Float 替身图集（每帧数百个小绘制烘焙页面，
  数量逐帧摊销波动属正常）。
- **两状态的绘制流结构等价**：27 个实例化绘制两边恒定提交，实例总数
  仅随相机小幅变化（14.6k vs 15.4k）。闪烁帧组内部逐帧也稳定。
- 因此**排除"绘制未提交/剔除计数错误"**；问题在内容层——图集页面
  内容或实例数据在坏帧为空/错。哪个页面被采样取决于相机角度与 LOD
  距离，正好解释"转视角消失、走近消失"。

**下一轮建议（按优先级）**：

1. 重点审查 [TextureCopy.cs](../src/Ryujinx.Graphics.Metal/TextureCopy.cs)
   与纹理 view 的 slice/level 映射：烘焙→拷入图集→采样 链条中拷贝环节
   最可疑（页面级、相机相关的内容错误特征）。对照 Vulkan 的
   TextureCopy/CommandBufferScoped 实现。
2. 带 `METAL_CAPTURE_ENABLED=1` 启动 v33，在闪烁现场
   `touch /tmp/ryujinx-metal-capture` 抓队列范围 gputrace（已加固，
   未再实测），Xcode 打开检查 64×64 图集纹理内容与实例 buffer。
3. 瘴气不流动同为内容层问题，同一轮 capture 中检查其材质输入
   （scrolling noise 纹理 / 时间 uniform / 顶点动画数据）。
4. 帧率：Vulkan 44.8 vs Metal 30–40（同点位、同 mod dFPS 45 上限）。
   稳态同步已清零，差距在别处：同点位双后端 5s sample 对比后端线程
   忙闲与编码成本（注意 v33 起做过多次配置漂移修复，测前核对 0.6 节）。

### 0.9 v34 与内容层排除链收官（2026-07-19 下午）

上节建议 1、2 已执行完毕，结果如下：

- **gputrace 路线放弃**：GPUTraceDocument 捕获激活后 command buffer 停止
  完成、后端线程空转轮询完成状态，三种捕获范围（整设备 / 主队列 /
  队列不含 present）全部同样卡死（用户被迫强退三次）。结论记录在
  v33 BUILD-INFO。纯日志绘制追踪（`touch /tmp/ryujinx-metal-trace`）
  安全可用，是本轮取证的主要工具。
- **v34（commit `337bccac`）拷贝理论证伪**：Texture.CopyTo 确实有四个
  被注释掉的静默丢弃分支（MS↔非MS、bpp 不匹配、depth↔color），v34
  为它们加了去重告警日志——但用户在深穴实测**零命中**，该理论对本
  症状出局。日志保留，其他游戏/场景仍可能命中。顺带的真修复保留：
  blit 拷贝改走 identity-swizzle view（Metal 禁止对 swizzle view blit，
  release 下静默错误）。
- **已证伪清单（全部有实测/代码证据）**：绘制未提交（27 个实例化绘制
  两状态恒定）、剔除计数（v31 宏 JIT 路径）、WFI 时序（v32 严格模式
  无效果）、MSL 数组采样索引截断（IR 全程整数）、拷贝丢弃（v34 零
  命中）、swizzle view blit（修复后症状不变）。

**当前头号嫌疑（未验证，留给下一轮）：argument buffer 纹理地址陈旧。**
机制：[EncoderStateManager.cs](../src/Ryujinx.Graphics.Metal/EncoderStateManager.cs)
的 `RenderResourcesPrepass` 只在对应 Dirty 位被置位时才执行
`UpdateAndBind`（重写 argument buffer 里的纹理 GPU 地址 + 收集驻留
声明列表）。若纹理对象因失效/迁移被**重建**（MtlTexture 句柄改变）
而 GAL 层绑定未变（不触发 SetTextureAndSampler → 不置脏），argument
buffer 中保留**旧句柄地址**，采样读到已释放内存（空/垃圾），且新句柄
从未进入 `UseResources` 驻留列表。图集类每帧烘焙、频繁失效的纹理最易
命中；哪个页面坏取决于哪个纹理对象被重建 → 相机/LOD 相关。Vulkan 不
受影响：descriptor 每次绘制从当前 Auto<> 句柄重写。

验证/修复思路：
1. 在 Texture 失效/替换路径（Dispose/storage swap/CreateView 替换）
   打点，确认深穴场景确有"句柄重建但绑定未标脏"的序列；
2. 快速验证补丁：`RenderResourcesPrepass` 无视 Dirty 位、每次绘制
   全量 UpdateAndBind（性能会掉，但若闪烁消失即证实机制）；
3. 正式修复：绑定缓存记录句柄代际（Auto<> 的替换计数），Prepass 比对
   代际差异自动置脏；或纹理重建时向 EncoderStateManager 广播脏标记。
   参考 Vulkan DescriptorSetUpdater 的做法。
4. 瘴气凝固大概率同根因（动画层采样的 noise/位移纹理句柄陈旧 →
   永远读同一份旧内容）。

### 0.10 v35 探针证伪与深度转换新方向（2026-07-19 下午）

**v35（commit `a003702a`，candidate `Ryujinx-metal-v35-rebind-probe`）：
argument buffer 陈旧假说被证伪。** `RYUJINX_METAL_FULL_REBIND=1` 探针
（每次绘制无视脏标记、从当前句柄全量重写 argument buffer 与驻留列表）
下用户实测：植物依旧走近/转视角消失。0.9 节的绑定代际修复思路不必再做。

新方向依据（web 检索 + 能力对照）：

- "花草跑动时快速出现消失"是 Ryujinx 跑 TOTK 的**已知跨后端问题类**
  （GBAtemp 632372 帖）；Ryujinx 官方 2023-05 进度报告专门处理过 TOTK
  远处几何的深度精度 z-fighting。
- 本机 MoltenVK 启用了 `VK_EXT_depth_clip_control`（boot 日志可证），
  Vulkan 后端把 MinusOneToOne 深度模式交给驱动处理；Metal 后端报告
  不支持，走 shader 翻译器的顶点补偿（`EmitterContext.PrepareForVertexReturn`
  的 z' = z·0.5 + w·0.5）。两条路径的实现细节差异（应用位置、精度、
  与 YNegate/passthrough 阶段的交互）是深度边缘闪烁的候选来源。
- 已核对等价的部分：视口深度映射两端一致（都 clamp [0,1]）；深度模式
  判定启发式（StateUpdater.GetDepthMode）为上游共享逻辑。

**v36（commit `1b64724c`，candidate `Ryujinx-metal-v36-depthmode-diag`）**：
一次性日志 "Guest is using MinusOneToOne (OpenGL-style) depth clip
mode."（StateUpdater 置位规格状态处）。决定性问题：TOTK（尤其深穴）
是否使用该模式——用则审计补偿变换的覆盖面（顶点返回各路径、
passthrough/模拟阶段、YNegate 交互）；不用则此线出局，转 Xcode 附加式
GPU capture 看烘焙 pass（程序化捕获已证明不可行，见 0.9）。

**v36/v37 实测裁决（用户确认，2026-07-19）**：

1. TOTK **开机 ~7 秒即选择 MinusOneToOne 并全程使用**（v36 命中）。
2. **深度模式零翻转**（v37 计数器整局只有启动时的 flip #1）——
   "烘焙与主场景混合约定"理论出局。
3. 解析对照结论：补偿变换 + 视口映射的复合与 MoltenVK 的
   depth_clip_control 实现**窗口空间恒等**（连 slope bias 的输入都
   相等）。因此深度线只剩一种可能成立的形态：**某条着色器翻译路径
   漏掉了变换**（候选：tess eval、VertexA/B 拆分着色器的返回路径、
   ViewportTransformDisable 分支的交互）。这属于逐路径代码审计 +
   着色器级证据的工作。

**0.11 验证层一锤定音（2026-07-19 16:0x）**：用户按指引从 Xcode
`Debug Executable…` 启动（自动开启 Metal 验证层），验证层即刻反复断言：
`scissor rect (65535x65535) must be <= render pass (1920x1080)`。
后端把 guest 的全屏剪裁哨兵值原样传给 Metal，对 64×64 烘焙 pass 违规
最大。无验证层时属未定义行为（TBDR 上随机丢弃光栅化输出）——与全部
证据吻合（绘制提交正常、逐页内容为空、相机/LOD 相关、Vulkan 因
MoltenVK 内部钳制而免疫）。v38（commit `93c44024`，candidate
`Ryujinx-metal-v38-scissor-clamp`）在应用时按当前 pass 尺寸钳制剪裁，
guest 原值保留供各 pass 重新钳制。待用户验证蘑菇/瘴气/地面三症状。

**0.12 验证层方法论与 v39（2026-07-19 傍晚）**：v38 剪裁修复后症状未消，
但方法升级为**验证层全量清单**：`MTL_DEBUG_LAYER=1
MTL_DEBUG_LAYER_ERROR_MODE=nslog MTL_DEBUG_LAYER_WARNING_MODE=nslog`
启动即可拿到全部 API 违规而不在第一条断言处死亡（Xcode 附加反而会
卡在首个断言）。标题画面即扫出两类系统性违规，v39（commits
`161aa69c`+`ae5de72c`，candidate `Ryujinx-metal-v39-usage-simd`）修复：

1. **所有纹理以 `MTLTextureUsage.Unknown` 创建**（"usage must be set"，
   标题画面去重后 2414 条；TextureBuffer 补修后归零）。对 Unknown usage
   纹理做渲染目标/着色器写/格式重解释 view 均为未定义行为——间歇性
   空内容/旧内容的系统性来源。现按格式声明
   ShaderRead|PixelFormatView（+RenderTarget，非压缩彩色再 +ShaderWrite，
   texture buffer 为 ShaderRead|ShaderWrite）。
2. **compute 管线无条件承诺 ThreadGroupSizeIsMultipleOfThreadExecutionWidth**
   而游戏派发 1×1×1（"must be multiples of 32"）。违约=计算结果未定义
   ——TOTK 的 GPU 剔除/页表 compute 直接中招，与蘑菇/瘴气症状因果直连。
   现仅在 local size 为 32 倍数时承诺。

复测（同验证层参数）：usage/threadgroup/scissor 三类全部归零、无其他
断言类别。**教训入档：此类"硬件相关、间歇性、逐资源"的内容错误，
应第一时间开验证层拿全量清单，而不是逐个理论探针排除。**
待用户深穴实测 v39 的蘑菇/瘴气/地面三症状与整体回归。

**0.13 深穴验证清单与 v40（2026-07-19 傍晚）**：v39 深穴实测两症状仍在，
但用户的深穴验证层运行（832MB 日志，验证信息经 stderr 镜像自动进
~/Library/Logs 文件日志）交出两条现场专属违规，v40（commit `9ddf5f2f`，
candidate `Ryujinx-metal-v40-barrier-mips`）修复：

1. **compute 屏障 scope 非法 ×5547**：compute encoder 的 MemoryBarrier
   传了 render-only 的 RenderTargets scope → 屏障行为未定义（可能整条
   失效）→ compute 写入可被后续读取"读旧"——瘴气流动图/植被页表冻结的
   直接同步学机制。现仅 Buffers|Textures。
2. **mip 拷贝越界**：逐级减半不做逐级钳制，src/dst 基级不同时 4 宽
   拷进 2/1 宽小 mip → 小 mip 内容损坏（远处地面块状嫌疑）。现按两侧
   真实 mip 尺寸钳制（对齐 Vulkan）。

待用户 v40 实测：瘴气流动（本轮最强线索）、植物闪烁、远处地面质量；
可选验证层复扫确认两类归零。

**0.14 v40 证伪同步论，转向 MSL 翻译审计（2026-07-19 晚）**：用户 v40
实测"还是老样子"——五类验证层违规全部修复后三症状不变。随后代码侧
把同步面彻底排空：所有 MTLBuffer 均 storageModeShared + 默认 hazard
tracking（tracked，跨编码器自动依赖）、compute encoder 用默认
descriptor（串行 dispatch，编码器内天然有序）、render pass 全部
Load/Store、`BufferHolder` 的 CPU 读/写路径与 Vulkan 逐行同构。
**结论：GPU 时间线与 CPU↔GPU 边界的同步理论全部出局，最大剩余分歧面
= 自研 MSL codegen（Vulkan 走 SPIRV-Cross/MoltenVK）**。v41（commit
`23e0f36d`，candidate `Ryujinx-metal-v41-shader-trace`）为取证构建：

1. 绘制追踪每条带稳定程序标签（MSL 源 XXH3 前 16 hex），并把所到
   程序的 MSL 源转储到 `/tmp/ryujinx-metal-shaders/{label}-{stage}.metal`；
   compute dispatch 与间接绘制也纳入追踪。
2. GPU capture 加 3 秒看门狗（定时器线程强制 StopCapture）——此前三次
   卡死都是"到不了下一个 present → capture 永不结束"，现在自动解卡，
   不再需要强退。此前卡死发生在命令流每帧含数千条非法 scope 屏障 +
   全部纹理 Usage=Unknown 的时期，UB 清零后值得重试一次。

用户会话流程：`METAL_CAPTURE_ENABLED=1 OS_ACTIVITY_MODE=disable
./Ryujinx -g Metal` → 深穴点位 touch `/tmp/ryujinx-metal-capture`
（capture 重试）→ touch `/tmp/ryujinx-metal-trace`（植物可见态）→
再 touch 一次（闪烁态）→ 退出。产物：日志（含程序标签）、
`/tmp/ryujinx-metal-*.gputrace`（若成功）、`/tmp/ryujinx-metal-shaders/`。
后续离线：比对两态绘制的程序集合差异，审计公告板 vert/frag 与瘴气
材质及其上游 compute kernel 的 MSL。

**（历史记录）当时排定的下一步**：

1. **Xcode 附加式 GPU capture**（用户操作 ~10 分钟，信息量最大）：
   `METAL_CAPTURE_ENABLED=1` 启动任一候选 → Xcode → Debug →
   Attach to Process → Metal 相机图标抓 1 帧。附加式捕获由 Xcode 管理
   边界，不经过 FrameCapture 代码，不受 0.9 节卡死问题影响。看三点：
   公告板绘制绑定的图集纹理内容、其 fragment 的深度测试结果、
   烘焙 pass 的输出。
2. 着色器转储：设置里"图形着色器转储路径"填一个目录，深穴玩一分钟，
   转储的 guest 着色器可离线送进翻译器复现 MSL 输出，audit 变换覆盖。
3. 深度变换逐路径审计（纯代码侧，无需用户）。

## 1. 结论先行

当前分支已经恢复并适配了实验性原生 Metal 后端，能够在 Apple M1 Max 上启动
Ryujinx 1.3.3、加载《塞尔达传说：王国之泪》1.4.2、进入 MainField 并持续运行。
FSR present 路径、TOTK Optimizer、16 倍各向异性和 SDL3 音频后端均能被检测到。

目前不能把 Metal 后端称为性能或画质已达标：

- MainField 常见约 21–28 FPS；2026-07-18 的截图为 22.58 FPS / 44.29 ms。
- 爆炸、粒子等复杂动效出现时，用户观察到约 15 FPS。
- 同条件下用户此前观察到 Vulkan 约 30–45 FPS，Metal 仍明显落后。
- v27 的同步合并优化确实减少了重复等待，但真正的
  `BufferModifiedRange` CPU/GPU 一致性等待仍占主要时间。
- 画面已不再是完全缺特效或黑屏，但相较 Vulkan 仍有偏青、暗部压黑、草地和
  岩面细节发硬或发糊的问题。

下一位开发者应优先解决真实 host sync / command-buffer pipeline bubble，并并行建立
Vulkan 与 Metal 的同场景画质对照。不要继续通过调整批量大小或加强锐化来掩盖根因。

## 2. 仓库与分支

- Remote：`https://git.ryujinx.app/ryubing/ryujinx.git`
- 上游本地基线：`origin/master` / `a82350bb774f70fcbd41c9987bf67a3775409963`
- 工作分支：`codex/native-metal-backend`
- 当前 HEAD：`5f96306be395e836521b811e67f2b2829117cd12`
- 相对 `origin/master`：0 个落后、10 个领先

分支上的提交按时间顺序为：

```text
9b22bb83 Revert "Revert the Metal Experiment (#701)"
0ebcedb4 graphics: adapt experimental Metal backend to current main
8871eeea metal: stabilize shader compilation and resource lifetime
cd6a5443 docs: record macOS Metal development safeguards
93daed37 metal: batch resource residency declarations
cd5d6512 metal: invoke batch resource selectors directly
b1c1d888 docs: record macOS Metal packaging lessons
c0d6fc54 Improve Metal shader cache and FSR present path
e58a3b10 docs: record DLL versioning lesson
5f96306b metal: improve presentation and host sync scheduling
```

开始工作前必须完整阅读仓库根目录的 [AGENTS.md](../AGENTS.md)。其中记录了候选版本
保护、程序集版本、签名、Finder 启动、File Provider 构建和最小游戏回归要求。

## 3. Git 与产物边界

当前 Git 已跟踪源码提交到 `5f96306b`，但 `artifacts/` 整体未被 Git 跟踪。

这意味着：

- 只推送或复制 Git 分支，不会带走 v25–v27 可执行目录、日志、截图或采样。
- 如果接手者需要复现实机状态，必须另外复制 `artifacts/terminal/` 和
  `artifacts/diagnostics/`，或者从源码重新构建。
- 不得把“源码已提交”描述成“候选程序已随 commit 保存”。

## 4. 候选版本清单

| 目录 | 定位 | 已知结果 |
| --- | --- | --- |
| `artifacts/terminal/Ryujinx-metal-v25-rebuild` | 最后保留的只读终端基线 | 可启动和进游戏；不要覆盖 |
| `artifacts/terminal/Ryujinx-metal-v26-sync-a` | 完全延迟 WaitForIdle host sync | 标题界面等待明显下降，但 MainField 不成立 |
| `artifacts/terminal/Ryujinx-metal-v26-sync-b-batch4` | 每 4 个 deferred sync 主动提交 | MainField 约 25–27 FPS，移动时仍可到 15 FPS |
| `artifacts/terminal/Ryujinx-metal-v27-sync-coalesce` | 当前候选；合并已被完成边界覆盖的等待 | 可进 MainField；常见 21–28 FPS，爆炸仍约 15 FPS |

所有候选都是松散终端目录，不是可交付的 Finder `.app`。v25、v26 和 v27 目录必须
继续独立保存，禁止为了省空间互相覆盖 DLL。

## 5. 当前 v27 的启动与配置

启动命令：

```bash
cd artifacts/terminal/Ryujinx-metal-v27-sync-coalesce
RYUJINX_METAL_DEFERRED_SYNC_BATCH=4 OS_ACTIVITY_MODE=disable ./Ryujinx -g Metal
```

严格同步 A/B 回退：

```bash
RYUJINX_METAL_STRICT_WFI=1 \
RYUJINX_METAL_DEFERRED_SYNC_BATCH=4 \
OS_ACTIVITY_MODE=disable \
./Ryujinx -g Metal
```

测试时确认的配置：

- 主机：Apple M1 Max，32 GiB 内存
- macOS：26.5.2（25F84）
- Ryujinx：`1.3.3+local-metal-v27-sync-coalesce.5f96306b`
- 游戏：TOTK 1.4.2，Title ID `0100F2C0115B6000`
- Guest DRAM：8 GiB
- 图形后端：Metal
- TOTK Optimizer：已加载
- 游戏渲染分辨率：2560×1440
- Scaling filter：FSR，level 80
- Anisotropy：16×
- Mod 最大帧率：45 FPS
- Audio：SDL3，音量 100%
- VSync：Switch

测试前务必再次核对这些值。此前配置迁移曾把 DRAM 改回 4 GiB、FSR 改成双线性、
各向异性改成自动，导致 UltraCam 从 1440p 回退到 1080p，产生过错误性能结论。

## 6. v27 实测结果

### 6.1 稳定性与功能

- 主界面正常启动，标题和程序集版本均为 v27 / 1.3.3。
- TOTK 1.4.2 正常越过加载和 Shader 阶段进入 MainField。
- 连续观察超过 8 分钟，没有闪退或死锁。
- TOTK Optimizer 和已配置的 mod/cheat 内容被发现。
- SDL3 AudioRenderer 正常注册、启动并建立 audio stream；是否最终可听仍应由用户确认。
- 当前日志未出现新的 Metal Shader 转换致命错误。

### 6.2 帧率与画面

- 用户截图：22.58 FPS、44.29 ms、FIFO 33.06%。
- 常见 MainField：约 21–28 FPS。
- 爆炸和明显粒子动效：用户观察约 15 FPS。
- FIFO 长期偏低，说明不能简单归因为 GPU 着色吞吐饱和；提交和等待造成的 pipeline
  bubble 更值得优先检查。
- FSR present sharpener 已记录为启用，输入纹理为 2560×1440。
- 画面仍比 Vulkan 更偏青，阴影区域明显压黑；草地和岩石的高频细节存在过硬、重复或
  发糊现象。不要继续单纯提高锐化强度。

### 6.3 同步统计

v27 在标题/菜单阶段表现良好：

- 每 120 帧约 450 次真实等待。
- 聚合等待约 0.62–0.77 秒。
- 每 120 帧约 2100–2240 个 covered sync 被合并。

进入 MainField 后，真正的资源同步再次成为主要瓶颈：

- 每 120 帧常见约 300–550 次等待。
- 聚合等待常见约 2.5–7.4 秒。
- 强制 flush 常见约 65–189 次。
- proactive flush 常见约 237–553 次，个别瞬间更高。
- 每 120 帧仍可合并约 1600–3100 个 covered sync。
- 等待来源几乎全部是 `BufferModifiedRange`。
- 创建来源主要是 `WaitForIdle` 和 `SetReference`，其次为
  `TextureUnbindForce`；`Syncpoint` 很少。

结论：v27 的 coalescing 逻辑有效，但它只消除了已经由同一或更晚 Metal 提交边界证明
完成的重复等待。MainField 中还存在大量必须等待的真实 CPU/GPU 资源依赖。

### 6.4 进程采样

5 秒 `sample` 共有 2310 个样本/线程。三个关键 guest 线程分别出现：

- `<MainThread>`：678 / 2310 样本停在 `_MTLCommandBuffer waitUntilCompleted`
  （约 29%）。
- `<ModuleSystemWorker1>`：618 / 2310（约 27%）。
- `<ModuleSystemWorker2>`：515 / 2310（约 22%）。

这些比例不能相加为进程总占用，但足以证明多个 guest 工作线程都被 Metal command
buffer 完成等待阻塞。

## 7. 已保存的诊断证据

以下文件位于未跟踪的 `artifacts/diagnostics/v27-sync-coalesce/`：

| 文件 | 内容 | SHA-256 |
| --- | --- | --- |
| `runtime.log` | 已脱敏的 v27 完整运行日志 | `30b9447a61709fb2a922789c471472c38ef05692e1e60211d11e6f907f0b5e75` |
| `mainfield-sample-5s.txt` | MainField 5 秒 macOS `sample` | `97bf972faa9f6cbd93971295e77a412f3ce052c9a8de78afb6c49964db6e83bd` |
| `mainfield-22.58fps.png` | 压缩后的 22.58 FPS 画面截图 | `ce85fe973dd5515d4245828b1edf34c037c6a11a469826169a51d87b7497c9b9` |

原始日志中本机用户目录已替换为 `/Users/[redacted]`。

## 8. 当前实现的关键点

### 8.1 Host sync 来源与诊断

- [HostSyncSource.cs](../src/Ryujinx.Graphics.GAL/HostSyncSource.cs) 定义 create/wait
  来源枚举。
- GAL、threaded renderer、GPU 和三个图形后端传递这些来源。
- [Pipeline.cs](../src/Ryujinx.Graphics.Metal/Pipeline.cs) 每 120 帧输出等待、forced
  flush、proactive flush、coalesced signals 和来源分布。

### 8.2 Deferred WaitForIdle

- [GPFifoClass.cs](../src/Ryujinx.Graphics.Gpu/Engine/GPFifo/GPFifoClass.cs) 在 Metal
  下默认延迟 WaitForIdle host sync。
- `RYUJINX_METAL_STRICT_WFI=1` 恢复严格行为，用于 A/B 和正确性回退。
- [BufferModifiedRangeList.cs](../src/Ryujinx.Graphics.Gpu/Memory/BufferModifiedRangeList.cs)
  不再在等待 Metal 时一直持有 range-list lock，等待后会重新查询范围。

### 8.3 主动批量提交

- [SyncManager.cs](../src/Ryujinx.Graphics.Metal/SyncManager.cs) 默认每 4 个 deferred
  sync 主动提交一次。
- `RYUJINX_METAL_DEFERRED_SYNC_BATCH` 范围为 0–64；0 表示完全延迟。
- batch 8 和 16 已经实测更差，不应再次作为默认值尝试。

### 8.4 Covered sync 合并

- 一个真实 fence wait 完成后，v27 会把同一或更早 `FlushId` 的 handle 标记为已完成。
- 依据是当前 Metal command buffers 提交到同一有序队列；后边界完成意味着之前提交已经完成。
- 该逻辑没有删除 sync、fence、resource barrier 或真实边界。
- 当前结果证明合并计数很高，但 MainField 的剩余真实等待仍足以限制帧率。

### 8.5 Present 与画质

- [Window.cs](../src/Ryujinx.Graphics.Metal/Window.cs) 根据 scaling filter 选择 present
  路径并记录源/目标尺寸。
- [Present.metal](../src/Ryujinx.Graphics.Metal/Shaders/Present.metal) 包含 Metal present
  sharpener，用于 FSR 模式。
- [GraphicsConfig.cs](../src/Ryujinx.Graphics.Gpu/GraphicsConfig.cs) 提供
  `RYUJINX_METAL_SHADER_CACHE_PRELOAD_LIMIT`；当前默认 0，避免大规模首次预加载冻结。

## 9. 已否定或不应重复的方向

### 9.1 仅调整 deferred batch

- batch 4：MainField 约 25–27 FPS，仍可掉到 15 FPS。
- batch 8：约 22–25 FPS，forced flush 和等待没有改善。
- batch 16：约 19–25 FPS，明显更差。

调整 batch 只改变等待出现的位置，并不能移除真实资源依赖。

### 9.2 用异步编译掩盖 Shader 转换失败

`Subgroup builtins are not available in this type of function` 属于着色器转换失败，不是编译
速度慢。异步编译只能推迟卡顿或暂时缺少效果，不能让无效 Shader 成功转换。

### 9.3 用降低画质换表面帧率

不能通过禁用特效、跳过 Shader 错误、降低 mod 分辨率或跳过资源同步来宣称性能提升。
当前用户目标同时要求 Vulkan 接近的画质与更好的帧率。

### 9.4 只替换单个默认 1.0.0.0 DLL

单项目构建若没有显式传入 1.3.3 版本参数，会产生默认 1.0.0.0 程序集。主界面可能能开，
但启动游戏加载 `Ryujinx.Graphics.Metal, Version=1.3.3.0` 时会闪退。

### 9.5 在 File Provider 工作区反复启动 publish

File Provider 路径中项目图计算可能长时间不动。不要堆积多个 publish/dotnet 进程；把源码
快照、输出、中间目录和 NuGet 缓存放到 `/private/tmp`，并禁用共享 build server。

## 10. 下一步建议（按优先级）

### P0：建立严格可重复的 Vulkan / Metal 基线

在完全相同的存档、坐标、时间、镜头、分辨率、mod、VSync 和 shader-cache 热度下分别记录：

- 静止 60 秒平均 FPS、1% low 和最低 FPS。
- 连续跑动 60 秒。
- 同一个爆炸/粒子触发点。
- Metal 和 Vulkan 截图。
- sync 统计和 5 秒进程采样。

没有匹配场景就不能把 v26 的 26 FPS 和 v27 的 22 FPS直接视为回退。

### P1：细分 `BufferModifiedRange` 的真实等待

现有来源粒度还不够。建议记录：

- 等待的 buffer 大小和被修改范围大小。
- 每个 handle 的创建调用点、FlushId 和等待线程。
- 等待是否由 guest CPU read、guest CPU write、texture copy、query 或 indirect buffer 触发。
- 一帧内同一 buffer/range 是否反复等待。
- `WaitForIdle` 与 `SetReference` 中哪些最终对应同一资源。

目标是找出少数高频资源/调用点，而不是再次全局调整 batch。

### P2：把可证明的依赖留在 GPU 队列

在确认依赖语义后，评估用 Metal shared event、fence 或 command-buffer dependency 表达 GPU→GPU
顺序，减少 guest 线程直接调用 `waitUntilCompleted`。只有 guest CPU 确实要访问 GPU 结果时才应
阻塞 CPU。任何改动必须保留 strict fallback，并通过材质、粒子、mod 和长时间运行验证。

### P3：单独处理画质正确性

FSR sharpener 已工作，剩余差异更可能在渲染正确性。建议逐项对照 Vulkan：

- sRGB/linear render-target 格式和 present 色彩空间。
- depth/stencil load、store、compare 和 resolve。
- blend factor、write mask、alpha-to-coverage。
- sampler compare、LOD clamp、mipmap 和 anisotropy。
- render-target swizzle、texture view 和 compressed format。
- 阴影贴图采样与临时 texture 生命周期。

优先使用同场景 Metal frame capture 或最小 render test，不要继续靠肉眼调锐化常数。

### P4：最后再做 `.app` 打包和 Finder 签名

当前性能判断只使用终端松散目录。游戏加载、画质、声音、mod 和帧率稳定之前，不要把主要
精力放回 `.app`。源码正确、终端可运行、App 包正确和 Finder 可启动是四个独立阶段。

## 11. 构建注意事项

已验证有效的主项目构建环境变量和参数：

```bash
AVALONIA_TELEMETRY_OPTOUT=1 \
DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
DOTNET_CLI_TELEMETRY_OPTOUT=1 \
MSBUILDDISABLENODEREUSE=1 \
dotnet build src/Ryujinx/Ryujinx.csproj \
  -c Release \
  --no-restore \
  --disable-build-servers \
  -m:1 \
  -nodeReuse:false \
  -p:UseSharedCompilation=false \
  -p:AssemblyVersion=1.3.3.0 \
  -p:FileVersion=1.3.3.0 \
  -p:Version=1.3.3 \
  -p:InformationalVersion=1.3.3+local-metal-<candidate>.<commit>
```

说明：

- `AVALONIA_TELEMETRY_OPTOUT=1` 是 Avalonia BuildServices 官方支持的开关；否则受限环境下
  它会尝试写 `~/Library/Application Support/AvaloniaUI/BuildServices/buildtasks.log` 并导致
  构建失败。
- 如果 `--no-restore`，本地快照必须有匹配的 `obj/project.assets.json` 和 NuGet 包缓存。
- 在新的环境中先执行一次正常 restore，再把构建放到本地临时磁盘。
- 不要复用 `$HOME`、`$home` 或 `$CODEX_HOME` 作为临时变量。
- 新候选必须显式使用 1.3.3 assembly/file version，并用 `strings`、哈希和实际游戏加载确认。

## 12. v27 产物身份

`artifacts/terminal/Ryujinx-metal-v27-sync-coalesce` 的关键文件：

```text
Ryujinx
be4dd24567e973131f5f97173d64ec82a57cc3175e734be19132adff5471835d

Ryujinx.dll
791fa0ea57d93263c87f8651bb714716352924c4012fbc87205daf0f21e32d5d

Ryujinx.Graphics.Gpu.dll
c1d7af8d43ce9a6ca1e55d467f4108f8978251606a600718ba97c43054061471

Ryujinx.Graphics.Metal.dll
5b6edc3a084ed651368f4f12c22ccb5f8acedbbfb36d12abfa9459b121ee524b

Ryujinx.Graphics.Shader.dll
35702f58768f3464e0ae799f0967231c8705dcfca1d1d649d517e791b7cfa8d1
```

Apphost：

- Signature：ad-hoc
- CDHash：`e969e75f9158f701122f860507ed57c9589c327e`
- Hypervisor entitlement：true
- `codesign --verify --strict --verbose=4`：通过

这只证明松散 apphost 自洽，不代表 Finder/Gatekeeper 会接受一个重新封装的 `.app`。

## 13. 接手者的最小验收清单

每个新候选至少完成：

1. 从独立新目录启动，版本标题与程序集版本一致。
2. 确认 Metal、8 GiB、FSR 80、16× 和 TOTK Optimizer 均已加载。
3. 越过 shader/pipeline 阶段进入同一 MainField 存档。
4. 静止、跑动和爆炸场景分别采样。
5. 检查画面、阴影、材质、粒子、声音和 mod。
6. 与同场景 Vulkan 比较，而不是与菜单或另一个地点比较。
7. 连续运行足够时间，确认无闪退、死锁和无限编译。
8. 保留最后可用候选，不直接覆盖。

## 14. 当前停点

2026-07-18 的优化工作停在 v27：

- 源码已提交到 `5f96306b`。
- v27 已完成终端启动和实际 MainField 验证。
- coalescing 被证明有效，但性能改善不足。
- 尚未开始下一轮 `BufferModifiedRange` 细粒度定位。
- 尚未把 v27 封装成新的 Finder `.app`。
- 用户要求暂时停止优化并准备交接。

