using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace PCL.Core.Test;

internal static class TestRepository
{
    // Source-based lookup also works when test artifacts are kept on a different drive.
    public static string FindRoot([CallerFilePath] string sourceFile = "")
    {
        foreach (string? start in new[] { Path.GetDirectoryName(sourceFile), AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(start)) continue;
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            {
                string root = directory.FullName;
                if (File.Exists(Path.Combine(root, "PCL.Core", "PCL.Core.csproj")) &&
                    File.Exists(Path.Combine(root, "PCL.Core.Test", "PCL.Core.Test.csproj")))
                    return root;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the PCL Lite source tree from '{sourceFile}' or '{AppContext.BaseDirectory}'.");
    }
}
