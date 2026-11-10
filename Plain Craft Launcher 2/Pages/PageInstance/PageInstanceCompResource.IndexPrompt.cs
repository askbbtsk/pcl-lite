using System.Windows;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.App.Localization;

namespace PCL;

public partial class PageInstanceCompResource
{
    private void OfferModIndex()
    {
        if (currentCompType != ModComp.CompType.Mod || !IsVisible) return;
        var instance = PageInstanceLeft.McInstance;
        var output = ModLocalComp.compResourceListLoader.output;
        if (instance is null || output is null
            || ModLocalComp.compResourceListLoader.input?.gameVersion != instance
            || !output.Any(mod => !mod.IsFolder && mod.JijPending)) return;
        // Defer the dialog until layout and list publication have finished; stale pages cannot prompt.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (!IsVisible || !ReferenceEquals(instance, PageInstanceLeft.McInstance)
                || !ReferenceEquals(output, ModLocalComp.compResourceListLoader.output)
                || !ModIndexService.Policy.ShouldPrompt(instance.PathInstance,
                    output.Any(mod => !mod.IsFolder && mod.JijPending), Config.Download.Comp.PromptToCreateModIndex))
                return;
            var choice = ModMain.MyMsgBox(Lang.Text("Setup.GameManage.ModIndex.Prompt", instance.Name),
                Lang.Text("Setup.GameManage.ModIndex.Title"), Lang.Text("Setup.GameManage.ModIndex.Create"),
                Lang.Text("Setup.GameManage.ModIndex.Later"), Lang.Text("Setup.GameManage.ModIndex.Never"));
            if (choice == 3) Config.Download.Comp.PromptToCreateModIndex = false;
            if (choice == 1) ModIndexService.CreateNow(instance);
        }));
    }
}
