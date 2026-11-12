using System.Windows;
using System.Windows.Controls;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI;

namespace PCL;

public partial class PageSetupUI
{
    private bool _liquidGlassBinding;

    private void RefreshLiquidGlassSettings()
    {
        _liquidGlassBinding = true;
        try
        {
            PanLiquidGlassSettings.Visibility = CheckTahoeGlass.Checked == true ? Visibility.Visible : Visibility.Collapsed;
            ComboLiquidGlassPreset.SelectedIndex = LiquidGlassPerformance.NormalizePreset(Config.Preference.TahoeGlass.PerformancePreset);
            SliderLiquidGlassFrameRate.getHintText = new Func<object, object>(value =>
                FrameRateLabel(LiquidGlassPerformance.FrameRateFromSlider((int)value)));
            SliderLiquidGlassFrameRate.Value = LiquidGlassPerformance.SliderFromFrameRate(Config.Preference.TahoeGlass.MaximumFrameRate);
            TextLiquidGlassFrameRate.Text = FrameRateLabel(Config.Preference.TahoeGlass.MaximumFrameRate);
            SliderLiquidGlassFrost.Value = LiquidGlassMaterial.FrostRadius(Config.Preference.TahoeGlass.FrostRadius);
            SliderLiquidGlassFrost.getHintText = new Func<object, object>(value => ((int)value) + " DIP");
            TextLiquidGlassFrost.Text = SliderLiquidGlassFrost.Value + " DIP";
            SliderLiquidGlassRefraction.Value = LiquidGlassMaterial.RefractionSetting(Config.Preference.TahoeGlass.RefractionStrength) - 5;
            SliderLiquidGlassRefraction.getHintText = new Func<object, object>(value => (((int)value) + 5) * 20 + "%");
            TextLiquidGlassRefraction.Text = (SliderLiquidGlassRefraction.Value + 5) * 20 + "%";
            TextLiquidGlassCost.Text = Lang.Text("Setup.Ui.LiquidGlass.Cost."
                + LiquidGlassPerformance.NormalizePreset(Config.Preference.TahoeGlass.PerformancePreset));
        }
        finally { _liquidGlassBinding = false; }
    }

    private static string FrameRateLabel(int fps) => fps == 0
        ? Lang.Text("Setup.Ui.LiquidGlass.Unlimited") : LiquidGlassPerformance.NormalizeFrameRate(fps) + " FPS";

    private void LiquidGlassFrost_Changed(object sender, bool user)
    {
        if (_liquidGlassBinding || ModAnimation.AniControlEnabled != 0) return;
        Config.Preference.TahoeGlass.FrostRadius = LiquidGlassMaterial.FrostRadius(SliderLiquidGlassFrost.Value);
        TextLiquidGlassFrost.Text = Config.Preference.TahoeGlass.FrostRadius + " DIP";
        LiquidGlassCore.Refresh();
    }

    private void LiquidGlassRefraction_Changed(object sender, bool user)
    {
        if (_liquidGlassBinding || ModAnimation.AniControlEnabled != 0) return;
        Config.Preference.TahoeGlass.RefractionStrength = LiquidGlassMaterial.RefractionSetting(SliderLiquidGlassRefraction.Value + 5);
        TextLiquidGlassRefraction.Text = Config.Preference.TahoeGlass.RefractionStrength * 20 + "%";
        LiquidGlassCore.Refresh();
    }

    private void LiquidGlassEnabled_Change(object sender, bool user)
    {
        if (_liquidGlassBinding || ModAnimation.AniControlEnabled != 0) return;
        if (CheckTahoeGlass.Checked == true)
        {
            var preset = LiquidGlassPerformance.NormalizePreset(Config.Preference.TahoeGlass.PerformancePreset);
            if (ModMain.MyMsgBox(Lang.Text("Setup.Ui.LiquidGlass.Confirm",
                    Lang.Text("Setup.Ui.LiquidGlass.Preset." + preset), FrameRateLabel(Config.Preference.TahoeGlass.MaximumFrameRate),
                    Lang.Text("Setup.Ui.LiquidGlass.Cost." + preset)),
                    Lang.Text("Setup.Ui.Tahoe.Title"), Lang.Text("Common.Action.Continue"),
                    Lang.Text("Common.Action.Cancel"), isWarn: true) != 1)
            {
                _liquidGlassBinding = true;
                try { CheckTahoeGlass.Checked = false; }
                finally { _liquidGlassBinding = false; }
                return;
            }
            Config.Preference.TahoeGlass.PerformanceConsent = true;
        }
        Config.Preference.TahoeGlass.IsEnabled = CheckTahoeGlass.Checked == true;
        RefreshLiquidGlassSettings();
        LiquidGlassCore.Refresh();
    }

    private void LiquidGlassDesktop_Change(object sender, bool user)
    {
        if (_liquidGlassBinding || ModAnimation.AniControlEnabled != 0) return;
        if (CheckTahoeDesktop.Checked == true)
        {
            if (ModMain.MyMsgBox(Lang.Text("Setup.Ui.LiquidGlass.DesktopConfirm"),
                    Lang.Text("Setup.Ui.Tahoe.Title"), Lang.Text("Common.Action.Continue"),
                    Lang.Text("Common.Action.Cancel"), isWarn: true) != 1)
            {
                _liquidGlassBinding = true;
                try { CheckTahoeDesktop.Checked = false; }
                finally { _liquidGlassBinding = false; }
                return;
            }
            Config.Preference.TahoeGlass.DesktopCaptureConsent = true;
        }
        Config.Preference.TahoeGlass.UseDesktopBackdrop = CheckTahoeDesktop.Checked == true;
        LiquidGlassCore.Refresh();
    }

    private void LiquidGlassPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_liquidGlassBinding || ModAnimation.AniControlEnabled != 0 || ComboLiquidGlassPreset.SelectedIndex < 0) return;
        var preset = LiquidGlassPerformance.NormalizePreset(ComboLiquidGlassPreset.SelectedIndex);
        Config.Preference.TahoeGlass.PerformancePreset = preset;
        Config.Preference.TahoeGlass.MaximumFrameRate = LiquidGlassPerformance.GetProfile(preset).FrameRate;
        RefreshLiquidGlassSettings();
        LiquidGlassCore.Refresh();
    }

    private void LiquidGlassFrameRate_Changed(object sender, bool user)
    {
        if (_liquidGlassBinding || ModAnimation.AniControlEnabled != 0) return;
        var fps = LiquidGlassPerformance.FrameRateFromSlider(SliderLiquidGlassFrameRate.Value);
        Config.Preference.TahoeGlass.MaximumFrameRate = fps;
        TextLiquidGlassFrameRate.Text = FrameRateLabel(fps);
    }
}
