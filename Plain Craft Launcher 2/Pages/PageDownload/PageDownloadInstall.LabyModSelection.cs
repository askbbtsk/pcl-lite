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



    /// <summary>
    ///     获取 LabyMod 的加载异常信息。若正常则返回 Nothing。
    /// </summary>
    private string LoadLabyModGetError()
    {
        if (LoadLabyMod is null || LoadLabyMod.State.LoadingState == MyLoading.MyLoadingState.Run)
            return Lang.Text("Download.Install.State.Loading");
        if (LoadLabyMod.State.LoadingState == MyLoading.MyLoadingState.Error)
            return $"{Lang.Text("Download.Install.State.GetVersionListFailed")}{((ModLoader.LoaderBase)LoadLabyMod.State).Error.Message}";
        // 检查 Loader
        if (GetLoaderError(LoadLabyMod) is not null)
            return GetLoaderError(LoadLabyMod);
        if (selectedOptiFine is not null)
            return Lang.Text("Download.Install.Compat.IncompatibleWithOptiFine");
        if (_selectedLoaderType is not null && _selectedLoaderType != ModLoaderType.LabyMod)
            return Lang.Text("Download.Install.Compat.IncompatibleWithLoader", _selectedLoaderType);
        if (selectedLiteLoader is not null)
            return Lang.Text("Download.Install.Compat.IncompatibleWithLiteLoader");
        foreach (JsonObject Version in ModDownload.dlLabyModListLoader.output.Value["production"]["minecraftVersions"].AsArray())
            if ((Version["version"].ToString() ?? "") == (_vanillaName ?? ""))
                return null;
        foreach (JsonObject Version in ModDownload.dlLabyModListLoader.output.Value["snapshot"]["minecraftVersions"].AsArray())
            if ((Version["version"].ToString() ?? "") == (_vanillaName ?? ""))
                return null;
        return Lang.Text("Download.Install.State.NoVersion");
    }


    // 限制展开
    private void CardLabyMod_PreviewSwap(object sender, ModBase.RouteEventArgs e)
    {
        if (LoadLabyModGetError() is not null)
            e.handled = true;
    }


    /// <summary>
    ///     尝试重新可视化 LabyMod 版本列表。
    /// </summary>
    private void LabyMod_Loaded()
    {
        try
        {
            if (LoadLabyMod.State.LoadingState == MyLoading.MyLoadingState.Run)
                return;
            // 获取版本列表
            var versions = ModDownload.dlLabyModListLoader.output.Value;
            if (versions is null || versions["production"] is null || versions["snapshot"] is null)
                return;
            // 可视化
            var processedVersions = new JsonArray();
            foreach (JsonObject Production in versions["production"]["minecraftVersions"].AsArray())
                if ((Production["version"].ToString() ?? "") == (_vanillaName ?? ""))
                {
                    var productionVersion = new JsonObject();
                    productionVersion.Add("version", versions["production"]["labyModVersion"].ToString());
                    productionVersion.Add("channel", "production");
                    productionVersion.Add("commitReference", versions["production"]["commitReference"].ToString());
                    processedVersions.Add(productionVersion);
                }

            foreach (JsonObject Snapshot in versions["snapshot"]["minecraftVersions"].AsArray())
                if ((Snapshot["version"].ToString() ?? "") == (_vanillaName ?? ""))
                {
                    var snapshotVersion = new JsonObject();
                    snapshotVersion.Add("version", versions["snapshot"]["labyModVersion"].ToString());
                    snapshotVersion.Add("channel", "snapshot");
                    snapshotVersion.Add("commitReference", versions["snapshot"]["commitReference"].ToString());
                    processedVersions.Add(snapshotVersion);
                }

            // MyMsgBox(If(ProcessedVersions.ToString, "Nothing"))
            PanLabyMod.Children.Clear();
            PanLabyMod.Tag = processedVersions;
            CardLabyMod.SwapControl = PanLabyMod;
            CardLabyMod.InstallMethod = stack =>
            {
                foreach (JsonObject item in (IEnumerable)stack.Tag)
                    stack.Children.Add(
                        ModDownloadLib.LabyModDownloadListItem(item, (a, b) => this.LabyMod_Selected((dynamic)a, b)));
            };
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "可视化 LabyMod 安装版本列表出错",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Download.Install.Error.OperationFailed"));
        }
    }


    // 选择与清除
    public void LabyMod_Selected(MyListItem sender, EventArgs e)
    {
        SelectModLoader(ModLoaderType.LabyMod);
        selectedLabyModChannel = ((dynamic)sender.Tag)["channel"].ToString();
        selectedLabyModCommitRef = ((dynamic)sender.Tag)["commitReference"].ToString();
        selectedLabyModBaseVersion = ((dynamic)sender.Tag)["version"].ToString();
        selectedLabyModVersion =
            selectedLabyModBaseVersion + " " + (selectedLabyModChannel == "snapshot" ? Lang.Text("Download.Version.Type.Snapshot") : Lang.Text("Download.Version.Type.Stable"));
        CardLabyMod.IsSwapped = true;
        ReloadSelected();
    }


    private void LabyMod_Clear(object sender, MouseButtonEventArgs e)
    {
        selectedLabyModCommitRef = null;
        selectedLabyModVersion = null;
        selectedLabyModBaseVersion = null;
        selectedLabyModChannel = null;
        
        if (_selectedLoaderType == ModLoaderType.LabyMod)
        {
            _selectedLoaderType = null;
        }    
        
        selectedAPIName = null;
        CardLabyMod.IsSwapped = true;
        e.Handled = true;
        ReloadSelected();
    }




    private void TextSelectName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && BtnStart.IsEnabled)
            BtnStart_Click();
    }


    private void BtnStart_Click()
    {
        if (!RestrictedFeaturePolicy.Check("minecraft-install")) return;
        // 确认版本隔离
        if (_selectedLoaderType is not null &&
            (Config.Launch.IndieSolutionV2 == 0 ||
             Config.Launch.IndieSolutionV2 == 2))
            if (ModMain.MyMsgBox(
                    Lang.Text("Download.Install.InstanceIsolation.Warning.Message"),
                    Lang.Text("Download.Install.InstanceIsolation.Warning.Title"),
                    Lang.Text("Download.Install.InstanceIsolation.Warning.Cancel"),
                    Lang.Text("Download.Install.InstanceIsolation.Warning.Continue")
                ) == 1)
                return;

        // 提交安装申请
        var instanceName = TextSelectName.Text;
        var request = new ModDownloadLib.McInstallRequest
        {
            targetInstanceName = instanceName,
            targetInstanceFolder = $@"{ModFolder.mcFolderSelected}versions\{instanceName}\",
            minecraftJson = _vanillaData?["url"].ToString(),
            minecraftName = _vanillaName,
            optiFineEntry = selectedOptiFine,
            forgeEntry = selectedForge,
            neoForgeEntry = selectedNeoForge,
            cleanroomEntry = selectedCleanroom,
            fabricVersion = selectedFabric,
            fabricApi = selectedFabricApi,
            optiFabric = selectedOptiFabric,
            liteLoaderEntry = selectedLiteLoader,
            labyModChannel = selectedLabyModChannel,
            labyModCommitRef = selectedLabyModCommitRef,
            legacyFabricVersion = selectedLegacyFabric,
            legacyFabricApi = selectedLegacyFabricApi
        };
        if (!ModDownloadLib.McInstall(request))
            return;
        // 返回，这样在再次进入安装页面时这个实例就会显示文件夹已重复
        ExitSelectPage();
    }

}
