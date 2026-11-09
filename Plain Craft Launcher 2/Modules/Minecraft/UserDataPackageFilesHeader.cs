using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using PCL.Core.App;
using PCL.Core.App.Localization;

namespace PCL;


[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
internal struct UserDataPackageFilesHeader
{
    public uint Version;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string PackageFullName;

    public uint FileCount;
}
