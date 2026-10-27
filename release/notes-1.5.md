<summary>Lunar 会话过期后不用再开一次 Lunar 启动器了：PCL 现在自己接管游戏要用的本地 IPC 端口，直接在启动器里完成续期；顺带纠正了文档里「账户库那个 accessToken 不是 Minecraft 令牌」的错误结论。</summary>

PCL Lite 1.5 正式版。

## ① Lunar 会话能在 PCL 里续期了

之前每次 Lunar 会话过期（24 小时一次），游戏内账号面板就会变成
**Invalid session. Please re-authenticate.**，唯一的解法是打开一次 Lunar 启动器。

现在不用了。

原理不复杂，是之前没往下挖透：游戏进程里的 `lunar.jar` 在令牌过期时会通过
本地 WebSocket（`ws://127.0.0.1:<--ipcPort>`）调
`AuthService/RefreshAccount` 找启动器要续期，启动器刷完自己写
`accounts.json`，只回一个 `success`。PCL 不经过 Lunar 启动器，这条链路上没人
应答，所以必然失败。

1.5 起由 PCL 自己当这个服务端：

- 启动前抢 `28190`。抢到了就把 `--ipcPort 28190` 传给游戏，由 PCL 应答续期
- 抢不到说明 Lunar 启动器正开着（它固定占这个端口），端口原样交给游戏，
  让启动器去处理 —— 不会两个服务端抢同一条连接
- 端口被占但对方根本不响应 WebSocket，就传 `--ipcPort 0`，干脆不启用

续期用的是 **PCL 自己登录**的微软账号：Lunar 账户库里那个 `refreshToken`
绑死在 Lunar 的 OAuth 应用上（拿它换新令牌只会得到 `AADSTS70000`），第三方用不了；
PCL 的 refreshToken 绑在自己的应用上，能用。所以前提是主页面登录的是同一个号
（按游戏内名字比对，对不上宁可失败，也不会把别人的令牌写进这条账号）。

写完 `accounts.json` 先落盘再回响应 —— 游戏拿到 `success` 之后会立刻重读那个文件。

## ② 顺手纠正一个说错的结论

1.4.x 的文档里写着「`accounts.json` 里的 `accessToken` 是 Lunar 自己签的 JWT，
拿它联机会被服务器拒掉」。**这是错的。**

它是货真价实的 Minecraft 令牌，`iss: authentication` 是 Mojang 的签发者，
`aid` 只是拿它时用的 Azure 应用。启动器自己也拿它去
`POST sessionserver.mojang.com/session/minecraft/join` 建会话 —— 反编译
`app.asar` 能看到 `executeWithAccessToken(t, e => Bke(e, u))` 那一段，
`Bke` 就是 join 请求。

所以之前那句「PCL 里没有这个账号的登录就只能退回账户库那份（联机大概率失败）」
也一起改了：退回的那份能联机，只是**不会再续期**；过期了才会失败。

## ③ 已知限制

- **只实现了 `AuthService`**。Lunar 启动器在 IPC 上注册了 9 个服务（社交、
  皮肤、实时设置同步、Tebex 等），PCL 只做了续期这一个，其余调用一律回空响应。
  好友/社交面板仍然不可用，这和 1.4.x 一样，没有变差
- **PCL 没登录对应账号时续期会失败**，游戏内还是显示 Invalid session。
  想彻底解决，在主页面登录同一个微软账号即可
- **同时只跑一次续期**。并发的第二个请求直接回 `ALREADY_REFRESHING`，
  这是游戏侧本来就认识的错误码

## ④ 升级

直接覆盖，配置不变。

1.4.7 是预览版，正式版通道之前停在 1.3.4 —— 这次会把正式版通道一次性带到 1.5。

## 下载

| 架构 | 文件 |
| --- | --- |
| x64 | `PCL-Lite_1.5_x64.exe` |

单文件、免解压。**需要已安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)**。

SHA-256：`6883FDFD4C29340210EB75B4DDF563C7BEC8117B73816292013F5E2C8F81275C`

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
