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



[Flags]
internal enum XvdVolumeAttributes : uint
{
    ReadOnly = 1,
    EncryptionDisabled = 2,
    DataIntegrityDisabled = 4,
    LegacySectorSize = 8,
    ResiliencyEnabled = 0x10,
    SraReadOnly = 0x20,
    RegionIdInXts = 0x40,
    EraSpecific = 0x80
}
