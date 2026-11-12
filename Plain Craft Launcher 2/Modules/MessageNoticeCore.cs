using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FluentValidation;
using Microsoft.VisualBasic;
using Microsoft.Win32;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.IdentityModel.OAuth;
using PCL.Core.UI;
using PCL.Core.Utils;
using PCL.Core.Utils.OS;
using PCL.Core.Utils.Secret;

using static PCL.ModMain;

namespace PCL;

internal static class MessageNoticeCore
{


    internal static string GetDefaultDialogTitle() => Lang.Text("Common.Dialog.Title");



    internal static string GetDefaultConfirmText() => Lang.Text("Common.Action.Confirm");



    internal static string GetDefaultCancelText() => Lang.Text("Common.Action.Cancel");



    /// <summary>
    ///     显示弹窗，返回点击按钮的编号（从 1 开始）。
    /// </summary>
    /// <param name="title">弹窗的标题。</param>
    /// <param name="caption">弹窗的内容。</param>
    /// <param name="button1">显示的第一个按钮，默认为“确定”。</param>
    /// <param name="button2">显示的第二个按钮，默认为空。</param>
    /// <param name="button3">显示的第三个按钮，默认为空。</param>
    /// <param name="button1Action">点击第一个按钮将执行该方法，不关闭弹窗。</param>
    /// <param name="button2Action">点击第二个按钮将执行该方法，不关闭弹窗。</param>
    /// <param name="button3Action">点击第三个按钮将执行该方法，不关闭弹窗。</param>
    /// <param name="isWarn">是否为警告弹窗，若为 True，弹窗配色和背景会变为红色。</param>
    public static int MyMsgBox(string caption, string? title = null, string? button1 = null, string? button2 = "",
        string? button3 = "", bool isWarn = false, bool highLight = true, bool forceWait = false,
        Action button1Action = null, Action button2Action = null, Action button3Action = null)
    {
        title ??= GetDefaultDialogTitle();
        button1 ??= GetDefaultConfirmText();
        button2 ??= "";
        button3 ??= "";
        // 将弹窗列入队列
        var converter = new MyMsgBoxConverter
        {
            Type = MyMsgBoxType.Text, Button1 = button1, Button2 = button2, Button3 = button3, Text = caption,
            IsWarn = isWarn, Title = title, HighLight = highLight, ForceWait = true, Button1Action = button1Action,
            Button2Action = button2Action, Button3Action = button3Action, ModalLifetimeOwnedByCaller = true
        };
        WaitingMyMsgBox.Add(converter);
        if (ModBase.RunInUi())
            // 若为 UI 线程，立即执行弹窗刻， 避免快速（连点器）点击时多次弹窗
            MyMsgBoxTick();
        if (button2.Length > 0 || forceWait)
        {
            // 若有多个按钮则开始等待
            if (frmMain is null || (frmMain.PanMsg is null && ModBase.RunInUi()))
            {
                // 主窗体尚未加载，用老土的弹窗来替代
                WaitingMyMsgBox.Remove(converter);
                if (button2.Length > 0)
                {
                    var rawResult = Interaction.MsgBox(caption,
                        (MsgBoxStyle)((int)(button3.Length > 0 ? MsgBoxStyle.YesNoCancel : MsgBoxStyle.YesNo) +
                                      (int)(isWarn ? MsgBoxStyle.Critical : MsgBoxStyle.Question)), title);
                    switch (rawResult)
                    {
                        case MsgBoxResult.Yes:
                        {
                            converter.Result = 1;
                            break;
                        }
                        case MsgBoxResult.No:
                        {
                            converter.Result = 2;
                            break;
                        }
                        case MsgBoxResult.Cancel:
                        {
                            converter.Result = 3;
                            break;
                        }
                    }
                }
                else
                {
                    Interaction.MsgBox(caption,
                        (MsgBoxStyle)((int)MsgBoxStyle.OkOnly +
                                      (int)(isWarn ? MsgBoxStyle.Critical : MsgBoxStyle.Question)), title);
                    converter.Result = 1;
                }

                ModBase.Log("[Control] 主窗体加载完成前出现意料外的等待弹窗：" + button1 + "," + button2 + "," + button3,
                    ModBase.LogLevel.Debug);
            }
            else
            {
                try
                {
                    frmMain.DragStop();
                    converter.ModalLifetimeOwnedByCaller = true;
                    ComponentDispatcher.PushModal();
                    Dispatcher.PushFrame(converter.WaitFrame);
                }
                finally
                {
                    ComponentDispatcher.PopModal();
                }
            }

            ModBase.Log($"[Control] 普通弹框返回：{converter.Result ?? "null"}");
            return (int)converter.Result;
        }

        // 不进行等待，直接返回
        return 1;
    }



    /// <summary>
    ///     显示弹窗，返回点击按钮的编号（从 1 开始）。
    /// </summary>
    /// <param name="title">弹窗的标题。</param>
    /// <param name="caption">弹窗的内容。</param>
    /// <param name="button1">显示的第一个按钮，默认为“确定”。</param>
    /// <param name="button2">显示的第二个按钮，默认为空。</param>
    /// <param name="button3">显示的第三个按钮，默认为空。</param>
    /// <param name="button1Action">点击第一个按钮将执行该方法，不关闭弹窗。</param>
    /// <param name="button2Action">点击第二个按钮将执行该方法，不关闭弹窗。</param>
    /// <param name="button3Action">点击第三个按钮将执行该方法，不关闭弹窗。</param>
    /// <param name="isWarn">是否为警告弹窗，若为 True，弹窗配色和背景会变为红色。</param>
    public static int MyMsgBoxMarkdown(string caption, string? title = null, string? button1 = null, string? button2 = "",
        string? button3 = "", bool isWarn = false, bool highLight = true, bool forceWait = false,
        Action button1Action = null, Action button2Action = null, Action button3Action = null)
    {
        title ??= GetDefaultDialogTitle();
        button1 ??= GetDefaultConfirmText();
        button2 ??= "";
        button3 ??= "";
        // 将弹窗列入队列
        var converter = new MyMsgBoxConverter
        {
            Type = MyMsgBoxType.Markdown, Button1 = button1, Button2 = button2, Button3 = button3, Text = caption,
            IsWarn = isWarn, Title = title, HighLight = highLight, ForceWait = true, Button1Action = button1Action,
            Button2Action = button2Action, Button3Action = button3Action, ModalLifetimeOwnedByCaller = true
        };
        WaitingMyMsgBox.Add(converter);
        if (ModBase.RunInUi())
            // 若为 UI 线程，立即执行弹窗刻， 避免快速（连点器）点击时多次弹窗
            MyMsgBoxTick();
        if (button2.Length > 0 || forceWait)
        {
            // 若有多个按钮则开始等待
            if (frmMain is null || (frmMain.PanMsg is null && ModBase.RunInUi()))
            {
                // 主窗体尚未加载，用老土的弹窗来替代
                WaitingMyMsgBox.Remove(converter);
                if (button2.Length > 0)
                {
                    var rawResult = Interaction.MsgBox(caption,
                        (MsgBoxStyle)((int)(button3.Length > 0 ? MsgBoxStyle.YesNoCancel : MsgBoxStyle.YesNo) +
                                      (int)(isWarn ? MsgBoxStyle.Critical : MsgBoxStyle.Question)), title);
                    switch (rawResult)
                    {
                        case MsgBoxResult.Yes:
                        {
                            converter.Result = 1;
                            break;
                        }
                        case MsgBoxResult.No:
                        {
                            converter.Result = 2;
                            break;
                        }
                        case MsgBoxResult.Cancel:
                        {
                            converter.Result = 3;
                            break;
                        }
                    }
                }
                else
                {
                    Interaction.MsgBox(caption,
                        (MsgBoxStyle)((int)MsgBoxStyle.OkOnly +
                                      (int)(isWarn ? MsgBoxStyle.Critical : MsgBoxStyle.Question)), title);
                    converter.Result = 1;
                }

                ModBase.Log("[Control] 主窗体加载完成前出现意料外的等待弹窗：" + button1 + "," + button2 + "," + button3,
                    ModBase.LogLevel.Debug);
            }
            else
            {
                try
                {
                    frmMain.DragStop();
                    converter.ModalLifetimeOwnedByCaller = true;
                    ComponentDispatcher.PushModal();
                    Dispatcher.PushFrame(converter.WaitFrame);
                }
                finally
                {
                    ComponentDispatcher.PopModal();
                }
            }

            ModBase.Log($"[Control] 普通弹框返回：{converter.Result ?? "null"}");
            return (int)converter.Result;
        }

        // 不进行等待，直接返回
        return 1;
    }



    /// <summary>
    ///     显示输入框并返回输入的文本。若点击第二个按钮，则返回 Nothing。
    /// </summary>
    /// <param name="title">弹窗的标题。</param>
    /// <param name="validateRules">文本框的输入检测。</param>
    /// <param name="text">弹窗的介绍文本。</param>
    /// <param name="defaultInput">文本框的默认内容。</param>
    /// <param name="hintText">文本框的提示内容。</param>
    /// <param name="button1">显示的第一个按钮，默认为“确定”。</param>
    /// <param name="button2">显示的第二个按钮，默认为“取消”。</param>
    /// <param name="isWarn">是否为警告弹窗，若为 True，弹窗配色和背景会变为红色。</param>
    /// <param name="multiline">是否使用多行、可滚动的高输入框（长令牌、Cookie 之类）。</param>
    public static string MyMsgBoxInput(string title, string text = "", string defaultInput = "",
        Collection<IValidator<string>>? validateRules = null, string hintText = "", string? button1 = null,
        string? button2 = null, bool isWarn = false, bool multiline = false)
    {
        button1 ??= GetDefaultConfirmText();
        button2 ??= GetDefaultCancelText();
        // 将弹窗列入队列
        var converter = new MyMsgBoxConverter
        {
            Text = text, HintText = hintText, Type = MyMsgBoxType.Input,
            ValidateRules = validateRules ?? [], Button1 = button1, Button2 = button2,
            Content = defaultInput, IsWarn = isWarn, Title = title, Multiline = multiline,
            ModalLifetimeOwnedByCaller = true
        };
        WaitingMyMsgBox.Add(converter);
        // 虽然我也不知道这是啥但是能用就成了 :)
        try
        {
            frmMain?.DragStop();
            ComponentDispatcher.PushModal();
            Dispatcher.PushFrame(converter.WaitFrame);
        }
        finally
        {
            ComponentDispatcher.PopModal();
        }

        ModBase.Log($"[Control] 输入弹框返回：{converter.Result}");
        return converter.Result?.ToString();
    }



    public static string MyMsgBoxAuthServer(string defaultServer, IReadOnlyDictionary<string, string> presets,
        string? title = null)
    {
        var converter = new MyMsgBoxConverter
        {
            Type = MyMsgBoxType.AuthServer,
            Title = title ?? Lang.Text("Launch.Account.Auth.SelectServer"),
            Button1 = GetDefaultConfirmText(),
            Button2 = GetDefaultCancelText(),
            AuthServerDefault = defaultServer,
            AuthServerPresets = presets,
            ForceWait = true,
            ModalLifetimeOwnedByCaller = true
        };
        WaitingMyMsgBox.Add(converter);
        try
        {
            frmMain?.DragStop();
            ComponentDispatcher.PushModal();
            Dispatcher.PushFrame(converter.WaitFrame);
        }
        finally
        {
            ComponentDispatcher.PopModal();
        }
        return converter.Result?.ToString() ?? string.Empty;
    }



    /// <summary>
    ///     显示选择框并返回选择的第几项（从 0 开始）。若点击第二个按钮，则返回 Nothing。
    /// </summary>
    /// <param name="title">弹窗的标题。</param>
    /// <param name="button1">显示的第一个按钮，默认为 “确定”。</param>
    /// <param name="button2">显示的第二个按钮，默认为空。</param>
    /// <param name="isWarn">是否为警告弹窗，若为 True，弹窗配色和背景会变为红色。</param>
    public static int? MyMsgBoxSelect(List<IMyRadio> selections, string? title = null, string? button1 = null,
        string? button2 = "", bool isWarn = false)
    {
        title ??= GetDefaultDialogTitle();
        button1 ??= GetDefaultConfirmText();
        button2 ??= "";
        // 将弹窗列入队列
        var converter = new MyMsgBoxConverter
        {
            Type = MyMsgBoxType.Select, Button1 = button1, Button2 = button2, Content = selections, IsWarn = isWarn,
            Title = title, ModalLifetimeOwnedByCaller = true
        };
        WaitingMyMsgBox.Add(converter);
        // 虽然我也不知道这是啥但是能用就成了 :)
        try
        {
            if (frmMain is not null)
                frmMain.DragStop();
            ComponentDispatcher.PushModal();
            Dispatcher.PushFrame(converter.WaitFrame);
        }
        finally
        {
            ComponentDispatcher.PopModal();
        }

        ModBase.Log($"[Control] 选择弹框返回：{converter.Result ?? "null"}");
        return (int?)converter.Result;
    }



    /// <summary>
    ///     显示多选选择框并返回勾选的所有项索引（从 0 开始）。若点击第二个按钮，则返回 Nothing。
    /// </summary>
    /// <param name="selections">需要展示的可勾选列表项。</param>
    /// <param name="title">弹窗的标题。</param>
    /// <param name="button1">显示的第一个按钮，默认为 “确定”。</param>
    /// <param name="button2">显示的第二个按钮，默认为空。</param>
    /// <param name="isWarn">是否为警告弹窗，若为 True，弹窗配色和背景会变为红色。</param>
    public static List<int>? MyMsgBoxMultiSelect(List<MyListItem> selections, string? title = null,
        string? button1 = null, string? button2 = "", bool isWarn = false)
        => MyMsgBoxMultiSelect(selections, false, title, button1, button2, isWarn);

    public static List<int>? MyMsgBoxMultiSelect(List<MyListItem> selections, bool allowEmptySelection,
        string? title = null, string? button1 = null, string? button2 = "", bool isWarn = false)
    {
        title ??= GetDefaultDialogTitle();
        button1 ??= GetDefaultConfirmText();
        button2 ??= "";
        // 将弹窗列入队列
        var converter = new MyMsgBoxConverter
        {
            Type = MyMsgBoxType.Select, MultiSelect = true, AllowEmptySelection = allowEmptySelection,
            Button1 = button1, Button2 = button2, Content = selections,
            IsWarn = isWarn, Title = title, ModalLifetimeOwnedByCaller = true
        };
        WaitingMyMsgBox.Add(converter);
        // 虽然我也不知道这是啥但是能用就成了 :)
        try
        {
            if (frmMain is not null)
                frmMain.DragStop();
            ComponentDispatcher.PushModal();
            Dispatcher.PushFrame(converter.WaitFrame);
        }
        finally
        {
            ComponentDispatcher.PopModal();
        }

        ModBase.Log($"[Control] 多选弹框返回：{converter.Result ?? "null"}");
        return (List<int>?)converter.Result;
    }




    public static void MyMsgBoxTick()
    {
        try
        {
            if (frmMain is null || frmMain.PanMsg is null || frmMain.WindowState == WindowState.Minimized)
                return;
            if (frmMain.PanMsg.Children.Count > 0)
            {
                // 弹窗中
                frmMain.PanMsgBackground.Visibility = Visibility.Visible;
            }
            else if (WaitingMyMsgBox.Any())
            {
                // 没有弹窗，显示一个等待的弹窗
                frmMain.PanMsgBackground.Visibility = Visibility.Visible;
                switch (WaitingMyMsgBox[0].Type)
                {
                    case MyMsgBoxType.Input:
                    {
                        frmMain.PanMsg.Children.Add(new MyMsgInput(WaitingMyMsgBox[0]));
                        break;
                    }
                    case MyMsgBoxType.Select:
                    {
                        frmMain.PanMsg.Children.Add(new MyMsgSelect(WaitingMyMsgBox[0]));
                        break;
                    }
                    case MyMsgBoxType.AuthServer:
                    {
                        frmMain.PanMsg.Children.Add(new MyMsgAuthServer(WaitingMyMsgBox[0]));
                        break;
                    }
                    case MyMsgBoxType.Text:
                    {
                        frmMain.PanMsg.Children.Add(new MyMsgText(WaitingMyMsgBox[0]));
                        break;
                    }
                    case MyMsgBoxType.Login:
                    {
                        frmMain.PanMsg.Children.Add(new MyMsgLogin(WaitingMyMsgBox[0]));
                        break;
                    }
                    case MyMsgBoxType.Markdown:
                    {
                        frmMain.PanMsg.Children.Add(new MyMsgMarkdown(WaitingMyMsgBox[0]));
                        break;
                    }
                }

                WaitingMyMsgBox.RemoveAt(0);
            }
            // 没有弹窗，没有等待的弹窗
            else if (!(frmMain.PanMsgBackground.Visibility == Visibility.Collapsed))
            {
                frmMain.PanMsgBackground.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "处理等待中的弹窗失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Main.Error.OperationFailed"));
        }
    }



    public static void MsgBoxWrapper_OnShow(string message, string caption, ICollection<MsgBoxButtonInfo> buttons,
        MsgBoxTheme theme, bool block, ref int result)
    {
        var btnText1 = buttons.Count < 1 ? GetDefaultConfirmText() : buttons.ElementAt(0).Context;
        var btnAct1 = (Action)(buttons.Count < 1 ? (object)null : buttons.ElementAt(0).OnClick);
        var btnText2 = buttons.Count < 2 ? GetDefaultCancelText() : buttons.ElementAt(1).Context;
        var btnAct2 = (Action)(buttons.Count < 2 ? (object)null : buttons.ElementAt(1).OnClick);
        var btnText3 = buttons.Count < 3 ? "" : buttons.ElementAt(2).Context;
        var btnAct3 = (Action)(buttons.Count < 3 ? (object)null : buttons.ElementAt(2).OnClick);

        var isWarn = theme == MsgBoxTheme.Warning || theme == MsgBoxTheme.Error;

        result = MyMsgBox(message, caption, btnText1, btnText2, btnText3, isWarn, forceWait: block,
            button1Action: btnAct1, button2Action: btnAct2, button3Action: btnAct3);
    }

}
