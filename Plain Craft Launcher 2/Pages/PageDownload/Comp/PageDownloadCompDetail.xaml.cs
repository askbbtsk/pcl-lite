using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using FluentValidation;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.Minecraft.ResourceProject;
using PCL.Core.UI;
using PCL.Core.Utils;
using PCL.Core.Utils.Validate;
using PCL.Network;
using PCL.Network.Loaders;
using Control = System.Windows.Forms.Control;

namespace PCL;

public partial class PageDownloadCompDetail
{
    // 资源下载；整合包另存为
    public static Dictionary<ModComp.CompType, string> cachedFolder = new(); // 仅在本次缓存的下载文件夹
    private MyCompItem _compItem;
    private bool _isFirstInit = true;


    private readonly ModLoader.LoaderTask<int, List<ModComp.CompFile>> _compFileLoader;

    public PageDownloadCompDetail()
    {
        _compFileLoader = new ModLoader.LoaderTask<int, List<ModComp.CompFile>>("Comp File", task =>
        {
            LoadTargetFromAdditional();
            var result = ModComp.CompFilesGet(_project.Id, _project.FromCurseForge);
            if (task.IsAborted)
                return;
            task.output = result;
        });
        Initialized += PageDownloadCompDetail_Inited;
        Loaded += (_, _) => LoadTargetFromAdditional();
        PageEnter += Init;
        InitializeComponent();
        Load.StateChanged += Load_State;
        BtnIntroWeb.Click += BtnIntroWeb_Click;
        BtnIntroWiki.Click += BtnIntroWiki_Click;
        BtnIntroCopy.Click += BtnIntroCopy_Click;
        BtnFavorites.Click += BtnFavorites_Click;
        BtnIntroLinkCopy.Click += BtnIntroLinkCopy_Click;
        BtnTranslate.Click += BtnTranslate_Click;
    }

    private ModComp.CompProject _project;
    private string _targetInstance;
    private ModComp.CompLoaderType _targetLoader;

    /// <summary>
    ///     当前页面应展示的内容类别。可能为 Any。
    /// </summary>
    private ModComp.CompType _pageType;

    private string? _instanceFilter;
    private string? _modLoaderFilter;
    private bool groupedDrop; // 是否按 Drop 筛选（1.21 / 1.20 / 1.19 / ...）而非小版本号（1.21.1 / 1.21 / 1.20.4 / ...）

    private bool groupedOld; // 是否折叠远古版本为一个选项

}
