**简体中文** | [English](README-EN.md) | [繁體中文](README-ZH_TW.md)

<div align="center">

# PCL Lite

前言：pcl和它的护卫队天天说动了蛋糕，顺手扣帽子，我想知道pcl这个帽子工厂和烘焙店是有仇吗 怎么帽子工厂天天动蛋糕？
https://github.com/askbbtsk/pcl-lite/readmedlc

[![Stars](https://img.shields.io/github/stars/askbbtsk/pcl-lite?style=for-the-badge&logo=github&labelColor=444444&color=eac54f)](https://github.com/askbbtsk/pcl-lite/stargazers)
[![Release](https://img.shields.io/github/v/release/askbbtsk/pcl-lite?include_prereleases&label=release&logo=github&style=for-the-badge)](https://github.com/askbbtsk/pcl-lite/releases)
[![Downloads](https://img.shields.io/github/downloads/askbbtsk/pcl-lite/total?style=for-the-badge&logo=github&labelColor=444444&color=1F883D)](https://github.com/askbbtsk/pcl-lite/releases)

[下载](https://github.com/askbbtsk/pcl-lite/releases) |
[文档](docs/) |
[许可证](#-许可证) |
[提交问题](https://github.com/askbbtsk/pcl-lite/issues/new)

</div>

> **非 MINECRAFT 官方产品。** 未经 MOJANG 或 MICROSOFT 批准，也不与 MOJANG 或 MICROSOFT 关联。
> NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.

PCL Lite 是基于 PCL 开源代码开发的衍生版本，在上游功能的基础上加入了一些额外的改动与修复。

本项目以 **MIT 许可证** 发布，并附带传染性（copyleft）附加条款，详见[许可证](#-许可证)。

**中国大陆用户请勿使用。**

## 📦 下载

前往 [Releases](https://github.com/askbbtsk/pcl-lite/releases) 下载最新版本。

| 架构 | 文件 | 说明 |
|---|---|---|
| x64（稳定版） | `PCL-Lite_1.1_x64.exe` | 单文件、免解压 |
| x64（预发布） | `PCL-Lite_1.2.2-beta_x64.exe` | 功能较新，可能不稳定 |

- 需要已安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)
- 启动器内置自动更新，更新源指向本仓库的 Releases
- 预发布版本（如 `0.1.0-beta`）会被更新通道视为 beta 通道

## 💻 支持平台

| 操作系统 | 支持情况 | 环境要求 |
|---|---|---|
| Windows 10 1809 (17763) 或更高 | ✅ 完整支持 | [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Windows 8 - Windows 10 1809 (17763) | ⚠️ 理论能跑，酌情提供支持 | [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Windows 7 或更低版本 | ❌ 不支持 | / |
| macOS / Linux / 其他操作系统 | ⚠️ 仅跨平台开发支持（交叉编译） | [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) |

**✅ 完整支持**：尽可能提供一切相关支持，但必须确保启动器为最新版本。

**⚠️ 理论能跑，酌情提供支持**：PCL Lite 应该可以在这些平台上运行，但不保证功能完全可用。你可能需要升级到完整支持的系统版本以获得进一步支持。

**❌ 不支持**：PCL Lite 在这些平台的可用性较低，甚至压根打不开。请升级操作系统以使用 PCL Lite。

**⚠️ 仅跨平台开发支持（交叉编译）**：PCL Lite 的源代码可以在 macOS 与 Linux 平台编译，但无法直接运行。作为开发者，你可以在这些平台上进行开发，然后将编译产物转移到 Windows 系统测试。

**注**：
仅对最新版本的启动器提供支持。
取决于部分问题的特殊性（如系统不完整），有时你仍然必须升级操作系统以继续获得支持。
PCL Lite 始终建议使用最新版本的操作系统以获得最佳体验。
你仍然可以尝试在不受支持的系统上使用 PCL Lite，但可能会遇到很多额外问题。

## 📖 文档

| 文档 | 讲什么 |
| --- | --- |
| [docs/README.md](docs/README.md) | 索引、项目结构、版本号在哪 |
| [主题与配色](docs/THEME.md) | 八级色阶怎么算、14 个预设、自定义主题、怎么加新主题 |
| [配置与数据目录](docs/CONFIG.md) | 配置文件位置、手动改法、迁移逻辑 |
| [Discord 状态显示](docs/DISCORD.md) | 显示内容、隐私边界、换自己的应用 |
| [构建与发布](docs/BUILD.md) | 编译、密钥注入、签名、发版 |
| [Azure 登录](docs/AZURE-LOGIN.md) | 从零注册应用，用自己的 client_id 走正版登录 |
| [MCP](docs/MCP.md) | 让 AI 客户端直接操作启动器 |
| [开源项目清单](docs/CREDITS.md) | 代码来源、第三方库、各自的许可证 |

## 🔒 许可证

本项目以 **[MIT 许可证](LICENSE)** 发布，并附带下列**传染性（copyleft）附加条款**。完整条款以 [LICENSE](LICENSE) 文件为准。

- **传染性**：任何修改版、衍生版或再分发版，必须同样以本协议（MIT + 附加条款）分发，不得改用其他许可证
- **必须署名**：必须完整保留本项目的版权声明与作者署名，不得删除或替换
- **二次修改须标注 PCL Lite**：修改后分发时，必须在名称或说明中明确标注本项目为 **PCL Lite**
- **与 Mojang / Microsoft 无关**：本项目与 Mojang Studios、Microsoft 及其关联公司没有任何关系，未获其批准、授权或背书
- **地域限制**：中国大陆用户请勿使用
- **协议可变**：本协议可能随时更新，且不提前通知；更新只对之后获取的版本生效，**已经 fork 或已经分发的版本不受影响**



## ❤️ 致谢
<img width="403" height="310" alt="ab4950f140f8c215f12435cd3dc3b667" src="https://github.com/user-attachments/assets/8fc63b73-d03c-4db3-8251-f93f7db93807" />
<img width="832" height="573" alt="image" src="https://github.com/user-attachments/assets/4fe49f35-4de5-486f-9bdd-325fa22023a7" />

参考文献:https://zhuanlan.zhihu.com/p/347828430

感谢这个人
后记:龙猫你个傻逼能不能不要用主题圈钱啊
