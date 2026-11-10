using System;
using System.IO;
using System.Threading;
using PCL.Core.Minecraft.Packs;

namespace PCL.Core.Minecraft.InstanceMigration;

/// <summary>Owned, leased scratch space under the OS temp folder; never a live Minecraft version.</summary>
public sealed class ModificationWorkspace : IDisposable
{
    private const string OwnerFile = ".pcl-modify-workspace";
    private const string LeaseFile = ".lease";
    private const string OwnerPrefix = "PCL-Lite modification workspace v1\n";
    private FileStream? _lease;
    private readonly string _identity;
    public string Root { get; }
    public string MinecraftRoot => Root;
    public string StageName { get; }
    public string StageDirectory => Path.Combine(Root, "versions", StageName);
    public Exception? CleanupError { get; private set; }
    public static string TempRoot => Path.Combine(Path.GetTempPath(), "PCL-Lite", "InstanceModification");

    private ModificationWorkspace(string root, string identity, FileStream lease)
    {
        Root = root;
        _identity = identity;
        _lease = lease;
        StageName = ".pcl-modify-" + identity;
    }

    public static ModificationWorkspace Create()
    {
        Directory.CreateDirectory(TempRoot);
        PackExportContent.CheckAncestors(Path.GetTempPath(), TempRoot);
        var identity = Guid.NewGuid().ToString("N");
        var root = Path.Combine(TempRoot, identity);
        Directory.CreateDirectory(root);
        PackExportContent.CheckAncestors(TempRoot, root);
        var lease = new FileStream(Path.Combine(root, LeaseFile), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var workspace = new ModificationWorkspace(root, identity, lease);
        try
        {
            File.WriteAllText(Path.Combine(root, OwnerFile), OwnerPrefix + identity);
            Directory.CreateDirectory(workspace.StageDirectory);
            return workspace;
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    /// <summary>Remove only marked, unlocked scratch directories. An active process keeps its lease open.</summary>
    public static void CleanupAbandoned(Action<Exception>? report = null)
    {
        if (!Directory.Exists(TempRoot)) return;
        PackExportContent.CheckAncestors(Path.GetTempPath(), TempRoot);
        foreach (var directory in Directory.EnumerateDirectories(TempRoot))
        {
            try
            {
                var identity = Path.GetFileName(directory);
                if (!Guid.TryParseExact(identity, "N", out _)) continue;
                ValidateOwner(directory, identity);
                using (new FileStream(Path.Combine(directory, LeaseFile), FileMode.Open,
                           FileAccess.ReadWrite, FileShare.None)) { }
                // Recheck after acquiring the lease; never follow links introduced into scratch trees.
                DeleteOwned(directory, identity);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                report?.Invoke(error);
            }
        }
    }

    private static void ValidateOwner(string root, string identity)
    {
        var expected = Path.Combine(Path.GetFullPath(TempRoot), identity);
        if (!string.Equals(Path.GetFullPath(root), expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Modification workspace is outside its owned temp root.");
        PackExportContent.CheckAncestors(TempRoot, root);
        var owner = Path.Combine(root, OwnerFile);
        PackExportContent.CheckAncestors(root, owner);
        if (!File.Exists(owner) || File.ReadAllText(owner) != OwnerPrefix + identity)
            throw new IOException("Modification workspace ownership changed; preserved.");
    }

    private static void DeleteOwned(string root, string identity)
    {
        ValidateOwner(root, identity);
        var pending = new System.Collections.Generic.Stack<string>();
        var directories = new System.Collections.Generic.List<string>();
        var files = new System.Collections.Generic.List<string>();
        pending.Push(root);
        while (pending.Count > 0)
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                PackExportContent.CheckAncestors(root, entry);
                if (Directory.Exists(entry)) { directories.Add(entry); pending.Push(entry); }
                else if (entry != Path.Combine(root, OwnerFile) && entry != Path.Combine(root, LeaseFile)) files.Add(entry);
            }
        // Keep ownership and lease markers until every payload has gone. A locked payload must
        // remain eligible for the next startup retry instead of leaving an unmarked orphan.
        foreach (var file in files)
        {
            PackExportContent.CheckAncestors(root, file);
            File.Delete(file);
        }
        for (var index = directories.Count - 1; index >= 0; index--)
        {
            PackExportContent.CheckAncestors(root, directories[index]);
            Directory.Delete(directories[index], recursive: false);
        }
        ValidateOwner(root, identity);
        File.Delete(Path.Combine(root, LeaseFile));
        File.Delete(Path.Combine(root, OwnerFile));
        Directory.Delete(root, recursive: false);
    }

    public void Dispose()
    {
        var lease = Interlocked.Exchange(ref _lease, null);
        if (lease is null) return;
        try
        {
            ValidateOwner(Root, _identity);
            lease.Dispose();
            DeleteOwned(Root, _identity);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            CleanupError = error;
        }
        finally { lease.Dispose(); }
    }
}
