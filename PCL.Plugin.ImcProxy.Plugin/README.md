# IMCProxy · PCL Lite 插件

在 PCL Lite 里管理 **IMCProxy-NextGen-Free**（C++ 版）：从 GitHub Release 下载解压、后台静默运行代理、修改配置。

代理是原生 `imcproxy.exe`，不依赖 Node.js，启动时不显示控制台窗口；插件通过代理自带的 HTTP API（默认 `http://127.0.0.1:8790/api`）读写状态与配置。

## 目录

```
PCL.Plugin.ImcProxy.Plugin/
├── PCL.Plugin.ImcProxy.csproj
├── Plugin.cs
├── ControlPage.cs
├── ImcProxyApi.cs
├── ImcProxyRuntime.cs
├── plugin.json
├── README.md
└── MARKET.md
```

## 构建

```
dotnet build PCL.Plugin.ImcProxy.Plugin\PCL.Plugin.ImcProxy.csproj -c Release
```

产物是 `PCL.Plugin.ImcProxy.dll` 与 `plugin.json`。不要连同 `PCL.Plugin.SDK.dll` 一起分发，宿主已经提供该程序集。

## 安装

把产物放进插件目录：

```
<PCL Lite 数据目录>/Plugins/imcproxy.control/
```

启动 PCL Lite，在插件列表里启用「IMCProxy」，侧边栏会出现同名页面。

## 使用

页面包含四块：

- 操作按钮：下载 / 更新、启动代理、停止代理、刷新状态、打开安装目录
- 状态：运行状态、1.8.9 / 1.12.2 监听、上游地址、在线玩家、当前安装版本
- 版本与更新：本地版本 / 远程版本、检查更新、加载更新日志
- 配置：直接编辑 `config.json`，或按项修改；保存后重启生效

首次使用：

1. 点「下载 / 更新」，插件会读取 `HertaTeam/IMCProxy-NextGen-Free` 最新的 release，下载 zip 并解压到插件数据目录
2. 点「启动代理」，`imcproxy.exe` 以隐藏窗口方式运行
3. 用 Minecraft 1.8.9 连 `127.0.0.1:25565`（1.12.2 是 `25566`）
4. 需要改配置时，在页面里编辑或逐项应用

安装目录：

```
<PCL Lite 数据目录>/Plugins/.plugin-data/imcproxy.control/imcproxy-free/
```

升级时会保留 `config.json`、`game-account.txt`、`game-accounts.txt`、`bind-code.txt`、`.imc-session.json`、`.imc-captoken.json`。

## 下载源

- Release 页面：https://github.com/HertaTeam/IMCProxy-NextGen-Free/releases
- 插件优先用 GitHub API 读取最新 release；API 被限流时回退到 releases/latest 页面解析
- 只接受 release 里的 `.zip` 资源（当前为 `imcproxy-free-<版本>-win64.zip`）

## 控制方式

插件全部通过代理的 HTTP API 操作，不解析控制台输出：

| 功能 | 接口 |
| --- | --- |
| 状态 | `GET /api/status` |
| 配置列表 | `GET /api/config` |
| 单项配置 | `GET /api/config/<key>` |
| 修改配置 | `POST /api/config/<key>` |
| 启动 / 停止监听 | `POST /api/start`、`POST /api/stop` |
| 更新日志 | `GET /api/changelog` |
| 版本 | `GET /api/version` |

API 地址与 token 会从安装目录的 `config.json` 读取并同步。

## 说明

- 插件只管理免费版 IMCProxy-NextGen-Free；正式版（IMCProxy-NextGen）不在此插件管理范围
- 代理进程由插件启动和结束，PCL Lite 退出时插件会停止代理
- 配置写入失败（例如端口非法）会给出原因，`config.json` 不会被改坏
- 更新日志优先调用代理接口，代理未启动时回退到 release 说明
