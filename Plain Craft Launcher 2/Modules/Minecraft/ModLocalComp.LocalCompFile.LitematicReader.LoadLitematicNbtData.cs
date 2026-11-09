using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using fNbt;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Core.Utils.Exts;
using PCL.Core.Utils.Hash;
using static PCL.ModComp;
using static PCL.ModLoader;

namespace PCL;

public static partial class ModLocalComp
{
public partial class LocalCompFile
{


        /// <summary>
        ///     读取 Litematic 文件的 NBT 数据。
        /// </summary>
        private void LoadLitematicNbtData()
        {
            try
            {
                ModBase.Log($"开始读取 Litematic NBT 数据：{path}", ModBase.LogLevel.Debug);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var scheNbt = new NbtFile();
                    scheNbt.LoadFromStream(fs, NbtCompression.AutoDetect);
                    // 读取版本信息
                    var versionTag = (NbtInt)scheNbt.RootTag.Get("Version");
                    if (versionTag is not null) _litematicVersion = versionTag.Value;

                    // 读取 Metadata 节点
                    var metadataTag = scheNbt.RootTag.Get<NbtCompound>("Metadata");
                    if (metadataTag is not null)
                    {
                        ModBase.Log("找到 Litematic Metadata 节点", ModBase.LogLevel.Debug);

                        // 读取名称
                        var nameTag = metadataTag.Get<NbtString>("Name");
                        if (nameTag is not null && !string.IsNullOrWhiteSpace(nameTag.Value) &&
                            nameTag.Value != "Unnamed") _litematicOriginalName = nameTag.Value;

                        // 读取描述信息
                        var descriptionTag = metadataTag.Get<NbtString>("Description");
                        if (descriptionTag is not null && !string.IsNullOrWhiteSpace(descriptionTag.Value))
                            _Description = descriptionTag.Value;

                        // 读取作者信息
                        var authorTag = metadataTag.Get<NbtString>("Author");
                        if (authorTag is not null && !string.IsNullOrWhiteSpace(authorTag.Value))
                            _Authors = authorTag.Value;

                        // 读取时间信息
                        var timeCreatedTag = metadataTag.Get<NbtLong>("TimeCreated");
                        if (timeCreatedTag is not null) _litematicTimeCreated = timeCreatedTag.Value;

                        var timeModifiedTag = metadataTag.Get<NbtLong>("TimeModified");
                        if (timeModifiedTag is not null) _litematicTimeModified = timeModifiedTag.Value;

                        // 读取包围盒大小
                        var enclosingSizeTag = metadataTag.Get<NbtCompound>("EnclosingSize");
                        if (enclosingSizeTag is not null)
                        {
                            var xTag = enclosingSizeTag.Get<NbtInt>("x");
                            var yTag = enclosingSizeTag.Get<NbtInt>("y");
                            var zTag = enclosingSizeTag.Get<NbtInt>("z");
                            if (xTag is not null && yTag is not null && zTag is not null)
                                _litematicEnclosingSize = $"{xTag.Value} × {yTag.Value} × {zTag.Value}";
                        }

                        // 读取区域数量
                        var regionCountTag = metadataTag.Get<NbtInt>("RegionCount");
                        if (regionCountTag is not null) _litematicRegionCount = regionCountTag.Value;

                        // 读取总方块数
                        var totalBlocksTag = metadataTag.Get<NbtInt>("TotalBlocks");
                        if (totalBlocksTag is not null) _litematicTotalBlocks = totalBlocksTag.Value;

                        // 读取总体积
                        var totalVolumeTag = metadataTag.Get<NbtInt>("TotalVolume");
                        if (totalVolumeTag is not null) _litematicTotalVolume = totalVolumeTag.Value;
                    }
                    else
                    {
                        ModBase.Log("未找到 Litematic Metadata 节点", ModBase.LogLevel.Debug);
                    }
                }

                ModBase.Log("Litematic NBT 数据读取完成", ModBase.LogLevel.Debug);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "读取 Litematic NBT 数据时出错（" + path + "）");
            }
        }

}
}
