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

public partial class PageInstanceCompResource : IRefreshable
{

    // 模组信息缓存 - 解决排序时重复创建FileInfo导致的性能问题
    private readonly Dictionary<string, (DateTime CreationTime, long Length)> modFileInfoCache = new();

    public PageInstanceCompResource()
    {
        InitializeComponent();
        Unloaded += Page_Unloaded;
        Loaded += (_, _) => PageOther_Loaded();
        Initialized += (_, _) => LoaderInit();
        PageExit += UnselectedAllWithAnimation;
        Load.Click += Load_Click;
        BtnManageBack.Click += BtnManageBack_Click;
        BtnHintBack.Click += BtnHintBack_Click;
        BtnManageOpen.Click += BtnManageOpen_Click;
        BtnHintOpen.Click += BtnManageOpen_Click;
        BtnManageSelectAll.Click += BtnManageSelectAll_Click;
        BtnManageInstall.Click += BtnManageInstall_Click;
        BtnHintInstall.Click += BtnManageInstall_Click;
        BtnManageInfoExport.Click += BtnManageInfoExport_Click;
        BtnManageDownload.Click += BtnManageDownload_Click;
        BtnHintDownload.Click += BtnManageDownload_Click;
        BtnSchematicDownloadMod.Click += BtnSchematicDownloadMod_Click;
        BtnSchematicVersionSelect.Click += BtnSchematicVersionSelect_Click;
        Load.StateChanged += (_, _, _) => UnselectedAllWithAnimation();
        SearchBox.PreviewKeyDown += SearchBox_PreviewKeyDown;
        BtnFilterAll.Check += ChangeFilter;
        BtnFilterCanUpdate.Check += ChangeFilter;
        BtnFilterDisabled.Check += ChangeFilter;
        BtnFilterEnabled.Check += ChangeFilter;
        BtnFilterError.Check += ChangeFilter;
        BtnFilterDuplicate.Check += ChangeFilter;
        BtnSort.Click += BtnSortClick;
        BtnSelectEnable.Click += BtnSelectED_Click;
        BtnSelectDisable.Click += BtnSelectED_Click;
        BtnSelectUpdate.Click += BtnSelectUpdate_Click;
        BtnSelectDelete.Click += BtnSelectDelete_Click;
        BtnSelectCancel.Click += BtnSelectCancel_Click;
        BtnSelectFavorites.Click += BtnSelectFavorites_Click;
        BtnSelectShare.Click += BtnSelectShare_Click;
        SearchBox.TextChanged += SearchRun;
    }



    private readonly ModComp.CompType currentCompType = ModComp.CompType.Mod;

    private readonly MyLocalCompItem.SwipeSelect currentSwipSelect;

    public PageInstanceCompResource(ModComp.CompType loadCompType)
    {
        currentCompType = loadCompType;
        CurrentFolderPath = ""; // 确保文件夹路径被重置为根目录
        currentSwipSelect = new MyLocalCompItem.SwipeSelect { TargetFrm = this };

        // 此调用是设计器所必需的。
        InitializeComponent();

        // 在 InitializeComponent() 调用之后添加任何初始化。

        if (new[] { ModComp.CompType.Shader, ModComp.CompType.ResourcePack, ModComp.CompType.Schematic }.Contains(
                currentCompType))
        {
            BtnSelectEnable.Visibility = Visibility.Collapsed;
            BtnSelectDisable.Visibility = Visibility.Collapsed;
        }

        // 投影文件管理页隐藏下载按钮
        if (currentCompType == ModComp.CompType.Schematic)
        {
            BtnManageDownload.Visibility = Visibility.Collapsed;
            BtnHintDownload.Visibility = Visibility.Collapsed;
        }

        if (currentCompType == ModComp.CompType.Mod)
            BtnManageJarInJar.Click += (_, _) =>
                ModMain.frmInstanceLeft?.PageChange(FormMain.PageSubType.VersionModJarInJar);
        else
            BtnManageJarInJar.Visibility = Visibility.Collapsed;

        Unloaded += Page_Unloaded;
        Loaded += (_, _) => PageOther_Loaded();
        LoaderInit();
        PageExit += UnselectedAllWithAnimation;
        // Handles
        Load.Click += Load_Click;
        BtnManageBack.Click += BtnManageBack_Click;
        BtnHintBack.Click += BtnHintBack_Click;
        BtnManageOpen.Click += BtnManageOpen_Click;
        BtnHintOpen.Click += BtnManageOpen_Click;
        BtnManageSelectAll.Click += BtnManageSelectAll_Click;
        BtnManageInstall.Click += BtnManageInstall_Click;
        BtnHintInstall.Click += BtnManageInstall_Click;
        BtnManageDownload.Click += BtnManageDownload_Click;
        BtnHintDownload.Click += BtnManageDownload_Click;
        BtnManageInfoExport.Click += BtnManageInfoExport_Click;
        BtnSchematicDownloadMod.Click += BtnSchematicDownloadMod_Click;
        BtnSchematicVersionSelect.Click += BtnSchematicVersionSelect_Click;
        Load.StateChanged += (_, _, _) => UnselectedAllWithAnimation();
        SearchBox.PreviewKeyDown += SearchBox_PreviewKeyDown;
        BtnFilterAll.Check += ChangeFilter;
        BtnFilterCanUpdate.Check += ChangeFilter;
        BtnFilterDisabled.Check += ChangeFilter;
        BtnFilterEnabled.Check += ChangeFilter;
        BtnFilterError.Check += ChangeFilter;
        BtnFilterDuplicate.Check += ChangeFilter;
        BtnSort.Click += BtnSortClick;
        BtnSelectEnable.Click += BtnSelectED_Click;
        BtnSelectDisable.Click += BtnSelectED_Click;
        BtnSelectUpdate.Click += BtnSelectUpdate_Click;
        BtnSelectDelete.Click += BtnSelectDelete_Click;
        BtnSelectCancel.Click += BtnSelectCancel_Click;
        BtnSelectFavorites.Click += BtnSelectFavorites_Click;
        BtnSelectShare.Click += BtnSelectShare_Click;
        SearchBox.TextChanged += SearchRun;
    }

    private bool isLoad;



    /// <summary>
    ///     当前显示的文件夹路径。空字符串表示根目录。
    /// </summary>
    public string CurrentFolderPath { get; set; } = "";



    /// <summary>
    ///     已加载的 Mod UI 缓存，不确保按显示顺序排列。Key 为 Mod 的 RawPath。
    /// </summary>
    public Dictionary<string, MyLocalCompItem> modItems = new();

    private int bottomBarShownCount;



    /// <summary>
    ///     选择的 Mod 的路径（不含 .disabled 和 .old）。
    /// </summary>
    public HashSet<string> selectedMods = new();



    public FilterType Filter
    {
        get => field;
        set
        {
            if (field == value)
                return;
            field = value;
            switch (value)
            {
                case FilterType.All:
                {
                    BtnFilterAll.Checked = true;
                    break;
                }
                case FilterType.Enabled:
                {
                    BtnFilterEnabled.Checked = true;
                    break;
                }
                case FilterType.Disabled:
                {
                    BtnFilterDisabled.Checked = true;
                    break;
                }
                case FilterType.CanUpdate:
                {
                    BtnFilterCanUpdate.Checked = true;
                    break;
                }
                case FilterType.Duplicate:
                {
                    BtnFilterDuplicate.Checked = true;
                    break;
                }

                default:
                {
                    BtnFilterError.Checked = true;
                    break;
                }
            }

            RefreshUI();
        }
    } = FilterType.All;



    private SortMethod currentSortMethod = SortMethod.CompName;

    private readonly object sortLock = new();

    /// <summary>
    ///     记录正在进行 Mod 更新的 mods 文件夹路径。
    /// </summary>
    public static List<string> updatingVersions = new();
    private List<ModLocalComp.LocalCompFile> searchResult;
    private CancellationTokenSource _cancelToken;

}
