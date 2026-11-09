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
    ///     获取 Fabric 的加载异常信息。若正常则返回 Nothing。
    /// </summary>
    private string LoadFabricGetError()
    {
        // 检查 OptiFine 1.20.5+：没有 OptiFabric 故全部不兼容
        if (selectedOptiFine is not null && McVersionComparer.CompareVersionGe(_vanillaName, "1.20.5"))
            return Lang.Text("Download.Install.Compat.IncompatibleWithOptiFine");
        // 检查 Loader
        if (GetLoaderError(LoadFabric) is not null)
            return GetLoaderError(LoadFabric);
        // 检查版本
        foreach (JsonObject version in ModDownload.dlFabricListLoader.output.Value["game"].AsArray())
            if ((version["version"].ToString() ?? "") ==
                (_vanillaName.Replace("∞", "infinite").Replace("Combat Test 7c", "1.16_combat-3") ?? ""))
            {
                if (_selectedLoaderType is not null && _selectedLoaderType != ModLoaderType.Fabric)
                    return Lang.Text("Download.Install.Compat.IncompatibleWithLoader", _selectedLoaderType);
                return null;
            }

        return Lang.Text("Download.Install.State.NoVersion");
    }


    // 限制展开
    private void CardFabric_PreviewSwap(object sender, ModBase.RouteEventArgs e)
    {
        if (LoadFabricGetError() is not null)
            e.handled = true;
    }


    /// <summary>
    ///     尝试重新可视化 Fabric 版本列表。
    /// </summary>
    private void Fabric_Loaded()
    {
        try
        {
            if (ModDownload.dlFabricListLoader.State != ModBase.LoadState.Finished)
                return;
            // 获取版本列表
            var versions = (JsonArray)ModDownload.dlFabricListLoader.output.Value["loader"];
            if (!versions.Any())
                return;
            // 可视化
            PanFabric.Children.Clear();
            PanFabric.Tag = versions;
            CardFabric.SwapControl = PanFabric;
            CardFabric.InstallMethod = stack =>
            {
                foreach (var item in (IEnumerable)stack.Tag)
                    stack.Children.Add(
                        ModDownloadLib.FabricDownloadListItem((JsonObject)item,
                            (a, b) => this.Fabric_Selected((dynamic)a, b)));
            };
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "可视化 Fabric 安装版本列表出错",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Install.Error.OperationFailed"));
        }
    }


    // 选择与清除
    public void Fabric_Selected(MyListItem sender, EventArgs e)
    {
        SelectModLoader(ModLoaderType.Fabric);
        selectedFabric = ((dynamic)sender.Tag)["version"].ToString();
        FabricApi_Loaded();
        OptiFabric_Loaded();
        CardFabric.IsSwapped = true;
        ReloadSelected();
    }


    private void Fabric_Clear(object sender, MouseButtonEventArgs e)
    {
        selectedFabric = null;
        selectedFabricApi = null;
        autoSelectedFabricApi = false;
        selectedOptiFabric = null;
        autoSelectedOptiFabric = false;
        _selectedLoaderType = null;
        selectedAPIName = null;
        CardFabric.IsSwapped = true;
        e.Handled = true;
        ReloadSelected();
    }




    /// <summary>
    ///     判断某 Fabric API 是否适配当前选择的原版版本。
    /// </summary>
    public bool IsFabricApiCompatible(ModComp.CompFile fabricApi)
    {
        var fabricApiName = fabricApi.DisplayName;
        try
        {
            if (fabricApiName is null || _vanillaName is null)
                return false;
            fabricApiName = fabricApiName.ToLower();
            _vanillaName = _vanillaName.Replace("∞", "infinite").Replace("Combat Test 7c", "1.16_combat-3").ToLower();
            if (fabricApiName.StartsWith("[" + _vanillaName + "]"))
                return true;
            if (!fabricApiName.Contains("/") || !fabricApiName.Contains("]"))
                return false;
            // 直接的判断（例如 1.18.1/22w03a）
            foreach (var part in fabricApiName.BeforeFirst("]").TrimStart('[').Split("/"))
                if ((part ?? "") == (_vanillaName ?? ""))
                    return true;
            // 将版本名分割语素（例如 1.16.4/5）
            var lefts = fabricApiName.BeforeFirst("]").RegexSearch("[a-z/]+|[0-9/]+");
            var rights = _vanillaName.BeforeFirst("]").RegexSearch("[a-z/]+|[0-9/]+");
            // 对每段进行判断
            var i = 0;
            while (true)
            {
                // 两边均缺失，感觉是一个东西
                if (lefts.Count - 1 < i && rights.Count - 1 < i)
                    return true;
                // 确定两边是否一致
                var leftValue = lefts.Count - 1 < i ? "-1" : lefts[i];
                var rightValue = rights.Count - 1 < i ? "-1" : rights[i];
                if (!leftValue.Contains("/"))
                {
                    if ((leftValue ?? "") != (rightValue ?? ""))
                        return false;
                }
                // 左边存在斜杠
                else if (!leftValue.Contains(rightValue))
                {
                    return false;
                }

                i += 1;
            }

        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "判断 Fabric API 版本适配性出错（" + fabricApiName + ", " + _vanillaName + "）");
            return false;
        }
    }


    /// <summary>
    ///     获取 FabricApi 的加载异常信息。若正常则返回 Nothing。
    /// </summary>
    private string LoadFabricApiGetError()
    {
        // 检查 Loader
        if (GetLoaderError(LoadFabricApi) is not null)
            return GetLoaderError(LoadFabricApi);
        if (ModDownload.dlFabricApiLoader.output is null)
            return selectedFabric is null ? Lang.Text("Download.Install.Compat.RequiresFabric") : Lang.Text("Download.Install.State.Getting");
        // 检查版本
        if (ModDownload.dlFabricApiLoader.output.Any(f => IsFabricApiCompatible(f)))
            return selectedFabric is null ? Lang.Text("Download.Install.Compat.RequiresFabric") : null;

        return Lang.Text("Download.Install.State.NoVersion");
    }


    // 限制展开
    private void CardFabricApi_PreviewSwap(object sender, ModBase.RouteEventArgs e)
    {
        if (LoadFabricApiGetError() is not null)
            e.handled = true;
    }


    /// <summary>
    ///     尝试重新可视化 FabricApi 版本列表。
    /// </summary>
    private void FabricApi_Loaded()
    {
        try
        {
            if (ModDownload.dlFabricApiLoader.State != ModBase.LoadState.Finished)
                return;
            if (_vanillaName is null || selectedFabric is null)
                return;
            // 获取版本列表
            var versions = new List<ModComp.CompFile>();
            foreach (var version in ModDownload.dlFabricApiLoader.output)
                if (IsFabricApiCompatible(version))
                {
                    if (!version.DisplayName.StartsWith("["))
                    {
                        ModBase.Log("[Download] 已特判修改 Fabric API 显示名：" + version.DisplayName, ModBase.LogLevel.Debug);
                        version.DisplayName = "[" + _vanillaName + "] " + version.DisplayName;
                    }

                    versions.Add(version);
                }

            if (!versions.Any())
                return;
            versions = versions.OrderByDescending(v => v.ReleaseDate).ToList();
            // 可视化
            PanFabricApi.Children.Clear();
            foreach (var version in versions)
            {
                if (!IsFabricApiCompatible(version))
                    continue;
                PanFabricApi.Children.Add(
                    ModDownloadLib.FabricApiDownloadListItem(version,
                        (a, b) => this.FabricApi_Selected((dynamic)a, b)));
            }

            // 自动选择 Fabric API
            if (!autoSelectedFabricApi)
            {
                autoSelectedFabricApi = true;
                ModBase.Log($"[Download] 已自动选择 Fabric API：{((MyListItem)PanFabricApi.Children[0]).Title}");
                FabricApi_Selected((MyListItem)PanFabricApi.Children[0], null);
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "可视化 Fabric API 安装版本列表出错",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Install.Error.OperationFailed"));
        }
    }


    // 选择与清除
    private void FabricApi_Selected(MyListItem sender, EventArgs e)
    {
        selectedFabricApi = (ModComp.CompFile)sender.Tag;
        selectedAPIName = "Fabric API";
        CardFabricApi.IsSwapped = true;
        ReloadSelected();
    }


    private void FabricApi_Clear(object sender, MouseButtonEventArgs e)
    {
        selectedFabricApi = null;
        selectedAPIName = null;
        CardFabricApi.IsSwapped = true;
        e.Handled = true;
        ReloadSelected();
    }




    /// <summary>
    ///     获取 LegacyFabric 的加载异常信息。若正常则返回 Nothing。
    /// </summary>
    private string LoadLegacyFabricGetError()
    {
        if (LoadLegacyFabric is null || LoadLegacyFabric.State.LoadingState == MyLoading.MyLoadingState.Run)
            return Lang.Text("Download.Install.State.Loading");
        if (LoadLegacyFabric.State.LoadingState == MyLoading.MyLoadingState.Error)
            return Lang.Text("Download.Install.State.GetVersionListFailed", ((ModLoader.LoaderBase)LoadLegacyFabric.State).Error.Message);
        foreach (JsonObject Version in ModDownload.dlLegacyFabricListLoader.output.Value["game"].AsArray())
            if ((Version["version"].ToString() ?? "") == (_vanillaName ?? ""))
            {
                if (selectedLiteLoader is not null)
                    return Lang.Text("Download.Install.Compat.IncompatibleWithLiteLoader");
                if (_selectedLoaderType is not null && _selectedLoaderType != ModLoaderType.LegacyFabric)
                    return Lang.Text("Download.Install.Compat.IncompatibleWithLoader", _selectedLoaderType);
                return null;
            }

        return Lang.Text("Download.Install.State.NoVersion");
    }


    // 限制展开
    private void CardLegacyFabric_PreviewSwap(object sender, ModBase.RouteEventArgs e)
    {
        if (LoadLegacyFabricGetError() is not null)
            e.handled = true;
    }


    /// <summary>
    ///     尝试重新可视化 LegacyFabric 版本列表。
    /// </summary>
    private void LegacyFabric_Loaded()
    {
        try
        {
            if (ModDownload.dlLegacyFabricListLoader.State != ModBase.LoadState.Finished)
                return;
            // 获取版本列表
            var versions = (JsonArray)ModDownload.dlLegacyFabricListLoader.output.Value["loader"];
            if (!versions.Any())
                return;
            // 可视化
            PanLegacyFabric.Children.Clear();
            PanLegacyFabric.Tag = versions;
            CardLegacyFabric.SwapControl = PanLegacyFabric;
            CardLegacyFabric.InstallMethod = stack =>
            {
                foreach (var item in (IEnumerable)stack.Tag)
                    stack.Children.Add(ModDownloadLib.LegacyFabricDownloadListItem((JsonObject)item,
                        (a, b) => this.LegacyFabric_Selected((dynamic)a, b)));
            };
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "可视化 LegacyFabric 安装版本列表出错",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Install.Error.OperationFailed"));
        }
    }


    // 选择与清除
    public void LegacyFabric_Selected(MyListItem sender, EventArgs e)
    {
        SelectModLoader(ModLoaderType.LegacyFabric);
        selectedLegacyFabric = ((dynamic)sender.Tag)["version"].ToString();
        LegacyFabricApi_Loaded();
        CardLegacyFabric.IsSwapped = true;
        ReloadSelected();
    }


    private void LegacyFabric_Clear(object sender, MouseButtonEventArgs e)
    {
        selectedLegacyFabric = null;
        selectedLegacyFabricApi = null;
        autoSelectedLegacyFabricApi = false;
        _selectedLoaderType = null;
        selectedAPIName = null;
        CardLegacyFabric.IsSwapped = true;
        e.Handled = true;
        ReloadSelected();
    }




    /// <summary>
    ///     从显示名判断该 API 是否与某版本适配。
    /// </summary>
    public static bool IsSuitableLegacyFabricApi(List<string> supportVersions, string minecraftVersion)
    {
        try
        {
            if (supportVersions.Contains(minecraftVersion)) return true;

            return false;
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "判断 Legacy Fabric API 版本适配性出错（" + supportVersions + ", " + minecraftVersion + "）");
            return false;
        }
    }


    /// <summary>
    ///     获取 LegacyFabricApi 的加载异常信息。若正常则返回 Nothing。
    /// </summary>
    private string LoadLegacyFabricApiGetError()
    {
        if (LoadLegacyFabricApi is null || LoadLegacyFabricApi.State.LoadingState == MyLoading.MyLoadingState.Run)
            return Lang.Text("Download.Install.State.Loading");
        if (LoadLegacyFabricApi.State.LoadingState == MyLoading.MyLoadingState.Error)
            return Lang.Text("Download.Install.State.GetVersionListFailed", ((ModLoader.LoaderBase)LoadLegacyFabricApi.State).Error.Message);
        if (selectedAPIName is not null && !ReferenceEquals(selectedAPIName, "Legacy Fabric API"))
            return Lang.Text("Download.Install.Compat.IncompatibleWithLoader", selectedAPIName);
        if (ModDownload.dlLegacyFabricApiLoader.output is null)
        {
            if (selectedLegacyFabric is null)
                return Lang.Text("Download.Install.Compat.RequiresLegacyFabric");
            return Lang.Text("Download.Install.State.Loading");
        }

        foreach (var Version in ModDownload.dlLegacyFabricApiLoader.output)
        {
            if (!IsSuitableLegacyFabricApi(Version.GameVersions, _vanillaName))
                continue;
            if (selectedLegacyFabric is null)
                return Lang.Text("Download.Install.Compat.RequiresLegacyFabric");
            return null;
        }

        return Lang.Text("Download.Install.State.NoVersion");
    }


    // 限制展开
    private void CardLegacyFabricApi_PreviewSwap(object sender, ModBase.RouteEventArgs e)
    {
        if (LoadLegacyFabricApiGetError() is not null)
            e.handled = true;
    }


    /// <summary>
    ///     尝试重新可视化 LegacyFabricApi 版本列表。
    /// </summary>
    private void LegacyFabricApi_Loaded()
    {
        try
        {
            if (ModDownload.dlLegacyFabricApiLoader.State != ModBase.LoadState.Finished)
                return;
            if (_vanillaName is null || selectedLegacyFabric is null)
                return;
            // 获取版本列表
            var versions = new List<ModComp.CompFile>();
            foreach (var Version in ModDownload.dlLegacyFabricApiLoader.output)
                if (IsSuitableLegacyFabricApi(Version.GameVersions, _vanillaName))
                    versions.Add(Version);

            if (!versions.Any())
                return;
            versions = versions.OrderByDescending(v => v.ReleaseDate).ToList();
            // 可视化
            PanLegacyFabricApi.Children.Clear();
            foreach (var Version in versions)
            {
                if (!IsSuitableLegacyFabricApi(Version.GameVersions, _vanillaName))
                    continue;
                PanLegacyFabricApi.Children.Add(
                    ModDownloadLib.LegacyFabricApiDownloadListItem(Version,
                        (a, b) => this.LegacyFabricApi_Selected((dynamic)a, b)));
            }

            // 自动选择 Legacy Fabric API
            if (!autoSelectedLegacyFabricApi)
            {
                autoSelectedLegacyFabricApi = true;
                ModBase.Log($"[Download] 已自动选择 Legacy Fabric API：{((MyListItem)PanLegacyFabricApi.Children[0]).Title}");
                LegacyFabricApi_Selected((MyListItem)PanLegacyFabricApi.Children[0], null);
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "可视化 Legacy Fabric API 安装版本列表出错",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Install.Error.OperationFailed"));
        }
    }


    // 选择与清除
    private void LegacyFabricApi_Selected(MyListItem sender, EventArgs e)
    {
        selectedLegacyFabricApi = (ModComp.CompFile)sender.Tag;
        selectedAPIName = "Legacy Fabric API";
        CardLegacyFabricApi.IsSwapped = true;
        ReloadSelected();
    }


    private void LegacyFabricApi_Clear(object sender, MouseButtonEventArgs e)
    {
        selectedLegacyFabricApi = null;
        selectedAPIName = null;
        CardLegacyFabricApi.IsSwapped = true;
        e.Handled = true;
        ReloadSelected();
    }





    /// <summary>
    ///     判断某 OptiFabric 是否适配当前选择的原版版本。
    /// </summary>
    private bool IsOptiFabricCompatible(ModComp.CompFile modFile)
    {
        try
        {
            if (_vanillaName is null)
                return false;
            return modFile.GameVersions.Contains(_vanillaName);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "判断 OptiFabric 版本适配性出错（" + _vanillaName + "）");
            return false;
        }
    }


    /// <summary>
    ///     获取 OptiFabric 的加载异常信息。若正常则返回 Nothing。
    /// </summary>
    private string LoadOptiFabricGetError()
    {
        if (VanillaDrop >= 140 && VanillaDrop <= 150)
            return Lang.Text("Download.Install.Compat.OptiFabricOriginsRequired");
        // 检查 Loader
        if (GetLoaderError(LoadOptiFabric) is not null)
            return GetLoaderError(LoadOptiFabric);
        // 检查版本
        if (ModDownload.dlOptiFabricLoader.output is null)
        {
            if (selectedFabric is null && selectedOptiFine is null)
                return Lang.Text("Download.Install.Compat.RequiresOptiFineAndFabric");
            if (selectedFabric is null)
                return Lang.Text("Download.Install.Compat.RequiresFabric");
            if (selectedOptiFine is null)
                return Lang.Text("Download.Install.Compat.RequiresOptiFine");
            return Lang.Text("Download.Install.State.Getting");
        }

        foreach (var version in ModDownload.dlOptiFabricLoader.output)
        {
            if (!IsOptiFabricCompatible(version))
                continue; // 2135#
            if (selectedFabric is null && selectedOptiFine is null)
                return Lang.Text("Download.Install.Compat.RequiresOptiFineAndFabric");
            if (selectedFabric is null)
                return Lang.Text("Download.Install.Compat.RequiresFabric");
            if (selectedOptiFine is null)
                return Lang.Text("Download.Install.Compat.RequiresOptiFine");
            return null; // 通过检查
        }

        return Lang.Text("Download.Install.State.NoVersion");
    }


    // 限制展开
    private void CardOptiFabric_PreviewSwap(object sender, ModBase.RouteEventArgs e)
    {
        if (LoadOptiFabricGetError() is not null)
            e.handled = true;
    }


    /// <summary>
    ///     尝试重新可视化 OptiFabric 版本列表。
    /// </summary>
    private void OptiFabric_Loaded()
    {
        try
        {
            if (ModDownload.dlOptiFabricLoader.State != ModBase.LoadState.Finished)
                return;
            if (_vanillaName is null || selectedFabric is null || selectedOptiFine is null)
                return;
            // 获取版本列表
            var versions = new List<ModComp.CompFile>();
            foreach (var Version in ModDownload.dlOptiFabricLoader.output)
                if (IsOptiFabricCompatible(Version))
                    versions.Add(Version);
            if (!versions.Any())
                return;
            // 排序
            versions = versions.OrderByDescending(v => v.ReleaseDate).ToList();
            // 可视化
            PanOptiFabric.Children.Clear();
            foreach (var Version in versions)
            {
                if (!IsOptiFabricCompatible(Version))
                    continue;
                PanOptiFabric.Children.Add(
                    ModDownloadLib.OptiFabricDownloadListItem(Version,
                        (a, b) => this.OptiFabric_Selected((dynamic)a, b)));
            }

            // 自动选择 OptiFabric
            if (autoSelectedOptiFabric || (VanillaDrop >= 140 && VanillaDrop <= 150))
                return; // 1.14~15 不自动选择
            autoSelectedOptiFabric = true;
            ModBase.Log($"[Download] 已自动选择 OptiFabric：{((MyListItem)PanOptiFabric.Children[0]).Title}");
            OptiFabric_Selected((MyListItem)PanOptiFabric.Children[0], null);
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "可视化 OptiFabric 安装版本列表出错",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Instance.Install.Error.OperationFailed"));
        }
    }


    // 选择与清除
    private void OptiFabric_Selected(MyListItem sender, EventArgs e)
    {
        selectedOptiFabric = (ModComp.CompFile)sender.Tag;
        CardOptiFabric.IsSwapped = true;
        ReloadSelected();
    }


    private void OptiFabric_Clear(object sender, MouseButtonEventArgs e)
    {
        selectedOptiFabric = null;
        CardOptiFabric.IsSwapped = true;
        e.Handled = true;
        ReloadSelected();
    }

}
