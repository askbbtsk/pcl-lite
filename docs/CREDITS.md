# 开源项目清单

PCL Lite 站在很多人的肩膀上。这份清单列出直接借用了代码或资源的项目，以及运行期依赖的第三方库。

> **本清单不改变本项目的许可证。** PCL Lite 仍以 [MIT + 传染性附加条款](../LICENSE) 发布，
> 详见 [README 的许可证一节](../README.md#-许可证)。这里列出的上游项目各自保留自己的许可证。

## 一、代码来源

### PCL（Plain Craft Launcher）


### BedrockBoot / BedrockLauncher.Core

- 作者：Round Studio 及相应贡献者。
- 仓库：<https://github.com/Round-Studio/BedrockLauncher.Core>、
  <https://github.com/Round-Studio/BedrockBoot>。
- 感谢上述项目为基岩版下载、安装、容器解析及解密功能提供的参考与代码来源。
- BedrockLauncher.Core 历史 MIT 版本：
  [`f262eb7af2e6e11665ef51d9b998c42819ee0bb3`](https://github.com/Round-Studio/BedrockLauncher.Core/tree/f262eb7af2e6e11665ef51d9b998c42819ee0bb3)。
  `Copyright (c) 2025 Round Studio`；该历史版本的许可全文保留在
  [`LICENSE-BEDROCKLAUNCHER-MIT`](../LICENSE-BEDROCKLAUNCHER-MIT)。

## 二、第三方库（NuGet）

运行期依赖，按包名排序。全部为宽松许可证（MIT / Apache-2.0 / BSD），与项目的 MIT 兼容。

| 包 | 用途 | 许可证 |
| --- | --- | --- |
| `Ae.Dns.Client` | DNS 解析（含 DoH） | MIT |
| `CommunityToolkit.Mvvm` | MVVM 基础设施 | MIT |
| `Dapper` | 轻量 ORM | Apache-2.0 |
| `DotNet.Glob` | 通配符匹配 | MIT |
| `Downloader` | 分块多线程下载 | MIT |
| `fNbt` | NBT 读写（存档、Level.dat） | MIT |
| `FluentValidation` | 参数校验 | Apache-2.0 |
| `Humanizer.Core.zh-CN` | 中文数字/时间人性化 | MIT |
| `LiteDB` | 嵌入式文档数据库 | MIT |
| `Markdig.Wpf` | Markdown 渲染（更新说明） | BSD-2-Clause |
| `Microsoft.Data.Sqlite` | SQLite 访问 | MIT |
| `Microsoft.Extensions.Http` | `HttpClientFactory` | MIT |
| `Microsoft.Extensions.Logging` | 日志抽象 | MIT |
| `Microsoft.Xaml.Behaviors.Wpf` | WPF 行为 | MIT |
| `NAudio` | 音频播放 | MIT |
| `PlainToolkit.CngProtectedData` | CNG 加密存储（口令） | MIT |
| `Polly` | 重试与熔断 | BSD-3-Clause |
| `protobuf-net` | Protobuf 序列化 | Apache-2.0 |
| `SharpZipLib` | zip 读写 | MIT |
| `SQLitePCLRaw.bundle_e_sqlite3` | SQLite 原生绑定 | Apache-2.0 |
| `System.IdentityModel.Tokens.Jwt` | JWT 解析（正版登录） | MIT |
| `System.Management` | WMI 查询 | MIT |
| `System.Security.Cryptography.ProtectedData` | DPAPI | MIT |
| `System.Text.Json` | JSON | MIT |
| `Wacton.Unicolour` | OKLCH / 色彩空间转换（主题引擎） | MIT |
| `YamlDotNet` | YAML 配置读写 | MIT |

## 三、资源与图标

| 资源 | 来源 | 许可证 |
| --- | --- | --- |
| SVG 图标集 | [Lucide](https://lucide.dev) | ISC |
| PCL Lite 图标 | 本项目自制 | 随本项目 |

## 四、怎么补充这份清单

新增第三方代码或资源时：

1. 在上面对应小节加一行，写清**作者、许可证、仓库地址**；
2. 如果是移植代码，在源文件顶部注释里注明出处（参考 `BedrockService.cs` 的写法）；
3. **不要因为引入外部代码而修改本项目的 LICENSE**。上游许可证的约束写在本文档里，
   而不是替换掉 `LICENSE`。
