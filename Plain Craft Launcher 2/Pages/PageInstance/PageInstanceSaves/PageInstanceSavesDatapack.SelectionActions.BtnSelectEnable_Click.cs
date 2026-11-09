using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using PCL.Core.App;
using PCL.Core.UI;
using PCL.Core.UI.Theme;
using PCL.Network;
using PCL.Network.Loaders;
using FileSystem = Microsoft.VisualBasic.FileSystem;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PageInstanceSavesDatapack
{



    // 启用
    private void BtnSelectEnable_Click(object sender, ModBase.RouteEventArgs e)
    {
        ToggleDatapacks(
            ModLocalComp.compResourceListLoader.output.Where(m => selectedDatapacks.Contains(m.RawPath)).ToList(),
            true);
        ChangeAllSelected(false);
    }


    // 禁用
    private void BtnSelectDisable_Click(object sender, ModBase.RouteEventArgs e)
    {
        ToggleDatapacks(
            ModLocalComp.compResourceListLoader.output.Where(m => selectedDatapacks.Contains(m.RawPath)).ToList(),
            false);
        ChangeAllSelected(false);
    }


    /// <summary>
    ///     启用/禁用数据包（通过重命名文件夹为 .disabled）
    /// </summary>
    private void ToggleDatapacks(IEnumerable<ModLocalComp.LocalCompFile> datapackList, bool isEnable)
    {
        var isSuccessful = true;
        foreach (var DatapackE in datapackList)
        {
            var datapackEntity = DatapackE;
            string newPath = null;

            if (datapackEntity.State == ModLocalComp.LocalCompFile.LocalFileStatus.Fine && !isEnable)
                // 禁用 - 添加 .disabled 后缀
                newPath = datapackEntity.path + ".disabled";
            else if (datapackEntity.State == ModLocalComp.LocalCompFile.LocalFileStatus.Disabled && isEnable)
                // 启用 - 移除 .disabled 后缀
                newPath = datapackEntity.RawPath;
            else
                continue;

            // 重命名
            try
            {
                if (File.Exists(newPath))
                {
                    ModMain.MyMsgBox(Lang.Text("Instance.Saves.Datapack.Replace.FileNameConflict", ModBase.GetFileNameFromPath(newPath)));
                    continue;
                }

                FileSystem.Rename(datapackEntity.path, newPath);
            }
            catch (FileNotFoundException ex)
            {
                ModBase.Log(
                    ex,
                    $"未找到需要重命名的数据包（{datapackEntity.path ?? "null"}）",
                    ModBase.LogLevel.Feedback,
                    userSummary: Lang.Text("Instance.Saves.Error.OperationFailed"));
                ReloadDatapackFileList(true);
                return;
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, $"重命名数据包失败（{datapackEntity.path ?? "null"}）");
                isSuccessful = false;
            }

            // 更改 Loader 中的列表
            var newDatapackEntity = new ModLocalComp.LocalCompFile(newPath);
            newDatapackEntity.FromJson(datapackEntity.ToJson());
            if (ModLocalComp.compResourceListLoader.output.Contains(datapackEntity))
            {
                var indexOfLoader = ModLocalComp.compResourceListLoader.output.IndexOf(datapackEntity);
                ModLocalComp.compResourceListLoader.output.RemoveAt(indexOfLoader);
                ModLocalComp.compResourceListLoader.output.Insert(indexOfLoader, newDatapackEntity);
            }

            if (searchResult is not null && searchResult.Contains(datapackEntity))
            {
                var indexOfResult = searchResult.IndexOf(datapackEntity);
                searchResult.Remove(datapackEntity);
                searchResult.Insert(indexOfResult, newDatapackEntity);
            }

            // 更改 UI 中的列表
            try
            {
                var newItem = BuildLocalCompItem(newDatapackEntity);
                datapackItems[datapackEntity.RawPath] = newItem;
                var indexOfUi = PanList.Children.IndexOf(PanList.Children.OfType<MyLocalCompItem>()
                    .FirstOrDefault(i => ReferenceEquals(i.Entry, datapackEntity)));
                if (indexOfUi == -1)
                    continue;
                PanList.Children.RemoveAt(indexOfUi);
                PanList.Children.Insert(indexOfUi, newItem);
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    $"更新 UI 列表项失败：{datapackEntity.FileName}",
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Instance.Saves.Error.OperationFailed"));
            }
        }

        Dispatcher.Invoke(() => PanList.UpdateLayout(), DispatcherPriority.Background);

        if (isSuccessful)
        {
            RefreshBars();
        }
        else
        {
            HintService.Hint(Lang.Text("Instance.Saves.Datapack.ToggleWarning"), HintType.Error);
            ReloadDatapackFileList(true);
        }

        LoaderRun(ModLoader.LoaderFolderRunType.UpdateOnly);
    }


    // 更新
    private void BtnSelectUpdate_Click(object sender, ModBase.RouteEventArgs e)
    {
        var updateList = ModLocalComp.compResourceListLoader.output
            .Where(m => selectedDatapacks.Contains(m.RawPath) && m.CanUpdate).ToList();
        if (!updateList.Any())
            return;
        UpdateResource(updateList);
        ChangeAllSelected(false);
    }


    private static bool TryGetSafeDatapackUpdateFileName(ModComp.CompFile file, out string fileName)
    {
        fileName = file.FileName?.Trim() ?? "";
        if (string.IsNullOrEmpty(fileName))
            return false;

        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return false;

        if (fileName.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
            return false;

        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        return fileName == Path.GetFileName(fileName) && fileName != "." && fileName != "..";
    }


    private static bool TryBuildDatapackUpdatePath(string rootPath, string fileName, out string fullPath)
    {
        var fullRootPath = Path.GetFullPath(rootPath);
        if (!fullRootPath.EndsWith(Path.DirectorySeparatorChar.ToString()) &&
            !fullRootPath.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
            fullRootPath += Path.DirectorySeparatorChar;

        fullPath = Path.GetFullPath(Path.Combine(fullRootPath, fileName));
        return fullPath.StartsWith(fullRootPath, StringComparison.OrdinalIgnoreCase);
    }


    public void UpdateResource(IEnumerable<ModLocalComp.LocalCompFile> datapackList)
    {
        // 更新前警告
        if (!States.Hint.FunctionDatapackUpdate || datapackList.Count() >= 15)
        {
            if (ModMain.MyMsgBox(
                    Lang.Text("Instance.Saves.Datapack.Update.Warning.Message"),
                    Lang.Text("Instance.Saves.Datapack.Update.Warning.Title"), Lang.Text("Instance.Saves.Datapack.Update.Warning.Confirm"), Lang.Text("Common.Action.Cancel"), isWarn: true) == 1)
                States.Hint.FunctionDatapackUpdate = true;
            else
                return;
        }

        try
        {
            // 构造下载信息
            datapackList = datapackList.ToList(); // 防止刷新影响迭代器
            var fileList = new List<DownloadFile>();
            var fileCopyList = new Dictionary<string, string>();
            var updateEntryList = new List<ModLocalComp.LocalCompFile>();
            var tempRoot = Path.Combine(ModBase.pathTemp, "DownloadedComp");
            var datapackRoot = Path.Combine(PageInstanceSavesLeft.currentSave, "datapacks");
            var skippedUnsafeFileCount = 0;
            foreach (var Entry in datapackList)
            {
                var file = Entry.UpdateFile;
                if (!file.Available)
                    continue;
                if (!TryGetSafeDatapackUpdateFileName(file, out var safeFileName) ||
                    !TryBuildDatapackUpdatePath(tempRoot, safeFileName, out var tempAddress) ||
                    !TryBuildDatapackUpdatePath(datapackRoot, safeFileName, out var realAddress))
                {
                    skippedUnsafeFileCount++;
                    ModBase.Log($"[DatapackUpdate] 已跳过不安全的数据包更新文件名：{file.FileName}", ModBase.LogLevel.Debug);
                    continue;
                }

                // 添加到下载列表
                fileList.Add(file.ToNetFile(tempAddress, ModComp.DownloadReason.Update,
                    file.RawGameVersions.FirstOrDefault()));
                fileCopyList[tempAddress] = realAddress;
                updateEntryList.Add(Entry);
            }

            if (skippedUnsafeFileCount > 0)
                HintService.Hint(
                    Lang.Text("Instance.Saves.Datapack.Update.UnsafeFilesSkipped", skippedUnsafeFileCount),
                    HintType.Error);
            if (!fileList.Any())
                return;

            // 构造加载器
            var installLoaders = new List<ModLoader.LoaderBase>();
            var finishedFileNames = new List<string>();
            installLoaders.Add(
                new LoaderDownload(Lang.Text("Instance.Saves.Datapack.Update.Task.DownloadFiles"), fileList)
                    { ProgressWeight = updateEntryList.Count * 1.5d });

            installLoaders.Add(new ModLoader.LoaderTask<int, int>(
                Lang.Text("Instance.Saves.Datapack.Update.Task.ReplaceFiles"), _ =>
                {
                    try
                    {
                        foreach (var Entry in updateEntryList)
                            if (File.Exists(Entry.path))
                                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(Entry.path, UIOption.AllDialogs,
                                    Config.Launch.DeleteToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently);
                            else
                                ModBase.Log($"[DatapackUpdate] 未找到更新前的数据包文件，跳过对它的删除：{Entry.path}",
                                    ModBase.LogLevel.Debug);

                        foreach (var Entry in fileCopyList)
                        {
                            if (File.Exists(Entry.Value))
                            {
                                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(Entry.Value, UIOption.AllDialogs,
                                    Config.Launch.DeleteToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently);
                                ModBase.Log($"[Datapack] 更新后的数据包文件已存在，将按删除设置处理旧文件：{Entry.Value}", ModBase.LogLevel.Debug);
                            }

                            if (Directory.Exists(ModBase.GetPathFromFullPath(Entry.Value)))
                            {
                                File.Move(Entry.Key, Entry.Value);
                                finishedFileNames.Add(ModBase.GetFileNameFromPath(Entry.Value));
                            }
                            else
                            {
                                ModBase.Log($"[Datapack] 更新后的目标文件夹已被删除：{Entry.Value}", ModBase.LogLevel.Debug);
                            }
                        }
                    }
                    catch (OperationCanceledException ex)
                    {
                        ModBase.Log(ex, "替换旧版数据包文件时被主动取消");
                    }
                }));

            // 结束处理
            var loader = new ModLoader.LoaderCombo<IEnumerable<ModLocalComp.LocalCompFile>>(
                Lang.Text("Instance.Saves.Datapack.Update.Task.Title",
                    ModBase.GetFolderNameFromPath(PageInstanceSavesLeft.currentSave)), installLoaders);
            var pathDatapacks = Path.Combine(PageInstanceSavesLeft.currentSave, "datapacks");

            loader.OnStateChanged = _ =>
            {
                switch (loader.State)
                {
                    case ModBase.LoadState.Finished:
                    {
                        switch (finishedFileNames.Count)
                        {
                            case 0:
                            {
                                ModBase.Log("[DatapackUpdate] 没有数据包被成功更新");
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

                ModBase.Log($"[DatapackUpdate] 已从正在进行数据包更新的文件夹列表移除：{pathDatapacks}");
                updatingVersions.Remove(pathDatapacks);

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
                        ModBase.Log(ex, "清理数据包更新缓存失败");
                    }
                }, "Clean Datapack Update Cache", ThreadPriority.BelowNormal);
            };

            // 启动加载器
            ModBase.Log($"[DatapackUpdate] 开始更新 {datapackList.Count()} 个数据包：{pathDatapacks}");
            updatingVersions.Add(pathDatapacks);
            loader.Start();
            ModLoader.LoaderTaskbarAdd(loader);
            ModMain.frmMain.BtnExtraDownload.ShowRefresh();
            ModMain.frmMain.BtnExtraDownload.Ribble();
            ReloadDatapackFileList(true);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "初始化数据包更新失败");
        }
    }


    // 删除
    private void BtnSelectDelete_Click(object sender, ModBase.RouteEventArgs e)
    {
        DeleteDatapacks(ModLocalComp.compResourceListLoader.output.Where(m => selectedDatapacks.Contains(m.RawPath)));
        ChangeAllSelected(false);
    }


    private void DeleteDatapacks(IEnumerable<ModLocalComp.LocalCompFile> datapackList)
    {
        try
        {
            var isSuccessful = true;
            var isShiftPressed = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

            // 确认需要删除的文件
            datapackList = datapackList.SelectMany(target =>
            {
                if (target.State == ModLocalComp.LocalCompFile.LocalFileStatus.Fine)
                    return new[] { target.path, target.path + ".disabled" };

                return new[] { target.path, target.RawPath };
            }).Distinct().Where(m => File.Exists(m)).Select(m => new ModLocalComp.LocalCompFile(m)).ToList();

            // 实际删除文件
            foreach (var DatapackEntity in datapackList)
            {
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(DatapackEntity.path,
                        UIOption.OnlyErrorDialogs, !isShiftPressed && Config.Launch.DeleteToRecycleBin
                            ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently);
                }
                catch (OperationCanceledException ex)
                {
                    ModBase.Log(ex, "删除数据包被主动取消");
                    ReloadDatapackFileList(true);
                    return;
                }
                catch (Exception ex)
                {
                    ModBase.Log(
                        ex,
                        $"删除数据包失败（{DatapackEntity.path}）",
                        ModBase.LogLevel.Msgbox,
                        userSummary: Lang.Text("Instance.Saves.Error.OperationFailed"));
                    isSuccessful = false;
                }

                // 取消选中
                selectedDatapacks.Remove(DatapackEntity.RawPath);
                // 更改 Loader 和 UI 中的列表
                ModLocalComp.compResourceListLoader.output.Remove(DatapackEntity);
                searchResult?.Remove(DatapackEntity);
                datapackItems.Remove(DatapackEntity.RawPath);
                var indexOfUi = PanList.Children.IndexOf(PanList.Children.OfType<MyLocalCompItem>()
                    .FirstOrDefault(i => i.Entry.Equals(DatapackEntity)));
                if (indexOfUi >= 0)
                    PanList.Children.RemoveAt(indexOfUi);
            }

            RefreshBars();
            if (!isSuccessful)
            {
                HintService.Hint(Lang.Text("Instance.Saves.Datapack.Delete.FileOccupied"), HintType.Error);
                ReloadDatapackFileList(true);
            }
            else if (PanList.Children.Count == 0)
            {
                ReloadDatapackFileList(true);
            }
            else
            {
                RefreshBars();
            }

            if (!isSuccessful)
                return;
            if (isShiftPressed || !Config.Launch.DeleteToRecycleBin)
            {
                if (datapackList.Count() == 1)
                    HintService.Hint(Lang.Text("Instance.Saves.Datapack.Delete.PermanentSingle", datapackList.Single().FileName), HintType.Success);
                else
                    HintService.Hint(Lang.Text("Instance.Saves.Datapack.Delete.PermanentMultiple", datapackList.Count()), HintType.Success);
            }
            else if (datapackList.Count() == 1)
            {
                HintService.Hint(Lang.Text("Instance.Saves.Datapack.Delete.RecycleSingle", datapackList.Single().FileName), HintType.Success);
            }
            else
            {
                HintService.Hint(Lang.Text("Instance.Saves.Datapack.Delete.RecycleMultiple", datapackList.Count()), HintType.Success);
            }
        }
        catch (OperationCanceledException ex)
        {
            ModBase.Log(ex, "删除数据包被主动取消");
            ReloadDatapackFileList(true);
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "删除数据包出现未知错误",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Saves.Error.OperationFailed"));
            ReloadDatapackFileList(true);
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
            .Where(m => selectedDatapacks.Contains(m.RawPath) && m.Comp is not null).Select(i => i.Comp).ToList();
        ModComp.CompFavorites.ShowMenu(selected, (UIElement)sender);
    }


    // 分享
    private void BtnSelectShare_Click(object sender, ModBase.RouteEventArgs e)
    {
        var shareList = ModLocalComp.compResourceListLoader.output
            .Where(m => selectedDatapacks.Contains(m.RawPath) && m.Comp is not null).Select(i => i.Comp.Id).ToHashSet();
        ModBase.ClipboardSet(ModComp.CompFavorites.GetShareCode(shareList));
        ChangeAllSelected(false);
    }

}
