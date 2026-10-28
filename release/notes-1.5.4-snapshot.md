<summary>修「Lunar 账号显示 Invalid session」：令牌过期后 PCL 会先刷新再启动，不再拿着过期的令牌硬上；Lunar 启动器开着但没登录时也不再把手感续期的端口让给它。</summary>

PCL Lite 1.5.4 快照版（snapshot）。

> **这是快照版，不是正式版。** 把更新通道选成「快照尝鲜」就能收到；
> 「始终最新」也会收到，「稳定版」「预览体验」不会。

## ① 为什么游戏里显示 Invalid session

日志里是这样的：

```
[Lunar] 账号：Bed_Spacekiller_（Lunar 账户库，Xbox）
[Lunar] Lunar 会话已于 2026-10-29 06:31 过期。
[Lunar] Minecraft 令牌：PCL 里没有这个账号的登录，账户库那份又已经过期 —— 联机大概率失败
```

明明 PCL 里登录着同一个账号（前两天还能打出「取自 PCL 登录」），却被判成
「没有这个账号的登录」。原因在 `TryGetPclToken` 的两行代码：

```csharp
if (string.IsNullOrWhiteSpace(profile.AccessToken) || profile.IsExpired) return null;
```

**PCL 登录里的令牌 24 小时就过期，账户库里那份也一样。** 一旦两边都过期，
这个函数就返回「没有登录」——而不是去刷新。于是 PCL 拿着账户库里那份同样过期的
令牌启动，游戏连 `sessionserver` 失败，面板上就是 `Invalid session. Please re-authenticate.`

现在改成启动前先刷：

```
选中 Lunar 账号
  → 在 PCL 里找同一个账号（UUID 优先，其次同名）
  → 令牌过期就用档案里的 refreshToken 静默刷一次（不弹窗、不重登）
  → 新令牌写回 accounts.json（游戏进程读的是这个文件）
  → 再用新令牌启动
```

刷不动的时候也不再闷着：页面上的账号提示会写清楚，同时弹一条
「会话已过期，而且没法自动续期 —— 游戏内会显示 Invalid session」的提示。

## ② Lunar 启动器开着但没登录时，不再把端口让给它

游戏要续期时会连 `127.0.0.1:28190`。以前只要这个端口被占（Lunar 启动器固定占它），
PCL 就认为「启动器在跑，续期交给它」——可启动器**没登录**的时候它根本续不了，
游戏照样弹 Invalid session。

现在只要 PCL 能自己续期（PCL 里有这个账号的登录），就抢不到 28190 也退到 **28191**，
把那个端口指给游戏。真正续不了的时候才让位。

## ③ 账号提示文案跟着改

- 「进游戏后会自动续期」→「启动前会先用 PCL 里的 xxx 刷新一次」（这才是实际行为）
- 判断「能不能续期」改成和续期时同一套匹配规则（UUID 优先），以前只比用户名

## ④ 设置 → Lunar 打不开了（1.5.2 起的回归）

打开「设置 → Lunar」会直接弹：

```
System.Windows.Markup.XamlParseException: 无法从文本"BtnBrowseJava_Click"创建"Click"。
 ---> System.ArgumentException: Cannot bind to the target method because its signature
      is not compatible with that of the delegate type.
```

原因是我把两个按钮的处理函数写成了 `(object, RoutedEventArgs)`，但 PCL 的自定义按钮
`Click` 不是路由事件：

| 控件 | `Click` 的第二个参数 |
| --- | --- |
| `MyButton` / `MyExtraButton` / `MyExtraTextButton` / `MyListItem` | `MouseButtonEventArgs` |
| `MyIconTextButton` | `ModBase.RouteEventArgs` |
| `MyIconButton` | `EventArgs` |
| `MyTextButton` | `EventArgs` |

**这种错误编译期查不出来**——XAML 里的 `Click="名字"` 只是个字符串，签名在加载页面时才反射匹配，
所以打不开那个页面就永远发现不了。这次顺手写了 `tools/audit-xaml-events.py` 把全仓
167 处 `Click` 绑定和 code-behind 的签名对了一遍，除了这两个没有别的。

## 下载

单文件、免解压。**需要已安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)**。

SHA-256：`FB1A6818FDF23AEDEC4F1A9773B34274A05A0656BEA098FE544D46651BEAFB89`

## 已知问题

- 发布文件已做代码签名（SHA-256 + DigiCert 时间戳）；证书为自签名，暂无商业信誉，
  个别安全软件首次运行时仍可能提示发布者信息
- 基岩版 GDK 安装包（`.msixvc`）解包依赖内置的内容密钥（CIK），正式版与预览版各一把，
  均已编入本构建；密钥若被官方轮换会失效，届时用环境变量覆盖即可，不必重新下载启动器
- Lunar 的本地 IPC 只实现了 `AuthService/RefreshAccount`（会话续期），
  好友列表、游戏内商店、聊天面板这些走 IPC 的功能仍然是空的。回空响应是安全的，
  游戏不会崩，只是对应界面没数据。
- Lunar 物料清单接口偶发 `SSL unexpected EOF`，重试几次通常能过
- 首次安装会下 300～500 MB（取决于版本），网速慢的话请耐心等进度条。
- 主页语言信封与 `lang:` 标签是 PCL Lite 扩展，原版 PCL 加载含这些内容的文件时
  会按普通 XAML 渲染（所有语言分支堆在一起）——属于预期的单向兼容
- UWP 安装需要开发者模式；系统策略禁用旁加载时只能手动到「设置 → 系统 → 开发者选项」打开

## 许可证

本项目基于 PCL-CE（MIT）修改，基岩版下载、安装与启动部分移植自
[PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)（Apache-2.0），
完整署名见 `docs/CREDITS.md`。
