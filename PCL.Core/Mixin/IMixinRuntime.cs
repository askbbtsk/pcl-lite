using System;
using System.Collections.Generic;
using System.Reflection;

namespace PCL.Mixin;

internal interface IMixinRuntime
{
    IReadOnlyList<MixinPatchInfo> Patches { get; }
    IReadOnlyList<MixinConflictInfo> Conflicts { get; }
    MixinApplyResult ApplyAssembly(Assembly assembly);
    MixinApplyResult ApplyConfiguration(
        Assembly assembly,
        MixinConfiguration configuration,
        string configurationName);
}

public sealed record MixinApplyResult(
    Assembly Assembly,
    int MixinCount,
    int TargetMethodCount,
    IReadOnlyList<string> Warnings);

public sealed class MixinApplyException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
