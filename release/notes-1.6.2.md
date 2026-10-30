<summary>修「Lunar 主界面黑色背景变透明」——UI 资源被解到了错误的目录，<code>assets/</code> 根本没被创建；修「PCL 覆盖 Lunar 启动器已有的内存设置」——现在会读官方启动器的 <code>allocatedMemory</code> 作为默认值，堆大小也改成纯粹的自定义输入；顺带把按版本覆盖的默认值填成当前生效值，不再留一个空框。</summary>

PCL Lite 1.6.2 正式版。

## ① Lunar 主界面的黑色背景没了（变透明）

现象：用 PCL 启动 Lunar，进去以后主界面是一片透明，能看见桌面。官方的 UI 贴图、字体、样式全都不生效。

根因在 UI 资源的落盘目录。

Lunar 的 UI 是一套打包好的网页：`ui/<sha1>/index.html` 引用的是
`/assets/index.css` 和 `/static/index.js`。PCL 下载 `ui.assets` 那一批文件时，
索引里的相对路径是**平的**（`bg.png`、`index.css`、`DMSans-*.ttf`…），
我们照着索引直接解到了 `ui/<sha1>/` 根目录 —— 于是 `assets/` 这个目录从来没被创建过，
WebOSR 加载不到 CSS 和背景图，页面就成了透明的一片。

官方启动器的做法是解到 `ui/<sha1>/assets/`（反编译 `main.js`：
`destination = join(uiDir, "assets")`）。现在改成一致。

已经踩过的目录不用重装：每次启动都会走一遍 `EnsureInstalledAsync`，
缺的文件会被补到 `assets/` 下，`index.html` 和 `static/` 保持原样。

## ② PCL 不再覆盖 Lunar 启动器里的内存设置

现象：在官方 Lunar 启动器里把内存调成 8G，换 PCL 启动却只有 3G（或者更少），
看起来就像 PCL 把设置改掉了。

其实是两边**各管各的**：

- 官方启动器把内存和 JVM 参数存在 `~/.lunarclient/settings/launcher.json` 的
  `settings.allocatedMemory` / `settings.jvmArgs`
- 游戏进程只认命令行，根本不读这个文件

PCL 以前完全不看它，只按自己的规则算：PCL 全局内存 → 保底 3072。于是用户辛苦调的
8G 就这么被盖掉了。上一版为了修背景透明，还加了一条「没配就至少给 3G」的硬保底 ——
背景其实不是内存的锅，这条保底反而成了新的覆盖源，这次一并删掉。

现在解析顺序是：

```
版本覆盖 → Lunar 全局 → 官方 Lunar 启动器 → PCL 全局 → 内置默认 3072
```

**只读不写。** PCL 不会去改 `launcher.json` —— 官方启动器每次改设置都会整份重写，
写进去的内容活不过下一次点击，互相写只会打架。

## ③ 堆大小改成自定义

设置 → Lunar Client 里的「最大内存」不再是一个「跟随 PCL 全局」的勾选框：

- 默认直接显示官方启动器里的数字（读不到才退回 3072），一眼能看到「Lunar 里设的 8G 在这儿」
- 右边加了个按钮，可以从 Lunar 启动器重新读一次
- 输入框永远可编辑，填什么就用什么，下限 512、上限 131072
- 下面一行小字写清楚这个数字是哪来的、改了会不会影响官方启动器

按版本覆盖也一起改了：勾上「为这个版本单独设置」时，内存框会**自动填成当前生效的值**，
而不是留一个空框让人猜「留空到底是继承了多少」。

## ④ 顺带

- 修更新检查里「已是最新」的判断方向（`IsUpToDate` 用 `!IsNewer` 而不是 `IsAtLeast`），
  以前会把带后缀的远端版本判成更旧
- 窗口大小的预设只决定**启动时的默认尺寸**，之后拉窗口不再回写预设
- 紧凑尺寸下主题色卡换行，不再和提示文字重叠
- Lunar 图标换回 `brand/lunar`

## 升级

直接覆盖，配置不变。之前把内存调成「跟随 PCL 全局」的用户，升级后会自动接上官方的
`allocatedMemory`（如果读过官方启动器的设置）；想固定某个值，去设置里填一次即可。

## 下载

| 架构 | 文件 |
| --- | --- |
| x64 | `PCL-Lite_1.6.2_x64.exe` |

单文件、免解压。**需要已安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)**。

SHA-256：`DE26EC49C3DC33C2C6A961AE2F46B29C0B8D6AAE1FBE8FE048CA1D5B7AA3F65F`

## 已知问题

- 发布文件已做代码签名（SHA-256 + DigiCert 时间戳）；证书为自签名，暂无商业信誉，
  个别安全软件首次运行时仍可能提示发布者信息
- 基岩版 GDK 安装包（`.msixvc`）解包依赖内置的内容密钥（CIK），正式版与预览版各一把，
  均已编入本构建；密钥若被官方轮换会失效，届时用环境变量覆盖即可，不必重新下载启动器
- Lunar 的本地 IPC 只实现了 `AuthService/RefreshAccount`（会话续期），好友列表、游戏内商店、
  聊天面板这些走 IPC 的功能仍然是空的。回空响应是安全的，游戏不会崩，只是对应界面没数据
- **PCL 没登录对应账号时续期会失败**，游戏内还是显示 Invalid session。想彻底解决，
  在主页面登录同一个微软账号即可
- 首次启动某个 Lunar 版本时，Genesis 会先把整个原版客户端补到
  `%APPDATA%\.minecraft`（100–200 MB，没有窗口也没有进度）。PCL 会先把缺的文件下好，
  正常情况下这一步已经跳过了
- 补齐原版依赖是按文件存在性判断的，不校验哈希；文件损坏（下了一半）仍要等 Genesis 自己
  发现并重下，那种情况依旧没有进度
- Lunar 物料清单接口偶发 `SSL unexpected EOF`，重试几次通常能过
- 第三方 addon（比如 Meowtils）可能与某个 Lunar 构建不兼容，表现为进游戏直接
  `NoSuchMethodError` 崩掉，这类崩溃不归 PCL
- PCL2 原版（`setup.ini`）来源的导入只搬设置，**账号需要在账号页重新登录**
- 主页语言信封与 `lang:` 标签是 PCL Lite 扩展，原版 PCL 加载含这些内容的文件时
  会按普通 XAML 渲染（所有语言分支堆在一起）——属于预期的单向兼容

## 许可证

本项目基于 PCL-CE（MIT）修改，基岩版下载、安装与启动部分移植自
[PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)（Apache-2.0），
完整署名见 `docs/CREDITS.md`。
