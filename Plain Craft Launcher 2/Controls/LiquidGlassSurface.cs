using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PCL;

/// <summary>
/// A background-only, native WPF glass layer. Put labels and interactive controls above it.
/// BackdropSource must be an independent visual in the same presentation source.
/// </summary>
public sealed class TahoeGlassSurface : Grid
{
    public static readonly DependencyProperty BackdropSourceProperty = DependencyProperty.Register(
        nameof(BackdropSource), typeof(FrameworkElement), typeof(TahoeGlassSurface), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty CornerRadiusProperty = Numeric(nameof(CornerRadius), 24, 0, 4096);
    public static readonly DependencyProperty RefractionStrengthProperty = Numeric(nameof(RefractionStrength), .05, 0, .15);
    public static readonly DependencyProperty BlurRadiusProperty = Numeric(nameof(BlurRadius), 2, 0, 32);
    public static readonly DependencyProperty FrostRadiusProperty = Numeric(nameof(FrostRadius), 0, 0, 24);
    public static readonly DependencyProperty TintOpacityProperty = Numeric(nameof(TintOpacity), .10, 0, 1);
    public static readonly DependencyProperty HighlightStrengthProperty = Numeric(nameof(HighlightStrength), .65, 0, 1);
    public static readonly DependencyProperty DispersionProperty = Numeric(nameof(Dispersion), .20, 0, 1);
    public static readonly DependencyProperty DiffuseStrengthProperty = Numeric(nameof(DiffuseStrength), .16, 0, .5);
    public static readonly DependencyProperty EdgeSharpnessProperty = Numeric(nameof(EdgeSharpness), .84, 0, 1);
    public static readonly DependencyProperty TintColorProperty = DependencyProperty.Register(
        nameof(TintColor), typeof(Color), typeof(TahoeGlassSurface), new PropertyMetadata(Colors.White, Changed));
    public static readonly DependencyProperty IsDarkModeProperty = DependencyProperty.Register(
        nameof(IsDarkMode), typeof(bool), typeof(TahoeGlassSurface), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty ForceCompatibilityProperty = DependencyProperty.Register(
        nameof(ForceCompatibility), typeof(bool), typeof(TahoeGlassSurface), new PropertyMetadata(false, Changed));
    private static readonly DependencyPropertyKey IsRefractionActivePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsRefractionActive), typeof(bool), typeof(TahoeGlassSurface), new PropertyMetadata(false));
    public static readonly DependencyProperty IsRefractionActiveProperty = IsRefractionActivePropertyKey.DependencyProperty;

    private static readonly DependencyPropertyDescriptor RenderTransformDescriptor =
        DependencyPropertyDescriptor.FromProperty(RenderTransformProperty, typeof(UIElement));
    private static readonly DependencyPropertyDescriptor LayoutTransformDescriptor =
        DependencyPropertyDescriptor.FromProperty(LayoutTransformProperty, typeof(FrameworkElement));

    private readonly Border _shadow = new()
    {
        Background = Brushes.Black,
        Opacity = .12,
        Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = .5, RenderingBias = RenderingBias.Performance }
    };
    private readonly Grid _clippedMaterial = new();
    private readonly Rectangle _surface = new();
    private readonly Border _tint = new();
    private readonly Border _rim = new() { BorderThickness = new Thickness(.55) };
    private readonly List<UIElement> _observedElements = [];
    private readonly List<DependencyObject?> _observedParents = [];
    private readonly HashSet<Transform> _observedTransforms = [];
    private FrameworkElement? _subscribedSource;
    private TahoeGlassEffect? _shader;
    private VisualBrush? _backdrop, _compatibilityBackdrop;
    private VisualBrush? _frostInput, _frostOutput;
    private Rectangle? _frostSource;
    private Matrix _lastMatrix;
    private Size _lastSourceSize, _lastPanelSize;
    private DpiScale _lastDpi;
    private bool _dirty = true, _tracking, _lastSourceValid, _hasSamplingGeometry;
    private bool _chromeDarkMode, _chromeShaderActive;
    private string? _shaderFailure;
    private Size _chromeSize;
    private DispatcherOperation? _queuedRefresh;
    private double _chromeRadius = -1, _chromeTintOpacity = -1;
    private Color _chromeTintColor;

    public TahoeGlassSurface()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Children.Add(_shadow);
        _clippedMaterial.Children.Add(_surface);
        _clippedMaterial.Children.Add(_tint);
        Children.Add(_clippedMaterial);
        Children.Add(_rim);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        IsVisibleChanged += OnVisibilityChanged;
    }

    public FrameworkElement? BackdropSource { get => (FrameworkElement?)GetValue(BackdropSourceProperty); set => SetValue(BackdropSourceProperty, value); }
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    /// <summary>Full-source UV coefficient f from the video. The default is 0.05.</summary>
    public double RefractionStrength { get => (double)GetValue(RefractionStrengthProperty); set => SetValue(RefractionStrengthProperty, value); }
    public double BlurRadius { get => (double)GetValue(BlurRadiusProperty); set => SetValue(BlurRadiusProperty, value); }
    public double FrostRadius { get => (double)GetValue(FrostRadiusProperty); set => SetValue(FrostRadiusProperty, value); }
    public Color TintColor { get => (Color)GetValue(TintColorProperty); set => SetValue(TintColorProperty, value); }
    public double TintOpacity { get => (double)GetValue(TintOpacityProperty); set => SetValue(TintOpacityProperty, value); }
    public double HighlightStrength { get => (double)GetValue(HighlightStrengthProperty); set => SetValue(HighlightStrengthProperty, value); }
    public double Dispersion { get => (double)GetValue(DispersionProperty); set => SetValue(DispersionProperty, value); }
    public double DiffuseStrength { get => (double)GetValue(DiffuseStrengthProperty); set => SetValue(DiffuseStrengthProperty, value); }
    public double EdgeSharpness { get => (double)GetValue(EdgeSharpnessProperty); set => SetValue(EdgeSharpnessProperty, value); }
    public bool IsDarkMode { get => (bool)GetValue(IsDarkModeProperty); set => SetValue(IsDarkModeProperty, value); }
    public bool ForceCompatibility { get => (bool)GetValue(ForceCompatibilityProperty); set => SetValue(ForceCompatibilityProperty, value); }
    public bool IsRefractionActive => (bool)GetValue(IsRefractionActiveProperty);
    public string RenderStatus => IsRefractionActive ? "Refraction" : _shaderFailure ?? (_lastSourceValid ? "Compatibility" : "Static");

    private static DependencyProperty Numeric(string name, double initial, double minimum, double maximum) =>
        DependencyProperty.Register(name, typeof(double), typeof(TahoeGlassSurface),
            new FrameworkPropertyMetadata(initial, Changed,
                (_, value) => TahoeGlassMath.FiniteClamp((double)value, minimum, maximum, initial)));

    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (TahoeGlassSurface)d;
        surface._dirty = true;
        if (e.Property == BackdropSourceProperty && surface._tracking) surface.SubscribeSource();
        surface.RefreshSampling();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_tracking || !IsVisible) return;
        _tracking = true;
        LayoutUpdated += OnLayoutUpdated;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        RenderCapability.TierChanged += OnRenderTierChanged;
        SubscribeSource();
        _dirty = true;
        RefreshSampling();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _queuedRefresh?.Abort();
        _queuedRefresh = null;
        _tracking = false;
        LayoutUpdated -= OnLayoutUpdated;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        RenderCapability.TierChanged -= OnRenderTierChanged;
        DetachSource();
        DetachTransforms();
        ReleaseBrushes();
        _surface.Fill = null;
        _surface.Effect = null;
        _lastSourceValid = false;
        _hasSamplingGeometry = false;
        SetValue(IsRefractionActivePropertyKey, false);
        _dirty = true;
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (IsVisible) OnLoaded(this, new RoutedEventArgs());
        else OnUnloaded(this, new RoutedEventArgs());
    }


    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.HighContrast)) return;
        _dirty = true;
        RefreshSampling();
    }

    private void OnRenderTierChanged(object? sender, EventArgs e)
    {
        _dirty = true;
        RefreshSampling();
    }

    private void SubscribeSource()
    {
        DetachSource();
        ReleaseBrushes();
        _subscribedSource = BackdropSource;
        if (_subscribedSource != null)
        {
            _subscribedSource.SizeChanged += OnSizeChanged;
            _subscribedSource.Loaded += OnSourceLoadChanged;
            _subscribedSource.Unloaded += OnSourceLoadChanged;
        }
        RebuildTransformObservers();
    }

    private void DetachSource()
    {
        if (_subscribedSource == null) return;
        _subscribedSource.SizeChanged -= OnSizeChanged;
        _subscribedSource.Loaded -= OnSourceLoadChanged;
        _subscribedSource.Unloaded -= OnSourceLoadChanged;
        _subscribedSource = null;
    }

    private void OnSourceLoadChanged(object sender, RoutedEventArgs e)
    {
        _dirty = true;
        RebuildTransformObservers();
        RefreshSampling();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) { _dirty = true; RefreshSampling(); }
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_tracking) return;
        // LayoutUpdated is global and can fire for unrelated UI. Ancestor/transform
        // collections only need rebuilding when a parent or transform is replaced.
        for (int i = 0; i < _observedElements.Count; i++)
        {
            if (VisualTreeHelper.GetParent(_observedElements[i]) == _observedParents[i]) continue;
            RebuildTransformObservers();
            break;
        }
        RefreshSampling();
    }

    private static IEnumerable<UIElement> Ancestors(DependencyObject? visual)
    {
        while (visual is Visual)
        {
            if (visual is UIElement element) yield return element;
            visual = VisualTreeHelper.GetParent(visual);
        }
    }

    private void RebuildTransformObservers()
    {
        if (!_tracking) return;
        var elements = Ancestors(this).Concat(Ancestors(BackdropSource)).Distinct().ToList();
        if (!_observedElements.SequenceEqual(elements))
        {
            DetachTransforms();
            foreach (UIElement element in elements)
            {
                RenderTransformDescriptor.AddValueChanged(element, OnTransformPropertyChanged);
                if (element is FrameworkElement) LayoutTransformDescriptor.AddValueChanged(element, OnTransformPropertyChanged);
                _observedElements.Add(element);
                _observedParents.Add(VisualTreeHelper.GetParent(element));
            }
        }
        var transforms = new HashSet<Transform>();
        foreach (UIElement element in elements)
        {
            if (element.RenderTransform is { IsFrozen: false } render) transforms.Add(render);
            if (element is FrameworkElement { LayoutTransform.IsFrozen: false } framework) transforms.Add(framework.LayoutTransform);
        }
        if (_observedTransforms.SetEquals(transforms)) return;
        foreach (Transform transform in _observedTransforms) transform.Changed -= OnTransformChanged;
        _observedTransforms.Clear();
        foreach (Transform transform in transforms)
        {
            transform.Changed += OnTransformChanged;
            _observedTransforms.Add(transform);
        }
    }

    private void OnTransformPropertyChanged(object? sender, EventArgs e)
    {
        RebuildTransformObservers();
        RefreshSampling();
    }
    private void OnTransformChanged(object? sender, EventArgs e) => RequestRefreshSampling();

    internal void RequestRefreshSampling()
    {
        if (!_tracking || Dispatcher.HasShutdownStarted || _queuedRefresh?.Status == DispatcherOperationStatus.Pending) return;
        _queuedRefresh = Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _queuedRefresh = null;
            RefreshSampling();
        });
    }

    private void DetachTransforms()
    {
        foreach (UIElement element in _observedElements)
        {
            RenderTransformDescriptor.RemoveValueChanged(element, OnTransformPropertyChanged);
            if (element is FrameworkElement) LayoutTransformDescriptor.RemoveValueChanged(element, OnTransformPropertyChanged);
        }
        _observedElements.Clear();
        _observedParents.Clear();
        foreach (Transform transform in _observedTransforms) transform.Changed -= OnTransformChanged;
        _observedTransforms.Clear();
    }

    private bool HasValidSource() => BackdropSource is { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } source &&
        source != this && !source.IsAncestorOf(this) && !IsAncestorOf(source) &&
        PresentationSource.FromVisual(this) is { } presentation && presentation == PresentationSource.FromVisual(source);

    /// <summary>Refresh after an external layout or animation change. Unchanged geometry performs no shader writes.</summary>
    public void RefreshSampling()
    {
        if (!IsLoaded || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0) return;
        Size size = RenderSize;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double radius = TahoeGlassMath.NormalizeRadius(CornerRadius, size);
        Matrix matrix = Matrix.Identity;
        bool valid = HasValidSource() && TahoeGlassMath.TryGetMatrix(this, BackdropSource!, out matrix);
        if (!valid)
        {
            _hasSamplingGeometry = false;
            if (_dirty || _lastSourceValid || size != _lastPanelSize) SetStaticMaterial(size, radius);
            return;
        }
        Size sourceSize = BackdropSource!.RenderSize;
        // A valid transform can have an EMPTY crop (e.g. a loaded button scrolled
        // beyond the source). Cache that result too: requiring a valid sample here
        // would recreate its fallback brush on every LayoutUpdated and queue another
        // render forever, preventing Dispatcher.ContextIdle from being reached.
        if (!_dirty && _hasSamplingGeometry && matrix == _lastMatrix && sourceSize == _lastSourceSize &&
            size == _lastPanelSize && dpi.Equals(_lastDpi)) return;
        _hasSamplingGeometry = true;
        _lastMatrix = matrix;
        _lastSourceSize = sourceSize;
        _lastPanelSize = size;
        _lastDpi = dpi;
        _lastSourceValid = true;
        UpdateChrome(size, radius);

        Rect crop = TahoeGlassMath.SamplingBounds(matrix, size, sourceSize, RefractionStrength, Math.Max(BlurRadius, FrostRadius));
        if (crop.IsEmpty || crop.Width < .001 || crop.Height < .001)
        {
            SetStaticMaterial(size, radius);
            return;
        }

        bool useShader = !ForceCompatibility && !SystemParameters.HighContrast &&
            RenderCapability.IsPixelShaderVersionSupported(3, 0) && _shaderFailure == null;
        if (useShader && _shader == null)
        {
            try { _shader = new TahoeGlassEffect(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _shaderFailure = "Shader unavailable: " + ex.GetType().Name;
                useShader = false;
            }
        }

        if (useShader)
        {
            if (_compatibilityBackdrop != null) _compatibilityBackdrop.Visual = null;
            _compatibilityBackdrop = null;
            _backdrop ??= new VisualBrush(BackdropSource)
            {
                ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill, TileMode = TileMode.None
            };
            _backdrop.Visual = BackdropSource;
            _backdrop.Viewbox = crop;

            if (FrostRadius > 0)
            {
                // Blur a clean padded crop in _frostSource (s1) while keeping Backdrop (s0) crisp
                // for sharp curved-bevel optical lensing and smooth frosted interior diffusion.
                _frostInput ??= new VisualBrush(BackdropSource)
                { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill, TileMode = TileMode.None };
                _frostInput.Visual = BackdropSource;
                _frostInput.Viewbox = crop;
                _frostSource ??= new Rectangle();
                _frostSource.Width = crop.Width;
                _frostSource.Height = crop.Height;
                _frostSource.Fill = _frostInput;
                if (_frostSource.Effect is not BlurEffect blur || blur.Radius != FrostRadius)
                    _frostSource.Effect = new BlurEffect { Radius = FrostRadius,
                        KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
                _frostSource.Measure(crop.Size);
                _frostSource.Arrange(new Rect(crop.Size));
                _frostOutput ??= new VisualBrush(_frostSource)
                { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill, TileMode = TileMode.None };
                _frostOutput.Visual = _frostSource;
                _frostOutput.Viewbox = new Rect(crop.Size);
                _shader!.FrostedBackdrop = _frostOutput;
            }
            else
            {
                ReleaseFrost();
                _shader!.FrostedBackdrop = _backdrop;
            }
            _shader!.Backdrop = _backdrop;
            _shader.Size = new Point4D(size.Width, size.Height, radius, 1 / Math.Max(dpi.DpiScaleX, dpi.DpiScaleY));
            _shader.Origin = new Point4D((matrix.OffsetX - crop.X) / crop.Width, (matrix.OffsetY - crop.Y) / crop.Height,
                matrix.M11 / crop.Width, matrix.M12 / crop.Height);
            _shader.Axis = new Point4D(matrix.M21 / crop.Width, matrix.M22 / crop.Height, crop.Width, crop.Height);
            _shader.Source = new Point4D(sourceSize.Width / crop.Width, sourceSize.Height / crop.Height, RefractionStrength, BlurRadius);
            // At zero refraction, chromatic taps must not displace the background either.
            _shader.Optics = new Point4D(HighlightStrength, RefractionStrength > 0 ? Dispersion : 0, -.55, -.83);
            _shader.Tint = new Point4D(TintColor.R / 255d, TintColor.G / 255d, TintColor.B / 255d, TintOpacity);
            _shader.Material = new Point4D(DiffuseStrength, EdgeSharpness, FrostRadius > 0 ? 1d : 0d, IsDarkMode ? 1d : 0d);
            _shader.Texel = new Point4D(Math.Min(.49, .5 / (crop.Width * dpi.DpiScaleX)), Math.Min(.49, .5 / (crop.Height * dpi.DpiScaleY)),
                1 / crop.Width, 1 / crop.Height);
            _surface.Margin = new Thickness(0);
            _surface.Fill = Brushes.White;
            _surface.Effect = _shader;
        }
        else
        {
            ReleaseFrost();
            _shader?.ClearValue(TahoeGlassEffect.BackdropProperty);
            _shader?.ClearValue(TahoeGlassEffect.FrostedBackdropProperty);
            if (_backdrop != null) _backdrop.Visual = null;
            _backdrop = null;
            // Capture surrounding pixels before blurring. Clip the material host afterwards,
            // so blur taps and the outer shadow are not cut at the rounded perimeter.
            double effectiveBlur = Math.Max(BlurRadius, FrostRadius);
            double padding = effectiveBlur * 3 + 2;
            Matrix sourceToSurface = matrix;
            sourceToSurface.Invert();
            sourceToSurface.Translate(padding, padding);
            _compatibilityBackdrop ??= new VisualBrush(BackdropSource)
            {
                ViewboxUnits = BrushMappingMode.Absolute, ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill, TileMode = TileMode.FlipXY
            };
            _compatibilityBackdrop.Viewbox = crop;
            _compatibilityBackdrop.Viewport = crop;
            _compatibilityBackdrop.Transform = new MatrixTransform(sourceToSurface);
            _surface.Margin = new Thickness(-padding);
            _surface.Fill = _compatibilityBackdrop;
            _surface.Effect = effectiveBlur > 0 ? new BlurEffect { Radius = effectiveBlur, RenderingBias = RenderingBias.Performance } : null;
        }
        SetValue(IsRefractionActivePropertyKey, useShader);
        UpdateChrome(size, radius);
        _dirty = false;
    }

    private void SetStaticMaterial(Size size, double radius)
    {
        ReleaseBrushes();
        _surface.Margin = new Thickness(0);
        _surface.Effect = null;
        Color color = TintColor;
        color.A = 235;
        _surface.Fill = FrozenBrush(color);
        _lastSourceValid = false;
        _lastPanelSize = size;
        SetValue(IsRefractionActivePropertyKey, false);
        UpdateChrome(size, radius);
        _dirty = false;
    }

    private void UpdateChrome(Size size, double radius)
    {
        bool shaderActive = IsRefractionActive;
        if (size == _chromeSize && radius == _chromeRadius && TintColor == _chromeTintColor &&
            TintOpacity == _chromeTintOpacity && IsDarkMode == _chromeDarkMode && shaderActive == _chromeShaderActive) return;
        _chromeSize = size;
        _chromeRadius = radius;
        _chromeTintColor = TintColor;
        _chromeTintOpacity = TintOpacity;
        _chromeDarkMode = IsDarkMode;
        _chromeShaderActive = shaderActive;
        _clippedMaterial.Clip = new RectangleGeometry(new Rect(size), radius, radius);
        _shadow.CornerRadius = _tint.CornerRadius = _rim.CornerRadius = new CornerRadius(radius);
        _shadow.Opacity = IsDarkMode ? .20 : .11;
        if (shaderActive)
        {
            // Optical shader already composites theme tint, diffuse scatter, and Screen rim glints.
            _tint.Background = Brushes.Transparent;
        }
        else
        {
            Color tint = TintColor;
            tint.A = (byte)Math.Round(TintOpacity * tint.A);
            _tint.Background = FrozenBrush(tint);
        }

        if (SystemParameters.HighContrast)
        {
            _rim.BorderThickness = new Thickness(1);
            _rim.BorderBrush = SystemColors.WindowTextBrush;
            return;
        }

        _rim.BorderThickness = new Thickness(shaderActive ? .55 : .75);
        var rim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        if (IsDarkMode)
        {
            rim.GradientStops.Add(new GradientStop(Color.FromArgb(shaderActive ? (byte)64 : (byte)110, 255, 255, 255), 0));
            rim.GradientStops.Add(new GradientStop(Color.FromArgb(10, 255, 255, 255), .42));
            rim.GradientStops.Add(new GradientStop(Color.FromArgb(24, 0, 0, 0), .74));
            rim.GradientStops.Add(new GradientStop(Color.FromArgb(shaderActive ? (byte)42 : (byte)75, 255, 255, 255), 1));
        }
        else
        {
            rim.GradientStops.Add(new GradientStop(Color.FromArgb(shaderActive ? (byte)108 : (byte)165, 255, 255, 255), 0));
            rim.GradientStops.Add(new GradientStop(Color.FromArgb(16, 255, 255, 255), .42));
            rim.GradientStops.Add(new GradientStop(Color.FromArgb(shaderActive ? (byte)22 : (byte)48, 25, 35, 48), .74));
            rim.GradientStops.Add(new GradientStop(Color.FromArgb(shaderActive ? (byte)70 : (byte)110, 255, 255, 255), 1));
        }
        rim.Freeze();
        _rim.BorderBrush = rim;
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void ReleaseBrushes()
    {
        ReleaseFrost();
        if (_backdrop != null) _backdrop.Visual = null;
        if (_compatibilityBackdrop != null) _compatibilityBackdrop.Visual = null;
        _backdrop = _compatibilityBackdrop = null;
        // Shader samplers accept only visual/image/cache brushes (or ImplicitInput).
        // A transparent SolidColorBrush is still invalid and throws during unload.
        _shader?.ClearValue(TahoeGlassEffect.BackdropProperty);
        _shader?.ClearValue(TahoeGlassEffect.FrostedBackdropProperty);
    }

    private void ReleaseFrost()
    {
        if (_frostInput != null) _frostInput.Visual = null;
        if (_frostOutput != null) _frostOutput.Visual = null;
        if (_frostSource != null) { _frostSource.Fill = null; _frostSource.Effect = null; }
        _frostInput = _frostOutput = null;
        _frostSource = null;
    }
}
