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
internal struct SegmentMetadataHeader
{
    public uint Magic;
    public uint Version0;
    public uint Version1;
    public uint HeaderLength;
    public uint SegmentCount;
    public uint FilePathsLength;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] PDUID;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 60)]
    public byte[] Unknown;
}
