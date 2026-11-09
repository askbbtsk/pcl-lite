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



    public class CompClipboard
    {
        // 剪贴板已读取内容
        public static string? currentText;

        // 识别剪贴板内容
        public static void GetClipboardResource()
        {
            string? text = null;
            ModBase.RunInUiWait(() => text = Clipboard.GetText());

            if (string.IsNullOrEmpty(text) || text == currentText) return;
            currentText = text;

            // 在新线程中处理网络请求
            ModBase.RunInNewThread(() =>
            {
                try
                {
                    var projectId = ResolveLinkToProjectId(text);

                    if (string.IsNullOrEmpty(projectId)) return;
                    ModBase.Log($"[Clipboard] Found ProjectId: {projectId}");

                    // 3. UI 交互：跳转到详情页
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Func<Task>(async () =>
                    {
                        if (ModMain.MyMsgBox(
                                Lang.Text("Download.Comp.Detail.Clipboard.Detected.Message"),
                                Lang.Text("Download.Comp.Detail.Clipboard.Detected.Title"),
                                Lang.Text("Common.Action.Confirm"), Lang.Text("Common.Action.Cancel"),
                                forceWait: true) == 1)
                        {
                            HintService.Hint(Lang.Text("Download.Comp.Detail.Clipboard.Fetching"));

                            var ids = new List<string> { projectId };
                            var compProjects = await CompRequest.GetCompProjectsByIdsAsync(ids);

                            if (compProjects.Count == 0)
                            {
                                HintService.Hint(Lang.Text("Download.Comp.Detail.Clipboard.InvalidContent"),
                                    HintType.Error);
                                return;
                            }

                            ModMain.frmMain.PageChange(new FormMain.PageStackData
                            {
                                page = FormMain.PageType.CompDetail,
                                additional = (compProjects.First(), new List<string>(), string.Empty, CompLoaderType.Any,
                                    CompType.Any, null)
                            });
                        }
                    }));
                }
                catch (Exception ex)
                {
                    ModBase.Log(ex, "Error processing clipboard resource");
                }
            }, "Clipboard Resource Processing");
        }
    }

}
