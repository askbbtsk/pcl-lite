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

public partial class PageInstanceSavesDatapack : IRefreshable
{

    private readonly Dictionary<string, (DateTime CreationTime, long Length)> datapackFileInfoCache = new();



    private readonly MyLocalCompItem.SwipeSelect currentSwipSelect;

    public PageInstanceSavesDatapack()
    {
        currentSwipSelect = new MyLocalCompItem.SwipeSelect { TargetFrm = this };

        InitializeComponent();
        Unloaded += Page_Unloaded;
        Loaded += (_, _) => PageOther_Loaded();
        LoaderInit();
        PageExit += UnselectedAllWithAnimation;
        // Handles
        Load.Click += Load_Click;
        BtnManageOpen.Click += BtnManageOpen_Click;
        BtnHintOpen.Click += BtnManageOpen_Click;
        BtnManageSelectAll.Click += BtnManageSelectAll_Click;
        BtnManageInstall.Click += BtnManageInstall_Click;
        BtnHintInstall.Click += BtnManageInstall_Click;
        BtnManageDownload.Click += BtnManageDownload_Click;
        BtnHintDownload.Click += BtnManageDownload_Click;
        BtnManageInfoExport.Click += BtnManageInfoExport_Click;
        Load.StateChanged += (_, _, _) => UnselectedAllWithAnimation();
        SearchBox.PreviewKeyDown += SearchBox_PreviewKeyDown;
        BtnFilterAll.Check += ChangeFilter;
        BtnFilterCanUpdate.Check += ChangeFilter;
        BtnFilterDisabled.Check += ChangeFilter;
        BtnFilterEnabled.Check += ChangeFilter;
        BtnFilterError.Check += ChangeFilter;
        BtnSort.Click += BtnSortClick;
        BtnSelectEnable.Click += BtnSelectEnable_Click;
        BtnSelectDisable.Click += BtnSelectDisable_Click;
        BtnSelectUpdate.Click += BtnSelectUpdate_Click;
        BtnSelectDelete.Click += BtnSelectDelete_Click;
        BtnSelectCancel.Click += BtnSelectCancel_Click;
        BtnSelectFavorites.Click += BtnSelectFavorites_Click;
        BtnSelectShare.Click += BtnSelectShare_Click;
        SearchBox.TextChanged += SearchRun;
    }

    private bool isLoad;



    /// <summary>
    ///     已加载的数据包 UI 缓存。Key 为数据包的 RawPath。
    /// </summary>
    public Dictionary<string, MyLocalCompItem> datapackItems = new();

    private int bottomBarShownCount;



    /// <summary>
    ///     选择的数据包的路径。
    /// </summary>
    public HashSet<string> selectedDatapacks = new();



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
    ///     记录正在进行数据包更新的 datapacks 文件夹路径。
    /// </summary>
    public static List<string> updatingVersions = new();
    private List<ModLocalComp.LocalCompFile> searchResult;

}
