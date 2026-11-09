using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using PCL.Core.App;
using PCL.Core.Logging;
using PCL.Core.UI;
using PCL.Core.UI.Theme;
using PCL.Network;
using PCL.Network.Loaders;
using FileSystem = Microsoft.VisualBasic.FileSystem;
using SearchOption = System.IO.SearchOption;
using PCL.Core.App.Localization;
using PCL.Core.Utils;

namespace PCL;

public partial class PageInstanceCompResource
{



    // 启用 / 禁用
    private void BtnSelectED_Click(object sender, ModBase.RouteEventArgs e)
    {
        EDMods(ModLocalComp.compResourceListLoader.output.Where(m => selectedMods.Contains(m.RawPath)).ToList(),
            !sender.Equals(BtnSelectDisable));
        ChangeAllSelected(false);
    }


    private void EDMods(IEnumerable<ModLocalComp.LocalCompFile> modList, bool isEnable)
    {
        var list = modList.ToList();
        if (!isEnable)
        {
            if (!_ConfirmCascadeReady()) return;
            var affected = _JijFindAffected(list);
            if (affected.Count > 0)
            {
                var choice = _AskJijCascade(affected, false);
                if (choice is null) return;
                if (choice == 1) list = list.Concat(affected).ToList();
            }
        }

        EDModsCore(list, isEnable);
    }


    private void DeleteMods(IEnumerable<ModLocalComp.LocalCompFile> modList)
    {
        // 在弹出任何模态框之前捕获 Shift（永久删除意图），否则选完档位时 Shift 多半已松开
        var isShiftPressed = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        if (!_ConfirmCascadeReady()) return;
        var list = modList.ToList();
        var affected = _JijFindAffected(list);
        if (affected.Count > 0)
        {
            var choice = _AskJijCascade(affected, true);
            if (choice is null) return;
            switch (choice)
            {
                case 1:
                    if (!EDModsCore(affected, false)) return;
                    break;
                case 2:
                    // 先禁用依赖者；若后续删除部分失败，也不会留下启用但缺少前置的 Mod。
                    if (!EDModsCore(affected, false)) return;
                    list = list.Concat(affected).ToList();
                    break;
            }
        }

        DeleteModsCore(list, isShiftPressed);
    }


    // 后台内嵌（Jar-in-Jar）解析未完成时，级联反查可能遗漏内嵌依赖，禁用/删除前提示确认
    private bool _ConfirmCascadeReady()
    {
        if (currentCompType != ModComp.CompType.Mod || ModLocalComp.CompJijResolved) return true;
        if (ModMain.MyMsgBox(
            Lang.Text("Instance.Resource.Mod.JarInJar.ParsePending.Message"),
            Lang.Text("Instance.Resource.Mod.JarInJar.ParsePending.Title"),
            Lang.Text("Common.Action.Continue"), Lang.Text("Common.Action.Cancel"), isWarn: true) != 1)
            return false;

        // “继续”表示先同步补齐整份列表，再重新构建级联索引；不能在残缺 output 上直接计算 affected。
        var output = ModLocalComp.compResourceListLoader.output;
        if (output is null) return false;
        var completed = true;
        try
        {
            ModJarInJarCache.UseInstance(PageInstanceLeft.McInstance?.PathInstance);
            ModLocalComp.JijScanContext = new ModJarInJar.ScanContext();
            foreach (var mod in output.Where(m => !m.IsFolder && m.JijPending))
                completed &= mod.ResolveJijNow();
            if (completed)
            {
                ModJarInJarCache.Prune(output.Where(m => !m.IsFolder).Select(m => m.path));
                ModJarInJarCache.Flush();
            }
        }
        finally
        {
            ModLocalComp.JijScanContext = null;
            ModJarInJarCache.UseInstance(null);
        }

        ModLocalComp.CompJijResolved = completed;
        ModMain.frmInstanceModJarInJar?.OnJijResolved();
        if (!completed)
            HintService.Hint(Lang.Text("Instance.Resource.Ed.ToggleFailed"), HintType.Error);
        return completed;
    }


    private List<ModLocalComp.LocalCompFile> _JijFindAffected(List<ModLocalComp.LocalCompFile> targets)
    {
        if (currentCompType != ModComp.CompType.Mod) return new List<ModLocalComp.LocalCompFile>();
        var output = ModLocalComp.compResourceListLoader.output;
        if (output is null) return new List<ModLocalComp.LocalCompFile>();
        var index = new ModJarInJarIndex(output, PageInstanceLeft.McInstance?.Info?.VanillaName);
        return index.FindAffected(targets);
    }


    // 级联选择弹窗；返回 0=仅此项 / 1=连带禁用 /（删除时）2=连带删除；null=取消
    private int? _AskJijCascade(List<ModLocalComp.LocalCompFile> affected, bool isDelete)
    {
        var sels = new List<IMyRadio>
        {
            new MyRadioBox { Text = Lang.Text("Instance.Resource.Mod.JarInJar.Cascade.OnlySelf") },
            new MyRadioBox { Text = Lang.Text("Instance.Resource.Mod.JarInJar.Cascade.WithDisable") }
        };
        if (isDelete)
            sels.Add(new MyRadioBox { Text = Lang.Text("Instance.Resource.Mod.JarInJar.Cascade.WithDelete") });

        return ModMain.MyMsgBoxSelect(sels,
            Lang.Text(isDelete
                ? "Instance.Resource.Mod.JarInJar.Cascade.DeleteTitle"
                : "Instance.Resource.Mod.JarInJar.Cascade.DisableTitle", affected.Count),
            Lang.Text("Common.Action.Confirm"), Lang.Text("Common.Action.Cancel"), true);
    }


    private bool EDModsCore(IEnumerable<ModLocalComp.LocalCompFile> modList, bool isEnable)
    {
        var isSuccessful = true;
        foreach (var ModE in modList)
        {
            var modEntity = ModE; // 仅用于去除迭代变量无法修改的限制
            // 用户可能在 Phase 2 尚未完成时选择继续操作。重命名前先同步补齐该项，
            // 否则后台仍持有旧路径实体，而新实体会复制空树并永久丢失本次会话的 JIJ 状态。
            if (currentCompType == ModComp.CompType.Mod && modEntity.JijPending)
            {
                var resolved = false;
                try
                {
                    ModJarInJarCache.UseInstance(PageInstanceLeft.McInstance?.PathInstance);
                    ModLocalComp.JijScanContext = new ModJarInJar.ScanContext();
                    resolved = modEntity.ResolveJijNow();
                    if (resolved) ModJarInJarCache.Flush();
                }
                finally
                {
                    ModLocalComp.JijScanContext = null;
                    ModJarInJarCache.UseInstance(null);
                }
                if (!resolved)
                {
                    HintService.Hint(Lang.Text("Instance.Resource.Ed.ToggleFailed"), HintType.Error);
                    isSuccessful = false;
                    continue;
                }
            }
            string newPath = null;
            if (modEntity.State == ModLocalComp.LocalCompFile.LocalFileStatus.Fine && !isEnable)
                // 禁用
                newPath = modEntity.path + (File.Exists(modEntity.path + ".old") ? ".old" : ".disabled");
            else if (modEntity.State == ModLocalComp.LocalCompFile.LocalFileStatus.Disabled && isEnable)
                // 启用
                newPath = modEntity.RawPath;
            else
                continue;
            // 重命名
            try
            {
                if (File.Exists(newPath))
                {
                    if (File.Exists(modEntity.path))
                    {
                        // 同时存在两个名称的 Mod
                        if ((ModBase.GetFileMD5(modEntity.path) ?? "") != (ModBase.GetFileMD5(newPath) ?? ""))
                        {
                            ModMain.MyMsgBox(
                                Lang.Text("Instance.Resource.Ed.FileConflict.Message", newPath, modEntity.path),
                                Lang.Text("Instance.Resource.Ed.FileConflict"));
                            isSuccessful = false;
                            continue;
                        }
                    }
                    else
                    {
                        // 已经重命名过了
                        ModBase.Log("[Mod] Mod 的状态已被切换", ModBase.LogLevel.Debug);
                        continue;
                    }
                }

                File.Delete(newPath);
                FileSystem.Rename(modEntity.path, newPath);
            }
            catch (FileNotFoundException ex)
            {
                ModBase.Log(
                    ex,
                    $"未找到需要重命名的 Mod（{modEntity.path ?? "null"}）",
                    ModBase.LogLevel.Feedback,
                    userSummary: Lang.Text("Instance.Resource.Error.OperationFailed"));
                ReloadCompFileList(true);
                return false;
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"重命名 Mod 失败（{modEntity.path ?? "null"}）");
                isSuccessful = false;
                continue;
            }

            // 更改 Loader 中的列表
            var newModEntity = new ModLocalComp.LocalCompFile(newPath);
            newModEntity.FromJson(modEntity.ToJson());
            newModEntity.CopyLoadedStateFrom(modEntity); // 仅改名未改内容，复用已解析元数据，免得 UI 线程重新解压
            if (ModLocalComp.compResourceListLoader.output.Contains(modEntity))
            {
                var indexOfLoader = ModLocalComp.compResourceListLoader.output.IndexOf(modEntity);
                ModLocalComp.compResourceListLoader.output.RemoveAt(indexOfLoader);
                ModLocalComp.compResourceListLoader.output.Insert(indexOfLoader, newModEntity);
            }

            if (searchResult is not null && searchResult.Contains(modEntity)) // #4862
            {
                var indexOfResult = searchResult.IndexOf(modEntity);
                searchResult.Remove(modEntity);
                searchResult.Insert(indexOfResult, newModEntity);
            }

            // 更改 UI 中的列表
            try
            {
                var newItem = BuildLocalCompItem(newModEntity);
                modItems[modEntity.RawPath] = newItem;
                var indexOfUi = PanList.Children.IndexOf(PanList.Children.OfType<MyLocalCompItem>()
                    .FirstOrDefault(i => ReferenceEquals(i.Entry, modEntity)));
                if (indexOfUi == -1)
                    continue; // 因为未知原因 Mod 的状态已经切换完了
                PanList.Children.RemoveAt(indexOfUi);
                PanList.Children.Insert(indexOfUi, newItem);
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    $"更新 UI 列表项失败：{modEntity.FileName}",
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Instance.Resource.Error.OperationFailed"));
            }
        }

        Dispatcher.Invoke(() => PanList.UpdateLayout(), DispatcherPriority.Background);
        if (isSuccessful)
        {
            RefreshBars();
        }
        else
        {
            HintService.Hint(Lang.Text("Instance.Resource.Ed.ToggleFailed"), HintType.Error);
            ReloadCompFileList(true);
        }

        LoaderRun(ModLoader.LoaderFolderRunType.UpdateOnly);
        return isSuccessful;
    }


    // 更新
    private void BtnSelectUpdate_Click(object sender, ModBase.RouteEventArgs e)
    {
        var updateList = ModLocalComp.compResourceListLoader.output
            .Where(m => selectedMods.Contains(m.RawPath) && m.CanUpdate).ToList();
        if (!updateList.Any())
            return;
        UpdateResource(updateList);
        ChangeAllSelected(false);
    }


    public void UpdateResource(IEnumerable<ModLocalComp.LocalCompFile> modList)
    {
        // 更新前警告
        if (currentCompType == ModComp.CompType.Mod && (!States.Hint.UpdateMod || modList.Count() >= 15))
        {
            if (ModMain.MyMsgBox(
                    Lang.Text("Instance.Resource.Update.Warning.Message"),
                    Lang.Text("Instance.Resource.Update.Warning.Title"), Lang.Text("Instance.Resource.Update.Warning.Confirm"), Lang.Text("Common.Action.Cancel"), isWarn: true) == 1)
                States.Hint.UpdateMod = true;
            else
                return;
        }

        try
        {
            // 构造下载信息
            modList = modList.ToList(); // 防止刷新影响迭代器
            var fileList = new List<DownloadFile>();
            var fileCopyList = new Dictionary<string, string>();
            foreach (var Entry in modList)
            {
                var file = Entry.UpdateFile;
                if (!file.Available)
                    continue;
                // 确认更新后的文件名
                var currentReplaceName = Entry.compFile.FileName.Replace(".jar", "").Replace(".old", "")
                    .Replace(".disabled", "");
                var newestReplaceName = Entry.UpdateFile.FileName.Replace(".jar", "").Replace(".old", "")
                    .Replace(".disabled", "");
                var currentSegs = currentReplaceName.Split('-').ToList();
                var newestSegs = newestReplaceName.Split('-').ToList();
                var shortened = false;
                while (true) // 移除前导相同部分（不能移除所有相同项，这会导致例如 1.2-forge-2 和 1.3-forge-3 中间的 forge 被去掉，导致尝试替换 1.2-2）
                {
                    if (!currentSegs.Any() || !newestSegs.Any())
                        break;
                    if ((currentSegs.First() ?? "") != (newestSegs.First() ?? ""))
                        break;
                    currentSegs.RemoveAt(0);
                    newestSegs.RemoveAt(0);
                    shortened = true;
                }

                while (true) // 移除后导相同部分
                {
                    if (!currentSegs.Any() || !newestSegs.Any())
                        break;
                    if ((currentSegs.Last() ?? "") != (newestSegs.Last() ?? ""))
                        break;
                    currentSegs.RemoveAt(currentSegs.Count - 1);
                    newestSegs.RemoveAt(newestSegs.Count - 1);
                    shortened = true;
                }

                if (shortened && currentSegs.Any() && newestSegs.Any())
                {
                    currentReplaceName = currentSegs.Join("-");
                    newestReplaceName = newestSegs.Join("-");
                }

                // 添加到下载列表
                var tempAddress = ModBase.pathTemp + @"DownloadedComp\" +
                                  Entry.FileName.Replace(currentReplaceName, newestReplaceName);
                var realAddress = ModBase.GetPathFromFullPath(Entry.path) +
                                  Entry.FileName.Replace(currentReplaceName, newestReplaceName);
                fileList.Add(file.ToNetFile(tempAddress, ModComp.DownloadReason.Update));
                fileCopyList[tempAddress] = realAddress;
            }

            // 构造加载器
            var installLoaders = new List<ModLoader.LoaderBase>();
            var finishedFileNames = new List<string>();
            installLoaders.Add(new LoaderDownload(Lang.Text("Instance.Resource.Update.Task.DownloadFiles"), fileList)
                { ProgressWeight = modList.Count() * 1.5d }); // 每个 Mod 需要 1.5s
            installLoaders.Add(new ModLoader.LoaderTask<int, int>(
                Lang.Text("Instance.Resource.Update.Task.ReplaceFiles"), _ =>
            {
                try
                {
                    foreach (var Entry in modList)
                        if (File.Exists(Entry.path))
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(Entry.path, UIOption.AllDialogs,
                                Config.Launch.DeleteToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently);
                        else
                            ModBase.Log($"[CompUpdate] 未找到更新前的资源文件，跳过对它的删除：{Entry.path}", ModBase.LogLevel.Debug);

                    foreach (var Entry in fileCopyList)
                    {
                        if (File.Exists(Entry.Value))
                        {
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(Entry.Value, UIOption.AllDialogs,
                                Config.Launch.DeleteToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently);
                            ModBase.Log($"[Mod] 更新后的资源文件已存在，将按删除设置处理旧文件：{Entry.Value}", ModBase.LogLevel.Debug);
                        }

                        if (Directory.Exists(ModBase.GetPathFromFullPath(Entry.Value)))
                        {
                            File.Move(Entry.Key, Entry.Value);
                            finishedFileNames.Add(ModBase.GetFileNameFromPath(Entry.Value));
                        }
                        else
                        {
                            ModBase.Log($"[Mod] 更新后的目标文件夹已被删除：{Entry.Value}", ModBase.LogLevel.Debug);
                        }
                    }
                }
                catch (OperationCanceledException ex)
                {
                    ModBase.Log(ex, "替换旧版资源文件时被主动取消");
                }
            }));
            // 结束处理
            var loader =
                new ModLoader.LoaderCombo<IEnumerable<ModLocalComp.LocalCompFile>>(
                    Lang.Text("Instance.Resource.Update.Task.Title", PageInstanceLeft.McInstance.Name), installLoaders);
            var pathMods = PageInstanceLeft.McInstance.PathIndie +
                           (PageInstanceLeft.McInstance.Info.HasLabyMod
                               ? Path.Combine("labymod-neo", "fabric", PageInstanceLeft.McInstance.Info.VanillaName)
                               : "") + ModLocalComp.GetPathNameByCompType(currentCompType) + @"\";
            loader.OnStateChanged = _ =>
            {
                // 结果提示
                switch (loader.State)
                {
                    case ModBase.LoadState.Finished:
                    {
                        switch (finishedFileNames.Count)
                        {
                            case 0: // 一般是由于 Mod 文件被占用，然后玩家主动取消
                            {
                                ModBase.Log("[CompUpdate] 没有资源被成功更新");
                                break;
                            }
                            case 1:
                            {
                                HintService.Hint(Lang.Text("Instance.Resource.Update.SuccessSingle", finishedFileNames.Single()), HintType.Success);
                                break;
                            }

                            default:
                            {
                                HintService.Hint(Lang.Text("Instance.Resource.Update.SuccessMultiple", finishedFileNames.Count), HintType.Success);
                                break;
                            }
                        }

                        break;
                    }
                    case ModBase.LoadState.Failed:
                    {
                        HintService.Hint(Lang.Text("Instance.Resource.Update.Failed", loader.Error.Message), HintType.Error);
                        break;
                    }
                    case ModBase.LoadState.Aborted:
                    {
                        HintService.Hint(Lang.Text("Instance.Resource.Update.Aborted"));
                        break;
                    }

                    default:
                    {
                        return;
                    }
                }

                ModBase.Log($"[CompUpdate] 已从正在进行资源更新的文件夹列表移除：{pathMods}");
                updatingVersions.Remove(pathMods);
                // 清理缓存
                ModBase.RunInNewThread(() =>
                {
                    try
                    {
                        foreach (var TempFile in fileCopyList.Keys)
                            if (File.Exists(TempFile))
                                File.Delete(TempFile);
                    }
                    catch (Exception ex)
                    {
                        ModBase.Log(ex, "清理资源更新缓存失败");
                    }
                }, "Clean Comp Update Cache", ThreadPriority.BelowNormal);
            };
            // 启动加载器
            ModBase.Log($"[CompUpdate] 开始更新 {modList.Count()} 个资源：{pathMods}");
            updatingVersions.Add(pathMods);
            loader.Start();
            ModLoader.LoaderTaskbarAdd(loader);
            ModMain.frmMain.BtnExtraDownload.ShowRefresh();
            ModMain.frmMain.BtnExtraDownload.Ribble();
            ReloadCompFileList(true);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "初始化资源更新失败");
        }
    }


    // 删除
    private void BtnSelectDelete_Click(object sender, ModBase.RouteEventArgs e)
    {
        DeleteMods(ModLocalComp.compResourceListLoader.output.Where(m => selectedMods.Contains(m.RawPath)));
        ChangeAllSelected(false);
    }


    private void DeleteModsCore(IEnumerable<ModLocalComp.LocalCompFile> modList, bool isShiftPressed)
    {
        try
        {
            var isSuccessful = true;
            // 确认需要删除的文件
            // 文件夹只需要删除自身
            modList = modList.SelectMany(target =>
                {
                    if (target.IsFolder) return new[] { target.path };

                    if (target.State == ModLocalComp.LocalCompFile.LocalFileStatus.Fine)
                        return new[]
                            { target.path, target.path + (File.Exists(target.path + ".old") ? ".old" : ".disabled") };

                    return new[] { target.path, target.RawPath };
                }).Distinct()
                .Where(m => m.EndsWithF(@"\__FOLDER__", true)
                    ? Directory.Exists(m.Replace(@"\__FOLDER__", ""))
                    : File.Exists(m)).Select(m => new ModLocalComp.LocalCompFile(m)).ToList();
            // 实际删除文件
            foreach (var ModEntity in modList)
            {
                // 删除
                try
                {
                    var sourcePath = ModEntity.IsFolder ? ModEntity.ActualPath : ModEntity.path;
                    var recycle = !isShiftPressed && Config.Launch.DeleteToRecycleBin
                        ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;
                    if (ModEntity.IsFolder)
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(sourcePath, UIOption.OnlyErrorDialogs, recycle);
                    else
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(sourcePath, UIOption.OnlyErrorDialogs, recycle);
                }
                catch (OperationCanceledException ex)
                {
                    ModBase.Log(ex, "删除资源被主动取消");
                    ReloadCompFileList(true);
                    return;
                }
                catch (Exception ex)
                {
                    ModBase.Log(
                        ex,
                        $"删除资源失败（{ModEntity.path}）",
                        ModBase.LogLevel.Msgbox,
                        userSummary: Lang.Text("Instance.Resource.Error.OperationFailed"));
                    isSuccessful = false;
                    continue;
                }

                // 取消选中
                selectedMods.Remove(ModEntity.RawPath);
                // 更改 Loader 和 UI 中的列表
                ModLocalComp.compResourceListLoader.output.Remove(ModEntity);
                searchResult?.Remove(ModEntity);
                modItems.Remove(ModEntity.RawPath);
                var indexOfUi = PanList.Children.IndexOf(PanList.Children.OfType<MyLocalCompItem>()
                    .FirstOrDefault(i => i.Entry.Equals(ModEntity)));
                if (indexOfUi >= 0)
                    PanList.Children.RemoveAt(indexOfUi);
            }

            RefreshBars();
            if (!isSuccessful)
            {
                HintService.Hint(Lang.Text("Instance.Resource.Delete.Failed"), HintType.Error);
                ReloadCompFileList(true);
            }
            else if (PanList.Children.Count == 0)
            {
                ReloadCompFileList(true); // 删除了全部项目
            }
            else
            {
                RefreshBars();
            }

            // 显示结果提示
            if (!isSuccessful)
                return;
            if (isShiftPressed || !Config.Launch.DeleteToRecycleBin)
            {
                if (modList.Count() == 1)
                    HintService.Hint(Lang.Text("Instance.Resource.Delete.PermanentSingle", modList.Single().FileName), HintType.Success);
                else
                    HintService.Hint(Lang.Text("Instance.Resource.Delete.PermanentMultiple", modList.Count()), HintType.Success);
            }
            else if (modList.Count() == 1)
            {
                HintService.Hint(Lang.Text("Instance.Resource.Delete.RecycleSingle", modList.Single().FileName), HintType.Success);
            }
            else
            {
                HintService.Hint(Lang.Text("Instance.Resource.Delete.RecycleMultiple", modList.Count()), HintType.Success);
            }
        }
        catch (OperationCanceledException ex)
        {
            ModBase.Log(ex, "删除资源被主动取消");
            ReloadCompFileList(true);
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "删除资源出现未知错误",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Resource.Error.OperationFailed"));
            ReloadCompFileList(true);
        }

        LoaderRun(ModLoader.LoaderFolderRunType.UpdateOnly);
    }


    // 取消选择
    private void BtnSelectCancel_Click(object sender, ModBase.RouteEventArgs e)
    {
        ChangeAllSelected(false);
    }


    // 收藏
    private void BtnSelectFavorites_Click(object sender, ModBase.RouteEventArgs e)
    {
        var selected = ModLocalComp.compResourceListLoader.output
            .Where(m => selectedMods.Contains(m.RawPath) && m.Comp is not null).Select(i => i.Comp).ToList();
        ModComp.CompFavorites.ShowMenu(selected, (UIElement)sender);
    }


    // 分享
    private void BtnSelectShare_Click(object sender, ModBase.RouteEventArgs e)
    {
        var shareList = ModLocalComp.compResourceListLoader.output
            .Where(m => selectedMods.Contains(m.RawPath) && m.Comp is not null).Select(i => i.Comp.Id).ToHashSet();
        ModBase.ClipboardSet(ModComp.CompFavorites.GetShareCode(shareList));
        ChangeAllSelected(false);
    }

}
