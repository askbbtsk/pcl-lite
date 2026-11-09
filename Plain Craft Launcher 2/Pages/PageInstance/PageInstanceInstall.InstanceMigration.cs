using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.InstanceMigration;
using PCL.Core.UI;

namespace PCL;

public partial class PageInstanceInstall
{
    private CancellationTokenSource? _migrationPreviewCancellation;

    private void CancelInstanceMigration_Click(object sender, MouseButtonEventArgs args)
        => _migrationPreviewCancellation?.Cancel();

    private async void BeginInstanceMigration_Click(object sender, MouseButtonEventArgs args)
    {
        if (_migrationPreviewCancellation is not null || !RestrictedFeaturePolicy.Check("minecraft-loader-install")) return;
        var source = PageInstanceLeft.McInstance;
        if (source is null) return;
        var loader = _selectedLoaderType switch
        {
            ModLoaderType.Fabric => (MigrationLoader?)MigrationLoader.Fabric,
            ModLoaderType.Forge => MigrationLoader.Forge,
            ModLoaderType.NeoForge => MigrationLoader.NeoForge,
            _ => null,
        };
        if (loader is null || string.IsNullOrWhiteSpace(_vanillaName))
        { HintService.Hint(Lang.Text("Instance.Migration.ChooseLoader"), HintType.Info); return; }
        var minecraftRoot = ModFolder.mcFolderSelected;
        var minecraftVersion = _vanillaName;
        var template = new ModDownloadLib.McInstallRequest
        {
            minecraftName = minecraftVersion, minecraftJson = _vanillaData?["url"]?.ToString(),
            fabricVersion = loader == MigrationLoader.Fabric ? selectedFabric : null,
            forgeEntry = loader == MigrationLoader.Forge ? selectedForge : null,
            neoForgeEntry = loader == MigrationLoader.NeoForge ? selectedNeoForge : null,
            neoForgeVersion = loader == MigrationLoader.NeoForge ? selectedNeoForgeVersion : null,
        };
        if ((loader == MigrationLoader.Fabric && string.IsNullOrWhiteSpace(template.fabricVersion))
            || (loader == MigrationLoader.Forge && template.forgeEntry is null)
            || (loader == MigrationLoader.NeoForge && template.neoForgeEntry is null && string.IsNullOrWhiteSpace(template.neoForgeVersion)))
        { HintService.Hint(Lang.Text("Instance.Migration.ChooseLoader"), HintType.Info); return; }
        var targetName = ModMain.MyMsgBoxInput(Lang.Text("Instance.Migration.TargetTitle"),
            Lang.Text("Instance.Migration.TargetPrompt", source.Name, minecraftVersion, loader.Value.ToString()),
            source.Name + "-" + loader.Value);
        if (string.IsNullOrWhiteSpace(targetName)) return;
        if (!InstanceMigrationPlanner.ValidFileName(targetName) || targetName.StartsWith(".pcl-", StringComparison.OrdinalIgnoreCase))
        { HintService.Hint(Lang.Text("Instance.Migration.InvalidName"), HintType.Warning); return; }
        var target = Path.Combine(minecraftRoot, "versions", targetName);
        if (Directory.Exists(target) || File.Exists(target))
        { HintService.Hint(Lang.Text("Instance.Migration.TargetConflict", targetName), HintType.Warning); return; }

        using var cancellation = new CancellationTokenSource();
        _migrationPreviewCancellation = cancellation;
        BtnMigrationPreview.IsEnabled = false; BtnMigrationCancel.IsEnabled = true;
        void CancelOnUnload(object? _, System.Windows.RoutedEventArgs e) => cancellation.Cancel();
        Unloaded += CancelOnUnload;
        try
        {
            InstanceMigrationRunner.EnsureContext(source, minecraftRoot);
            var preview = await Task.Run(() => InstanceMigrationCatalog.Build(source, minecraftVersion, loader.Value, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsLoaded || !ReferenceEquals(source, PageInstanceLeft.McInstance)) return;
            var summary = new StringBuilder(Lang.Text("Instance.Migration.PreviewSummary", source.Name, targetName, minecraftVersion, loader.Value.ToString()));
            foreach (var item in preview.Plan.Mods)
                summary.AppendLine().Append(Path.GetFileName(item.Source.Path)).Append(" → ")
                    .Append(item.TargetFileName ?? Lang.Text("Instance.Migration.Pending")).Append(" · ")
                    .Append(Lang.Text("Instance.Migration.Action." + item.Action));
            if (ModMain.MyMsgBox(summary.ToString(), Lang.Text("Instance.Migration.Title"),
                    Lang.Text("Common.Action.Continue"), Lang.Text("Common.Action.Cancel"), isWarn: true) != 1) return;
            var applicable = preview.Plan.Mods.Where(item => item.Action != MigrationModAction.NeedsReview).ToArray();
            var selectedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (applicable.Length > 0)
            {
                var choices = applicable.Select(item =>
                {
                    var row = new MyListItem
                    {
                        Title = Path.GetFileName(item.Source.Path), Info = item.TargetFileName,
                        Height = 44, Type = MyListItem.CheckType.CheckBox,
                    };
                    row.SetChecked(true, user: false, anime: false);
                    return row;
                }).ToList();
                var selected = ModMain.MyMsgBoxMultiSelect(choices, Lang.Text("Instance.Migration.SelectMods"),
                    Lang.Text("Common.Action.Continue"), Lang.Text("Common.Action.Cancel"));
                if (selected is null) return;
                foreach (var index in selected.Where(index => index >= 0 && index < applicable.Length)) selectedSources.Add(applicable[index].Source.Path);
            }
            var selectedPlan = preview.Plan with
            {
                Mods = preview.Plan.Mods.Select(item => item.Action != MigrationModAction.NeedsReview && !selectedSources.Contains(item.Source.Path)
                    ? item with { Action = MigrationModAction.NeedsReview, Candidate = null, TargetFileName = null, Reason = Lang.Text("Instance.Migration.Skipped") }
                    : item).ToArray(),
            };
            var optional = ChooseOptionalContent();
            if (optional is null) return;
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsLoaded || !ReferenceEquals(source, PageInstanceLeft.McInstance)) return;
            InstanceMigrationRunner.Start(source, minecraftRoot, targetName, preview with { Plan = selectedPlan }, template, optional);
        }
        catch (OperationCanceledException) { HintService.Hint(Lang.Text("Instance.Migration.Cancelled"), HintType.Info); }
        catch (Exception error)
        {
            ModBase.Log(error, "跨加载器迁移预览失败", ModBase.LogLevel.Normal);
            ModMain.MyMsgBox(Lang.Text("Instance.Migration.PreviewFailed", error.Message), Lang.Text("Instance.Migration.Title"), isWarn: true);
        }
        finally
        {
            Unloaded -= CancelOnUnload; _migrationPreviewCancellation = null;
            BtnMigrationPreview.IsEnabled = true; BtnMigrationCancel.IsEnabled = false;
        }
    }

    private static IReadOnlyList<string>? ChooseOptionalContent()
    {
        var action = ModMain.MyMsgBox(Lang.Text("Instance.Migration.OptionalPrompt"), Lang.Text("Instance.Migration.Title"),
            Lang.Text("Instance.Migration.OnlyMods"), Lang.Text("Instance.Migration.ChooseOptional"), Lang.Text("Common.Action.Cancel"), isWarn: true);
        if (action == 1) return [];
        if (action != 2) return null;
        var paths = new[] { "config", "options.txt", "resourcepacks", "shaderpacks", "saves" };
        var items = paths.Select(path => new MyListItem
        {
            Title = Lang.Text("Instance.Migration.Optional." + path.Replace('.', '_')),
            Info = path, Height = 44, Type = MyListItem.CheckType.CheckBox,
        }).ToList();
        var selected = ModMain.MyMsgBoxMultiSelect(items, Lang.Text("Instance.Migration.ChooseOptional"),
            Lang.Text("Common.Action.Continue"), Lang.Text("Common.Action.Cancel"), isWarn: true);
        return selected?.Where(index => index >= 0 && index < paths.Length).Select(index => paths[index]).ToArray();
    }
}
