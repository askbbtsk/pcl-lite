using System.Collections.Generic;
using System.Linq;

namespace PCL.Core.Minecraft.Packs;

/// <summary>The existing installer success marker is necessary, but a nonzero exit still means failure.</summary>
public static class InstallerCompletionPolicy
{
    public static bool IsSuccessful(int exitCode, IEnumerable<string> lastLines)
        => exitCode == 0 && lastLines.Any(line => line == "true");
}
