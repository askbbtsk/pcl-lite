using System;

namespace PCL;

/// <summary>Uses the ordinary launch rules and selector for the new target, never the source Java preference.</summary>
internal static class InstanceMigrationJava
{
    internal static bool SelectForTarget(McInstance target)
    {
        var constraints = ModLaunch.ResolveJavaConstraints(target);
        if (constraints.Maximum < constraints.Minimum) return false;
        lock (ModJava.javaLock)
            return ModJava.JavaSelect("$$", constraints.Minimum, constraints.Maximum, target,
                enforceVersionRange: true, compatibleMajors: constraints.CompatibleMajors) is not null;
    }
}
