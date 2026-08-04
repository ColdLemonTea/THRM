# THRM Flutter 3 UI 迁移计划

## 目标

在不重写后台核心的前提下，用 Flutter 3 桌面端替换 Wails + Next.js + React UI，消除
WebKitGTK 依赖，并在 NVIDIA + KWin + Wayland 实机上解决当前显式同步规避后出现的撕裂。

迁移成功必须同时满足：

- Flutter 默认渲染路径下无持续画面撕裂或窗口协议崩溃，不再设置
  `__NV_DISABLE_EXPLICIT_SYNC=1`。
- HID/BLE、温控算法、温度采样、托盘、热键与后台常驻仍由现有 Go Core 负责。
- Linux 配置和状态继续遵循 XDG Base Directory；Linux 日志继续进入 journal，journal
  不可用时回退 stderr。
- Windows 10/11 与 Linux X11/Wayland 都能构建、安装和运行。
- Flutter 达到功能等价后才删除 Wails UI；迁移期间现有版本始终可作为对照和回退。

## 已确认的现状

- 基线：`origin/feat/linux-logging-packaging`，提交 `5270e54`。
- 当前 GUI：Wails v2 + Next.js 16 + React 19，共约 15,415 行前端源码；另有约 1,942
  行 Go GUI 适配层和 1,808 行 Wails 生成绑定。
- UI 只有四个一级页面：状态、曲线、控制、关于。主要复杂度集中在曲线/历史图、控制设置、
  噪音测试和更新流程。
- Core 已是独立常驻进程，关闭 GUI 不影响设备连接和自动控温；托盘也由 Core 管理。
- Core 与 GUI 已有版本化的 JSON Lines IPC（协议 `3.0`）：Linux 使用 Unix Domain
  Socket，Windows 使用 Named Pipe；请求支持并发响应路由，实时事件已在 Core 限频。
- Wails Go 层的大多数公开方法只是 IPC 请求包装。Flutter 可直接使用 `internal/ipc` 中的
  请求和事件契约，不需要把 Go 编译为共享库，也不需要新增本地 HTTP 服务。
- `docs/linux.md` 是历史移植计划，仍包含 Wails/WebKitGTK 前提和一些已完成事项；迁移完成时
  应更新为当前实现说明，不能把它作为 Flutter 架构依据。
- 当前 Windows 开发环境已安装 Flutter 3.44.8 stable（Dart 3.12.2）与 Go 1.26.5；
  Visual Studio C++ 桌面工具链已通过 `flutter doctor`。Android 与 Chrome 警告不影响本项目。

## 目标架构

```text
Flutter 3 GUI
├─ Material 3 界面、窗口状态、系统主题与本地化
├─ 应用状态（ChangeNotifier / ValueNotifier）
└─ JSON Lines IPC Client（基于 dart_ipc 2.0.0 字节流）
   ├─ Linux: Unix Domain Socket
   └─ Windows: Named Pipe（Dart FFI + overlapped I/O）
                │
                ▼
现有 THRM Core（Go）
├─ HID / BLE / 温度与功耗读取
├─ SmartControl / 曲线 / 历史记录
├─ 配置 / XDG / journal / 诊断
└─ 托盘 / 热键 / 自启动 / 插件
```

不引入第三个常驻进程，不把 IPC 改成 TCP/HTTP，不把托盘迁入 Flutter，也不重写硬件层。

## 阶段 0：可行性门槛

这一步通过前不开始页面迁移。

1. 安装 Flutter 3.44.8 stable（内置 Dart 3.12.2，满足 `dart_ipc 2.0.0` 的 Dart 3.10+
   要求）和 Go 1.26.5，记录完整 `flutter --version`/`go version`，CI 固定到相同版本；
   不额外引入 FVM。
2. 在 `ui/` 创建仅含 Linux、Windows 的最小 Flutter 工程和渲染压力页：连续动画、滚动、
   多折线和窗口缩放。
3. 在出现原问题的 NVIDIA + KWin + Wayland 机器上，以未设置
   `__NV_DISABLE_EXPLICIT_SYNC` 的环境运行 profile 构建，并覆盖：
   - 窗口拖动、缩放、最大化和多显示器；
   - 60 Hz 与高刷新率显示；
   - 持续动画、滚动和图表更新；
   - 睡眠/唤醒及显示器重连。
4. 同时验证 X11/XWayland，确认 Flutter 没有把问题从 Wayland 转移到另一会话类型。
5. 使用 `dart_ipc 2.0.0` 完成 IPC 最小探针：`Ping`、`GetConfig`、一个实时事件、Core
   重启后重连。它已统一提供 Linux Unix Socket 和 Windows Named Pipe 的 `Socket` API；
   不采用 README 中额外的 WebSocket/JSON-RPC 示例，因为 Core 已有自己的 JSON Lines 协议。
6. 补一个针对真实 Go Core 的大消息/分片/全双工测试。该包当前测试只覆盖基础收发和顺序多客户
   端，THRM 仍需验证超过 4096 字节的配置、并发响应与高频事件不会破坏分帧。

退出条件：目标机器没有持续撕裂/崩溃，两个平台都能完成请求、事件和重连。任一条件失败就
停止大规模迁移，保留压力页作为渲染问题复现器。

### 2026-08-04 本机实验记录（Windows）

- 已在 `experiment/flutter3-ui` 创建仅含 Windows、Linux runner 的 `ui/` 工程；未生成
  Android、iOS、macOS 或 Web 平台。
- 渲染压力页已覆盖持续动画、八条折线、长列表滚动和响应式窗口尺寸；目标 Linux 机器仍需
  手工执行拖动、缩放、最大化、多显示器、休眠唤醒等验收。
- `dart_ipc 2.0.0` 已直接连接现有 Go `internal/ipc.Server`。隔离 fixture 验证了 16 KiB
  配置分片、16 个乱序并发响应、8 KiB 事件交错，以及服务端重启后的同客户端重连。
- Windows `dart_ipc` 的 `IOSink.flush()` 不能并发调用，因此探针在唯一写入口串行发送完整帧；
  响应读取和 `requestId` 路由仍保持并发。
- `go test ./internal/ipc`、`flutter analyze`、`flutter test` 和
  `flutter build windows --release` 均通过。
- 没有 Flutter 开发环境的 Linux 目标机可下载
  `.github/workflows/flutter-linux-experiment.yml` 生成的 profile bundle，解压后直接做渲染验收；
  实验分支推送时自动构建，workflow 进入默认分支后也可手动触发。

用户已在目标 Linux 机器确认 Wayland 与 XWayland profile bundle 基础运行流畅，阶段 0 的
基础渲染门槛通过，可以开始最小外壳和状态页迁移。多显示器、高刷新率、休眠唤醒与长时间运行
仍保留为发布前验收项，不能由这次基础烟雾测试替代。

## 阶段 1：Flutter 外壳与 IPC

1. 在 `dart_ipc` 返回的 `Socket` 上实现一个薄 IPC client：换行帧解析、`requestId` 路由、
   请求超时、事件流、断线重连及 `protocolVersion == 3.0` 检查。沿用现有读请求可重试、
   写请求不盲目重放的语义，不再自写 Unix Socket/Named Pipe transport。
2. 直接消费 `internal/ipc/ipc.go` 定义的全部事件；温度事件保留当前“未变化的传感器元数据
   沿用上一帧”逻辑，界面端继续将高频刷新合并到约 200 ms 一次。
3. Core 未运行时，从同目录或 `PATH` 启动 `thrm-core`；保持 portable 和系统安装两种布局。
4. 用 Dart 文件锁实现 GUI 单实例。第二个实例只向 Core 发送 `ShowWindow` 后退出；处理
   Core 推送的 `show-window` 与 `quit` 事件。
5. 用现有 `window.json` 格式保存窗口位置、大小和最大化状态；Linux 继续写
   `$XDG_CONFIG_HOME/thrm`，不引入新的配置目录。
6. 状态管理先使用 Flutter 自带 `ChangeNotifier`/`ValueNotifier`，导航使用四项枚举；不引入
   Bloc、Riverpod 或路由框架。
7. 配置以“保留未知字段的 JSON map”持有，只为界面实际使用的结构建立类型。更新单项配置时
   patch 原始 map，避免 Flutter 模型落后时覆盖或丢失 Core 新字段。

最小自动检查：IPC 分帧/并发响应/超时重连测试，以及“修改一个配置字段仍保留未知字段”的测试。

## 阶段 2：首个端到端页面

1. 复用现有图标、品牌图片和 HarmonyOS Sans 字体。
2. 机械复制中/英/日三份翻译 JSON，保留现有 key；用一个薄的嵌套 key 查询器读取，Flutter
   自带 localizations 只负责系统控件，不先做 ARB/文案重构。
3. 用 Material 3 实现应用外壳、四页导航、浅色/深色/跟随系统、加载态和错误恢复面板。
4. 先完成“状态”页：连接/断开、CPU/GPU 温度与功耗、设备/风扇状态、实时事件、桥接故障
   提示和 Core 重同步。
5. 补齐键盘焦点、Semantics 标签、缩放和最低 800×600 布局；视觉层先保持信息层级与品牌色，
   不把逐像素复刻或装饰动画作为迁移门槛。

退出条件：状态页可在真实 HID 与 BLE 设备上连续运行，Core 在 GUI 打开、隐藏、关闭、重启时
仍保持正确生命周期；profile 模式下实时数据不造成持续超预算帧。

## 阶段 3：曲线、历史与 SmartControl

1. 先移植并测试现有纯逻辑：历史合并/采样/保留、时间轴去重、分时规则规范化、手动挡位预设。
2. 用 Flutter `CustomPainter` + 手势实现风扇曲线编辑和温度历史图，避免为两张定制图同时引入
   一整套图表框架；保留拖点、缩放、tooltip、当前温度标记和多数据序列。
3. 完成曲线方案的新建、重命名、切换、删除、导入/导出，以及低转速风险确认。
4. 完成分时曲线、转速避让、学习偏好、预测前馈、瞬时尖峰过滤、目标温度和学习结果重置。
5. 保留当前“Core 是历史和时间轴真源”的规则，Flutter 不从前台事件反推后台历史。

退出条件：同一份配置和实时输入下，Wails 与 Flutter 对曲线、方案、历史和 SmartControl 的展示
与下发结果一致。

## 阶段 4：控制、噪音、关于与平台能力

1. 迁移设备设置、RGB、亮度、智能启停、自定义 RPM、传感器/GPU 选择、热键、开机自启动、
   Legion Fn+Q、调试命令和离线提示。
2. 噪音测试使用麦克风 PCM 插件；只在这一阶段加入录音和 FFT/A 计权所必需的依赖，移植现有
   扫速、稳定等待、相对 dB、共振区间检测和中断后恢复设备状态逻辑。
3. 迁移关于页、credits、GitHub 版本检查、剪贴板和外部链接。
4. 把诊断包生成下沉到 Core，并新增“目标路径”IPC 请求；Flutter 仅负责原生保存对话框，继续
   复用现有 Go ZIP、配置快照和 journal 日志收集代码。
5. 把 Windows 更新下载/来源白名单/静默安装下沉到 Core，以 IPC 事件报告进度；Flutter 不重写
   已有安全校验。
6. Flutter 的重要生命周期和错误日志通过低频 IPC 转交 Core 写 journal；IPC 尚不可用时直接
   回退 stderr，避免在 Dart 中复制 Journal Native Protocol 实现。
7. Windows 窗口材质只保留当前已支持的效果且按系统能力降级；Linux 不增加 WebView。

### 自定义主题处理

现有任意 CSS 主题无法可靠映射为 Flutter widget 样式。首个 Flutter 版本保留 system/light/dark
和内置 THRM 品牌主题；检测到旧 CSS 主题时回退到其 `base`，不删除用户文件。只有在确有用户
需求后再定义小型 JSON design-token 格式，不尝试实现 CSS 解释器。

## 阶段 5：打包、并行验证与切换

1. CI 增加 `flutter analyze`、`flutter test`、`flutter build windows` 和
   `flutter build linux`；Go Core 继续执行 `go test ./...` 和现有构建。
2. Linux 将 Flutter bundle 安装到 `/usr/lib/thrm/`，由 `/usr/bin/thrm` 启动；保留
   `/usr/bin/thrm-core`、udev 规则和 desktop entry。删除 WebKit2GTK 运行/构建依赖，但保留
   GTK 与 hidapi 等真实依赖。
3. Windows NSIS 和 portable 包收集完整 Flutter runner bundle，而不是只复制一个 EXE；继续
   打包 Core、TempBridge 和 PawnIO。
4. 在实验发布中让 Wails 与 Flutter 产物使用不同名称，完成下列矩阵后再替换正式 `thrm`：
   - Windows 10/11；
   - Linux X11、Wayland，至少覆盖 NVIDIA + KWin 原问题机器；
   - BS1 BLE 与至少一个 BS2/BS3 HID 设备；
   - 首次启动、自动启动、托盘恢复窗口、GUI/Core 独立重启、睡眠唤醒、设备重连；
   - 手动挡位、自动控温、曲线/历史、RGB、噪音测试、诊断导出和 Windows 更新。
5. 验证 `$XDG_CONFIG_HOME`、`$XDG_STATE_HOME`、配置迁移、`window.json`、journal identifier、
   非 systemd stderr 回退及只读安装目录。
6. 达到功能矩阵后删除 Wails 入口、`frontend/`、`wails.json` 和 Wails/Next 依赖，更新 README、
   `docs/linux.md`、构建说明和主题文档。删除必须独立提交，便于回退。

## 不做的事

- 不重写 Go Core、设备协议、SmartControl 或温度采样。
- 不新增本地 Web 服务、共享 Go 动态库或额外常驻 bridge。
- 不在迁移过程中顺便重设计 IPC 协议；只补 GUI 无法替代的少量请求。
- 不在功能等价前删除旧 UI，也不把像素级复刻和全部装饰动画设为切换条件。

## 最终验收命令

```text
go test ./...
flutter analyze
flutter test
flutter build windows --release
flutter build linux --release
```

此外必须在原 NVIDIA/KWin/Wayland 机器上完成实机验收；CI 构建成功不能替代撕裂验证。
