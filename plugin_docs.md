# PCL Lite 插件文档

插件作者与维护者指南，集中收录 SDK 快速开始、宿主 API、包签名、Mixin、兼容性、测试和排错内容。

宿主可靠性说明（dev）：实例继承循环或超过 64 层时会明确失败；读取此类实例的插件应处理异常，不将错误对象继续用于启动或修改。动画颜色遵循插件控件/页面/窗口资源作用域。现有多选弹框签名及默认行为保留，只有迁移预览显式允许零项确认。旧安装管线仍依赖当前 Minecraft 根目录，跨目录修改前必须先切换目录；不支持时会在写入前拒绝。详情见 [实例可靠性修复](docs/INSTANCE-RELIABILITY.md)。

## 目录

- [插件开发入口](#overview)
- [快速开始](#quickstart)
- [宿主 API 参考](#api-reference)
- [插件依赖与加载顺序](#dependencies)
- [插件版本兼容性](#compatibility)
- [包格式、签名与安全边界](#packaging-security)
- [编译型 Mixin 指南](#mixins)
- [Mixin 目标定位与调试工作流](#mixin-debugging)
- [测试与排错](#testing-troubleshooting)
- [SDK 工程 README（英文）](#sdk-readme)


<a id="overview"></a>
## 插件开发入口

本文面向插件作者和 AI 编码助手。先读“插件开发入口”，再按任务阅读对应专题，减少无关上下文。

### 先确认插件类型

PCL Lite 插件是 **编译后的 .NET 程序集**，不是脚本。普通插件实现 `IPclPlugin`，通过 SDK 宿主 API 注册页面、订阅事件、存取数据等。需要改启动器已有方法时，使用编译期 C# Mixin：它在启动器主窗体创建前应用 IL 补丁，不能用普通 API 插件页面替代。

| 任务 | 只需阅读 |
|---|---|
| 新建、构建、打包最小插件 | [快速开始](#quickstart) |
| 调用启动器提供的全部接口 | [宿主 API 参考](#api-reference) |
| 改启动器内部方法、字段或 IL 调用 | [Mixin 指南](#mixins) |
| 声明插件间依赖和版本要求 | [依赖与加载顺序](#dependencies) |
| 声明适用的 PCL Core 版本范围 | [版本兼容性](#compatibility) |
| 修改启动器已有类型、方法或 IL 并定位 Mixin 故障 | [Mixin 调试工作流](#mixin-debugging) |
| 清单、证书、签名、安装和信任 | [包格式与安全](#packaging-security) |
| 测试、故障定位和提交前检查 | [测试与排错](#testing-troubleshooting) |

### 给 AI 编码助手的约束

1. 先读本页，再只读当前任务对应的专题；不要一次性加载所有文档。
2. 写可编译的 C# 插件，不要把插件改成脚本或假设有未列出的 API。以当前仓库中的 `PCL.Plugin.SDK` 接口为准。
3. 优先调用稳定 SDK；只有确实需要修改启动器已有实现时才写 Mixin。Mixin 目标及签名须从当前源码核对，不能猜方法名、参数、重载或 IL 匹配点。
4. 不要修改 Bedrock / CIK 现有逻辑。涉及启动页、Minecraft 版本、启动流程的改动要确认不触碰 CIK 路径，并执行相关回归检查。
5. 所有宿主内插件都运行在启动器进程并拥有启动器的当前用户权限。签名只验证包内容完整性；不构成沙箱、作者身份认证或安全隔离。
6. 优先复用 SDK、WPF 和启动器已有依赖。避免把大型通用框架、重复运行时、无用资源打进包；延迟创建页面和昂贵对象，异步工作响应取消。
7. 完成代码后按 [测试与排错](#testing-troubleshooting) 验证。不要声称接口可用，除非相应路径有自动化或运行证据。

### API 状态说明

SDK 当前版本号为 API v1，含 12 个公开接口、69 个接口成员。契约覆盖生命周期、日志、独立存储与设置、受配额约束的文本/JSON/二进制/流存储、服务发现、PCL 主题按钮、带生命周期的页面/通知/确认、类型化事件、带请求头和超时的 HTTP、带 SHA-256 校验的下载、启动器信息及 Minecraft 实例/配置查询和带启动选项的启动；没有未实现的接口存根。`PluginApiRuntimeTests` 已覆盖这些宿主接口的正常、取消、错误、网络响应上限、下载原子替换与哈希校验、日志等级、停止生命周期、符号链接拒绝和事件清理路径；完整启动器 UI 与真实 Minecraft 进程仍需发行构建回归。`PluginMixinPackageRuntimeTests` 覆盖签名包安装、Mixin 应用、安全模式跳过、必需配置失败时回滚、拒绝安装目录 junction、用签名插件覆盖 `PCL.Core` 中的真实宿主方法以及释放时恢复。

Mixin 使用编译后的 .NET 程序集修改启动器自身的目标类型、方法和 IL，不是脚本层。当前公开操作包括注入、覆盖、重定向、参数/参数组/常量/局部变量修改、Accessor/Invoker、Shadow/Unique、Slice、优先级和匹配约束。PCL2-Nex 也提供编译型 Mixin packages；本项目不承诺两者每个操作的语义、边界或兼容行为完全等价。请按[Mixin 指南](#mixins)、当前运行时代码及测试确认具体能力。签名包集成测试已验证对 `PCL.Core` 真实方法的覆盖和恢复，但尚未覆盖每一种操作。

清单可把相关配置声明为独立的 `experimentalFeatures` 组。每组默认关闭，用户明确启用并重启后才应用；启停选择会保存，变更需重启生效。某组任一配置读取或应用失败时，运行时回滚该组已应用的配置、记录错误并自动将该组关闭，其他组和插件入口可继续运行。详见[Mixin 指南](#mixins)。

### 关键约定

- 入口：`plugin.json` 的 `entryAssembly` + `entryType`。
- 普通运行时：启用包先在主窗体创建前校验并应用 Mixin；主窗体进入 `WindowCreated` 后才调用 `StartAsync`，退出时调用 `StopAsync`。变更启用状态、更新或卸载需要重启生效。
- Mixin：编译程序集 + 清单中的 `mixinConfig` 或 `mixinConfigs`；在窗体构建前应用。配置文件和程序集都由包签名覆盖。
- 恢复模式：设置环境变量 `PCL_LITE_SAFE_MODE=1` 或使用 `--safe-mode` 启动会跳过已启用插件的加载和 Mixin 应用，方便移除故障插件；包仍会被检查并显示状态。它只影响本次启动，不会永久关闭插件。
- 发布包：`.pclplugin` ZIP 包。推荐使用本地 PFX 证书签名；不提交证书私钥或密码。
- 限额：压缩包最大 100 MiB，解压内容最大 500 MiB，最多 10,000 个包条目；Mixin 配置最多 64 个，每份配置最大 256 KiB、最多 512 个类型名。插件数据树（包括 Storage、Settings 和 Launcher.DownloadAsync）使用每插件 256 MiB、单文件 64 MiB、10,000 文件上限；HTTP 请求/响应正文各限 16 MiB；单次 HTTPS 下载限 64 MiB。市场索引和 GitHub Release API 响应各限 2 MiB，索引最多 1,000 条；市场、包和 Release API 重定向必须保持 HTTPS。下载和存储写入共用并发配额锁，先写临时文件并在完整且 Content-Length 匹配后原子替换。插件自身仍应控制依赖体积和内存占用。


<a id="quickstart"></a>
## 快速开始

### 环境

- Windows / WPF
- .NET 10 SDK
- `PCL.Plugin.SDK` 项目引用

复制仓库的 `PCL.Plugin.Sample` 作为起点，保持项目目标框架和 nullable 配置一致。

### 最小插件

```csharp
using PCL.Plugin.SDK;
using System.Windows.Controls;

namespace Example.Plugin;

public sealed class ExamplePlugin : IPclPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Logger.Info("Example started");
        context.Ui.RegisterPage("home", "Example", () =>
        {
            var page = new StackPanel();
            page.Children.Add(new TextBlock { Text = "Hello" });
            page.Children.Add(context.Ui.CreateButton("Show greeting", () =>
            {
                context.Logger.Info("Greeting button clicked");
                return Task.CompletedTask;
            }, PluginButtonStyle.Highlight));
            return page;
        }, new PluginPageLifecycle(
            OnShown: _ =>
            {
                context.Logger.Info("Example page shown; refresh page data here.");
                return Task.CompletedTask;
            },
            OnHidden: _ =>
            {
                context.Logger.Info("Example page hidden; stop page polling here.");
                return Task.CompletedTask;
            }));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
```

`StartAsync` 保存必要的订阅句柄；`StopAsync` 取消自建工作、释放资源。事件处理可能在非 UI 线程运行，更新 WPF 控件时通过 Dispatcher 切回 UI 线程。所有异步操作都应传递宿主提供的 `CancellationToken`。

标准按钮优先用 `context.Ui.CreateButton`，由宿主创建 PCL 原生主题控件，不依赖 `ColorBrush*` 字符串键。插件页视图在首次打开时创建并复用；通过 `PluginPageLifecycle` 在打开/关闭时刷新数据或停止轮询，动态页面用 `UnregisterPage` 移除。

### 清单

将 `plugin.json` 放在构建输出目录：

```json
{
  "schemaVersion": 1,
  "id": "example.hello",
  "name": "Example Hello",
  "version": "1.0.0",
  "author": "Example author",
  "description": "A small compiled PCL Lite plugin.",
  "apiVersion": 1,
  "entryAssembly": "Example.Plugin.dll",
  "entryType": "Example.Plugin.ExamplePlugin"
}
```

`id` 使用小写 ASCII 字母、数字、点和连字符；`entryAssembly` 必须是包内 DLL 的安全相对路径。使用 Mixin 时，另外阅读 [Mixin 指南](#mixins)。

### 构建、签名、打包

请先建立干净的 Release 输出目录，只放插件运行所需的程序集、依赖、资源、`plugin.json` 和 Mixin 配置。`PCL.Plugin.Tools pack` 会读取输入目录中的所有普通文件（仅跳过已有 `signature.json`），不会自动排除 PDB、XML 文档、测试程序集、测试数据或大型资源；这些文件会计入签名和包大小。不要直接把整个 Debug/测试输出目录发布到市场。打包器会拒绝符号链接，并在压缩包超过 100 MiB 或解压载荷超过 500 MiB 时失败。

请使用仓库工具，不要手工拼签名 JSON：

```powershell
dotnet build .\Example.Plugin.csproj -c Release
dotnet run --project .\PCL.Plugin.Tools -- cert "CN=Example Hello" .\secrets\example-signing.pfx
dotnet run --project .\PCL.Plugin.Tools -- pack .\staging .\secrets\example-signing.pfx .\dist\example.pclplugin
```

工具会交互读取 PFX 密码，不要把密码写进命令行、源代码或仓库。对安装弹窗显示的签名人、签名时间和包信息进行检查；本地自签名只提供完整性校验，不证明现实身份。详见[包格式与安全](#packaging-security)。

### 手动安装与调试

在启动器的插件页面选择本地 `.pclplugin` 安装。启用、禁用、更新、实验功能开关和卸载后重启启动器。测试时使用临时插件 ID、独立数据目录和测试签名证书，避免覆盖日常安装。


<a id="api-reference"></a>
## 宿主 API 参考

所有接口定义在 `PCL.Plugin.SDK`。宿主通过 `IPluginContext` 提供服务。当前 API 不是权限沙箱；插件可使用 .NET 自身能力访问当前用户允许的资源。

### 生命周期和插件信息

- `IPclPlugin.StartAsync(context, cancellationToken)`：启动插件并注册页面/事件。
- `IPclPlugin.StopAsync(cancellationToken)`：释放插件创建的资源。
- `context.Plugin`：ID、名称、版本、作者、描述、API 版本。
- `context.DataDirectory`：插件独立数据根目录。

启动器会逐插件隔离加载错误，但插件运行在同一进程；未捕获的进程级故障仍可能影响启动器。Mixin 在主窗体创建前应用，`StartAsync` 在 `WindowCreated` 后执行。安装、启用状态变更和移除需重启生效。使用 `PCL_LITE_SAFE_MODE=1` 或 `--safe-mode` 可跳过插件加载以恢复启动器。

### 日志、存储和设置

- `Logger.Trace/Debug/Info/Warn/Error`：日志按插件 ID 分类。
- `Storage.ReadTextAsync/WriteTextAsync`：读写插件数据目录下的安全相对路径。
- `Storage.ReadBytesAsync/WriteBytesAsync`、`OpenReadAsync`、`WriteStreamAsync`：受单文件和总量配额约束的二进制/流存储。
- `OpenReadAsync` 返回的 `Stream` 由插件负责 `Dispose`（推荐使用 `await using`）；否则 Windows 可能保留文件句柄，导致后续 `DeleteAsync` 失败。
- `Storage.ExistsAsync`、`DeleteAsync`、`ListAsync`：检查、删除和列出插件数据目录条目。
- `Storage.ReadJsonAsync<T>/WriteJsonAsync<T>`：JSON 文件存取。
- `Settings.GetAsync<T>/SetAsync<T>/RemoveAsync`：以受限 ASCII key 存取设置，底层为插件独立 JSON 文件。

存储读写受每插件 256 MiB 总量、单文件 64 MiB 和 10,000 文件限制；写入先进入临时文件，成功后替换目标。路径不能穿越插件数据目录，符号链接/联接点会被拒绝。

这些限制约束 `Storage` / `Settings` 的宿主读写 API，不是操作系统配额；同权限的编译插件直接用 .NET 文件 API 仍可绕过。`Launcher.DownloadAsync` 与 Storage 共用插件数据树配额锁：单次传输最多 64 MiB，累计不超过 256 MiB，新增文件不超过 10,000 个，并在响应声明 Content-Length 时验证实际长度。

`context.Services` 提供按类型注册、获取和注销的进程内服务；服务实现由插件自行负责生命周期和线程安全，宿主不会把它当作权限边界。

不要把密钥写入日志；对可变数据使用版本字段，并处理首次读取返回 `null` 的情况。

### UI

- `Ui.RegisterPage(id, title, Func<FrameworkElement>)`：注册插件页。工厂在首次打开时调用，创建的视图会复用。
- `Ui.RegisterPage(id, title, Func<FrameworkElement>, PluginPageLifecycle)`：注册带显示/隐藏异步回调的插件页，可用于刷新数据和停止轮询。
- `Ui.UnregisterPage(id)`：移除当前插件注册的页面；若正在显示，宿主先触发隐藏回调。
- `Ui.CreateButton(text, Func<Task>, style)`：创建宿主的 PCL 原生按钮，不暴露内部主题资源键。`PluginButtonStyle` 支持 `Normal`、`Highlight` 和 `Danger`。
- `Ui.ShowNotification(message, kind)`：发送提示；可选点击回调会在用户点击 PCL 提示条时执行一次。
- `Ui.ConfirmAsync(title, message, cancellationToken)`：显示确认提示并返回用户选择。
- `Ui.ChooseAsync(title, message, options, cancellationToken)`：显示确认、取消和可选第三个动作，并返回动作索引。

`PluginDialogOptions` 的确认/取消文案默认留空，宿主会按当前 UI 语言填充；只有传入非空文案时才覆盖宿主默认翻译。

页面 ID 在同一插件内应稳定且唯一。后台事件处理器不能直接操作 WPF 控件；使用 `Application.Current.Dispatcher`。

### 事件

```csharp
IDisposable subscription = context.Events.Subscribe<PluginGameStartedEvent>(
    async (game, ct) =>
    {
        context.Logger.Info($"Started {game.InstanceName} (PID {game.ProcessId})");
        await Task.CompletedTask;
    });
```

宿主事件：`PluginGameLaunchingEvent`、`PluginGameStartedEvent`、`PluginGameExitedEvent`。`PublishAsync<T>` 可在同一启动器进程中的同类型订阅者间发布事件。保存并释放订阅句柄；插件停止时宿主会清理 context 级订阅。单个处理器异常会记录并继续分发。

另外提供 `PluginThemeChangedEvent`、`PluginInstanceListChangedEvent`、`PluginPageNavigationEvent` 和 `PluginStateChangedEvent`，用于订阅主题、实例目录、页面导航和插件状态变化。宿主在主题变更、实例列表完成刷新、主页面切换，以及插件安装/启用/禁用/卸载或实验功能变更时发布这些事件。

### 网络和下载

- `Network.GetAsync`、`Network.SendAsync`：HTTP 请求，返回状态码、原始 `ContentBytes`、`ContentType`、响应头及正文 `Content`。文本正文按响应的 charset 解码；未声明或不支持的 charset 回退 UTF-8。二进制响应使用 `ContentBytes`，不要使用可能含替换字符的 `Content`。
- `Network.GetAsync`/`SendAsync` 的重载可传递请求头和单次超时；宿主仍限制 HTTP 请求和响应正文各 16 MiB。
- `Launcher.DownloadAsync(uri, relativePath, progress, cancellationToken)`：只允许 HTTPS，单次下载不超过 64 MiB，目标限定在插件数据目录，和 Storage 共用配额锁，写入随机临时文件，完整成功且 Content-Length 匹配后原子替换，可报告字节进度。
- `Launcher.DownloadAsync(..., expectedSha256, ...)`：在提交目标文件前验证 SHA-256，校验失败会保留旧文件。

HTTP API 可向插件配置的地址发送请求，不代表该地址可信。避免把令牌放入 URL 或日志；校验远端数据和下载内容。大文件应使用流式处理，不要额外复制整个内容到内存。

`Network` 返回完整字节正文而不是流，响应会在内存中缓冲；`Content` 是方便处理文本的解码视图。请求和响应正文各不超过 16 MiB，插件应请求更小的内容。

### Minecraft 和启动器

- `Minecraft.GetInstancesAsync`：返回 ID、名称、版本、目录和是否选中。
- `Minecraft.GetSelectedInstanceAsync`：返回当前选中的实例。
- `Minecraft.GetInstanceConfigurationAsync(instanceId)`：读取宿主提供的实例配置键值。
- `Minecraft.LaunchAsync(instanceId)`：请求宿主启动对应实例；不存在的 ID 返回 `false`。
- `Minecraft.LaunchAsync(instanceId, options)`：带额外 JVM 参数、环境变量和可选世界名请求启动。
- `Launcher.Name/Version/CoreVersion/DataDirectory/PluginsDirectory`：宿主信息、实际兼容性版本和目录路径。用 `PluginVersionRange.Satisfies(Launcher.CoreVersion, manifestRange)` 检查插件声明的兼容范围。
- `Launcher.OpenUrl`：支持 HTTP(S)。
- `Launcher.OpenPath`：打开当前用户有权访问的已有文件或目录；插件可传任意路径，宿主不将它限制在插件目录。

启动实例是用户可见行为；避免未经用户操作自动启动或反复触发。

### Mixin 不是宿主 API

Mixin 是编译程序集中的方法补丁，见 [Mixin 指南](#mixins)。与 `IPluginContext` 不同，它会改写启动器进程中的方法执行，兼容性和故障影响范围更大。

### 覆盖状态

当前宿主 API 的专项探针覆盖正常、失败、取消、资源释放和桥接行为；完整 UI、真实下载服务和真实 Minecraft 启动仍需发行构建回归。`PluginMixinPackageRuntimeTests` 覆盖签名包经安装和加载后应用 Mixin、必需配置失败回滚及释放时恢复目标方法；该单项集成测试不代表所有 Mixin 操作均已覆盖。提交新 API 或 Mixin 操作时，增加相应运行时测试；参见[测试与排错](#testing-troubleshooting)。

### 接口实现与验证索引

新增或修改接口前，先沿下表找到生产适配器和探针。多数 API 行为通过同一个已签名测试插件端到端调用；WPF 页面导航和真实游戏启动仍需按[测试与排错](#testing-troubleshooting)做发行版烟测。

| SDK 接口 | PCL Lite 生产实现 | 自动化验证 |
|---|---|---|
| `IPclPlugin` | `PCL.Core/Plugins/PluginRuntimeManager.cs`：`_PreparePluginAsync`、`_StartPluginAsync`、`DisposeAsync` 停止路径 | `PluginApiRuntimeTests.LoadedPluginCanUseEveryHostApiAndDisposesEventSubscriptions`；依赖顺序见 `PluginPackageTests.RuntimeStartsCompiledPluginsInDependencyOrder` |
| `IPluginContext`、`IPluginMetadata` | `PluginRuntimeManager.cs`：`PluginContext`、`PluginMetadata` | `PluginApiRuntimeTests.LoadedPluginCanUseEveryHostApiAndDisposesEventSubscriptions` |
| `IPluginServices` | `PluginRuntimeManager.cs`：`PluginServices` | 同上（按类型注册、获取和注销） |
| `IPluginLogger` | `PluginRuntimeManager.cs`：`PluginLogger` | 同上（验证五个日志等级） |
| `IPluginStorage` | `PluginRuntimeManager.cs`：`PluginStorage` | 同上（文本/JSON/二进制/流、列出/删除、路径限制、取消、并发写入和失败清理） |
| `IPluginSettings` | `PluginRuntimeManager.cs`：`PluginSettings` | 同上（读写、删除和取消） |
| `IPluginUi` | `PluginRuntimeManager.cs`：`PluginUi`、`PluginPageRegistry`；生产 WPF 桥接在 `Plain Craft Launcher 2/Program.cs` 的 `_ConfigurePluginBridge`，插件视图和生命周期在 `Plain Craft Launcher 2/Pages/PagePlugins/PagePluginsInstalled.cs` 管理 | 同上（UI 桥接探针）；实际按钮外观和页面导航仍需启动器烟测 |
| `IPluginEvents` | `PluginRuntimeManager.cs`：`PluginEvents`、`PublishAsync` | 同上（订阅、分发、异常隔离、插件停止后清理） |
| `IPluginNetwork` | `PluginRuntimeManager.cs`：`PluginNetwork` | 同上（HTTP、二进制、字符集、请求头/超时、错误状态、响应大小上限） |
| `IPluginMinecraft` | `PluginRuntimeManager.cs`：`PluginMinecraft`；生产实例和启动桥接在 `Plain Craft Launcher 2/Program.cs` 的 `_ConfigurePluginBridge` | 同上（实例、选中项、配置和启动选项使用替身桥接）；真实实例与进程仍需烟测 |
| `IPluginLauncher` | `PluginRuntimeManager.cs`：`PluginLauncher` | 同上（信息、打开路径及 HTTPS 下载限制/原子写入/SHA-256 校验） |

运行插件探针：`dotnet test .\PCL.Core.Test\PCL.Core.Test.csproj --filter FullyQualifiedName~PluginApiRuntimeTests`。测试通过只证明测试桥接和当前适配器契约；若改动生产 WPF 桥接，还要构建启动器并验证页面线程、实例 ID 映射和启动结果。


<a id="dependencies"></a>
## 插件依赖与加载顺序

插件清单可以声明必须同时安装并启用的插件依赖。宿主在执行任何插件 Mixin 前验证依赖、版本和依赖环，然后按依赖优先顺序应用 Mixin、调用生命周期。

```json
{
  "id": "example.addon",
  "version": "1.4.0",
  "dependencies": {
    "example.library": "^1.2.0"
  }
}
```

每个插件最多声明 64 个依赖。依赖 ID 使用清单的插件 ID 规则；不能自引用，ID 不区分大小写且不能重复。版本要求为 SemVer 2.0，范围最多 256 字符，支持：

- 精确版本：`1.2.3`
- 比较条件交集：`>=1.2.0 <2.0.0` 或 `>= 1.2.0, < 2.0.0`
- 插入符范围：`^1.2.3`；兼容上界按 SemVer major-zero 规则收窄
- 波浪范围：`~1.2.3`，即大于等于该版本且低于下一个 minor
- 任意 SemVer：`*`

当前管理器**不会自动下载或更新依赖**。作者应在测试和发布说明中清楚列出需要先安装的插件。依赖插件的入口程序集会共享给消费者的加载上下文，因此消费者可在编译时引用依赖插件公开的 .NET 类型，而不用把同一个依赖 DLL 再复制进包；同名依赖程序集冲突会拒绝加载。缺失、禁用、版本不匹配、依赖环或依赖插件加载失败时，依赖它的插件会显示具体错误并跳过加载；其 Mixin 不会留下部分补丁。依赖插件先应用 Mixin、先执行 `StartAsync`，停止时按相反顺序释放。

禁用、卸载或更新依赖后重启启动器，依赖关系才会重新检查。插件依赖不是沙箱或权限边界；插件仍在同一个启动器进程和用户权限下运行。不要通过直接读取其他插件的私有数据目录来建立依赖协议。

### 验证

`PluginDependencyResolverTests` 覆盖 SemVer 范围、预发布版本、依赖拓扑、缺失/禁用/不匹配依赖以及环和下游阻断。`PluginPackageTests.RuntimeStartsCompiledPluginsInDependencyOrder` 通过真实签名包安装、加载两个不同编译程序集，确认依赖程序集可共享且依赖先启动；`RuntimeBlocksDependentsWhenProviderLifecycleFails` 确认依赖启动失败会阻止消费者运行。


<a id="compatibility"></a>
## 插件版本兼容性

插件的 `apiVersion` 标识 SDK 契约，必须与宿主 API 版本精确相等。需要比 API 版本更精细地限制启动器内部实现时，可在 `plugin.json` 声明 `pclCoreVersion`：

```json
{
  "apiVersion": 1,
  "pclCoreVersion": ">=1.7.0 <1.9.0"
}
```

`pclCoreVersion` 使用受限 SemVer 2.0 范围语法：精确版本、比较条件交集、`^`、`~` 或 `*`；最多 256 字符。省略时不额外限制 PCL Core 版本。开发者应按 Mixin 目标方法和 IL 的真实测试范围声明兼容区间，不要为了通过校验而写过宽范围。

超出所声明范围的包仍可安装，以便用户之后切换到兼容版本；确认界面会显示警告。当前启动器版本不会加载该插件，也不会应用它的 Mixin。安装后再次启动时会重新检查，因此安全模式和运行时行为一致。当前兼容值来自实际嵌入的启动器元数据版本，不能根据上游 PCL2 版本推断。

版本范围是开发者声明，不是自动兼容性证明，也不替代针对每个目标版本运行集成测试。详见 [Mixin 指南](#mixins) 和[测试与排错](#testing-troubleshooting)。


<a id="packaging-security"></a>
## 包格式、签名与安全边界

### 包结构

`.pclplugin` 是 ZIP 容器，至少包含：

- `plugin.json`：插件 ID、版本、入口程序集/类型、API 版本；可声明 Mixin 配置。
- 入口程序集和需要的插件依赖。
- 可选资源、WPF 资源和 JSON 配置。
- 签名包包含 `signature.json`。

包校验拒绝非法清单、重复 ZIP 条目、路径穿越、超限包和不匹配签名。签名覆盖规范化清单以及所有载荷文件的 SHA-256。Mixin 配置必须作为载荷打包，不能在安装后从任意路径加载。

限额：压缩包 100 MiB，解压总量 500 MiB，最多 10,000 个条目，`plugin.json` 最大 64 KiB，签名文件最大 64 KiB；单个包内路径最多 1,024 字符、最多 32 层、每段最多 128 字符。每份 Mixin 配置最大 256 KiB、最多 512 个类型名，清单最多 64 份 Mixin 配置和 64 个插件依赖；依赖版本范围和 `pclCoreVersion` 各最多 256 字符。清单的作者最多 128 字符、描述最多 4 KiB、入口程序集/类型最多 512 字符，网站和 GitHub Release 资产地址最多 2 KiB 且必须使用 HTTPS。插件数据树（`Storage` / `Settings` / `DownloadAsync`）限制每插件 256 MiB、单文件或单次下载 64 MiB、10,000 文件；HTTP 请求/响应正文各 16 MiB。下载与存储写入共用配额锁，先写临时文件，并在成功且 Content-Length 匹配后原子替换。以上宿主 API 限制不是操作系统配额，同进程插件可自行调用 .NET 文件 API 绕过。不要用接近上限的限额替代合理依赖；建议按需引用依赖并从输出中移除调试符号、测试数据和重复资源。

插件市场索引限制为 2 MiB、最多 1,000 个条目；条目名称/作者最多 128 字符、版本最多 64 字符、描述最多 4 KiB。GitHub Release API 响应限制为 2 MiB，单个 release 最多 1,000 个资产。市场索引、包下载和 Release API 即使发生重定向也必须保持 HTTPS。安装后启动时会拒绝插件目录中的符号链接和 Windows 联接点。签名证书仅支持 RSA，密钥至少 2,048 位。

### 证书和信任

- 使用 `PCL.Plugin.Tools cert` 创建自签名 PFX；PFX 私钥和密码只保存在开发者本地安全位置。RSA 密钥至少 2,048 位，工具默认生成更强密钥。
- 使用 `PCL.Plugin.Tools pack` 从干净的 Release staging 目录生成签名包；目录仅放运行所需的程序集、依赖、资源、`plugin.json` 和 Mixin 配置。打包器会读取输入目录中的所有普通文件（只跳过已有的 `signature.json`），不会自动筛除 PDB、XML 文档、测试程序集、测试数据或大型资源；这些文件会被签名并计入包体积。
- 不提交 `.pfx`、密码、私有发布凭据或开发者机器绝对路径。
- 签名时间由签名方写入，不是可信时间戳。GitHub Release 的资产摘要和发布时间可作为独立的发布记录佐证，但不能证明作者身份，也不能证明真实签名时间。
- 签名无效或包内容发生变更必须拒绝；无签名包需用户显式确认。更新已签名插件时，如果签名指纹发生变化或移除签名，确认界面会以警告样式展示旧、新指纹/未签名状态，用户需要再次确认。安装 UI 显示的信息只是提示，不是系统级证书信任。
- 安装状态会记录安装来源类型、市场索引 URL、下载包 SHA-256 和安装时间；旧版本没有这些来源字段时会显示来源未知。市场页按 SemVer 比较版本，区分已是最新、升级和降级。同版本包哈希或市场源变更时仍允许重新安装，但确认窗会提示内容变化或来源变化；降级确认会展示当前和目标版本。
- 市场版本更新会先暂存插件文件和对应状态，启动器重启时替换插件目录后再提交状态。替换或状态校验失败时保留旧插件状态并记录错误，待暂存包可重试；不要手动编辑 `.pending-install` 或 `.plugin-state` 内容。

### 威胁边界

插件在启动器同一进程、同一用户权限下运行；Mixin 能修改启动器的方法。当前设计没有进程隔离或权限沙箱。插件可以调用普通 .NET API，不能用签名阻止它访问用户可访问的数据或启动进程。不要把“不安全插件可随意安装”误解为“宿主限制了其行为”。

GitHub hash、包签名和发布者身份是不同概念：哈希验证文件摘要；签名验证文件内容未被改动；来源/开发者信任另需可信的注册与审核机制。

### 安全发布清单

- 审核 diff、清单、所有依赖和 Mixin handler。
- 在干净输出目录构建并打包；运行工具的包检查和测试。
- 检查包内没有密钥、用户数据、私有符号文件或不必要的大资源。
- 保存并备份签名 PFX；不要把签名私钥上传到插件市场仓库。
- 对安装弹窗展示的信息以及市场资产摘要进行核对。
- 对 Mixin 目标版本和应用结果做逐版本测试；补丁不兼容时发布修正版，不静默重定向到另一个目标。
- 若插件导致启动失败，先使用 `PCL_LITE_SAFE_MODE=1` 或 `--safe-mode` 启动，确认启动器恢复后再禁用或移除插件。


<a id="mixins"></a>
## 编译型 Mixin 指南

Mixin 由插件程序集中的 C# 属性和处理器构成，不是脚本。启动器在主窗体创建前加载启用包并应用 Mixin 配置；普通 `IPclPlugin.StartAsync` 生命周期在 `WindowCreated` 后开始。Mixin 与宿主 API 插件共享同一个 `.pclplugin` 包和签名。`PCL_LITE_SAFE_MODE=1` 或 `--safe-mode` 会跳过插件和 Mixin 加载，可用于恢复启动器。

清单还可以用 `experimentalFeatures` 把一组 Mixin 配置声明为独立的实验功能。新组默认关闭；用户启用后需要重启，运行时才会应用。某个实验组应用失败时，只回滚该组已经应用的配置，记录错误并将该组持久化为关闭，插件的其他 Mixin 和普通入口仍可继续运行。这个回滚范围不等于进程沙箱，也不能抵消已经执行过的任意插件代码。

### 基本结构

首次添加或排查补丁时，按[Mixin 调试工作流](#mixin-debugging)从目标源码、方法重载和 IL 匹配点开始，先写目标行为测试，再选择最窄的注入操作。本文说明 Mixin 的声明与约束；调试专题说明怎样把这些约束落实到当前启动器构建。

1. 在插件项目中引用 `PCL.Plugin.SDK`，使用 `PCL.Mixin` 属性。
2. 在 `plugin.json` 声明 `mixinConfig`（单个）或 `mixinConfigs`（多个）。
3. 将 JSON 配置和程序集一并放入包并签名。
4. 通过实际打包后的插件，在测试版启动器上验证目标方法、冲突和恢复行为；使用实验组时还要分别验证默认关闭、启用后的重启和失败后单组回滚。

每份 Mixin JSON 限制为 256 KiB、1–512 个 Mixin 类型名；清单最多声明 64 份配置，配置路径不区分大小写且不能重复。配置中的 `package`、`plugin` 和每个类型名最长 256 个字符，`injectors.defaultRequire` 必须为 0–1000，类型名不能重复。配置文件必须在包内并被签名覆盖。

配置字段以 JSON 名称为准：

| 字段 | 类型 | 默认值 / 限制 |
|---|---|---|
| `required` | bool | `true`；仅表示补丁应用失败时是否使该插件失败 |
| `package` | string | 空字符串；类型名解析用的命名空间前缀，最多 256 字符 |
| `mixins` | string 数组 | 必填，1–512 个唯一类型名，每项最多 256 字符 |
| `priority` | int | 默认 `1000`；控制补丁顺序，范围 -10,000–10,000 |
| `injectors.defaultRequire` | int | 默认 `1`，范围 0–1000 |
| `plugin` | string/null | 可选配置处理器类型名，最多 256 字符 |

清单属性名为 `mixinConfig`（单个路径）或 `mixinConfigs`（路径数组）。混合使用时路径总数也不能超过 64。无效/缺失 JSON 在安装或启动校验阶段拒绝；`required:false` 不会让损坏的配置文件变成可忽略错误。

### 独立实验功能组

如需让部分 Mixin 可单独选择，不要把它们放入始终应用的 `mixinConfig(s)`；在 `plugin.json` 的 `experimentalFeatures` 中声明命名组，并为每组提供 1–64 个独立配置路径。每个实验组默认关闭，用户启用并重启后才会应用；开关选择会持久保存，变更需重启生效。

```json
{
  "experimentalFeatures": [
    {
      "id": "experimental.patch",
      "name": "Experimental patch",
      "description": "Optional changes to launcher behavior",
      "mixinConfigs": ["mixins/experimental-patch.json"]
    }
  ]
}
```

实验配置路径须存在于签名包内，且不能与常规 Mixin 配置或其他实验组的路径重复。某组配置读取或应用失败时，宿主会回滚该组此前已应用的配置、记录错误并持久关闭该组；普通插件入口和其他成功的 Mixin 组仍可继续运行。失败组可在已安装插件页重新启用后重试。此回滚只撤销该组的 Mixin 注册，不会撤销它运行过的任意代码，也不提供进程隔离。

示意清单：

```json
{
  "entryAssembly": "Example.Plugin.dll",
  "entryType": "Example.Plugin.ExamplePlugin",
  "mixinConfigs": ["mixins/launcher.json"]
}
```

示意配置（目标与类名须按真实程序集填写）：

```json
{
  "required": true,
  "package": "Example.Plugin.Mixins",
  "mixins": ["ExampleMixin"],
  "priority": 1000,
  "injectors": { "defaultRequire": 1 }
}
```

示意处理器：

```csharp
using PCL.Mixin;

[Mixin("PCL.TargetType, PCL.Core")]
public static class ExampleMixin
{
    [Inject("TargetMethod", At = MixinAt.Head, Cancellable = true)]
    public static void BeforeTarget(CallbackInfo callback)
    {
        // 只在你确认目标版本与语义后修改 callback。
    }
}
```

以上目标名只是占位示例。必须对照当前启动器代码检查目标类型完整名称、程序集、方法重载、参数绑定和目标 IL。目标解析失败、匹配数量不符或处理器签名错误时，必需配置应让该插件补丁失败并回滚；可选配置可跳过并记录警告。

### 支持的操作类型

具体限制由 `PCL.Mixin` 运行时验证，能力包括：

- `Inject`：方法入口/返回点及指定 IL 匹配点注入处理器，可取消或修改结果。
- `Overwrite`：替换目标方法。
- `Redirect`：重定向调用、字段或构造指令。
- `ModifyArg` / `ModifyArgs`：修改某个调用参数或整组参数。
- `ModifyConstant`：修改目标方法中的常量。
- `ModifyVariable`：修改局部变量加载/存储值。
- `Accessor` / `Invoker`、`Shadow`、`Unique`：访问目标成员、调用目标方法和声明 Mixin 成员。
- `Slice`、优先级和匹配数量约束：收窄目标 IL 范围并检测意外匹配。

不要以“某属性可编译”推断某注入点有效。每个操作都应有针对目标方法的测试，包括匹配失败、重载歧义、冲突排序以及回滚。

#### 值类型调用和字段重定向

- `ModifyArg` / `ModifyArgs` 改写值类型实例方法调用时，运行时保留调用栈中的托管指针接收者。
- `Redirect` 值类型实例方法调用时，处理器第一个参数应为 `ref TargetStruct`；普通引用类型接收者仍使用 `TargetClass`。
- `stfld` 写值类型字段时，处理器接收者应为 `ref TargetStruct`，后续参数为待写字段值。`ldfld` 的栈形状受目标 IL 生产方式影响；应检查当前目标指令，并分别测试读与写，不能只凭字段声明类型推断处理器签名。
- 当前自动化覆盖 struct 方法调用的 `ModifyArg` / `ModifyArgs` / `Redirect`，以及局部变量 struct 的字段读写重定向。其他编译器、嵌套 struct 或地址加载形式必须以目标构建的实际测试为准。

### 安全、兼容和资源要求

- Mixin handler 在启动器进程内执行，和启动器拥有相同权限；它可以触达当前用户可访问的文件和进程。
- Mixin 会影响所有调用目标方法的路径。错误的 `Overwrite`、宽泛的 `Redirect` 或未约束的 IL 匹配可能导致启动崩溃。
- 包签名只证明当前包内容与签名时的字节一致；自签名不能证明作者身份，也不能限制代码行为。
- `required: true` 配置失败应回滚该插件注册的补丁；`required: false` 只用于确实可选的扩展，不要用它掩盖必需补丁故障。
- 通过版本检测明确支持的 PCL Core 版本；目标实现变化时应拒绝应用或要求用户确认，不要悄悄猜测 IL。
- 保持补丁少而窄。避免静态持有大对象/大图像，不要在 handler 中做阻塞 I/O、长计算或同步等待。
- 不改 Bedrock / CIK 现有逻辑。触碰 Minecraft 启动流程时，应增加明确的 CIK 不变回归断言。

必需配置的补丁应用失败会回滚该插件已应用的补丁；可选配置的补丁应用失败会记录警告并继续。安全模式会在应用补丁前跳过插件。`PluginMixinPackageRuntimeTests` 已通过真实签名包安装及加载路径验证 Mixin 应用、必需配置失败回滚和释放时恢复目标方法；另有用签名插件实际覆盖 `PCL.Core.PluginRuntimeManager.GetInstalled` 并在退出时恢复的宿主方法测试。测试仍未覆盖每种 Mixin 操作，也不证明任意已执行代码都能安全卸载。Mixin 没有沙箱。


<a id="mixin-debugging"></a>
## Mixin 目标定位与调试工作流

本专题帮助插件作者和 AI 对当前 PCL Lite 构建编写可验证的启动器补丁。先读 AI 总入口 [`README.md`](#overview)，再按需参照 [Mixin 声明与约束](#mixins)。Mixin 是进程内编译代码，目标变更或匹配不确定时应停止安装，不要靠扩大匹配范围“试到能用”。

### 1. 固定要支持的启动器版本

记录当前启动器版本和 commit/发行包哈希。插件清单通过 `pclCoreVersion` 声明支持范围；不要把一个开发构建上的成功推断成其他版本兼容。升级启动器后重新跑目标测试。未识别或超出范围时按兼容性指南阻止或要求用户确认。

### 2. 从当前源码解析目标

先从触发行为开始搜索，而不是猜类型名：

```powershell
rg -n '可见文本|日志关键字|调用的 UI 文案' 'Plain Craft Launcher 2' PCL.Core
```

记录目标类型完整名称、程序集、方法声明、可见性、静态/实例状态、参数顺序和返回类型。重载方法必须按参数类型精确指定；本引擎对开放泛型和 ref-return 边界目标有限制。接着检查调用者和所有影响路径，判断改动是否会碰到启动流程、Bedrock 或 CIK；Bedrock/CIK 现有逻辑必须保持不变。

### 3. 检查实际 IL 和注入点

源码不等于 Release IL：优化、编译器版本、构建配置会改变常量、局部变量、分支和调用形式。对准备支持的实际发行程序集用 ILSpy 或同等 IL 查看器检查目标方法，记录目标指令（opcode）、被调用成员完整签名、字段/常量、局部变量索引和分支。源代码修改后重新查看 IL；不要把旧版本截图或调试构建当作当前证据。

把检查结果映射到一个明确的选择器：`MixinAt`、`Target`、`Opcode`、`Ordinal` 和/或 `Slice`。优先选最具体的成员签名和最窄的 Slice；声明 `Require`/`Expect` 以便目标漂移时显式失败。避免只凭“附近有个相似调用”使用无约束的 `Redirect` 或常量匹配。

### 4. 为每个操作构造可重复测试

在 `PCL.Core.Test/Plugins/MixinRuntimeTests.cs` 增加独立、`NoInlining` 的目标夹具，确保测试目标有可预测的 IL。每个操作至少断言：

- 应用前后的目标结果或副作用；
- 精确重载、操作码、序号和 Slice 只命中预期指令；
- 缺失/多重匹配时的 `Require`、`Expect`、`Allow` 结果；
- 与另一个补丁冲突时的顺序、诊断及失败回滚；
- `LocalCapture`、ref/out 和值类型 receiver 等相关栈/类型边界；
- `Dispose` 或配置回滚后原始目标行为恢复。

不要把多个待验证操作塞进一个复杂 handler；分开应用和断言可让失败定位到具体选择器。目标夹具需要复现线上方法的关键栈形状，不能仅测一个返回值相同但 IL 不同的替代方法。

```powershell
dotnet test .\PCL.Core.Test\PCL.Core.Test.csproj --filter FullyQualifiedName~MixinRuntimeTest
dotnet test .\PCL.Core.Test\PCL.Core.Test.csproj --filter FullyQualifiedName~PluginMixinPackageRuntimeTests
```

### 5. 验证签名包和实际发行版

测试单元运行时后，构建并签名 `.pclplugin`，在独立的启动器数据目录安装。先运行 safe mode 确认故障恢复路径，再正常启动检查插件页状态和日志。`PluginRuntimeManager` 会记录配置应用结果与警告；搜索该插件 ID 对应的 `插件 Mixin 已应用`、`Mixin 诊断`、`加载或应用 Mixin 失败` 信息，并对照匹配数和目标方法。

最后在每个声明支持的 Release 启动器版本中重复安装、启动、功能行为、禁用/卸载及安全模式检查。真实 UI dispatcher 和真实 Minecraft 进程行为不由 `MixinRuntimeTests` 的纯运行时夹具证明。如果改动可能触及启动路径，执行对应回归并确认 Bedrock/CIK 行为未变。若发行版验证失败，撤销/禁用该补丁或收窄版本范围；不要静默扩大注入范围。

### 失败诊断顺序

1. 包签名和清单是否有效，Mixin JSON 是否在已签名包内。
2. 当前运行的启动器版本是否在声明的 `pclCoreVersion` 范围中。
3. 类型解析结果是否是期望程序集中的唯一类型；是否使用正确的嵌套类型全名。
4. 目标方法是否选择了正确的重载，handler 的 receiver/参数/返回类型是否匹配。
5. 目标 IL 中选择器是否命中，命中数量是否符合 `Require`/`Expect`/`Allow`。
6. 是否存在优先级冲突、前置插件失败或配置级回滚；查看对应插件 ID 的启动器日志。
7. 用 safe mode 恢复启动器，先禁用问题插件，再对相同发行版重跑最小测试。


<a id="testing-troubleshooting"></a>
## 测试与排错

### 必做验证

插件不应只以“能编译”作为可用证据：

1. 构建 Release 插件和 `PCL.Plugin.Tools`，检查最终输出目录与清单路径匹配。
2. 生成签名包并检查；篡改任一 payload 后验签必须失败。
3. 将真实包安装到隔离数据目录，验证启动、禁用、重启、更新、移除和卸载回滚。
4. 每个 API 至少测试正常结果、错误、取消和资源清理；异步 UI 操作验证线程切换。
5. 每个 Mixin 单独覆盖匹配、签名、运行效果、冲突、必需配置失败回滚和启动器继续运行。`PluginMixinPackageRuntimeTests` 已覆盖签名包安装、Mixin 加载应用、安全模式跳过补丁、后续必需配置失败回滚以及释放时恢复目标方法；这项集成测试不替代各 Mixin 操作的独立用例。
6. Minecraft 启动流程插件必须执行 Bedrock / CIK 不变回归测试。

### 仓库验证命令

```powershell
dotnet test .\PCL.Core.Test\PCL.Core.Test.csproj
dotnet build .\Plain Craft Launcher 2\Plain Craft Launcher 2.csproj
dotnet build .\PCL.Plugin.Tools\PCL.Plugin.Tools.csproj
dotnet build .\PCL.Plugin.Sample\PCL.Plugin.Sample.csproj
```

如果因当前分支依赖或环境导致命令无法执行，应记录完整错误和未覆盖范围，不要把上次测试结果当成本次证据。

### 常见故障

| 现象 | 检查 |
|---|---|
| 包无法安装 | `plugin.json` 的 ID、API 版本、入口 DLL/类型和相对路径；ZIP 是否含重复或越界条目 |
| 签名无效 | 打包后是否改过任何文件、是否使用正确证书、是否把旧 `signature.json` 混入输入目录 |
| 插件显示已启用但未运行 | 已安装插件页错误文本、启动器日志中插件 ID、入口类型构造和 `StartAsync` 异常 |
| 插件页面没有显示 | `RegisterPage` 是否成功、page ID 是否冲突、工厂是否在创建视图时抛异常 |
| Mixin 报目标不存在 | 当前发行版中的类型名、程序集名、目标方法/重载及包内 `mixinConfig(s)` 路径 |
| Mixin 匹配数量变化 | 目标版本 IL 是否改变、匹配范围是否过宽、`Require`/`Expect` 是否符合预期 |
| 下载失败或文件残缺 | URI 是否 HTTPS、取消/权限/磁盘空间/响应错误；不要假定失败下载会自动恢复远端文件 |
| 启动器因插件无法启动 | 使用 `PCL_LITE_SAFE_MODE=1` 或 `--safe-mode` 跳过插件；进入插件页后禁用/卸载故障包并重启 |

宿主 API 专项测试 `PluginApiRuntimeTests.LoadedPluginCanUseEveryHostApiAndDisposesEventSubscriptions` 覆盖插件生命周期和停止、独立存储/设置、日志等级、UI 桥接、事件、HTTP 16 MiB 响应上限、Minecraft 桥接、HTTPS 下载 64 MiB 上限、取消、超限声明 Content-Length、声明长度与实际字节数不符时保留原文件，以及临时文件原子清理。包与市场安全测试覆盖低于 2048 位 RSA 拒绝、市场索引/Release JSON 响应限制、条目上限和 HTTPS 重定向限制。Mixin 集成测试 `PluginMixinPackageRuntimeTests.SignedPackagesApplyMixinRollbackFailedPluginAndRestoreHostMethodOnShutdown` 验证签名包安装和加载、Mixin 应用、必需配置失败回滚，以及管理器释放时恢复目标方法；另有安全模式跳过补丁测试。测试未覆盖存储 256 MiB 总量、64 MiB 单文件及 10,000 文件的极限压力；这些配额也不是对任意同进程文件访问的强制隔离。完整真实 UI 和游戏进程仍需发行构建回归；每种 Mixin 操作还需要各自的目标、匹配和运行效果用例。

依赖版本范围和拓扑加载由 `PluginDependencyResolverTests` 覆盖；`PluginPackageTests.RuntimeStartsCompiledPluginsInDependencyOrder` 通过两个独立程序集验证依赖先启动且其公开类型可由消费者加载；`RuntimeBlocksDependentsWhenProviderLifecycleFails` 验证提供方启动失败后的传播。该管理器当前不会自动下载或更新依赖。

市场更新状态由 SemVer 比较驱动：`PluginPackageTests.MarketCandidatesDistinguishCurrentUpgradeDowngradeAndSourceOrPackageChanges` 覆盖当前版本、升级、降级、市场源变化和同版本包摘要变化；`LocalInstallRecordsProvenanceAndDeclinedDowngradeKeepsInstalledPackage` 检查来源、包摘要、安装时间以及拒绝降级后旧安装保持不变。`MarketIndexRecordsResolvedSourceAndRequiresSemVerVersions` 验证索引来源记录与 SemVer 校验。更新暂存应用后会校验包文件与待提交状态一致，再提交来源记录。

`PluginPackageTests.IncompatibleCoreRangeIsShownAtInstallAndSkippedAtStartup` 验证清单声明不兼容的 PCL Core 版本范围时，安装确认展示警告而运行时不加载插件。

`MixinRuntimeTest.ModifyArg_ShouldPreserveManagedPointerReceiverForStructCalls` 验证 struct 实例方法的 `ModifyArg`、`ModifyArgs` 和 `Redirect`，并覆盖局部 struct 的字段读取/写入重定向签名；不要把单个 IL 形态的测试扩大解释为对所有编译器和嵌套 struct IL 的保证。

独立实验 Mixin 组由 `PluginPackageTests.ExperimentalFeatureManifestRequiresUniqueSignedMixinConfigurationGroups`、`ExperimentalFeatureIsOffByDefaultAndPersistsUserChoiceAcrossRestart` 和 `FailedExperimentalFeatureRollsBackOnlyItsMixinGroupsAndKeepsPluginRunning` 覆盖：分别验证清单路径唯一性、默认关闭与重启持久化、失败时仅回滚该组并保持插件入口运行。

### 贡献和本地留痕

每个功能改动应有对应的测试和文档。提交前检查 `git diff`，只提交本功能文件，避免把工作区其他改动一起纳入。不要提交 PFX、插件运行数据、打包产物或私钥。保持 Mixin 引擎有明确来源归属，并保留适用许可证文本。


<a id="sdk-readme"></a>
## SDK 工程 README（英文）

The SDK targets .NET 10 for Windows and exposes the versioned `PCL.Plugin.SDK` API. Plugins run inside the launcher process with the same permissions as the launcher; package signatures do not sandbox plugins.

### Create a plugin

Reference `PCL.Plugin.SDK.csproj`, implement `IPclPlugin`, and add a `plugin.json` file to the build output. The manifest's `entryAssembly` and `entryType` identify the implementation. Set the optional `releaseAssetUrl` to the final GitHub Release asset URL when you want installation to show independent GitHub hash and publication-time corroboration.

```csharp
using PCL.Plugin.SDK;

public sealed class ExamplePlugin : IPclPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Logger.Info("Example plugin started");
        context.Ui.RegisterPage("home", "Example", () => new System.Windows.Controls.StackPanel
        {
            Children =
            {
                new System.Windows.Controls.TextBlock { Text = "Hello from a plugin" },
                context.Ui.CreateButton("Show message", () =>
                {
                    context.Ui.ShowNotification("Hello from PCL Lite");
                    return Task.CompletedTask;
                }, PluginButtonStyle.Highlight)
            }
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

`IPluginContext` provides plugin metadata, logging, plugin-specific files and settings, UI notifications, pages, PCL-themed buttons, typed events, HTTP, launcher information, and Minecraft instance query/launch methods. `Launcher.CoreVersion` exposes the actual host compatibility version for checking a manifest's `pclCoreVersion` range at runtime. Use `Ui.CreateButton` for standard launcher buttons; it creates the actual PCL control without exposing internal resource keys. `PluginPageLifecycle` supports refresh-on-show and stop-on-hide callbacks, and `UnregisterPage` removes a page dynamically. `PluginGameLaunchingEvent`, `PluginGameStartedEvent`, and `PluginGameExitedEvent` report game lifecycle changes.

### Sign and package

Build `PCL.Plugin.Tools`, then create a private signing certificate and package the build output directory:

```powershell
dotnet run --project PCL.Plugin.Tools -- cert "CN=Your Plugin Name" .\plugin-signing.pfx
dotnet run --project PCL.Plugin.Tools -- pack .\staging .\plugin-signing.pfx .\plugin.pclplugin
```

The tool prompts for the PFX password without echoing it. Keep the PFX and password private and backed up. A certificate can be used to sign packages, but a self-signed certificate does not establish the author's real-world identity.

Create a clean Release staging directory containing only the files the plugin needs at runtime, then pass that directory to `pack`. The packer includes every regular file under the input directory (except an existing `signature.json`); it does not automatically exclude PDBs, XML documentation, test assemblies/data, or large assets. All included files are signed and count toward package limits.

The package signature covers a normalized manifest and the SHA-256 hash of every payload file. The signed time is supplied by the signer, so it is not a trusted timestamp. When an install is linked to a GitHub Release asset, PCL Lite independently verifies the current asset's SHA-256 against GitHub's release record and shows its publication time. That check corroborates the current artifact and release record; it does not authenticate the plugin author or prove the signature's exact signing time.

Unsigned packages require explicit confirmation. Invalid signatures, changed files, invalid manifests, and unsafe archive paths are rejected. A plugin can access the same user resources available to the launcher, so install only plugins you trust.

### Compiled Mixin patches

The SDK also defines C# Mixin attributes for compiled assemblies that patch launcher types, methods, and IL directly. This is executable code in the launcher's process, not a script or sandbox. The available operation set includes injection, overwrite, redirect, argument/constant/variable modification, accessors/invokers, shadows, unique members, slices, priorities, and match-count constraints. Do not assume complete semantic parity with PCL2-Nex; verify the current runtime behavior and tests for each operation and target launcher version.

Manifest `experimentalFeatures` group Mixin configuration files into separately selectable features. Each group defaults off and only applies after the user enables it and restarts the launcher. If a group fails to load, the host rolls back that group's already-applied configurations, records the error, and persists that group as disabled while allowing the plugin entrypoint and other groups to continue. This rollback is not process isolation and cannot undo arbitrary code that has already run.
