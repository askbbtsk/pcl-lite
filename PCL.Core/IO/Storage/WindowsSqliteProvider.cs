using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PCL.Core.IO.Storage;

/// <summary>
/// Uses the SQLite shipped and serviced by Windows 10/11. Initialize before any
/// Microsoft.Data.Sqlite connection, including connections created by plugins.
/// No application-directory native library or downloaded fallback is loaded.
/// </summary>
internal static class WindowsSqliteProvider
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification =
        "Select the process-wide provider before consumers can open SQLite connections.")]
    internal static void Initialize()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(SQLitePCL.SQLite3Provider_winsqlite3).Assembly, ResolveSystemLibrary);
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        SQLitePCL.raw.FreezeProvider();
    }

    private static nint ResolveSystemLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        return libraryName is "winsqlite3" or "winsqlite3.dll"
            ? NativeLibrary.Load("winsqlite3.dll", assembly, DllImportSearchPath.System32)
            : 0;
    }
}
