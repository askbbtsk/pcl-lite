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



        public override string ToString()
        {
            return $"{State} - {path}";
        }


        public override bool Equals(object obj)
        {
            var target = obj as LocalCompFile;
            return target is not null && (path ?? "") == (target.path ?? "");
        }


        public override int GetHashCode() => (path ?? "").GetHashCode(StringComparison.Ordinal);

}
}
