<summary>仓库迁移至 askbbtsk/pcl-lite，自动更新改为读 update.json 指针（以后再换仓库旧版本也不会失联）。新增主页语言 API：同一份主页内容可针对不同语言展示不同内容，完全兼容 PCL 原版主页协议；欢迎页改用该协议实现多语言；精简为简体中文 / 繁體中文 / 英文三种语种；标题栏 Lite 徽标改为纯文字并不再强加到自定义标题后面。</summary>

PCL Lite 1.3.1 正式版。

> **仓库已迁移**：本项目现在位于 <https://github.com/askbbtsk/pcl-lite>。
> 旧的 `bedkillerspacex-boop/Pcl-Lite` 已转为私有备份，不再更新。
> 下载、提交 issue、查看源码请一律使用新地址。

## 新增：主页语言 API

主页协议现在支持**语言协商**——同一份主页内容可以对不同语言展示不同内容。
这是对 PCL 原版主页协议的**纯增量扩展**，原版主页文件无需任何改动即可继续使用，行为完全一致：

| 内容形态 | 行为 |
| --- | --- |
| 不以 `{` 开头的 XAML 片段（原版主页） | 原样交给 XAML 解析器，与上游 PCL 完全一致 |
| 不含任何 `lang:` 标签的元素 | 一个都不会被移除 |
| 是合法 JSON 但没有 `languages` / `default` 字段 | 原样返回，按普通文本处理 |

### 机制一：语言信封（整页级）

主页内容以 `{` 开头且包含 `languages` 或 `default` 字段时，视为语言信封，按当前 UI 语言
取出对应分支的 XAML：

```json
{
  "format": "pcl-lite-homepage-lang",
  "version": 1,
  "default": "<TextBlock Text=\"Fallback\" />",
  "languages": {
    "zh-CN": "<TextBlock Text=\"简体中文主页\" />",
    "zh-TW": "<TextBlock Text=\"繁體中文主頁\" />",
    "en": "<TextBlock Text=\"English homepage\" />"
  }
}
```

取值顺序：完整代码精确匹配 → 中性代码匹配（`en` 同时命中 `en-US` 与 `en-GB`）→ `default` →
都没有则显示空白并记录警告。

### 机制二：`lang:` 标签（元素级）

XAML 中任意元素都可以带语言标签，实例化后只保留命中当前语言的分支：

```xml
<StackPanel>
    <TextBlock Tag="lang:zh-CN" Text="只在简体中文下显示" />
    <TextBlock Tag="lang:zh-TW" Text="只在繁體中文下顯示" />
    <TextBlock Tag="lang:en" Text="Shown for both en-US and en-GB" />
    <TextBlock Tag="lang:default" Text="没有任何语言命中时的兜底" />
    <TextBlock Text="我没有 Tag，所有语言都显示" />
</StackPanel>
```

- `lang:zh` / `lang:en` 等**中性标签**匹配同语言的所有区域，写一份即可覆盖 en-US 与 en-GB
- `lang:default` 是兜底分支，仅在没有更精确的分支命中时保留
- **按父容器生效**：只有存在命中分支时才移除未命中的；若某容器下没有任何分支命中，
  则保留全部——主页不会因为漏翻而变成空白
- 两种机制可叠加：信封决定整页用哪份 XAML，标签决定这份 XAML 里哪些元素可见

### 接入方式

本地文件（`PCL/Custom.xaml`）与联网 URL 两种主页来源走同一条加载管线，都支持上述机制。
切换界面语言后主页立即按新语言重新加载；停留在其他页面时只清缓存，不会把用户拽回主页。

完整说明见 [docs/HOMEPAGE-LANG.md](https://github.com/askbbtsk/pcl-lite/blob/main/docs/HOMEPAGE-LANG.md)，
含 23 个单元测试覆盖代码归一化、标签匹配、信封分支选择与可视化树过滤。

## 欢迎页多语言

欢迎页改用主页语言协议实现多语言，是该协议的第一个内置示例：

- 标语与两张卡片的说明文字按 `lang:zh-CN` / `lang:zh-TW` / `lang:en` 分分支存放，
  由 `HomepageLanguageApi` 在加载时按当前语言过滤
- 卡片标题、按钮与网络授权状态行沿用 `DynamicResource` / `Lang.Text`，切换语言立即刷新
- 切换语言后欢迎页整体重建，不会出现「切了语言但主页还是旧语言」的情况

## 语言精简

只保留**简体中文**、**繁體中文**与**英文**（en-US / en-GB）三个语种：

- 删除 日本語 / Français / Español 三个长期只有机翻残页的语言
- 其余语种若在配置里被选中，会回退到简体中文

## 标题栏 Lite 徽标

- 原来是黑底 + 绿色竖条 + 等宽粗体 `LITE` 的方块徽标，现改为紧贴 PCL 字标的纯文字 **Lite**，
  字重比 PCL 略粗一点（SemiBold）
- **不再强加到自定义标题后面**：只有使用默认 PCL 字标时才显示 Lite；
  自定义文本标题、自定义图片标题以及 HMCL 主题下都不再出现

## 设置 → 语言

- 删除「前往翻译平台」按钮，只保留「提交 PR」
- 国际化横幅第二行文案同步改为只提 Pull Request

## 静默更新下线

「自动下载并安装」这个选项没了。更新必须让用户看见：

- 下载完成后亮出重启按钮并弹提示，**不点就不替换 exe**
- 老配置里选了静默更新的用户会被自动改成「自动下载并提示更新」
- 设置页的该项已隐藏，不会再被选中

理由很直接：静默替换 exe 是唯一一条「用户完全不知情、程序自我改写」的路径，
出问题的时候既没有现场也没有日志，排查成本远高于省下的那一次点击。

## 主页语言 API 双端兼容

用 PCL Lite 的语言 API 写主页，直接丢给原版 PCL 会出两种问题：
只打 `lang:` 标签 → 所有分支一起显示（丑）；用语言信封 → 原版 PCL 把 JSON 当 XAML 解析，**直接报错**。

现在给出了双端兼容写法：非默认分支预先写 `Visibility="Collapsed"`，并保留一个 `lang:default` 兜底。
PCL Lite 会把命中当前语言的那一支重新设为可见，原版 PCL 则只显示默认分支——
同一份文件在两个客户端上都正常，**不会崩、不会重叠**。

完整 API 参考与写法约定见
[docs/HOMEPAGE-LANG.md](https://github.com/askbbtsk/pcl-lite/blob/main/docs/HOMEPAGE-LANG.md)。

## 更新机制：update.json

自动更新不再直接盯着「某个仓库有没有发 Release」，而是先读一份只有一行指针的清单：

```json
{ "update": "askbbtsk/pcl-lite/releases/latest" }
```

清单地址固定在 `Pcl-Lite/Pcl-Lite-service`，**以后换发布仓库只改这一行**，
所有已经装出去的旧版本下次检查更新时会自动走到新地址，不需要发版、不需要重装。
版本号、直链、SHA-256 全部由 Release 自己提供，因此发版也不用再手改清单，
不存在「忘了改哈希导致所有人更新失败」这种坑。

拉清单本身是一串通道链：raw 直连 → 多个 GitHub 反代 → jsDelivr → GitHub Contents API，
前一个失败自动换下一个；全部失败则回退到直接读当前仓库的 Releases，行为与以前一致。

完整说明见 [docs/UPDATE.md](https://github.com/askbbtsk/pcl-lite/blob/main/docs/UPDATE.md)。

## 修复

- 修复 1.3.1-beta 因语言变更回调签名不合法导致主窗体构造失败、启动白屏的问题
- `PCL.Core` 程序集版本号此前停留在 1.2.5，已同步为 1.3.1

## 社区

遇到问题、想反馈、或者只是想看看别人怎么折腾，欢迎来 QQ 交流群：

**QQ 交流群：1085542362**

启动器里也放了这个入口：欢迎页的「社区」卡片，以及「设置 → 关于」里贡献者列表下方。

## 下载

| 架构 | 文件 |
| --- | --- |
| x64 | `PCL-Lite_1.3.1_x64.exe` |

单文件、免解压。**需要已安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)**。

SHA-256：`f6f33e0b6d5b9218e73c3b4eb24f145f70e246cf2bff326cd7e6f48635480a24`

## 已知问题

- 主页语言信封与 `lang:` 标签是 PCL Lite 扩展，原版 PCL 加载含这些内容的文件时
  会按普通 XAML 渲染（所有语言分支堆在一起）——属于预期的单向兼容
- UWP 安装需要开发者模式；系统策略禁用旁加载时只能手动到「设置 → 系统 → 开发者选项」打开
- 预览版 GDK 包仅在设备拥有该包 license（装过商店预览版）时才能解密
- 发布文件已做代码签名（SHA-256 + DigiCert 时间戳）；证书为自签名，暂无商业信誉，
  个别安全软件首次运行时仍可能提示发布者信息
- 自动更新暂不做完整性哈希校验，以下载通道自身的安全性为准

## 许可证

本项目基于 PCL-CE（MIT）修改，基岩版下载、安装与启动部分移植自
[PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)（Apache-2.0），
完整署名见 `docs/CREDITS.md`。
