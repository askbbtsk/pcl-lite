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

public partial class PageInstanceCompResource
{

    private ModLocalComp.CompLocalLoaderData GetRequireLoaderData()
    {
        var res = new ModLocalComp.CompLocalLoaderData();
        res.gameVersion = PageInstanceLeft.McInstance;
        res.frm = this;
        var requireLoaders = new List<ModComp.CompLoaderType>();
        switch (currentCompType)
        {
            case ModComp.CompType.Mod:
            {
                requireLoaders = ModLocalComp.GetCurrentVersionModLoader();
                break;
            }
            case ModComp.CompType.ResourcePack:
            {
                requireLoaders = new[] { ModComp.CompLoaderType.Minecraft }.ToList();
                break;
            }
            case ModComp.CompType.Shader:
            {
                requireLoaders = new[]
                {
                    ModComp.CompLoaderType.OptiFine, ModComp.CompLoaderType.Iris, ModComp.CompLoaderType.Vanilla,
                    ModComp.CompLoaderType.Canvas
                }.ToList();
                break;
            }
            case ModComp.CompType.Schematic:
            {
                requireLoaders = new[] { ModComp.CompLoaderType.Minecraft }.ToList();
                break;
            }
        }

        res.loaders = requireLoaders;
        res.compPath = PageInstanceLeft.McInstance.PathIndie +
                       (PageInstanceLeft.McInstance.Info.HasLabyMod
                           ? Path.Combine("labymod-neo", "fabric", PageInstanceLeft.McInstance.Info.VanillaName)
                           : "") + ModLocalComp.GetPathNameByCompType(currentCompType) + @"\";
        res.compType = currentCompType;
        return res;
    }


    public void PageOther_Loaded()
    {
        CurrentFolderPath = string.Empty;

        if (ModMain.frmMain.pageLast.page != FormMain.PageType.CompDetail)
            PanBack.ScrollToHome();
        ModAnimation.AniControlEnabled += 1;
        selectedMods.Clear();
        ReloadCompFileList();
        ChangeAllSelected(false);
        ModAnimation.AniControlEnabled -= 1;
        OfferModIndex();

        // 非重复加载部分
        if (isLoad)
            return;
        isLoad = true;

        // 检查是否为原理图管理界面且首次打开
        if (currentCompType == ModComp.CompType.Schematic && !States.Hint.SchematicFirstTime)
            // 显示首次打开提示
            ModBase.RunInUi(() =>
            {
                ModMain.MyMsgBox(Lang.Text("Instance.Saves.Folder.DoubleClickHint.Message"), Lang.Text("Instance.Saves.Folder.DoubleClickHint.Title"), Lang.Text("Common.Action.GotIt"));
                States.Hint.SchematicFirstTime = true;
            }, true);

        ModMain.frmMain.KeyDown += FrmMain_KeyDown;
        // 调整按钮边距（这玩意儿没法从 XAML 改）
        foreach (MyRadioButton Btn in PanFilter.Children)
            Btn.LabText.Margin = new Thickness(-2, 0d, 8d, 0d);
    }


    /// <summary>
    ///     刷新 Mod 列表。
    /// </summary>
    public void ReloadCompFileList(bool forceReload = false)
    {
        if (LoaderRun(forceReload
                ? ModLoader.LoaderFolderRunType.ForceRun
                : ModLoader.LoaderFolderRunType.RunOnUpdated))
        {
            ModBase.Log($"[System] 已刷新 {currentCompType} 列表");
            modFileInfoCache.Clear();

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
        Refresh(currentCompType);
    }


    void IRefreshable.Refresh()
    {
        RefreshSelf();
    }


    public static void Refresh(ModComp.CompType whichPage)
    {
        // 强制刷新
        try
        {
            ModComp.compProjectCache.Clear();
            ModComp.compFilesCache.Clear();
            File.Delete(ModBase.pathTemp + @"Cache\LocalComp.json");
            ModBase.Log("[CompResource] 由于点击刷新按钮，清理本地工程信息缓存");
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "强制刷新时清理本地工程信息缓存失败");
        }

        switch (whichPage)
        {
            case ModComp.CompType.Mod:
            {
                if (ModMain.frmInstanceMod is not null)
                    ModMain.frmInstanceMod.ReloadCompFileList(true); // 无需 Else，还没加载刷个鬼的新
                ModMain.frmInstanceLeft.ItemMod.Checked = true;
                break;
            }
            case ModComp.CompType.ResourcePack:
            {
                if (ModMain.frmInstanceResourcePack is not null)
                    ModMain.frmInstanceResourcePack.ReloadCompFileList(true);
                ModMain.frmInstanceLeft.ItemResourcePack.Checked = true;
                break;
            }
            case ModComp.CompType.Shader:
            {
                if (ModMain.frmInstanceShader is not null)
                    ModMain.frmInstanceShader.ReloadCompFileList(true);
                ModMain.frmInstanceLeft.ItemShader.Checked = true;
                break;
            }
            case ModComp.CompType.Schematic:
            {
                if (ModMain.frmInstanceSchematic is not null)
                    ModMain.frmInstanceSchematic.ReloadCompFileList(true);
                ModMain.frmInstanceLeft.ItemSchematic.Checked = true;
                break;
            }
        }

        HintService.Hint(Lang.Text("Instance.Left.Refreshing"), log: false);
    }


    private void LoaderInit()
    {
        PageLoaderInit(Load, PanLoad, PanAllBack, null, ModLocalComp.compResourceListLoader,
            _ => LoadUIFromLoaderOutput(), () => currentCompType, false);
    }


    private void Load_Click(object sender, MouseButtonEventArgs e)
    {
        if (ModLocalComp.compResourceListLoader.State == ModBase.LoadState.Failed)
            LoaderRun(ModLoader.LoaderFolderRunType.ForceRun);
    }


    public bool LoaderRun(ModLoader.LoaderFolderRunType type)
    {
        string loadPath;
        if (string.IsNullOrEmpty(CurrentFolderPath))
            // 加载根目录
            loadPath = PageInstanceLeft.McInstance.PathIndie +
                       (PageInstanceLeft.McInstance.Info.HasLabyMod
                           ? Path.Combine("labymod-neo", "fabric", PageInstanceLeft.McInstance.Info.VanillaName)
                           : "") + ModLocalComp.GetPathNameByCompType(currentCompType) + @"\";
        else
            // 加载当前文件夹
            loadPath = CurrentFolderPath;
        return ModLoader.LoaderFolderRun(ModLocalComp.compResourceListLoader, loadPath, type,
            loaderInput: GetRequireLoaderData());
    }

}
