using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using FluentValidation;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.Minecraft.ResourceProject;
using PCL.Core.UI;
using PCL.Core.Utils;
using PCL.Core.Utils.Validate;
using PCL.Network;
using PCL.Network.Loaders;
using Control = System.Windows.Forms.Control;

namespace PCL;

public partial class PageDownloadCompDetail
{

    private void Init()
    {
        ModAnimation.AniControlEnabled += 1;
        _project = ModMain.frmMain.pageCurrent.additional.Value.CompProject;
        PanBack.ScrollToHome();
        // 重启加载器
        if (_isFirstInit)
            // 在 Me.Initialized 已经初始化了加载器，不再重复初始化
            _isFirstInit = false;
        else
            PageLoaderRestart(isForceRestart: true);
        // 放置当前工程
        if (_compItem is not null)
            PanIntro.Children.Remove(_compItem);
        _compItem = _project.ToCompItem(true, true);
        _compItem.CanInteraction = false;
        _compItem.ShowFavoriteBtn = false;
        _compItem.Margin = new Thickness(-7, -7, 0d, 8d);
        PanIntro.Children.Insert(0, _compItem);

        // 决定按钮显示
        BtnIntroWeb.Text = _project.FromCurseForge ? "CurseForge" : "Modrinth";
        BtnIntroWiki.Visibility = Lang.IsChineseMainland && _project.WikiId != 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        BtnTranslate.Visibility = Lang.IsChineseMainland
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshFavoriteButton();

        ModAnimation.AniControlEnabled -= 1;
    }


    // 整合包安装
    public void Install_Click(MyListItem sender, EventArgs e)
    {
        if (!RestrictedFeaturePolicy.Check("online-modpack-install")) return;
        try
        {
            // 获取基本信息
            var file = (ModComp.CompFile)sender.Tag;
            var loaderName =
                $"{(_project.FromCurseForge ? "CurseForge" : "Modrinth")} {Lang.Text("Download.Comp.Detail.ModpackDownload")}：{_project.TranslatedName} ";

            // 获取实例名
            var packName = _project.TranslatedName.Replace(".zip", "").Replace(".rar", "").Replace(".mrpack", "")
                .Replace(@"\", "＼").Replace("/", "／").Replace("|", "｜").Replace(":", "：").Replace("<", "＜")
                    .Replace(">", "＞").Replace("*", "＊").Replace("?", "？").Replace("\"", "").Replace("： ", "：");
            var validate = new FolderNameValidator(ModFolder.mcFolderSelected + "versions");
            if (!validate.Validate(packName).IsValid)
                packName = "";
            var instanceName = ModMain.MyMsgBoxInput(Lang.Text("Download.Comp.Detail.InputInstanceName"), "", packName, [validate]);
            if (string.IsNullOrEmpty(instanceName))
                return;

            // 构造步骤加载器
            var loaders = new List<ModLoader.LoaderBase>();
            var target =
                $@"{ModFolder.mcFolderSelected}versions\{instanceName}\原始整合包.{(_project.FromCurseForge ? "zip" : "mrpack")}";
            var logoFileAddress = MyImage.GetTempPath(_compItem.Logo);
            loaders.Add(new LoaderDownload(Lang.Text("Download.Comp.Detail.DownloadModpackFile"),
                    new List<DownloadFile> { file.ToNetFile(target, ModComp.DownloadReason.ModPack) })
                { ProgressWeight = 10d, block = true });
            loaders.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Download.Comp.Detail.PrepareModpackInstall"),
                _ => ModModpack.ModpackInstall(target, instanceName,
                    System.IO.File.Exists(logoFileAddress) ? logoFileAddress : null, file.ProjectId,
                    true)) { ProgressWeight = 0.1d });

            // 启动
            var loader = new ModLoader.LoaderCombo<string>(loaderName, loaders)
            {
                OnStateChanged = myLoader =>
                {
                    switch (myLoader.State)
                    {
                        case ModBase.LoadState.Failed:
                        {
                            HintService.Hint(
                                Lang.Text("Download.Comp.Detail.Task.Failed", myLoader.name,
                                    myLoader.Error.ToString()), HintType.Error);
                            break;
                        }
                        case ModBase.LoadState.Aborted:
                        {
                            HintService.Hint(Lang.Text("Download.Comp.Detail.Task.Cancelled", myLoader.name));
                            break;
                        }
                        case ModBase.LoadState.Loading:
                        {
                            return; // 不重新加载版本列表
                        }
                    }

                    ModDownloadLib.McInstallFailedClearFolder(myLoader);
                }
            };
            loader.Start(Path.Combine(ModFolder.mcFolderSelected, "versions", instanceName));
            ModLoader.LoaderTaskbarAdd(loader);
            ModMain.frmMain.BtnExtraDownload.ShowRefresh();
            ModMain.frmMain.BtnExtraDownload.Ribble();
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "下载资源整合包失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Download.Comp.Error.OperationFailed"));
        }
    }


    // 世界下载
    public void InstallWorld_Click(MyListItem sender, EventArgs e)
    {
        try
        {
            // 获取基本信息
            var file = (ModComp.CompFile)sender.Tag;
            var loaderName = $"{(_project.FromCurseForge ? "CurseForge" : "Modrinth")} {Lang.Text("Download.Comp.Detail.WorldDownload")}：{_project.TranslatedName} ";

            // 确认默认保存位置
            string defaultFolder = null;
            var subFolder = @"saves\";
            Func<McInstance, bool> isVersionSuitable = null;
            // 获取资源所需的加载器
            var allowedLoaders = new List<ModComp.CompLoaderType>();
            if (file.ModLoaders.Any())
                allowedLoaders = file.ModLoaders;
            else if (_project.ModLoaders.Any()) allowedLoaders = _project.ModLoaders;
            ModBase.Log("[Comp] 世界要求的加载器种类：" + (allowedLoaders.Any() ? allowedLoaders.Join(" / ") : "无要求"));
            // 判断某个版本是否符合资源要求
            isVersionSuitable = version =>
            {
                if (version is null)
                    return false;
                if (!version.IsLoaded)
                    version.Load();
                if (file.GameVersions.Any(v => v.Contains(".")) && !file.GameVersions.Any(v =>
                        v.Contains(".") && (v ?? "") == (version.Info.VanillaName ?? "")))
                    return false;
                // 加载器
                if (!allowedLoaders.Any())
                    return true; // 无要求
                return false;
            };
            // 获取常规资源默认下载位置
            if (cachedFolder.ContainsKey(file.Type) && !string.IsNullOrEmpty(cachedFolder[file.Type]))
            {
                defaultFolder = cachedFolder.GetOrDefault(file.Type,
                    ModInstanceList.McMcInstanceSelected?.PathIndie ?? ModBase.exePath);
                ModBase.Log($"[Comp] 使用上次下载时的文件夹作为默认下载位置：{defaultFolder}");
            }
            else if (ModInstanceList.McMcInstanceSelected is not null && isVersionSuitable(ModInstanceList.McMcInstanceSelected))
            {
                defaultFolder = $"{ModInstanceList.McMcInstanceSelected.PathIndie}{subFolder}";
                Directory.CreateDirectory(defaultFolder);
                ModBase.Log($"[Comp] 使用当前实例作为默认下载位置：{defaultFolder}");
            }
            else
            {
                // 查找所有可能的实例
                var needLoad = ModInstanceList.mcInstanceListLoader.State != ModBase.LoadState.Finished;
                if (needLoad)
                {
                    HintService.Hint(Lang.Text("Download.Comp.Detail.FindingApplicableInstance"));
                    ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, ModFolder.mcFolderSelected,
                        ModLoader.LoaderFolderRunType.ForceRun, 1, @"versions\", true);
                }

                var suitableVersions = ModInstanceList.mcInstanceList.Values.SelectMany(l => l)
                    .Where(v => isVersionSuitable(v)).Select(v => new DirectoryInfo($"{v.PathIndie}{subFolder}"));
                if (suitableVersions.Any())
                {
                    var selectedVersion = suitableVersions
                        .OrderByDescending(dir => dir.Exists ? dir.LastWriteTimeUtc : DateTime.MinValue)
                        .ThenByDescending(dir => dir.Exists ? dir.GetFiles().Length : -1).First(); // 先按文件夹更改时间降序
                    // 再按文件夹中的文件数量降序
                    defaultFolder = selectedVersion.FullName;
                    Directory.CreateDirectory(defaultFolder);
                    ModBase.Log($"[Comp] 使用适合的游戏实例作为默认下载位置：{defaultFolder}");
                }
                else
                {
                    defaultFolder = ModFolder.mcFolderSelected;
                    if (needLoad)
                        HintService.Hint(Lang.Text("Download.Comp.Detail.NoApplicableInstance"));
                    else
                        ModBase.Log("[Comp] 由于当前实例不兼容，使用当前的 MC 文件夹作为默认下载位置");
                }
            }

            var target = SystemDialogs.SelectSaveFile(Lang.Text("Download.Comp.Detail.SelectWorldInstallLocation"), file.FileName, Lang.Text("Download.Comp.Detail.WorldFile.Filter"),
                defaultFolder);
            if (string.IsNullOrEmpty(target))
                return;

            // 构造步骤加载器
            var loaders = new List<ModLoader.LoaderBase>();
            var targetPath = target.BeforeLast(@"\");
            var logoFileAddress = MyImage.GetTempPath(_compItem.Logo);
            loaders.Add(new LoaderDownload(Lang.Text("Download.Comp.Detail.DownloadWorldFile"),
                    new List<DownloadFile>
                    {
                        file.ToNetFile(target, ModComp.DownloadReason.Standalone, file.RawGameVersions.FirstOrDefault())
                    })
                { ProgressWeight = 10d, block = true });
            loaders.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Download.Comp.Detail.InstallWorld"),
                _ => ModBase.ExtractFile(target, targetPath, Encoding.UTF8)) { ProgressWeight = 0.1d, block = true });
            loaders.Add(new ModLoader.LoaderTask<int, int>(Lang.Text("Download.Comp.Detail.CleanCache"),
                _ => System.IO.File.Delete(target)));

            // 启动
            var loader = new ModLoader.LoaderCombo<int>(loaderName, loaders)
                { OnStateChanged = ModDownloadLib.LoaderStateChangedHintOnly };
            loader.Start();
            ModLoader.LoaderTaskbarAdd(loader);
            ModMain.frmMain.BtnExtraDownload.ShowRefresh();
            ModMain.frmMain.BtnExtraDownload.Ribble();
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "下载世界资源失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Download.Comp.Error.OperationFailed"));
        }
    }


    public void Save_Click(object sender, EventArgs e)
    {
        // 获取点击项关联的文件对象
        var file = sender switch
        {
            FrameworkElement Element when Element.Tag is ModComp.CompFile CompFile => CompFile,
            FrameworkElement Element when Element.Parent is FrameworkElement Parent && Parent.Tag is ModComp.CompFile CompFile => CompFile,
            FrameworkElement Element when Element.Parent is FrameworkElement Parent && Parent.Parent is FrameworkElement GrandParent && GrandParent.Tag is ModComp.CompFile CompFile => CompFile,
            _ => null
        };

        ModBase.RunInNewThread(() =>
        {
            try
            {
                var desc = file.Type switch
                {
                    ModComp.CompType.ModPack => Lang.Text("Download.Comp.Type.Modpack"),
                    ModComp.CompType.Mod => Lang.Text("Download.Comp.Type.Mod"),
                    ModComp.CompType.ResourcePack => Lang.Text("Download.Comp.Type.ResourcePack"),
                    ModComp.CompType.Shader => Lang.Text("Download.Comp.Type.Shader"),
                    ModComp.CompType.DataPack => Lang.Text("Download.Comp.Type.DataPack"),
                    ModComp.CompType.World => Lang.Text("Download.Comp.Type.World"),
                    _ => ""
                };

                // 确认默认保存位置
                string defaultFolder = null;
                var allowedLoaders = new List<ModComp.CompLoaderType>();
                if (file.Type != ModComp.CompType.ModPack)
                {
                    var subFolder = "";
                    switch (file.Type)
                    {
                        case ModComp.CompType.Mod: subFolder = "mods\\"; break;
                        case ModComp.CompType.ResourcePack: subFolder = "resourcepacks\\"; break;
                        case ModComp.CompType.Shader: subFolder = "shaderpacks\\"; break;
                        case ModComp.CompType.World: subFolder = "saves\\"; break;
                        case ModComp.CompType.DataPack: subFolder = ""; break; // 导航到版本根目录
                    }

                    // 获取资源所需的加载器
                    if (file.ModLoaders.Any())
                        allowedLoaders = file.ModLoaders;
                    else if (_project.ModLoaders.Any()) allowedLoaders = _project.ModLoaders;
                    ModBase.Log(
                        $"[Comp] {desc}要求的加载器种类：{(allowedLoaders.Any() ? string.Join(" / ", allowedLoaders) : "无要求")}");

                    // 判断某个版本是否符合资源要求 (局部函数)
                    Func<McInstance, bool> isVersionSuitable = version =>
                    {
                        if (version is null) return false;
                        if (!version.IsLoaded) version.Load();

                        // 只对 Mod 和数据包进行版本检测
                        if (file.Type == ModComp.CompType.Mod || file.Type == ModComp.CompType.DataPack)
                            if (file.GameVersions.Any(v => v.Contains(".")) &&
                                !file.GameVersions.Any(v => v.Contains(".") && v == version.Info.VanillaName))
                                return false;

                        // 加载器判定
                        if (!allowedLoaders.Any()) return true; // 无要求
                        if (allowedLoaders.Contains(ModComp.CompLoaderType.Forge) && version.Info.HasForge) return true;
                        if (allowedLoaders.Contains(ModComp.CompLoaderType.Forge) && version.Info.HasCleanroom) 
                            return true;
                        if (allowedLoaders.Contains(ModComp.CompLoaderType.Fabric) &&
                            (version.Info.HasFabric || version.Info.HasLegacyFabric)) return true;
                        if (allowedLoaders.Contains(ModComp.CompLoaderType.NeoForge) && version.Info.HasNeoForge)
                            return true;
                        if (allowedLoaders.Contains(ModComp.CompLoaderType.LiteLoader) && version.Info.HasLiteLoader)
                            return true;
                        return false;
                    };

                    // 获取常规资源默认下载位置逻辑
                    if (cachedFolder.ContainsKey(file.Type) && !string.IsNullOrEmpty(cachedFolder[file.Type]))
                    {
                        defaultFolder = cachedFolder.GetOrDefault(file.Type,
                            ModInstanceList.McMcInstanceSelected?.PathIndie ?? ModBase.exePath);
                        ModBase.Log($"[Comp] 使用上次下载时的文件夹作为默认下载位置：{defaultFolder}");
                    }
                    else if (ModInstanceList.McMcInstanceSelected is not null &&
                             isVersionSuitable(ModInstanceList.McMcInstanceSelected))
                    {
                        defaultFolder = $"{ModInstanceList.McMcInstanceSelected.PathIndie}{subFolder}";
                        Directory.CreateDirectory(defaultFolder);
                        ModBase.Log($"[Comp] 使用当前实例作为默认下载位置：{defaultFolder}");
                    }
                    else
                    {
                        // 查找所有可能的实例
                        var needLoad = ModInstanceList.mcInstanceListLoader.State != ModBase.LoadState.Finished;
                        if (needLoad)
                        {
                            HintService.Hint(Lang.Text("Download.Comp.Detail.FindingApplicableInstance"));
                            ModLoader.LoaderFolderRun(ModInstanceList.mcInstanceListLoader, ModFolder.mcFolderSelected,
                                ModLoader.LoaderFolderRunType.ForceRun, 1, "versions\\", true);
                        }

                        var suitableVersions = ModInstanceList.mcInstanceList.Values.SelectMany(l => l)
                            .Where(v => isVersionSuitable(v))
                            .Select(v => new DirectoryInfo($"{v.PathIndie}{subFolder}"));

                        if (suitableVersions.Any())
                        {
                            var selectedVersion = suitableVersions
                                .OrderByDescending(dir => dir.Exists ? dir.LastWriteTimeUtc : DateTime.MinValue)
                                .ThenByDescending(dir => dir.Exists ? dir.GetFiles().Length : -1)
                                .First();
                            defaultFolder = selectedVersion.FullName;
                            Directory.CreateDirectory(defaultFolder);
                            ModBase.Log($"[Comp] 使用适合的游戏实例作为默认下载位置：{defaultFolder}");
                        }
                        else
                        {
                            defaultFolder = ModFolder.mcFolderSelected;
                            if (needLoad)
                                HintService.Hint(Lang.Text("Download.Comp.Detail.NoApplicableInstance"));
                            else
                                ModBase.Log("[Comp] 由于当前实例不兼容，使用当前的 MC 文件夹作为默认下载位置");
                        }
                    }
                }

                // 获取文件名并弹窗
                var fileName = ModComp.CompFileNameGet(_project, file);
                ModBase.RunInUi(() =>
                {
                    var target = SystemDialogs.SelectSaveFile(Lang.Text("Download.Comp.Detail.SelectSaveLocation"),
                        fileName, Lang.Text("Download.Comp.Detail.ResourceFile.Filter", desc) + "|" +
                                  (file.Type == ModComp.CompType.Mod
                                      ? file.FileName.EndsWith(".litemod") ? "*.litemod" : "*.jar"
                                      : file.FileName.EndsWith(".mrpack")
                                          ? "*.mrpack"
                                          : "*.zip"),
                        defaultFolder);

                    if (!target.Contains("\\")) return;

                    // 记录缓存路径
                    var targetDir = ModBase.GetPathFromFullPath(target);
                    if (target != defaultFolder)
                    {
                        if (cachedFolder.ContainsKey(file.Type))
                            cachedFolder[file.Type] = targetDir;
                        else
                            cachedFolder.Add(file.Type, targetDir);
                    }

                    if (file.Type == ModComp.CompType.Mod && Config.Download.Comp.AutoInstallDependencies &&
                        file.Dependencies.Any())
                    {
                        try
                        {
                            McInstance? targetInstance = null;
                            var knownInstances = new List<McInstance>();
                            if (ModInstanceList.McMcInstanceSelected is not null)
                            {
                                knownInstances.Add(ModInstanceList.McMcInstanceSelected);
                            }

                            knownInstances.AddRange(ModInstanceList.mcInstanceList.Values.SelectMany(list => list)
                                .Where(instance => instance is not null));
                            targetInstance = knownInstances
                                .Distinct()
                                .FirstOrDefault(instance =>
                                    targetDir.StartsWith(instance.PathIndie, StringComparison.OrdinalIgnoreCase));
                            if (targetInstance is not null && !targetInstance.IsLoaded)
                            {
                                targetInstance.Load();
                            }

                            var mcVersion = targetInstance?.Info?.VanillaName
                                            ?? file.GameVersions.FirstOrDefault(version => version.Contains("."))
                                            ?? string.Empty;
                            var targetLoaders = new List<ModComp.CompLoaderType>();
                            if (targetInstance is not null)
                            {
                                if (targetInstance.Info.HasForge || targetInstance.Info.HasCleanroom)
                                    targetLoaders.Add(ModComp.CompLoaderType.Forge);
                                if (targetInstance.Info.HasFabric || targetInstance.Info.HasLegacyFabric)
                                    targetLoaders.Add(ModComp.CompLoaderType.Fabric);
                                if (targetInstance.Info.HasQuilt)
                                    targetLoaders.Add(ModComp.CompLoaderType.Quilt);
                                if (targetInstance.Info.HasNeoForge)
                                    targetLoaders.Add(ModComp.CompLoaderType.NeoForge);
                                if (targetInstance.Info.HasLiteLoader)
                                    targetLoaders.Add(ModComp.CompLoaderType.LiteLoader);
                            }

                            if (!targetLoaders.Any())
                            {
                                targetLoaders = allowedLoaders.ToList();
                            }

                            ModBase.Log($"[CompDeps] 开始解析必需前置: {file.Dependencies.Count} 个依赖");
                            var request = ModCompDependency.BuildRequest(file, _project, mcVersion, targetLoaders,
                                targetDir);
                            var resolver = new ModDependencyResolver();
                            var result = resolver.Resolve(request);

                            void DownloadDependencies()
                            {    
                                if (!result.ToInstall.Any())
                                {
                                    ModBase.Log("[CompDeps] 所有前置均无法解析，仅下载 Mod 本体");
                                    return;
                                }
                                
                                ModBase.Log($"[CompDeps] 准备下载: {result.ToInstall.Count} 个前置");
                                var depDownloads = ModCompDependency.BuildDependencyDownloads(result, targetDir);
                                foreach (var (depFilename, downloadFile) in depDownloads)
                                {
                                    var depLoaderName = Lang.Text("Download.Comp.Detail.DownloadResource", desc,
                                        ModBase.GetFileNameWithoutExtentionFromPath(depFilename));
                                    var depLoaders = new List<ModLoader.LoaderBase>
                                    {
                                        new LoaderDownload(Lang.Text("Download.Comp.Detail.DownloadFile"),
                                            new List<DownloadFile> { downloadFile })
                                        {
                                            ProgressWeight = 6,
                                            block = true
                                        }
                                    };

                                    // 启动加载器
                                    var depLoader = new ModLoader.LoaderCombo<int>(depLoaderName, depLoaders);
                                    depLoader.OnStateChanged = ModDownloadLib.LoaderStateChangedHintOnly;
                                    depLoader.Start(1);
                                    ModLoader.LoaderTaskbarAdd(depLoader);
                                }
                            }
                            
                            if (result.Unresolved.Any() || result.ToInstall.Any())
                            {
                                var installChoice = ModCompDependency.ConfirmDependencyInstall(result);

                                switch (installChoice)
                                {
                                    case ModComp.CompDepsInstallTypes.Unresolved:
                                        ModBase.Log("[CompDeps] 发现无法解析的前置");
                                        DownloadDependencies();
                                        break;

                                    case ModComp.CompDepsInstallTypes.WithDeps:
                                        DownloadDependencies();
                                        break;

                                    case ModComp.CompDepsInstallTypes.WithoutDeps:
                                        ModBase.Log("[CompDeps] 用户选择仅下载 Mod 本体，跳过前置下载");
                                        break;

                                    case ModComp.CompDepsInstallTypes.Cancel:
                                        ModBase.Log("[CompDeps] 用户取消安装");
                                        return;

                                    default:
                                        ModBase.Log($"[CompDeps] 未知返回值: {installChoice} ，终止下载");
                                        return;
                                }
                            }
                            else
                            {
                                ModBase.Log("[CompDeps] 已满足: 所有必需前置已安装");
                            }
                        }
                        catch (Exception depEx)
                        {
                            ModBase.Log(depEx, "[CompDeps] 依赖解析失败，跳过前置安装");
                            var message = ExceptionDetails.Compose(
                                Lang.Text("Download.Comp.Dependency.ResolveFailed.Message"),
                                depEx);
                            ModMain.MyMsgBox(
                                message,
                                Lang.Text("Download.Comp.Dependency.ResolveFailed.Title"),
                                Lang.Text("Download.Comp.Dependency.ResolveFailed.Continue"),
                                isWarn: true,
                                forceWait: true);
                        }
                    }

                    // 构造下载任务
                    var loaderName = Lang.Text("Download.Comp.Detail.DownloadResource", desc,
                        ModBase.GetFileNameWithoutExtentionFromPath(target));
                    var loaders = new List<ModLoader.LoaderBase>
                    {
                        new LoaderDownload(Lang.Text("Download.Comp.Detail.DownloadFile"),
                            new List<DownloadFile>
                            {
                                file.Type == ModComp.CompType.Mod
                                    ? file.ToNetFile(target)
                                    : file.ToNetFile(target, ModComp.DownloadReason.Standalone, null)
                            })
                        {
                            ProgressWeight = 6,
                            block = true
                        }
                    };

                    // 启动加载器
                    var loader = new ModLoader.LoaderCombo<int>(loaderName, loaders);
                    loader.OnStateChanged = ModDownloadLib.LoaderStateChangedHintOnly;
                    loader.Start(1);
                    ModLoader.LoaderTaskbarAdd(loader);

                    ModMain.frmMain.BtnExtraDownload.ShowRefresh();
                    ModMain.frmMain.BtnExtraDownload.Ribble();
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    "保存资源文件失败",
                    ModBase.LogLevel.Feedback,
                    userSummary: Lang.Text("Download.Comp.Error.OperationFailed"));
            }
        }, "Download CompDetail Save");
    }


    private void BtnIntroWeb_Click(object sender, EventArgs e)
    {
        ModBase.OpenWebsite(_project.Website);
    }


    private void BtnIntroWiki_Click(object sender, EventArgs e)
    {
        ModBase.OpenWebsite("https://www.mcmod.cn/class/" + _project.WikiId + ".html");
    }


    private void BtnIntroCopy_Click(object sender, EventArgs e)
    {
        ModBase.ClipboardSet(_compItem.LabTitle.Text + _compItem.LabTitleRaw.Text);
    }


    private void BtnFavorites_Click(object sender, EventArgs e)
    {
        ModComp.CompFavorites.ShowMenu(_project, (UIElement)sender, RefreshFavoriteButton);
    }


    private void BtnIntroLinkCopy_Click(object sender, EventArgs e)
    {
        ModComp.CompClipboard.currentText = _project.Website;
        ModBase.ClipboardSet(_project.Website);
    }


    // 翻译简介
    private async void BtnTranslate_Click(object sender, EventArgs e)
    {
        HintService.Hint(Lang.Text("Download.Comp.Detail.DescriptionTranslating", _project.TranslatedName));
        var chineseDescription = await _project.ChineseDescription;
        if (chineseDescription is null)
            return;
        ModMain.MyMsgBox(Lang.Text("Download.Comp.Detail.DescriptionTranslationResult", _project.Description,
            chineseDescription));
    }


    /// <summary>
    ///     刷新收藏按钮的显示状态
    /// </summary>
    public void RefreshFavoriteButton()
    {
        try
        {
            if (_project is null) return;

            var isFavourite = ModComp.CompFavorites.IsFavourite(_project.Id);
            BtnFavorites.SvgIcon = isFavourite ? "lucide/heart-filled" : "lucide/heart";

            // 刷新顶部的项目卡片收藏状态
            if (_compItem is not null)
                _compItem.RefreshFavoriteStatus();
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "刷新收藏按钮状态时出错");
        }
    }

}
