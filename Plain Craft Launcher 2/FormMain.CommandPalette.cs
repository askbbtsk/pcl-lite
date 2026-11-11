using System.IO;
using System.Windows;
using System.Windows.Input;
using PCL.Core.App.Localization;
using PCL.Core.UI.Navigation;

namespace PCL;

public partial class FormMain
{
    private CancellationTokenSource? _paletteLoading;

    private void InitializeCommandPalette()
    {
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.K || Keyboard.Modifiers != ModifierKeys.Control) return;
            e.Handled = true;
            if (!e.IsRepeat) ToggleCommandPalette();
        };
        Palette.Dismissed += () => _paletteLoading?.Cancel();
        PanMsgBackground.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Palette.Close();
        };
        Palette.ExecutionFailed += ex => ModBase.Log(ex, "执行命令面板操作失败", ModBase.LogLevel.Hint,
            userSummary: Lang.Text("CommandPalette.Error"));
        Closed += (_, _) => _paletteLoading?.Cancel();
    }

    private void CommandPalette_Click(object sender, EventArgs e) => ToggleCommandPalette();

    private async void ToggleCommandPalette()
    {
        // Respect the launcher's existing confirmation and login dialogs.
        if (PanMsg.Children.Count > 0 || ModMain.WaitingMyMsgBox.Any()) return;
        if (Palette.Visibility == Visibility.Visible)
        {
            Palette.Close();
            return;
        }

        _paletteLoading?.Cancel();
        using var loading = new CancellationTokenSource();
        _paletteLoading = loading;
        var token = loading.Token;
        Palette.Open(CreatePageCommands());
        try
        {
            // Wait for the existing loader. Opening search must not initiate a rescan.
            while (ModInstanceList.mcInstanceListLoader.State == ModBase.LoadState.Loading)
                await Task.Delay(100, token);
            token.ThrowIfCancellationRequested();
            var selected = ModInstanceList.McMcInstanceSelected;
            var instances = ModInstanceList.mcInstanceList
                .Where(group => group.Key != McInstanceCardType.Hidden)
                .SelectMany(group => group.Value)
                .Append(selected)
                .Where(instance => instance is not null)
                .DistinctBy(instance => instance.PathInstance, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Palette.AddCommands(instances.Select(instance => new PaletteCommand(
                new CommandPaletteEntry("instance:" + instance.PathInstance, instance.Name,
                    Lang.Text(instance.state == McInstanceState.Error ? "Select.Instance.OpenFolder" : "CommandPalette.OpenInstance"),
                    Lang.Text("CommandPalette.Instance"), instance.Desc),
                () => OpenPaletteInstance(instance))));

            if (selected is null)
            {
                Palette.CompleteLoading(Lang.Text("CommandPalette.NoInstance"));
                return;
            }

            // Capture the selected instance once. A late scan never switches to a newer selection.
            var modsPath = Path.Combine(selected.PathIndie, "mods");
            var instanceName = selected.Name;
            var modCategory = Lang.Text("CommandPalette.Mod");
            var modDescription = Lang.Text("CommandPalette.LocateMod", instanceName);
            var disabled = Lang.Text("CommandPalette.Disabled");
            var mods = await Task.Run(() => ReadPaletteMods(modsPath, modCategory, modDescription, disabled, token), token);
            token.ThrowIfCancellationRequested();
            Palette.AddCommands(mods);
            Palette.CompleteLoading();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Palette.CompleteLoading(Lang.Text("CommandPalette.ScanFailed"));
                ModBase.Log(ex, "命令面板读取本地目录失败", ModBase.LogLevel.Developer);
            }
        }
        finally
        {
            if (ReferenceEquals(_paletteLoading, loading)) _paletteLoading = null;
        }
    }

    private void OpenPaletteInstance(McInstance instance)
    {
        if (!Directory.Exists(instance.PathInstance)) throw new DirectoryNotFoundException(instance.PathInstance);
        // Match the instance list: damaged instances expose their folder instead of management.
        if (instance.state == McInstanceState.Error)
        {
            ModBase.OpenExplorer(instance.PathInstance);
            return;
        }
        PageInstanceLeft.McInstance = instance;
        PageChange(PageType.InstanceSetup, PageSubType.VersionOverall);
    }

    private static List<PaletteCommand> ReadPaletteMods(string directory, string category,
        string description, string disabled, CancellationToken token)
    {
        var commands = new List<PaletteCommand>();
        if (!Directory.Exists(directory)) return commands;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Directory,
            IgnoreInaccessible = false
        };
        foreach (var file in Directory.EnumerateFiles(directory, "*", options))
        {
            token.ThrowIfCancellationRequested();
            if (!ModLocalComp.LocalCompFile.IsModFile(file)) continue;
            var local = new ModLocalComp.LocalCompFile(file);
            var fileName = Path.GetFileName(file);
            var title = fileName;
            var modId = "";
            // A malformed or locked mod must not hide the rest of the catalog.
            try { title = local.Name; modId = local.ModId; }
            catch (Exception ex) { ModBase.Log(ex, "命令面板跳过不可读取的模组元数据", ModBase.LogLevel.Developer); }
            var isDisabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                             || file.EndsWith(".old", StringComparison.OrdinalIgnoreCase);
            commands.Add(new PaletteCommand(new CommandPaletteEntry("mod:" + file, title,
                    $"{description} · {fileName}{(isDisabled ? " · " + disabled : "")}", category,
                    $"{fileName} {modId}"),
                () =>
                {
                    if (!File.Exists(file)) throw new FileNotFoundException(fileName);
                    ModBase.OpenExplorer(file);
                }));
        }
        return commands.OrderBy(command => command.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private IEnumerable<PaletteCommand> CreatePageCommands()
    {
        PaletteCommand Page(string id, string titleKey, PageType page, PageSubType sub = PageSubType.Default,
            string keywords = "", bool setting = false) => new(
            new CommandPaletteEntry(id, Lang.Text(titleKey), Lang.Text("CommandPalette.OpenPage"),
                Lang.Text(setting ? "CommandPalette.Setting" : "CommandPalette.Action"), keywords),
            () => PageChange(page, sub));

        yield return Page("launch", "Main.Tab.Launch", PageType.Launch, keywords: "home launch 主页 啟動");
        yield return Page("instances", "Main.Title.InstanceSelect", PageType.InstanceSelect, keywords: "instances 实例 實例");
        yield return Page("tasks", "Main.Title.TaskManager", PageType.TaskManager, keywords: "tasks downloads 任务 任務 下载 下載");
        yield return Page("install", "Download.Left.Minecraft", PageType.Download, PageSubType.DownloadInstall, "install minecraft 安装 安裝");
        yield return Page("mods", "Download.Left.Mod", PageType.Download, PageSubType.DownloadMod, "download mods 模组 模組");
        yield return Page("java", "Setup.Left.Item.Java", PageType.Setup, PageSubType.SetupJava, "java runtime jdk 运行时 執行階段", true);
        yield return Page("launch-settings", "Setup.Left.Item.Launch", PageType.Setup, PageSubType.SetupLaunch, "memory ram jvm 内存 記憶體 启动 啟動 参数 參數", true);
        yield return Page("appearance", "Setup.Left.Item.Ui", PageType.Setup, PageSubType.SetupUI, "theme wallpaper background glass 主题 主題 壁纸 桌布 背景 玻璃", true);
        yield return Page("language", "Setup.Language.Title", PageType.Setup, PageSubType.SetupLauncherLanguage, "language locale 语言 語言", true);
        yield return Page("game-settings", "Setup.Left.Item.GameManage", PageType.Setup, PageSubType.SetupGameManage, "game isolation mods 游戏 遊戲 隔离 隔離 模组 模組", true);
        yield return Page("misc", "Setup.Left.Item.Misc", PageType.Setup, PageSubType.SetupLauncherMisc, "proxy network mcp 代理 网络 網路", true);
        yield return Page("update", "Setup.Left.Item.Update", PageType.Setup, PageSubType.SetupUpdate, "update channel 更新 通道", true);
        yield return Page("about", "Setup.Left.Item.About", PageType.Setup, PageSubType.SetupAbout, "about version 关于 關於 版本", true);
        yield return Page("plugins", "Main.Tab.Plugins", PageType.Plugins, PageSubType.PluginInstalled, "plugins 插件 外掛");
    }
}
