<summary>更新卡片的「下载并安装」按钮不再被裁成半截；百宝箱的按钮行里加了「转移 PCL 设置」；Lunar 缺的原版依赖（libraries 那几百个文件）现在也交给 PCL 下载，进度进下载任务栏，不再由 Genesis 闷头下 1.3 GB。</summary>

PCL Lite 1.5.7 快照版（snapshot）。

> **这是快照版，不是正式版。** 把更新通道选成「快照尝鲜」就能收到；
> 「始终最新」也会收到，「稳定版」「预览体验」不会。

## ① 更新按钮被裁成「wnload and ins」

设置 → 启动器 → 更新，那颗「下载并安装」按钮写死了 `Width="85"`。中文四个字放得下，
英文 `Download and install` 放不下，于是左右各被裁掉一截，只剩中间的 `wnload and ins`。

现在按钮宽度跟着文案走（`MinWidth="85"`、`MaxWidth="230"`），所在列也从固定宽度改成 `Auto`。
「再次检查」「查看更新日志」和可选更新那张卡片是同一类问题，一起改了。
基岩版启动页的「打开文件夹」「启动」和百宝箱的成就预览按钮也顺手从 `Width` 换成 `MinWidth`。

## ② 百宝箱加「转移 PCL 设置」

1.5.6 把导入页放在百宝箱**左栏**，但左栏只有两项，很容易被忽略。现在百宝箱那张卡片的按钮行里
多了一颗 **转移 PCL 设置**，点它直接跳过去，左栏的勾选也跟着走。

功能本身没变：自动搜 `%APPDATA%` / `%LOCALAPPDATA%` 下的常见位置，也能自己选文件夹；
认 PCL CE / PCL Lite（`config.v1.json`）、PCL2 原版（`setup.ini`）、PCL2 便携版（`PCL\setup.ini`）；
导入前二次确认，覆盖前留 `.importbak`，源文件夹只读。

## ③ Lunar 缺的原版依赖也交给 PCL 下载

1.5.6 只看 `versions/<ver>/<ver>.{jar,json}` 在不在，来判断原版客户端齐没齐。这个判断不够：
Genesis 还要 `libraries` 里几百个文件，只缺那一半时判断结果是「已就绪」，于是 PCL 什么都不做，
Genesis 在后台自己下 —— 实测能下到 **1321 MB / 970 个文件**，界面上一句话都没有，
日志里只有一句「正在下载原版依赖：已 xx MB」。

现在改成：

- 用 PCL 自己的库文件清单（`ModLibrary.McLibNetFilesFromInstance`，主 jar 也在清单里）
  逐个核一遍，**缺一个就算没就绪**
- 没就绪就走 PCL 的下载任务，**进度进下载任务栏**，能看、能暂停
- 新增 `repairOnly` 参数：jar 和 json 都在、只缺 libraries 时，普通模式会被当成
  「实例已存在」直接跳过，加上这个参数才会照样去补齐
- 补不齐也不拦启动，Genesis 还会再试一次；这时日志会写明「Lunar 自己在补（PCL 那边没补完）」，
  不再让人以为是 PCL 卡住了

## ④ 繁中与英式英语补齐 59 个缺失的键

`zh-TW` 和 `en-GB` 一直缺 59 个键（JarInJar 模组关系面板、皮肤披风名、区域限制提示、
下载协议设置），其中 20 个直接在界面上用 —— 繁中下那几块显示的是原始键名
（`Instance.Resource.Mod.JarInJar.Entry` 这种）。

这次补齐了，四份语言文件现在**完全对齐**（各 3176 个键，无缺失、无重复），
`LocalizationTest.AllLanguageDictionariesShouldContainBaseKeys` 转绿。

## 下载

单文件、免解压。**需要已安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)**。

SHA-256：`57505EE2F1A6DE2E1621E3C71F18DDA4EC0A04EBE774ABC7089EF306C44E0C16`

## 已知问题

- 发布文件已做代码签名（SHA-256 + DigiCert 时间戳）；证书为自签名，暂无商业信誉，
  个别安全软件首次运行时仍可能提示发布者信息
- 基岩版 GDK 安装包（`.msixvc`）解包依赖内置的内容密钥（CIK），正式版与预览版各一把，
  均已编入本构建；密钥若被官方轮换会失效，届时用环境变量覆盖即可，不必重新下载启动器
- Lunar 的本地 IPC 只实现了 `AuthService/RefreshAccount`（会话续期），
  好友列表、游戏内商店、聊天面板这些走 IPC 的功能仍然是空的。回空响应是安全的，
  游戏不会崩，只是对应界面没数据。
- Lunar 物料清单接口偶发 `SSL unexpected EOF`，重试几次通常能过
- 补齐原版依赖这一步是按文件存在性判断的，不校验哈希；文件损坏（下了一半）仍要等
  Genesis 自己发现并重下，那种情况依旧没有进度
- 主页语言信封与 `lang:` 标签是 PCL Lite 扩展，原版 PCL 加载含这些内容的文件时
  会按普通 XAML 渲染（所有语言分支堆在一起）——属于预期的单向兼容
- UWP 安装需要开发者模式；系统策略禁用旁加载时只能手动到「设置 → 系统 → 开发者选项」打开

## 许可证

本项目基于 PCL-CE（MIT）修改，基岩版下载、安装与启动部分移植自
[PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)（Apache-2.0），
完整署名见 `docs/CREDITS.md`。
