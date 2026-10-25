# 主页语言 API

PCL Lite 的主页协议在 PCL 原版基础上增加了**语言协商**能力：同一份主页内容可以针对不同语言
展示不同内容。该 API 是**纯增量扩展**——原版主页文件无需任何改动即可继续使用，行为完全一致。

实现见 `PCL.Core/App/Localization/HomepageLanguageApi.cs`，单元测试见
`PCL.Core.Test/HomepageLanguageApiTest.cs`。

## 兼容性保证

| 内容形态 | 行为 |
| --- | --- |
| 不以 `{` 开头的 XAML 片段（原版主页） | 原样交给 XAML 解析器，与上游 PCL 完全一致 |
| 不含任何 `lang:` 标签的可视化树 | 一个元素都不会被移除 |
| 以 `{` 开头但不是合法 JSON | 原样返回，由 XAML 解析器报出与上游相同的错误 |
| 是合法 JSON 但没有 `languages` / `default` 字段 | 原样返回，按普通 JSON 文本处理 |

> 也就是说：**任何能在原版 PCL 上工作的主页，在 PCL Lite 上的表现一模一样。**

## 机制一：语言信封（整页级）

当主页内容以 `{` 开头、且能被解析为包含 `languages` 或 `default` 字段的 JSON 对象时，
视为「语言信封」。启动器按当前 UI 语言取出对应分支的 XAML，其余分支被丢弃。

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

取值顺序：

1. `languages` 中与当前语言**完整代码**相同的分支（`zh-CN` 命中 `zh-CN`）
2. `languages` 中与当前语言**中性代码**相同的分支（`en` 命中 `en-US` 与 `en-GB`）
3. `default` 分支
4. 都没有 → 主页显示为空白，并记录警告

`format` 与 `version` 字段会被读取但不强制校验；判定是否为信封只看 `languages` / `default`。

## 机制二：`lang:` 标签（元素级）

XAML 中任意元素都可以带上语言标签，实例化后启动器只保留命中当前语言的分支：

```xml
<StackPanel>
    <TextBlock Tag="lang:zh-CN" Text="只在简体中文下显示" />
    <TextBlock Tag="lang:zh-TW" Text="只在繁體中文下顯示" />
    <TextBlock Tag="lang:en" Text="Shown for both en-US and en-GB" />
    <TextBlock Tag="lang:default" Text="任何语言都没有命中时的兜底" />
    <TextBlock Text="我没有 Tag，所有语言都显示" />
</StackPanel>
```

匹配规则：

- `lang:zh-CN` 精确匹配完整代码
- `lang:zh` / `lang:en` 等中性标签匹配同语言的所有区域（`lang:en` 同时覆盖 `en-US` 与 `en-GB`）
- `lang:default` 是兜底分支，仅在没有更精确的分支命中时保留
- 比较忽略大小写，`lang:ZH_cn` 同样有效

**按父容器生效**：只有当某个父容器下存在命中当前语言的分支（或 `default`）时，才会移除未命中的
分支；如果一个父容器下没有任何分支命中，则保留全部分支——主页不会因为漏翻而变成空白。

未被标签的元素永远不受影响。

## 两种机制可以叠加

信封决定「整页用哪份 XAML」，标签决定「这份 XAML 里哪些元素可见」：

```json
{
  "languages": {
    "zh-CN": "<StackPanel><TextBlock Tag=\"lang:zh-CN\" Text=\"简体\" /><TextBlock Tag=\"lang:en\" Text=\"English\" /></StackPanel>"
  }
}
```

## 在自定义主页中使用

把内容写进 `PCL/Custom.xaml`（本地文件）或填入「设置 → 个性化 → 主页来源 → 联网」的 URL 即可，
两种来源都走同一条加载管线。切换界面语言后主页会立即按新语言重新加载。

## 在内置页面中使用

欢迎页（`PageWelcome`）是这套协议的第一个内置示例：静态文案按 `lang:` 分分支存放，
加载时由 `HomepageLanguageApi.ApplyToTree` 过滤；卡片标题、按钮与状态行等需要随语言即时刷新的
部分沿用 `DynamicResource` / `Lang.Text`。

在自己编写的 WPF 页面中复用只需两步：

```csharp
// 1. 给分分支的元素打 Tag
var block = new TextBlock { Text = "English", Tag = "lang:en" };

// 2. 构建完控件树后按当前语言过滤（语言切换时重新构建并再次调用）
HomepageLanguageApi.ApplyToTree(this, LocalizationService.CurrentLanguage.Code);
```
