using System;
using System.Windows;
using PCL.Core.App;

namespace PCL;

public partial class PageSetupUI
{
    private bool _updatingWallpaperEngine;

    private void WallpaperEngineSettingsLoaded(object sender, RoutedEventArgs e)
    {
        WallpaperEngineBackground.StatusChanged -= RefreshWallpaperEngineStatus;
        WallpaperEngineBackground.StatusChanged += RefreshWallpaperEngineStatus;
        _updatingWallpaperEngine = true;
        try
        {
            CheckWallpaperEngine.Checked = Config.Preference.Background.FollowWallpaperEngine;
            SliderWallpaperEngineQuality.Value = Math.Clamp(Config.Preference.Background.WallpaperEngineQuality, 25, 100) - 25;
            SliderWallpaperEngineQuality.getHintText = new Func<object, object>(value => ((int)value + 25) + "%");
        }
        finally { _updatingWallpaperEngine = false; }
        RefreshWallpaperEngineStatus();
    }

    private void WallpaperEngineSettingsUnloaded(object sender, RoutedEventArgs e)
        => WallpaperEngineBackground.StatusChanged -= RefreshWallpaperEngineStatus;

    private void RefreshWallpaperEngineStatus()
    {
        PanWallpaperEngine.Visibility = Config.Preference.Background.FollowWallpaperEngine ? Visibility.Visible : Visibility.Collapsed;
        TextWallpaperEngineStatus.Text = WallpaperEngineBackground.StatusText;
        CardBackground.TriggerForceResize();
    }

    private void WallpaperEngineChanged(object sender, bool user)
    {
        if (_updatingWallpaperEngine || ModAnimation.AniControlEnabled != 0) return;
        Config.Preference.Background.FollowWallpaperEngine = CheckWallpaperEngine.Checked == true;
        RefreshWallpaperEngineStatus();
    }

    private void WallpaperEngineQualityChanged(object sender, bool user)
    {
        if (_updatingWallpaperEngine || ModAnimation.AniControlEnabled != 0) return;
        Config.Preference.Background.WallpaperEngineQuality = SliderWallpaperEngineQuality.Value + 25;
    }
}
