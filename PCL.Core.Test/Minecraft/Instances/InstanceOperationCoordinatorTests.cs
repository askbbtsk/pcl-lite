using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

    [TestMethod]
    public void ExistingShortAliasAndNewChildUseTheSameRootLockWhen83IsAvailable()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows 8.3 aliases require Windows."); return; }
        var directory = Path.Combine(Path.GetTempPath(), "PCL Instance Root Long Name", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "Long Game Directory");
        Directory.CreateDirectory(root);
        try
        {
            var buffer = new StringBuilder(32768);
            var count = GetShortPathName(root, buffer, (uint)buffer.Capacity);
            var alias = buffer.ToString();
            if (count == 0 || count >= buffer.Capacity || !alias.Contains('~'))
            { Assert.Inconclusive("The temporary volume does not expose 8.3 aliases."); return; }
            var gates = Path.Combine(directory, "gates");
            using (InstanceOperationCoordinator.Acquire(gates, root))
                Assert.ThrowsExactly<IOException>(() => InstanceOperationCoordinator.Acquire(gates, alias));
            var child = Path.Combine(root, "not-created", "target");
            using (InstanceOperationCoordinator.Acquire(gates, child))
                Assert.ThrowsExactly<IOException>(() => InstanceOperationCoordinator.Acquire(gates,
                    Path.Combine(alias, "not-created", "target")));
            Assert.IsFalse(Directory.Exists(child));
            using (InstanceOperationCoordinator.Acquire(gates, root, alias)) { }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferLength);
}
