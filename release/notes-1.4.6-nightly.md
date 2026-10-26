<summary>修 Lunar 账号的令牌问题：命令行现在传真正的 Minecraft 令牌，不再是 Lunar 自己签的那串 JWT；账号列表会标出「会话已过期」，并说明为什么游戏内会显示 Invalid session。</summary>

PCL Lite 1.4.6 不稳定版（nightly）。

> **这是 nightly，不是正式版。** 只有把更新通道选成「最新」才会收到。

## 这次修的是「启动成功但账号校验失败」

你反馈的现象是对的，而且原因不止一个。查下来是两件事叠在一起。

### ① 命令行里传错了令牌（这个是 bug，已修）

`~/.lunarclient/settings/game/accounts.json` 里那个 `accessToken`，我一直当成
Minecraft 的会话令牌在用。**它不是。** 解出来看，签发方是 `authentication`，
里面是 `xuid`、`profiles`、`platform` 这些字段，有效期 24 小时——这是
**Lunar 自己签的 JWT**，只给它自己的后端用。

Mojang 的会话校验不认这个。所以拿它当 `--accessToken` 传给游戏，
进别人的服务器会被直接拒掉。

现在的做法：

- PCL 主页面登录的账号如果和 Lunar 这条**同名**，就用 PCL 的 Minecraft 令牌
- 对不上就不用（令牌和 UUID 绑在一起，张冠李戴联机会被踢），退回账户库那份，
  日志里会写明「联机大概率失败」
- 运行日志里能看到这次到底用了哪份

### ② Lunar 自己的会话过期了，这个只能它的启动器续（不是 bug）

你的账号 `Bed_Spacekiller_` 的 Lunar 会话在 **10 月 20 日**就过期了，
`lunar.jar` 里对应的失败原因是 `launcher_not_open`：

```
com.lunarclient.gameipc.auth.v1.AuthService / RefreshAccount
  RefreshAccountRequest  { Uuid uuid }
  RefreshAccountResponse { bool success; AccountError error }
```

响应里没有 token 字段——说明**续期是启动器干的**：它刷新完自己写
`accounts.json`，然后只回一个「成功」。游戏只是通过本地 IPC 等这个结果。

我们不是 Lunar 启动器，那条 IPC 也没接，所以刷新一定失败，面板就显示
**Invalid session. Please re-authenticate.**

也试过替它刷新，不行：`accounts.json` 里的 `refreshToken` 是微软发的，
但绑在 Lunar 的 OAuth 应用上，换谁的 client id 去换都会拿到

```
AADSTS70000: The token was issued for a different client id
```

**所以这一条目前只能这样绕：每隔 24 小时打开一次 Lunar 启动器，让它把会话刷新掉。**
它不影响进游戏、也不影响联机（那是 ① 里那份 Minecraft 令牌的事），
只影响 Lunar 自己的账号面板和它家的后端功能。

### 界面上现在会说清楚

- 账号下拉框里过期的会直接写「（会话已过期）」，鼠标悬停能看到过期时间
- 左栏账号卡片同理，并给出「打开一次 Lunar 启动器即可刷新」
- 启动时运行日志会打印：用的哪个账号、会话有没有过期、令牌从哪来

## 从 1.4.5-nightly 升上来

直接覆盖，配置不变。

## 当前不支持

- **Lunar 会话过期后无法在 PCL 内续期**（见上文 ②）。要彻底解决，需要在 PCL 里
  实现 Lunar 那条本地 IPC 的 `AuthService`，而且就算接上了，也得拿到 Lunar 后端签的
  JWT 才行——这部分暂时没做
- **好友与社交面板**依赖上面那个会话，会话有效期内可用，过期后不可用
- **`baseModpack`**（1.21+ 响应里那份 mrpack）没有铺进实例。实测不铺也能起来；
  如果某些模组功能异常，先怀疑这里
- 首次切到一个没玩过的 Lunar 版本时，需要下载该版本全部物料（几十到一百多 MB）
  和对应 JRE（约 40 MB），会慢一次

## QQ 交流群

**QQ 交流群：1085542362** —— 点一下用浏览器打开官方加群短链。
入口在欢迎页的「社区」卡片，以及「设置 → 关于」里贡献者列表下方。

## 下载

| 架构 | 文件 |
| --- | --- |
| x64 | `PCL-Lite_1.4.6-nightly_x64.exe` |

单文件、免解压。**需要已安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)**。

SHA-256：`713904C7C38E3871C9F7F07F76072FEFC0F39EF62298821E9ED387BD07EC6467`

## 已知问题

- 发布文件已做代码签名（SHA-256 + DigiCert 时间戳）；证书为自签名，暂无商业信誉，
  个别安全软件首次运行时仍可能提示发布者信息
- 基岩版 GDK 安装包（`.msixvc`）解包依赖内置的内容密钥（CIK），正式版与预览版各一把，
  均已编入本构建；密钥若被官方轮换会失效，届时用环境变量覆盖即可，不必重新下载启动器
- 主页语言信封与 `lang:` 标签是 PCL Lite 扩展，原版 PCL 加载含这些内容的文件时
  会按普通 XAML 渲染（所有语言分支堆在一起）——属于预期的单向兼容
- UWP 安装需要开发者模式；系统策略禁用旁加载时只能手动到「设置 → 系统 → 开发者选项」打开

## 许可证

本项目基于 PCL-CE（MIT）修改，基岩版下载、安装与启动部分移植自
[PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)（Apache-2.0），
完整署名见 `docs/CREDITS.md`。
