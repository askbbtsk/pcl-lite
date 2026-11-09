using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Enhancements;

namespace PCL;

public partial class PageInstanceOverall
{
    private async void CompareInstances_Click(object sender, MouseButtonEventArgs e)
    {
        var source = PageInstanceLeft.McInstance;
        var gameRoot = ModFolder.mcFolderSelected;
        var candidates = ModInstanceList.mcInstanceList.Values.SelectMany(instances => instances)
            .Where(instance => !string.Equals(source.PathInstance, instance.PathInstance, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(instance => instance.PathInstance, StringComparer.OrdinalIgnoreCase).ToArray();
        if (candidates.Length == 0) { HintService.Hint(Lang.Text("Instance.Enhancement.CompareEmpty")); return; }
        var choices = candidates.Select(instance => (IMyRadio)new MyRadioBox { Text = instance.Name, MinHeight = 28 }).ToList();
        var selected = ModMain.MyMsgBoxSelect(choices, Lang.Text("Instance.Enhancement.Compare"),
            Lang.Text("Common.Action.Confirm"), Lang.Text("Common.Action.Cancel"));
        if (selected is null || selected < 0 || selected >= candidates.Length) return;
        var other = candidates[selected.Value];
        using var cancellation = new CancellationTokenSource();
        void CancelOnUnload(object _, System.Windows.RoutedEventArgs __) => cancellation.Cancel();
        Unloaded += CancelOnUnload;
        try
        {
            var text = await Task.Run(() =>
            {
                var left = new McInstance(source.PathInstance);
                var right = new McInstance(other.PathInstance);
                var leftReport = AnalyzeInstance(left.PathInstance, gameRoot, cancellation.Token);
                var rightReport = AnalyzeInstance(right.PathInstance, gameRoot, cancellation.Token);
                var a = InstanceComparison.Capture(left.PathIndie, leftReport.Components, cancellation.Token);
                var b = InstanceComparison.Capture(right.PathIndie, rightReport.Components, cancellation.Token);
                var differences = InstanceComparison.Compare(a, b);
                var output = new StringBuilder(Lang.Text("Instance.Enhancement.CompareSummary", left.Name, right.Name, differences.Count));
                foreach (var item in differences.Take(50))
                    output.AppendLine().Append(item.Name).Append(": ").Append(item.Category == "component"
                        ? (item.Left ?? "—") + " → " + (item.Right ?? "—")
                        : Lang.Text(item.Category == "unknown" ? "Instance.Enhancement.CompareUnavailable" : item.Left is null ? "Instance.Enhancement.OnlyRight" : item.Right is null ? "Instance.Enhancement.OnlyLeft" : "Instance.Enhancement.ContentChanged"));
                if (a.Unavailable.Count + b.Unavailable.Count > 0)
                    output.AppendLine().AppendLine(Lang.Text("Instance.Enhancement.CompareUnknown", a.Unavailable.Count + b.Unavailable.Count));
                ModBase.Log("[Enhancements] Comparison: " + System.Text.Json.JsonSerializer.Serialize(differences), ModBase.LogLevel.Normal);
                return output.ToString();
            }, cancellation.Token);
            if (!cancellation.IsCancellationRequested && IsLoaded && ReferenceEquals(source, PageInstanceLeft.McInstance)
                && string.Equals(gameRoot, ModFolder.mcFolderSelected, StringComparison.OrdinalIgnoreCase))
                ModMain.MyMsgBox(text, Lang.Text("Instance.Enhancement.Compare"));
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            ModBase.Log(error, "[Enhancements] Comparison failed", ModBase.LogLevel.Normal);
            if (IsLoaded && !cancellation.IsCancellationRequested)
                HintService.Hint(Lang.Text("Instance.Enhancement.HealthFailure", error.Message), HintType.Error);
        }
        finally { Unloaded -= CancelOnUnload; }
    }
}
