using System.IO;
using System.Windows;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Link.Bedrock;
using PCL.Core.UI;

namespace PCL;

/// <summary>
///     基岩版页面：列出检测到的基岩版安装并启动。
///     <para>
///         启动逻辑移植自 PCL-Symbio-Edition（Apache-2.0），见
///         <see cref="BedrockService" /> 的类型注释。
///     </para>
/// </summary>
public partial class PageToolsBedrock
{
    private bool _detecting;

    public PageToolsBedrock()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    /// <summary>重新检测并刷新列表。</summary>
    public void Reload()
    {
        if (_detecting) return;
        _detecting = true;

        LabStatus.Text = Lang.Text("Tools.Bedrock.Detecting");
        BtnRefresh.IsEnabled = false;

        var directories = GetConfiguredDirectories();
        Task.Run(() =>
        {
            var installs = BedrockService.Detect(directories, out var error);
            ModBase.RunInUi(() =>
            {
                _detecting = false;
                BtnRefresh.IsEnabled = true;
                Bind(installs, error);
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
            LabStatus.Text = Lang.Text("Tools.Bedrock.DetectFailed", error);
            return;
        }

        LabStatus.Text = BedrockService.IsRunning()
            ? Lang.Text("Tools.Bedrock.Running")
            : Lang.Text("Tools.Bedrock.NotRunning");
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
            HintService.Hint(Lang.Text("Tools.Bedrock.Launched", install.DisplayName), HintType.Success);
            LabStatus.Text = Lang.Text("Tools.Bedrock.Running");
            return;
        }

        ModBase.Log($"[Bedrock] 启动失败（{install.DisplayName}）：{error}", ModBase.LogLevel.Msgbox,
            userSummary: Lang.Text("Tools.Bedrock.LaunchFailed", error ?? ""));
    }

    private void BtnStop_Click(object sender, MouseButtonEventArgs e)
    {
        var count = BedrockService.StopAll();
        HintService.Hint(count > 0
            ? Lang.Text("Tools.Bedrock.Stopped", count)
            : Lang.Text("Tools.Bedrock.NoneRunning"));

        LabStatus.Text = BedrockService.IsRunning()
            ? Lang.Text("Tools.Bedrock.Running")
            : Lang.Text("Tools.Bedrock.NotRunning");
    }

    private void BtnAddFolder_Click(object sender, MouseButtonEventArgs e)
    {
        var folder = SystemDialogs.SelectFolder(Lang.Text("Tools.Bedrock.AddFolder"));
        if (string.IsNullOrWhiteSpace(folder)) return;

        folder = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directories = GetConfiguredDirectories();
        if (directories.Any(x => string.Equals(x, folder, StringComparison.OrdinalIgnoreCase)))
        {
            HintService.Hint(Lang.Text("Tools.Bedrock.AlreadyAdded"));
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
            HintService.Hint(Lang.Text("Tools.Bedrock.SavesNotFound"), HintType.Warning);
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
                ? Lang.Text("Tools.Bedrock.Kind.Store")
                : Lang.Text("Tools.Bedrock.Kind.Gdk");

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
