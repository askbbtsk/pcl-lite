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



    // 详情
    public void Info_Click(object sender, EventArgs e)
    {
        try
        {
            var modEntry = ((MyLocalCompItem)(sender is MyIconButton iconButton ? iconButton.Tag : sender)).Entry;
            // 判断该 LabyMod 是否支持安装 Fabric Mod
            var moddedLabyMod = PageInstanceLeft.McInstance.Info.HasLabyMod && PageInstanceLeft.McInstance.Modable;
            // 加载失败信息
            if (modEntry.State == ModLocalComp.LocalCompFile.LocalFileStatus.Unavailable)
            {
                ModMain.MyMsgBox(
                    Lang.Text("Instance.Resource.Item.Info.FailedMessage.WithDetail",
                        modEntry.FileUnavailableReason.ToString()),
                    Lang.Text("Instance.Resource.Item.Info.FailedTitle"));
                return;
            }

            if (modEntry.Comp is not null)
            {
                // 跳转到 Mod 下载页面
                ModMain.frmMain.PageChange(new FormMain.PageStackData
                {
                    page = FormMain.PageType.CompDetail,
                    additional = (modEntry.Comp, new List<string>(), PageInstanceLeft.McInstance.Info.VanillaName,
                        PageInstanceLeft.McInstance.Info.HasForge ? ModComp.CompLoaderType.Forge :
                        PageInstanceLeft.McInstance.Info.HasNeoForge ? ModComp.CompLoaderType.NeoForge :
                        PageInstanceLeft.McInstance.Info.HasFabric || moddedLabyMod ? ModComp.CompLoaderType.Fabric :
                        ModComp.CompLoaderType.Any,
                        currentCompType, null)
                });
            }
            else
            {
                // 对于原理图文件，使用异步加载避免UI卡顿
                if (modEntry.path.EndsWithF(".litematic", true) || modEntry.path.EndsWithF(".schem", true) ||
                    modEntry.path.EndsWithF(".schematic", true) || modEntry.path.EndsWithF(".nbt", true))
                {
                    ShowSchematicInfoAsync(modEntry);
                    return;
                }

                // 获取信息
                var contentLines = new List<string>();

                // 检查是否为文件夹
                if (modEntry.IsFolder)
                {
                    // 处理文件夹详情
                    var folderPath = modEntry.ActualPath;
                    if (Directory.Exists(folderPath))
                    {
                        var fileCount = 0;
                        try
                        {
                            // 根据当前资源类型计算文件数量
                            switch (currentCompType)
                            {
                                case ModComp.CompType.Schematic:
                                {
                                    fileCount = new DirectoryInfo(folderPath)
                                        .EnumerateFiles("*", SearchOption.AllDirectories).Where(f =>
                                            ModLocalComp.LocalCompFile.IsCompFile(f.FullName,
                                                ModComp.CompType.Schematic)).Count();
                                    break;
                                }
                                case ModComp.CompType.Mod:
                                {
                                    fileCount = new DirectoryInfo(folderPath)
                                        .EnumerateFiles("*.jar", SearchOption.AllDirectories).Count();
                                    break;
                                }
                                case ModComp.CompType.ResourcePack:
                                {
                                    fileCount = new DirectoryInfo(folderPath)
                                        .EnumerateFiles("*.zip", SearchOption.AllDirectories).Count();
                                    break;
                                }
                                case ModComp.CompType.Shader:
                                {
                                    fileCount = new DirectoryInfo(folderPath)
                                        .EnumerateFiles("*.zip", SearchOption.AllDirectories).Count();
                                    break;
                                }

                                default:
                                {
                                    fileCount = new DirectoryInfo(folderPath)
                                        .EnumerateFiles("*", SearchOption.AllDirectories).Count();
                                    break;
                                }
                            }
                        }
                        catch (Exception)
                        {
                            fileCount = 0;
                        }

                        if (fileCount == 0)
                            contentLines.Add(Lang.Text("Instance.Resource.Item.Info.EmptyFolder") + "\r\n");
                        else if (fileCount == 1)
                            contentLines.Add(Lang.Text("Instance.Resource.Item.Info.ContainsOne") + "\r\n");
                        else
                            contentLines.Add(Lang.Text("Instance.Resource.Item.Info.ContainsMany", fileCount) + "\r\n");
                    }
                    else
                    {
                        contentLines.Add(Lang.Text("Instance.Resource.Item.Info.FolderNotFound") + "\r\n");
                    }

                    contentLines.Add(Lang.Text("Instance.Resource.Item.Info.Path", folderPath));
                }
                else
                {
                    // 处理普通文件详情
                    if (modEntry.Description is not null)
                        contentLines.Add(modEntry.Description + "\r\n");
                    if (modEntry.Authors is not null)
                        contentLines.Add(Lang.Text("Instance.Resource.Item.Info.Author", modEntry.Authors));
                    contentLines.Add(Lang.Text("Instance.Resource.Item.Info.File", modEntry.FileName, ModBase.GetString(GetModFileInfo(modEntry.path).Length)));
                    if (modEntry.Version is not null)
                        contentLines.Add(Lang.Text("Instance.Resource.Item.Info.Version", modEntry.Version));

                    // 原理图文件的详情信息已通过异步方法处理
                }

                // 只有普通文件才显示调试信息
                if (!modEntry.IsFolder)
                {
                    var debugInfo = new List<string>();
                    if (modEntry.ModId is not null) debugInfo.Add(Lang.Text("Instance.Resource.Item.Info.ModId", modEntry.ModId));
                    if (modEntry.Dependencies.Any())
                    {
                        debugInfo.Add(Lang.Text("Instance.Resource.Item.Info.Dependency"));
                        foreach (var Dep in modEntry.Dependencies)
                            debugInfo.Add(" - " + (Dep.Value is null
                                ? Dep.Key
                                : Lang.Text("Instance.Resource.Item.Info.DependencyVersion", Dep.Key, Dep.Value)));
                    }

                    if (debugInfo.Any())
                    {
                        contentLines.Add("");
                        contentLines.AddRange(debugInfo);
                    }
                }

                // 显示详情信息
                if (modEntry.IsFolder)
                {
                    // 文件夹只显示基本信息，不提供搜索功能
                    ModMain.MyMsgBox(contentLines.Join("\r\n"), modEntry.Name, Lang.Text("Instance.Resource.Item.Info.Return"));
                }
                else
                {
                    // 获取用于搜索的 Mod 名称
                    var modOriginalName = modEntry.Name.Replace(" ", "+");
                    var modSearchName = modOriginalName.Substring(0, 1);
                    for (int i = 1, loopTo = modOriginalName.Count() - 1; i <= loopTo; i++)
                    {
                        var isLastLower = modOriginalName[i - 1].ToString().ToLower()
                            .Equals(modOriginalName[i - 1].ToString());
                        var isCurrentLower = modOriginalName[i].ToString().ToLower()
                            .Equals(modOriginalName[i].ToString());
                        if (isLastLower && !isCurrentLower)
                            // 上一个字母为小写，这一个字母为大写
                            modSearchName += "+";
                        modSearchName += modOriginalName[i].ToString();
                    }

                    modSearchName = modSearchName.Replace("++", "+").Replace("pti+Fine", "ptiFine");
                    // 显示
                    if (currentCompType == ModComp.CompType.Schematic || !Lang.IsChineseMainland)
                    {
                        // 投影原理图文件或非中文区域不显示百科搜索选项
                        if (modEntry.Url is null)
                            ModMain.MyMsgBox(contentLines.Join("\r\n"), modEntry.Name, Lang.Text("Instance.Resource.Item.Info.Return"));
                        else if (ModMain.MyMsgBox(contentLines.Join("\r\n"), modEntry.Name, Lang.Text("Instance.Resource.Item.Info.OpenWebsite"), Lang.Text("Instance.Resource.Item.Info.Return")) ==
                                 1) ModBase.OpenWebsite(modEntry.Url);
                    }
                    // 其他资源类型保留百科搜索功能
                    else if (modEntry.Url is null)
                    {
                        if (ModMain.MyMsgBox(contentLines.Join("\r\n"), modEntry.Name, Lang.Text("Instance.Resource.Item.Info.McMod"), Lang.Text("Instance.Resource.Item.Info.Return")) == 1)
                            ModBase.OpenWebsite("https://www.mcmod.cn/s?key=" + modSearchName + "&site=all&filter=0");
                    }
                    else
                    {
                        switch (ModMain.MyMsgBox(contentLines.Join("\r\n"), modEntry.Name, Lang.Text("Instance.Resource.Item.Info.OpenWebsite"), Lang.Text("Instance.Resource.Item.Info.McMod"),
                                    Lang.Text("Instance.Resource.Item.Info.Return")))
                        {
                            case 1:
                            {
                                ModBase.OpenWebsite(modEntry.Url);
                                break;
                            }
                            case 2:
                            {
                                ModBase.OpenWebsite(
                                    "https://www.mcmod.cn/s?key=" + modSearchName + "&site=all&filter=0");
                                break;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "获取资源详情失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Resource.Error.OperationFailed"));
        }
    }


    // 打开文件所在的位置
    public void Open_Click(MyIconButton sender, EventArgs e)
    {
        try
        {
            var listItem = (MyLocalCompItem)sender.Tag;
            // 对于文件夹使用实际路径，对于文件使用原路径
            var targetPath = listItem.Entry.IsFolder ? listItem.Entry.ActualPath : listItem.Entry.path;
            ModBase.OpenExplorer(targetPath);
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "打开资源文件位置失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Resource.Error.OperationFailed"));
        }
    }


    // 删除
    public void Delete_Click(MyIconButton sender, EventArgs e)
    {
        var listItem = (MyLocalCompItem)sender.Tag;
        DeleteMods(new[] { listItem.Entry });
    }


    // 启用 / 禁用
    public void ED_Click(MyIconButton sender, EventArgs e)
    {
        var listItem = (MyLocalCompItem)sender.Tag;
        EDMods(new[] { listItem.Entry }, listItem.Entry.State == ModLocalComp.LocalCompFile.LocalFileStatus.Disabled);
    }


    /// <summary>
    ///     异步显示原理图详情信息，避免UI卡顿
    /// </summary>
    private void ShowSchematicInfoAsync(ModLocalComp.LocalCompFile modEntry)
    {
        // 显示加载提示
        HintService.Hint(Lang.Text("Instance.Resource.Item.Info.LoadingDetail"));

        // 在后台线程中加载NBT数据
        // 确保 NBT 数据已加载

        // 在 UI 线程中显示详情
        // 构建详情信息


        // 根据文件类型显示详细信息

        // 显示调试信息

        // 显示详情对话框


        // 记录错误日志但不显示错误提示，因为通用的文件状态检查已经处理了
        ModBase.RunInNewThread(() =>
        {
            try
            {
                modEntry.LoadNbtDataIfNeeded();
                ModBase.RunInUi(() =>
                {
                    try
                    {
                        var contentLines = new List<string>();
                        if (modEntry.Description is not null) contentLines.Add(modEntry.Description + "\r\n");
                        if (modEntry.Authors is not null) contentLines.Add(Lang.Text("Instance.Resource.Item.Info.Author", modEntry.Authors));
                        contentLines.Add(Lang.Text("Instance.Resource.Item.Info.File", modEntry.FileName, ModBase.GetString(GetModFileInfo(modEntry.path).Length)));
                        if (modEntry.Version is not null) contentLines.Add(Lang.Text("Instance.Resource.Item.Info.Version", modEntry.Version));
                        if (modEntry.path.EndsWithF(".litematic", true))
                            ShowLitematicDetails(contentLines, modEntry);
                        else if (modEntry.path.EndsWithF(".schem", true))
                            ShowSchemDetails(contentLines, modEntry);
                        else if (modEntry.path.EndsWithF(".schematic", true))
                            ShowSchematicDetails(contentLines, modEntry);
                        else if (modEntry.path.EndsWithF(".nbt", true)) ShowNbtDetails(contentLines, modEntry);
                        ShowDebugInfo(contentLines, modEntry);
                        ShowSchematicDialog(contentLines, modEntry);
                    }
                    catch (Exception ex)
                    {
                        ModBase.Log(
                            ex,
                            "显示原理图详情失败",
                            ModBase.LogLevel.Feedback,
                            userSummary: Lang.Text("Instance.Resource.Error.OperationFailed"));
                    }
                });
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    "加载原理图 NBT 数据失败",
                    ModBase.LogLevel.Feedback,
                    userSummary: Lang.Text("Instance.Resource.Error.OperationFailed"));
            }
        });
    }

}
