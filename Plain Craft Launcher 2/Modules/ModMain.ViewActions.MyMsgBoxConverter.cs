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

namespace PCL;

public static partial class ModMain
{


    /// <summary>
    ///     存储弹窗信息的转换器。
    /// </summary>
    public class MyMsgBoxConverter
    {
        /// <summary>Optional monotonic delay for the primary text-dialog action, measured from visible load.</summary>
        public int MinimumConfirmDelaySeconds { get; set; }
        /// <summary>The delayed helper owns the sole PushModal/PopModal pair.</summary>
        public bool ModalLifetimeOwnedByCaller { get; set; }

        // 设置轮询 Url
        public string Button1 = "";

        /// <summary>
        ///     点击第一个按钮将执行该方法，不关闭弹窗。
        /// </summary>
        public Action Button1Action;

        public string Button2 = "";

        /// <summary>
        ///     点击第二个按钮将执行该方法，不关闭弹窗。
        /// </summary>
        public Action Button2Action;

        public string Button3 = "";

        /// <summary>
        ///     点击第三个按钮将执行该方法，不关闭弹窗。
        /// </summary>
        public Action Button3Action;

        /// <summary>
        ///     输入模式：文本框的文本。
        ///     选择模式：需要放进去的 List(Of MyListItem)。
        ///     登录模式：登录步骤 1 中返回的 JSON。
        /// </summary>
        public object Content;

        /// <summary>
        /// Optional provider-specific device-code polling callback used by OAuth providers other than Microsoft.
        /// </summary>
        public Func<JsonObject, CancellationToken, Task<AuthorizeResult?>> DeviceCodePoll;

        public Func<AuthorizeResult, CancellationToken, Task>? LoginResultHandler;

        public Action<object>? CompletionHandler;

        public bool ForceWait;

        /// <summary>
        ///     选择模式：是否允许勾选多个选项
        /// </summary>
        public bool MultiSelect;

        /// <summary>
        ///     有多个按钮时，是否给第一个按钮加高亮。
        /// </summary>
        public bool HighLight;

        /// <summary>
        ///     输入模式：提示文本。
        /// </summary>
        public string HintText = "";

        /// <summary>
        ///     输入模式：是否换成多行、可滚动的高输入框。
        ///     <para>
        ///         长令牌、Cookie 这类内容塞进单行框里既看不全也容易溢出，改用多行框。
        ///         </para>
        /// </summary>
        public bool Multiline;

        /// <summary>
        ///     弹窗是否已经关闭。
        /// </summary>
        public bool IsExited = false;

        public bool IsWarn;

        /// <summary>
        ///     输入模式：输入的文本。若点击了 非 第一个按钮，则为 Nothing。
        ///     选择模式：点击的按钮编号，从 1 开始。
        ///     登录模式：字符串数组 {AccessToken, RefreshToken} 或一个 Exception。
        /// </summary>
        public object Result;

        public string Text;
        public string Title;
        public MyMsgBoxType Type;

        /// <summary>
        ///     输入模式：输入验证规则。
        /// </summary>
        public Collection<IValidator<string>> ValidateRules;

        public DispatcherFrame WaitFrame = new(true);

        public string AuthServerDefault = "";
        public IReadOnlyDictionary<string, string>? AuthServerPresets;
    }


    public enum MyMsgBoxType
    {
        Text,
        Select,
        Input,
        AuthServer,
        Login,
        Markdown
    }


    private static string GetDefaultDialogTitle() =>MessageNoticeCore.GetDefaultDialogTitle();


    private static string GetDefaultConfirmText() =>MessageNoticeCore.GetDefaultConfirmText();


    private static string GetDefaultCancelText() =>MessageNoticeCore.GetDefaultCancelText();


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
=>MessageNoticeCore.MyMsgBox(caption, title, button1, button2, button3, isWarn, highLight, forceWait, button1Action, button2Action, button3Action);


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
=>MessageNoticeCore.MyMsgBoxMarkdown(caption, title, button1, button2, button3, isWarn, highLight, forceWait, button1Action, button2Action, button3Action);


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
=>MessageNoticeCore.MyMsgBoxInput(title, text, defaultInput, validateRules, hintText, button1, button2, isWarn, multiline);


    public static string MyMsgBoxAuthServer(string defaultServer, IReadOnlyDictionary<string, string> presets,
        string? title = null)
=>MessageNoticeCore.MyMsgBoxAuthServer(defaultServer, presets, title);


    /// <summary>
    ///     显示选择框并返回选择的第几项（从 0 开始）。若点击第二个按钮，则返回 Nothing。
    /// </summary>
    /// <param name="title">弹窗的标题。</param>
    /// <param name="button1">显示的第一个按钮，默认为 “确定”。</param>
    /// <param name="button2">显示的第二个按钮，默认为空。</param>
    /// <param name="isWarn">是否为警告弹窗，若为 True，弹窗配色和背景会变为红色。</param>
    public static int? MyMsgBoxSelect(List<IMyRadio> selections, string? title = null, string? button1 = null,
        string? button2 = "", bool isWarn = false)
=>MessageNoticeCore.MyMsgBoxSelect(selections, title, button1, button2, isWarn);


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
=>MessageNoticeCore.MyMsgBoxMultiSelect(selections, title, button1, button2, isWarn);



    public static void MyMsgBoxTick()
=>MessageNoticeCore.MyMsgBoxTick();


    public static void MsgBoxWrapper_OnShow(string message, string caption, ICollection<MsgBoxButtonInfo> buttons,
        MsgBoxTheme theme, bool block, ref int result)
=>MessageNoticeCore.MsgBoxWrapper_OnShow(message, caption, buttons, theme, block, ref result);


    private static void TimerFool()
    {
        try
        {
            if (frmLaunchLeft is null || frmLaunchLeft.AprilPosTrans is null || frmMain.lastMouseArg is null)
                return;
            if (isAprilGiveup || frmMain.pageCurrent != FormMain.PageType.Launch ||
                ModAnimation.AniControlEnabled != 0 || !frmLaunchLeft.BtnLaunch.IsLoaded)
                return;

            // 计算是否空闲
            var mousePos = frmMain.lastMouseArg.GetPosition(frmMain);
            if (mousePos == aprilMousePosLast)
            {
                aprilIdieCount += 1;
            }
            else
            {
                aprilMousePosLast = mousePos;
                aprilIdieCount = 0;
            }

            // 计算躲避移动
            Vector direction;
            double distance;
            var buttonWidth = frmLaunchLeft.BtnLaunch.ActualWidth / 2d;
            var buttonHeight = frmLaunchLeft.BtnLaunch.ActualHeight / 2d;
            var vec = (Vector)(frmMain.lastMouseArg.GetPosition(frmLaunchLeft.BtnLaunch) -
                               new Vector(buttonWidth, buttonHeight));
            var dir = new Vector(vec.X, vec.Y);
            dir.Normalize();
            direction = -dir;
            distance = new Vector(Math.Max(0d, Math.Abs(vec.X) - buttonWidth),
                Math.Max(0d, Math.Abs(vec.Y) - buttonHeight)).Length;
            var breathScale = Math.Sin(timer150Count / 37.5d * Math.PI);
            var acc = Math.Max(0d, breathScale * 0.25d - 0.65d - Math.Log((distance + 0.4d) / 200d)) * direction; // 加速度
            // 计算回归移动
            if (aprilIdieCount >= 64 * 5)
            {
                var safeDist = (Vector)(frmMain.lastMouseArg.GetPosition(frmMain.PanMain) -
                                        new Vector(buttonWidth, frmMain.PanMain.ActualHeight - buttonHeight * 3d));
                var back = new Vector(frmLaunchLeft.AprilPosTrans.X, frmLaunchLeft.AprilPosTrans.Y);
                if (safeDist.Length > 250d && back.Length > 0.4d)
                {
                    acc -= back * 0.0005d;
                    back.Normalize();
                    acc -= back * 0.15d;
                }
            }

            // 回到边界
            var relative = frmLaunchLeft.BtnLaunch.TranslatePoint(new Point(0d, 0d), frmMain.PanForm);
            if (relative.X < -buttonWidth * 2d)
            {
                frmLaunchLeft.AprilPosTrans.X += frmMain.PanForm.ActualWidth + buttonWidth * 2d; // 离开左边界
                aprilSpeed.X -= 80d;
                if (relative.Y < 0d)
                    frmLaunchLeft.AprilPosTrans.Y += buttonHeight * 2.5d;
                else if (relative.Y > frmMain.PanForm.ActualHeight - buttonHeight * 2d)
                    frmLaunchLeft.AprilPosTrans.Y -= buttonHeight * 2.5d;
            }
            else if (relative.X > frmMain.PanForm.ActualWidth)
            {
                frmLaunchLeft.AprilPosTrans.X -= frmMain.PanForm.ActualWidth + buttonWidth * 2d; // 离开右边界
                aprilSpeed.X += 80d;
                if (relative.Y < 0d)
                    frmLaunchLeft.AprilPosTrans.Y += buttonHeight * 2.5d;
                else if (relative.Y > frmMain.PanForm.ActualHeight - buttonHeight * 2d)
                    frmLaunchLeft.AprilPosTrans.Y -= buttonHeight * 2.5d;
            }
            else if (relative.Y < -buttonHeight * 2d)
            {
                frmLaunchLeft.AprilPosTrans.Y += frmMain.PanForm.ActualHeight + buttonHeight * 2d; // 离开上边界
                aprilSpeed.Y -= 25d;
                if (relative.X < 0d)
                    frmLaunchLeft.AprilPosTrans.X += buttonWidth * 2d;
                else if (relative.X > frmMain.PanForm.ActualWidth - buttonWidth * 2d)
                    frmLaunchLeft.AprilPosTrans.X -= buttonWidth * 2d;
            }
            else if (relative.Y > frmMain.PanForm.ActualHeight)
            {
                frmLaunchLeft.AprilPosTrans.Y -= frmMain.PanForm.ActualHeight + buttonHeight * 2d; // 离开下边界
                aprilSpeed.Y += 25d;
                if (relative.X < 0d)
                    frmLaunchLeft.AprilPosTrans.X += buttonWidth * 2d;
                else if (relative.X > frmMain.PanForm.ActualWidth - buttonWidth * 2d)
                    frmLaunchLeft.AprilPosTrans.X -= buttonWidth * 2d;
            }

            // 移动
            aprilSpeed = aprilSpeed * 0.8d + acc;
            var speedValue = Math.Min(60d, aprilSpeed.Length);
            if (speedValue < 0.01d)
                return;
            aprilSpeed.Normalize();
            aprilSpeed *= speedValue;
            aprilDistance = (int)Math.Round(aprilDistance + speedValue);
            frmLaunchLeft.AprilPosTrans.X += aprilSpeed.X;
            frmLaunchLeft.AprilPosTrans.Y += aprilSpeed.Y;
            // 大小改变
            frmLaunchLeft.AprilScaleTrans.ScaleX =
                ModBase.MathClamp(1d - (Math.Abs(direction.X) - Math.Abs(direction.Y)) * (speedValue / 160d), 0.2d,
                    1.8d);
            frmLaunchLeft.AprilScaleTrans.ScaleY =
                ModBase.MathClamp(1d - (Math.Abs(direction.Y) - Math.Abs(direction.X)) * (speedValue / 100d), 0.2d,
                    1.8d);
            // 放弃提示
            if (aprilDistance > 4000)
            {
                aprilDistance = -4000;
                switch (RandomUtils.NextInt(0, 3))
                {
                    case 0:
                    {
                        HintService.Hint("放弃吧！只需要点一下右下角的小白旗……");
                        break;
                    }
                    case 1:
                    {
                        HintService.Hint("看到右下角的那面小白旗了吗？");
                        break;
                    }
                    case 2:
                    {
                        HintService.Hint("这里建议点一下右下角的小白旗投降呢.jpg");
                        break;
                    }
                    case 3:
                    {
                        HintService.Hint("右下角的小白旗永远等着你……");
                        break;
                    }
                }
            }
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "愚人节移动出错",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Main.Error.OperationFailed"));
        }
    }

}
