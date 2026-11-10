using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.InstanceMigration;

namespace PCL.Core.Test.Minecraft.InstanceMigration;

[TestClass]
[DoNotParallelize]
public sealed class ModificationWorkspaceTests
{
    [TestMethod]
    public void StageLivesUnderSystemTempAndPreservesStandardMinecraftLayout()
    {
        string root;
        using (var workspace = ModificationWorkspace.Create())
        {
            root = workspace.Root;
            Assert.IsTrue(Path.GetFullPath(root).StartsWith(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(Path.Combine(root, "versions", workspace.StageName), workspace.StageDirectory);
            Assert.AreEqual(root, workspace.MinecraftRoot);
            Assert.IsTrue(Directory.Exists(workspace.StageDirectory));
            File.WriteAllText(Path.Combine(workspace.StageDirectory, workspace.StageName + ".json"), "{}");
            File.WriteAllText(Path.Combine(workspace.StageDirectory, "download.tmp"), "payload");
        }
        Assert.IsFalse(Directory.Exists(root), "Successful work must remove the whole scratch root.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailureAndCancellationRemoveScratchWithoutTouchingSource(bool cancelled)
    {
        var source = Path.Combine(Path.GetTempPath(), "PCLWorkspaceSource-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        var original = Path.Combine(source, "user-config.txt");
        File.WriteAllText(original, "user content");
        string? scratch = null;
        try
        {
            try
            {
                using var workspace = ModificationWorkspace.Create();
                scratch = workspace.Root;
                File.Copy(original, Path.Combine(workspace.StageDirectory, "copied.txt"));
                if (cancelled) throw new OperationCanceledException();
                throw new InvalidDataException("installer failed");
            }
            catch (Exception error) when (error is OperationCanceledException or InvalidDataException) { }
            Assert.IsNotNull(scratch);
            Assert.IsFalse(Directory.Exists(scratch));
            Assert.AreEqual("user content", File.ReadAllText(original));
        }
        finally { Directory.Delete(source, true); }
    }

    [TestMethod]
    public void StartupCleanupNeverDeletesAnActiveWorkspace()
    {
        using var first = ModificationWorkspace.Create();
        using var second = ModificationWorkspace.Create();
        ModificationWorkspace.CleanupAbandoned();
        Assert.IsTrue(Directory.Exists(first.StageDirectory));
        Assert.IsTrue(Directory.Exists(second.StageDirectory));
        Assert.AreNotEqual(first.Root, second.Root);
    }

    [TestMethod]
    public void StartupCleanupRemovesOnlyMarkedUnlockedWorkspace()
    {
        var identity = Guid.NewGuid().ToString("N");
        var abandoned = Path.Combine(ModificationWorkspace.TempRoot, identity);
        var unowned = Path.Combine(ModificationWorkspace.TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(abandoned);
        Directory.CreateDirectory(unowned);
        try
        {
            File.WriteAllText(Path.Combine(abandoned, ".pcl-modify-workspace"), "PCL-Lite modification workspace v1\n" + identity);
            File.WriteAllText(Path.Combine(abandoned, ".lease"), "");
            File.WriteAllText(Path.Combine(abandoned, "incomplete-download.jar"), "partial");
            File.WriteAllText(Path.Combine(unowned, "user.txt"), "keep");
            ModificationWorkspace.CleanupAbandoned();
            Assert.IsFalse(Directory.Exists(abandoned));
            Assert.AreEqual("keep", File.ReadAllText(Path.Combine(unowned, "user.txt")));
        }
        finally
        {
            if (Directory.Exists(abandoned)) Directory.Delete(abandoned, true);
            Directory.Delete(unowned, true);
        }
    }

    [TestMethod]
    public void TamperedOwnershipIsPreservedOnDisposeAndStartupCleanup()
    {
        var workspace = ModificationWorkspace.Create();
        try
        {
            File.WriteAllText(Path.Combine(workspace.Root, ".pcl-modify-workspace"), "not ours");
            File.WriteAllText(Path.Combine(workspace.StageDirectory, "user.txt"), "keep");
            workspace.Dispose();
            Assert.IsNotNull(workspace.CleanupError);
            ModificationWorkspace.CleanupAbandoned();
            Assert.AreEqual("keep", File.ReadAllText(Path.Combine(workspace.StageDirectory, "user.txt")));
            workspace.Dispose();
        }
        finally { workspace.Dispose(); Directory.Delete(workspace.Root, true); }
    }

    [TestMethod]
    public void LockedPayloadDefersCleanupUntilNextSafeAttempt()
    {
        var workspace = ModificationWorkspace.Create();
        var payload = Path.Combine(workspace.StageDirectory, "busy.jar");
        File.WriteAllText(payload, "payload");
        try
        {
            using (var held = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                workspace.Dispose();
                Assert.IsNotNull(workspace.CleanupError);
                Assert.IsTrue(File.Exists(payload));
            }
            ModificationWorkspace.CleanupAbandoned();
            Assert.IsFalse(Directory.Exists(workspace.Root));
        }
        finally
        {
            workspace.Dispose();
            if (Directory.Exists(workspace.Root)) Directory.Delete(workspace.Root, true);
        }
    }

    [TestMethod]
    public void CleanupIsIdempotent()
    {
        var workspace = ModificationWorkspace.Create();
        workspace.Dispose();
        workspace.Dispose();
        ModificationWorkspace.CleanupAbandoned();
        Assert.IsNull(workspace.CleanupError);
        Assert.IsFalse(Directory.Exists(workspace.Root));
    }

    [TestMethod]
    public void WorkspaceCleanupRefusesLinksAndPreservesTheirExternalTarget()
    {
        var outside = Path.Combine(Path.GetTempPath(), "PCLWorkspaceOutside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "user.txt"), "keep outside");
        var workspace = ModificationWorkspace.Create();
        var link = Path.Combine(workspace.StageDirectory, "linked");
        try
        {
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException)
            { Assert.Inconclusive("Symbolic link creation is unavailable: " + error.Message); }
            workspace.Dispose();
            Assert.IsNotNull(workspace.CleanupError);
            ModificationWorkspace.CleanupAbandoned();
            Assert.AreEqual("keep outside", File.ReadAllText(Path.Combine(outside, "user.txt")));
            Directory.Delete(link);
            ModificationWorkspace.CleanupAbandoned();
            Assert.IsFalse(Directory.Exists(workspace.Root));
        }
        finally
        {
            workspace.Dispose();
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(workspace.Root)) Directory.Delete(workspace.Root, true);
            Directory.Delete(outside, true);
        }
    }
}
