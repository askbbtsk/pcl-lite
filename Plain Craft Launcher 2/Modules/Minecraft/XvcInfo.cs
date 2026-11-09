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


[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct XvcInfo
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] ContentId;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 192)]
    public XvcEncryptionKeyId[] EncryptionKeyIds;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public byte[] Description;

    public uint Version;
    public uint RegionCount;
    public uint Flags;
    public ushort PaddingD1C;
    public ushort KeyCount;
    public uint UnknownD20;
    public uint InitialPlayRegionId;
    public ulong InitialPlayOffset;
    public long FileTimeCreated;
    public uint PreviewRegionId;
    public uint UpdateSegmentCount;
    public ulong PreviewOffset;
    public ulong UnusedSpace;
    public uint RegionSpecifierCount;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 84)]
    public byte[] ReservedD54;
}
