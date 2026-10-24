using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace PCL.Core.UI.Theme;

/// <summary>
/// 配色参数，对应原版 PCL2 的 <c>UiLauncherHue</c> / <c>UiLauncherSat</c> / <c>UiLauncherLight</c> / <c>UiLauncherDelta</c>。
/// </summary>
/// <param name="Hue">色相，0-360。</param>
/// <param name="Saturation">饱和度，0-100。</param>
/// <param name="LightAdjust">明度调整量，-20 - 20 为常用区间。</param>
/// <param name="TopbarDelta">标题栏渐变两端的色相偏移量，0 表示单色渐变。</param>
/// <param name="TopbarHueTriple">
/// 标题栏三个色标各自的色相偏移量。原版中有两个主题用它做非对称渐变
/// （青绿 <c>[5, -5, 5]</c>、星空蓝 <c>[-30, 10, 50]</c>），为 <see langword="null"/> 时退化为
/// <c>[-delta, 0, delta]</c>。
/// </param>
/// <param name="TopbarLightTriple">
/// 标题栏三个色标各自的绝对明度。原版玄素黑用 <c>[25, 15, 25]</c> 做中间更暗的效果，
/// 这组值是硬编码的，<b>不叠加</b> <paramref name="LightAdjust"/>；
/// 为 <see langword="null"/> 时使用标准的 <c>[48, 54, 48] + LightAdjust</c>。
/// </param>
/// <param name="TopbarWide">
/// 是否使用原版「欧皇彩」的四点宽渐变（<c>hue±21 / ±7</c>，明度 <c>53/47/47/53</c>）。
/// 开启时 <paramref name="TopbarHueTriple"/> 与 <paramref name="TopbarLightTriple"/> 都会被忽略。
/// </param>
public readonly record struct PclThemeArgs(
    int Hue, int Saturation, int LightAdjust, int TopbarDelta,
    int[]? TopbarHueTriple = null, int[]? TopbarLightTriple = null, bool TopbarWide = false);

/// <summary>
/// 原版 PCL2 配色引擎的忠实移植。
/// <para>
/// 算法来源：<c>PCL.ModSecret.ThemeRefresh</c> 与 <c>PCL.ModBase.MyColor.FromHSL2</c>。
/// 与原版保持一致的关键点：
/// </para>
/// <list type="bullet">
/// <item>先做 14 段色相的感知明度校正（<see cref="FromHsl2"/>），再交给标准 HSL 转 RGB；</item>
/// <item>八级色阶的明度固定为 25 / 45 / 55 / 65 / 80 / 91 / 95 / 97；</item>
/// <item>一级色阶饱和度压到 20%，因此深色底不会脏；七、八级趋近白色，用作背景与分隔线。</item>
/// </list>
/// </summary>
public static class PclThemeEngine
{
    /// <summary>
    /// 每 30 度一个采样点的明度校正系数，来自原版 <c>FromHSL2</c>。
    /// </summary>
    private static readonly double[] HueLightnessCorrection =
    [
        0.1, -0.06, -0.3, -0.19, -0.15, -0.24, -0.32, -0.09, 0.18, 0.05, -0.12, -0.02, 0.1, -0.06
    ];

    /// <summary>
    /// 原版 HSL 的单通道求值。
    /// </summary>
    private static double HueChannel(double v1, double v2, double vH)
    {
        if (vH < 0d) vH++;
        if (vH > 1d) vH--;
        if (vH < 0.16667d) return v1 + (v2 - v1) * 6d * vH;
        if (vH < 0.5d) return v2;
        if (vH < 0.66667d) return v1 + (v2 - v1) * (4d - vH * 6d);
        return v1;
    }

    /// <summary>
    /// 标准 HSL 转 RGB，各通道取值为 0-255。
    /// </summary>
    /// <param name="h">色相，0-360。</param>
    /// <param name="s">饱和度，0-100。</param>
    /// <param name="l">明度，0-100。</param>
    public static (double R, double G, double B) FromHsl(double h, double s, double l)
    {
        if (s <= 0d)
        {
            var gray = Math.Clamp(l, 0d, 100d) * 2.55d;
            return (gray, gray, gray);
        }

        var hn = h / 360d;
        var sn = s / 100d;
        var ln = l / 100d;
        var v2 = ln < 0.5d ? sn * ln + ln : sn * (1d - ln) + ln;
        var v1 = 2d * ln - v2;
        return (
            Math.Clamp(255d * HueChannel(v1, v2, hn + 1d / 3d), 0d, 255d),
            Math.Clamp(255d * HueChannel(v1, v2, hn), 0d, 255d),
            Math.Clamp(255d * HueChannel(v1, v2, hn - 1d / 3d), 0d, 255d));
    }

    /// <summary>
    /// 带感知明度校正的 HSL 转 RGB。校正后 <paramref name="l"/> 为 50 时视觉上接近中灰，
    /// 这正是原版各色相在相同明度下观感一致的原因。
    /// </summary>
    public static (double R, double G, double B) FromHsl2(double h, double s, double l)
    {
        if (s <= 0d) return FromHsl(h, 0d, l);

        var hue = (h + 3_600_000d) % 360d;
        var x = hue / 30d;
        var index = (int)Math.Floor(x);
        index = Math.Clamp(index, 0, HueLightnessCorrection.Length - 2);
        var mid = 50d - ((1d - x + index) * HueLightnessCorrection[index]
                         + (x - index) * HueLightnessCorrection[index + 1]) * s;
        mid = Math.Clamp(mid, 1d, 99d);
        var mapped = (l < mid ? l / mid : 1d + (l - mid) / (100d - mid)) * 50d;
        return FromHsl(hue, s, Math.Clamp(mapped, 0d, 100d));
    }

    /// <summary>
    /// sRGB 分量（0-255）转 scRGB 线性分量（0-1），用于 <see cref="CatColorResource"/>。
    /// </summary>
    private static double ToScRgb(double srgb)
    {
        var c = Math.Clamp(srgb, 0d, 255d) / 255d;
        return c <= 0.04045d ? c / 12.92d : Math.Pow((c + 0.055d) / 1.055d, 2.4d);
    }

    private static CatColorResource _Resource(
        (double R, double G, double B) rgb, string suffix, double alpha = 1d, bool applyToColor = true, bool applyToBrush = true)
        => new()
        {
            Red = (float)ToScRgb(rgb.R),
            Green = (float)ToScRgb(rgb.G),
            Blue = (float)ToScRgb(rgb.B),
            Alpha = (float)alpha,
            Suffix = suffix,
            ApplyToColor = applyToColor,
            ApplyToBrush = applyToBrush
        };

    /// <summary>
    /// 按给定参数生成整套彩色资源，返回的键与原版一致：1 - 8、Bg0、Bg1、SemiTransparent。
    /// </summary>
    /// <param name="args">配色参数。</param>
    /// <param name="isDarkMode">深色模式下明度整体翻转，七、八级随之变成近黑。</param>
    /// <param name="tone">当前灰度档位，用于合成 Bg0。</param>
    public static CatColorResource[] Generate(PclThemeArgs args, bool isDarkMode, ToneProfile tone)
    {
        var hue = (double)args.Hue;
        var sat = Math.Clamp((double)args.Saturation, 0d, 100d);
        var adjust = (double)args.LightAdjust;

        // 八级色阶的基准明度取自当前深浅模式的 ToneProfile：
        // 浅色是 35/50/57.5/65/80/92/94/96，深色是 96/75/60/65/45/25/22.5/20。
        // 不能简单按 100-l 翻转，也不能写死原版的 25/45/55/65/80/91/95/97 ——
        // 前者会把玄素黑的标题栏翻成浅灰，后者会让深色模式下的 ColorBrush1 变成深色、文字直接看不见。
        var baseLight = new[]
        {
            tone.L1, tone.L2, tone.L3, tone.L4, tone.L5, tone.L6, tone.L7, tone.L8
        };

        var levels = new (double R, double G, double B)[8];
        for (var i = 0; i < levels.Length; i++)
        {
            // 一级色阶压饱和度，与原版 ThemeRefresh 一致
            var levelSat = i == 0 ? sat * 0.2d : sat;
            var levelLight = baseLight[i] * 100d + i switch
            {
                0 => adjust * 0.3d,
                1 or 2 or 3 => adjust,
                4 => adjust * 0.4d,
                5 => adjust * 0.1d,
                _ => 0d
            };
            levels[i] = FromHsl2(hue, levelSat, Math.Clamp(levelLight, 0d, 100d));
        }

        // Bg0 = 4 级 * 0.4 + 5 级 * 0.4 + 灰 4 * 0.2
        var gray4 = FromHsl2(hue, 0d, Math.Clamp(tone.L4 * 100d, 0d, 100d));
        var bg0 = (
            levels[3].R * 0.4d + levels[4].R * 0.4d + gray4.R * 0.2d,
            levels[3].G * 0.4d + levels[4].G * 0.4d + gray4.G * 0.2d,
            levels[3].B * 0.4d + levels[4].B * 0.4d + gray4.B * 0.2d);

        var result = new CatColorResource[11];
        for (var i = 0; i < 8; i++) result[i] = _Resource(levels[i], (i + 1).ToString());
        result[8] = _Resource(bg0, "Bg0");
        result[9] = _Resource(levels[6], "Bg1", tone.ASemiWhite);
        result[10] = _Resource(levels[7], "SemiTransparent", tone.ASemiTransparent, false);
        return result;
    }

    /// <summary>
    /// 生成标题栏渐变的三个色标，返回 (偏移, 颜色) 序列。
    /// </summary>
    /// <param name="args">配色参数。</param>
    /// <param name="isDarkMode">深色模式下明度整体翻转。</param>
    public static (double Offset, (double R, double G, double B) Color)[] GenerateTopbar(PclThemeArgs args, bool isDarkMode)
    {
        var hue = (double)args.Hue;
        var sat = Math.Clamp((double)args.Saturation, 0d, 100d);
        var adjust = (double)args.LightAdjust;
        var delta = args.TopbarDelta;

        // 与原版一致：标题栏明度同样是固定值，不随深浅模式翻转
        double L(double value) => value;

        // 原版 12 号「欧皇彩」：四点宽渐变，色相跨 ±21，明度中间更低（这里会叠加明度调整量）
        if (args.TopbarWide)
            return
            [
                (0d, FromHsl2(hue - 21d, sat, L(53d + adjust))),
                (0.33d, FromHsl2(hue - 7d, sat, L(47d + adjust))),
                (0.67d, FromHsl2(hue + 7d, sat, L(47d + adjust))),
                (1d, FromHsl2(hue + 21d, sat, L(53d + adjust)))
            ];

        // 三个色标各自的色相偏移：优先用预设自带的非对称三元组
        var hueOffsets = args.TopbarHueTriple is { Length: 3 } custom
            ? custom
            : delta == 0
                ? [0, 0, 0]
                : [-delta, 0, delta];

        // 三个色标各自的明度：
        // 玄素黑用的 [25, 15, 25] 在原版里是硬编码的绝对值，不叠加明度调整量；
        // 其余主题是 48/54/48 再叠加调整量。
        if (args.TopbarLightTriple is { Length: 3 } lights)
            return
            [
                (0d, FromHsl2(hue + hueOffsets[0], sat, L(lights[0]))),
                (0.5d, FromHsl2(hue + hueOffsets[1], sat, L(lights[1]))),
                (1d, FromHsl2(hue + hueOffsets[2], sat, L(lights[2])))
            ];

        return
        [
            (0d, FromHsl2(hue + hueOffsets[0], sat, L(48d + adjust))),
            (0.5d, FromHsl2(hue + hueOffsets[1], sat, L(54d + adjust))),
            (1d, FromHsl2(hue + hueOffsets[2], sat, L(48d + adjust)))
        ];
    }

    /// <summary>
    /// 生成用于界面上实时预览的色卡（sRGB 直接可用）。
    /// </summary>
    public static Color[] GenerateSwatches(PclThemeArgs args, bool isDarkMode, ToneProfile tone)
    {
        var resources = Generate(args, isDarkMode, tone);
        var result = new Color[resources.Length];
        for (var i = 0; i < resources.Length; i++)
        {
            var r = resources[i];
            result[i] = Color.FromScRgb(1f, r.Red, r.Green, r.Blue);
        }

        return result;
    }

    /// <summary>
    /// 把生成结果直接转换为 <see cref="LinearGradientBrush"/>。
    /// </summary>
    public static LinearGradientBrush CreateTopbarBrush(PclThemeArgs args, bool isDarkMode)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0d, 0d),
            EndPoint = new System.Windows.Point(1d, 0d)
        };
        foreach (var (offset, color) in GenerateTopbar(args, isDarkMode))
            brush.GradientStops.Add(new GradientStop(
                Color.FromScRgb(1f, (float)ToScRgb(color.R), (float)ToScRgb(color.G), (float)ToScRgb(color.B)),
                offset));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 生成主窗口的背景渐变，对应原版 <c>ThemeRefreshMain</c> 里 <c>UiBackgroundColorful</c> 的分支：
    /// 三点 <c>hue-15 / hue / hue+15</c>，饱和度压到 80%，明度 91（深色模式反转）。
    /// </summary>
    public static LinearGradientBrush CreateBackgroundBrush(PclThemeArgs args, bool isDarkMode)
    {
        var hue = (double)args.Hue;
        var sat = Math.Clamp(args.Saturation * 0.8d, 0d, 100d);
        var light = isDarkMode ? 9d : 91d;

        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0.9d, 0d),
            EndPoint = new System.Windows.Point(0.1d, 1d)
        };
        foreach (var (offset, hueOffset) in new[] { (-0.1d, -15d), (0.4d, 0d), (1.1d, 15d) })
        {
            var (r, g, b) = FromHsl2(hue + hueOffset, sat, light);
            brush.GradientStops.Add(new GradientStop(
                Color.FromScRgb(1f, (float)ToScRgb(r), (float)ToScRgb(g), (float)ToScRgb(b)), offset));
        }

        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// 原版 PCL2 的一个预设配色。
/// </summary>
/// <param name="Index">原版主题编号，与 <c>UiLauncherTheme</c> 取值一致。</param>
/// <param name="Name">主题名，隐藏主题沿用原版叫法。</param>
/// <param name="Args">配色参数。</param>
/// <param name="IsRandom">是否为「欧皇彩」这类每次进入都随机换色的主题。</param>
public sealed record PclThemePreset(int Index, string Name, PclThemeArgs Args, bool IsRandom = false);

/// <summary>
/// 原版 PCL2 的预设配色表，逐项取自 <c>PCL.ModSecret.ThemeRefresh</c> 的 switch 分支。
/// <para>
/// 列表顺序与原版设置页的单选顺序一致：前 5 个是常规主题，其后 9 个是隐藏主题，
/// 最后一个是「自定义」（编号 14，由滑块与色卡接管，不在这里）。
/// </para>
/// <para>
/// 原版还有编号 42 的特殊主题「极客蓝」（225/60/-15），与编号 11 重名，这里不再单独列出。
/// </para>
/// </summary>
public static class PclThemePresets
{
    /// <summary>
    /// 常规主题，对应设置页「主题」那一行。
    /// </summary>
    public static readonly IReadOnlyList<PclThemePreset> Main =
    [
        new(0, "龙猫蓝", new PclThemeArgs(210, 85, 0, 0)),
        new(1, "甜柠青", new PclThemeArgs(180, 50, 0, 0)),
        new(2, "小草绿", new PclThemeArgs(130, 40, 0, 0, TopbarHueTriple: [5, -5, 5])),
        new(3, "菠萝黄", new PclThemeArgs(75, 50, 0, 0)),
        new(4, "橡木棕", new PclThemeArgs(35, 30, 0, 0))
    ];

    /// <summary>
    /// 隐藏主题，对应设置页「隐藏主题」那两行。原版需要逐个解锁，这里全部放开。
    /// </summary>
    public static readonly IReadOnlyList<PclThemePreset> Hidden =
    [
        new(5, "玄素黑", new PclThemeArgs(225, 10, -20, 0, TopbarLightTriple: [25, 15, 25])),
        new(13, "滑稽彩", new PclThemeArgs(290, 50, 0, 0, TopbarHueTriple: [-30, 10, 50])),
        new(6, "铁杆粉", new PclThemeArgs(345, 70, 15, 0)),
        new(7, "神秘紫", new PclThemeArgs(285, 50, 0, 0)),
        new(12, "欧皇彩", new PclThemeArgs(0, 45, 0, 0, TopbarWide: true), IsRandom: true),
        new(8, "秋仪金", new PclThemeArgs(50, 65, 10, 0)),
        new(9, "活跃橙", new PclThemeArgs(25, 75, 5, 0)),
        new(10, "跳票红", new PclThemeArgs(5, 70, -5, 0)),
        new(11, "极客蓝", new PclThemeArgs(230, 60, -5, 0))
    ];

    /// <summary>
    /// 全部预设，按界面显示顺序拼接。
    /// </summary>
    public static readonly IReadOnlyList<PclThemePreset> All = [.. Main, .. Hidden];

    /// <summary>
    /// 「自定义」对应的主题编号。
    /// </summary>
    public const int CustomIndex = 14;

    /// <summary>
    /// 按编号查找预设，找不到时返回 <see langword="null"/>。
    /// </summary>
    public static PclThemePreset? ByIndex(int index)
    {
        foreach (var preset in All)
            if (preset.Index == index)
                return preset;
        return null;
    }

    /// <summary>
    /// 按配色参数查找最接近的预设，用于高亮当前色卡。
    /// </summary>
    public static PclThemePreset? Match(PclThemeArgs args)
    {
        foreach (var preset in All)
        {
            if (preset.IsRandom) continue;
            var a = preset.Args;
            if (a.Hue == args.Hue && a.Saturation == args.Saturation
                && a.LightAdjust == args.LightAdjust && a.TopbarDelta == args.TopbarDelta)
                return preset;
        }

        return null;
    }
}
