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
        ///     读取 .nbt 文件的 NBT 数据（Minecraft 结构文件格式）。
        /// </summary>
        private void LoadStructureNbtData()
        {
            try
            {
                ModBase.Log($"开始读取 NBT 结构文件数据：{path}", ModBase.LogLevel.Debug);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var scheNbt = new NbtFile();
                    scheNbt.LoadFromStream(fs, NbtCompression.AutoDetect);
                    // 读取作者信息
                    var authorTag = scheNbt.RootTag.Get<NbtString>("author");
                    if (authorTag is not null && !string.IsNullOrWhiteSpace(authorTag.Value))
                    {
                        _structureAuthor = authorTag.Value;
                        if (_Authors is null)
                            _Authors = _structureAuthor;
                    }

                    // 读取尺寸信息
                    var sizeTag = scheNbt.RootTag.Get<NbtList>("size");
                    if (sizeTag is not null)
                    {
                        var sizeElements = sizeTag.ToArray();
                        if (sizeElements.Length >= 3)
                        {
                            var sizeArray = sizeElements.Take(3).Select(e => e.IntValue).ToArray();
                            _litematicEnclosingSize = $"{sizeArray[0]} × {sizeArray[1]} × {sizeArray[2]}";
                            _litematicTotalVolume = sizeArray[0] * sizeArray[1] * sizeArray[2];
                        }
                    }

                    // 读取方块数量信息
                    var blocksTag = scheNbt.RootTag.Get<NbtList>("blocks");
                    if (blocksTag is not null)
                        _litematicTotalBlocks = blocksTag.Where(x => x.TagType == NbtTagType.Compound).Count();

                    // 读取调色板信息来计算区域数量
                    var paletteTag = scheNbt.RootTag.Get<NbtList>("palette");
                    if (paletteTag is not null) _litematicRegionCount = 1; // 原版结构文件通常只有一个区域
                }
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "读取 NBT 结构文件数据时出错（" + path + "）");
            }
        }

}
}
