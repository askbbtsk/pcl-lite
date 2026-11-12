using System;
using System.Collections.Generic;
using System.Linq;

namespace PCL.Core.App.Lunar;

/// <summary>Recognizes JVM fatal native allocation reports without rewriting game files or memory settings.</summary>
public static class LunarCrashDiagnostics
{
    public static bool IsNativeMemoryFailure(IEnumerable<string> output) => output.Any(line =>
        line.Contains("There is insufficient memory for the Java Runtime Environment to continue",
            StringComparison.OrdinalIgnoreCase)
        || (line.Contains("Native memory allocation", StringComparison.OrdinalIgnoreCase)
            && line.Contains("failed to", StringComparison.OrdinalIgnoreCase)));
}
