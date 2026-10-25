using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Link.Bedrock;
using PCL.Core.UI;

namespace PCL;

/// <summary>
///     基岩版下载页：列出 <see cref="BedrockVersionCatalog" /> 里的全部版本，下载原包并安装。
///     <para>
///         UWP 包（商店包）走 <see cref="ModBedrockInstaller.InstallUwp" />：解包、改清单、注册进系统；
///         GDK 包（<c>.msixvc</c>）走 <see cref="ModBedrockInstaller.InstallGdk" />：用内容密钥解到独立目录。
///         两者都是官方 CDN 上的原包，启动器只负责搬运与安装。
///     </para>
/// </summary>
public partial class PageBedrockDownload
{
    /// <summary>同一时间只允许跑一个下载/安装任务，否则进度会互相覆盖。</summary>
    private static bool _busy;

    /// <summary>列表最多渲染多少行：数据源有 600 多条，全塞进非虚拟化的 ItemsControl 会明显卡顿。</summary>
    private const int MaxRows = 150;

    private readonly List<VersionView> _views = [];
    private IReadOnlyList<BedrockVersionEntry> _all = [];
    private bool _isLoadedOnce;

    public PageBedrockDownload()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!_isLoadedOnce)
            {
                _isLoadedOnce = true;
                _ = ReloadAsync();
                return;
            }

            // 从「启动」页切回来时刷新已安装状态即可，不必重新联网
            RefreshStates();
        };
    }

    private async Task ReloadAsync(bool force = false)
    {
        if (_busy) return;

        BtnRefresh.IsEnabled = false;
        LabStatus.Text = Lang.Text("Bedrock.Download.Status.Loading");

        try
        {
            _all = await ModBedrockDownload.LoadAsync(force);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Bedrock] 获取版本列表失败", ModBase.LogLevel.Normal);
            LabStatus.Text = Lang.Text("Bedrock.Download.Status.LoadFailed", ex.Message);
        }
        finally
        {
            BtnRefresh.IsEnabled = true;
        }
    }

    /// <summary>按当前筛选条件重建列表。</summary>
    private void ApplyFilter()
    {
        var channel = ComboChannel?.SelectedIndex ?? 0;
        var kind = ComboKind?.SelectedIndex ?? 0;

        IEnumerable<BedrockVersionEntry> query = _all;

        if (channel > 0)
        {
            var target = channel switch
            {
                1 => "Release",
                2 => "Preview",
                _ => "Beta"
            };
            query = query.Where(e => string.Equals(e.Channel, target, StringComparison.OrdinalIgnoreCase));
        }

        if (kind > 0)
        {
            var target = kind == 1 ? BedrockPackageKind.Uwp : BedrockPackageKind.Gdk;
            query = query.Where(e => e.Kind == target);
        }

        // 版本号搜索：546 条全列出来找不动，直接按文本筛
        var keyword = TextSearch?.Text?.Trim();
        if (!string.IsNullOrEmpty(keyword))
            query = query.Where(e =>
                e.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                e.Version.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        var matched = query.ToList();
        var shown = matched.Take(MaxRows).ToList();

        _views.Clear();
        _views.AddRange(shown.Select(entry => new VersionView(entry)));

        ListVersions.ItemsSource = null;
        ListVersions.ItemsSource = _views;

        LabEmpty.Visibility = _views.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LabStatus.Text = matched.Count > shown.Count
            ? Lang.Text("Bedrock.Download.Status.LoadedLimited", matched.Count, shown.Count)
            : Lang.Text("Bedrock.Download.Status.Loaded", matched.Count);
    }

    /// <summary>只刷新每一行的「已下载 / 已安装」状态，不动列表结构。</summary>
    private void RefreshStates()
    {
        foreach (var view in _views) view.RefreshState();
    }

    private void ComboFilter_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // XAML 初始化时控件可能还没建好
        if (!_isLoadedOnce || _all.Count == 0) return;
        ApplyFilter();
    }

    private void TextSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_isLoadedOnce || _all.Count == 0) return;
        ApplyFilter();
    }

    private void BtnRefresh_Click(object sender, MouseButtonEventArgs e)
    {
        _ = ReloadAsync(force: true);
    }

    private void BtnOpenFolder_Click(object sender, MouseButtonEventArgs e)
    {
        var path = ModBedrockDownload.RootDirectory;
        Directory.CreateDirectory(path);
        ModBase.OpenExplorer(path);
    }

    private void BtnDeveloperMode_Click(object sender, MouseButtonEventArgs e)
    {
        if (ModBedrockInstaller.IsDeveloperModeEnabled())
        {
            HintService.Hint(Lang.Text("Bedrock.Download.DeveloperMode.On"), HintType.Success);
            return;
        }

        if (ModBedrockInstaller.EnableDeveloperMode())
        {
            HintService.Hint(Lang.Text("Bedrock.Download.DeveloperMode.Enabled"), HintType.Success);
            LabStatus.Text = Lang.Text("Bedrock.Download.DeveloperMode.On");
            return;
        }

        HintService.Hint(Lang.Text("Bedrock.Download.DeveloperMode.Failed"), HintType.Error);
        LabStatus.Text = Lang.Text("Bedrock.Download.DeveloperMode.Off");
    }

    private async void BtnAction_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VersionView view }) return;

        if (_busy)
        {
            HintService.Hint(Lang.Text("Bedrock.Download.Busy"), HintType.Warning);
            return;
        }

        if (view.Installed)
        {
            HintService.Hint(Lang.Text("Bedrock.Download.AlreadyInstalled", view.Entry.Key), HintType.Info);
            return;
        }

        _busy = true;
        BtnRefresh.IsEnabled = false;
        view.SetBusy(true);

        try
        {
            var packagePath = ModBedrockDownload.GetPackagePath(view.Entry);

            if (!File.Exists(packagePath))
            {
                view.SetStatus(Lang.Text("Bedrock.Download.Status.Resolving"));
                var url = await ModBedrockDownload.ResolveUrlAsync(view.Entry);
                if (string.IsNullOrWhiteSpace(url))
                    throw new InvalidOperationException(Lang.Text("Bedrock.Download.UrlFailed", view.Entry.Key));

                view.SetStatus(Lang.Text("Bedrock.Download.Status.Downloading", 0d));
                var progress = new Progress<(long Received, long Total)>(p =>
                {
                    var percent = p.Total > 0 ? p.Received * 100d / p.Total : 0d;
                    view.SetProgress(percent);
                    view.SetStatus(p.Total > 0
                        ? Lang.Text("Bedrock.Download.Status.Downloading", percent)
                        : Lang.Text("Bedrock.Download.Status.DownloadingUnknown", p.Received / 1024d / 1024d));
                });

                packagePath = await ModBedrockDownload.DownloadAsync(view.Entry, url, progress);
            }

            view.SetProgress(100d);
            view.SetStatus(Lang.Text("Bedrock.Download.Status.Verifying"));
            if (!ModBedrockDownload.VerifyMd5(packagePath, view.Entry.Md5))
                throw new InvalidOperationException(Lang.Text("Bedrock.Download.Md5Failed"));

            view.SetStatus(Lang.Text("Bedrock.Download.Status.Installing"));
            var installedPath = await Task.Run(() => Install(view, packagePath));

            view.SetBusy(false);
            view.RefreshState();
            LabStatus.Text = Lang.Text("Bedrock.Download.Done", view.Entry.Key, installedPath);
            HintService.Hint(Lang.Text("Bedrock.Download.Done", view.Entry.Key, installedPath), HintType.Success);
        }
        catch (Exception ex)
        {
            view.SetBusy(false);
            view.SetStatus(Lang.Text("Bedrock.Download.State.NotInstalled"));
            LabStatus.Text = Lang.Text("Bedrock.Download.Failed", ex.Message);
            ModBase.Log(ex, $"[Bedrock] 安装 {view.Entry.Key} 失败", ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Bedrock.Download.Failed", ex.Message));
        }
        finally
        {
            _busy = false;
            BtnRefresh.IsEnabled = true;
        }
    }

    /// <summary>按包形态安装。UWP 注册进系统，GDK 解包到独立目录。</summary>
    private static string Install(VersionView view, string packagePath)
    {
        var entry = view.Entry;

        if (entry.Kind == BedrockPackageKind.Uwp)
            return ModBedrockInstaller.InstallUwp(packagePath, entry.Key,
                text => ModBase.RunInUi(() => view.SetStatus(text)));

        return ModBedrockInstaller.InstallGdk(packagePath, entry.Key, (name, index, total) =>
        {
            var percent = total > 0 ? index * 100d / total : 0d;
            ModBase.RunInUi(() =>
            {
                view.SetProgress(percent);
                view.SetStatus(Lang.Text("Bedrock.Download.Status.Extracting", percent, name));
            });
        });
    }

    /// <summary>列表项：把 <see cref="BedrockVersionEntry" /> 摊平成可直接绑定的字段。</summary>
    public sealed class VersionView : INotifyPropertyChanged
    {
        private const double BarWidth = 220d;

        private string _status = "";
        private double _progress;
        private Visibility _progressVisibility = Visibility.Collapsed;
        private bool _isBusy;

        public VersionView(BedrockVersionEntry entry)
        {
            Entry = entry;
            RefreshState();
        }

        public BedrockVersionEntry Entry { get; }

        public string Title => Entry.Key;

        public string Badge =>
            $"{ChannelName(Entry.Channel)} · {(Entry.Kind == BedrockPackageKind.Uwp ? "UWP" : "GDK")}";

        public string Subtitle => string.IsNullOrWhiteSpace(Entry.Date)
            ? Entry.Version
            : $"{Entry.Version} · {Entry.Date}";

        public string Status
        {
            get => _status;
            private set => SetField(ref _status, value);
        }

        public double ProgressWidth => _progress / 100d * BarWidth;

        public Visibility ProgressVisibility
        {
            get => _progressVisibility;
            private set => SetField(ref _progressVisibility, value);
        }

        public string ActionText => Installed
            ? Lang.Text("Bedrock.Download.Action.Installed")
            : Lang.Text("Bedrock.Download.Action.DownloadAndInstall");

        public bool Installed { get; private set; }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetField(ref _isBusy, value)) OnPropertyChanged(nameof(ActionText));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>重新读一次本地状态：没下载 / 已下载 / 已安装。</summary>
        public void RefreshState()
        {
            Installed = CheckInstalled(Entry);
            if (Installed)
                Status = Lang.Text("Bedrock.Download.State.Installed");
            else if (ModBedrockDownload.IsDownloaded(Entry))
                Status = Lang.Text("Bedrock.Download.State.Downloaded");
            else
                Status = Lang.Text("Bedrock.Download.State.NotInstalled");

            if (Installed || !IsBusy) SetProgress(0d);
            OnPropertyChanged(nameof(ActionText));
            OnPropertyChanged(nameof(Status));
        }

        public void SetStatus(string text)
        {
            Status = text;
        }

        public void SetProgress(double percent)
        {
            _progress = Math.Clamp(percent, 0d, 100d);
            ProgressVisibility = percent is > 0d and < 100d ? Visibility.Visible : Visibility.Collapsed;
            OnPropertyChanged(nameof(ProgressWidth));
        }

        public void SetBusy(bool busy)
        {
            IsBusy = busy;
            if (!busy) SetProgress(0d);
        }

        private static bool CheckInstalled(BedrockVersionEntry entry)
        {
            try
            {
                return entry.Kind == BedrockPackageKind.Gdk
                    ? ModBedrockGdk.IsInstalled(
                        ModBedrockUwp.GetVersionDirectory(ModBedrockDownload.VersionsDirectory, entry.Key))
                    : Directory.Exists(
                        ModBedrockUwp.GetVersionDirectory(ModBedrockDownload.ExtractedDirectory, entry.Key));
            }
            catch (Exception)
            {
                // 版本键不合法之类的，当作没装
                return false;
            }
        }

        private static string ChannelName(string channel) => channel.ToUpperInvariant() switch
        {
            "RELEASE" => Lang.Text("Bedrock.Channel.Release"),
            "PREVIEW" => Lang.Text("Bedrock.Channel.Preview"),
            "BETA" => Lang.Text("Bedrock.Channel.Beta"),
            _ => channel
        };

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? property = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(property);
            return true;
        }

        private void OnPropertyChanged(string? property)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
