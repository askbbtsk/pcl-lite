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
        ///     读取 .schematic 文件的 NBT 数据（MCEdit/WorldEdit 格式）。
        /// </summary>
        private void LoadSchematicNbtData()
        {
            try
            {
                ModBase.Log($"开始读取 Schematic NBT 数据：{path}", ModBase.LogLevel.Debug);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var scheNbt = new NbtFile();
                    scheNbt.LoadFromStream(fs, NbtCompression.AutoDetect);
                    // 读取尺寸信息
                    var widthTag = scheNbt.RootTag.Get<NbtShort>("Width");
                    var heightTag = scheNbt.RootTag.Get<NbtShort>("Height");
                    var lengthTag = scheNbt.RootTag.Get<NbtShort>("Length");
                    if (widthTag is not null && heightTag is not null && lengthTag is not null)
                    {
                        _litematicEnclosingSize = $"{widthTag.Value} × {heightTag.Value} × {lengthTag.Value}";
                        _litematicTotalVolume = (short)(widthTag.Value * heightTag.Value) * lengthTag.Value;
                    }

                    // 读取材料列表
                    var materialsTag = scheNbt.RootTag.Get<NbtString>("Materials");
                    if (materialsTag is not null)
                        ModBase.Log($"Schematic 材料类型：{materialsTag.Value}", ModBase.LogLevel.Debug);

                    ModBase.Log("Schematic NBT 数据读取完成", ModBase.LogLevel.Debug);
                }
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "读取 Schematic NBT 数据时出错（" + path + "）");
            }
        }

}
}
