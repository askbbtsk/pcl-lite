<summary>好友列表和私聊接上了——上一版说「做不了」是我搞错了，Lunar 那条通道能逆向出来；顺手修好三件事：「回到游戏」真的把窗口提到前台了、点停止不再 10 秒就杀进程（现在等它存完盘，最多两分钟）、启动后不再误报「疑似卡死」。</summary>

PCL Lite 1.6.3 快照版 3（snapshot-3）。

> **这是快照版，不是正式版。** 把更新通道选成「快照尝鲜」就能收到；
> 「最新」也会收到，「稳定版」「预览体验」不会。
>
> 已经是 `1.6.3-snapshot-2` 的话，这次是它的增量：好友/私聊 + 三个修复。

## ① 好友列表与私聊

上一版我写的是「好友列表做不了，PCL 没有 Lunar 的账号后端，这条路堵死」。
**这句话是错的。** 那条通道确实不走本地 IPC，但它也不需要 Lunar 的账号后端 ——
它要的是一个 Minecraft 令牌，而 PCL 本来就有。

Lunar 页现在多了一张「好友与私聊」卡片：

```
点「连接」→ 拿账号的 Minecraft 令牌 → 换 Lunar 的启动器 JWT → 连后端 → 拉好友列表
点好友   → 拉历史消息 → 输入框回车发送
```

在线好友会显示在哪个服务器（公共服名 / 单人 / 菜单 / 局域网），离线显示「离线」。

技术上是一条三段式握手，和 Mojang 的服务器加密握手一模一样，只是把「连服务器」
换成了「连 Lunar」：

```
① wss://authenticator.lunarclientprod.com/launcher-v3  → hello { identity, initiator }
② ← encryptionRequest { publicKey, randomBytes }
③ sharedSecret = random(16)；sha1(sharedSecret ‖ publicKey) 拿去 sessionserver join
   → encryptionResponse { RSA(sharedSecret), RSA(randomBytes) }
④ ← authSuccess { encryptedJwt } → AES-128-GCM 解出 JWT
⑤ wss://websocket.lunarclientprod.com/launcher-v3 → 裸 Handshake → RPC
```

好友走 `FriendService.Login`，私聊走 `ConversationService` 的
`GetConversations` / `CreateConversation` / `LoadConversation` / `SendConversationMessage`，
新消息由服务端推 `ConversationMessagePush` 过来。

几个踩过的坑都写进 `docs/LUNAR.md` 了，最坑的一个是：**握手里的启动器版本号填错会被直接踢**
（关码 1008 `Unsupported launcher version`）。要填**官方启动器**的版本（当前 3.7.27），
不是游戏日志里那个 `--launcherVersion 3.3.0`。

## ② 「回到游戏」以前是坏的

上一版这个按钮只发了一条 `FocusGamePush`。游戏确实收到了、也确实执行了 ——
日志里 `Received focus game push, focusing window` 一行接一行，**窗口纹丝不动**。
原因是它内部只做 AWT 层面的 `toFront()`，被 Windows 的前台锁定挡住时不会真把窗口抬起来。

现在改成 PCL 自己动手：找到游戏的主窗口（按 `LWJGL` / `GLFW30` / `SunAwtFrame`
类名 + 进程号，再用尺寸排掉 GLFW 那个 0×0 消息窗口），最小化过就先还原，
然后把输入队列临时挂到当前前台线程上再 `SetForegroundWindow` —— 这是绕过前台锁定的老办法。
推送仍然发，但只当辅助信号，让游戏自己把键盘焦点也切过去。

## ③ 停止游戏：10 秒太短了

上一版给 10 秒。**实测不够**：1.8.9 装了整合包之后，收到退出请求要走完
「停集成服务器 → 存档落盘 → 关资源包 → 关声音」，十几秒很正常。10 秒一到就被杀，
等于存档只写了一半 —— 跟直接强杀没区别，白白浪费了这条推送。

现在改成看动静，而不是看表：

| 情况 | 行为 |
| --- | --- |
| 15 秒内退了 | 直接结束，不打扰 |
| 还在退（有日志 / 在烧 CPU） | 状态栏换成「游戏正在保存存档…」，继续等 |
| 连续 30 秒没日志也没 CPU | 判定卡死，强制结束 |
| 总共超过 2 分钟 | 放弃等待，强制结束 |

也就是说正常情况它会一直等到游戏自己退完，只有**真卡住**才会提前杀。

## ④ 启动后不再误报「疑似卡死」

以前判据是「有没有窗口」+「原版依赖目录有没有变大」。两个都会误判：

- Lunar 自带的 log4j 配置会让文件 appender 起不来（日志里那几行
  `Unable to locate appender`），`logs/latest.log` 根本不写 → 「没窗口」永远成立
- 物料齐了以后体积本来就不再涨 → 「没变大」也永远成立

结果就是游戏都进世界了，还在弹「启动 100 秒后既没有窗口、原版依赖也没有再变大」。
现在按四个信号判断活着，任意一个成立就继续等：**窗口出来了**、**最近 90 秒吐过字**、
**原版目录还在变大**、**进程还在烧 CPU**。四个同时熄火连续三轮（约 60 秒）才提示。

## 升级

直接覆盖，配置不变。好友功能需要 Lunar 账户库里有账号（或者 PCL 里登录了同一个微软账号）。

## 下载

| 架构 | 文件 |
| --- | --- |
| x64 | `PCL-Lite_1.6.3-snapshot-3_x64.exe` |

单文件、免解压。**需要已安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)**。

SHA-256：`E661603679C4C3B60F8BA301A1A3185B08A5445501FB6019387061848F4A16B1`

## 已知问题

- **好友/私聊目前只有一对一**，群聊（`CONVERSATION_TYPE_GROUP_CHAT`）还没做界面，
  但协议已经解出来了
- **好友列表要手动点「连接」**，不会自动连 —— 每次连接都要换一次 JWT，还要调一次
  Mojang 的 sessionserver，不适合在启动时闷头做
- **令牌过期的账号连不上**：Lunar 后端只认有效的 Minecraft 令牌。PCL 里没登录对应账号时
  请先在账号页登录
- **Lunar 官方启动器开着时不要同时连**：两条连接用同一个账号会被服务端踢掉其中一条
- 停止等待上限两分钟：真遇到超长存档仍然会被强杀，但比 10 秒好得多
- 「回到游戏」要求窗口已经出现（进到主菜单之后）。加载期间点会提示「还没找到游戏窗口」
- 游戏内聊天**没有面板**，消息只进日志
- 内嵌结账不支持：Tebex / PayNow 都会回「去网页买」，PCL 没有内嵌浏览器
- 发布文件已做代码签名（SHA-256 + DigiCert 时间戳）；证书为自签名，暂无商业信誉，
  个别安全软件首次运行时仍可能提示发布者信息
- 基岩版 GDK 安装包（`.msixvc`）解包依赖内置的内容密钥（CIK），正式版与预览版各一把，
  均已编入本构建；密钥若被官方轮换会失效，届时用环境变量覆盖即可，不必重新下载启动器
- Lunar 物料清单接口偶发 `SSL unexpected EOF`，重试几次通常能过
- 补齐原版依赖是按文件存在性判断的，不校验哈希；文件损坏（下了一半）仍要等 Genesis 自己
  发现并重下，那种情况依旧没有进度
- PCL2 原版（`setup.ini`）来源的导入只搬设置，**账号需要在账号页重新登录**
- UWP 安装需要开发者模式；系统策略禁用旁加载时只能手动到「设置 → 系统 → 开发者选项」打开

## 许可证

本项目基于 PCL-CE（MIT）修改，基岩版下载、安装与启动部分移植自
[PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)（Apache-2.0），
完整署名见 `docs/CREDITS.md`。
