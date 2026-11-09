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



    /// <summary>
    ///     打开 datapacks 文件夹。
    /// </summary>
    private void BtnManageOpen_Click(object sender, EventArgs e)
    {
        try
        {
            var datapackPath = Path.Combine(PageInstanceSavesLeft.currentSave, "datapacks");
            Directory.CreateDirectory(datapackPath);
            ModBase.OpenExplorer(datapackPath);
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "打开 datapacks 文件夹失败",
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Instance.Saves.Error.OperationFailed"));
        }
    }


    /// <summary>
    ///     全选。
    /// </summary>
    private void BtnManageSelectAll_Click(object sender, MouseButtonEventArgs e)
    {
        ChangeAllSelected(selectedDatapacks.Count < PanList.Children.Count);
    }


    /// <summary>
    ///     安装数据包。
    /// </summary>
    private void BtnManageInstall_Click(object sender, MouseButtonEventArgs e)
    {
        var fileList = SystemDialogs.SelectFiles(
            Lang.Text("Instance.Saves.Datapack.Install.FileDialog.Filter"),
            Lang.Text("Instance.Saves.Datapack.Install.FileDialog.Title"));
        if (fileList is null || fileList.Length == 0)
            return;
        InstallDatapackFiles(fileList);
        Refresh();
    }


    /// <summary>
    ///     安装数据包文件。
    /// </summary>
    public static void InstallDatapackFiles(IEnumerable<string> filePathList)
    {
        if (!filePathList.Any())
            return;

        var extension = filePathList.First().AfterLast(".").ToLower();

        // 检查文件扩展名
        if (extension != "zip")
        {
            HintService.Hint(Lang.Text("Instance.Resource.Install.UnsupportedFormat", extension, Lang.Text("Download.Comp.Type.DataPack"), "zip"), HintType.Error);
            return;
        }

        // 检查回收站
        if (filePathList.First().Contains(@":\$RECYCLE.BIN\"))
        {
            HintService.Hint(Lang.Text("Instance.Resource.Install.RestoreFromRecycleBin"), HintType.Error);
            return;
        }

        ModBase.Log($"[System] 文件为 {extension} 格式，尝试作为数据包安装");

        // 确认安装
        if (!(ModMain.frmMain.pageCurrent == FormMain.PageType.InstanceSetup &&
              ModMain.frmMain.PageCurrentSub == FormMain.PageSubType.VersionSavesDatapack))
            if (ModMain.MyMsgBox(Lang.Text("Instance.Saves.Datapack.Install.Message"),
                    Lang.Text("Instance.Saves.Datapack.Install.Title"), Lang.Text("Common.Action.Confirm"),
                    Lang.Text("Common.Action.Cancel")) != 1)
                return;

        // 执行安装
        try
        {
            var datapackFolder = Path.Combine(PageInstanceSavesLeft.currentSave, "datapacks");
            Directory.CreateDirectory(datapackFolder);

            foreach (var FilePath in filePathList)
            {
                var newFileName = ModBase.GetFileNameFromPath(FilePath);
                var destFile = datapackFolder + newFileName;

                if (File.Exists(destFile))
                    if (ModMain.MyMsgBox(Lang.Text("Instance.Resource.Install.OverwriteConfirm.Message", newFileName), Lang.Text("Instance.Resource.Install.OverwriteConfirm.Title"), Lang.Text("Common.Action.Overwrite"), Lang.Text("Common.Action.Cancel")) != 1)
                        continue;

                ModBase.CopyFile(FilePath, destFile);
            }

            if (filePathList.Count() == 1)
                HintService.Hint(Lang.Text("Instance.Resource.Install.SuccessSingle", ModBase.GetFileNameFromPath(filePathList.First())), HintType.Success);
            else
                HintService.Hint(Lang.Text("Instance.Resource.Install.SuccessMultiple", filePathList.Count(), Lang.Text("Download.Comp.Type.DataPack")), HintType.Success);

            // 刷新列表
            if (ModMain.frmMain.pageCurrent == FormMain.PageType.InstanceSetup &&
                ModMain.frmMain.PageCurrentSub == FormMain.PageSubType.VersionSavesDatapack)
                if (ModMain.frmInstanceSavesDatapack is not null)
                    ModMain.frmInstanceSavesDatapack.ReloadDatapackFileList(true);
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "复制数据包文件失败",
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Instance.Saves.Error.OperationFailed"));
        }
    }


    /// <summary>
    ///     下载数据包。
    /// </summary>
    private void BtnManageDownload_Click(object sender, MouseButtonEventArgs e)
    {
        var datapackPath = Path.Combine(PageInstanceSavesLeft.currentSave, "datapacks");
        Directory.CreateDirectory(datapackPath);
        PageDownloadCompDetail.cachedFolder[ModComp.CompType.DataPack] = datapackPath;
        ModMain.frmMain.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadDataPack);
        PageComp.targetVersion = PageInstanceLeft.McInstance; // 将当前实例设置为筛选器
    }


    /// <summary>
    ///     导出信息。
    /// </summary>
    private void BtnManageInfoExport_Click(object sender, MouseButtonEventArgs e)
    {
        var choice =
            ModMain.MyMsgBox(
                Lang.Text("Instance.Saves.Datapack.Export.Mode.Message"),
                Lang.Text("Instance.Resource.Export.Mode.Title"), Lang.Text("Instance.Resource.Export.Mode.Txt"), Lang.Text("Instance.Resource.Export.Mode.Csv"), Lang.Text("Common.Action.Cancel"));

        void ExportText(string content, string fileName)
        {
            try
            {
                var savePath =
                    SystemDialogs.SelectSaveFile(Lang.Text("Instance.Resource.Export.SelectSaveLocation"), fileName, Lang.Text("Instance.Resource.Export.FilesFilter"));
                if (string.IsNullOrWhiteSpace(savePath)) return;
                File.WriteAllText(savePath, content, Encoding.UTF8);
                ModBase.OpenExplorer(savePath);
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    "导出数据包信息失败",
                    ModBase.LogLevel.Msgbox,
                    userSummary: Lang.Text("Instance.Saves.Error.OperationFailed"));
            }
        }

        ;
        switch (choice)
        {
            case 1: // TXT
            {
                var exportContent = new List<string>();
                foreach (var DatapackEntity in ModLocalComp.compResourceListLoader.output)
                    exportContent.Add(DatapackEntity.FileName);
                ExportText(exportContent.Join("\r\n"),
                    ModBase.GetFolderNameFromPath(PageInstanceSavesLeft.currentSave) + "的数据包信息.txt");
                break;
            }

            case 2: // CSV
            {
                var exportContent = new List<string>();
                exportContent.Add("文件名,数据包名称,数据包版本,此版本更新时间,工程 ID,文件大小（字节）,文件路径");
                foreach (var DatapackEntity in ModLocalComp.compResourceListLoader.output)
                    exportContent.Add(
                        $"{DatapackEntity.FileName},{DatapackEntity.Comp?.TranslatedName},{DatapackEntity.Version},{DatapackEntity.compFile?.ReleaseDate},{DatapackEntity.Comp?.Id},{GetDatapackFileInfo(DatapackEntity.path).Length},{DatapackEntity.path}");
                ExportText(exportContent.Join("\r\n"),
                    ModBase.GetFolderNameFromPath(PageInstanceSavesLeft.currentSave) + "的数据包信息.csv");
                break;
            }
        }
    }

}
