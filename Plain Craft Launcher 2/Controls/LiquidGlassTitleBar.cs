using System.Windows;
using System.Windows.Media;

namespace PCL;

/// <summary>
/// Title-only presentation rule. Queued legacy theme animations may finish after
/// glass is enabled; their base brush remains available but must not cover the glass.
/// Other AnimatedBackgroundGrid controls and the animation engine are unchanged.
/// </summary>
public sealed class LiquidGlassTitleBar : AnimatedBackgroundGrid
{
    public static readonly DependencyProperty SuppressThemeBackgroundProperty =
        DependencyProperty.Register(nameof(SuppressThemeBackground), typeof(bool),
            typeof(LiquidGlassTitleBar), new PropertyMetadata(false,
                (element, _) => element.CoerceValue(BackgroundProperty)));

    static LiquidGlassTitleBar()
    {
        BackgroundProperty.OverrideMetadata(typeof(LiquidGlassTitleBar),
            new FrameworkPropertyMetadata(null, null,
                (element, brush) => ((LiquidGlassTitleBar)element).SuppressThemeBackground
                    ? Brushes.Transparent : brush));
    }

    public bool SuppressThemeBackground
    {
        get => (bool)GetValue(SuppressThemeBackgroundProperty);
        set => SetValue(SuppressThemeBackgroundProperty, value);
    }
}
