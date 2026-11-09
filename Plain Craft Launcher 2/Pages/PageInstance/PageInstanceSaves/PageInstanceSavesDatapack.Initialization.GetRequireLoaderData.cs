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

    private ModLocalComp.CompLocalLoaderData GetRequireLoaderData()
    {
        var res = new ModLocalComp.CompLocalLoaderData();
        res.gameVersion = PageInstanceLeft.McInstance;
        res.frm = null;
        res.loaders = new[] { ModComp.CompLoaderType.Minecraft }.ToList();
        res.compPath = Path.Combine(PageInstanceSavesLeft.currentSave, "datapacks");
        res.compType = ModComp.CompType.DataPack;
        return res;
    }


    public void PageOther_Loaded()
    {
        if (ModMain.frmMain.pageLast.page != FormMain.PageType.CompDetail)
            PanBack.ScrollToHome();
        ModAnimation.AniControlEnabled += 1;
        selectedDatapacks.Clear();
        ReloadDatapackFileList();
        ChangeAllSelected(false);
        ModAnimation.AniControlEnabled -= 1;

        // 非重复加载部分
        if (isLoad)
            return;
        isLoad = true;

        ModMain.frmMain.KeyDown += FrmMain_KeyDown;
        // 调整按钮边距（这玩意儿没法从 XAML 改）
        foreach (MyRadioButton Btn in PanFilter.Children)
            Btn.LabText.Margin = new Thickness(-2, 0d, 8d, 0d);
    }


    /// <summary>
    ///     刷新数据包列表。
    /// </summary>
    public void ReloadDatapackFileList(bool forceReload = false)
    {
        if (LoaderRun(forceReload
                ? ModLoader.LoaderFolderRunType.ForceRun
                : ModLoader.LoaderFolderRunType.RunOnUpdated))
        {
            ModBase.Log("[System] 已刷新数据包列表");
            datapackFileInfoCache.Clear();

            ModBase.RunInUi(() =>
            {
                Filter = FilterType.All;
                PanBack.ScrollToHome();
                SearchBox.Text = "";
            });
        }
    }


    // 强制刷新
    private void RefreshSelf()
    {
        Refresh();
    }


    void IRefreshable.Refresh()
    {
        RefreshSelf();
    }


    public void Refresh()
    {
        ModMain.frmInstanceSavesDatapack.ReloadDatapackFileList(true);
        ModBase.Log("[Datapack] 刷新数据包列表");
    }


    private void LoaderInit()
    {
        PageLoaderInit(Load, PanLoad, PanAllBack, null, ModLocalComp.compResourceListLoader,
            _ => LoadUIFromLoaderOutput(), () => ModComp.CompType.DataPack, false);
    }


    private void Load_Click(object sender, MouseButtonEventArgs e)
    {
        if (ModLocalComp.compResourceListLoader.State == ModBase.LoadState.Failed)
            LoaderRun(ModLoader.LoaderFolderRunType.ForceRun);
    }


    public bool LoaderRun(ModLoader.LoaderFolderRunType type)
    {
        var loadPath = Path.Combine(PageInstanceSavesLeft.currentSave, "datapacks");
        return ModLoader.LoaderFolderRun(ModLocalComp.compResourceListLoader, loadPath, type,
            loaderInput: GetRequireLoaderData());
    }

}
