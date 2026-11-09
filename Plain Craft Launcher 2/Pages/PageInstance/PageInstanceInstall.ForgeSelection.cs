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



    /// <summary>
    ///     获取 OptiFine 的加载异常信息。若正常则返回 Nothing。
    /// </summary>
    private string LoadOptiFineGetError()
    {
        if (_selectedLoaderType is ModLoaderType.NeoForge or ModLoaderType.LabyMod or ModLoaderType.Cleanroom)
            return Lang.Text("Download.Install.Compat.IncompatibleWithLoader", _selectedLoaderType);
        if (LoadOptiFine is null || LoadOptiFine.State.LoadingState == MyLoading.MyLoadingState.Run)
            return Lang.Text("Download.Install.State.Loading");
        if (LoadOptiFine.State.LoadingState == MyLoading.MyLoadingState.Error)
            return $"{Lang.Text("Download.Install.State.GetVersionListFailed")}{((ModLoader.LoaderBase)LoadOptiFine.State).Error.Message}";
        // 检查 Forge 1.13 - 1.14.3：全部不兼容
        if (_selectedLoaderType == ModLoaderType.Forge && McVersionComparer.CompareVersion(_vanillaName, "1.13") >= 0 &&
            McVersionComparer.CompareVersion("1.14.3", _vanillaName) >= 0) return Lang.Text("Download.Install.Compat.IncompatibleWithLoader", _selectedLoaderType);
        // 检查 Fabric 1.20.5+: 全部不兼容
        if (selectedFabric is not null && McVersionComparer.CompareVersion(_vanillaName, "1.20.4") > 0)
            return Lang.Text("Download.Install.Compat.IncompatibleWithLoader", _selectedLoaderType);
        // 检查 Loader
        if (GetLoaderError(LoadOptiFine) is not null)
            return GetLoaderError(LoadOptiFine);
        // 检查 Forge 版本
        var hasAny = false;
        var hasRequiredVersion = false;
        foreach (var OptiFineVersion in ModDownload.dlOptiFineListLoader.output.Value)
        {
            if (!OptiFineVersion.DisplayName.StartsWith(_vanillaName + " "))
                continue; // 不是同一个大版本
            hasAny = true;
            if (selectedForge is null)
                return null; // 未选择 Forge
            if ((bool)IsOptiFineSuitForForge(OptiFineVersion, selectedForge))
                return null; // 该版本可用
            if (OptiFineVersion.RequiredForgeVersion is not null)
                hasRequiredVersion = true;
        }

        if (!hasAny) return Lang.Text("Download.Install.State.NoVersion");

        if (hasRequiredVersion) return Lang.Text("Download.Install.Compat.CompatForgeSpecificOnly");

        return Lang.Text("Download.Install.Compat.IncompatibleWithLoader", _selectedLoaderType);
    }


    // 检查某个 OptiFine 是否与某个 Forge 兼容
    private object IsOptiFineSuitForForge(ModDownload.DlOptiFineListEntry optiFine,
        ModDownload.DlForgeVersionEntry forge)
    {
        if ((forge.Inherit ?? "") != (optiFine.Inherit ?? ""))
            return false; // 不是同一个大版本
        if (optiFine.RequiredForgeVersion is null)
            return false; // 不兼容 Forge
        if (string.IsNullOrWhiteSpace(optiFine.RequiredForgeVersion))
            return true; // #4183
        if (optiFine.RequiredForgeVersion.Contains(".")) // XX.X.XXX
            return McVersionComparer.CompareVersion(forge.version.ToString(), optiFine.RequiredForgeVersion) == 0;

        // XXXX
        return forge.version.Revision == Convert.ToDouble(optiFine.RequiredForgeVersion);
    }


    // 限制展开
    private void CardOptiFine_PreviewSwap(object sender, ModBase.RouteEventArgs e)
    {
        if (LoadOptiFineGetError() is not null)
            e.handled = true;
    }


    /// <summary>
    ///     尝试重新可视化 OptiFine 版本列表。
    /// </summary>
    private void OptiFine_Loaded()
    {
        try
        {
            if (ModDownload.dlOptiFineListLoader.State != ModBase.LoadState.Finished)
                return;

            // 获取版本列表
            var versions = new List<ModDownload.DlOptiFineListEntry>();
            foreach (var Version in ModDownload.dlOptiFineListLoader.output.Value)
            {
                if (selectedForge is not null &&
                                          !(bool)IsOptiFineSuitForForge(Version, selectedForge))
                    continue;
                if (Version.DisplayName.StartsWith(_vanillaName + " "))
                    versions.Add(Version);
            }

            if (!versions.Any())
                return;
            // 排序
            versions.Sort((left, right) =>
            {
                if (!left.IsPreview && right.IsPreview)
                    return true;
                if (left.IsPreview && !right.IsPreview)
                    return false;
                return McVersionComparer.CompareVersion(left.DisplayName, right.DisplayName) != 0;
            });
            // 可视化
            PanOptiFine.Children.Clear();
            foreach (var Version in versions)
                PanOptiFine.Children.Add(
                    ModDownloadLib.OptiFineDownloadListItem(Version, (a, b) =>
                        this.OptiFine_Selected((dynamic)a, b), false));
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "可视化 OptiFine 安装版本列表出错",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Install.Error.OperationFailed"));
        }
    }


    // 选择与清除
    private void OptiFine_Selected(MyListItem sender, EventArgs e)
    {
        selectedOptiFine = (ModDownload.DlOptiFineListEntry)sender.Tag;
        if (selectedForge is not null &&
                                  !(bool)IsOptiFineSuitForForge(selectedOptiFine, selectedForge))
            selectedForge = null;
        OptiFabric_Loaded();
        Forge_Loaded();
        NeoForge_Loaded();
        CardOptiFine.IsSwapped = true;
        ReloadSelected();
    }


    private void OptiFine_Clear(object sender, MouseButtonEventArgs e)
    {
        selectedOptiFine = null;
        selectedOptiFabric = null;
        autoSelectedOptiFabric = false;
        CardOptiFine.IsSwapped = true;
        e.Handled = true;
        Forge_Loaded();
        NeoForge_Loaded();
        ReloadSelected();
    }




    /// <summary>
    ///     获取 LiteLoader 的加载异常信息。若正常则返回 Nothing。
    /// </summary>
    private string LoadLiteLoaderGetError()
    {
        // 检查 Loader
        if (GetLoaderError(LoadLiteLoader) is not null)
            return GetLoaderError(LoadLiteLoader);
        if (_selectedLoaderType is ModLoaderType.NeoForge or ModLoaderType.LegacyFabric or ModLoaderType.LabyMod or ModLoaderType.Cleanroom)
            return Lang.Text("Download.Install.Compat.IncompatibleWithLoader", _selectedLoaderType);
        // 检查版本
        return ModDownload.dlLiteLoaderListLoader.output.Value.Any(v => (v.Inherit ?? "") == (_vanillaName ?? ""))
            ? null
            : Lang.Text("Download.Install.State.NoVersion");
    }


    // 限制展开
    private void CardLiteLoader_PreviewSwap(object sender, ModBase.RouteEventArgs e)
    {
        if (LoadLiteLoaderGetError() is not null)
            e.handled = true;
    }


    /// <summary>
    ///     尝试重新可视化 LiteLoader 版本列表。
    /// </summary>
    private void LiteLoader_Loaded()
    {
        try
        {
            if (ModDownload.dlLiteLoaderListLoader.State != ModBase.LoadState.Finished)
                return;
            // 获取版本列表
            var versions = new List<ModDownload.DlLiteLoaderListEntry>();
            foreach (var Version in ModDownload.dlLiteLoaderListLoader.output.Value)
                if ((Version.Inherit ?? "") == (_vanillaName ?? ""))
                    versions.Add(Version);
            if (!versions.Any())
                return;
            // 可视化
            PanLiteLoader.Children.Clear();
            foreach (var Version in versions)
                PanLiteLoader.Children.Add(ModDownloadLib.LiteLoaderDownloadListItem(Version,
                    (a, b) => this.LiteLoader_Selected((dynamic)a, b), false));
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "可视化 LiteLoader 安装版本列表出错",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Install.Error.OperationFailed"));
        }
    }


    // 选择与清除
    private void LiteLoader_Selected(MyListItem sender, EventArgs e)
    {
        selectedLiteLoader = (ModDownload.DlLiteLoaderListEntry)sender.Tag;
        CardLiteLoader.IsSwapped = true;
        ReloadSelected();
    }


    private void LiteLoader_Clear(object sender, MouseButtonEventArgs e)
    {
        selectedLiteLoader = null;
        CardLiteLoader.IsSwapped = true;
        e.Handled = true;
        ReloadSelected();
    }




    /// <summary>
    ///     获取 Forge 的加载异常信息。若正常则返回 Nothing。
    /// </summary>
    private string LoadForgeGetError()
    {
        if (McVersionComparer.CompareVersionGe("1.5.1", _vanillaName) && McVersionComparer.CompareVersionGe(_vanillaName, "1.1"))
            return Lang.Text("Download.Install.State.NoVersion");
                
        if (_selectedLoaderType is not null && _selectedLoaderType != ModLoaderType.Forge)
            return Lang.Text("Download.Install.Compat.IncompatibleWithLoader", _selectedLoaderType);

        // 检查 Loader
        if (GetLoaderError(LoadForge) is not null)
            return GetLoaderError(LoadForge);
        var loader = (ModLoader.LoaderTask<string, List<ModDownload.DlForgeVersionEntry>>)LoadForge.State;
        if ((_vanillaName ?? "") != (loader.input ?? ""))
            return Lang.Text("Download.Install.State.Getting");
        // 检查版本
        foreach (var Version in loader.output)
        {
            if (Version.Category == "universal" || Version.Category == "client")
                continue; // 跳过无法自动安装的版本
            if (_selectedLoaderType is not null && _selectedLoaderType != ModLoaderType.Forge)
                return Lang.Text("Download.Install.Compat.IncompatibleWithLoader", _selectedLoaderType);
            if (selectedOptiFine is not null && McVersionComparer.CompareVersionGe(_vanillaName, "1.13") &&
                McVersionComparer.CompareVersionGe("1.14.3", _vanillaName))
                return Lang.Text("Download.Install.Compat.IncompatibleWithOptiFine"); // 1.13 ~ 1.14.3 OptiFine 检查
            if (selectedOptiFine is not null && !(bool)IsOptiFineSuitForForge(selectedOptiFine, Version))
                continue;
            return null;
        }

        return Lang.Text("Download.Install.Compat.IncompatibleWithOptiFine");
    }


    // 限制展开
    private void CardForge_PreviewSwap(object sender, ModBase.RouteEventArgs e)
    {
        if (LoadForgeGetError() is not null)
            e.handled = true;
    }


    /// <summary>
    ///     尝试重新可视化 Forge 版本列表。
    /// </summary>
    private void Forge_Loaded()
    {
        try
        {
            if (!LoadForge.State.IsLoader)
                return;
            var loader = (ModLoader.LoaderTask<string, List<ModDownload.DlForgeVersionEntry>>)LoadForge.State;
            if ((_vanillaName ?? "") != (loader.input ?? ""))
                return;
            if (loader.State != ModBase.LoadState.Finished)
                return;
            // 获取要显示的版本
            var versions = loader.output.ToList(); // 复制数组，以免 Output 在实例化后变空
            if (!loader.output.Any())
                return;
            PanForge.Children.Clear();
            versions = versions.Where(v =>
            {
                if (v.Category == "universal" || v.Category == "client")
                    return false; // 跳过无法自动安装的版本
                if (selectedOptiFine is not null &&
                                          !(bool)IsOptiFineSuitForForge(selectedOptiFine, v))
                    return false;
                return true;
            }).OrderByDescending(v => v).ToList();
            ModDownloadLib.ForgeDownloadListItemPreload(PanForge, versions,
                (a, b) => this.Forge_Selected((dynamic)a, b), false);
            foreach (var Version in versions)
                PanForge.Children.Add(
                    ModDownloadLib.ForgeDownloadListItem(Version, (a, b) => this.Forge_Selected((dynamic)a, b), false));
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "可视化 Forge 安装版本列表出错",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Install.Error.OperationFailed"));
        }
    }


    // 选择与清除
    private void Forge_Selected(MyListItem sender, EventArgs e)
    {
        SelectModLoader(ModLoaderType.Forge);
        selectedForge = (ModDownload.DlForgeVersionEntry)sender.Tag;
        CardForge.IsSwapped = true;
        if (selectedOptiFine is not null &&
                                  !(bool)IsOptiFineSuitForForge(selectedOptiFine, selectedForge))
            selectedOptiFine = null;
        OptiFine_Loaded();
        ReloadSelected();
    }


    private void Forge_Clear(object sender, MouseButtonEventArgs e)
    {
        selectedForge = null;
        _selectedLoaderType = null;
        CardForge.IsSwapped = true;
        e.Handled = true;
        OptiFine_Loaded();
        ReloadSelected();
    }




    /// <summary>
    ///     获取 NeoForge 的加载异常信息。若正常则返回 Nothing。
    /// </summary>
    private string LoadNeoForgeGetError()
    {
        if (selectedOptiFine is not null)
            return Lang.Text("Download.Install.Compat.IncompatibleWithOptiFine");
        if (_selectedLoaderType is not null && _selectedLoaderType != ModLoaderType.NeoForge)
            return Lang.Text("Download.Install.Compat.IncompatibleWithLoader", _selectedLoaderType);
        // 检查 Loader
        if (GetLoaderError(LoadNeoForge) is not null)
            return GetLoaderError(LoadNeoForge);
        // 检查版本
        return ModDownload.dlNeoForgeListLoader.output.Value.Any(v => (v.Inherit ?? "") == (_vanillaName ?? ""))
            ? null
            : Lang.Text("Download.Install.State.NoVersion");
    }


    // 限制展开
    private void CardNeoForge_PreviewSwap(object sender, ModBase.RouteEventArgs e)
    {
        if (LoadNeoForgeGetError() is not null)
            e.handled = true;
    }


    /// <summary>
    ///     尝试重新可视化 NeoForge 版本列表。
    /// </summary>
    private void NeoForge_Loaded()
    {
        try
        {
            // 获取版本列表
            if (ModDownload.dlNeoForgeListLoader.State != ModBase.LoadState.Finished)
                return;
            var versions = ModDownload.dlNeoForgeListLoader.output.Value
                .Where(v => (v.Inherit ?? "") == (_vanillaName ?? "")).ToList();
            if (!versions.Any())
                return;
            // 可视化
            PanNeoForge.Children.Clear();
            ModDownloadLib.NeoForgeDownloadListItemPreload(PanNeoForge, versions,
                (a, b) => this.NeoForge_Selected((dynamic)a, b),
                false);
            foreach (var Version in versions)
                PanNeoForge.Children.Add(
                    ModDownloadLib.NeoForgeDownloadListItem(Version, (a, b) => this.NeoForge_Selected((dynamic)a, b),
                        false));
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "可视化 NeoForge 安装版本列表出错",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Install.Error.OperationFailed"));
        }
    }


    // 选择与清除
    private void NeoForge_Selected(MyListItem sender, EventArgs e)
    {
        SelectModLoader(ModLoaderType.NeoForge);
        selectedNeoForge = (ModDownload.DlNeoForgeListEntry)sender.Tag;
        CardNeoForge.IsSwapped = true;
        OptiFine_Loaded();
        ReloadSelected();
    }


    private void NeoForge_Clear(object sender, MouseButtonEventArgs e)
    {
        selectedNeoForge = null;
        _selectedLoaderType = null;
        CardNeoForge.IsSwapped = true;
        e.Handled = true;
        OptiFine_Loaded();
        ReloadSelected();
    }




    /// <summary>
    ///     获取 Cleanroom 的加载异常信息。若正常则返回 Nothing。
    /// </summary>
    private string LoadCleanroomGetError()
    {
        if (!_vanillaName.StartsWith("1."))
            return Lang.Text("Download.Install.State.NoAvailableVersion");
        if (selectedOptiFine is not null)
            return Lang.Text("Download.Install.Compat.IncompatibleWithOptiFine");
        if (_selectedLoaderType is not null && _selectedLoaderType != ModLoaderType.Cleanroom)
            return Lang.Text("Download.Install.Compat.IncompatibleWithLoader", _selectedLoaderType);
        if (selectedLiteLoader is not null) 
            return Lang.Text("Download.Install.Compat.IncompatibleWithLiteLoader");
        // 检查 Loader
        if (GetLoaderError(LoadCleanroom) is not null)
            return GetLoaderError(LoadCleanroom);
        // 检查版本
        return ModDownload.dlCleanroomListLoader.output.Value.Any(v => (v.Inherit ?? "") == (_vanillaName ?? ""))
            ? null
            : Lang.Text("Download.Install.State.NoVersion");
    }


    // 限制展开
    private void CardCleanroom_PreviewSwap(object sender, ModBase.RouteEventArgs e)
    {
        if (LoadCleanroomGetError() is not null)
            e.handled = true;
    }


    /// <summary>
    ///     尝试重新可视化 Cleanroom 版本列表。
    /// </summary>
    private void Cleanroom_Loaded()
    {
        try
        {
            // 获取版本列表
            if (ModDownload.dlCleanroomListLoader.State != ModBase.LoadState.Finished)
                return;
            var versions = ModDownload.dlCleanroomListLoader.output.Value
                .Where(v => (v.Inherit ?? "") == (_vanillaName ?? "")).ToList();
            if (!versions.Any())
                return;
            // 可视化
            PanCleanroom.Children.Clear();
            ModDownloadLib.CleanroomDownloadListItemPreload(PanCleanroom, versions,
                (a, b) => this.Cleanroom_Selected((dynamic)a, b), false);
            foreach (var Version in versions)
                PanCleanroom.Children.Add(
                    ModDownloadLib.CleanroomDownloadListItem(Version, (a, b) => this.Cleanroom_Selected((dynamic)a, b),
                        false));
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "可视化 Cleanroom 安装版本列表出错",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Install.Error.OperationFailed"));
        }
    }


    // 选择与清除
    private void Cleanroom_Selected(MyListItem sender, EventArgs e)
    {
        SelectModLoader(ModLoaderType.Cleanroom);
        selectedCleanroom = (ModDownload.DlCleanroomListEntry)sender.Tag;
        CardCleanroom.IsSwapped = true;
        OptiFine_Loaded();
        ReloadSelected();
    }


    private void Cleanroom_Clear(object sender, MouseButtonEventArgs e)
    {
        selectedCleanroom = null;
        selectedCleanroomVersion = null;
        _selectedLoaderType = null;
        selectedAPIName = null;
        CardCleanroom.IsSwapped = true;
        e.Handled = true;
        OptiFine_Loaded();
        ReloadSelected();
    }

}
