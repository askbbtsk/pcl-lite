using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCL.Core.App.Localization;

namespace PCL;

/// <summary>Built-in performance preset. Sampling runs only while its card is loaded.</summary>
public sealed class PagePerformance : MyPageRight
{
    private readonly StackPanel _root = new() { Margin = new Thickness(0, 0, 0, 15) };

    public PagePerformance()
    {
        Child = _root;
        Loaded += (_, _) => Build();
        WeakLanguageChanged.Add(this, OnLanguageChanged);
    }

    private static void OnLanguageChanged(PagePerformance page) => ModBase.RunInUi(page.Build);

    private void Build()
    {
        _root.Children.Clear();
        _root.Children.Add(BuildPerformanceCard());
    }

    /// <summary>
    ///     性能状态：CPU 与内存占用的实时读数。
    ///     <para>
    ///         全部数据来自本机 kernel32 调用（<c>GetSystemTimes</c> 与 <c>GlobalMemoryStatusEx</c>），
    ///         不上传、不联网、不写盘；每 2 秒刷新一次，页面卸载时停止计时器。
    ///     </para>
    /// </summary>
    private MyCard BuildPerformanceCard()
    {
        var card = new MyCard { Margin = new Thickness(0, 15, 0, 0), CanSwap = false };

        var panel = new StackPanel { Margin = new Thickness(25, 30, 25, 22) };

        var titleMain = _Text(Lang.Text("Welcome.Performance.Title"), 28, bold: true);
        titleMain.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
        panel.Children.Add(titleMain);

        var subtitle = _Text(Lang.Text("Welcome.Performance.Subtitle"), 12, false);
        subtitle.Margin = new Thickness(0, 6, 0, 0);
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4");
        panel.Children.Add(subtitle);

        var cpuRow = _BuildMetricRow(Lang.Text("Welcome.Performance.Cpu"), out var cpuValue, out var cpuBar);
        cpuRow.Margin = new Thickness(0, 22, 0, 0);
        panel.Children.Add(cpuRow);

        var ramRow = _BuildMetricRow(Lang.Text("Welcome.Performance.Memory"), out var ramValue, out var ramBar);
        ramRow.Margin = new Thickness(0, 14, 0, 0);
        panel.Children.Add(ramRow);

        var detail = _Text("", 11, false);
        detail.Margin = new Thickness(0, 14, 0, 0);
        detail.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4");
        panel.Children.Add(detail);

        card.Children.Add(panel);

        ulong lastIdle = 0, lastKernel = 0, lastUser = 0;
        var seeded = false;

        void Update()
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return;

            var idleNow = ToUInt64(idle);
            var kernelNow = ToUInt64(kernel);
            var userNow = ToUInt64(user);

            if (seeded)
            {
                // 第一次采样只作为基准，避免刚打开时闪出一个假的 0%
                var idleDelta = idleNow - lastIdle;
                var totalDelta = kernelNow - lastKernel + (userNow - lastUser);
                var cpu = totalDelta > 0 ? (1d - idleDelta / (double)totalDelta) * 100d : 0d;
                cpu = Math.Clamp(cpu, 0d, 100d);
                cpuValue.Text = $"{cpu:0}%";
                cpuBar.Set(cpu);
            }

            lastIdle = idleNow;
            lastKernel = kernelNow;
            lastUser = userNow;
            seeded = true;

            var mem = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref mem))
            {
                var ram = mem.dwMemoryLoad;
                ramValue.Text = $"{ram}%";
                ramBar.Set(ram);
                detail.Text = Lang.Text("Welcome.Performance.Detail",
                    FormatGb(mem.ullTotalPhys - mem.ullAvailPhys),
                    FormatGb(mem.ullTotalPhys),
                    FormatGb(mem.ullAvailPhys));
            }
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => Update();
        card.Loaded += (_, _) =>
        {
            Update();
            timer.Start();
        };
        card.Unloaded += (_, _) => timer.Stop();

        return card;
    }

    private static Grid _BuildMetricRow(string label, out TextBlock value, out MetricBar bar)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var lab = _Text(label, 12, false);
        lab.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3");
        row.Children.Add(lab);

        value = _Text("--", 12, bold: true);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1");
        Grid.SetColumn(value, 1);
        row.Children.Add(value);

        var track = new Grid
        {
            Height = 7,
            Margin = new Thickness(0, 7, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var background = new Border { CornerRadius = new CornerRadius(3.5) };
        background.SetResourceReference(Border.BackgroundProperty, "ColorBrush7");
        track.Children.Add(background);

        var fill = new System.Windows.Shapes.Rectangle
        {
            Width = 0,
            Height = 7,
            RadiusX = 3.5,
            RadiusY = 3.5,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        fill.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "ColorBrush3");
        track.Children.Add(fill);

        bar = new MetricBar(track, fill);

        Grid.SetRow(track, 1);
        Grid.SetColumnSpan(track, 2);
        row.Children.Add(track);
        return row;
    }

    /// <summary>
    ///     一条进度条的填充控制：宽度按轨道实际宽度换算，轨道尺寸变化时同步刷新。
    /// </summary>
    private sealed class MetricBar
    {
        private readonly Grid _track;
        private readonly System.Windows.Shapes.Rectangle _fill;
        private double _percent;

        public MetricBar(Grid track, System.Windows.Shapes.Rectangle fill)
        {
            _track = track;
            _fill = fill;
            _track.SizeChanged += (_, _) => Apply();
        }

        public void Set(double percent)
        {
            _percent = Math.Clamp(percent, 0d, 100d);
            Apply();
        }

        private void Apply()
        {
            var width = _track.ActualWidth;
            if (width <= 0) return;
            _fill.Width = width * _percent / 100d;
        }
    }

    private static string FormatGb(ulong bytes) => $"{bytes / 1024d / 1024d / 1024d:0.0} GB";

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    private static ulong ToUInt64(FileTime time) => ((ulong)time.High << 32) | time.Low;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static TextBlock _Text(string text, double size, bool bold = false, string? tag = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal
        };
        if (tag is not null) block.Tag = tag;
        block.SetResourceReference(TextBlock.ForegroundProperty, bold ? "ColorBrush1" : "ColorBrushGray2");
        return block;
    }

}
