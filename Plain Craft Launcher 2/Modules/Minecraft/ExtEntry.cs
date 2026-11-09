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


[StructLayout(LayoutKind.Sequential)]
internal struct ExtEntry
{
    public uint Code;
    public uint Length;
    public ulong Offset;
    public uint DataLength;
    public uint Reserved;
}
