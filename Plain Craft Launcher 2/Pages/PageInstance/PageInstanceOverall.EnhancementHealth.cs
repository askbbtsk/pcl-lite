using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Enhancements;
using PCL.Core.UI;

namespace PCL;

public partial class PageInstanceOverall
{
    private CancellationTokenSource _enhancementCheck;

    private async void BtnEnhancementHealth_Click(object sender, MouseButtonEventArgs e)
    {
        // Capture the instance and game root before leaving the dispatcher; a stale result must
        // never be shown on another instance page. Re-clicking cancels the preceding analysis.
        _enhancementCheck?.Cancel();
        var cancellation = new CancellationTokenSource();
        _enhancementCheck = cancellation;
        var instance = PageInstanceLeft.McInstance;
        var root = ModFolder.mcFolderSelected;
        void CancelOnUnload(object _, System.Windows.RoutedEventArgs __) => cancellation.Cancel();
        Unloaded += CancelOnUnload;
        HintService.Hint(Lang.Text("Instance.Enhancement.HealthBusy"));
        try
        {
            var report = await Task.Run(() => AnalyzeInstance(instance.PathInstance, root, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || !IsLoaded
                || !ReferenceEquals(instance, PageInstanceLeft.McInstance)
                || !string.Equals(root, ModFolder.mcFolderSelected, StringComparison.OrdinalIgnoreCase)) return;
            var missing = report.Libraries.Count(entry => entry.Status == LibraryHealthStatus.Missing);
            var damaged = report.Libraries.Count(entry => entry.Status == LibraryHealthStatus.Damaged);
            var unknown = report.Libraries.Count(entry => entry.Status is not (LibraryHealthStatus.Valid or LibraryHealthStatus.Missing or LibraryHealthStatus.Damaged));
            var text = new StringBuilder(Lang.Text("Instance.Enhancement.HealthSummary", report.Libraries.Count, missing, damaged, unknown));
            text.AppendLine().AppendLine().AppendLine(Lang.Text("Instance.Enhancement.HealthNote"));
            if (report.Libraries.Count == 0) text.AppendLine(Lang.Text("Instance.Enhancement.HealthEmpty"));
            foreach (var component in report.Components)
                text.AppendLine(component.Id + " " + component.Version + (component.Disabled ? " [disabled]" : ""));
            foreach (var entry in report.Libraries.Where(entry => entry.Status != LibraryHealthStatus.Valid).Take(35))
                text.AppendLine(Lang.Text("Instance.Enhancement.Status." + entry.Status) + ": " + (entry.Name ?? Path.GetFileName(entry.Path)));
            if (report.Libraries.Count(entry => entry.Status != LibraryHealthStatus.Valid) > 35)
                text.AppendLine(Lang.Text("Instance.Enhancement.MoreInLog"));
            if (!report.InputsStable) text.AppendLine(Lang.Text("Instance.Enhancement.InputChanged"));
            ModBase.Log("[Enhancements] " + System.Text.Json.JsonSerializer.Serialize(report), ModBase.LogLevel.Normal);
            ModMain.MyMsgBox(text.ToString(), Lang.Text("Instance.Enhancement.HealthTitle"));
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            ModBase.Log(error, "[Enhancements] Instance health check failed", ModBase.LogLevel.Normal);
            if (!cancellation.IsCancellationRequested && IsLoaded && ReferenceEquals(instance, PageInstanceLeft.McInstance))
                HintService.Hint(Lang.Text("Instance.Enhancement.HealthFailure", error.Message), HintType.Error);
        }
        finally
        {
            Unloaded -= CancelOnUnload;
            if (ReferenceEquals(_enhancementCheck, cancellation)) _enhancementCheck = null;
            cancellation.Dispose();
        }
    }

    private static InstanceResolvedReport AnalyzeInstance(string directory, string gameRoot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var current = new McInstance(directory);
        var effective = current.JsonObject.ToJsonString();
        var libraries = ModLibrary.McLibListGet(current, true);
        var store = new InstanceEnhancementStore(directory);
        var declaration = store.ReadDeclaration();
        var sourceFiles = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ancestor = current;
        while (visited.Add(ancestor.PathInstance))
        {
            var json = Path.Combine(ancestor.PathInstance, ancestor.Name + ".json");
            if (File.Exists(json)) sourceFiles.Add(json);
            var parent = ancestor.InheritInstanceName;
            if (string.IsNullOrEmpty(parent)) break;
            if (visited.Count >= 64 || parent.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) >= 0)
                throw new InvalidDataException("Invalid version inheritance.");
            ancestor = new McInstance(Path.Combine(gameRoot, "versions", parent));
        }
        if (declaration.Status == EnhancementReadStatus.Valid) sourceFiles.Add(store.DeclarationPath);
        var setup = Path.Combine(directory, "PCL", "Setup.ini");
        if (File.Exists(setup)) sourceFiles.Add(setup);
        var input = new EnhancementAnalysisInput
        {
            InstanceDirectory = directory,
            EffectiveVersionJson = effective,
            MinecraftVersion = current.Info.VanillaName,
            Libraries = libraries.Select(library => new EnhancementLibrary(library.LocalPath, library.OriginalName,
                library.Sha1, library.size > 0 ? library.size : null, IsLocal: library.IsLocal)).ToArray(),
            AllowedLibraryRoots = [gameRoot],
            SourceFiles = sourceFiles,
            DeclaredComponents = declaration.Value?.Components ?? [],
            Platform = "windows",
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        };
        var report = InstanceEnhancementAnalyzer.Analyze(input, token);
        // The host resolver owns inheritance/patch rules. Re-read it rather than trusting an
        // McInstance object's earlier in-memory JsonObject when external edits are possible.
        if (new McInstance(directory).JsonObject.ToJsonString() != effective)
            report = report with { InputsStable = false };
        if (!store.TrySaveResolved(report, out var cacheError, token))
            ModBase.Log("[Enhancements] Cache was not saved: " + cacheError, ModBase.LogLevel.Normal);
        return report;
    }
}
