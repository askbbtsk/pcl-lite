<summary>Lunar 现在每个版本都能自己选 loader（OptiFine / 原版 / Fabric / Forge，点一下就换）；修好 1.17.1 点启动后没窗口的问题；数据目录从 PCLCE 改名 PCLLite；基岩版页面不再被挤成一条缝。</summary>

PCL Lite 1.5.5 快照版（snapshot）。

> **这是快照版，不是正式版。** 把更新通道选成「快照尝鲜」就能收到；
> 「始终最新」也会收到，「稳定版」「预览体验」不会。

## ① Lunar 每个版本可以自己选 loader 了

Lunar 服务端的每个子版本都带一份 `modules` 清单，说明「这个版本能装哪些 loader」。
以前 PCL 写死用 `lunar`（Lunar + OptiFine），现在改成按服务端下发的清单来：

| module | 显示名 | 说明 |
| --- | --- | --- |
| `lunar` | Lunar + OptiFine | Lunar 自带 OptiFine 的那套 |
| `lunar-noOF` | Lunar | 不带任何额外 mod |
| `fabric` | Lunar + Fabric | |
| `forge` | Lunar + Forge | 老版本才有 |

版本下拉框**下面**多了一行胶囊按钮，点一下就切换，不用开下拉框再点一次。
默认值跟着官方走 —— 1.17.1 官方默认是 Fabric，1.8.9 默认是 OptiFine。
在 1.17.1 选了 Fabric 再切到不提供 Fabric 的版本，会自动退回该版本的默认，
但**你的选择不会被覆盖**，切回去还在。

同一版本换 loader 只换附件清单（OptiFine 那套 61 个附件，Fabric 那套 63 个），
主类、JRE、JVM 参数完全一样，所以不需要重下 JRE。

顺手修了一个老 bug：版本列表的磁盘缓存是扁平结构，却用服务端
`versions[].subversions[]` 的解析器去读，导致**缓存永远读不回来** ——
日志里一直是「已获取 0 个 Lunar 版本」。现在能正常读，旧格式的缓存会被判定无效并自动重拉一次。

## ② 1.17.1 点启动后没有窗口

不是 loader 选错，也不是物料没下完。**是 Lunar 自己在下载整个原版客户端。**

`Genesis` 在 `ClientGameBootstrap` 里会把目标版本的原版客户端补到
`%APPDATA%\.minecraft`：`versions/1.17.1/1.17.1.jar`（19.5 MB）+ json +
`libraries` 里上百个文件 + 资源索引。**全程没有窗口、没有进度条、
`ichor-boot.log` 也停在 `Launching Minecraft 1.17.1`** —— 从外面看就是「双击了没反应」。

本机的时间线正好对得上：1.8.9 的 jar 是 7 月就有的，所以它一直没出问题；
1.17.1 是第一次启动，06:47 下完 jar 之后卡在 libraries 上，被当成卡死杀掉了。

现在做了三件事：

- 启动前检查 `versions/<ver>/<ver>.{jar,json}`，不在就把状态改成
  「正在下载原版 X 的依赖…」并说明会下到哪个目录、大概多少量
- 启动后每 20 秒量一次 `libraries` + `versions` 的体积，在涨就报一行
  「已下载 xx MB、xx 个文件，继续等待」；**连续 100 秒不涨**才判定网络卡住并提示配代理
- 子进程退出时把退出码和最后 80 行输出写进 PCL 文件日志（以前只推给界面日志框，
  页面一关就没了，所以上次根本查不到死因）

## ③ 代理现在会注入游戏 JVM

`Genesis` 那次下载用的是 Java 的 `HttpClient`，**不读系统代理**。直连被墙时 TCP 能连上、
HTTP 应答永远不来，主线程永久阻塞在 `HttpClientImpl.send()`。

现在 `BuildStartInfo` 会把 PCL 当前生效的代理翻成 `-Dhttp.proxyHost` / `-Dhttps.proxyHost`
塞进 JVM 参数；PCL 没配代理时退回 `-Djava.net.useSystemProxies=true`。
`127.0.0.1` 已在 `nonProxyHosts` 里放过 —— 游戏要连本地 IPC 端口做会话续期。

## ④ 数据目录从 `PCLCE` 改成 `PCLLite`

| 用途 | 旧 | 新 |
| --- | --- | --- |
| 共享数据 | `%APPDATA%\PCLCE` | `%APPDATA%\PCLLite` |
| 共享本地数据 | `%LOCALAPPDATA%\PCLCE` | `%LOCALAPPDATA%\PCLLite` |
| 临时 | `%TEMP%\PCLCE` | `%TEMP%\PCLLite` |

**没有做迁移**（按你的要求）：从 1.5.4 及更早升级上来时，设置和登录状态是空的，
需要重新配置一次并重新登录。老目录原样留在磁盘上，新版本不读也不删。

## ⑤ 基岩版页面被挤成一条缝

左栏那一列是 `Width="Auto"`，而基岩版左栏没设宽度。状态里出现一句长文案
（「安装失败：没能拿到 1.18.10.21 的下载地址…」）就会把左栏按内容无限撑宽，
把右边版本列表压到只剩 230px —— 状态列固定 185 + 按钮 110，标题列直接被压成 0 宽，
所以只剩「未下载」三个字。

- 基岩版 / Lunar 左栏都固定成 260px，长文案改成换行 + 限高三行
- 下载状态文字从 `NoWrap` 改成 `Wrap`，失败原因不再被省略号吃掉
- 版本列表的标题从横向 `StackPanel` 换成 `Grid`（`StackPanel` 不给约束，
  `TextTrimming` 根本不生效，窄窗口下标题只会被硬裁）

## ⑥ 顺便说清「为什么有些旧版本获取不到」

不是网络问题。对 1.18.10.21 的 6 个 UpdateID 逐个请求微软 FE3 接口，
全部返回 `HTTP 200` 但 `GetExtendedUpdateInfo2Result` 是空的；同一个接口请求
1.21.114 / 1.21.101 都能正常返回 2 条下载地址。那个 2021 年的 Beta 包
**已经被微软从下载服务里下线**，`fe3cr` 端点、`GetExtendedUpdateInfo` v1、
revision 1/2/3 全试过都换不出来，rg-adguard 也只保留当前版本。

现在失败原因分三种分开报：下架 / FE3 接口报错（附 HTTP 详情）/ 连不上 FE3。
顺手修了个隐患：SOAP 里的 `a:MessageID` 原本写死成固定 UUID，会被服务端当成重放请求
回 `500 InvalidSecurity`，现在每次请求都生成新的。

## 下载

单文件、免解压。**需要已安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)**。

SHA-256：`472476881DB83C7FFD9E7CDF4A1368E8894FA5C80B54D198C3DEBA6A78CE5412`

## 已知问题

- 发布文件已做代码签名（SHA-256 + DigiCert 时间戳）；证书为自签名，暂无商业信誉，
  个别安全软件首次运行时仍可能提示发布者信息
- 基岩版 GDK 安装包（`.msixvc`）解包依赖内置的内容密钥（CIK），正式版与预览版各一把，
  均已编入本构建；密钥若被官方轮换会失效，届时用环境变量覆盖即可，不必重新下载启动器
- Lunar 的本地 IPC 只实现了 `AuthService/RefreshAccount`（会话续期），
  好友列表、游戏内商店、聊天面板这些走 IPC 的功能仍然是空的。回空响应是安全的，
  游戏不会崩，只是对应界面没数据。
- Lunar 物料清单接口偶发 `SSL unexpected EOF`，重试几次通常能过
- 首次启动某个 Lunar 版本时，Lunar 会先把原版客户端下到 `%APPDATA%\.minecraft`
  （约 100–200 MB），这期间没有窗口是正常的，页面状态会显示「正在下载原版 X 的依赖」
- 主页语言信封与 `lang:` 标签是 PCL Lite 扩展，原版 PCL 加载含这些内容的文件时
  会按普通 XAML 渲染（所有语言分支堆在一起）——属于预期的单向兼容
- UWP 安装需要开发者模式；系统策略禁用旁加载时只能手动到「设置 → 系统 → 开发者选项」打开

## 许可证

本项目基于 PCL-CE（MIT）修改，基岩版下载、安装与启动部分移植自
[PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)（Apache-2.0），
完整署名见 `docs/CREDITS.md`。
