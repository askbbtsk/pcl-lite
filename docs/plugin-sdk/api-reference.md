# 宿主 API 参考（v1）

所有接口定义在 `PCL.Plugin.SDK`。宿主通过 `IPluginContext` 提供服务。当前 API 不是权限沙箱；插件可使用 .NET 自身能力访问当前用户允许的资源。

## 生命周期和插件信息

- `IPclPlugin.StartAsync(context, cancellationToken)`：启动插件并注册页面/事件。
- `IPclPlugin.StopAsync(cancellationToken)`：释放插件创建的资源。
- `context.Plugin`：ID、名称、版本、作者、描述、API 版本。
- `context.DataDirectory`：插件独立数据根目录。

启动器会逐插件隔离加载错误，但插件运行在同一进程；未捕获的进程级故障仍可能影响启动器。Mixin 在主窗体创建前应用，`StartAsync` 在 `WindowCreated` 后执行。安装、启用状态变更和移除需重启生效。使用 `PCL_LITE_SAFE_MODE=1` 或 `--safe-mode` 可跳过插件加载以恢复启动器。

## 日志、存储和设置

- `Logger.Trace/Debug/Info/Warn/Error`：日志按插件 ID 分类。
- `Storage.ReadTextAsync/WriteTextAsync`：读写插件数据目录下的安全相对路径。
- `Storage.ReadJsonAsync<T>/WriteJsonAsync<T>`：JSON 文件存取。
- `Settings.GetAsync<T>/SetAsync<T>/RemoveAsync`：以受限 ASCII key 存取设置，底层为插件独立 JSON 文件。

存储写入受每插件 256 MiB 总量、单文件 64 MiB 和 10,000 文件限制；写入先进入临时文件，成功后替换目标。路径不能穿越插件数据目录，符号链接/联接点会被拒绝。

这些限制约束 `Storage` / `Settings` 的宿主写入 API，不是操作系统配额；同权限的编译插件直接用 .NET 文件 API 仍可绕过。`Storage` 读写的是完整文本/JSON，不提供流式或二进制存储。`Launcher.DownloadAsync` 与 Storage 共用插件数据树配额锁：单次传输最多 64 MiB，累计不超过 256 MiB，新增文件不超过 10,000 个，并在响应声明 Content-Length 时验证实际长度。

不要把密钥写入日志；对可变数据使用版本字段，并处理首次读取返回 `null` 的情况。

## UI

- `Ui.RegisterPage(id, title, Func<FrameworkElement>)`：注册插件页。工厂在页面实际显示时调用，适合延迟创建大型视图。
- `Ui.ShowNotification(message, kind)`：发送提示。
- `Ui.ConfirmAsync(title, message, cancellationToken)`：显示确认提示并返回用户选择。

页面 ID 在同一插件内应稳定且唯一。后台事件处理器不能直接操作 WPF 控件；使用 `Application.Current.Dispatcher`。

## 事件

```csharp
IDisposable subscription = context.Events.Subscribe<PluginGameStartedEvent>(
    async (game, ct) =>
    {
        context.Logger.Info($"Started {game.InstanceName} (PID {game.ProcessId})");
        await Task.CompletedTask;
    });
```

宿主事件：`PluginGameLaunchingEvent`、`PluginGameStartedEvent`、`PluginGameExitedEvent`。`PublishAsync<T>` 可在同一启动器进程中的同类型订阅者间发布事件。保存并释放订阅句柄；插件停止时宿主会清理 context 级订阅。单个处理器异常会记录并继续分发。

## 网络和下载

- `Network.GetAsync`、`Network.SendAsync`：HTTP 请求，返回状态码、原始 `ContentBytes`、`ContentType`、响应头及正文 `Content`。文本正文按响应的 charset 解码；未声明或不支持的 charset 回退 UTF-8。二进制响应使用 `ContentBytes`，不要使用可能含替换字符的 `Content`。
- `Launcher.DownloadAsync(uri, relativePath, progress, cancellationToken)`：只允许 HTTPS，单次下载不超过 64 MiB，目标限定在插件数据目录，和 Storage 共用配额锁，写入随机临时文件，完整成功且 Content-Length 匹配后原子替换，可报告字节进度。

HTTP API 可向插件配置的地址发送请求，不代表该地址可信。避免把令牌放入 URL 或日志；校验远端数据和下载内容。大文件应使用流式处理，不要额外复制整个内容到内存。

`Network` 返回完整字节正文而不是流，响应会在内存中缓冲；`Content` 是方便处理文本的解码视图。请求和响应正文各不超过 16 MiB，插件应请求更小的内容。

## Minecraft 和启动器

- `Minecraft.GetInstancesAsync`：返回 ID、名称、版本、目录和是否选中。
- `Minecraft.LaunchAsync(instanceId)`：请求宿主启动对应实例；不存在的 ID 返回 `false`。
- `Launcher.Name/Version/DataDirectory/PluginsDirectory`：宿主信息和目录路径。
- `Launcher.OpenUrl`：支持 HTTP(S)。
- `Launcher.OpenPath`：打开当前用户有权访问的已有文件或目录；插件可传任意路径，宿主不将它限制在插件目录。

启动实例是用户可见行为；避免未经用户操作自动启动或反复触发。

## Mixin 不是宿主 API

Mixin 是编译程序集中的方法补丁，见 [Mixin 指南](mixins.md)。与 `IPluginContext` 不同，它会改写启动器进程中的方法执行，兼容性和故障影响范围更大。

## 覆盖状态

当前宿主 API 的专项探针覆盖正常、失败、取消、资源释放和桥接行为；完整 UI、真实下载服务和真实 Minecraft 启动仍需发行构建回归。`PluginMixinPackageRuntimeTests` 覆盖签名包经安装和加载后应用 Mixin、必需配置失败回滚及释放时恢复目标方法；该单项集成测试不代表所有 Mixin 操作均已覆盖。提交新 API 或 Mixin 操作时，增加相应运行时测试；参见[测试与排错](testing-troubleshooting.md)。

## 接口实现与验证索引

新增或修改接口前，先沿下表找到生产适配器和探针。多数 API 行为通过同一个已签名测试插件端到端调用；WPF 页面导航和真实游戏启动仍需按[测试与排错](testing-troubleshooting.md)做发行版烟测。

| SDK 接口 | PCL Lite 生产实现 | 自动化验证 |
|---|---|---|
| `IPclPlugin` | `PCL.Core/Plugins/PluginRuntimeManager.cs`：`_PreparePluginAsync`、`_StartPluginAsync`、`DisposeAsync` 停止路径 | `PluginApiRuntimeTests.LoadedPluginCanUseEveryHostApiAndDisposesEventSubscriptions`；依赖顺序见 `PluginPackageTests.RuntimeStartsCompiledPluginsInDependencyOrder` |
| `IPluginContext`、`IPluginMetadata` | `PluginRuntimeManager.cs`：`PluginContext`、`PluginMetadata` | `PluginApiRuntimeTests.LoadedPluginCanUseEveryHostApiAndDisposesEventSubscriptions` |
| `IPluginLogger` | `PluginRuntimeManager.cs`：`PluginLogger` | 同上（验证五个日志等级） |
| `IPluginStorage` | `PluginRuntimeManager.cs`：`PluginStorage` | 同上（文本/JSON、路径限制、取消、并发写入和失败清理） |
| `IPluginSettings` | `PluginRuntimeManager.cs`：`PluginSettings` | 同上（读写、删除和取消） |
| `IPluginUi` | `PluginRuntimeManager.cs`：`PluginUi`、`PluginPageRegistry`；生产 WPF 桥接在 `Plain Craft Launcher 2/Program.cs` 的 `_ConfigurePluginBridge`，插件视图在 `Plain Craft Launcher 2/Pages/PagePlugins/PagePluginsInstalled.cs` 创建 | 同上（UI 桥接探针）；实际视图导航仍需启动器烟测 |
| `IPluginEvents` | `PluginRuntimeManager.cs`：`PluginEvents`、`PublishAsync` | 同上（订阅、分发、异常隔离、插件停止后清理） |
| `IPluginNetwork` | `PluginRuntimeManager.cs`：`PluginNetwork` | 同上（HTTP、二进制、字符集、错误状态、响应大小上限） |
| `IPluginMinecraft` | `PluginRuntimeManager.cs`：`PluginMinecraft`；生产实例和启动桥接在 `Plain Craft Launcher 2/Program.cs` 的 `_ConfigurePluginBridge` | 同上（使用替身桥接）；真实实例与进程仍需烟测 |
| `IPluginLauncher` | `PluginRuntimeManager.cs`：`PluginLauncher` | 同上（信息、打开路径及 HTTPS 下载限制/原子写入） |

运行插件探针：`dotnet test .\PCL.Core.Test\PCL.Core.Test.csproj --filter FullyQualifiedName~PluginApiRuntimeTests`。测试通过只证明测试桥接和当前适配器契约；若改动生产 WPF 桥接，还要构建启动器并验证页面线程、实例 ID 映射和启动结果。
