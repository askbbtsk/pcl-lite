# PCL Lite 插件开发文档（AI 入口）

本目录面向插件作者和 AI 编码助手。先读本页，再按任务只读一个专题，减少无关上下文。

## 先确认插件类型

PCL Lite 插件是 **编译后的 .NET 程序集**，不是脚本。普通插件实现 `IPclPlugin`，通过 SDK 宿主 API 注册页面、订阅事件、存取数据等。需要改启动器已有方法时，使用编译期 C# Mixin：它在启动器主窗体创建前应用 IL 补丁，不能用普通 API 插件页面替代。

| 任务 | 只需阅读 |
|---|---|
| 新建、构建、打包最小插件 | [快速开始](quickstart.md) |
| 调用启动器提供的全部接口 | [宿主 API 参考](api-reference.md) |
| 改启动器内部方法、字段或 IL 调用 | [Mixin 指南](mixins.md) |
| 声明插件间依赖和版本要求 | [依赖与加载顺序](dependencies.md) |
| 声明适用的 PCL Core 版本范围 | [版本兼容性](compatibility.md) |
| 清单、证书、签名、安装和信任 | [包格式与安全](packaging-security.md) |
| 测试、故障定位和提交前检查 | [测试与排错](testing-troubleshooting.md) |

## 给 AI 编码助手的约束

1. 先读本页，再只读当前任务对应的专题；不要一次性加载所有文档。
2. 写可编译的 C# 插件，不要把插件改成脚本或假设有未列出的 API。以当前仓库中的 `PCL.Plugin.SDK` 接口为准。
3. 优先调用稳定 SDK；只有确实需要修改启动器已有实现时才写 Mixin。Mixin 目标及签名须从当前源码核对，不能猜方法名、参数、重载或 IL 匹配点。
4. 不要修改 Bedrock / CIK 现有逻辑。涉及启动页、Minecraft 版本、启动流程的改动要确认不触碰 CIK 路径，并执行相关回归检查。
5. 所有宿主内插件都运行在启动器进程并拥有启动器的当前用户权限。签名只验证包内容完整性；不构成沙箱、作者身份认证或安全隔离。
6. 优先复用 SDK、WPF 和启动器已有依赖。避免把大型通用框架、重复运行时、无用资源打进包；延迟创建页面和昂贵对象，异步工作响应取消。
7. 完成代码后按 [测试与排错](testing-troubleshooting.md) 验证。不要声称接口可用，除非相应路径有自动化或运行证据。

## API 状态说明

SDK 当前版本号为 API v1，含 11 个公开接口、46 个接口成员。契约覆盖生命周期、日志、独立存储与设置、WPF 页面/通知/确认、类型化事件、HTTP、下载、启动器信息及 Minecraft 实例查询/启动；没有未实现的接口存根。`PluginApiRuntimeTests` 已覆盖这些宿主接口的正常、取消、错误、网络响应上限、下载原子替换、日志等级、停止生命周期、符号链接拒绝和事件清理路径；完整启动器 UI 与真实 Minecraft 进程仍需发行构建回归。`PluginMixinPackageRuntimeTests` 覆盖签名包安装、Mixin 应用、安全模式跳过、必需配置失败时回滚、拒绝安装目录 junction、用签名插件覆盖 `PCL.Core` 中的真实宿主方法以及释放时恢复。

Mixin 使用编译后的 .NET 程序集修改启动器自身的目标类型、方法和 IL，不是脚本层。当前公开操作包括注入、覆盖、重定向、参数/参数组/常量/局部变量修改、Accessor/Invoker、Shadow/Unique、Slice、优先级和匹配约束。PCL2-Nex 也提供编译型 Mixin packages；本项目不承诺两者每个操作的语义、边界或兼容行为完全等价。请按[Mixin 指南](mixins.md)、当前运行时代码及测试确认具体能力。签名包集成测试已验证对 `PCL.Core` 真实方法的覆盖和恢复，但尚未覆盖每一种操作。

清单可把相关配置声明为独立的 `experimentalFeatures` 组。每组默认关闭，用户明确启用并重启后才应用；启停选择会保存，变更需重启生效。某组任一配置读取或应用失败时，运行时回滚该组已应用的配置、记录错误并自动将该组关闭，其他组和插件入口可继续运行。详见[Mixin 指南](mixins.md)。

## 关键约定

- 入口：`plugin.json` 的 `entryAssembly` + `entryType`。
- 普通运行时：启用包先在主窗体创建前校验并应用 Mixin；主窗体进入 `WindowCreated` 后才调用 `StartAsync`，退出时调用 `StopAsync`。变更启用状态、更新或卸载需要重启生效。
- Mixin：编译程序集 + 清单中的 `mixinConfig` 或 `mixinConfigs`；在窗体构建前应用。配置文件和程序集都由包签名覆盖。
- 恢复模式：设置环境变量 `PCL_LITE_SAFE_MODE=1` 或使用 `--safe-mode` 启动会跳过已启用插件的加载和 Mixin 应用，方便移除故障插件；包仍会被检查并显示状态。它只影响本次启动，不会永久关闭插件。
- 发布包：`.pclplugin` ZIP 包。推荐使用本地 PFX 证书签名；不提交证书私钥或密码。
- 限额：压缩包最大 100 MiB，解压内容最大 500 MiB，最多 10,000 个包条目；Mixin 配置最多 64 个，每份配置最大 256 KiB、最多 512 个类型名。插件数据树（包括 Storage、Settings 和 Launcher.DownloadAsync）使用每插件 256 MiB、单文件 64 MiB、10,000 文件上限；HTTP 请求/响应正文各限 16 MiB；单次 HTTPS 下载限 64 MiB。市场索引和 GitHub Release API 响应各限 2 MiB，索引最多 1,000 条；市场、包和 Release API 重定向必须保持 HTTPS。下载和存储写入共用并发配额锁，先写临时文件并在完整且 Content-Length 匹配后原子替换。插件自身仍应控制依赖体积和内存占用。
