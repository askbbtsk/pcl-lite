using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.InstanceMigration;
using PCL.Core.Minecraft.PrismImport;
using PCL.Core.UI;

namespace PCL;

public partial class PageToolsImport
{
    private CancellationTokenSource? _prismPreviewCancellation;

    private void PrismCancel_Click(object sender, MouseButtonEventArgs args) => _prismPreviewCancellation?.Cancel();

    private async void PrismImport_Click(object sender, MouseButtonEventArgs args)
    {
        if (_prismPreviewCancellation is not null || !RestrictedFeaturePolicy.Check("minecraft-install")) return;
        var picked = SystemDialogs.SelectFolder();
        if (string.IsNullOrWhiteSpace(picked)) return;
        var minecraftRoot = ModFolder.mcFolderSelected;
        using var cancellation = new CancellationTokenSource();
        _prismPreviewCancellation = cancellation;
        BtnPrismImport.IsEnabled = false; BtnPrismCancel.IsEnabled = true;
        void CancelOnUnload(object? _, RoutedEventArgs e) => cancellation.Cancel();
        Unloaded += CancelOnUnload;
        try
        {
            var source = await Task.Run(() => PrismInstanceReader.Read(picked, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); if (!IsLoaded) return;
            var summary = new StringBuilder(Lang.Text("Instance.Prism.Preview", source.SuggestedName, source.MinecraftVersion,
                source.LoaderUid ?? "Minecraft", source.LoaderVersion ?? source.MinecraftVersion));
            foreach (var warning in source.Warnings) summary.AppendLine().Append(warning);
            foreach (var problem in source.BlockingIssues) summary.AppendLine().Append(problem);
            if (!source.CanImport)
            { ModMain.MyMsgBox(summary.ToString(), Lang.Text("Instance.Prism.Title"), isWarn: true); return; }
            if (ModMain.MyMsgBox(summary.ToString(), Lang.Text("Instance.Prism.Title"),
                    Lang.Text("Common.Action.Continue"), Lang.Text("Common.Action.Cancel"), isWarn: true) != 1) return;
            var targetName = ModMain.MyMsgBoxInput(Lang.Text("Instance.Prism.TargetTitle"),
                Lang.Text("Instance.Prism.TargetPrompt"), source.SuggestedName);
            if (string.IsNullOrWhiteSpace(targetName)) return;
            if (!InstanceMigrationPlanner.ValidFileName(targetName) || targetName.StartsWith(".pcl-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Lang.Text("Instance.Migration.InvalidName"));
            var target = Path.Combine(minecraftRoot, "versions", targetName);
            if (Directory.Exists(target) || File.Exists(target)) throw new IOException(Lang.Text("Instance.Migration.TargetConflict", targetName));
            cancellation.Token.ThrowIfCancellationRequested(); if (!IsLoaded) return;
            InstancePrismImportWorkflow.Start(source, targetName, minecraftRoot);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            ModBase.Log(error, "Prism 实例只读导入预览失败", ModBase.LogLevel.Normal);
            ModMain.MyMsgBox(Lang.Text("Instance.Prism.Failed", error.Message), Lang.Text("Instance.Prism.Title"), isWarn: true);
        }
        finally
        {
            Unloaded -= CancelOnUnload; _prismPreviewCancellation = null;
            BtnPrismImport.IsEnabled = true; BtnPrismCancel.IsEnabled = false;
        }
    }
}
