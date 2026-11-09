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
internal struct XvcRegionHeader
{
    public uint Id;
    public ushort KeyId;
    public ushort Padding6;
    public uint Flags;
    public uint FirstSegmentIndex;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string Description;

    public ulong Offset;
    public ulong Length;
    public ulong Hash;
    public ulong Unknown68;
    public ulong Unknown70;
    public ulong Unknown78;
}
