using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Instances;

namespace PCL.Core.Test.Minecraft.Instances;

[TestClass]
public sealed class VersionInheritanceGuardTests
{
    private static string Instance(string name) => Path.Combine(Path.GetTempPath(), "inheritance-tests", name);

    [TestMethod]
    public void SelfTwoNodeAndLongerCyclesRejectBeforeUnboundedRecursion()
    {
        foreach (var chain in new[] { new[] { "A", "A" }, new[] { "A", "B", "A" }, new[] { "A", "B", "C", "A" } })
        {
            void Resolve(int index)
            {
                using var guard = VersionInheritanceGuard.Enter(Instance(chain[index]));
                if (index + 1 < chain.Length) Resolve(index + 1);
            }
            Assert.Throws<VersionInheritanceException>(() => Resolve(0));
            using var retry = VersionInheritanceGuard.Enter(Instance("A"));
        }
    }

    [TestMethod]
    public void NormalizedCaseInsensitiveAliasesCannotBypassCycleDetection()
    {
        using var active = VersionInheritanceGuard.Enter(Instance("Case"));
        Assert.Throws<VersionInheritanceException>(() => VersionInheritanceGuard.Enter(Instance("case") + Path.DirectorySeparatorChar));
        Assert.Throws<VersionInheritanceException>(() => VersionInheritanceGuard.Enter(Path.Combine(Instance("other"), "..", "Case")));
    }

    [TestMethod]
    public void DepthLimitIsBoundedAndAllScopesCanBeReleasedAfterFailure()
    {
        var guards = new List<IDisposable>();
        try
        {
            for (int index = 0; index < VersionInheritanceGuard.MaximumDepth; index++)
                guards.Add(VersionInheritanceGuard.Enter(Instance(index.ToString())));
            Assert.Throws<VersionInheritanceException>(() => VersionInheritanceGuard.Enter(Instance("overflow")));
        }
        finally { for (int index = guards.Count - 1; index >= 0; index--) guards[index].Dispose(); }
        using var retry = VersionInheritanceGuard.Enter(Instance("0"));
    }

    [TestMethod]
    public void IndependentReadersAndDiamondParentsDoNotShareActiveChains()
    {
        using var active = VersionInheritanceGuard.Enter(Instance("A"));
        Task.Run(() =>
        {
            using var independent = VersionInheritanceGuard.Enter(Instance("A"));
        }).GetAwaiter().GetResult();
        using (VersionInheritanceGuard.Enter(Instance("base"))) { }
        using (VersionInheritanceGuard.Enter(Instance("base"))) { }
    }

    [TestMethod]
    public void FailureAndIdempotentCleanupAllowFreshResolution()
    {
        Assert.Throws<IOException>(() =>
        {
            using var guard = VersionInheritanceGuard.Enter(Instance("A"));
            throw new IOException("Invalid parent JSON");
        });
        var retry = VersionInheritanceGuard.Enter(Instance("A"));
        retry.Dispose();
        retry.Dispose();
        using var subsequent = VersionInheritanceGuard.Enter(Instance("A"));
    }
}
