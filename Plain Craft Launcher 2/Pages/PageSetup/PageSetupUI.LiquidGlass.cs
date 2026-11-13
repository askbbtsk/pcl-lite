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
            SliderLiquidGlassRefraction.Value = LiquidGlassCore.RefractionPercent;
            SliderLiquidGlassRefraction.getHintText = new Func<object, object>(value => value + "%");
            TextLiquidGlassRefraction.Text = SliderLiquidGlassRefraction.Value + "%";
            SliderLiquidGlassSampling.getHintText = new Func<object, object>(value =>
                FrameRateLabel(LiquidGlassPerformance.FrameRateFromSlider((int)value)));
            SliderLiquidGlassSampling.Value = LiquidGlassPerformance.SliderFromFrameRate(LiquidGlassCore.SamplingFrameRate);
            TextLiquidGlassSampling.Text = FrameRateLabel(LiquidGlassCore.SamplingFrameRate);
            BindGlassSlider(SliderLiquidGlassQuality, TextLiquidGlassQuality,
                Math.Clamp(Config.Preference.TahoeGlass.SamplingQuality, 25, 100) - 25, "%");
            SliderLiquidGlassQuality.getHintText = new Func<object, object>(value => ((int)value + 25) + "%");
            TextLiquidGlassQuality.Text = (SliderLiquidGlassQuality.Value + 25) + "%";
            var profile = LiquidGlassCore.Profile;
            BindGlassSlider(SliderLiquidGlassBlur, TextLiquidGlassBlur, (int)profile.BlurRadius, " DIP");
            BindGlassSlider(SliderLiquidGlassDispersion, TextLiquidGlassDispersion, (int)Math.Round(profile.Dispersion * 100), "%");
            BindGlassSlider(SliderLiquidGlassHighlight, TextLiquidGlassHighlight, (int)Math.Round(profile.Highlight * 100), "%");
            BindGlassSlider(SliderLiquidGlassDiffuse, TextLiquidGlassDiffuse, (int)Math.Round(LiquidGlassCore.DiffuseStrength * 100), "%");
            BindGlassSlider(SliderLiquidGlassSharpness, TextLiquidGlassSharpness, (int)Math.Round(LiquidGlassCore.EdgeSharpness * 100), "%");
            BindGlassSlider(SliderLiquidGlassTint, TextLiquidGlassTint, (int)Math.Round(LiquidGlassCore.TintScale * 100), "%");
            BindGlassSlider(SliderLiquidGlassCard, TextLiquidGlassCard,
                LiquidGlassMaterial.CardOpacityPercent(Config.Preference.TahoeGlass.CardOpacity, ThemeManager.IsDarkMode), "%");
            CheckLiquidGlassMotion.Checked = profile.Motion;
            CheckLiquidGlassEdgeAA.Checked = Config.Preference.TahoeGlass.EdgeAntiAliasing;
            ComboLiquidGlassEdgeAA.SelectedIndex = Math.Clamp(Config.Preference.TahoeGlass.EdgeAntiAliasingQuality, 0, 2);
            ComboLiquidGlassEdgeAA.IsEnabled = Config.Preference.TahoeGlass.EdgeAntiAliasing;
            BindBrightnessSlider(SliderLiquidGlassBrightness, TextLiquidGlassBrightness, Config.Preference.TahoeGlass.Brightness);
            BindBrightnessSlider(SliderLiquidGlassCardBrightness, TextLiquidGlassCardBrightness, Config.Preference.TahoeGlass.CardBrightness);
            TextLiquidGlassCost.Text = Lang.Text("Setup.Ui.LiquidGlass.Cost."
                + LiquidGlassPerformance.NormalizePreset(Config.Preference.TahoeGlass.PerformancePreset));
        }
        finally { _liquidGlassBinding = false; }
    }

    private static string FrameRateLabel(int fps) => fps == 0
        ? Lang.Text("Setup.Ui.LiquidGlass.Unlimited") : LiquidGlassPerformance.NormalizeFrameRate(fps) + " FPS";

    private static void BindGlassSlider(MySlider slider, TextBlock label, int value, string unit)
    {
        slider.getHintText = new Func<object, object>(raw => raw + unit);
        slider.Value = value;
        label.Text = value + unit;
    }

    private void UseCustomGlassSettings()
    {
        // Materialize only inherited values before dropping the preset identity.
        // Otherwise changing one parameter would silently reset the others to Balanced.
        var config = Config.Preference.TahoeGlass;
        var profile = LiquidGlassCore.Profile;
        if (config.SamplingFrameRate == -1) config.SamplingFrameRate = LiquidGlassCore.SamplingFrameRate;
        if (config.BlurRadius == -1) config.BlurRadius = (int)profile.BlurRadius;
        if (config.Dispersion == -1) config.Dispersion = (int)Math.Round(profile.Dispersion * 100);
        if (config.Highlight == -1) config.Highlight = (int)Math.Round(profile.Highlight * 100);
        if (config.Diffuse == -1) config.Diffuse = (int)Math.Round(LiquidGlassCore.DiffuseStrength * 100);
        if (config.EdgeSharpness == -1) config.EdgeSharpness = (int)Math.Round(LiquidGlassCore.EdgeSharpness * 100);
        if (config.Motion == -1) config.Motion = profile.Motion ? 1 : 0;
        config.PerformancePreset = LiquidGlassPerformance.CustomPreset;
        var previous = _liquidGlassBinding;
        _liquidGlassBinding = true;
        try { ComboLiquidGlassPreset.SelectedIndex = LiquidGlassPerformance.CustomPreset; }
        finally { _liquidGlassBinding = previous; }
        TextLiquidGlassCost.Text = Lang.Text("Setup.Ui.LiquidGlass.Cost.4");
    }

    private static void BindBrightnessSlider(MySlider slider, TextBlock label, int configured)
    {
        var value = Math.Clamp(configured, -50, 50);
        slider.getHintText = new Func<object, object>(raw => ((int)raw - 50).ToString("+0;-0;0"));
        slider.Value = value + 50;
        label.Text = value.ToString("+0;-0;0");
    }

    private void LiquidGlassFrost_Changed(object sender, bool user)
    {
        if (_liquidGlassBinding || ModAnimation.AniControlEnabled != 0) return;
        UseCustomGlassSettings();
        Config.Preference.TahoeGlass.FrostRadius = LiquidGlassMaterial.FrostRadius(SliderLiquidGlassFrost.Value);
        TextLiquidGlassFrost.Text = Config.Preference.TahoeGlass.FrostRadius + " DIP";
        LiquidGlassCore.Refresh();
    }

    private void LiquidGlassRefraction_Changed(object sender, bool user)
    {
        if (_liquidGlassBinding || ModAnimation.AniControlEnabled != 0) return;
        UseCustomGlassSettings();
        Config.Preference.TahoeGlass.RefractionPercent = Math.Clamp(SliderLiquidGlassRefraction.Value, 0, 200);
        TextLiquidGlassRefraction.Text = Config.Preference.TahoeGlass.RefractionPercent + "%";
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
        UseCustomGlassSettings();
        Config.Preference.TahoeGlass.UseDesktopBackdrop = CheckTahoeDesktop.Checked == true;
        LiquidGlassCore.Refresh();
    }

    private void LiquidGlassPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_liquidGlassBinding || ModAnimation.AniControlEnabled != 0 || ComboLiquidGlassPreset.SelectedIndex < 0) return;
        var preset = LiquidGlassPerformance.NormalizePreset(ComboLiquidGlassPreset.SelectedIndex);
        if (preset == LiquidGlassPerformance.CustomPreset)
        {
            UseCustomGlassSettings();
            return;
        }
        ApplyGlassRecommendation(preset);
    }

    private void ApplyGlassRecommendation(int preset)
    {
        var profile = LiquidGlassPerformance.GetProfile(preset);
        var config = Config.Preference.TahoeGlass;
        config.PerformancePreset = preset;
        config.MaximumFrameRate = profile.FrameRate;
        config.SamplingFrameRate = profile.FrameRate;
        config.SamplingQuality = preset switch { 0 => 50, 1 => 100, _ => 100 };
        config.BlurRadius = (int)profile.BlurRadius;
        config.FrostRadius = preset == 0 ? 0 : 8;
        config.RefractionStrength = 5;
        config.RefractionPercent = 100;
        config.Dispersion = (int)Math.Round(profile.Dispersion * 100);
        config.Highlight = (int)Math.Round(profile.Highlight * 100);
        config.Diffuse = ThemeManager.IsDarkMode ? 14 : 17;
        config.EdgeSharpness = 90;
        config.TintScale = 100;
        config.CardOpacity = -1;
        config.EdgeAntiAliasing = true;
        config.EdgeAntiAliasingQuality = 1;
        config.Brightness = config.CardBrightness = 0;
        config.Motion = profile.Motion ? 1 : 0;
        RefreshLiquidGlassSettings();
        LiquidGlassCore.Refresh();
    }

    private void LiquidGlassReset_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ModMain.MyMsgBox(Lang.Text("Setup.Ui.LiquidGlass.Reset.Confirm"),
                Lang.Text("Setup.Ui.LiquidGlass.Reset"), Lang.Text("Common.Action.Confirm"),
                Lang.Text("Common.Action.Cancel"), isWarn: true) != 1) return;
        ApplyGlassRecommendation(1);
    }

    private void LiquidGlassFrameRate_Changed(object sender, bool user)
    {
        if (_liquidGlassBinding || ModAnimation.AniControlEnabled != 0) return;
        UseCustomGlassSettings();
        var fps = LiquidGlassPerformance.FrameRateFromSlider(SliderLiquidGlassFrameRate.Value);
        Config.Preference.TahoeGlass.MaximumFrameRate = fps;
        TextLiquidGlassFrameRate.Text = FrameRateLabel(fps);
    }

    private void LiquidGlassSampling_Changed(object sender, bool user)
    {
        if (_liquidGlassBinding || ModAnimation.AniControlEnabled != 0) return;
        UseCustomGlassSettings();
        var fps = LiquidGlassPerformance.FrameRateFromSlider(SliderLiquidGlassSampling.Value);
        Config.Preference.TahoeGlass.SamplingFrameRate = fps;
        TextLiquidGlassSampling.Text = FrameRateLabel(fps);
    }

    private void LiquidGlassOptics_Changed(object sender, bool user)
    {
        if (_liquidGlassBinding || ModAnimation.AniControlEnabled != 0 || sender is not MySlider slider) return;
        UseCustomGlassSettings();
        var config = Config.Preference.TahoeGlass;
        switch (slider.Tag?.ToString())
        {
            case "Blur": config.BlurRadius = slider.Value; break;
            case "Dispersion": config.Dispersion = slider.Value; break;
            case "Highlight": config.Highlight = slider.Value; break;
            case "Diffuse": config.Diffuse = slider.Value; break;
            case "Sharpness": config.EdgeSharpness = slider.Value; break;
            case "Tint": config.TintScale = slider.Value; break;
            case "Card": config.CardOpacity = slider.Value; break;
            case "Quality": config.SamplingQuality = slider.Value + 25; break;
            case "Brightness": config.Brightness = slider.Value - 50; break;
            case "CardBrightness": config.CardBrightness = slider.Value - 50; break;
        }
        RefreshLiquidGlassSettings();
        LiquidGlassCore.Refresh();
    }

    private void LiquidGlassMotion_Changed(object sender, bool user)
    {
        if (_liquidGlassBinding || ModAnimation.AniControlEnabled != 0) return;
        UseCustomGlassSettings();
        Config.Preference.TahoeGlass.Motion = CheckLiquidGlassMotion.Checked == true ? 1 : 0;
        LiquidGlassCore.Refresh();
    }

    private void LiquidGlassEdgeAA_Changed(object sender, bool user)
    {
        if (_liquidGlassBinding || ModAnimation.AniControlEnabled != 0) return;
        UseCustomGlassSettings();
        Config.Preference.TahoeGlass.EdgeAntiAliasing = CheckLiquidGlassEdgeAA.Checked == true;
        ComboLiquidGlassEdgeAA.IsEnabled = Config.Preference.TahoeGlass.EdgeAntiAliasing;
        LiquidGlassCore.Refresh();
    }

    private void LiquidGlassEdgeAAQuality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_liquidGlassBinding || ModAnimation.AniControlEnabled != 0 || ComboLiquidGlassEdgeAA.SelectedIndex < 0) return;
        UseCustomGlassSettings();
        Config.Preference.TahoeGlass.EdgeAntiAliasingQuality = Math.Clamp(ComboLiquidGlassEdgeAA.SelectedIndex, 0, 2);
        LiquidGlassCore.Refresh();
    }
}
