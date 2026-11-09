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



    // 详情
    public void Info_Click(object sender, EventArgs e)
    {
        try
        {
            var datapackEntry = ((MyLocalCompItem)(sender is MyIconButton iconBtn ? iconBtn.Tag : sender)).Entry;

            // 加载失败信息
            if (datapackEntry.State == ModLocalComp.LocalCompFile.LocalFileStatus.Unavailable)
            {
                ModMain.MyMsgBox(
                    Lang.Text(
                        "Instance.Saves.Datapack.Info.ReadFailed.WithDetail",
                        datapackEntry.FileUnavailableReason.ToString()),
                    Lang.Text("Instance.Saves.Datapack.Info.ReadFailedTitle"));
                return;
            }

            if (datapackEntry.Comp is not null)
            {
                // 跳转到数据包下载页面
                ModMain.frmMain.PageChange(new FormMain.PageStackData
                {
                    page = FormMain.PageType.CompDetail,
                    additional = (datapackEntry.Comp, new List<string>(), PageInstanceLeft.McInstance.Info.VanillaName,
                        ModComp.CompLoaderType.Minecraft, ModComp.CompType.DataPack, null)
                });
            }
            else
            {
                // 获取信息
                var contentLines = new List<string>();

                if (datapackEntry.Description is not null)
                    contentLines.Add(datapackEntry.Description + "\r\n");
                if (datapackEntry.Authors is not null)
                    contentLines.Add(Lang.Text("Instance.Saves.Datapack.Info.Author") + datapackEntry.Authors);
                contentLines.Add(Lang.Text("Instance.Saves.Datapack.Info.File") + datapackEntry.FileName + "（" +
                                 ModBase.GetString(GetDatapackFileInfo(datapackEntry.path).Length) + "）");
                if (datapackEntry.Version is not null)
                    contentLines.Add(Lang.Text("Instance.Saves.Datapack.Info.Version") + datapackEntry.Version);

                var debugInfo = new List<string>();
                if (datapackEntry.ModId is not null) debugInfo.Add(Lang.Text("Instance.Saves.Datapack.Info.DatapackId") + datapackEntry.ModId);
                if (debugInfo.Any())
                {
                    contentLines.Add("");
                    contentLines.AddRange(debugInfo);
                }

                // 显示详情信息
                if (datapackEntry.Url is null)
                    ModMain.MyMsgBox(contentLines.Join("\r\n"), datapackEntry.Name, Lang.Text("Instance.Resource.Item.Info.Return"));
                else if (ModMain.MyMsgBox(contentLines.Join("\r\n"), datapackEntry.Name, Lang.Text("Instance.Resource.Item.Info.OpenWebsite"), Lang.Text("Instance.Resource.Item.Info.Return")) == 1)
                    ModBase.OpenWebsite(datapackEntry.Url);
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "获取数据包详情失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Saves.Error.OperationFailed"));
        }
    }


    // 打开文件所在的位置
    public void Open_Click(MyIconButton sender, EventArgs e)
    {
        try
        {
            var listItem = (MyLocalCompItem)sender.Tag;
            ModBase.OpenExplorer(listItem.Entry.path);
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "打开数据包文件位置失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Saves.Error.OperationFailed"));
        }
    }


    // 删除
    public void Delete_Click(MyIconButton sender, EventArgs e)
    {
        var listItem = (MyLocalCompItem)sender.Tag;
        DeleteDatapacks(new[] { listItem.Entry });
    }


    // 启用
    public void Enable_Click(MyIconButton sender, EventArgs e)
    {
        var listItem = (MyLocalCompItem)sender.Tag;
        ToggleDatapacks(new[] { listItem.Entry }, true);
    }


    // 禁用
    public void Disable_Click(MyIconButton sender, EventArgs e)
    {
        var listItem = (MyLocalCompItem)sender.Tag;
        ToggleDatapacks(new[] { listItem.Entry }, false);
    }

}
