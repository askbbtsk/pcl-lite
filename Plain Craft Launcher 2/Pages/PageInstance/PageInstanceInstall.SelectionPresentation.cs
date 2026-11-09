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
    ///     重载已选择的项目的显示。
    /// </summary>
    private void ReloadSelected()
    {
        if (_vanillaName is null || _ReloadSelected_Ongoing)
            return;
        _ReloadSelected_Ongoing = true;
        try
        {
        var selectedInfo = GetSelectInfo();
        // 主预览
        ItemSelect.Title = PageInstanceLeft.McInstance.Name;
        ItemSelect.Logo = GetSelectLogo();
        BtnSelectStart.IsEnabled = true;
        if ((selectedInfo ?? "") == (currentInfo ?? ""))
        {
            ItemSelect.Info = selectedInfo;
            BtnSelectStart.Text = Lang.Text("Instance.Install.Action.StartReset");
            _installAction = InstallAction.Reset;
            BtnSelectStart.SvgIcon = "lucide/rotate-ccw";
        }
        else
        {
            ItemSelect.Info = currentInfo + " → " + selectedInfo;
            BtnSelectStart.Text = Lang.Text("Instance.Install.Action.StartModify");
            _installAction = InstallAction.Modify;
            BtnSelectStart.SvgIcon = "lucide/pencil";
        }

        // Minecraft
        ImgMinecraft.Source = new MyBitmap(_vanillaIcon);
        LabMinecraft.Text = _vanillaName;
        LabMinecraft.Foreground = ThemeManager.colorGray1;
        // OptiFine
        if (!McVersionComparer.CompareVersionGe(_vanillaName, "1.7.2"))
        {
            CardOptiFine.Visibility = Visibility.Collapsed;
        }
        else
        {
            CardOptiFine.Visibility = Visibility.Visible;
            var optiFineError = LoadOptiFineGetError();
            CardOptiFine.MainSwap.Visibility = optiFineError is null ? Visibility.Visible : Visibility.Collapsed;
            if (optiFineError is not null)
                CardOptiFine.IsSwapped = true;
            SetPanelVisibility(PanOptiFineInfo, CardOptiFine.IsSwapped);
            if (selectedOptiFine is null)
            {
                BtnOptiFineClear.Visibility = Visibility.Collapsed;
                ImgOptiFine.Visibility = Visibility.Collapsed;
                LabOptiFine.Text = optiFineError ?? Lang.Text("Download.Install.State.CanAdd");
                LabOptiFine.Foreground = ThemeManager.colorGray4;
            }
            else
            {
                BtnOptiFineClear.Visibility = Visibility.Visible;
                ImgOptiFine.Visibility = Visibility.Visible;
                LabOptiFine.Text = selectedOptiFine.DisplayName.Replace(_vanillaName + " ", "");
                LabOptiFine.Foreground = ThemeManager.colorGray1;
            }
        }

        // LiteLoader
        if (!McInstanceInfo.IsFormatFit(_vanillaName)
            || !McVersionComparer.CompareVersionGe(_vanillaName, "1.5.2")
            || !McVersionComparer.CompareVersionGe("1.12.2", _vanillaName))
        {
            CardLiteLoader.Visibility = Visibility.Collapsed;
        }
        else
        {
            CardLiteLoader.Visibility = Visibility.Visible;
            var liteLoaderError = LoadLiteLoaderGetError();
            CardLiteLoader.MainSwap.Visibility = liteLoaderError is null ? Visibility.Visible : Visibility.Collapsed;
            if (liteLoaderError is not null)
                CardLiteLoader.IsSwapped = true; // 例如在同时展开卡片时选择了不兼容项则强制折叠
            SetPanelVisibility(PanLiteLoaderInfo, CardLiteLoader.IsSwapped);
            if (selectedLiteLoader is null)
            {
                BtnLiteLoaderClear.Visibility = Visibility.Collapsed;
                ImgLiteLoader.Visibility = Visibility.Collapsed;
                LabLiteLoader.Text = liteLoaderError ?? Lang.Text("Download.Install.State.CanAdd");
                LabLiteLoader.Foreground = ThemeManager.colorGray4;
            }
            else
            {
                BtnLiteLoaderClear.Visibility = Visibility.Visible;
                ImgLiteLoader.Visibility = Visibility.Visible;
                LabLiteLoader.Text = selectedLiteLoader.Inherit;
                LabLiteLoader.Foreground = ThemeManager.colorGray1;
            }
        }

        // Forge
        if (!McInstanceInfo.IsFormatFit(_vanillaName)
            || !McVersionComparer.CompareVersionGe(_vanillaName, "1.1"))
        {
            CardForge.Visibility = Visibility.Collapsed;
        }
        else
        {
            CardForge.Visibility = Visibility.Visible;
            var forgeError = LoadForgeGetError();
            CardForge.MainSwap.Visibility = forgeError is null ? Visibility.Visible : Visibility.Collapsed;
            if (forgeError is not null)
                CardForge.IsSwapped = true;
            SetPanelVisibility(PanForgeInfo, CardForge.IsSwapped);
            if (selectedForge is null)
            {
                BtnForgeClear.Visibility = Visibility.Collapsed;
                ImgForge.Visibility = Visibility.Collapsed;
                LabForge.Text = forgeError ?? Lang.Text("Download.Install.State.CanAdd");
                LabForge.Foreground = ThemeManager.colorGray4;
            }
            else
            {
                BtnForgeClear.Visibility = Visibility.Visible;
                ImgForge.Visibility = Visibility.Visible;
                LabForge.Text = selectedForge.VersionName;
                LabForge.Foreground = ThemeManager.colorGray1;
            }
        }

        // Cleanroom
        if (_vanillaName == "1.12.2")
        {
            CardCleanroom.Visibility = Visibility.Visible;
            var cleanroomError = LoadCleanroomGetError();
            CardCleanroom.MainSwap.Visibility = cleanroomError is null ? Visibility.Visible : Visibility.Collapsed;
            if (cleanroomError is not null)
                CardCleanroom.IsSwapped = true;
            SetPanelVisibility(PanCleanroomInfo, CardCleanroom.IsSwapped);
            if (selectedCleanroom is null)
            {
                BtnCleanroomClear.Visibility = Visibility.Collapsed;
                ImgCleanroom.Visibility = Visibility.Collapsed;
                LabCleanroom.Text = cleanroomError ?? Lang.Text("Download.Install.State.CanAdd");
                LabCleanroom.Foreground = ThemeManager.colorGray4;
            }
            else
            {
                BtnCleanroomClear.Visibility = Visibility.Visible;
                ImgCleanroom.Visibility = Visibility.Visible;
                LabCleanroom.Text = selectedCleanroom.VersionName;
                LabCleanroom.Foreground = ThemeManager.colorGray1;
            }
        }
        else
        {
            CardCleanroom.Visibility = Visibility.Collapsed;
        }

        // NeoForge
        if (!McVersionComparer.CompareVersionGe(_vanillaName, "1.20.1"))
        {
            CardNeoForge.Visibility = Visibility.Collapsed;
        }
        else
        {
            CardNeoForge.Visibility = Visibility.Visible;
            var neoForgeError = LoadNeoForgeGetError();
            CardNeoForge.MainSwap.Visibility = neoForgeError is null ? Visibility.Visible : Visibility.Collapsed;
            if (neoForgeError is not null)
                CardNeoForge.IsSwapped = true;
            SetPanelVisibility(PanNeoForgeInfo, CardNeoForge.IsSwapped);
            if (selectedNeoForge is null)
            {
                BtnNeoForgeClear.Visibility = Visibility.Collapsed;
                ImgNeoForge.Visibility = Visibility.Collapsed;
                LabNeoForge.Text = neoForgeError ?? Lang.Text("Download.Install.State.CanAdd");
                LabNeoForge.Foreground = ThemeManager.colorGray4;
            }
            else
            {
                BtnNeoForgeClear.Visibility = Visibility.Visible;
                ImgNeoForge.Visibility = Visibility.Visible;
                LabNeoForge.Text = selectedNeoForge.VersionName;
                LabNeoForge.Foreground = ThemeManager.colorGray1;
            }
        }

        // Fabric
        if (VanillaDrop < 130
            || (VanillaDrop == 130 && !McVersionComparer.CompareVersionGe(_vanillaName, "18w43b")))
        {
            CardFabric.Visibility = Visibility.Collapsed;
        }
        else
        {
            CardFabric.Visibility = Visibility.Visible;
            var fabricError = LoadFabricGetError();
            CardFabric.MainSwap.Visibility = fabricError is null ? Visibility.Visible : Visibility.Collapsed;
            if (fabricError is not null)
                CardFabric.IsSwapped = true;
            SetPanelVisibility(PanFabricInfo, CardFabric.IsSwapped);
            if (selectedFabric is null)
            {
                BtnFabricClear.Visibility = Visibility.Collapsed;
                ImgFabric.Visibility = Visibility.Collapsed;
                LabFabric.Text = fabricError ?? Lang.Text("Download.Install.State.CanAdd");
                LabFabric.Foreground = ThemeManager.colorGray4;
            }
            else
            {
                BtnFabricClear.Visibility = Visibility.Visible;
                ImgFabric.Visibility = Visibility.Visible;
                LabFabric.Text = selectedFabric.Replace("+build", "");
                LabFabric.Foreground = ThemeManager.colorGray1;
            }
        }

        // FabricApi
        if (selectedFabric is null)
        {
            CardFabricApi.Visibility = Visibility.Collapsed;
        }
        else
        {
            CardFabricApi.Visibility = Visibility.Visible;
            var fabricApiError = LoadFabricApiGetError();
            CardFabricApi.MainSwap.Visibility = fabricApiError is null ? Visibility.Visible : Visibility.Collapsed;
            if (fabricApiError is not null || selectedFabric is null)
                CardFabricApi.IsSwapped = true;
            SetPanelVisibility(PanFabricApiInfo, CardFabricApi.IsSwapped);
            if (selectedFabricApi is null)
            {
                BtnFabricApiClear.Visibility = Visibility.Collapsed;
                ImgFabricApi.Visibility = Visibility.Collapsed;
                LabFabricApi.Text = fabricApiError ?? Lang.Text("Download.Install.State.CanAdd");
                LabFabricApi.Foreground = ThemeManager.colorGray4;
            }
            else
            {
                BtnFabricApiClear.Visibility = Visibility.Visible;
                ImgFabricApi.Visibility = Visibility.Visible;
                LabFabricApi.Text = selectedFabricApi.DisplayName.Split("]")[1].Replace("Fabric API ", "")
                    .Replace(" build ", ".").Split("+").First().Trim();
                LabFabricApi.Foreground = ThemeManager.colorGray1;
            }
        }

        // LegacyFabric
        if (VanillaDrop < 30 || VanillaDrop > 130)
        {
            CardLegacyFabric.Visibility = Visibility.Collapsed;
        }
        else
        {
            CardLegacyFabric.Visibility = Visibility.Visible;
            var legacyFabricError = LoadLegacyFabricGetError();
            CardLegacyFabric.MainSwap.Visibility =
                legacyFabricError is null ? Visibility.Visible : Visibility.Collapsed;
            if (legacyFabricError is not null)
                CardLegacyFabric.IsSwapped = true;
            SetPanelVisibility(PanLegacyFabricInfo, CardLegacyFabric.IsSwapped);
            if (selectedLegacyFabric is null)
            {
                BtnLegacyFabricClear.Visibility = Visibility.Collapsed;
                ImgLegacyFabric.Visibility = Visibility.Collapsed;
                LabLegacyFabric.Text = legacyFabricError ?? Lang.Text("Download.Install.State.CanAdd");
                LabLegacyFabric.Foreground = ThemeManager.colorGray4;
            }
            else
            {
                BtnLegacyFabricClear.Visibility = Visibility.Visible;
                ImgLegacyFabric.Visibility = Visibility.Visible;
                LabLegacyFabric.Text = selectedLegacyFabric.Replace("+build", "");
                LabLegacyFabric.Foreground = ThemeManager.colorGray1;
            }
        }

        // LegacyFabricApi
        if (selectedLegacyFabric is null)
        {
            CardLegacyFabricApi.Visibility = Visibility.Collapsed;
        }
        else
        {
            CardLegacyFabricApi.Visibility = Visibility.Visible;
            var legacyFabricApiError = LoadLegacyFabricApiGetError();
            CardLegacyFabricApi.MainSwap.Visibility =
                legacyFabricApiError is null ? Visibility.Visible : Visibility.Collapsed;
            if (legacyFabricApiError is not null || selectedLegacyFabric is null)
                CardLegacyFabricApi.IsSwapped = true;
            SetPanelVisibility(PanLegacyFabricApiInfo, CardLegacyFabricApi.IsSwapped);
            if (selectedLegacyFabricApi is null)
            {
                BtnLegacyFabricApiClear.Visibility = Visibility.Collapsed;
                ImgLegacyFabricApi.Visibility = Visibility.Collapsed;
                LabLegacyFabricApi.Text = legacyFabricApiError ?? Lang.Text("Download.Install.State.CanAdd");
                LabLegacyFabricApi.Foreground = ThemeManager.colorGray4;
            }
            else
            {
                BtnLegacyFabricApiClear.Visibility = Visibility.Visible;
                ImgLegacyFabricApi.Visibility = Visibility.Visible;
                LabLegacyFabricApi.Text = selectedLegacyFabricApi.DisplayName.Replace("Legacy Fabric API ", "");
                LabLegacyFabricApi.Foreground = ThemeManager.colorGray1;
            }
        }

        // LabyMod
        if (!McInstanceInfo.IsFormatFit(_vanillaName)
            || !McVersionComparer.CompareVersionGe(_vanillaName, "1.8.9"))
        {
            CardLabyMod.Visibility = Visibility.Collapsed;
        }
        else
        {
            CardLabyMod.Visibility = Visibility.Visible;
            var labyModError = LoadLabyModGetError();
            CardLabyMod.MainSwap.Visibility = labyModError is null ? Visibility.Visible : Visibility.Collapsed;
            if (labyModError is not null)
                CardLabyMod.IsSwapped = true;
            SetPanelVisibility(PanLabyModInfo, CardLabyMod.IsSwapped);
            if (selectedLabyModVersion is null)
            {
                BtnLabyModClear.Visibility = Visibility.Collapsed;
                ImgLabyMod.Visibility = Visibility.Collapsed;
                LabLabyMod.Text = labyModError ?? Lang.Text("Download.Install.State.CanAdd");
                LabLabyMod.Foreground = ThemeManager.colorGray4;
            }
            else
            {
                BtnLabyModClear.Visibility = Visibility.Visible;
                ImgLabyMod.Visibility = Visibility.Visible;
                LabLabyMod.Text = selectedLabyModVersion;
                LabLabyMod.Foreground = ThemeManager.colorGray1;
            }
        }

        // OptiFabric
        if (selectedFabric is null || selectedOptiFine is null)
        {
            CardOptiFabric.Visibility = Visibility.Collapsed;
        }
        else
        {
            CardOptiFabric.Visibility = Visibility.Visible;
            var optiFabricError = LoadOptiFabricGetError();
            CardOptiFabric.MainSwap.Visibility = optiFabricError is null ? Visibility.Visible : Visibility.Collapsed;
            if (optiFabricError is not null || selectedFabric is null)
                CardOptiFabric.IsSwapped = true;
            SetPanelVisibility(PanOptiFabricInfo, CardOptiFabric.IsSwapped);
            if (selectedOptiFabric is null)
            {
                BtnOptiFabricClear.Visibility = Visibility.Collapsed;
                ImgOptiFabric.Visibility = Visibility.Collapsed;
                LabOptiFabric.Text = optiFabricError ?? Lang.Text("Download.Install.State.CanAdd");
                LabOptiFabric.Foreground = ThemeManager.colorGray4;
            }
            else
            {
                BtnOptiFabricClear.Visibility = Visibility.Visible;
                ImgOptiFabric.Visibility = Visibility.Visible;
                LabOptiFabric.Text = selectedOptiFabric.DisplayName.ToLower().Replace("optifabric-", "")
                    .Replace(".jar", "").Trim().TrimStart('v');
                LabOptiFabric.Foreground = ThemeManager.colorGray1;
            }
        }

        // 主警告
        if (selectedFabric is not null && selectedFabricApi is null)
            HintFabricAPI.Visibility = Visibility.Visible;
        else
            HintFabricAPI.Visibility = Visibility.Collapsed;
        if (selectedLegacyFabric is not null && selectedLegacyFabricApi is null)
            HintLegacyFabricAPI.Visibility = Visibility.Visible;
        else
            HintLegacyFabricAPI.Visibility = Visibility.Collapsed;

        if ((selectedFabric is not null || selectedLegacyFabric is not null) && selectedOptiFine is not null &&
            selectedOptiFabric is null)
        {
            if (VanillaDrop >= 140 && VanillaDrop <= 150)
            {
                HintOptiFabric.Visibility = Visibility.Collapsed;
                HintLegacyOptiFabric.Visibility = Visibility.Collapsed;
                HintOptiFabricOld.Visibility = Visibility.Visible;
            }
            else if (selectedLegacyFabric is not null)
            {
                HintOptiFabric.Visibility = Visibility.Collapsed;
                HintLegacyOptiFabric.Visibility = Visibility.Visible;
                HintOptiFabricOld.Visibility = Visibility.Collapsed;
            }
            else
            {
                HintOptiFabric.Visibility = Visibility.Visible;
                HintOptiFabricOld.Visibility = Visibility.Collapsed;
                HintLegacyOptiFabric.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            HintOptiFabric.Visibility = Visibility.Collapsed;
            HintOptiFabricOld.Visibility = Visibility.Collapsed;
            HintLegacyOptiFabric.Visibility = Visibility.Collapsed;
        }

        if (VanillaDrop >= 160 && selectedOptiFine is not null &&
            (selectedForge is not null || selectedFabric is not null))
            HintModOptiFine.Visibility = Visibility.Visible;
        else
            HintModOptiFine.Visibility = Visibility.Collapsed;
        // 结束
        }
        finally
        {
            _ReloadSelected_Ongoing = false;
        }
    }


    /// <summary>
    ///     清空已选择的项目。
    /// </summary>
    private void ClearSelected()
    {
        _vanillaName = null;
        _vanillaData = null;
        _vanillaIcon = null;
        selectedOptiFine = null;
        selectedLiteLoader = null;
        _selectedLoaderType = null;
        selectedAPIName = null;
        selectedForge = null;
        selectedNeoForge = null;
        selectedNeoForgeVersion = null;
        selectedCleanroom = null;
        selectedCleanroomVersion = null;
        selectedFabric = null;
        selectedFabricApi = null;
        selectedOptiFabric = null;
        selectedLabyModCommitRef = null;
        selectedLabyModVersion = null;
        selectedLabyModChannel = null;
        selectedLegacyFabric = null;
        selectedLegacyFabricApi = null;
    }


    /// <summary>
    ///     选择主 Mod 加载器时，清除其它主加载器及其专属组件。
    ///     OptiFine 与 LiteLoader 可与部分加载器共存，故不在此处清除。
    /// </summary>
    private void SelectModLoader(ModLoaderType loaderType)
    {
        if (loaderType != ModLoaderType.Forge)
            selectedForge = null;

        if (loaderType != ModLoaderType.NeoForge)
        {
            selectedNeoForge = null;
            selectedNeoForgeVersion = null;
        }

        if (loaderType != ModLoaderType.Cleanroom)
        {
            selectedCleanroom = null;
            selectedCleanroomVersion = null;
        }

        if (loaderType != ModLoaderType.Fabric)
        {
            selectedFabric = null;
            selectedFabricApi = null;
            selectedOptiFabric = null;
            autoSelectedFabricApi = false;
            autoSelectedOptiFabric = false;
        }

        if (loaderType != ModLoaderType.LegacyFabric)
        {
            selectedLegacyFabric = null;
            selectedLegacyFabricApi = null;
            autoSelectedLegacyFabricApi = false;
        }

        if (loaderType != ModLoaderType.LabyMod)
        {
            selectedLabyModChannel = null;
            selectedLabyModCommitRef = null;
            selectedLabyModVersion = null;
        }

        _selectedLoaderType = loaderType;
        selectedAPIName = null;
    }


    // 信息栏动画
    private void SetPanelVisibility(Grid panel, bool visible)
    {
        if (Equals(panel.Tag, visible.ToString()))
            return;
        panel.Tag = visible.ToString();
        if (visible)
            ModAnimation.AniStart(
                new[]
                {
                    ModAnimation.AaTranslateY(panel, -((TranslateTransform)panel.RenderTransform).Y, 150,
                        ease: new ModAnimation.AniEaseOutFluent()),
                    ModAnimation.AaOpacity(panel, 1d - panel.Opacity, 60)
                }, "PageDownloadInstall Visibility " + panel.Name);
        else
            ModAnimation.AniStart(
                new[]
                {
                    ModAnimation.AaTranslateY(panel, 6d - ((TranslateTransform)panel.RenderTransform).Y, 60),
                    ModAnimation.AaOpacity(panel, -panel.Opacity, 60)
                }, "PageDownloadInstall Visibility " + panel.Name);
    }


    /// <summary>
    ///     获取实例图标。
    /// </summary>
    private string GetSelectLogo()
    {
        if (selectedFabric is not null) return "pack://application:,,,/images/Blocks/Fabric.png";

        if (selectedLegacyFabric is not null) return "pack://application:,,,/images/Blocks/Fabric.png";

        if (selectedForge is not null) return "pack://application:,,,/images/Blocks/Anvil.png";

        if (selectedNeoForge is not null) return "pack://application:,,,/images/Blocks/NeoForge.png";

        if (selectedLiteLoader is not null) return "pack://application:,,,/images/Blocks/Egg.png";

        if (selectedOptiFine is not null) return "pack://application:,,,/images/Blocks/GrassPath.png";

        if (selectedCleanroom is not null) return "pack://application:,,,/images/Blocks/Cleanroom.png";

        if (selectedLabyModVersion is not null) return "pack://application:,,,/images/Blocks/LabyMod.png";

        return _vanillaIcon;
    }


    /// <summary>
    ///     获取实例描述信息。
    /// </summary>
    private string GetSelectInfo()
    {
        var parts = new List<string>
        {
            _vanillaName
        };

        var loaderInfos = new (string NameKey, string? Version)[]
        {
            ("Common.Installation.Fabric", selectedFabric?.Replace("+build", "")),
            ("Common.Installation.LegacyFabric", selectedLegacyFabric),
            ("Common.Installation.Forge", selectedForge?.VersionName),
            ("Common.Installation.NeoForge",
                selectedNeoForge?.VersionName ?? VersionOrNull(selectedNeoForgeVersion)),
            ("Common.Installation.Cleanroom",
                selectedCleanroom?.VersionName ?? VersionOrNull(selectedCleanroomVersion)),
            ("Common.Installation.LabyMod", selectedLabyModVersion),
            ("Common.Installation.OptiFine",
                selectedOptiFine?.DisplayName.Replace(_vanillaName + " ", ""))
        };

        parts.AddRange(
            loaderInfos
                .Where(info => !string.IsNullOrWhiteSpace(info.Version))
                .Select(info => $"{Lang.Text(info.NameKey)} {info.Version}")
        );

        if (selectedLiteLoader is not null) parts.Add(Lang.Text("Common.Installation.LiteLoader"));

        if (parts.Count == 1) parts.Add(Lang.Text("Instance.Install.NoExtraInstall"));

        return string.Join("  |  ", parts);
    }


    private static string? VersionOrNull<T>(T version)
    {
        return EqualityComparer<T>.Default.Equals(version, default!)
            ? null
            : version?.ToString();
    }


    private object GetCurrentFabricApi() // 进入页面和联网加载时调用
    {
        var loaderOutput = ModDownload.dlFabricApiLoader.output;
        if (loaderOutput is null)
            return null; // 确保联网信息已加载
        var localComp = ModLocalComp.GetModLocalCompByKeywords(PageInstanceLeft.McInstance,
            new[] { "fabric-api", "fabric" }, "fabric", "api");
        if (localComp is null)
            return null;
        var result = loaderOutput.FirstOrDefault(comp => (comp.Hash ?? "") == (localComp.ModrinthHash ?? ""));
        if (result is not null)
        {
            _currentFabricApi = result;
            _currentFabricApiPath = localComp.path;
        }

        return result;
    }


    private object GetCurrentLegacyFabricApi() // 进入页面和联网加载时调用
    {
        var loaderOutput = ModDownload.dlLegacyFabricApiLoader.output;
        if (loaderOutput is null)
            return null; // 确保联网信息已加载
        var localComp = ModLocalComp.GetModLocalCompByKeywords(PageInstanceLeft.McInstance,
            new[] { "legacy-fabric-api", "legacy-fabric" }, "legacy-fabric", "api");
        if (localComp is null)
            return null;
        var result = loaderOutput.FirstOrDefault(comp => (comp.Hash ?? "") == (localComp.ModrinthHash ?? ""));
        if (result is not null)
        {
            _currentLegacyFabricApi = result;
            _currentLegacyFabricApiPath = localComp.path;
        }

        return result;
    }


    private object GetCurrentOptiFabric()
    {
        var loaderOutput = ModDownload.dlOptiFabricLoader.output;
        if (loaderOutput is null)
            return null;
        var localComp =
            ModLocalComp.GetModLocalCompByKeywords(PageInstanceLeft.McInstance, "optifabric", "optifabric", "opti");
        if (localComp is null)
            return null;
        var result = loaderOutput.FirstOrDefault(comp => (comp.Hash ?? "") == (localComp.ModrinthHash ?? ""));
        if (result is not null)
        {
            _currentOptiFabric = result;
            _currentOptiFabricPath = localComp.path;
        }

        return result;
    }


    // 当前信息获取
    public void GetCurrentInfo()
    {
        ClearSelected();
        BtnSelectStart.IsEnabled = true;
        var currentInstance = PageInstanceLeft.McInstance.Info;
        _vanillaName = currentInstance.VanillaName;
        if (currentInstance.HasLiteLoader)
            selectedLiteLoader = new ModDownload.DlLiteLoaderListEntry { Inherit = currentInstance.VanillaName };
        if (currentInstance.HasOptiFine)
            selectedOptiFine = new ModDownload.DlOptiFineListEntry
            {
                DisplayName = currentInstance.VanillaName + " " + currentInstance.OptiFine.Replace("_", " "),
                IsPreview = currentInstance.OptiFine.ContainsF("pre"), Inherit = currentInstance.VanillaName,
                NameVersion = currentInstance.VanillaName + "-OptiFine_HD_U_" + currentInstance.OptiFine
            };
        if (currentInstance.HasCleanroom)
        {
            _selectedLoaderType = ModLoaderType.Cleanroom;
            selectedAPIName = "Cleanroom";
            selectedCleanroomVersion = currentInstance.Cleanroom;
            selectedCleanroom = new ModDownload.DlCleanroomListEntry(selectedCleanroomVersion);
        }
        else if (currentInstance.HasForge)
        {
            _selectedLoaderType = ModLoaderType.Forge;
            selectedForge =
                new ModDownload.DlForgeVersionEntry(currentInstance.Forge, null, currentInstance.VanillaName)
                {
                    Category = "installer", forgeType = ModDownload.DlForgelikeEntry.ForgelikeType.Forge,
                    Inherit = currentInstance.VanillaName
                };
        }
        else if (currentInstance.HasLegacyFabric)
        {
            _selectedLoaderType = ModLoaderType.LegacyFabric;
            selectedLegacyFabric = currentInstance.LegacyFabric;
            selectedLegacyFabricApi = (ModComp.CompFile)GetCurrentLegacyFabricApi();
        }
        else if (currentInstance.HasFabric)
        {
            _selectedLoaderType = ModLoaderType.Fabric;
            selectedFabric = currentInstance.Fabric;
            selectedFabricApi = (ModComp.CompFile)GetCurrentFabricApi();
        }
        else if (currentInstance.HasLabyMod)
        {
            _selectedLoaderType = ModLoaderType.LabyMod;
            selectedLabyModVersion = currentInstance.LabyMod;
        }
        else if (currentInstance.HasNeoForge)
        {
            _selectedLoaderType = ModLoaderType.NeoForge;
            selectedNeoForgeVersion = currentInstance.NeoForge;
            selectedNeoForge = new ModDownload.DlNeoForgeListEntry(currentInstance.NeoForge)
            {
                VersionName = currentInstance.NeoForge, Inherit = currentInstance.VanillaName,
                forgeType = ModDownload.DlForgelikeEntry.ForgelikeType.NeoForge
            };
        }

        if (currentInstance.HasFabric && currentInstance.HasOptiFine)
            selectedOptiFabric = (ModComp.CompFile)GetCurrentOptiFabric();
        _vanillaIcon = "pack://application:,,,/images/Blocks/Grass.png"; // TODO: 需要判断 Icon
        currentInfo = GetSelectInfo();
        EnterSelectPage();
    }

}
