using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.UI;
using PCL.Core.Utils;

namespace PCL;

public partial class PageInstanceInstall
{
    private enum InstallAction
    {
        Modify,
        Reset
    }

    private bool isLoad;
    private string? lastVersionName;
    private InstallAction _installAction;

    public PageInstanceInstall()
    {
        Initialized += (a, b) => LoaderInit();
        Loaded += (a, b) => Init();
        InitializeComponent();
        LoadMinecraft.Text = Lang.Text("Download.Version.LoadingList");
    }

    private void LoaderInit()
    {
        disabledPageAnimControls.Add(BtnSelectStart);
        // PageLoaderInit(LoadMinecraft, PanLoad, PanBack, Nothing, DlClientListLoader, AddressOf LoadMinecraft_OnFinish)
        PageLoaderInit(LoadMinecraft, PanLoad, PanAllBack, null, ModDownload.dlClientListLoader, _ => GetCurrentInfo());
        LoadOptiFine.StateChanged += (_, _, _) => { OptiFine_Loaded(); ReloadSelected(); };
        LoadLiteLoader.StateChanged += (_, _, _) => { LiteLoader_Loaded(); ReloadSelected(); };
        LoadForge.StateChanged += (_, _, _) => { Forge_Loaded(); ReloadSelected(); };
        LoadNeoForge.StateChanged += (_, _, _) => { NeoForge_Loaded(); ReloadSelected(); };
        LoadCleanroom.StateChanged += (_, _, _) => { Cleanroom_Loaded(); ReloadSelected(); };
        LoadFabric.StateChanged += (_, _, _) => { Fabric_Loaded(); ReloadSelected(); };
        LoadFabricApi.StateChanged += (_, _, _) => { FabricApi_Loaded(); ReloadSelected(); };
        LoadLegacyFabric.StateChanged += (_, _, _) => { LegacyFabric_Loaded(); ReloadSelected(); };
        LoadLegacyFabricApi.StateChanged += (_, _, _) => { LegacyFabricApi_Loaded(); ReloadSelected(); };
        LoadOptiFabric.StateChanged += (_, _, _) => { OptiFabric_Loaded(); ReloadSelected(); };
        LoadLabyMod.StateChanged += (_, _, _) => { LabyMod_Loaded(); ReloadSelected(); };
        PageExit += () => isInSelectPage = false;
    }

    private void Init()
    {
        PanBack.ScrollToHome();

        GetCurrentInfo();

        var needRefresh = lastVersionName is null || (lastVersionName ?? "") != (_vanillaName ?? "");
        lastVersionName = _vanillaName;

        ModDownload.dlOptiFineListLoader.Start(isForceRestart: needRefresh);
        ModDownload.dlLiteLoaderListLoader.Start(isForceRestart: needRefresh);
        ModDownload.dlFabricListLoader.Start(isForceRestart: needRefresh);
        ModDownload.dlNeoForgeListLoader.Start(isForceRestart: needRefresh);
        ModDownload.dlCleanroomListLoader.Start(isForceRestart: needRefresh);
        ModDownload.dlLabyModListLoader.Start(isForceRestart: needRefresh);
        ModDownload.dlLegacyFabricListLoader.Start(isForceRestart: needRefresh);
        ModDownload.dlFabricApiLoader.Start(isForceRestart: needRefresh);
        ModDownload.dlLegacyFabricApiLoader.Start(isForceRestart: needRefresh);
        ModDownload.dlOptiFabricLoader.Start(isForceRestart: needRefresh);

        // 非重复加载部分
        if (isLoad)
        {
            ReloadSelected();
            return;
        }
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


    private void BtnSelectStart_Click(object sender, MouseButtonEventArgs mouseButtonEventArgs)
    {
        if (!RestrictedFeaturePolicy.Check("minecraft-loader-install")) return;
        if (!HasReplacementVersion()) return;
        var changingInstance = PageInstanceLeft.McInstance;
        string minecraftRoot;
        try
        {
            minecraftRoot = PCL.Core.Minecraft.Instances.InstanceRootContext.Require(
                changingInstance.PathInstance, ModFolder.mcFolderSelected);
        }
        catch (InvalidDataException)
        {
            HintService.Hint(Lang.Text("Instance.Migration.ContextChanged"), HintType.Warning);
            return;
        }
        // Quilt 实例无法通过安装管线重装/修改（已移除 Quilt 安装支持）
        if (PageInstanceLeft.McInstance.Info.HasQuilt)
        {
            HintService.Hint(Lang.Text("Instance.Overall.Reset.QuiltUnsupported"));
            return;
        }

        // 确认版本隔离
        if (_selectedLoaderType is not null &&
            (Config.Launch.IndieSolutionV2 == 0 ||
             Config.Launch.IndieSolutionV2 == 2))
            if (ModMain.MyMsgBox(
                    Lang.Text("Download.Install.InstanceIsolation.Warning.Message"), Lang.Text("Download.Install.InstanceIsolation.Warning.Title"), Lang.Text("Download.Install.InstanceIsolation.Warning.Cancel"), Lang.Text("Download.Install.InstanceIsolation.Warning.Continue")) == 1)
                return;

        if (_installAction == InstallAction.Reset)
            if (ModMain.MyMsgBox(
                    Lang.Text("Instance.Install.Reset.Message"),
                    Lang.Text("Instance.Install.Reset.Title"),
                    Lang.Text("Common.Action.Continue"),
                    Lang.Text("Common.Action.Cancel")
                ) == 2)
                return;

        // Both explicit backup acknowledgements must finish before any instance mutation.
        if (_installAction == InstallAction.Modify)
        {
            if (!ConfirmLoaderBackup(changingInstance) || !IsLoaded
                || !ReferenceEquals(changingInstance, PageInstanceLeft.McInstance)) return;
            var versionChanged = !string.Equals(changingInstance.Info.VanillaName, _vanillaName, StringComparison.Ordinal);
            var loaderChanged = GetExistingLoader(changingInstance) != _selectedLoaderType;
            bool? migrateMods = false;
            if (versionChanged || loaderChanged)
            {
                migrateMods = AskToMigrateMods(changingInstance);
                if (migrateMods is null || !IsLoaded || !ReferenceEquals(changingInstance, PageInstanceLeft.McInstance)) return;
            }
            // Same-loader build changes need exactly the same staging/validation/rollback as cross-loader changes.
            var modification = BuildModificationRequest(changingInstance);
            if (selectedFabricApi?.Equals(_currentFabricApi) == true) modification.fabricApi = null;
            if (selectedLegacyFabricApi?.Equals(_currentLegacyFabricApi) == true) modification.legacyFabricApi = null;
            if (selectedOptiFabric?.Equals(_currentOptiFabric) == true) modification.optiFabric = null;
            var apiReplacements = new List<InstanceModificationRunner.ApiReplacement>();
            if (modification.fabricApi is not null && _currentFabricApiPath is not null)
                apiReplacements.Add(new(_currentFabricApiPath, modification.fabricApi));
            if (modification.legacyFabricApi is not null && _currentLegacyFabricApiPath is not null)
                apiReplacements.Add(new(_currentLegacyFabricApiPath, modification.legacyFabricApi));
            if (modification.optiFabric is not null && _currentOptiFabricPath is not null)
                apiReplacements.Add(new(_currentOptiFabricPath, modification.optiFabric));
            InstanceModificationRunner.Start(changingInstance, minecraftRoot, modification, migrateMods.Value,
                CheckAutomaticMigrationDependencies.Checked == true, apiReplacements);
            return;
        }
        if (!IsLoaded || !ReferenceEquals(changingInstance, PageInstanceLeft.McInstance)) return;
        try
        {
            _ = PCL.Core.Minecraft.Instances.InstanceRootContext.Require(
                changingInstance.PathInstance, ModFolder.mcFolderSelected);
        }
        catch (InvalidDataException)
        {
            HintService.Hint(Lang.Text("Instance.Migration.ContextChanged"), HintType.Warning);
            return;
        }

        // 删除 LabyMod Neo 文件
        if ((PageInstanceLeft.McInstance.PathIndie ?? "") != (PageInstanceLeft.McInstance.PathInstance ?? "") &&
            PageInstanceLeft.McInstance.Info.HasLabyMod)
            Directory.Delete(System.IO.Path.Combine(PageInstanceLeft.McInstance.PathIndie, "labymod-neo"), true);
        // 备份实例核心文件
        ModBase.CopyFile(PageInstanceLeft.McInstance.PathInstance + PageInstanceLeft.McInstance.Name + ".json",
            PageInstanceLeft.McInstance.PathInstance + @"PCLInstallBackups\" + PageInstanceLeft.McInstance.Name + ".json");
        if (File.Exists(PageInstanceLeft.McInstance.PathInstance + PageInstanceLeft.McInstance.Name + ".jar"))
            ModBase.CopyFile(PageInstanceLeft.McInstance.PathInstance + PageInstanceLeft.McInstance.Name + ".jar",
                PageInstanceLeft.McInstance.PathInstance + @"PCLInstallBackups\" + PageInstanceLeft.McInstance.Name +
                ".jar");
        // 确认独立 API (如 Fabric API 等) 是否需要被修改
        if (selectedFabricApi?.Equals(_currentFabricApi) == true)
            selectedFabricApi = null;
        if (selectedLegacyFabricApi?.Equals(_currentLegacyFabricApi) == true)
            selectedLegacyFabricApi = null;
        if (selectedOptiFabric?.Equals(_currentOptiFabric) == true)
            selectedOptiFabric = null;
        // 提交安装申请
        var request = new ModDownloadLib.McInstallRequest
        {
            targetInstanceName = PageInstanceLeft.McInstance.Name,
            targetInstanceFolder = changingInstance.PathInstance,
            minecraftJson = _vanillaData?["url"].ToString(),
            minecraftName = _vanillaName,
            optiFineEntry = selectedOptiFine,
            forgeEntry = selectedForge,
            neoForgeEntry = selectedNeoForge,
            neoForgeVersion = selectedNeoForgeVersion,
            cleanroomEntry = selectedCleanroom,
            cleanroomVersion = selectedCleanroomVersion,
            fabricVersion = selectedFabric,
            fabricApi = selectedFabricApi,
            optiFabric = selectedOptiFabric,
            liteLoaderEntry = selectedLiteLoader,
            labyModChannel = selectedLabyModChannel,
            labyModCommitRef = selectedLabyModCommitRef,
            legacyFabricVersion = selectedLegacyFabric,
            legacyFabricApi = selectedLegacyFabricApi
        };
        BtnSelectStart.IsEnabled = false;
        if (!ModDownloadLib.McInstall(request, _installAction == InstallAction.Modify ? Lang.Text("Instance.Install.Action.ModifyLabel") : Lang.Text("Common.Action.Reset")))
            return;
        // 删除旧的独立 API 文件
        if (selectedFabricApi is not null && _currentFabricApiPath is not null)
            File.Delete(_currentFabricApiPath);
        if (selectedLegacyFabricApi is not null && _currentLegacyFabricApiPath is not null)
            File.Delete(_currentLegacyFabricApiPath);
        if (selectedOptiFabric is not null && _currentOptiFabricPath is not null)
            File.Delete(_currentOptiFabricPath);
        // 返回主页
        ModMain.frmMain.PageChange(new FormMain.PageStackData { page = FormMain.PageType.Launch });
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
                return message == Lang.Text("Download.Install.State.NoVersion") ? Lang.Text("Download.Install.State.NoVersion") : Lang.Text("Download.Install.State.GetFailed", message);
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


    // 页面切换动画
    public bool isInSelectPage;
    private bool isFirstLoaded;

    private void EnterSelectPage()
    {
        if (isInSelectPage)
            return;
        isInSelectPage = true;

        disabledPageAnimControls.Remove(BtnSelectStart);
        BtnSelectStart.Show = true;
        autoSelectedFabricApi = false;
        autoSelectedOptiFabric = false;
        PanSelect.Visibility = Visibility.Visible;
        PanSelect.IsHitTestVisible = true;
        PanMinecraft.IsHitTestVisible = false;
        PanBack.IsHitTestVisible = false;
        PanBack.ScrollToHome();

        CardMinecraft.IsSwapped = true;
        CardOptiFine.IsSwapped = true;
        CardLiteLoader.IsSwapped = true;
        CardForge.IsSwapped = true;
        CardNeoForge.IsSwapped = true;
        CardCleanroom.IsSwapped = true;
        CardFabric.IsSwapped = true;
        CardFabricApi.IsSwapped = true;
        CardOptiFabric.IsSwapped = true;
        CardLabyMod.IsSwapped = true;
        CardLegacyFabric.IsSwapped = true;
        CardLegacyFabricApi.IsSwapped = true;

        if (!(bool)States.Hint.InstallPageBack)
        {
            States.Hint.InstallPageBack = true;
            HintService.Hint(Lang.Text("Download.Install.Hint.MinecraftBack"));
        }

        // 如果在选择页面按了刷新键，选择页的东西可能会由于动画被隐藏，但不会由于加载结束而再次显示，因此这里需要手动恢复
        foreach (var Card in GetAllAnimControls(PanSelect))
        {
            Card.Opacity = 1d;
            Card.RenderTransform = new TranslateTransform();
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

        // 启动 Fabric API、Legacy Fabric API、OptiFabric 加载
        ModDownload.dlFabricApiLoader.Start();
        ModDownload.dlLegacyFabricApiLoader.Start();
        ModDownload.dlOptiFabricLoader.Start();

        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaOpacity(PanMinecraft, -PanMinecraft.Opacity, 100, 10),
            ModAnimation.AaCode(() =>
            {
                PanBack.ScrollToHome();
                OptiFine_Loaded();
                LiteLoader_Loaded();
                Forge_Loaded();
                NeoForge_Loaded();
                Cleanroom_Loaded();
                Fabric_Loaded();
                LegacyFabric_Loaded();
                FabricApi_Loaded();
                LegacyFabricApi_Loaded();
                LabyMod_Loaded();
                OptiFabric_Loaded();
                ReloadSelected();
            }, after: true),
            ModAnimation.AaOpacity(PanSelect, 1d - PanSelect.Opacity, 250, 150),
            ModAnimation.AaCode(() =>
            {
                PanMinecraft.Visibility = Visibility.Collapsed;
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
                BtnLegacyFabricClearInner.SetBinding(Shape.FillProperty,
                    new Binding("Foreground") { Source = CardLegacyFabric.MainTextBlock, Mode = BindingMode.OneWay });
                BtnNeoForgeClearInner.SetBinding(Shape.FillProperty,
                    new Binding("Foreground") { Source = CardNeoForge.MainTextBlock, Mode = BindingMode.OneWay });
                BtnCleanroomClearInner.SetBinding(Shape.FillProperty,
                    new Binding("Foreground") { Source = CardCleanroom.MainTextBlock, Mode = BindingMode.OneWay });
                BtnFabricClearInner.SetBinding(Shape.FillProperty,
                    new Binding("Foreground") { Source = CardFabric.MainTextBlock, Mode = BindingMode.OneWay });
                BtnLegacyFabricApiClearInner.SetBinding(Shape.FillProperty,
                    new Binding("Foreground")
                        { Source = CardLegacyFabricApi.MainTextBlock, Mode = BindingMode.OneWay });
                BtnFabricApiClearInner.SetBinding(Shape.FillProperty,
                    new Binding("Foreground") { Source = CardFabricApi.MainTextBlock, Mode = BindingMode.OneWay });
                BtnLabyModClearInner.SetBinding(Shape.FillProperty,
                    new Binding("Foreground") { Source = CardLabyMod.MainTextBlock, Mode = BindingMode.OneWay });
                BtnOptiFabricClearInner.SetBinding(Shape.FillProperty,
                    new Binding("Foreground") { Source = CardOptiFabric.MainTextBlock, Mode = BindingMode.OneWay });
            }, after: true)
        }, "FrmInstanceInstall SelectPageSwitch", true);
    }

    public void ExitSelectPage()
    {
        if (!isInSelectPage)
            return;
        isInSelectPage = false;

        LoadMinecraft_OnFinish();

        disabledPageAnimControls.Add(BtnSelectStart);
        BtnSelectStart.Show = false;

        ClearSelected(); // 清除已选择项
        PanMinecraft.Visibility = Visibility.Visible;
        PanSelect.IsHitTestVisible = false;
        PanMinecraft.IsHitTestVisible = true;
        PanBack.IsHitTestVisible = false;
        PanBack.ScrollToHome();

        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaOpacity(PanSelect, -PanSelect.Opacity, 90, 10),
            ModAnimation.AaCode(() => PanBack.ScrollToHome(), after: true),
            ModAnimation.AaOpacity(PanMinecraft, 1d - PanMinecraft.Opacity, 150, 100),
            ModAnimation.AaCode(() =>
            {
                PanSelect.Visibility = Visibility.Collapsed;
                PanBack.IsHitTestVisible = true;
            }, after: true)
        }, "FrmInstanceInstall SelectPageSwitch");
    }

    // 页面切换触发
    public void MinecraftSelected(MyListItem sender, MouseButtonEventArgs e)
    {
        // Loader builds belong to a specific Minecraft version; do not reuse the source build
        // after selecting another game version. This changes pending UI state only.
        ClearSelected();
        _replacementLoader = null;
        _vanillaName = sender.Title;
        _vanillaData = (JsonObject)sender.Tag;
        _vanillaIcon = sender.Logo;
        EnterSelectPage();
    }

    private void CardMinecraft_PreviewSwap(object sender, ModBase.RouteEventArgs e)
    {
        ExitSelectPage();
        e.handled = true;
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
    private string? selectedCleanroomVersion;

    // NeoForge
    private ModDownload.DlNeoForgeListEntry? selectedNeoForge;
    private string? selectedNeoForgeVersion;

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

    // OptiFabric
    private ModComp.CompFile? selectedOptiFabric;

    private bool _ReloadSelected_Ongoing; // #3742 中，LoadOptiFineGetError 会初始化 LoadOptiFine，触发事件 LoadOptiFine.StateChanged，导致再次调用 SelectReload



    private ModComp.CompFile? _currentFabricApi; // 加载完成后直接调用以提高性能
    private string? _currentFabricApiPath;

    private ModComp.CompFile? _currentLegacyFabricApi; // 加载完成后直接调用以提高性能
    private string? _currentLegacyFabricApiPath;

    private ModComp.CompFile? _currentOptiFabric;
    private string? _currentOptiFabricPath;

    private string? currentInfo;

    /// <summary>
    ///     当 MC 版本列表加载完时，立即自动选择的版本。用于外部调用。
    /// </summary>
    public static string? mcVersionWaitingForSelect = null;

    private bool autoSelectedFabricApi;

    private bool autoSelectedLegacyFabricApi;

    private bool autoSelectedOptiFabric;

}
