# PCL Lite LiquidGlass Core

入口：设置 → 个性化 → 液态玻璃 · LiquidGlass Core。独立卡片，不放进高级材质选项。
默认关闭；点击开关先显示性能确认，无倒计时。取消不会开启。确认后在开关下展开设置。
原 `UiTahoeGlassEnabled` / `UiTahoeDesktopBackdrop` 配置键和控件类型保留兼容；
主实现与文件统一为 `LiquidGlassCore.cs`、`LiquidGlass*.cs` 和 `Resources/LiquidGlass/`。
旧实验配置没有新的性能与桌面采样确认标记时不能自行启用。

默认折射启动器背景，可正常截图和录屏，不设置 HWND 截图排除。没有背景图片时显示静态材质。
“实时折射桌面”默认关闭，单独开启还需确认截图限制。Windows Graphics Capture 采样真实桌面，经 D3D11 局部回读送入八次幂着色器。该模式隐藏启动器壁纸并暂停视频，不删除文件或修改壁纸配置。

为防止递归绘制，采样期间通过 `WDA_EXCLUDEFROMCAPTURE` 排除启动器自身；因此启动器也不会出现在系统截图或录屏中。关闭、失败或退出时恢复原有截图状态。系统可能显示桌面采集边框；实现没有绕过系统的录制提示或授权设置。

这是原 PR“截屏时窗口消失”的原因：实际窗口没有关闭，是系统截图刻意排除了它。
实时桌面与普通截图兼容无法在当前 WGC 自排除实现中同时保证，不以移除标志造成递归画面来伪装修复。
多屏恢复耗尽时全部退役并恢复截图状态；affinity 恢复暂时失败时，独立 dispatcher 最多重试三次，
即使主采集已停用或 Dispose 也保留有限恢复；窗口销毁后停止。永久系统调用失败记录日志，不承诺系统一定接受。

## 性能设置

预设框及选项最小高度 38 DIP，开关高 32 DIP，关闭时设置折叠于开关下。

| 预设 | 模糊 | 色散 | 高光 | 按压动效 | 默认效果帧率 |
| --- | ---: | ---: | ---: | --- | ---: |
| 低消耗 | 0 DIP | 0 | 0.35 | 关闭 | 30 FPS |
| 均衡 | 1 DIP | 0.08 | 0.50 | 开启 | 60 FPS |
| 高画质 | 2 DIP | 0.20 | 0.65 | 开启 | 90 FPS |
| 极致 | 3 DIP | 0.30 | 0.80 | 开启 | 120 FPS |

原生滑块可选 30–240 FPS，最右端不限制。切换预设会应用该档默认帧率，之后可独立修改。
上限作用于桌面回读/上传及按压动画，不修改 Minecraft 帧率，不保证实际渲染达到设置值；
移动/缩放不绕过上限，不限制仍受系统采集和显示器刷新率约束。
开销只作相对预估，不填造 CPU 百分比或内存数值，硬件、分辨率、多屏、动态背景均会影响结果。

## 来源与参数

- 视频：[BV1PuuEzaEzj](https://www.bilibili.com/video/BV1PuuEzaEzj/)，03:52 最终片段。
- Android：[Kyant0/AndroidLiquidGlass](https://github.com/Kyant0/AndroidLiquidGlass)，参考提交 `65ab177e90e5c1d8c62e70cf7755841982da65f6`。
- 外观参考：macOS Tahoe 工具栏、胶囊操作与侧栏；实现使用 C# / WPF / HLSL。

圆形内部采用视频的公式：

```text
offset = -normal * 0.05 * (d / r)^8
```

圆角矩形采用 Android 的 rounded rectangle SDF 与法线，令 `ratio = clamp(1 + signedDistance / radius, 0, 1)`。
因此半径 100 DIP 的 200 × 200 圆内，该公式与视频等价；圆角矩形、坐标变换与模糊是宿主扩展。
`0.05` 是完整背景的 UV 系数，并非固定 12 DIP；例如背景宽 1000 DIP 时，水平方向最大位移为 50 DIP。
视频未展示 `blur()` 函数体，本实现使用权重归一的九点高斯采样，默认半径 2 DIP。
Android 自身的 `circleMap()` 没有沿用，折射曲线按要求使用八次幂。

按压参考 `LiquidButton.kt` 与 `InteractiveHighlight.kt`：

| 参数 | 数值 |
| --- | --- |
| 弹簧阻尼比 / 刚度 | 0.5 / 300 |
| 基础扩张 | 4 DIP |
| 拖动位移 | `minDimension * tanh(0.05 * offset / minDimension)` |
| 按压白色叠层 | `0.08 * progress` |
| 径向高光 | `0.15 * progress` |
| 按钮染色 | 0.10；悬停 0.14 |

导航、胶囊操作及桌面模式的窗口外层染色为 0.10，侧栏为 0.20；阅读卡片采用中性内容表面。标签、按钮文字和页面内容不进入着色器。
主要操作通过透明蓝色叠层标识，表单采用轻透明填充和细边框，避免实色填充盖住背景。
默认字体使用本机 Segoe UI Variable Text，中文回退 Microsoft YaHei UI；尊重用户自定义字体，没有随程序分发 SF Pro。
内置蓝青曲线背景已移除。关闭桌面采样时仍可折射用户自行配置的启动器壁纸。
这是公开算法在 Windows 上的实现，不等同于 Apple 私有渲染器的逐像素输出。

## 渲染与生命周期

- `TahoeBackdrop` 只包含桌面采样像素或用户背景图片/视频，与玻璃层相互独立。
- 桌面采集按显示器与窗口交集建立会话，GPU 只回读交集区域；有界帧队列、用户帧率上限、复用像素缓冲、跳过相同帧。最小化/隐藏释放会话，恢复后重建；坐标使用实际屏幕像素及 WPF 变换。
- 坐标通过不可变区域快照无锁发布，原生会话在工作线程退役；UI 不等待 GPU 回读或 WGC Close。每个会话最多只有一份待呈现帧；缓冲比较后仅上传发生变化的行带，连续内存一次复制。
- 监听显示器拓扑变化并重新枚举；旧会话的帧和失败回调按身份隔离。失败最多自动重试三次，耗尽后停止定时器；显示器变化或重新开关可重试，单块健康显示器不会重置故障显示器的重试预算。
- 采样未就绪时不给控件提供虚假的可折射背景；设置页实时显示等待、工作、暂停、恢复、失败及模糊兼容状态。
- `TahoeGlassSurface` 排除自身、祖先、后代及跨窗口背景；无效源显示静态材质。
- `VisualBrush` 只采样当前控件附近区域，按位移与模糊外扩；局部坐标和法线变换到背景坐标，支持旋转、镜像和缩放。
- 源 alpha 按 WPF 预乘规则保留；Shader 采样夹紧边界。
- 不支持 ps_3_0 时退回背景模糊、染色和固定描边；高对比模式使用不透明材质。
- 布局、源和祖先变换改变时更新坐标；隐藏或卸载解除观察并释放画刷。
- 普通 LayoutUpdated 不再重复构建祖先列表与变换集合；同一帧的连续变换合并更新采样参数，卸载时取消排队更新。
- 按钮弹簧只在未收敛时订阅 Rendering，遵循系统减少动画设置。
- Android Apache 2.0 完整许可与来源随 `Resources/LiquidGlass/NOTICE.txt` 嵌入。

## 构建与验证

```powershell
powershell -ExecutionPolicy Bypass -File "Plain Craft Launcher 2/Resources/LiquidGlass/Compile.ps1"
dotnet build "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Debug
dotnet test PCL.Core.Test/PCL.Core.Test.csproj -c Debug --filter "FullyQualifiedName~TahoeGlass"
```

运行时只加载已编译的 `Glass.ps`；编译脚本优先 fxc，再使用 Windows D3DCompile。
数学测试覆盖八次幂、参数边界、圆角、旋转/镜像、局部采样和四档 DPI 的坐标运算。
数学测试不能代替多台显示器/四档系统 DPI 的实机画面验收。

### 桌面生命周期修复与性能记录

- 着色器重新编译为 `ps_3_0/main`，2696 字节。
- 23 项针对性回归通过：原有 13 项数学/STA 材质测试，以及 10 项桌面协调器与缓冲测试。新增测试通过注入采集平台覆盖过期回调、显示器恢复、有界重试、部分显示器故障、清理异常、重复开关、隐藏恢复、不支持采集以及脏行目标坐标。
- 已基于上游 `9865e3c` 重建，启动器 Debug 构建 0 警告、0 错误；全量 958 项测试通过、无跳过。测试构建有两条上游既有的 MSTEST0044 过时用法警告。DEBUG / CI / RELEASE / BETA 四种源码契约检查均通过；有意变更的 35 个控件接线成员按仓库机制记录了逐成员审批与精确源 SHA256，没有修改校验器或使用通配跳过。
- 本机同一隔离配置、启动到主页后等待 6 秒，再采样 6 秒：修复前消耗 1593.75 ms CPU，修复后 1218.75 ms CPU，约减少 23.5%。工作集约 270 MB → 279 MB，私有内存约 281 MB → 282 MB；没有内存下降的结论。
- 这是 Debug 单次短样本，实际桌面内容未完全受控，不能等价于帧率提升、长时间负载或跨设备性能保证；原始采样保存在本地忽略目录 `artifacts/tahoe-integration/perf-before.json` 与 `perf-after.json`。
- 修复版实际日志记录 `DesktopCapture=Live; Shader=EighthPower; f=0.05`，随后 `Desktop=LiveDesktopRefraction`。真实多显示器拔插、四档系统 DPI 及最终视觉比较仍需实机矩阵；故障恢复测试使用替身平台，不冒充物理显示器拔插验收。

主项目目标框架现为 `net10.0-windows10.0.19041.0`，最低系统为 Windows 10 2004；Debug 可执行文件位于 `Plain Craft Launcher 2/bin/Debug/net10.0-windows10.0.19041.0/`。D3D11 绑定使用 Vortice.Direct3D11 3.8.3。

桌面采集参考：

- https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture
- https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nn-windows-graphics-capture-interop-igraphicscaptureiteminterop
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity
- https://github.com/robmikh/Win32CaptureSample/blob/master/Win32CaptureSample/SimpleCapture.cpp

### 2026-11-10 桌面折射运行证据

隔离预览的实际启动日志记录 `DesktopCapture=Live; Shader=EighthPower; f=0.05`，随后记录 `Toolbar=Refraction; Sidebar=Refraction; Desktop=LiveDesktopRefraction; Tier=2`。这证明桌面首帧已进入 WPF 采样源及实际着色器路径，不能替代最终视觉比对或多显示器/DPI 验收。

开发日志中的 `[Tahoe]` 行报告当前工具栏/侧栏渲染模式与 WPF Tier。

### 2026-11-10 首轮验证记录（视觉重写前）

- Debug 启动器构建：0 警告、0 错误；HLSL 编译为 ps_3_0，1940 字节。
- 13 项测试通过：9 项数学测试及 4 项 STA 窗口/采样器/屏外缓存回归。
- 原生 PCL 实测：设置开关即时生效、关闭恢复、重启保留、浅色/深色、页面滚动、进入/离开页面、Tab 焦点、Enter 激活及缩小窗口。
- 运行日志确认 `Toolbar=Refraction; Sidebar=Refraction; Tier=2`。
- 实测发现并修复：卸载时非法 sampler 画刷；屏外采样区域未缓存导致的循环；静态审查修复了模态窗口下底层按钮的键盘穿透。
- 同一机器上的短时 CPU 抽样：旧玻璃开启状态 1.5 秒消耗 1687.5 ms CPU；修复后的静止个性化页 2 秒消耗 375 ms；关闭玻璃基线 2 秒消耗 484.38 ms。这只是单机诊断抽样，不是跨设备性能保证。
- 四档系统 DPI、真实高对比系统设置、视频背景连续播放，以及长时间内存/性能尚未做完整实机矩阵；兼容/无源/隐藏卸载路径已由 STA 回归覆盖。

### 2026-11-10 视觉重写

- 重新参考缓存的 Apple WWDC25 官方资料及 GitHub `BarredEwe/LiquidGlass`、用户提供的 `xiaofangstudio/liquid-glass-web`、`Kyant0/AndroidLiquidGlass` 源码。
- 标题拆分为浮动导航和窗口按钮组；重做侧栏选择、胶囊按钮、卡片表面和默认矢量背景。
- 移除盖在按钮材质上的实色填充；加入细方向高光、轻色散，保留八次幂和 UV 系数 0.05，以及原 Android 弹簧参数。
- HLSL 重新编译为 `ps_3_0/main`，2696 字节；主项目构建通过，0 警告、0 错误；13 项玻璃回归通过。
- 用户提供的当前截图确认了原生页面渲染，同时暴露表单填充偏实、光学细节不足；据此继续统一输入框/下拉框，并补充背景细轮廓及接触阴影。
- 本轮窗口截图工具连续返回 `foreground window did not report a process id`，未完成最终视觉与交互矩阵。源码/数学测试通过不能视为最终画面验收。
