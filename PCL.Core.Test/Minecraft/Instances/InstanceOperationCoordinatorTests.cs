using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Instances;

namespace PCL.Core.Test.Minecraft.Instances;

[TestClass]
public sealed class InstanceOperationCoordinatorTests
{
    [TestMethod]
    public void SameRootConflictsAndFailedMultiRootAdmissionReleasesEarlierRoots()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PCLInstanceGates", Guid.NewGuid().ToString("N"));
        try
        {
            var rootA = Path.Combine(directory, "a");
            var rootB = Path.Combine(directory, "b");
            using (InstanceOperationCoordinator.Acquire(Path.Combine(directory, "gates"), rootB))
            {
                Assert.ThrowsExactly<IOException>(() => InstanceOperationCoordinator.Acquire(Path.Combine(directory, "gates"), rootA, rootB));
                using var independent = InstanceOperationCoordinator.Acquire(Path.Combine(directory, "gates"), rootA);
                Assert.IsFalse(Directory.Exists(rootA));
                Assert.IsFalse(Directory.Exists(rootB));
            }
            using (InstanceOperationCoordinator.Acquire(Path.Combine(directory, "gates"), rootB, rootA)) { }
            using (InstanceOperationCoordinator.Acquire(Path.Combine(directory, "gates"), rootA.ToUpperInvariant())) { }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
