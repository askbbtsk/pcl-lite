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
    ///     进入指定的文件夹。
    /// </summary>
    private void EnterFolder(string folderPath)
    {
        try
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            {
                HintService.Hint(Lang.Text("Instance.Saves.Folder.NotFound"), HintType.Error);
                return;
            }

            CurrentFolderPath = folderPath;
            ModBase.Log($"[原理图] 进入文件夹：{folderPath}");

            ModLoader.LoaderFolderRun(ModLocalComp.compResourceListLoader, folderPath,
                ModLoader.LoaderFolderRunType.ForceRun, loaderInput: GetRequireLoaderData());
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "进入文件夹失败",
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Instance.Resource.Error.OperationFailed"));
        }
    }


    /// <summary>
    ///     进入指定文件夹。
    /// </summary>
    private void EnterFolderWithCheck(string folderPath)
    {
        try
        {
            EnterFolder(folderPath);
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "进入文件夹失败",
                ModBase.LogLevel.Msgbox,
                userSummary: Lang.Text("Instance.Resource.Error.OperationFailed"));
        }
    }


    /// <summary>
    ///     返回上级文件夹。
    /// </summary>
    private void GoBackToParentFolder()
    {
        if (string.IsNullOrEmpty(CurrentFolderPath))
            return;

        try
        {
            // 获取根路径
            var rootPath = ModLocalComp.GetResourceDirectory(PageInstanceLeft.McInstance, currentCompType);
            rootPath = Path.GetFullPath(rootPath.TrimEnd('\\'));

            // 获取父级路径
            var parentPath = Directory.GetParent(CurrentFolderPath)?.FullName;

            // 如果父级路径就是根路径或者父级路径不在根路径范围内，则返回根目录
            if (parentPath is null || parentPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase) ||
                !parentPath.StartsWith(rootPath + @"\", StringComparison.OrdinalIgnoreCase))
                CurrentFolderPath = "";
            else
                CurrentFolderPath = parentPath;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "路径处理失败");
            // 发生错误时直接返回根目录
            CurrentFolderPath = "";
        }

        ModBase.Log($"[原理图] 返回上级文件夹：{(string.IsNullOrEmpty(CurrentFolderPath) ? "根目录" : CurrentFolderPath)}");

        // 重新加载当前文件夹的内容
        string loadPath;
        if (string.IsNullOrEmpty(CurrentFolderPath))
            // 返回到根目录
            loadPath = ModLocalComp.GetResourceDirectory(PageInstanceLeft.McInstance, currentCompType);
        else
            // 加载当前文件夹
            loadPath = CurrentFolderPath;

        // 强制刷新UI状态
        // 确保按钮状态正确
        ModBase.RunInUi(() =>
            BtnManageBack.Visibility =
                !string.IsNullOrEmpty(CurrentFolderPath) ? Visibility.Visible : Visibility.Collapsed);

        // 延迟一帧后再加载，确保UI状态已更新
        ModBase.RunInUi(
            () => ModLoader.LoaderFolderRun(ModLocalComp.compResourceListLoader, loadPath,
                ModLoader.LoaderFolderRunType.ForceRun, loaderInput: GetRequireLoaderData()), true);
    }

}
