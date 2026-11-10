# PCL Lite LiquidGlass Core 代码审查

> 原作者的第一轮修复记录：下列五项已处理。桌面协调器与 WGC 分离；区域无锁发布、旧会话按身份隔离、显示器变化触发重建、释放逐项执行。以下保留当时证据及行号。
>
> 维护者复审：截图消失由 WDA_EXCLUDEFROMCAPTURE 直接导致，默认改为可截图的背景模式；实时桌面需独立确认限制。另修复多屏失败耗尽后残留采样与截图排除，以及 affinity 暂时恢复失败后缺少有限重试。设置加入性能确认、四档真实参数和帧率滑块。详见 `LIQUID-GLASS.md`，新增测试替代对“已完全验证”的笼统表述；真实多屏、DPI 和硬件采样仍需验收。

日期：2026-11-10。范围：原生 WPF 桌面采集、材质采样、八次幂 HLSL、主题切换及生命周期。

## 结论

发现 1 项 P1、4 项 P2；本轮为静态审查，未修改实现、未运行新增测试、未完成最终画面验收。维持当前正在运行的预览实例。未给视觉健康度评分，源码不足以证明真实桌面上的对比度、缩放和连续拖动表现。

平台仍为 C# / WPF / HLSL。八次幂折射链路确实保留：Glass.hlsl:32-35 连续平方得到 ratio^8，:42 使用完整源 UV 系数 f=0.05。文字/按钮前景与独立的 TahoeBackdrop 分开。

## 问题（按优先级）

### [P1] 显示器断开后永久停止桌面采样，直至人为重新启用

位置：Plain Craft Launcher 2/Modules/UI/Theme/TahoeDesktopCapture.cs:245、150-158、167-186。

ItemClosed 调用 _failed，Failed 在 UI 线程调用 Stop；Stop 解除位置、尺寸、DPI、可见性等全部观察。没有显示器拓扑事件、恢复状态机或自动重建入口。窗口跨两块屏幕时拔掉其中一块，也会释放另一块仍可用的会话。窗口回到有效显示器后，移动或调整大小不能触发恢复。

建议：区分显示器会话关闭与整个服务失败，移除失效会话并重新枚举有效显示器；对设备丢失采用有界恢复，保留用户开关状态并提供真实状态反馈。显示器拓扑变化应绕过仅比较窗口 bounds 的缓存。

### [P2] 过期失败回调可以停止新启动的会话

位置：Plain Craft Launcher 2/Modules/UI/Theme/TahoeDesktopCapture.cs:150-158。

Failed 将无会话身份的回调投递到 Dispatcher，仅判断 _started。若旧会话先投递错误，用户关闭再打开开关后，该回调执行时 _started 已属于新会话，却仍会 Stop。Present 已校验 region.Version，失败路径缺少对应的会话代数/身份校验。

建议：创建会话时捕获独立的 session generation；UI 执行失败通知前验证该身份仍有效，停用后令旧通知失效。不要在回调执行时读取并捕获新身份。

### [P2] 资源释放异常会中断剩余清理

位置：Plain Craft Launcher 2/Modules/UI/Theme/TahoeDesktopCapture.cs:161-186、307-329。

MonitorCapture.Dispose 顺序释放 session、pool、staging、context、device、winrtDevice；任一个 Dispose 抛错，后续对象不会释放，但 _disposed 已经置 true，字段已清空，无法重试。ClearCaptures 中任一个会话释放抛错，也会阻止其他显示器释放和字典清空。Stop 虽然用 finally 恢复 affinity，后续清空 Source、bitmap 和状态的代码仍会被跳过，异常还可能从 UI 事件/Dispatcher 回调冒出。

建议：逐对象尝试释放并聚合/记录非致命异常；将字典、位图、事件和状态清理放入保证执行的路径，避免清理失败令 UI 崩溃。

### [P2] 采集失败后仍把空背景标为可折射

位置：Plain Craft Launcher 2/Modules/UI/Theme/TahoeDesktopCapture.cs:150-158；Plain Craft Launcher 2/Modules/UI/Theme/TahoeThemeManager.cs:103-106；Plain Craft Launcher 2/Controls/TahoeGlassSurface.cs:251-253。

Failed 清空 TahoeDesktopImage.Source，却不通知主题或可观察 UI 状态。主题仍按 Requested 隐藏壁纸，BackdropSource 仍指向已加载、有尺寸的中性背景 Grid。HasValidSource 只验证视觉关系，因而玻璃仍可报告 Refraction，即使已经没有桌面像素。设置页仅有静态兼容说明，无法分辨正在采样、初始化或失败；一次 DesktopCapture=Live 日志也不能证明后续持续工作。

建议：将桌面帧就绪/失败状态连接到材质与设置页，失败时明确显示静态材质状态；保持用户“不要背景”的要求，不能恢复自制风景或改用 Acrylic 冒充折射。

### [P2] 拖动窗口可能被后台 GPU 回读阻塞

位置：Plain Craft Launcher 2/Modules/UI/Theme/TahoeDesktopCapture.cs:120、244、249-303。

位置/尺寸变化在 UI 线程调用 SetRegion 并等待 _gate；FrameArrived 持有相同锁完成同步 GPU Copy/Map、逐行 Marshal.Copy 和整个缓冲的相同比较。若 GPU Map 等待或回读较慢，UI 将等待后台工作，影响拖动与调整大小。此项是从锁和同步调用链确认的阻塞风险，未声称测得具体掉帧率。

建议：将区域发布与原生资源生命周期同步分离；使用不可变 region 的原子快照/非阻塞发布，使 UI 线程不等待 GPU 回读。释放会话时仍需保证没有回调访问已释放资源。

## 保留与验证边界

- 独立背景源、拒绝自身/祖先/后代采样、sampler ClearValue、未变化坐标缓存及按钮动画收敛后退订已有处理。
- 当前进程 13768 查询时 Responding=True；启动日志含 DesktopCapture=Live; Shader=EighthPower; f=0.05。这只能证明启动与首帧路径。
- 已检查的 TahoeGlassMathTests 与 TahoeGlassSurfaceTests 为 9 项数学参数测试和 4 项 STA 材质生命周期测试；测试项目链接了三个 Controls 源文件，没有链接 TahoeDesktopCapture，也没有对 WGC 会话、失败回调和显示器恢复的测试。因此先前 13 项通过不覆盖上述问题。
- 本轮没有继续尝试已失败的窗口截图工具，没有改动系统 DPI、显示器连接、用户配置或运行中的预览。

## 建议修复顺序

1. 会话身份校验与保证执行的资源释放。
2. 显示器拓扑重建和有界恢复。
3. 状态绑定与真实兼容反馈。
4. UI 区域更新去除 GPU 回读锁等待，并核验实际帧时间。
5. 修复后补会话生命周期与故障恢复回归，再进行真实浅深主题、多屏、缩放及连续拖动验收。
