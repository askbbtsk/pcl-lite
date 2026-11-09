using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Dapper;
using Microsoft.Data.Sqlite;
using PCL.Core.App;
using PCL.Core.Logging;
using PCL.Core.Utils;
using PCL.Core.Utils.Hash;
using PCL.Network;
using PCL.Network.Loaders;
using ProtoBuf;
using PCL.Core.App.Localization;
using PCL.Core.UI;

namespace PCL;

public static partial class ModComp
{


    /// <summary>
    /// 资源卡片的快速下载入口：按 <see cref="Config.Download.Comp.QuickDownloadBehavior"/> 指定的行为，
    /// 下载该资源最新（优先 Release、其次最新发布）的兼容版本到目标实例或文件夹。
    /// 由 <see cref="MyCompItem"/> 上的快速下载按钮调用。快速下载不会自动安装前置。
    /// </summary>
    public static void QuickDownload(CompProject project)
    {
        ModBase.RunInNewThread(() =>
        {
            try
            {
                HintService.Hint(Lang.Text("Download.Comp.QuickDownload.Hint.Loading"), HintType.Info);
                var files = FilterFilesByType(
                    CompFilesGet(project.Id, project.FromCurseForge).Where(f => f.Available).ToList(),
                    project.Type);
                if (files.Count == 0)
                {
                    HintService.Hint(Lang.Text("Download.Comp.QuickDownload.Hint.NoFile"), HintType.Info);
                    return;
                }

                var behavior = Config.Download.Comp.QuickDownloadBehavior;
                if (behavior == 0)
                {
                    // 总是询问：弹「方式选择」
                    int? choice = ModBase.RunInUiWait(() =>
                    {
                        var options = new List<IMyRadio>
                        {
                            new MyRadioBox { Text = Lang.Text("Download.Comp.QuickDownload.ChooseMethod.CurrentInstance") },
                            new MyRadioBox { Text = Lang.Text("Download.Comp.QuickDownload.ChooseMethod.AskInstance") },
                            new MyRadioBox { Text = Lang.Text("Download.Comp.QuickDownload.ChooseMethod.AskPath") }
                        };
                        return ModMain.MyMsgBoxSelect(options,
                            Lang.Text("Download.Comp.QuickDownload.ChooseMethod.Title"),
                            button1: Lang.Text("Common.Action.Continue"),
                            button2: Lang.Text("Common.Action.Cancel"));
                    });
                    if (choice is null) return; // 用户取消
                    behavior = choice.Value + 1; // 0→1 当前实例, 1→2 选实例, 2→3 选路径
                }

                switch (behavior)
                {
                    case 1: // 下载到当前选中实例
                        _QuickDownloadToInstance(project, files, ModInstanceList.McMcInstanceSelected);
                        break;
                    case 2: // 询问并下载到选择的实例
                    {
                        var instances = _QuickDownloadPickInstances(project, files);
                        if (instances is null) return;
                        var startedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var instance in instances)
                            _QuickDownloadToInstance(project, files, instance, startedTargets);
                        break;
                    }
                    case 3: // 询问并下载到一个路径
                        _QuickDownloadToFolder(project, files);
                        break;
                }
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    "[Comp] 快速下载失败",
                    ModBase.LogLevel.Feedback,
                    userSummary: Lang.Text("Minecraft.Comp.Error.OperationFailed"));
            }
        }, "Comp QuickDownload");
    }


    /// <summary>下载到指定实例的最新兼容版本。</summary>
    private static void _QuickDownloadToInstance(CompProject project, List<CompFile> files, McInstance? instance,
        ISet<string>? startedTargets = null)
    {
        if (instance is null)
        {
            HintService.Hint(Lang.Text("Download.Comp.QuickDownload.Hint.NoInstance"), HintType.Info);
            return;
        }
        if (!instance.IsLoaded) instance.Load();
        var compatible = files
            .Where(f => IsInstanceSuitableForFile(instance, f, _ResolveLoaders(f, project)))
            .ToList();
        var file = _PickLatestFile(compatible);
        if (file is null)
        {
            HintService.Hint(Lang.Text("Download.Comp.QuickDownload.Hint.NoCompatibleFile"), HintType.Info);
            return;
        }
        var folder = instance.PathIndie + _GetSubFolder(project.Type);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, CompFileNameGet(project, file));
        //只下载一次，避免并发写同一文件
        if (startedTargets is not null && !startedTargets.Add(target))
            return;
        _StartQuickDownload(file, target);
        HintService.Hint(Lang.Text("Download.Comp.QuickDownload.Hint.DownloadStarted", project.RawName), HintType.Success);
    }


    /// <summary>弹实例列表让用户选择，返回选中的实例（兼容者优先、当前选中实例居首）；取消或无兼容实例返回 null。</summary>
    private static List<McInstance>? _QuickDownloadPickInstances(CompProject project, List<CompFile> files)
    {
        var needLoad = ModInstanceList.mcInstanceListLoader.State != ModBase.LoadState.Finished;
        if (needLoad)
        {
            HintService.Hint(Lang.Text("Download.Comp.QuickDownload.Hint.Loading"), HintType.Info);
            ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, ModFolder.mcFolderSelected,
                ModLoader.LoaderFolderRunType.ForceRun, 1, "versions\\", true);
        }
        var compatible = ModInstanceList.mcInstanceList.Values
            .SelectMany(l => l)
            .Where(v => v is not null && files.Any(f => IsInstanceSuitableForFile(v, f, _ResolveLoaders(f, project))))
            .ToList();
        if (compatible.Count == 0)
        {
            HintService.Hint(Lang.Text("Download.Comp.QuickDownload.Hint.NoCompatibleInstance"), HintType.Info);
            return null;
        }
        // 当前选中实例排首位（呼应 issue「第一行应为当前所选实例」）
        var current = ModInstanceList.McMcInstanceSelected;
        if (current is not null)
            compatible = compatible
                .OrderBy(v => v == current ? 0 : 1)
                .ThenBy(v => v.Name)
                .ToList();
        var indices = ModBase.RunInUiWait(() =>
        {
            var options = compatible
                .Select(v => new MyListItem { Title = v.Name })
                .ToList();
            return ModMain.MyMsgBoxMultiSelect(options,
                Lang.Text("Download.Comp.QuickDownload.ChooseInstance.Title"),
                button1: Lang.Text("Common.Action.Continue"),
                button2: Lang.Text("Common.Action.Cancel"));
        });
        if (indices is null) return null;
        return indices.Select(i => compatible[i]).ToList();
    }


    /// <summary>下载最新版本到用户选择的文件夹。</summary>
    private static void _QuickDownloadToFolder(CompProject project, List<CompFile> files)
    {
        var file = _PickLatestFile(files);
        if (file is null)
        {
            HintService.Hint(Lang.Text("Download.Comp.QuickDownload.Hint.NoFile"), HintType.Info);
            return;
        }
        var saveFolder = ModBase.RunInUiWait(() =>
            SystemDialogs.SelectFolder(Lang.Text("Download.Comp.QuickDownload.Hint.SelectFolder")));
        if (string.IsNullOrWhiteSpace(saveFolder)) return; // 取消
        var target = Path.Combine(saveFolder, CompFileNameGet(project, file));
        _StartQuickDownload(file, target);
        HintService.Hint(Lang.Text("Download.Comp.QuickDownload.Hint.DownloadStarted", project.RawName), HintType.Success);
    }


    /// <summary>构造并启动单文件下载任务（与详情页 Save_Click 末段一致）。</summary>
    private static void _StartQuickDownload(CompFile file, string target)
    {
        var desc = file.Type switch
        {
            CompType.Mod => Lang.Text("Download.Comp.Type.Mod"),
            CompType.ResourcePack => Lang.Text("Download.Comp.Type.ResourcePack"),
            CompType.Shader => Lang.Text("Download.Comp.Type.Shader"),
            CompType.DataPack => Lang.Text("Download.Comp.Type.DataPack"),
            CompType.World => Lang.Text("Download.Comp.Type.World"),
            _ => Lang.Text("Download.Comp.Type.Mod")
        };
        var loaderName = Lang.Text("Download.Comp.Detail.DownloadResource", desc,
            ModBase.GetFileNameWithoutExtentionFromPath(target));
        var loaders = new List<ModLoader.LoaderBase>
        {
            new LoaderDownload(Lang.Text("Download.Comp.Detail.DownloadFile"),
                new List<DownloadFile>
                {
                    file.Type == CompType.Mod
                        ? file.ToNetFile(target)
                        : file.ToNetFile(target, DownloadReason.Standalone, null)
                })
            {
                ProgressWeight = 6,
                block = true
            }
        };
        if (file.Type == CompType.World)
        {
            var extractDir = Path.GetDirectoryName(target);
            loaders.Add(new ModLoader.LoaderTask<int, int>(
                Lang.Text("Download.Comp.Detail.InstallWorld"),
                _ => ModBase.ExtractFile(target, extractDir, Encoding.UTF8))
            {
                ProgressWeight = 0.1d,
                block = true
            });
            loaders.Add(new ModLoader.LoaderTask<int, int>(
                Lang.Text("Download.Comp.Detail.CleanCache"),
                _ => System.IO.File.Delete(target)));
        }
        var loader = new ModLoader.LoaderCombo<int>(loaderName, loaders)
        {
            OnStateChanged = ModDownloadLib.LoaderStateChangedHintOnly
        };
        loader.Start(1);
        ModLoader.LoaderTaskbarAdd(loader);
        ModMain.frmMain.BtnExtraDownload.ShowRefresh();
        ModMain.frmMain.BtnExtraDownload.Ribble();
    }


    /// <summary>根据资源类型返回实例内的目标子文件夹（与 Save_Click 一致）。</summary>
    private static string _GetSubFolder(CompType type) => type switch
    {
        CompType.Mod => "mods\\",
        CompType.ResourcePack => "resourcepacks\\",
        CompType.Shader => "shaderpacks\\",
        CompType.World => "saves\\",
        _ => ""
    };


    /// <summary>取文件自身声明的加载器，缺失时回退到工程的加载器。</summary>
    private static List<CompLoaderType> _ResolveLoaders(CompFile file, CompProject project)
        => file.ModLoaders.Count > 0 ? file.ModLoaders : project.ModLoaders;


    /// <summary>
    /// 按资源类型筛选文件，与详情页 GetResults 一致：Modrinth 会返回 Mod / 服务端插件 / 数据包混合的列表，
    /// 需过滤回当前类型，避免快速下载到另一种产物。光影与资源包不筛（原版光影以资源包格式发布）。
    /// </summary>
    private static List<CompFile> FilterFilesByType(List<CompFile> files, CompType type)
    {
        if (type == CompType.Shader || type == CompType.ResourcePack)
            return files;
        return files.Where(f => f.Type == type).ToList();
    }


    /// <summary>判断某实例是否兼容该文件（基于 Save_Click 的 isVersionSuitable，补全了 Quilt 判定）。</summary>
    public static bool IsInstanceSuitableForFile(McInstance? version, CompFile file, List<CompLoaderType> allowedLoaders)
    {
        if (version is null) return false;
        if (!version.IsLoaded) version.Load();

        // 只对 Mod 和数据包进行版本检测
        if (file.Type == CompType.Mod || file.Type == CompType.DataPack)
            if (file.GameVersions.Any(v => v.Contains(".")) &&
                !file.GameVersions.Any(v => v.Contains(".") && v == version.Info.VanillaName))
                return false;

        // 加载器判定
        if (allowedLoaders.Count == 0) return true; // 无要求
        if (allowedLoaders.Contains(CompLoaderType.Forge) && version.Info.HasForge) return true;
        if (allowedLoaders.Contains(CompLoaderType.Forge) && version.Info.HasCleanroom) 
            return true;
        if (allowedLoaders.Contains(CompLoaderType.Fabric) &&
            (version.Info.HasFabric || version.Info.HasLegacyFabric)) return true;
        if (allowedLoaders.Contains(CompLoaderType.NeoForge) && version.Info.HasNeoForge) return true;
        if (allowedLoaders.Contains(CompLoaderType.Quilt) && version.Info.HasQuilt) return true;
        if (allowedLoaders.Contains(CompLoaderType.LiteLoader) && version.Info.HasLiteLoader) return true;
        return false;
    }


    /// <summary>挑选最新文件：优先 Release，其次按发布日期最新。compatFilter 为空时不做兼容过滤。</summary>
    private static CompFile? _PickLatestFile(List<CompFile> files, Func<CompFile, bool>? compatFilter = null)
    {
        var candidates = (compatFilter is null ? files : files.Where(compatFilter)).ToList();
        if (candidates.Count == 0) return null;
        return candidates
            .OrderByDescending(f => f.Status == CompFileStatus.Release)
            .ThenByDescending(f => f.ReleaseDate)
            .First();
    }



    /// <summary>
    /// 预载包含大量 CompFile 的卡片，添加必要的元素和前置列表。
    /// 前置列表（必要 / 可选）会被放入可折叠栏：必要前置默认展开，可选前置默认收起。
    /// </summary>
    public static void CompFilesCardPreload(StackPanel stack, List<CompFile> files)
    {
        // 获取卡片对应的前置 ID
        // 如果为整合包就不会有 Dependencies 信息，所以不用管
        var deps = files.SelectMany(f => f.Dependencies).Distinct().ToList();
        var optionalDeps = files.SelectMany(f => f.OptionalDependencies).Distinct().ToList();
        if (!deps.Any() && !optionalDeps.Any())
            return;

        // 必要前置：默认展开
        _AddDependencyBar(stack, deps,
            Lang.Text("Download.Comp.Detail.FileList.RequiredDependencies"), collapsed: false);
        // 可选前置：默认收起（库 Mod 可能有大量可选前置，参见 Issue #2873）
        _AddDependencyBar(stack, optionalDeps,
            Lang.Text("Download.Comp.Detail.FileList.OptionalDependencies"), collapsed: true);

        // 添加结尾间隔（版本列表标题）
        stack.Children.Add(new TextBlock
        {
            Text = Lang.Text("Download.Comp.Detail.FileList.VersionList"), FontSize = 14d,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6d, 12d, 0d, 5d)
        });
    }


    /// <summary>
    /// 将一组前置依赖（按工程 ID）渲染为一个可折叠栏并加入 <paramref name="stack"/>。
    /// 仅保留在 compProjectCache 中有信息的前置；若过滤后为空则不添加任何折叠栏。
    /// </summary>
    /// <param name="collapsed">是否默认收起。前置 item 全部加入即可，靠 MyVirtualizingElement 在可见时才实例化。</param>
    private static void _AddDependencyBar(StackPanel stack, List<string> depIds, string title, bool collapsed)
    {
        if (depIds is null || !depIds.Any())
            return;

        depIds.Sort();
        var projects = new List<CompProject>();
        foreach (var dep in depIds)
        {
            if (compProjectCache.TryGetValue(dep, out var project))
                projects.Add(project);
            else
                ModBase.Log($"[Comp] 未找到 ID {dep} 的前置信息", ModBase.LogLevel.Debug);
        }
        if (!projects.Any())
            return;

        var bar = new MyCollapseBar
        {
            Title = $"{title} ({projects.Count})",
            IsCollapsed = collapsed
        };
        foreach (var project in projects)
            bar.ContentPanel.Children.Add(project.ToCompItem(false, false));

        stack.Children.Add(bar);
    }

}
