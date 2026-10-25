using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Link.Bedrock;
using PCL.Core.UI;

namespace PCL;

/// <summary>
///     基岩版启动页：列出本机所有可启动的基岩版（商店版、手动指定的 GDK 目录、以及下载页解包出来的版本）。
///     <para>
///         检测与启动逻辑在 <see cref="BedrockService" />，移植自 PCL-Symbio-Edition（Apache-2.0）。
///     </para>
/// </summary>
public partial class PageBedrockLaunch
{
    private bool _detecting;

    public PageBedrockLaunch()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    /// <summary>重新检测并刷新列表。</summary>
    public void Reload()
    {
        if (_detecting) return;
        _detecting = true;

        LabStatus.Text = Lang.Text("Bedrock.Launch.Detecting");
        BtnRefresh.IsEnabled = false;

        var directories = GetConfiguredDirectories();
        Task.Run(() =>
        {
            var installs = BedrockService.Detect(directories, out var error);
            // 下载页解包出来的 GDK 版本不向系统注册，只能由启动器自己扫目录
            var downloaded = BedrockService.DetectDownloaded(ModBedrockDownload.VersionsDirectory);
            var merged = installs
                .Concat(downloaded)
                .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            ModBase.RunInUi(() =>
            {
                _detecting = false;
                BtnRefresh.IsEnabled = true;
                Bind(merged, error);
            });
        });
    }

    private void Bind(IReadOnlyList<BedrockInstall> installs, string? error)
    {
        var items = installs.Select(install => new BedrockItemView(install)).ToList();
        ListInstalls.ItemsSource = items;
        LabEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(error))
        {
            LabStatus.Text = Lang.Text("Bedrock.Launch.DetectFailed", error);
            return;
        }

        LabStatus.Text = BedrockService.IsRunning()
            ? Lang.Text("Bedrock.Launch.Running")
            : Lang.Text("Bedrock.Launch.NotRunning");
    }

    private void BtnRefresh_Click(object sender, MouseButtonEventArgs e)
    {
        Reload();
    }

    private void BtnLaunch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BedrockInstall install }) return;

        if (BedrockService.Launch(install, out var error))
        {
            HintService.Hint(Lang.Text("Bedrock.Launch.Launched", install.DisplayName), HintType.Success);
            LabStatus.Text = Lang.Text("Bedrock.Launch.Running");
            return;
        }

        ModBase.Log($"[Bedrock] 启动失败（{install.DisplayName}）：{error}", ModBase.LogLevel.Msgbox,
            userSummary: Lang.Text("Bedrock.Launch.LaunchFailed", error ?? ""));
    }

    private void BtnStop_Click(object sender, MouseButtonEventArgs e)
    {
        var count = BedrockService.StopAll();
        HintService.Hint(count > 0
            ? Lang.Text("Bedrock.Launch.Stopped", count)
            : Lang.Text("Bedrock.Launch.NoneRunning"));

        LabStatus.Text = BedrockService.IsRunning()
            ? Lang.Text("Bedrock.Launch.Running")
            : Lang.Text("Bedrock.Launch.NotRunning");
    }

    private void BtnOpenInstallFolder_Click(object sender, MouseButtonEventArgs e)
    {
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
        var directories = GetConfiguredDirectories();
        if (directories.Any(x => string.Equals(x, folder, StringComparison.OrdinalIgnoreCase)))
        {
            HintService.Hint(Lang.Text("Bedrock.Launch.AlreadyAdded"));
            return;
        }

        directories.Add(folder);
        Config.Launch.BedrockGdkDirectories = string.Join("\n", directories);
        Reload();
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

    private static List<string> GetConfiguredDirectories()
    {
        var raw = Config.Launch.BedrockGdkDirectories;
        if (string.IsNullOrWhiteSpace(raw)) return [];

        return raw
            .Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>列表项：把安装信息摊平成可直接绑定的两个字符串。</summary>
    public sealed class BedrockItemView
    {
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
    }
}
