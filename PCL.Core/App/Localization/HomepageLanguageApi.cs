using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.Logging;

namespace PCL.Core.App.Localization;

/// <summary>
/// 主页语言 API：让同一份主页内容对不同语言展示不同内容。
/// <para>
/// 该 API 是对 PCL 原版主页协议的**纯增量扩展**——原版协议要求主页内容是一段可直接被
/// XAML 解析器实例化的 XAML 片段，本 API 不改变这一点：
/// </para>
/// <list type="bullet">
///     <item>内容不以 <c>{</c> 开头、且不含任何 <c>lang:</c> 标签时，行为与原版完全一致。</item>
///     <item>内容以 <c>{</c> 开头且能被解析为带 <c>languages</c> / <c>default</c>字段的 JSON 时，
///     视为「语言信封」，按当前语言取出对应分支的 XAML。</item>
///     <item>XAML 任意元素都可以带 <c>Tag="lang:xx"</c>（或 <c>lang:default</c>），
///     实例化后只保留命中当前语言的分支。</item>
/// </list>
/// <para>
/// 两种机制可以同时使用：信封决定「整页用哪份 XAML」，标签决定「这份 XAML 里哪些元素可见」。
/// </para>
/// </summary>
public static class HomepageLanguageApi
{
    /// <summary>语言标签前缀，例如 <c>Tag="lang:zh-CN"</c>。</summary>
    public const string LanguageTagPrefix = "lang:";

    /// <summary>默认分支标签，任何语言都会命中；仅在没有更精确的分支命中时保留。</summary>
    public const string DefaultLanguageTag = "lang:default";

    /// <summary>
    /// 归一化语言代码：统一小写、下划线转连字符、去掉多余空白。
    /// <c>zh_CN</c> → <c>zh-cn</c>，<c>EN-us</c> → <c>en-us</c>。
    /// </summary>
    public static string NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        return code.Trim().Replace('_', '-').ToLowerInvariant();
    }

    /// <summary>
    /// 取语言代码的中性部分：<c>zh-CN</c> → <c>zh</c>，<c>en-US</c> → <c>en</c>。
    /// 本身即为中性代码时原样返回。
    /// </summary>
    public static string GetNeutralCode(string code)
    {
        var normalized = NormalizeCode(code);
        var index = normalized.IndexOf('-');
        return index <= 0 ? normalized : normalized[..index];
    }

    /// <summary>
    /// 判断一个 <see cref="FrameworkElement.Tag"/> 是否是语言标签。
    /// </summary>
    public static bool IsLanguageTag(object? tag)
    {
        return tag is string text &&
               text.StartsWith(LanguageTagPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断语言标签是否命中指定语言。
    /// <para>
    /// 命中规则：先按完整代码精确比较（<c>lang:zh-cn</c> 命中 <c>zh-CN</c>），
    /// 再按中性代码比较（<c>lang:zh</c> 命中 <c>zh-CN</c> 与 <c>zh-TW</c>，
    /// <c>lang:en</c> 同时命中 <c>en-US</c> 与 <c>en-GB</c>）。
    /// </para>
    /// </summary>
    public static bool IsLanguageTagMatch(object? tag, string languageCode)
    {
        if (!IsLanguageTag(tag) || tag is not string text) return false;

        var expected = NormalizeCode(languageCode);
        if (expected.Length == 0) return false;

        var actual = NormalizeCode(text.Substring(LanguageTagPrefix.Length));
        if (actual.Length == 0) return false;

        return string.Equals(actual, expected, StringComparison.Ordinal) ||
               string.Equals(actual, GetNeutralCode(expected), StringComparison.Ordinal);
    }

    /// <summary>
    /// 判断语言标签是否为默认分支。
    /// </summary>
    public static bool IsDefaultLanguageTag(object? tag)
    {
        return tag is string text &&
               string.Equals(text.Trim(), DefaultLanguageTag, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从原始主页内容中选出当前语言对应的内容。
    /// <para>
    /// 返回值一定是可以直接交给 XAML 解析器的文本：
    /// 原版 XAML 原样返回，语言信封返回选中分支的 XAML。
    /// </para>
    /// </summary>
    /// <param name="rawContent">主页原始内容（本地文件或网络下载的文本）。</param>
    /// <param name="languageCode">当前 UI 语言代码，例如 <c>zh-CN</c>。</param>
    public static string SelectContent(string? rawContent, string languageCode)
    {
        if (string.IsNullOrWhiteSpace(rawContent)) return rawContent ?? string.Empty;

        // 原版协议的主页内容是 XAML，一定以 '<' 开头；只有以 '{' 开头才可能是语言信封。
        // 这样原版主页文件走的是完全相同的代码路径，没有任何额外开销与行为差异。
        var trimmed = rawContent.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return rawContent;

        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(rawContent,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            // 不是合法 JSON：按原版协议原样交给 XAML 解析器，错误表现与上游一致。
            return rawContent;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return rawContent;

            var hasLanguages = root.TryGetProperty("languages", out var languages) &&
                               languages.ValueKind == JsonValueKind.Object;
            var hasDefault = root.TryGetProperty("default", out var fallback) &&
                             fallback.ValueKind == JsonValueKind.String;

            // 既没有 languages 也没有 default，说明只是恰好以 { 开头的普通 JSON，不是语言信封。
            if (!hasLanguages && !hasDefault) return rawContent;

            var selected = hasLanguages ? _SelectFromLanguages(languages, languageCode) : null;
            if (selected is not null) return selected;

            if (hasDefault) return fallback.GetString() ?? string.Empty;

            LogWrapper.Warn("HomepageLanguage",
                $"主页语言信封中没有当前语言（{languageCode}）的分支，也没有 default 分支，主页将显示为空白");
            return string.Empty;
        }
    }

    private static string? _SelectFromLanguages(JsonElement languages, string languageCode)
    {
        var expected = NormalizeCode(languageCode);
        var neutral = GetNeutralCode(expected);

        // 先精确匹配完整代码，再退到中性代码，保证 en-US 与 en-GB 可以共用同一份 lang:en 分支。
        foreach (var property in languages.EnumerateObject())
        {
            if (string.Equals(NormalizeCode(property.Name), expected, StringComparison.Ordinal))
                return property.Value.GetString();
        }

        if (neutral.Length != 0)
        {
            foreach (var property in languages.EnumerateObject())
            {
                if (string.Equals(NormalizeCode(property.Name), neutral, StringComparison.Ordinal))
                    return property.Value.GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// 在已实例化的可视化树中过滤语言分支，返回被移除的元素数量。
    /// <para>
    /// 以「父容器」为单位处理：只有当某个父容器下存在命中当前语言的分支（或 default 分支）时，
    /// 才会移除未命中的分支；否则保留全部分支，避免主页因为漏翻而变成空白。
    /// 不含任何语言标签的子树完全不受影响。
    /// </para>
    /// </summary>
    /// <param name="root">可视化树根节点。</param>
    /// <param name="languageCode">当前 UI 语言代码，例如 <c>zh-CN</c>。</param>
    public static int ApplyToTree(DependencyObject? root, string languageCode)
    {
        if (root is null) return 0;
        var removed = 0;
        _ProcessChildren(root, languageCode, ref removed);
        return removed;
    }

    private static void _ProcessChildren(DependencyObject parent, string languageCode, ref int removed)
    {
        var children = _GetChildren(parent).OfType<FrameworkElement>().ToArray();
        if (children.Length == 0) return;

        var tagged = children.Where(child => IsLanguageTag(child.Tag)).ToArray();
        if (tagged.Length > 0)
        {
            var matched = tagged.Where(child => IsLanguageTagMatch(child.Tag, languageCode)).ToArray();
            var fallback = tagged.Where(child => IsDefaultLanguageTag(child.Tag)).ToArray();
            var winners = matched.Length > 0 ? matched : fallback;

            foreach (var child in tagged)
            {
                if (winners.Contains(child)) continue;
                if (winners.Length == 0)
                {
                    // 没有任何分支命中：保留全部，行为退化为原版协议。
                    continue;
                }

                if (_RemoveChild(parent, child)) removed++;
            }
        }

        foreach (var child in children)
        {
            if (!_IsStillAttached(parent, child)) continue;
            _ProcessChildren(child, languageCode, ref removed);
        }
    }

    private static IEnumerable<DependencyObject> _GetChildren(DependencyObject parent)
    {
        switch (parent)
        {
            case Panel panel:
                return panel.Children.OfType<DependencyObject>();
            case Decorator decorator:
                return decorator.Child is null ? [] : [decorator.Child];
            case ContentControl control when control.Content is DependencyObject content:
                return [content];
            case ItemsControl items:
                return items.Items.OfType<DependencyObject>();
            default:
                return [];
        }
    }

    private static bool _RemoveChild(DependencyObject parent, DependencyObject child)
    {
        switch (parent)
        {
            case Panel panel when child is UIElement element && panel.Children.Contains(element):
                panel.Children.Remove(element);
                return true;
            case Decorator decorator when ReferenceEquals(decorator.Child, child):
                decorator.Child = null;
                return true;
            case ContentControl control when ReferenceEquals(control.Content, child):
                control.Content = null;
                return true;
            default:
                return false;
        }
    }

    private static bool _IsStillAttached(DependencyObject parent, DependencyObject child)
    {
        switch (parent)
        {
            case Panel panel:
                return panel.Children.Contains(child as UIElement);
            case Decorator decorator:
                return ReferenceEquals(decorator.Child, child);
            case ContentControl control:
                return ReferenceEquals(control.Content, child);
            default:
                return true;
        }
    }
}
