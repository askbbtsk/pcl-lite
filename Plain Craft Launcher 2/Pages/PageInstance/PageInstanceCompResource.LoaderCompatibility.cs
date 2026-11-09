using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PCL.Core.App;

namespace PCL;

internal sealed record ModLoaderCompatibilityContext(string InstancePath, string GameDirectory,
    string MinecraftVersion, IReadOnlyList<string> Loaders, bool HasBridge)
{
    internal static IReadOnlyList<string> GetLoaders(McInstanceInfo info)
    {
        var loaders = new List<string>();
        if (info.HasForge || info.HasCleanroom) loaders.Add("Forge");
        if (info.HasNeoForge) loaders.Add("NeoForge");
        if (info.HasFabric || info.HasLegacyFabric || info.HasLabyMod) loaders.Add("Fabric");
        if (info.HasQuilt) loaders.Add("Quilt");
        return loaders;
    }

    internal bool IsCurrent(ModLocalComp.LocalCompFile entry)
    {
        var current = PageInstanceLeft.McInstance;
        if (current?.Info is null || !string.Equals(current.PathInstance, InstancePath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(current.PathIndie, GameDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(current.Info.VanillaName, MinecraftVersion, StringComparison.Ordinal)
            || !GetLoaders(current.Info).SequenceEqual(Loaders, StringComparer.OrdinalIgnoreCase)) return false;
        var root = Path.GetFullPath(GameDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(entry.path).StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && entry.LoaderMetadataIsCurrent();
    }
}

public partial class PageInstanceCompResource
{
    private ModLoaderCompatibilityContext? _loaderCompatibilityContext;

    private void CaptureLoaderCompatibilityContext()
    {
        _loaderCompatibilityContext = null;
        try
        {
            if (currentCompType != ModComp.CompType.Mod || !Config.Launch.MarkIncompatibleMods) return;
            var instance = PageInstanceLeft.McInstance;
            if (instance?.Info is null) return;
            var bridgeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "connector", "sinytra_connector", "forgified_fabric_api", "kilt", "patchwork",
            };
            var hasBridge = ModLocalComp.compResourceListLoader.output
                .Where(entry => !entry.IsFolder && entry.State == ModLocalComp.LocalCompFile.LocalFileStatus.Fine)
                .Any(entry => bridgeIds.Contains(entry.ModId ?? "") || entry.ProvidedIds.Any(bridgeIds.Contains));
            _loaderCompatibilityContext = new ModLoaderCompatibilityContext(instance.PathInstance, instance.PathIndie,
                instance.Info.VanillaName, ModLoaderCompatibilityContext.GetLoaders(instance.Info), hasBridge);
        }
        catch (Exception ex)
        {
            // Optional annotations may not prevent the ordinary resource list from opening.
            ModBase.Log(ex, "无法创建可选模组加载器标注上下文", ModBase.LogLevel.Developer);
        }
    }
}
