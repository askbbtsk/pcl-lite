# 主页语言 API

PCL Lite 的主页协议在 PCL 原版基础上增加了**语言协商**能力：同一份主页内容可以针对不同语言
展示不同内容。该 API 是**纯增量扩展**——原版主页文件无需任何改动即可继续使用，行为完全一致。

实现见 `PCL.Core/App/Localization/HomepageLanguageApi.cs`，单元测试见
`PCL.Core.Test/HomepageLanguageApiTest.cs`。

---

## 一、先看这个：怎么让主页在**原版 PCL** 上也不炸

这是最容易踩的坑。开发者按 PCL Lite 的 API 写好主页，直接丢给原版 PCL，会出现两种后果：

| 你用了什么 | 原版 PCL 的表现 | 危险程度 |
| --- | --- | --- |
| 只有 `Tag="lang:xx"` | 所有分支**一起显示**（内容重复，排版变丑） | 丑，但**不会崩** |
| 语言信封（`{ … }` JSON） | 把 JSON 当 XAML 解析，**直接解析失败** | **会崩** |

原因很简单：原版 PCL 拿到主页内容后直接丢给 XAML 解析器。`Tag` 是合法属性，它照收不误；
但 `{` 开头的 JSON 根本不是 XAML，解析器会抛异常。

### 结论一：要双端兼容，就别用语言信封

信封是「整页级」的，它的第一层就不是 XAML，原版 PCL 没有任何办法处理。
**双端兼容的主页只用 `lang:` 标签。**

### 结论二：非默认分支预先 `Collapsed`，两边都好看

只打 `lang:` 标签的话，原版 PCL 会把所有语言的分支堆在一起。加一行 `Visibility="Collapsed"`
就解决了——因为 PCL Lite 会把**命中当前语言的那一支重新设为可见**：

```xml
<StackPanel>
    <!--  英文分支：原版 PCL 看不到（Collapsed），PCL Lite 在英文环境下把它露出来  -->
    <TextBlock Tag="lang:en" Visibility="Collapsed" Text="Welcome to PCL Lite" />
    <!--  繁体分支：同理  -->
    <TextBlock Tag="lang:zh-TW" Visibility="Collapsed" Text="歡迎使用 PCL Lite" />
    <!--  默认分支：不写 Visibility，两边都显示  -->
    <TextBlock Tag="lang:default" Text="欢迎使用 PCL Lite" />
</StackPanel>
```

| 客户端 | 实际看到的内容 |
| --- | --- |
| 原版 PCL | 只看到 `lang:default`（其余被 `Collapsed` 挡住）→ **看起来完全正常** |
| PCL Lite（简体） | `lang:default`（没有 zh-CN 分支时兜底） |
| PCL Lite（英文） | `lang:en` 被重新设为可见，其余分支被移除 |

于是同一份文件在两个客户端上都对。这就是**双端兼容写法**：

> 每个非默认语言分支 = `Tag="lang:xx"` + `Visibility="Collapsed"`
> 兜底分支 = `Tag="lang:default"`，不写 `Visibility`

两个必须遵守的点：

1. **一定要写 `lang:default`**。原版 PCL 只显示默认分支，PCL Lite 在没有任何分支命中时也靠它兜底。
   没有默认分支 = 原版 PCL 上整块内容消失。
2. **不要在需要双端兼容的分支里用语言信封**。信封只适合「只给 PCL Lite 用」的主页。

---

## 二、兼容性保证（PCL Lite 侧）

| 内容形态 | 行为 |
| --- | --- |
| 不以 `{` 开头的 XAML 片段（原版主页） | 原样交给 XAML 解析器，与上游 PCL 完全一致 |
| 不含任何 `lang:` 标签的可视化树 | 一个元素都不会被移除 |
| 以 `{` 开头但不是合法 JSON | 原样返回，由 XAML 解析器报出与上游相同的错误 |
| 是合法 JSON 但没有 `languages` / `default` 字段 | 原样返回，按普通 JSON 文本处理 |

> 也就是说：**任何能在原版 PCL 上工作的主页，在 PCL Lite 上的表现一模一样。**

---

## 三、机制一：语言信封（整页级）

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

> **只给 PCL Lite 用。** 原版 PCL 会把它当 XAML 解析并失败，见第一节。

---

## 四、机制二：`lang:` 标签（元素级）

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

**命中分支会被设为可见**：如果命中的那一支写着 `Visibility="Collapsed"`，PCL Lite 会把它改回
`Visible`。这就是第一节的双端兼容写法能成立的原因。

未被打标签的元素永远不受影响。

---

## 五、两种机制可以叠加

信封决定「整页用哪份 XAML」，标签决定「这份 XAML 里哪些元素可见」：

```json
{
  "languages": {
    "zh-CN": "<StackPanel><TextBlock Tag=\"lang:zh-CN\" Text=\"简体\" /><TextBlock Tag=\"lang:en\" Text=\"English\" /></StackPanel>"
  }
}
```

叠加是允许的，但同样**只适合 PCL Lite 专用主页**。

---

## 六、API 参考

命名空间 `PCL.Core.App.Localization`，全部为静态成员。

### 常量

| 成员 | 值 | 说明 |
| --- | --- | --- |
| `LanguageTagPrefix` | `"lang:"` | 语言标签前缀 |
| `DefaultLanguageTag` | `"lang:default"` | 兜底分支标签 |

### `SelectContent`

```csharp
public static string SelectContent(string? rawContent, string languageCode)
```

从原始主页内容中选出当前语言对应的内容。返回值一定是可以直接交给 XAML 解析器的文本。

| 输入 | 返回 |
| --- | --- |
| 原版 XAML（不以 `{` 开头） | **原样返回**，一个字符都不动 |
| 语言信封 | 选中分支的 XAML 字符串 |
| 信封里没有匹配分支但有 `default` | `default` 的 XAML |
| 信封里两者都没有 | 空字符串 + 一条警告日志 |
| 以 `{` 开头但 JSON 非法 | 原样返回（交给解析器报上游同款错误） |

### `ApplyToTree`

```csharp
public static int ApplyToTree(DependencyObject? root, string languageCode)
public static int ApplyToTree(DependencyObject? root, string languageCode, out int revealed)
```

在**已实例化**的可视化树里过滤语言分支。

- 返回值：被移除的元素数量
- `revealed`：被从 `Collapsed` 改回 `Visible` 的元素数量（双端兼容写法会用到）
- `root` 为 `null` 时直接返回 0

支持遍历的容器：`Panel`（`Children`）、`Decorator`（`Child`）、`ContentControl`（`Content`）、
`ItemsControl`（`Items`）。

### 语言代码工具

| 方法 | 示例 |
| --- | --- |
| `NormalizeCode(string?)` | `zh_CN` → `zh-cn`，`EN-us` → `en-us` |
| `GetNeutralCode(string)` | `zh-CN` → `zh`，`en-US` → `en` |

### 标签判定

| 方法 | 说明 |
| --- | --- |
| `IsLanguageTag(object? tag)` | 是否以 `lang:` 开头 |
| `IsLanguageTagMatch(object? tag, string languageCode)` | 是否命中指定语言（含中性代码回退） |
| `IsDefaultLanguageTag(object? tag)` | 是否为 `lang:default` |

> 注意：这三个方法的参数是 **`Tag` 的值**，不是元素本身。
> 常见错误是写成 `children.Where(IsDefaultLanguageTag)`——把元素传进去，永远返回 `false`。
> 正确写法：`children.Where(c => IsDefaultLanguageTag(c.Tag))`。

---

## 七、在自定义主页中使用

把内容写进 `PCL/Custom.xaml`（本地文件）或填入「设置 → 个性化 → 主页来源 → 联网」的 URL 即可，
两种来源都走同一条加载管线。切换界面语言后主页会立即按新语言重新加载。

## 八、在内置页面中使用

欢迎页（`PageWelcome`）是这套协议的第一个内置示例：静态文案按 `lang:` 分分支存放，
加载时由 `HomepageLanguageApi.ApplyToTree` 过滤；卡片标题、按钮与状态行等需要随语言即时刷新的
部分沿用 `DynamicResource` / `Lang.Text`。

在自己编写的 WPF 页面中复用只需两步：

```csharp
// 1. 给分分支的元素打 Tag（双端兼容的话再加 Visibility = Collapsed）
var block = new TextBlock { Text = "English", Tag = "lang:en", Visibility = Visibility.Collapsed };

// 2. 构建完控件树后按当前语言过滤（语言切换时重新构建并再次调用）
HomepageLanguageApi.ApplyToTree(this, LocalizationService.CurrentLanguage.Code);
```

---

## 九、常见问题

**Q：主页在原版 PCL 上显示成一堆重复文字？**
只打了 `lang:` 标签、没写 `Visibility="Collapsed"`。按第一节的双端兼容写法改。

**Q：主页在原版 PCL 上直接报解析错误 / 白屏？**
用了语言信封。信封是 PCL Lite 专属，原版 PCL 无法解析。改回 `lang:` 标签方案。

**Q：切了语言但主页没变？**
自定义主页会在切换语言后重新加载；如果是自己写的页面，需要在语言变更回调里重新
`ApplyToTree`（可参考 `PageWelcome` 的 `WeakLanguageChanged.Add` 用法）。

**Q：某个容器下的内容全都不见了？**
该容器下所有分支都没命中当前语言，且没有 `lang:default` 兜底。补一个默认分支即可。
（注意：完全没有任何分支命中时会保留全部，不会空白；出现空白通常意味着命中了
`default` 之外的路径但内容为空，或信封里没有该语言也没有 `default`。）
