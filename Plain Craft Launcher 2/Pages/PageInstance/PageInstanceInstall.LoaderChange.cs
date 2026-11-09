using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PageInstanceInstall
{
    private ModLoaderType? _replacementLoader;

    private void SelectReplacementLoader_Click(object sender, MouseButtonEventArgs e)
    {
        if (!BtnSelectStart.IsEnabled) return;
        var instance = PageInstanceLeft.McInstance;
        var types = new[] { ModLoaderType.Fabric, ModLoaderType.Forge, ModLoaderType.NeoForge };
        var choices = types.Select(type => (IMyRadio)new MyRadioBox { Text = type.ToString(), MinHeight = 30 }).ToList();
        var index = ModMain.MyMsgBoxSelect(choices, Lang.Text("Instance.LoaderChange.SelectTarget"),
            Lang.Text("Common.Action.Confirm"), Lang.Text("Common.Action.Cancel"));
        if (index is null || index < 0 || index >= types.Length
            || !IsLoaded || !ReferenceEquals(instance, PageInstanceLeft.McInstance)) return;

        // Change only the pending UI selection. Existing incompatible selections must not
        // prevent the new loader's catalog from opening; no disk mutation occurs here.
        selectedOptiFine = null;
        selectedLiteLoader = null;
        SelectModLoader(types[index.Value]);
        _replacementLoader = types[index.Value];
        switch (_replacementLoader)
        {
            case ModLoaderType.Fabric:
                selectedFabric = null; selectedFabricApi = null; selectedOptiFabric = null;
                autoSelectedFabricApi = false; autoSelectedOptiFabric = false;
                Fabric_Loaded();
                break;
            case ModLoaderType.Forge:
                selectedForge = null;
                Forge_Loaded();
                break;
            case ModLoaderType.NeoForge:
                selectedNeoForge = null; selectedNeoForgeVersion = null;
                NeoForge_Loaded();
                break;
        }
        ReloadSelected();
        switch (_replacementLoader)
        {
            case ModLoaderType.Fabric: CardFabric.IsSwapped = false; break;
            case ModLoaderType.Forge: CardForge.IsSwapped = false; break;
            case ModLoaderType.NeoForge: CardNeoForge.IsSwapped = false; break;
        }
        HintService.Hint(Lang.Text("Instance.LoaderChange.SelectVersion", _replacementLoader));
    }

    private bool HasReplacementVersion()
    {
        if (_replacementLoader is null) return true;
        var complete = _selectedLoaderType switch
        {
            ModLoaderType.Fabric => !string.IsNullOrWhiteSpace(selectedFabric),
            ModLoaderType.Forge => selectedForge is not null,
            ModLoaderType.NeoForge => selectedNeoForge is not null || !string.IsNullOrWhiteSpace(selectedNeoForgeVersion),
            _ => false,
        };
        if (!complete) HintService.Hint(Lang.Text("Instance.Migration.ChooseLoader"), HintType.Warning);
        return complete;
    }

    private static bool ConfirmLoaderBackup(McInstance instance)
    {
        if (ModMain.MyMsgBox(Lang.Text("Instance.LoaderChange.BackupWarning", instance.Name),
                Lang.Text("Instance.LoaderChange.BackupTitle"), Lang.Text("Instance.LoaderChange.BackedUp"),
                Lang.Text("Common.Action.Cancel"), isWarn: true) != 1) return false;
        return TimedConfirmation.Show(Lang.Text("Instance.LoaderChange.SecondWarning", instance.Name),
            Lang.Text("Instance.LoaderChange.BackupTitle"), Lang.Text("Instance.LoaderChange.ConfirmChange"),
            Lang.Text("Common.Action.Cancel"), 5) == 1;
    }

    private bool? AskToMigrateMods(McInstance instance)
    {
        var choice = ModMain.MyMsgBox(Lang.Text("Instance.LoaderChange.ModPrompt", instance.Name,
                instance.Info.VanillaName, _vanillaName),
            Lang.Text("Instance.LoaderChange.ModTitle"), Lang.Text("Instance.LoaderChange.MigrateMods"),
            Lang.Text("Instance.LoaderChange.KeepMods"), Lang.Text("Common.Action.Cancel"), isWarn: true);
        if (choice == 2) return false;
        if (choice != 1) return null;
        if (TimedConfirmation.Show(Lang.Text("Instance.LoaderChange.ModSecondWarning"),
                Lang.Text("Instance.LoaderChange.ModTitle"), Lang.Text("Instance.LoaderChange.MigrateMods"),
                Lang.Text("Common.Action.Cancel"), 3) != 1) return null;
        return true;
    }

    private static ModLoaderType? GetExistingLoader(McInstance instance)
    {
        var info = instance.Info;
        if (info.HasCleanroom) return ModLoaderType.Cleanroom;
        if (info.HasNeoForge) return ModLoaderType.NeoForge;
        if (info.HasForge) return ModLoaderType.Forge;
        if (info.HasLabyMod) return ModLoaderType.LabyMod;
        if (info.HasLegacyFabric) return ModLoaderType.LegacyFabric;
        if (info.HasFabric) return ModLoaderType.Fabric;
        return null;
    }

    private ModDownloadLib.McInstallRequest BuildModificationRequest(McInstance instance) => new()
    {
        targetInstanceName = instance.Name,
        targetInstanceFolder = instance.PathInstance,
        minecraftJson = _vanillaData?["url"]?.ToString(),
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
        legacyFabricApi = selectedLegacyFabricApi,
    };
}
