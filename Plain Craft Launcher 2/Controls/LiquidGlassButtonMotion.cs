using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PCL;

/// <summary>
/// Native WPF adaptation of AndroidLiquidGlass LiquidButton and InteractiveHighlight.
/// The upstream spring uses dampingRatio = 0.5 and stiffness = 300 (Apache-2.0).
/// </summary>
internal sealed class TahoeButtonMotion(
    FrameworkElement owner,
    ScaleTransform scale,
    TranslateTransform translation,
    Border wash,
    Border highlight,
    Action refreshSampling) : IDisposable
{
    private readonly Spring _press = new();
    private readonly Spring _positionX = new();
    private readonly Spring _positionY = new();
    private readonly RadialGradientBrush _highlightBrush = CreateHighlightBrush();
    private Point _start;
    private bool _pressed;
    private bool _subscribed;
    private long _lastTimestamp;

    public void Press(Point position)
    {
        if (!LiquidGlassCore.Profile.Motion) { Reset(); return; }
        _pressed = true;
        _start = position;
        _positionX.Snap(position.X);
        _positionY.Snap(position.Y);
        _press.Target = 1;
        Animate();
    }

    public void Move(Point position)
    {
        if (!_pressed || !LiquidGlassCore.Profile.Motion) return;
        // Compose snaps the drag position to the pointer; only the return is sprung.
        _positionX.Snap(position.X);
        _positionY.Snap(position.Y);
        Apply();
    }

    public void Release()
    {
        _pressed = false;
        _press.Target = 0;
        _positionX.Target = _start.X;
        _positionY.Target = _start.Y;
        Animate();
    }

    public void Reset()
    {
        Unsubscribe();
        _pressed = false;
        _press.Snap(0);
        _positionX.Snap(_start.X);
        _positionY.Snap(_start.Y);
        scale.ScaleX = scale.ScaleY = 1;
        translation.X = translation.Y = 0;
        wash.Opacity = highlight.Opacity = 0;
        refreshSampling();
    }

    public void Dispose() => Reset();

    private void Animate()
    {
        if (!owner.IsLoaded || !SystemParameters.ClientAreaAnimation || !LiquidGlassCore.Profile.Motion)
        {
            _press.Snap(_press.Target);
            _positionX.Snap(_positionX.Target);
            _positionY.Snap(_positionY.Target);
            Unsubscribe();
            Apply();
            return;
        }
        if (_subscribed) return;
        _lastTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
        _subscribed = true;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!owner.IsLoaded || !owner.IsVisible)
        {
            Reset();
            return;
        }
        if (!SystemParameters.ClientAreaAnimation || !LiquidGlassCore.Profile.Motion)
        {
            Animate();
            return;
        }
        var timestamp = Stopwatch.GetTimestamp();
        if (!PCL.Core.UI.LiquidGlassPerformance.ShouldRender(timestamp - _lastTimestamp, Stopwatch.Frequency,
                PCL.Core.App.Config.Preference.TahoeGlass.MaximumFrameRate)) return;
        var elapsed = Math.Clamp((timestamp - _lastTimestamp) / (double)Stopwatch.Frequency, 0, 0.1);
        _lastTimestamp = timestamp;
        _press.Step(elapsed);
        _positionX.Step(elapsed);
        _positionY.Step(elapsed);
        Apply();
        if (_press.IsAtRest && _positionX.IsAtRest && _positionY.IsAtRest)
            Unsubscribe();
    }

    private void Apply()
    {
        var width = Math.Max(owner.ActualWidth, 1);
        var height = Math.Max(owner.ActualHeight, 1);
        var minimum = Math.Min(width, height);
        var maximum = Math.Max(width, height);
        var x = _positionX.Value - _start.X;
        var y = _positionY.Value - _start.Y;
        var progress = _press.Value;
        var reduceMotion = !SystemParameters.ClientAreaAnimation;
        var baseScale = 1 + 4 / height * progress;
        var angle = Math.Atan2(y, x);
        var dragScale = 4 / height;

        scale.ScaleX = reduceMotion ? 1 : baseScale + dragScale * Math.Abs(Math.Cos(angle) * x / maximum) * Math.Min(width / height, 1);
        scale.ScaleY = reduceMotion ? 1 : baseScale + dragScale * Math.Abs(Math.Sin(angle) * y / maximum) * Math.Min(height / width, 1);
        translation.X = reduceMotion ? 0 : minimum * Math.Tanh(0.05 * x / minimum);
        translation.Y = reduceMotion ? 0 : minimum * Math.Tanh(0.05 * y / minimum);

        // Keep foreground text outside these layers; it is never blurred or shaded.
        wash.Opacity = Math.Clamp(0.08 * progress, 0, 1);
        highlight.Opacity = Math.Clamp(0.15 * progress, 0, 1);
        _highlightBrush.Center = _highlightBrush.GradientOrigin = new Point(
            Math.Clamp(_positionX.Value, 0, width), Math.Clamp(_positionY.Value, 0, height));
        _highlightBrush.RadiusX = _highlightBrush.RadiusY = minimum * 1.5;
        highlight.Background = _highlightBrush;
        refreshSampling();
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        CompositionTarget.Rendering -= OnRendering;
        _subscribed = false;
    }

    private static RadialGradientBrush CreateHighlightBrush()
    {
        var brush = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute };
        // Piecewise samples of smoothstep(radius, radius * 0.5, distance).
        for (var i = 0; i <= 8; i++)
        {
            var distance = i / 8d;
            var t = Math.Clamp((1 - distance) * 2, 0, 1);
            var intensity = t * t * (3 - 2 * t);
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(255 * intensity), 255, 255, 255), distance));
        }
        return brush;
    }

    private sealed class Spring
    {
        public double Value { get; private set; }
        public double Velocity { get; private set; }
        public double Target { get; set; }
        public bool IsAtRest => Math.Abs(Value - Target) < 0.001 && Math.Abs(Velocity) < 0.001;

        public void Snap(double value)
        {
            Value = Target = value;
            Velocity = 0;
        }

        public void Step(double elapsed)
        {
            const double dampingRatio = 0.5;
            var angularFrequency = Math.Sqrt(300);
            var decayRate = dampingRatio * angularFrequency;
            var dampedFrequency = angularFrequency * Math.Sqrt(1 - dampingRatio * dampingRatio);
            var displacement = Value - Target;
            var sineWeight = (Velocity + decayRate * displacement) / dampedFrequency;
            var decay = Math.Exp(-decayRate * elapsed);
            var cosine = Math.Cos(dampedFrequency * elapsed);
            var sine = Math.Sin(dampedFrequency * elapsed);
            Value = Target + decay * (displacement * cosine + sineWeight * sine);
            Velocity = decay * ((-decayRate * displacement + sineWeight * dampedFrequency) * cosine +
                                (-decayRate * sineWeight - displacement * dampedFrequency) * sine);
            if (IsAtRest) Snap(Target);
        }
    }
}
