using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace PCL;

internal sealed class TahoeGlassEffect : ShaderEffect
{
    private static readonly Lazy<PixelShader> CompiledShader = new(() =>
    {
        string assembly = typeof(TahoeGlassEffect).Assembly.GetName().Name!;
        var resource = new Uri($"/{assembly};component/Resources/LiquidGlass/Glass.ps", UriKind.Relative);
        using Stream stream = Application.GetResourceStream(resource)?.Stream
            ?? throw new FileNotFoundException("The compiled Tahoe glass shader resource was not found.");
        var shader = new PixelShader();
        shader.SetStreamSource(stream);
        shader.Freeze();
        return shader;
    });

    public static readonly DependencyProperty BackdropProperty = RegisterPixelShaderSamplerProperty(
        nameof(Backdrop), typeof(TahoeGlassEffect), 0, SamplingMode.Bilinear);
    public static readonly DependencyProperty FrostedBackdropProperty = RegisterPixelShaderSamplerProperty(
        nameof(FrostedBackdrop), typeof(TahoeGlassEffect), 1, SamplingMode.Bilinear);
    public static readonly DependencyProperty SizeProperty = Constant(nameof(Size), 0);
    public static readonly DependencyProperty OriginProperty = Constant(nameof(Origin), 1);
    public static readonly DependencyProperty AxisProperty = Constant(nameof(Axis), 2);
    public static readonly DependencyProperty SourceProperty = Constant(nameof(Source), 3);
    public static readonly DependencyProperty TexelProperty = Constant(nameof(Texel), 4);
    public static readonly DependencyProperty OpticsProperty = Constant(nameof(Optics), 5);
    public static readonly DependencyProperty TintProperty = Constant(nameof(Tint), 6);
    public static readonly DependencyProperty MaterialProperty = Constant(nameof(Material), 7);

    private static DependencyProperty Constant(string name, int register) => DependencyProperty.Register(
        name, typeof(Point4D), typeof(TahoeGlassEffect),
        new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(register)));

    public TahoeGlassEffect()
    {
        PixelShader = CompiledShader.Value;
        foreach (DependencyProperty property in new[]
                 {
                     BackdropProperty, FrostedBackdropProperty, SizeProperty, OriginProperty,
                     AxisProperty, SourceProperty, TexelProperty, OpticsProperty, TintProperty, MaterialProperty
                 })
            UpdateShaderValue(property);
    }

    public Brush Backdrop { get => (Brush)GetValue(BackdropProperty); set => SetValue(BackdropProperty, value); }
    public Brush FrostedBackdrop { get => (Brush)GetValue(FrostedBackdropProperty); set => SetValue(FrostedBackdropProperty, value); }
    public Point4D Size { get => (Point4D)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public Point4D Origin { get => (Point4D)GetValue(OriginProperty); set => SetValue(OriginProperty, value); }
    public Point4D Axis { get => (Point4D)GetValue(AxisProperty); set => SetValue(AxisProperty, value); }
    public Point4D Source { get => (Point4D)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public Point4D Texel { get => (Point4D)GetValue(TexelProperty); set => SetValue(TexelProperty, value); }
    public Point4D Optics { get => (Point4D)GetValue(OpticsProperty); set => SetValue(OpticsProperty, value); }
    public Point4D Tint { get => (Point4D)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public Point4D Material { get => (Point4D)GetValue(MaterialProperty); set => SetValue(MaterialProperty, value); }
}
