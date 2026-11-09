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

using static PCL.ModLocalComp;

namespace PCL;

internal static class ResourceFolderNames
{

    public static string GetPathNameByCompType(CompType theType)
    {
        switch (theType)
        {
            case CompType.Mod:
            {
                return "mods";
            }
            case CompType.ResourcePack:
            {
                return "resourcepacks";
            }
            case CompType.Shader:
            {
                return "shaderpacks";
            }
            case CompType.Schematic:
            {
                return "schematics";
            }
            case CompType.World:
            {
                return "saves";
            }
        }

        return "Nothing";
    }

}
