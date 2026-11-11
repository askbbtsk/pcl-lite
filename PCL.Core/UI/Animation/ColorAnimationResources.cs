using System;
using System.Windows;

namespace PCL.Core.UI.Animation;

public static class ColorAnimationResources
{
    /// <summary>Match DynamicResource: target, style/template, ancestors/window, then application resources.</summary>
    public static object Resolve(FrameworkElement target, string key)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(key);
        return target.FindResource(key);
    }
}
