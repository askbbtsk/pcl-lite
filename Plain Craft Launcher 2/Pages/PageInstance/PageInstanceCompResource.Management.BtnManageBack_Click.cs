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



    /// <summary>
    ///     打开 Mods 文件夹。
    /// </summary>
    private void BtnManageBack_Click(object sender, EventArgs e)
    {
        GoBackToParentFolder();
    }


    private void BtnHintBack_Click(object sender, EventArgs e)
    {
        GoBackToParentFolder();
    }


    private void BtnManageOpen_Click(object sender, EventArgs e)
    {
        try
        {
            string compFilePath;

            // 如果当前在子文件夹中，则打开当前子文件夹；否则打开根目录
            if (string.IsNullOrEmpty(CurrentFolderPath))
                // 打开根目录
                compFilePath = PageInstanceLeft.McInstance.PathIndie +
                               (PageInstanceLeft.McInstance.Info.HasLabyMod
                                   ? Path.Combine("labymod-neo", "fabric", PageInstanceLeft.McInstance.Info.VanillaName)
                                   : "") + ModLocalComp.GetPathNameByCompType(currentCompType) + @"\";
            else
                // 打开当前子文件夹
                compFilePath = CurrentFolderPath.EndsWith(@"\") ? CurrentFolderPath : CurrentFolderPath + @"\";
            Directory.CreateDirectory(compFilePath);
            ModBase.OpenExplorer(compFilePath);
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "打开 Mods 文件夹失败",
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Instance.Resource.Error.OperationFailed"));
        }
    }



    /// <summary>
    ///     全选。
    /// </summary>
    private void BtnManageSelectAll_Click(object sender, MouseButtonEventArgs e)
    {
        ChangeAllSelected(selectedMods.Count < PanList.Children.Count);
    }


    /// <summary>
    ///     安装 Mod。
    /// </summary>
    private void BtnManageInstall_Click(object sender, MouseButtonEventArgs e)
    {
        string[] fileList = null;
        switch (currentCompType)
        {
            case ModComp.CompType.Mod:
            {
                fileList = SystemDialogs.SelectFiles(
                    Lang.Text("Instance.Resource.Install.FileDialog.Mod.Filter"),
                    Lang.Text("Instance.Resource.Install.FileDialog.Mod.Title"));
                break;
            }
            case ModComp.CompType.ResourcePack:
            {
                fileList = SystemDialogs.SelectFiles(
                    Lang.Text("Instance.Resource.Install.FileDialog.ResourcePack.Filter"),
                    Lang.Text("Instance.Resource.Install.FileDialog.ResourcePack.Title"));
                break;
            }
            case ModComp.CompType.Shader:
            {
                fileList = SystemDialogs.SelectFiles(
                    Lang.Text("Instance.Resource.Install.FileDialog.Shader.Filter"),
                    Lang.Text("Instance.Resource.Install.FileDialog.Shader.Title"));
                break;
            }
            case ModComp.CompType.Schematic:
            {
                fileList = SystemDialogs.SelectFiles(
                    Lang.Text("Instance.Resource.Install.FileDialog.Schematic.Filter"),
                    Lang.Text("Instance.Resource.Install.FileDialog.Schematic.Title"));
                break;
            }
        }

        if (fileList is null || !fileList.Any())
            return;
        InstallCompFiles(fileList, currentCompType, CurrentFolderPath);
    }


    /// <summary>
    ///     尝试安装 Mod。
    ///     返回输入的文件是否为一个 Mod 文件，仅用于判断拖拽行为。
    /// </summary>
    public static bool InstallMods(IEnumerable<string> filePathList)
    {
        if (!filePathList.Any()) return false;

        // 1. Check file extension
        var firstFile = filePathList.First();
        var extension = firstFile.Split('.').LastOrDefault()?.ToLower();
        string[] allowedExtensions = { "jar", "litemod", "disabled", "old" };

        if (!allowedExtensions.Contains(extension)) return false;

        LogWrapper.Info("[System] 文件格式为 jar/litemod，尝试安装为 Mod");

        // 2. Check recycle bin
        if (firstFile.Contains(@":\$RECYCLE.BIN\"))
        {
            HintWrapper.Show(Lang.Text("Instance.Resource.Install.RestoreFromRecycleBin"), HintTheme.Error);
            return true;
        }

        // 3. Determine target instance
        var targetInstance = ModInstanceList.McMcInstanceSelected;
        if (ModMain.frmMain.pageCurrent == FormMain.PageType.InstanceSetup) targetInstance = PageInstanceLeft.McInstance;

        // 4. Validate instance status
        if (ModMain.frmMain.pageCurrent == FormMain.PageType.InstanceSelect || targetInstance is null ||
            !targetInstance.Modable)
        {
            HintWrapper.Show(Lang.Text("Instance.Resource.Install.SelectModableInstance"));
            return true;
        }

        // 5. Check if user confirmation is required
        var isModPage = ModMain.frmMain.pageCurrent == FormMain.PageType.InstanceSetup &&
                        ModMain.frmMain.PageCurrentSub == FormMain.PageSubType.VersionMod;

        if (!isModPage)
        {
            if (ModMain.MyMsgBox(Lang.Text("Instance.Resource.Install.ModConfirm.Message", targetInstance.Name), Lang.Text("Instance.Resource.Install.ModConfirm.Title"), Lang.Text("Common.Action.Confirm"), Lang.Text("Common.Action.Cancel")) !=
                1) return true;
        }

        // 6. Execution: Install Mods
        ExecuteModInstallation(targetInstance, filePathList, isModPage);

        return true;
    }


    private static void ExecuteModInstallation(McInstance targetMcInstance, IEnumerable<string> filePathList,
        bool refreshList)
    {
        // Path resolution logic
        var modPathSuffix = targetMcInstance.Info.HasLabyMod
            ? $@"labymod-neo\fabric\{targetMcInstance.Info.VanillaName}\"
            : "";
        var modFolder = $@"{targetMcInstance.PathIndie}{modPathSuffix}mods\";

        try
        {
            foreach (var modFile in filePathList)
            {
                var fileName = ModBase.GetFileNameFromPath(modFile)
                    .Replace(".disabled", "")
                    .Replace(".old", "");

                if (!fileName.Contains(".")) fileName += ".jar"; // Ensure extension (#4227)

                ModBase.CopyFile(modFile, Path.Combine(modFolder, fileName));
            }

            // Success hint
            if (filePathList.Count() == 1)
            {
                var installedName = ModBase.GetFileNameFromPath(filePathList.First()).Replace(".disabled", "")
                    .Replace(".old", "");
                HintWrapper.Show(Lang.Text("Instance.Resource.Install.SuccessSingle", installedName), HintTheme.Success);
            }
            else
            {
                HintWrapper.Show(Lang.Text("Instance.Resource.Install.SuccessMultiple", filePathList.Count(), Lang.Text("Download.Comp.Type.Mod")), HintTheme.Success);
            }

            // 7. Refresh list if necessary
            if (refreshList)
                ModLoader.LoaderFolderRun(ModLocalComp.compResourceListLoader,
                    modFolder,
                    ModLoader.LoaderFolderRunType.ForceRun,
                    loaderInput: ModMain.frmInstanceMod.GetRequireLoaderData()
                );
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "拷贝文件失败");
        }
    }


    /// <summary>
    ///     安装组件文件（Mod、资源包、光影包、投影文件等）。
    /// </summary>
    public static void InstallCompFiles(IEnumerable<string> filePathList, ModComp.CompType compType,
        string targetFolderPath = "")
    {
        if (!filePathList.Any())
            return;

        var extension = filePathList.First().AfterLast(".").ToLower();
        string[] validExtensions = null;
        var compTypeName = "";
        var compFolder = "";

        // 检查回收站：回收站中的文件有错误的文件名
        if (filePathList.First().Contains(@":\$RECYCLE.BIN\"))
        {
            HintService.Hint(Lang.Text("Instance.Resource.Install.RestoreFromRecycleBin"), HintType.Error);
            return;
        }

        // 获取并检查目标实例
        var targetInstance = ModInstanceList.McMcInstanceSelected;
        if (ModMain.frmMain.pageCurrent == FormMain.PageType.InstanceSetup)
            targetInstance = PageInstanceLeft.McInstance;

        // 根据组件类型设置相关参数
        switch (compType)
        {
            case ModComp.CompType.Mod:
            {
                validExtensions = new[] { "jar", "litemod", "disabled", "old" };
                compTypeName = "Mod";
                if (string.IsNullOrEmpty(targetFolderPath))
                    compFolder = targetInstance.PathIndie +
                                 (targetInstance.Info.HasLabyMod
                                     ? Path.Combine("labymod-neo", "fabric", targetInstance.Info.VanillaName)
                                     : "") + @"mods\";
                else
                    compFolder = targetFolderPath;

                break;
            }
            case ModComp.CompType.ResourcePack:
            {
                validExtensions = new[] { "zip" };
                compTypeName = Lang.Text("Download.Comp.Type.ResourcePack");
                if (string.IsNullOrEmpty(targetFolderPath))
                    compFolder = targetInstance.PathIndie + @"resourcepacks\";
                else
                    compFolder = targetFolderPath;

                break;
            }
            case ModComp.CompType.Shader:
            {
                validExtensions = new[] { "zip" };
                compTypeName = Lang.Text("Download.Comp.Type.Shader");
                if (string.IsNullOrEmpty(targetFolderPath))
                    compFolder = targetInstance.PathIndie + @"shaderpacks\";
                else
                    compFolder = targetFolderPath;

                break;
            }
            case ModComp.CompType.Schematic:
            {
                validExtensions = new[] { "litematic", "nbt", "schematic", "schem" };
                compTypeName = Lang.Text("Download.Comp.Type.Schematic");
                if (string.IsNullOrEmpty(targetFolderPath))
                    compFolder = targetInstance.PathIndie + @"schematics\";
                else
                    compFolder = targetFolderPath;

                break;
            }
        }

        // 检查文件扩展名
        if (!validExtensions.Contains(extension))
        {
            HintService.Hint(Lang.Text("Instance.Resource.Install.UnsupportedFormat", extension, compTypeName, string.Join(", ", validExtensions)),
                HintType.Error);
            return;
        }

        ModBase.Log($"[System] 文件为 {extension} 格式，尝试作为{compTypeName}安装");

        // 检查实例兼容性
        if (compType == ModComp.CompType.Mod && (ModMain.frmMain.pageCurrent == FormMain.PageType.InstanceSelect ||
                                                 targetInstance is null || !targetInstance.Modable))
        {
            HintService.Hint(Lang.Text("Instance.Resource.Install.SelectModableInstance"));
            return;
        }

        // 确认安装
        var currentPage = FormMain.PageSubType.VersionMod;
        switch (compType)
        {
            case ModComp.CompType.Mod:
            {
                currentPage = FormMain.PageSubType.VersionMod;
                break;
            }
            case ModComp.CompType.ResourcePack:
            {
                currentPage = FormMain.PageSubType.VersionResourcePack;
                break;
            }
            case ModComp.CompType.Shader:
            {
                currentPage = FormMain.PageSubType.VersionShader;
                break;
            }
            case ModComp.CompType.Schematic:
            {
                currentPage = FormMain.PageSubType.VersionSchematic;
                break;
            }
        }

        if (!(ModMain.frmMain.pageCurrent == FormMain.PageType.InstanceSetup &&
              ModMain.frmMain.PageCurrentSub == currentPage))
            if (ModMain.MyMsgBox(
                    Lang.Text("Instance.Resource.Install.GenericConfirm.Message", compTypeName, targetInstance.Name),
                    Lang.Text("Instance.Resource.Install.GenericConfirm.Title", compTypeName), Lang.Text("Common.Action.Confirm"), Lang.Text("Common.Action.Cancel")) != 1)
                return;

        // 执行安装
        try
        {
            Directory.CreateDirectory(compFolder);
            foreach (var FilePath in filePathList)
            {
                var newFileName = ModBase.GetFileNameFromPath(FilePath);
                if (compType == ModComp.CompType.Mod)
                {
                    newFileName = newFileName.Replace(".disabled", "").Replace(".old", "");
                    if (!newFileName.Contains("."))
                        newFileName += ".jar";
                }

                var destFile = compFolder + newFileName;
                if (File.Exists(destFile))
                    if (ModMain.MyMsgBox(Lang.Text("Instance.Resource.Install.OverwriteConfirm.Message", newFileName), Lang.Text("Instance.Resource.Install.OverwriteConfirm.Title"), Lang.Text("Common.Action.Overwrite"), Lang.Text("Common.Action.Cancel")) != 1)
                        continue;

                ModBase.CopyFile(FilePath, destFile);
            }

            if (filePathList.Count() == 1)
                HintService.Hint(Lang.Text("Instance.Resource.Install.SuccessSingle", ModBase.GetFileNameFromPath(filePathList.First())), HintType.Success);
            else
                HintService.Hint(Lang.Text("Instance.Resource.Install.SuccessMultiple", filePathList.Count(), compTypeName), HintType.Success);

            // 刷新列表
            if (ModMain.frmMain.pageCurrent == FormMain.PageType.InstanceSetup &&
                ModMain.frmMain.PageCurrentSub == currentPage)
                switch (compType)
                {
                    case ModComp.CompType.Mod:
                    {
                        if (ModMain.frmInstanceMod is not null)
                            ModLoader.LoaderFolderRun(ModLocalComp.compResourceListLoader, compFolder,
                                ModLoader.LoaderFolderRunType.ForceRun,
                                loaderInput: ModMain.frmInstanceMod?.GetRequireLoaderData());

                        break;
                    }
                    case ModComp.CompType.ResourcePack:
                    case ModComp.CompType.Shader:
                    case ModComp.CompType.Schematic:
                    {
                        var currentForm = GetCurrentCompResourceForm();
                        if (currentForm is not null) ModBase.RunInUi(() => currentForm.ReloadCompFileList(true));

                        break;
                    }
                }
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                $"复制{compTypeName}文件失败",
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Instance.Resource.Error.OperationFailed"));
        }
    }


    /// <summary>
    ///     获取当前的组件资源管理窗体。
    /// </summary>
    private static PageInstanceCompResource GetCurrentCompResourceForm()
    {
        switch (ModMain.frmMain.PageCurrentSub)
        {
            case FormMain.PageSubType.VersionMod:
            {
                return ModMain.frmInstanceMod;
            }
            case FormMain.PageSubType.VersionResourcePack:
            {
                return ModMain.frmInstanceResourcePack;
            }
            case FormMain.PageSubType.VersionShader:
            {
                return ModMain.frmInstanceShader;
            }
            case FormMain.PageSubType.VersionSchematic:
            {
                return ModMain.frmInstanceSchematic;
            }

            default:
            {
                return null;
            }
        }
    }


    private void BtnManageInfoExport_Click(object sender, MouseButtonEventArgs e)
    {
        var choice =
            ModMain.MyMsgBox(
                Lang.Text("Instance.Resource.Export.Mode.Message"), Lang.Text("Instance.Resource.Export.Mode.Title"), Lang.Text("Instance.Resource.Export.Mode.Txt"), Lang.Text("Instance.Resource.Export.Mode.Csv"), Lang.Text("Common.Action.Cancel"));

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
                    "导出资源信息失败",
                    ModBase.LogLevel.Msgbox,
                    userSummary: Lang.Text("Instance.Resource.Error.OperationFailed"));
            }
        }

        ;
        switch (choice)
        {
            case 1: // TXT
            {
                var exportContent = new List<string>();
                foreach (var ModEntity in ModLocalComp.compResourceListLoader.output)
                {
                    exportContent.Add(ModEntity.FileName);
                    _AppendEmbeddedForExport(exportContent, ModEntity.EmbeddedMods, 1);
                }
                ExportText(exportContent.Join("\r\n"), PageInstanceLeft.McInstance.Name + "已安装的资源信息.txt");
                break;
            }

            case 2: // CSV
            {
                var exportContent = new List<string>();
                exportContent.Add("文件名,资源名称,资源版本,此版本更新时间,Mod ID,对应平台工程 ID,文件大小（字节）,文件路径,内嵌模组");
                foreach (var ModEntity in ModLocalComp.compResourceListLoader.output)
                    exportContent.Add(
                        $"{ModEntity.FileName},{ModEntity.Comp?.TranslatedName},{ModEntity.Version},{ModEntity.compFile?.ReleaseDate},{ModEntity.ModId},{ModEntity.Comp?.Id},{GetModFileInfo(ModEntity.path).Length},{ModEntity.path},{string.Join(";", _FlattenEmbeddedNames(ModEntity.EmbeddedMods))}");
                ExportText(exportContent.Join("\r\n"), PageInstanceLeft.McInstance.Name + "已安装的资源信息.csv");
                break;
            }
        }
    }


    private static void _AppendEmbeddedForExport(List<string> lines, List<ModLocalComp.LocalCompFile> mods, int depth)
    {
        var indent = new string('\t', depth);
        foreach (var mod in mods)
        {
            var line = indent + "└ " + (mod.Name ?? mod.ModId ?? mod.FileName);
            if (!string.IsNullOrWhiteSpace(mod.Version))
                line += $" ({mod.Version})";
            lines.Add(line);
            if (mod.EmbeddedMods is { Count: > 0 })
                _AppendEmbeddedForExport(lines, mod.EmbeddedMods, depth + 1);
        }
    }


    private static IEnumerable<string> _FlattenEmbeddedNames(List<ModLocalComp.LocalCompFile> mods)
    {
        foreach (var mod in mods)
        {
            yield return mod.Name ?? mod.ModId ?? mod.FileName;
            if (mod.EmbeddedMods is { Count: > 0 })
                foreach (var child in _FlattenEmbeddedNames(mod.EmbeddedMods))
                    yield return child;
        }
    }


    /// <summary>
    ///     下载 Mod。
    /// </summary>
    private void BtnManageDownload_Click(object sender, MouseButtonEventArgs e)
    {
        switch (currentCompType)
        {
            case ModComp.CompType.Mod:
            {
                ModMain.frmMain.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadMod);
                break;
            }
            case ModComp.CompType.ResourcePack:
            {
                ModMain.frmMain.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadResourcePack);
                break;
            }
            case ModComp.CompType.Shader:
            {
                ModMain.frmMain.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadShader);
                break;
            }
        }

        PageComp.targetVersion = PageInstanceLeft.McInstance; // 将当前实例设置为筛选器
    }


    /// <summary>
    ///     下载投影Mod按钮点击事件。
    /// </summary>
    private void BtnSchematicDownloadMod_Click(object sender, MouseButtonEventArgs e)
    {
        ModMain.frmMain.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadMod);
        PageComp.targetVersion = PageInstanceLeft.McInstance; // 将当前实例设置为筛选器
    }


    /// <summary>
    ///     实例选择按钮点击事件。
    /// </summary>
    private void BtnSchematicVersionSelect_Click(object sender, MouseButtonEventArgs e)
    {
        ModMain.frmMain.PageChange(FormMain.PageType.Launch);
        ModMain.frmMain.PageChange(FormMain.PageType.InstanceSelect);
    }

}
