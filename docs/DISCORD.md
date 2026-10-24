# Discord 状态显示（Rich Presence）

在 Discord 个人资料里显示「你在启动器里干什么 / 在玩哪个版本 / 玩了多久」。
默认关闭，开关在 **设置 → 启动器 → Discord 状态显示**。

## 一、显示什么

分两种状态，由游戏进程是否存活自动切换：

| | 未开游戏 | 游戏运行中 |
| --- | --- | --- |
| 标题行 | `PCL Lite 1.2.1-beta` | `正在游玩 1.20.1-Forge` |
| 副标题行 | `已运行 37 分钟 · 启动器 384 MB` | `已游玩 12 分钟 · 游戏 1.8 GB · mc.example.com` |
| 计时器 | 启动器启动到现在 | 游戏启动到现在（Discord 自己跳秒） |
| 按钮 | `获取 PCL Lite` → 仓库地址 | 同左 |

副标题里的内容是**多选**的，在设置页勾选：

| 配置值 | 显示项 | 默认 | 说明 |
| --- | --- | --- | --- |
| `launcherVersion` | 启动器版本 | ✅ | 未开游戏时进标题行 |
| `launcherMemory` | 启动器内存 | ✅ | 未开游戏时显示 |
| `gameVersion` | 游戏版本/实例名 | ❌ | 开游戏后进标题行，如 `1.20.1-Forge` |
| `gameMemory` | 游戏内存 | ❌ | 开游戏后显示 |
| `playTime` | 时长 | ✅ | 开游戏是游玩时长，否则是启动器运行时长 |
| `serverAddress` | 服务器地址 | ❌ | **默认不显示**，见下 |

### 服务器地址为什么默认关

地址是从游戏日志里读的，打开后任何能看到你 Discord 资料的人（好友、同服的人）
都能直接看到你在哪个服务器。所以它单独一项、默认不勾，只在你主动勾选时才出现。

读取方式：尾读 `logs/latest.log` 最后 256 KB，正则匹配
`Connecting to <host>, <port>`，取最后一次；端口不是 25565 时带上 `:port`。
用「文件路径 + 长度」做缓存键，日志没长就不重复读。

## 二、隐私边界

- **默认关闭**，不打开就完全不连 Discord
- 状态通过本机命名管道 `\\.\pipe\discord-ipc-0` 直接交给本机 Discord 客户端，
  **不经过任何第三方服务器**，启动器本身不发起任何网络请求
- 关掉开关会立刻断开并清除状态
- 设置页有「在 Discord 中清除」按钮，可以临时撤下状态（改任何设置会自动恢复）

## 三、配置键

都在本地配置 `PCL/config.v1.yml`（见 [CONFIG.md](CONFIG.md)）：

| 键 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- |
| `DiscordRpcEnabled` | bool | `false` | 总开关 |
| `DiscordRpcAppId` | string | 内置 ID | Discord 应用 ID，一般不用改 |
| `DiscordRpcFields` | string | `launcherVersion,launcherMemory,playTime` | 逗号分隔的显示项 |

## 四、换成你自己的 Discord 应用

内置的应用 ID 属于本项目，状态卡片上显示的应用名和图标是 PCL Lite 的。
想显示成你自己的应用：

1. 打开 [Discord 开发者门户](https://discord.com/developers/applications)，`New Application`
2. 名字随意（会显示在状态卡片上）
3. **General Information → APP ICON** 上传图标，1024×1024 PNG。
   本仓库自带一张：`docs/assets/logo-1024.png`（另有 512 / 256 两版）
4. 复制 **Application ID**，填进设置页的「应用 ID」
5. 回设置页点「立即刷新」

> 不需要创建机器人、不需要 Token、不需要填 OAuth2 回调。
> Rich Presence 只用 Application ID 这一项。

## 五、实现结构

| 文件 | 职责 |
| --- | --- |
| `PCL.Core/Link/Discord/DiscordIpcClient.cs` | 命名管道传输：扫描 `discord-ipc-0..9`、握手、帧读写、读循环、断开通知 |
| `PCL.Core/Link/Discord/DiscordActivityBuilder.cs` | 纯函数：快照 → activity JSON（可脱离启动器测试） |
| `PCL.Core/Link/Discord/DiscordRichPresenceService.cs` | 生命周期服务：5 秒轮询、连接退避、节流上报、状态文本 |
| `PCL.Core/Link/Discord/DiscordHostBridge.cs` | 前端注入「游戏快照」的桥 |
| `PCL.Core/Link/Discord/DiscordRpcFields.cs` | 显示项常量与默认值 |
| `Plain Craft Launcher 2/Modules/ModDiscord.cs` | 读游戏进程/实例/内存，解析日志拿服务器地址 |

设计上的几个决定：

- **不引第三方 RPC 库**。协议就是「4 字节 op + 4 字节长度 + JSON」，自己实现约 200 行，
  比维护一个 SDK 依赖省心，也不用担心供应链
- **内容没变就不发**。Discord 对 `SET_ACTIVITY` 限流是 20 秒 5 次；时长交给
  `timestamps.start` 让 Discord 自己跳秒，所以稳定状态下每分钟才发一次
- **连接失败退避 30 秒**。Discord 没开的时候不要每 5 秒去扫十个管道
- **握手成功不代表连接可用**。Discord 对无效的应用 ID 会先回 `READY` 再立刻断开，
  所以发送前要再确认一次 `IsConnected`，否则用户只会看到「未连接」这种没有信息量的报错

## 六、常见问题

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| 状态里啥都没有 | Discord 桌面端没开（网页版不算） | 打开 Discord 桌面端，等 30 秒自动重连 |
| 状态显示「未填写应用 ID」 | 应用 ID 被清空了 | 恢复成内置 ID，或填自己的 |
| 「连接断开：Discord 关闭了连接」 | 应用 ID 无效 | 核对 Application ID，别把公钥/Token 填进去 |
| 改了设置没反应 | 内容没变化时不会重发 | 点「立即刷新」 |
| 状态一直是「启动器中」但游戏在跑 | 游戏不是通过本启动器启动的 | 只认本启动器拉起的进程 |

## 七、怎么测（不用真的开游戏）

`DiscordActivityBuilder` 和 `DiscordIpcClient` 都不依赖 WPF 与配置系统，可以单独编译测试：

- **渲染**：直接调用 `DiscordActivityBuilder.Build(...)`，注入固定的时间和快照，断言 JSON 内容
- **传输**：用 `NamedPipeServerStream` 起一个假的 `discord-ipc-1`，让客户端
  `Connect(appId, out err, pipeIndex: 1)` 连过去，断言收到握手帧和 `SET_ACTIVITY` 帧

注意本机装了 Discord 时 `discord-ipc-0` 会被占用，假服务端要用别的槽位。
