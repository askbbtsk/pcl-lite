using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Core.Utils;
using PCL.Network;

namespace PCL;

public partial class PageLaunchLeft
{
    private double actualUsedHeight;
    private double actualUsedWidth;
    private int btnLaunchState;
    private string _btnLaunchLanguage;
    private McInstance btnLaunchVersion;
    private bool isHeightAnimating;

    private LaunchButtonAction _launchButtonAction;

    // 加载当前实例
    private bool isLoad;

    private bool isLoadFinished;

    // 尺寸改变动画
    private bool isWidthAnimating;
    private double showProgress;

    public PageLaunchLeft()
    {
        InitializeComponent();
        Loaded += PageLaunchLeft_Loaded;
        WeakLanguageChanged.Add(this, OnLanguageChanged);
        // Handles
        BtnInstance.Click += BtnInstance_Click;
        BtnLaunch.Click += BtnLaunch_Click;
        BtnLaunch.Loaded += (_, _) => RefreshButtonsUI();
        BtnCancel.Click += BtnCancel_Click;
        BtnMore.Click += BtnMore_Click;
        PanLaunchingInfo.SizeChanged += PanLaunchingInfo_SizeChangedW;
        PanLaunchingInfo.SizeChanged += PanLaunchingInfo_SizeChangedH;
    }

    /// <summary>
    ///     当前页面的种类。
    /// </summary>
    private PageType pageCurrent = PageType.None;



    // 正版皮肤
    public static ModLoader.LoaderTask<ModBase.EqualableList<string>, string> skinMs = new("Loader Skin Ms", SkinMsLoad,
        SkinMsInput, ThreadPriority.AboveNormal);

    // 离线皮肤
    public static ModLoader.LoaderTask<ModBase.EqualableList<string>, string> skinLegacy = new("Loader Skin Legacy",
        SkinLegacyLoad, SkinLegacyInput, ThreadPriority.AboveNormal);

    // Authlib-Injector 皮肤
    public static ModLoader.LoaderTask<ModBase.EqualableList<string>, string> skinAuth = new("Loader Skin Auth",
        SkinAuthLoad, SkinAuthInput, ThreadPriority.AboveNormal);

    // 全部皮肤加载器
    // 需要放在其中元素的后面，否则会因为它提前被加载而莫名其妙变成 Nothing
    public static List<ModLoader.LoaderTask<ModBase.EqualableList<string>, string>> skinLoaders = new()
        { skinMs, skinLegacy, skinAuth };

}
