# Ryujinx macOS / Metal 工作约束

本仓库包含实验性的 macOS 原生 Metal 后端。源码能够编译不等于 `.app` 能被 Finder 启动；主界面能够打开也不等于游戏能够加载。处理构建、签名、打包和性能问题时必须遵守以下规则。

## 保护可用版本

- 将已经由用户验证可启动的 App 视为只读基线，禁止直接向其中覆盖 DLL、可执行文件或 `Info.plist`。
- 新修改必须生成名称明确的候选 App，在候选版本完成验证后才能替换交付版本。
- 修改 App 前记录其完整路径、版本、签名类型和 CDHash，并对将要替换的文件计算 SHA-256。
- 备份必须放在持久目录并标明来源，不能只依赖 `/private/tmp`。确认新版本完全可用后再清理备份。
- 工作目录中正常只保留一个已验证交付 App；失败版本和临时副本及时移入废纸篓或清理，避免用户选错。

## macOS 签名与启动

- macOS App 的资源封装受代码签名清单保护。替换任意被封装的 DLL 都会使原签名失效，即使主可执行文件本身没有变化。
- Ad-hoc 重签名会产生新的 CDHash；旧 App 已经获得的本机许可不会自动继承给新 CDHash。
- 在没有有效 Apple 代码签名身份时，不得承诺新 ad-hoc App 可以通过 Finder、右键“打开”或 Gatekeeper 提示正常启动。先执行 `security find-identity -v -p codesigning` 确认签名条件。
- Apple Development/Personal Team 签名只能说明本机身份可用，不代表所有 entitlement 都会被系统接受。尤其是 `com.apple.security.hypervisor`、JIT、可执行内存和库验证相关 entitlement，必须通过实际启动日志、崩溃日志或最小探针确认已经生效。
- 不得为了绕过签名问题擅自启用 Terminal 开发者模式、关闭 Gatekeeper、修改“允许以下来源的应用程序”或安装自签名根证书。这些操作必须由用户明确批准。
- 每个候选包至少执行以下检查：
  - `codesign --verify --deep --verbose=4 <app>`
  - `codesign -dvvv <app>` 并记录 CDHash
  - 使用 Finder 等价路径（例如 `open -n <app>`）启动
  - 检查 AMFI、Gatekeeper 和崩溃日志，确认没有 `security-policy(8)` 或未知证书链拒绝
- `codesign` 验证通过只表示资源封装自洽，不代表 Gatekeeper/AMFI 一定允许该身份运行。

## 构建与打包分层

- 将“源码正确”“命令行构建可运行”“App 打包正确”“Finder 可启动”视为四个独立阶段，分别验证，不能相互代替。
- 不要把松散的 RID 输出误当成正式 `.app`。正式交付必须使用匹配当前项目结构的打包流程，并保持版本信息、entitlements、原生库和资源目录一致。
- 当签名或 Finder 启动仍不稳定时，先用终端启动的松散运行目录验证 Metal 后端的真实性能和画质；等游戏加载、Shader 编译、Mod 和帧率都稳定后，再回到 `.app` 签名打包。不要让打包问题污染性能判断。
- 终端可运行不代表 `.app` 可运行。手工封装 .NET/macOS App 时，apphost 路径、`Contents/MacOS`、`Contents/Resources`、`Contents/Frameworks`、托管 DLL 和原生库布局必须一起验证，不能只复制一个可执行文件。
- 版本号必须在交付前核对。基于 1.3.3 的本地修复包不能显示成 1.0.1；标题栏、程序集信息和 App 元数据应一致。
- 单独构建并替换某个 Graphics DLL 时，必须核对该 DLL 的 `AssemblyVersion`、`FileVersion`、`AssemblyInformationalVersion` 是否与当前运行目录匹配。单项目 `dotnet build` 可能产出默认 `1.0.0.0` 程序集版本，导致运行时按 `Ryujinx.Graphics.Metal, Version=1.3.3.0` 加载失败，表现为主界面能开但启动游戏闪退。
- 如果需要临时单独构建 DLL，必须显式传入当前候选包所需版本参数（例如 `-p:AssemblyVersion=1.3.3.0 -p:FileVersion=1.3.3.0 -p:Version=1.3.3 -p:InformationalVersion=...`），并用 `strings <dll>`、哈希和一次实际游戏加载验证，不得只凭编译通过交付。
- 对 .NET universal single-file App 做 bundle 分析前，先使用 `lipo -thin arm64` 或 `lipo -thin x86_64` 提取对应架构；bundle 内偏移通常相对于单架构切片，不能直接按 universal 文件偏移解析。
- 如果 File Provider 路径中的 publish 长时间停在项目图计算，优先把构建输出、中间目录和 NuGet 缓存放到本地临时磁盘；不要反复启动多个挂起的 publish 进程。
- Git 提交只代表已跟踪源码。交付 App、临时 DLL 和 `artifacts/` 中的未跟踪文件不能被描述成“已随 commit 保存”。交付时明确记录分支、基线提交、修复提交和产物路径。

## Metal 修复的验证标准

- 不能以“主界面打开”作为成功标准。很多 Graphics DLL 只在启动游戏后加载，签名破坏、缺失符号和 Metal 管线错误可能到此时才出现。
- 每次修改 Metal、Shader 或 GPU 项目后都要完成以下最小回归：
  1. App 从 Finder 等价路径启动并稳定停留在主界面。
  2. 实际启动目标游戏，越过首次 Shader/Pipeline 编译阶段。
  3. 连续运行足够时间，确认没有闪退、死锁或无限编译。
  4. 检查画面、材质、特效和黑屏/闪烁问题。
  5. 确认用户的 Mod 被加载且画质效果仍存在。
  6. 在相同场景、分辨率、Mod 和缓存条件下对比 Vulkan 与 Metal 帧率。
- 性能优化必须与正确性一起验收。若 Metal 帧率低于 Vulkan、画质 Mod 不生效或 Shader 阶段卡住，应先保留日志和场景条件，再定位资源绑定、管线缓存、同步等待和 Mod 加载路径；不能用降低画质或跳过错误来掩盖问题。
- `Subgroup builtins are not available in this type of function` 一类错误属于着色器转换失败，不是单纯编译速度慢。异步编译只能改变卡顿出现的时间，不能使无效着色器成功转换。
- 优化性能前先建立可重复基线。记录游戏版本、场景、分辨率、VSync、Mod、Shader 缓存状态、平均帧率和最低帧率，避免把缓存预热或 Mod 丢失误判为后端优化。
- 不得通过禁用画质路径、忽略 Shader 错误或跳过资源同步来换取表面帧率，除非用户明确接受对应画质/正确性损失。

## 故障处理与交付

- 出现“之前能启动、修改后不能启动”时，第一步比较签名清单、CDHash 和被修改资源，不要先假设是 Metal 运行时问题。
- 保留最后可启动版本并进行二分验证：先验证 App 启动，再逐个引入 Metal、Shader、GPU 变更；不要一次覆盖多个 DLL 后再猜测故障来源。
- 诊断结论必须基于可核对证据：进程是否存活、退出码、日志中的异常、签名验证结果和文件哈希。不要仅凭程序坞图标或 `open` 命令返回成功判断 App 已启动。
- 如果为了恢复启动而回滚产物 DLL，必须明确告诉用户：源码修复仍然存在，但当前运行的 App 使用的是哪个产物版本；不能把“恢复可启动”表述为“最新修复已经全部集成”。
- 删除产物前精确列出目标。优先移动到废纸篓；永久删除临时文件后说明删除了什么以及是否可恢复。
