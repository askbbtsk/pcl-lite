<summary>修一个 1.5.11 引入的严重回归：下载页、模组搜索、资源包页全部报「The format of value 'PCL-Lite' is invalid.」。另外加了启动器窗口大小预设、修好从 PCL2 导入后主题颜色不生效、把 PCL2 存在注册表里的那 58 项设置也纳入导入。</summary>

PCL Lite 1.5.12 预览版（pre）。

> **这是预览版，不是正式版。** 把更新通道选成「预览体验」就能收到；
> 「快照尝鲜」「始终最新」也会收到，「稳定版」不会。

> **1.5.11 有问题，请直接升到这一版。** 那版的下载页是完全不可用的。

## 一、下载页整个空掉（1.5.11 的回归）

「模组」「整合包」「资源包」这些页面一打开就是一片红字：

```
The format of value 'PCL-Lite' is invalid.
```

1.5.11 为了不再上报版本号，把 User-Agent 从 `PCL-Lite/1.5.11-pre` 改成了 `PCL-Lite`。
值是对的，**但用错了构造函数重载**：

```csharp
new ProductInfoHeaderValue("PCL-Lite")          // 单参数 = 注释版，值必须是 "(注释)"
new ProductInfoHeaderValue("PCL-Lite", 版本号)   // 双参数 = 产品名 + 版本
```

单参数那个收的是 **comment**，`PCL-Lite` 不以 `(` 开头，构造时就抛
`FormatException`。而这行在 `ConfigureHttpClient` 里，等于**任何一次网络请求都会炸** ——
所以整个下载页、模组搜索、CurseForge 和 Modrinth 全部取不到数据。

现在改成双参数、显式传 `null`（版本为 null 时只输出产品名，正是想要的效果）。

顺带把下载器那行的 `Headers.Add` 换成 `TryAddWithoutValidation`：前者会先按 RFC 解析一遍
UA，解析不过同样抛 `FormatException`，同一个坑不该留两次。

**这次真的验证过了**，不是「应该没问题」：

```
旧写法 单参数: FormatException - The format of value 'PCL-Lite' is invalid.
新写法 双参数 null: OK  -> [PCL-Lite]
下载器 UA: OK  -> [PCL2 PCLCE Mozilla/5.0 ... Edg/136.0.0.0]
```

## 二、启动器窗口大小可以设了

「设置 → 个性化 → 基本」里多了一行「窗口大小」：

- 紧凑 820 × 520
- 标准 900 × 550
- 宽屏 1080 × 650
- 超大 1280 × 760
- 自定义（自己填宽 × 高）

选中即刻生效，不用重启。选「自定义」才露出两个输入框；手动拖过窗口边界再回来，
会自动落到「自定义」并显示当前实际尺寸。

两个限制：不超过屏幕工作区（否则窗口会有一截跑到屏幕外、拖不回来），
也不低于窗口自身的最小尺寸（820 × 470，比这更小的值窗口会自己夹回去）。

## 三、从 PCL2 导入后主题颜色不生效

PCL2 用一个编号（`UiLauncherTheme`）同时决定配色和标题栏纹理。这里把它拆成了两半：
**编号管纹理**，**`UiLauncherHue/Sat/Light/Delta` 管颜色**。

导入只搬了编号，于是纹理换了、颜色还是旧的 —— 表现就是「看着不对劲，回设置页重选一次才好」，
因为手动点预设时 `ApplyPreset` 会把这两半一起写。

现在 `UiLauncherTheme` 变化时会检查编号对应的预设，发现参数对不上就按预设补齐。
参数已经一致时什么都不做，所以正常点预设不会被重复覆盖。
顺手把 PCL2 的编号 42（「极客蓝」的另一个编号，和 11 同色）规范成 11，否则纹理和颜色都对不上。

## 四、PCL2 的注册表设置也一起导入

以前「转移 PCL 设置」只读 `PCL\setup.ini`。但 PCL2 的配置其实分两半：

| 位置 | 内容 |
| --- | --- |
| `PCL\setup.ini` | 默认来源的键，明文，通常只有十几个 |
| `HKCU\Software\PCL` | 显式标了注册表来源的键，**83 个**，其中 19 个加密 |

也就是说代理设置、调试开关、启动次数这些**从来没被搬过来过**。现在两半都读，
83 个里能对上 58 个 PCL Lite 认得的键。

加密的那 19 个用 `EncryptHelper.SecretDecrypt` 解（PCL-CE 一直保留着这条旧版兼容路径）。
密钥派生自 `SHA256(CpuId)`，**换台机器就解不开**——那种情况会跳过该键并记日志，不会写空值进去。

**账号仍然不搬**：`LoginMsJson` 里只有 `{UserName: OAuthToken}`，PCL2 不保存 refreshToken，
accessToken 二十四小时过期且无法续期，搬过来只会多一条启动即失败的档案。
导入完成的提示里会告诉你发现了几个账号、需要去账号页重新登录。

## 下载

单文件、免解压。**需要已安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)**。

SHA-256：`51CA170850A743F47C51598605DE600F474CB7C158F1BBE546E521D52DF0168B`

## 已知问题

- 发布文件已做代码签名（SHA-256 + DigiCert 时间戳）；证书为自签名，暂无商业信誉，
  个别安全软件首次运行时仍可能提示发布者信息
- 基岩版 GDK 安装包（`.msixvc`）解包依赖内置的内容密钥（CIK），正式版与预览版各一把，
  均已编入本构建；密钥若被官方轮换会失效，届时用环境变量覆盖即可，不必重新下载启动器
- PCL2 原版里改过名的键搬不过来，比如「上次选的版本」（PCL Lite 叫 `LaunchInstanceSelect`，
  PCL2 叫 `LaunchVersionSelect`），导入后需要重新选一次
- Lunar 的本地 IPC 只实现了 `AuthService/RefreshAccount`（会话续期），
  好友列表、游戏内商店、聊天面板这些走 IPC 的功能仍然是空的。回空响应是安全的，
  游戏不会崩，只是对应界面没数据。
- Lunar 物料清单接口偶发 `SSL unexpected EOF`，重试几次通常能过
- 补齐原版依赖这一步是按文件存在性判断的，不校验哈希；文件损坏（下了一半）仍要等
  Genesis 自己发现并重下，那种情况依旧没有进度

## 许可证

本项目基于 PCL-CE（MIT）修改，基岩版下载、安装与启动部分移植自
[PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)（Apache-2.0），
完整署名见 `docs/CREDITS.md`。
