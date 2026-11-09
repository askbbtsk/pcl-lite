using System.Collections;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PCL.Core.App;
using PCL.Core.Utils.Validate;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PageDownloadInstall
{
    private bool isLoad;

    public PageDownloadInstall()
    {
        Initialized += (_, _) => LoaderInit();
        Loaded += (_, _) => Init();
        InitializeComponent();
        PanScroll = PanBack;
        LoadMinecraft.Text = Lang.Text("Download.Version.LoadingList");
        WeakLanguageChanged.Add(this, OnLanguageChanged);
        BtnBack.Click += (_, _) => ExitSelectPage();
        CardOptiFine.Swap += (_, _) => ReloadSelected();
        LoadOptiFine.StateChanged += (_, _, _) => ReloadSelected();
        CardForge.Swap += (_, _) => ReloadSelected();
        LoadForge.StateChanged += (_, _, _) => ReloadSelected();
        CardNeoForge.Swap += (_, _) => ReloadSelected();
        LoadNeoForge.StateChanged += (_, _, _) => ReloadSelected();
        CardFabric.Swap += (_, _) => ReloadSelected();
        LoadFabric.StateChanged += (_, _, _) => ReloadSelected();
        CardFabricApi.Swap += (_, _) => ReloadSelected();
        LoadFabricApi.StateChanged += (_, _, _) => ReloadSelected();
        CardOptiFabric.Swap += (_, _) => ReloadSelected();
        LoadOptiFabric.StateChanged += (_, _, _) => ReloadSelected();
        CardLiteLoader.Swap += (_, _) => ReloadSelected();
        LoadLiteLoader.StateChanged += (_, _, _) => ReloadSelected();
        LoadCleanroom.StateChanged += (_, _, _) => ReloadSelected();
        CardCleanroom.Swap += (_, _) => ReloadSelected();
        LoadLabyMod.StateChanged += (_, _, _) => ReloadSelected();
        CardLabyMod.Swap += (_, _) => ReloadSelected();
        TextSelectName.TextChanged += TextSelectName_TextChanged;
        TextSelectName.ValidateChanged += TextSelectName_ValidateChanged;
        CardOptiFine.PreviewSwap += CardOptiFine_PreviewSwap;
        LoadOptiFine.StateChanged += (_, _, _) => OptiFine_Loaded();
        BtnOptiFineClear.MouseLeftButtonUp += OptiFine_Clear;
        CardLiteLoader.PreviewSwap += CardLiteLoader_PreviewSwap;
        LoadLiteLoader.StateChanged += (_, _, _) => LiteLoader_Loaded();
        BtnLiteLoaderClear.MouseLeftButtonUp += LiteLoader_Clear;
        CardForge.PreviewSwap += CardForge_PreviewSwap;
        LoadForge.StateChanged += (_, _, _) => Forge_Loaded();
        BtnForgeClear.MouseLeftButtonUp += Forge_Clear;
        CardNeoForge.PreviewSwap += CardNeoForge_PreviewSwap;
        LoadNeoForge.StateChanged += (_, _, _) => NeoForge_Loaded();
        BtnNeoForgeClear.MouseLeftButtonUp += NeoForge_Clear;
        CardCleanroom.PreviewSwap += CardCleanroom_PreviewSwap;
        LoadCleanroom.StateChanged += (_, _, _) => Cleanroom_Loaded();
        BtnCleanroomClear.MouseLeftButtonUp += Cleanroom_Clear;
        CardFabric.PreviewSwap += CardFabric_PreviewSwap;
        LoadFabric.StateChanged += (_, _, _) => Fabric_Loaded();
        BtnFabricClear.MouseLeftButtonUp += Fabric_Clear;
        CardFabricApi.PreviewSwap += CardFabricApi_PreviewSwap;
        LoadFabricApi.StateChanged += (_, _, _) => FabricApi_Loaded();
        BtnFabricApiClear.MouseLeftButtonUp += FabricApi_Clear;
        CardLegacyFabric.PreviewSwap += CardLegacyFabric_PreviewSwap;
        LoadLegacyFabric.StateChanged += (_, _, _) => LegacyFabric_Loaded();
        BtnLegacyFabricClear.MouseLeftButtonUp += LegacyFabric_Clear;
        CardLegacyFabricApi.PreviewSwap += CardLegacyFabricApi_PreviewSwap;
        LoadLegacyFabricApi.StateChanged += (_, _, _) => LegacyFabricApi_Loaded();
        BtnLegacyFabricApiClear.MouseLeftButtonUp += LegacyFabricApi_Clear;
        CardOptiFabric.PreviewSwap += CardOptiFabric_PreviewSwap;
        LoadOptiFabric.StateChanged += (_, _, _) => OptiFabric_Loaded();
        BtnOptiFabricClear.MouseLeftButtonUp += OptiFabric_Clear;
        CardLabyMod.PreviewSwap += CardLabyMod_PreviewSwap;
        LoadLabyMod.StateChanged += (_, _, _) => LabyMod_Loaded();
        BtnLabyModClear.MouseLeftButtonUp += LabyMod_Clear;
        TextSelectName.KeyDown += TextSelectName_KeyDown;
        BtnStart.Click += (_, _) => BtnStart_Click();
    }
    private static void OnLanguageChanged(PageDownloadInstall page) => page._OnLanguageChanged();

    private void LoaderInit()
    {
        disabledPageAnimControls.Add(BtnStart);
        PageLoaderInit(LoadMinecraft, PanLoad, PanAllBack, null, ModDownload.dlClientListLoader,
            _ => LoadMinecraft_OnFinish());
    }

    private void Init()
    {
        PanBack.ScrollToHome();
        ModDownload.dlOptiFineListLoader.Start();
        ModDownload.dlLiteLoaderListLoader.Start();
        ModDownload.dlFabricListLoader.Start();
        ModDownload.dlNeoForgeListLoader.Start();
        ModDownload.dlCleanroomListLoader.Start();
        ModDownload.dlLabyModListLoader.Start();
        ModDownload.dlLegacyFabricListLoader.Start();

        // 重载预览
        TextSelectName.ValidateRules = [new FolderNameValidator(ModFolder.mcFolderSelected + "versions")];
        TextSelectName.Validate();
        ReloadSelected();

        // 非重复加载部分
        if (isLoad)
            return;
        isLoad = true;

        ModDownloadLib.McDownloadForgeRecommendedRefresh();

        LoadOptiFine.State = ModDownload.dlOptiFineListLoader;
        LoadLiteLoader.State = ModDownload.dlLiteLoaderListLoader;
        LoadFabric.State = ModDownload.dlFabricListLoader;
        LoadFabricApi.State = ModDownload.dlFabricApiLoader;
        LoadNeoForge.State = ModDownload.dlNeoForgeListLoader;
        LoadCleanroom.State = ModDownload.dlCleanroomListLoader;
        LoadOptiFabric.State = ModDownload.dlOptiFabricLoader;
        LoadLabyMod.State = ModDownload.dlLabyModListLoader;
        LoadLegacyFabric.State = ModDownload.dlLegacyFabricListLoader;
        LoadLegacyFabricApi.State = ModDownload.dlLegacyFabricApiLoader;
    }

    private string GetLoaderError(MyLoading loader)
    {
        if (loader is null || !loader.State.IsLoader)
            return Lang.Text("Download.Install.State.Getting");
        switch (loader.State.LoadingState)
        {
            case MyLoading.MyLoadingState.Run:
            {
                return Lang.Text("Download.Install.State.Getting");
            }
            case MyLoading.MyLoadingState.Error:
            {
                var message = ((ModLoader.LoaderBase)loader.State).Error.Message;
                return message == Lang.Text("Download.Install.State.NoVersion")
                    ? Lang.Text("Download.Install.State.NoVersion")
                    : Lang.Text("Download.Install.State.GetFailed", message);
            }
            case MyLoading.MyLoadingState.Unloaded:
            {
                return Lang.Text("Download.Install.State.UnknownUnloaded");
            }

            default:
            {
                return null;
            }
        }
    }

    private void BtnBack_Click(object sender, EventArgs e)
    {
        ExitSelectPage();
    }


    // 页面切换动画
    public bool isInSelectPage;
    private bool isFirstLoaded;

    private void EnterSelectPage()
    {
        if (isInSelectPage)
            return;
        isInSelectPage = true;

        PanInner.Margin = new Thickness(25d, 10d, 25d, 40d);

        autoSelectedFabricApi = false;
        autoSelectedOptiFabric = false;
        isSelectNameEdited = false;
        PanSelect.Visibility = Visibility.Visible;
        PanSelect.IsHitTestVisible = true;
        PanMinecraft.IsHitTestVisible = false;
        PanBack.IsHitTestVisible = false;
        PanBack.ScrollToHome();

        disabledPageAnimControls.Remove(BtnStart);
        BtnStart.Show = true;
        CardOptiFine.IsSwapped = true;
        CardLiteLoader.IsSwapped = true;
        CardForge.IsSwapped = true;
        CardNeoForge.IsSwapped = true;
        CardCleanroom.IsSwapped = true;
        CardFabric.IsSwapped = true;
        CardFabricApi.IsSwapped = true;
        CardOptiFabric.IsSwapped = true;
        CardLabyMod.IsSwapped = true;

        if (!States.Hint.InstallPageBack)
        {
            States.Hint.InstallPageBack = true;
            HintService.Hint(Lang.Text("Download.Install.Hint.MinecraftBack"));
        }

        // 如果在选择页面按了刷新键，选择页的东西可能会由于动画被隐藏，但不会由于加载结束而再次显示，因此这里需要手动恢复
        foreach (var control in GetAllAnimControls(PanSelect))
        {
            control.Opacity = 1d;
            if (control.RenderTransform is null || control.RenderTransform is TranslateTransform)
                control.RenderTransform = new TranslateTransform();
        }

        // 启动 Forge 加载
        if (McInstanceInfo.IsFormatFit(_vanillaName))
        {
            var forgeLoader =
                new ModLoader.LoaderTask<string, List<ModDownload.DlForgeVersionEntry>>(
                    "DlForgeVersion " + _vanillaName, ModDownload.DlForgeVersionMain);
            LoadForge.State = forgeLoader;
            forgeLoader.Start(_vanillaName);
        }

        // 启动 Fabric API、Legacy Fabric API、OptiFabric、LabyMod 加载
        ModDownload.dlFabricApiLoader.Start();
        ModDownload.dlLegacyFabricApiLoader.Start();
        ModDownload.dlOptiFabricLoader.Start();
        ModDownload.dlLabyModListLoader.Start();

        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaOpacity(PanMinecraft, -PanMinecraft.Opacity, 70, 10),
            ModAnimation.AaTranslateX(PanMinecraft, -50 - ((TranslateTransform)PanMinecraft.RenderTransform).X, 90, 10),
            ModAnimation.AaCode(() =>
            {
                PanBack.ScrollToHome();
                TextSelectName.Validate();
                OptiFine_Loaded();
                LiteLoader_Loaded();
                Forge_Loaded();
                NeoForge_Loaded();
                Cleanroom_Loaded();
                Fabric_Loaded();
                LegacyFabric_Loaded();
                FabricApi_Loaded();
                LegacyFabricApi_Loaded();
                OptiFabric_Loaded();
                LabyMod_Loaded();
                ReloadSelected();
                PanMinecraft.Visibility = Visibility.Collapsed;
            }, after: true),
            ModAnimation.AaOpacity(PanSelect, 1d - PanSelect.Opacity, 70, 100),
            ModAnimation.AaTranslateX(PanSelect, -((TranslateTransform)PanSelect.RenderTransform).X, 160, 100,
                new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.ExtraStrong)),
            ModAnimation.AaCode(() =>
            {
                PanBack.IsHitTestVisible = true;
                // 初始化 Binding
                if (isFirstLoaded)
                    return;
                isFirstLoaded = true;
                BtnOptiFineClearInner.SetBinding(Shape.FillProperty,
                    new Binding("Foreground") { Source = CardOptiFine.MainTextBlock, Mode = BindingMode.OneWay });
                BtnLiteLoaderClearInner.SetBinding(Shape.FillProperty,
                    new Binding("Foreground") { Source = CardLiteLoader.MainTextBlock, Mode = BindingMode.OneWay });
                BtnForgeClearInner.SetBinding(Shape.FillProperty,
                    new Binding("Foreground") { Source = CardForge.MainTextBlock, Mode = BindingMode.OneWay });
                BtnNeoForgeClearInner.SetBinding(Shape.FillProperty,
                    new Binding("Foreground") { Source = CardNeoForge.MainTextBlock, Mode = BindingMode.OneWay });
                BtnCleanroomClearInner.SetBinding(Shape.FillProperty,
                    new Binding("Foreground") { Source = CardCleanroom.MainTextBlock, Mode = BindingMode.OneWay });
                BtnFabricClearInner.SetBinding(Shape.FillProperty,
                    new Binding("Foreground") { Source = CardFabric.MainTextBlock, Mode = BindingMode.OneWay });
                BtnLegacyFabricClearInner.SetBinding(Shape.FillProperty,
                    new Binding("Foreground") { Source = CardLegacyFabric.MainTextBlock, Mode = BindingMode.OneWay });
                BtnFabricApiClearInner.SetBinding(Shape.FillProperty,
                    new Binding("Foreground") { Source = CardFabricApi.MainTextBlock, Mode = BindingMode.OneWay });
                BtnLegacyFabricApiClearInner.SetBinding(Shape.FillProperty,
                    new Binding("Foreground")
                        { Source = CardLegacyFabricApi.MainTextBlock, Mode = BindingMode.OneWay });
                BtnLabyModClearInner.SetBinding(Shape.FillProperty,
                    new Binding("Foreground") { Source = CardLabyMod.MainTextBlock, Mode = BindingMode.OneWay });
                BtnOptiFabricClearInner.SetBinding(Shape.FillProperty,
                    new Binding("Foreground") { Source = CardOptiFabric.MainTextBlock, Mode = BindingMode.OneWay });
            }, after: true)
        }, "FrmDownloadInstall SelectPageSwitch", true);
    }

    public void ExitSelectPage()
    {
        if (!isInSelectPage)
            return;
        isInSelectPage = false;

        PanInner.Margin = new Thickness(25d, 10d, 25d, 25d);

        disabledPageAnimControls.Add(BtnStart);
        BtnStart.Show = false;
        ClearSelected(); // 清除已选择项
        PanMinecraft.Visibility = Visibility.Visible;
        PanSelect.IsHitTestVisible = false;
        PanMinecraft.IsHitTestVisible = true;
        PanBack.IsHitTestVisible = false;
        PanBack.ScrollToHome();

        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaOpacity(PanSelect, -PanSelect.Opacity, 70, 10),
            ModAnimation.AaTranslateX(PanSelect, 50d - ((TranslateTransform)PanSelect.RenderTransform).X, 90, 10),
            ModAnimation.AaCode(() => PanBack.ScrollToHome(), after: true),
            ModAnimation.AaOpacity(PanMinecraft, 1d - PanMinecraft.Opacity, 70, 100),
            ModAnimation.AaTranslateX(PanMinecraft, -((TranslateTransform)PanMinecraft.RenderTransform).X, 160, 100,
                new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.ExtraStrong)),
            ModAnimation.AaCode(() =>
            {
                PanSelect.Visibility = Visibility.Collapsed;
                PanBack.IsHitTestVisible = true;
            }, after: true)
        }, "FrmDownloadInstall SelectPageSwitch");
    }

    public void MinecraftSelected(MyListItem sender, MouseButtonEventArgs e)
    {
        _vanillaName = sender.Title;
        _vanillaData = (JsonObject)(dynamic)sender.Tag;
        _vanillaIcon = sender.Logo;
        EnterSelectPage();
    }



    // Minecraft
    private string? _vanillaName;
    private JsonObject? _vanillaData;
    private string? _vanillaIcon;
    private int VanillaDrop => McInstanceInfo.VersionToDrop(_vanillaName, true);

    // OptiFine
    private ModDownload.DlOptiFineListEntry? selectedOptiFine;

    /// <summary>
    ///     选定的主 Mod 加载器。
    /// </summary>
    private ModLoaderType? _selectedLoaderType;

    /// <summary>
    ///     选定的 Mod Loader API 名称，内容应为 Fabric API
    /// </summary>
    private string? selectedAPIName;

    // LiteLoader
    private ModDownload.DlLiteLoaderListEntry? selectedLiteLoader;

    // Forge
    private ModDownload.DlForgeVersionEntry? selectedForge;

    // Cleanroom
    private ModDownload.DlCleanroomListEntry? selectedCleanroom;

    // NeoForge
    private ModDownload.DlNeoForgeListEntry? selectedNeoForge;

    // Fabric
    private string? selectedFabric;

    // FabricApi
    private ModComp.CompFile? selectedFabricApi;

    // LegacyFabric
    private string? selectedLegacyFabric;

    // Legacy FabricApi
    private ModComp.CompFile? selectedLegacyFabricApi;

    // LabyMod
    private string? selectedLabyModChannel;
    private string? selectedLabyModCommitRef;
    private string? selectedLabyModVersion;
    private string? selectedLabyModBaseVersion;

    // OptiFabric
    private ModComp.CompFile? selectedOptiFabric;

    private bool _ReloadSelected_Ongoing; // #3742 中，LoadOptiFineGetError 会初始化 LoadOptiFine，触发事件 LoadOptiFine.StateChanged，导致再次调用 SelectReload

    private bool isSelectNameEdited;
    private bool isSelectNameChanging;

    /// <summary>
    ///     当 MC 版本列表加载完时，立即自动选择的版本。用于外部调用。
    /// </summary>
    public static string? mcVersionWaitingForSelect = null;

    private bool autoSelectedFabricApi;

    private bool autoSelectedLegacyFabricApi;

    private bool autoSelectedOptiFabric;

}
