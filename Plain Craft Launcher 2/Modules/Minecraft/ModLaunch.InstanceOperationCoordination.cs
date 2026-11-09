using System;
using System.IO;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft.Instances;

namespace PCL;

public static partial class ModLaunch
{
    private static IDisposable AcquireLaunchOperation()
    {
        try
        {
            return InstanceOperationCoordinator.Acquire(Path.Combine(Paths.SharedLocalData, "InstanceOperations"),
                ModFolder.mcFolderSelected, ModInstanceList.McMcInstanceSelected.PathIndie);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            HintService.Hint(Lang.Text("Instance.Enhancement.OperationBusy"), HintType.Warning);
            throw new ModBase.CancelledException();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                                      or System.Security.SecurityException)
        {
            // Optional coordination storage must not break ordinary play. Mutating enhancement
            // operations require the same gate and remain unavailable while it cannot be acquired.
            ModBase.Log(error, "[Enhancements] Launch coordination unavailable; preserving normal launch", ModBase.LogLevel.Normal);
            return null;
        }
    }
}
