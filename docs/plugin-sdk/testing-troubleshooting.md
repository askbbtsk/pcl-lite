# 测试与排错

## 必做验证

插件不应只以“能编译”作为可用证据：

1. 构建 Release 插件和 `PCL.Plugin.Tools`，检查最终输出目录与清单路径匹配。
2. 生成签名包并检查；篡改任一 payload 后验签必须失败。
3. 将真实包安装到隔离数据目录，验证启动、禁用、重启、更新、移除和卸载回滚。
4. 每个 API 至少测试正常结果、错误、取消和资源清理；异步 UI 操作验证线程切换。
5. 每个 Mixin 单独覆盖匹配、签名、运行效果、冲突、必需配置失败回滚和启动器继续运行。`PluginMixinPackageRuntimeTests` 已覆盖签名包安装、Mixin 加载应用、后续必需配置失败回滚以及释放时恢复目标方法；这项集成测试不替代各 Mixin 操作的独立用例。
6. Minecraft 启动流程插件必须执行 Bedrock / CIK 不变回归测试。

## 仓库验证命令

```powershell
dotnet test .\PCL.Core.Test\PCL.Core.Test.csproj
dotnet build .\Plain Craft Launcher 2\Plain Craft Launcher 2.csproj
dotnet build .\PCL.Plugin.Tools\PCL.Plugin.Tools.csproj
dotnet build .\PCL.Plugin.Sample\PCL.Plugin.Sample.csproj
```

如果因当前分支依赖或环境导致命令无法执行，应记录完整错误和未覆盖范围，不要把上次测试结果当成本次证据。

## 常见故障

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

宿主 API 专项测试 `PluginApiRuntimeTests.LoadedPluginCanUseEveryHostApiAndDisposesEventSubscriptions` 当前通过 1/1，覆盖插件生命周期、独立存储/设置、日志、UI 桥接、事件、HTTP 16 MiB 响应上限、Minecraft 桥接、HTTPS 下载 64 MiB 上限、取消、Content-Length 检查和临时文件原子清理。Mixin 集成测试 `PluginMixinPackageRuntimeTests.SignedPackagesApplyMixinRollbackFailedPluginAndRestoreHostMethodOnShutdown` 验证签名包安装和加载、Mixin 应用、必需配置失败回滚，以及管理器释放时恢复目标方法。测试未覆盖存储 256 MiB 总量、64 MiB 单文件及 10,000 文件的极限压力；这些配额也不是对任意同进程文件访问的强制隔离。完整真实 UI 和游戏进程仍需发行构建回归；每种 Mixin 操作还需要各自的目标、匹配和运行效果用例。

## 贡献和本地留痕

每个功能改动应有对应的测试和文档。提交前检查 `git diff`，只提交本功能文件，避免把工作区其他改动一起纳入。不要提交 PFX、插件运行数据、打包产物或私钥。保持 Mixin 引擎有明确来源归属，并保留适用许可证文本。
