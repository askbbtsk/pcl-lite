# PCL Lite 文档

给后来的人看的。想改代码、想换配色、想知道配置存在哪，都从这里进。

## 目录

| 文档 | 讲什么 | 什么时候看 |
| --- | --- | --- |
| [THEME.md](THEME.md) | 配色/主题系统：八级色阶怎么算、14 个预设、自定义主题参数、怎么加一个新主题 | 想改界面配色、想加主题 |
| [CONFIG.md](CONFIG.md) | 配置文件在哪、YAML/JSON 长什么样、怎么手动改、改了怎么生效 | 想改配置、想查数据目录 |
| [BUILD.md](BUILD.md) | 怎么编译、密钥怎么注入、怎么签名、怎么发 Release | 想自己构建一个能用的版本 |
| [AZURE-LOGIN.md](AZURE-LOGIN.md) | 从零注册 Azure 应用，拿到自己的 `client_id` 走微软正版登录 | 想让正版登录用自己的应用 |
| [MCP.md](MCP.md) | 内置 MCP 服务，让 AI 客户端直接操作启动器 | 想接 Claude / Cursor / Cherry Studio |

## 项目速览

```
PCL-CE/
├── PCL.Core/                     核心库（无 UI 依赖的部分：配置、网络、认证、主题算法）
│   ├── App/                      配置模型、路径、元数据
│   ├── IO/Net/                   网络层（DNS、代理、用途白名单）
│   ├── Link/                     联机、第三方账号
│   ├── Minecraft/                启动、下载、档案、认证
│   └── UI/Theme/                 ★ 配色引擎（见 THEME.md）
├── Plain Craft Launcher 2/       WPF 前端（页面、控件、语言文件、metadata.json）
├── PCL.Core.SourceGenerators/    源生成器：配置项、密钥注入
└── docs/                         就是这里
```

技术栈：.NET 10 + WPF。前端是 WPF 页面 + 自定义控件，核心库被前端通过 IoC 生命周期服务调用。

## 版本号在哪

三个地方，改版本必须一起改，否则界面显示和文件属性会对不上：

1. `Plain Craft Launcher 2/metadata.json` → `version.{base,suffix,code}`
   **界面显示的是这个**，不是 csproj。`suffix` 为空表示正式版，非空会拼成 `1.1.0-beta` 这样。
2. `Plain Craft Launcher 2/Plain Craft Launcher 2.csproj` → `AssemblyVersion` / `FileVersion` / `InformationalVersion`
3. `PCL.Core/PCL.Core.csproj` → `AssemblyVersion` / `FileVersion`

> 踩过的坑：1.0.0 那次只改了 csproj，`metadata.json` 还是 `0.1.0` + `beta`，
> 结果文件属性写着 1.0.0、界面显示 `0.1.0-beta`。

## 几个容易找错的地方

- **主题算法**在 `PCL.Core/UI/Theme/`，不在前端目录。前端只负责把资源应用上去。
- **密钥**（OAuth client_id 等）不在源码里，编译期由 `PCL.Core.SourceGenerators` 从环境变量注入。
  所以仓库里搜不到任何 client_id，这是设计如此，不是漏了。
- **语言文件**在 `PCL.Core/App/Localization/Languages/*.xaml`，四种语言：zh-CN / zh-TW / en-US / en-GB。
  加文案要四个一起加，缺了会回退到 zh-CN。
- **上游代码**在 `Plain Craft Launcher 2/` 与 `PCL.Core/` 里混合，改之前先用 `git log` 看这行是谁写的，
  避免把上游的修复覆盖掉。
