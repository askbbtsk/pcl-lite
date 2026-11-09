using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using PCL.Core.App;
using PCL.Core.Logging;
using PCL.Core.UI;
using PCL.Core.UI.Theme;
using PCL.Network;
using PCL.Network.Loaders;
using FileSystem = Microsoft.VisualBasic.FileSystem;
using SearchOption = System.IO.SearchOption;
using PCL.Core.App.Localization;
using PCL.Core.Utils;

namespace PCL;

public partial class PageInstanceCompResource
{


    /// <summary>
    ///     显示 Litematic 文件的详细信息
    /// </summary>
    private void ShowLitematicDetails(List<string> contentLines, ModLocalComp.LocalCompFile modEntry)
    {
        contentLines.Add("");
        contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.DetailInfo"));

        // 显示原始名称（从 NBT Metadata/Name 读取）
        if (modEntry.LitematicOriginalName is not null) contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.OriginalName") + modEntry.LitematicOriginalName);

        // 显示版本信息
        if (modEntry.LitematicVersion.HasValue) contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.Version") + modEntry.LitematicVersion.Value);

        // 显示尺寸信息
        if (modEntry.LitematicEnclosingSize is not null) contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.EnclosingSize") + modEntry.LitematicEnclosingSize);

        // 显示方块和体积统计
        if (modEntry.LitematicTotalBlocks.HasValue)
            contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.TotalBlocks") + Lang.Number(modEntry.LitematicTotalBlocks.Value, "N0"));

        if (modEntry.LitematicTotalVolume.HasValue)
            contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.TotalVolume") + Lang.Number(modEntry.LitematicTotalVolume.Value, "N0"));

        // 显示区域数量
        if (modEntry.LitematicRegionCount.HasValue) contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.RegionCount") + modEntry.LitematicRegionCount.Value);

        // 显示时间信息
        if (modEntry.LitematicTimeCreated.HasValue)
            try
            {
                var createdTime = DateTimeOffset.FromUnixTimeMilliseconds(modEntry.LitematicTimeCreated.Value)
                    .ToLocalTime().DateTime;
                contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.CreatedTime") + Lang.Date(createdTime, "G"));
            }
            catch
            {
                contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.CreatedTime") + modEntry.LitematicTimeCreated.Value);
            }

        if (modEntry.LitematicTimeModified.HasValue)
            try
            {
                var modifiedTime = DateTimeOffset.FromUnixTimeMilliseconds(modEntry.LitematicTimeModified.Value)
                    .ToLocalTime().DateTime;
                contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.ModifiedTime") + Lang.Date(modifiedTime, "G"));
            }
            catch
            {
                contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.ModifiedTime") + modEntry.LitematicTimeModified.Value);
            }
    }


    /// <summary>
    ///     显示 Schem 文件的详细信息
    /// </summary>
    private void ShowSchemDetails(List<string> contentLines, ModLocalComp.LocalCompFile modEntry)
    {
        contentLines.Add("");
        contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.DetailInfo"));

        // 显示原始名称（从 NBT Metadata/Name 读取）
        if (modEntry.SchemOriginalName is not null) contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.OriginalName") + modEntry.SchemOriginalName);

        // 显示版本信息
        if (modEntry.StructureGameVersion is not null) contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.GameVersion") + modEntry.StructureGameVersion);

        if (modEntry.SpongeVersion.HasValue) contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.SpongeVersion") + modEntry.SpongeVersion.Value);

        if (modEntry.StructureDataVersion.HasValue) contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.DataVersion") + modEntry.StructureDataVersion.Value);

        // 显示尺寸信息
        if (modEntry.LitematicEnclosingSize is not null) contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.EnclosingDimensions") + modEntry.LitematicEnclosingSize);

        // 显示方块和体积统计
        if (modEntry.LitematicTotalBlocks.HasValue)
            contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.TotalBlocks") + Lang.Number(modEntry.LitematicTotalBlocks.Value, "N0"));

        if (modEntry.LitematicTotalVolume.HasValue)
            contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.TotalVolume") + Lang.Number(modEntry.LitematicTotalVolume.Value, "N0"));

        // 显示区域数量
        if (modEntry.LitematicRegionCount.HasValue) contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.RegionCount") + modEntry.LitematicRegionCount.Value);

        contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.FileType",
            Lang.Text("Instance.Resource.Item.Schematic.FileType.Sponge")));
    }


    /// <summary>
    ///     显示 Schematic 文件的详细信息
    /// </summary>
    private void ShowSchematicDetails(List<string> contentLines, ModLocalComp.LocalCompFile modEntry)
    {
        contentLines.Add("");
        contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.DetailInfo"));

        // 显示尺寸信息
        if (modEntry.LitematicEnclosingSize is not null) contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.Size") + modEntry.LitematicEnclosingSize);

        // 显示方块和体积统计
        if (modEntry.LitematicTotalBlocks.HasValue)
            contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.TotalBlocks") + Lang.Number(modEntry.LitematicTotalBlocks.Value, "N0"));

        if (modEntry.LitematicTotalVolume.HasValue)
            contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.TotalVolume") + Lang.Number(modEntry.LitematicTotalVolume.Value, "N0"));

        contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.FileType",
            Lang.Text("Instance.Resource.Item.Schematic.FileType.Mcedit")));
    }


    /// <summary>
    ///     显示 NBT 结构文件的详细信息
    /// </summary>
    private void ShowNbtDetails(List<string> contentLines, ModLocalComp.LocalCompFile modEntry)
    {
        contentLines.Add("");
        contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.DetailInfo"));

        // 显示作者信息
        if (modEntry.StructureAuthor is not null) contentLines.Add(Lang.Text("Instance.Resource.Item.Info.Author", modEntry.StructureAuthor));

        // 显示版本信息
        if (modEntry.StructureGameVersion is not null) contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.GameVersion") + modEntry.StructureGameVersion);

        if (modEntry.StructureDataVersion.HasValue) contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.DataVersion") + modEntry.StructureDataVersion.Value);

        // 显示尺寸信息
        if (modEntry.LitematicEnclosingSize is not null) contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.EnclosingDimensions") + modEntry.LitematicEnclosingSize);

        // 显示方块和体积统计
        if (modEntry.LitematicTotalBlocks.HasValue)
            contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.TotalBlocks") + Lang.Number(modEntry.LitematicTotalBlocks.Value, "N0"));

        if (modEntry.LitematicTotalVolume.HasValue)
            contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.TotalVolume") + Lang.Number(modEntry.LitematicTotalVolume.Value, "N0"));

        // 显示区域数量
        if (modEntry.LitematicRegionCount.HasValue) contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.RegionCount") + modEntry.LitematicRegionCount.Value);

        contentLines.Add(Lang.Text("Instance.Resource.Item.Schematic.FileType",
            Lang.Text("Instance.Resource.Item.Schematic.FileType.Nbt")));
    }



    private void ShowDebugInfo(List<string> contentLines, ModLocalComp.LocalCompFile modEntry)
    {
        var debugInfo = new List<string>();
        if (modEntry.ModId is not null) debugInfo.Add(Lang.Text("Instance.Resource.Item.Info.ModId", modEntry.ModId));
        if (modEntry.Dependencies.Any())
        {
            debugInfo.Add(Lang.Text("Instance.Resource.Item.Info.Dependency"));
            foreach (var Dep in modEntry.Dependencies)
                debugInfo.Add(" - " + Dep.Key + (Dep.Value is null
                    ? Dep.Key
                    : Lang.Text("Instance.Resource.Item.Info.DependencyVersion", Dep.Key, Dep.Value)));
        }

        if (debugInfo.Any())
        {
            contentLines.Add("");
            contentLines.AddRange(debugInfo);
        }
    }


    private void ShowSchematicDialog(List<string> contentLines, ModLocalComp.LocalCompFile modEntry)
    {
        // 投影原理图文件不显示百科搜索选项
        if (modEntry.Url is null)
            ModMain.MyMsgBox(contentLines.Join("\r\n"), modEntry.Name, Lang.Text("Instance.Resource.Item.Info.Return"));
        else if (ModMain.MyMsgBox(contentLines.Join("\r\n"), modEntry.Name, Lang.Text("Instance.Resource.Item.Info.OpenWebsite"), Lang.Text("Instance.Resource.Item.Info.Return")) == 1)
            ModBase.OpenWebsite(modEntry.Url);
    }

}
