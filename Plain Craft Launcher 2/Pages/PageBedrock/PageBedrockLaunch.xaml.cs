using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Link.Bedrock;
using PCL.Core.UI;

namespace PCL;

/// <summary>
///     基岩版启动页：列出本机所有可启动的基岩版（商店版、手动指定的 GDK 目录、下载页解包出来的版本）。
///     <para>
///         这个列表只是 <see cref="ModBedrockRuntime" /> 的一份视图：点某一行就把它设为当前版本，
///         左下角「启动基岩版」按钮启动的就是它。检测与启动逻辑在 <see cref="BedrockService" />。
///     </para>
/// </summary>
public partial class PageBedrockLaunch
{
    /// <summary>
    ///     列表数据源必须是可观察集合。
    ///     <para>
    ///         原来用 <c>List&lt;T&gt;</c> 并在刷新时清空重填：集合内容变了，但
    ///         <c>ItemsSource</c> 拿到的还是同一个对象引用，WPF 判定「没变化」不会重画，
    ///         于是解包完成后新版本永远不出现在列表里。换成可观察集合才会真正通知到界面。
    ///     </para>
    /// </summary>
    private readonly ObservableCollection<BedrockItemView> _items = [];

    /// <summary>已绑定的 <see cref="ModBedrockRuntime.Revision" />，相同就不重建列表。</summary>
    private int _boundRevision = -1;

    private bool _subscribed;

    public PageBedrockLaunch()
    {
        InitializeComponent();
        ListInstalls.ItemsSource = _items;

        Loaded += (_, _) =>
        {
            if (!_subscribed)
            {
                _subscribed = true;
                ModBedrockRuntime.StateChanged += OnStateChanged;
            }

            Bind(force: true);
            ModBedrockRuntime.Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (!_subscribed) return;
            _subscribed = false;
            ModBedrockRuntime.StateChanged -= OnStateChanged;
        };
    }

    private void OnStateChanged()
    {
        if (!IsLoaded) return;
        Bind();
    }

    /// <summary>把共享状态摊平成列表项。列表内容没变时只更新选中态，不重建行。</summary>
    /// <param name="force">忽略版本号，强制重建。</param>
    private void Bind(bool force = false)
    {
        if (force || _boundRevision != ModBedrockRuntime.Revision)
        {
            _boundRevision = ModBedrockRuntime.Revision;

            _items.Clear();
            foreach (var install in ModBedrockRuntime.Installs) _items.Add(new BedrockItemView(install));

            LabEmpty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        var currentId = ModBedrockRuntime.Current?.Id;
        foreach (var item in _items)
            item.IsCurrent = string.Equals(item.Install.Id, currentId, StringComparison.OrdinalIgnoreCase);

        BtnRefresh.IsEnabled = !ModBedrockRuntime.IsBusy;
    }

    private void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BedrockItemView item }) return;
        Select(item);
        e.Handled = true;
    }

    private void Select(BedrockItemView item)
    {
        ModBedrockRuntime.Select(item.Install);
        foreach (var view in _items) view.IsCurrent = ReferenceEquals(view, item);
    }

    private void BtnRefresh_Click(object sender, MouseButtonEventArgs e)
    {
        ModBedrockRuntime.Refresh();
    }

    private void BtnLaunch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BedrockInstall install }) return;
        // 阻止冒泡到行，否则点「启动」会顺带把选中项切过去
        e.Handled = true;

        var view = _items.FirstOrDefault(i => ReferenceEquals(i.Install, install));
        if (view is not null) Select(view);

        ModBedrockRuntime.Launch();
    }

    private void BtnStop_Click(object sender, MouseButtonEventArgs e)
    {
        ModBedrockRuntime.Stop();
    }

    private void BtnOpenInstallFolder_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: BedrockInstall install }) return;

        // 商店版的程序目录在 WindowsApps 下，普通权限打不开，直接提示去看存档
        if (install.ExecutablePath is not { } exe)
        {
            HintService.Hint(Lang.Text("Bedrock.Launch.NoFolder"), HintType.Warning);
            return;
        }

        var folder = Path.GetDirectoryName(exe);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            HintService.Hint(Lang.Text("Bedrock.Launch.NoFolder"), HintType.Warning);
            return;
        }

        ModBase.OpenExplorer(folder);
    }

    private void BtnAddFolder_Click(object sender, MouseButtonEventArgs e)
    {
        var folder = SystemDialogs.SelectFolder(Lang.Text("Bedrock.Launch.AddFolder"));
        if (string.IsNullOrWhiteSpace(folder)) return;

        folder = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directories = ModBedrockRuntime.GetConfiguredDirectories();
        if (directories.Any(x => string.Equals(x, folder, StringComparison.OrdinalIgnoreCase)))
        {
            HintService.Hint(Lang.Text("Bedrock.Launch.AlreadyAdded"));
            return;
        }

        directories.Add(folder);
        Config.Launch.BedrockGdkDirectories = string.Join("\n", directories);
        ModBedrockRuntime.Refresh();
    }

    private void BtnOpenSaves_Click(object sender, MouseButtonEventArgs e)
    {
        var path = BedrockService.GetSavesDirectory();
        if (!Directory.Exists(path))
        {
            HintService.Hint(Lang.Text("Bedrock.Launch.SavesNotFound"), HintType.Warning);
            return;
        }

        ModBase.OpenExplorer(path);
    }

    /// <summary>列表项：把安装信息摊平成可直接绑定的字符串。</summary>
    public sealed class BedrockItemView : INotifyPropertyChanged
    {
        private bool _isCurrent;

        public BedrockItemView(BedrockInstall install)
        {
            Install = install;

            var kind = install.Kind == BedrockKind.Store
                ? Lang.Text("Bedrock.Kind.Store")
                : Lang.Text("Bedrock.Kind.Gdk");

            var parts = new List<string> { kind };
            if (!string.IsNullOrWhiteSpace(install.Version)) parts.Add(install.Version);
            if (install.Kind == BedrockKind.GdkPortable && install.ExecutablePath is { } exe)
                parts.Add(Path.GetDirectoryName(exe) ?? exe);

            Info = string.Join(" · ", parts);
        }

        public BedrockInstall Install { get; }
        public string DisplayName => Install.DisplayName;
        public string Info { get; }

        /// <summary>是否是左下角启动按钮当前指向的那个版本。</summary>
        public bool IsCurrent
        {
            get => _isCurrent;
            set
            {
                if (_isCurrent == value) return;
                _isCurrent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
