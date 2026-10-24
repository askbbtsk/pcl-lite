using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCL.Core.App;
using PCL.Core.UI.Theme;

namespace PCL;

public static class ThemeManager
{
    private static bool _contextMenuHandlerRegistered;

    public static bool IsDarkMode => ThemeService.IsDarkMode;

    public static ResourceDictionary AppResources => System.Windows.Application.Current.Resources;

    public static ModBase.MyColor colorGray1 = new(AppResources["ColorObjectGray1"]);
    public static ModBase.MyColor colorGray4 = new(AppResources["ColorObjectGray4"]);
    public static ModBase.MyColor colorGray5 = new(AppResources["ColorObjectGray5"]);
    public static ModBase.MyColor colorSemiTransparent = new(AppResources["ColorBrushSemiTransparent"]);

    public static void ThemeRefresh(int newTheme = -1)
    {
        colorGray1 = new ModBase.MyColor(AppResources["ColorObjectGray1"]);
        colorGray4 = new ModBase.MyColor(AppResources["ColorObjectGray4"]);
        colorGray5 = new ModBase.MyColor(AppResources["ColorObjectGray5"]);
        colorSemiTransparent = new ModBase.MyColor(AppResources["ColorBrushSemiTransparent"]);
        ThemeRefreshMain();
    }

    public static void ThemeRefreshMain()
    {
        ModBase.RunInUi(() =>
        {
            if (!ModMain.frmMain.IsLoaded) return;
            RefreshTitleBar();
            RefreshBackground();
            RefreshAllContextMenuThemes();
        });
    }

    /// <summary>
    /// 刷新标题栏：渐变底色 + 原版纹理叠加。
    /// <para>
    /// 对应原版 <c>PCL.ModSecret.ThemeRefreshMain</c>：三点渐变写进 <c>PanTitle.Background</c>，
    /// 编号 5 以上再叠一张 <c>Themes/{n}.png</c> 纹理（编号 13 透明度 0.25，其余 0.5）。
    /// </para>
    /// </summary>
    private static void RefreshTitleBar()
    {
        var main = ModMain.frmMain;
        if (main?.ImgTitleGradient is null) return;

        if (ThemeService.GetCustomThemeArgs() is not { } args)
        {
            main.ImgTitleGradient.Fill = null;
            main.ImgTitle.Source = null;
            return;
        }

        main.ImgTitleGradient.Fill = PclThemeEngine.CreateTopbarBrush(args, IsDarkMode);

        var index = Config.Preference.Theme.ThemeSelected;
        var uri = new Uri($"{ModBase.pathImage}Themes/{index}.png", UriKind.Absolute);
        var hasTexture = index >= 5 && index != PclThemePresets.CustomIndex && _ResourceExists(uri);
        main.ImgTitle.Source = hasTexture ? uri.ToString() : null;
        main.ImgTitle.Opacity = index == 13 ? 0.25d : 0.5d;
        ModBase.Log($"[Theme] 标题栏渐变 {args}；纹理 {index}.png = {(hasTexture ? "已加载" : "无")}",
            ModBase.LogLevel.Developer);
    }

    private static bool _ResourceExists(Uri uri)
    {
        try
        {
            return System.Windows.Application.GetResourceStream(uri) is not null;
        }
        catch
        {
            return false;
        }
    }

    // 主页面背景
    private static void RefreshBackground()
    {
        if (Config.Preference.Background.BackgroundColorful)
        {
            if (ThemeService.GetCustomThemeArgs() is { } args)
            {
                // 原版 ThemeRefreshMain 的 UiBackgroundColorful 分支：hue±15、饱和度 80%、明度 91
                ModMain.frmMain.PanForm.Background = PclThemeEngine.CreateBackgroundBrush(args, IsDarkMode);
            }
            else
            {
                var brush = new LinearGradientBrush
                {
                    EndPoint = new Point(0.1, 1),
                    StartPoint = new Point(0.9, 0)
                };

                var hue = ThemeService.GetCurrentThemeArgs().Hue;
                var hue1 = hue - 15;
                var hue2 = hue + 15;
                var tone = ThemeService.CurrentTone;
                var darkLight = IsDarkMode ? 0.2d : 1d;
                brush.GradientStops.Add(new GradientStop
                    { Offset = -0.1d, Color = LabColor.FromLch(0.84d * darkLight, tone.C5, hue1) });
                brush.GradientStops.Add(new GradientStop
                    { Offset = 0.4d, Color = LabColor.FromLch(0.96d * darkLight, tone.C7, hue) });
                brush.GradientStops.Add(new GradientStop
                    { Offset = 1.1d, Color = LabColor.FromLch(0.84d * darkLight, tone.C5, hue2) });
                ModMain.frmMain.PanForm.Background = brush;
            }
        }
        else
        {
            ModMain.frmMain.PanForm.Background = (Brush)System.Windows.Application.Current.Resources["ColorBrushBackground"];
        }

        ModMain.frmMain.PanForm.Background.Freeze();
    }

    // 通用ContextMenu主题刷新
    private static void RefreshAllContextMenuThemes()
    {
        try
        {
            if (!_contextMenuHandlerRegistered)
            {
                EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
                    new RoutedEventHandler(OnContextMenuOpened));
                _contextMenuHandlerRegistered = true;
            }

            foreach (Window window in System.Windows.Application.Current.Windows)
                RefreshContextMenusInElement(window);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "刷新ContextMenu主题时出错");
        }
    }

    private static void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is ContextMenu contextMenu)
            {
                contextMenu.ClearValue(FrameworkElement.StyleProperty);
                contextMenu.UpdateDefaultStyle();
            }
        }
        catch
        {
            // 忽略个别错误
        }
    }

    private static void RefreshContextMenusInElement(DependencyObject element)
    {
        if (element is null)
            return;

        try
        {
            if (element is FrameworkElement { ContextMenu: not null } fe)
            {
                fe.ContextMenu.ClearValue(FrameworkElement.StyleProperty);
                fe.ContextMenu.UpdateDefaultStyle();
            }

            var childrenCount = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < childrenCount; i++)
                RefreshContextMenusInElement(VisualTreeHelper.GetChild(element, i));
        }
        catch
        {
            // 忽略个别元素的错误，继续处理其他元素
        }
    }
}