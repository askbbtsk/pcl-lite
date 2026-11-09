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
        ///     读取 .schem 文件的 NBT 数据（Sponge Schematic 格式）。
        /// </summary>
        private void LoadSchemNbtData()
        {
            try
            {
                ModBase.Log($"开始读取 Schem NBT 数据：{path}", ModBase.LogLevel.Debug);

                // 使用自动检测压缩格式
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var scheNbt = new NbtFile();
                    scheNbt.LoadFromStream(fs, NbtCompression.AutoDetect);

                    // 读取Sponge版本信息
                    var versionTag = scheNbt.RootTag.Get<NbtInt>("Version");
                    if (versionTag is not null) _spongeVersion = versionTag.Value;

                    // 读取数据版本信息
                    var dataVersionTag = scheNbt.RootTag.Get<NbtInt>("DataVersion");
                    if (dataVersionTag is not null) _structureDataVersion = dataVersionTag.Value;

                    // 读取尺寸信息
                    var widthTag = scheNbt.RootTag.Get<NbtShort>("Width");
                    var heightTag = scheNbt.RootTag.Get<NbtShort>("Height");
                    var lengthTag = scheNbt.RootTag.Get<NbtShort>("Length");

                    if (widthTag is not null && heightTag is not null && lengthTag is not null)
                    {
                        _litematicEnclosingSize = $"{widthTag.Value} × {heightTag.Value} × {lengthTag.Value}";
                        _litematicTotalVolume = (short)(widthTag.Value * heightTag.Value) * lengthTag.Value;

                        // 对于Sponge格式，方块数量等于总体积（因为包含空气方块）
                        _litematicTotalBlocks = _litematicTotalVolume;
                    }

                    // 读取调色板信息来计算区域数量
                    var paletteTag = scheNbt.RootTag.Get<NbtCompound>("Palette");
                    if (paletteTag is not null) _litematicRegionCount = 1; // Sponge Schematic 通常只有一个区域

                    // 读取元数据
                    var metadataTag = scheNbt.RootTag.Get<NbtCompound>("Metadata");
                    if (metadataTag is not null)
                    {
                        // 读取名称
                        var nameTag = metadataTag.Get<NbtString>("Name");
                        if (nameTag is not null && !string.IsNullOrWhiteSpace(nameTag.Value))
                            _schemOriginalName = nameTag.Value;

                        // 读取作者信息
                        var authorTag = metadataTag.Get<NbtString>("Author");
                        if (authorTag is not null && !string.IsNullOrWhiteSpace(authorTag.Value))
                        {
                            _structureAuthor = authorTag.Value;
                            if (_Authors is null)
                                _Authors = _structureAuthor;
                        }
                    }
                }

                ModBase.Log("Schem NBT 数据读取完成", ModBase.LogLevel.Debug);
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "读取 Schem NBT 数据时出错（" + path + "）");
            }
        }

}
}
