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


[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1, Size = 16)]
internal struct Segment
{
    public ushort Flags;
    public ushort PathLength;
    public uint PathOffset;
    public ulong FileSize;
}
