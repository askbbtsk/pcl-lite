using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Minecraft.Packs;

namespace PCL.Core.Minecraft.InstanceMigration;

public sealed record MigrationStage(string StageDirectory, string StageName, string TargetDirectory,
    string TargetName, string SourceDirectory);

public sealed record MigrationOutcome(bool Success, bool Cancelled, string? TargetDirectory,
    IReadOnlyList<string> Problems, string? RetainedStageDirectory = null);

/// <summary>Owns a fresh staging instance; delegates actual installation, downloads and dependency/Java checks to the host.</summary>
public static class InstanceMigrationTransaction
{
    public static async Task<MigrationOutcome> ExecuteAsync(string sourceInstanceDirectory, string targetInstanceDirectory,
        InstanceMigrationPlan plan, Func<MigrationStage, CancellationToken, Task> install,
        Func<MigrationStage, MigrationModPlan, CancellationToken, Task> materialize,
        Func<MigrationStage, CancellationToken, Task<IReadOnlyList<string>>> validate,
        Func<MigrationStage, CancellationToken, Task>? copyOptionalContent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(materialize);
        ArgumentNullException.ThrowIfNull(validate);
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceInstanceDirectory));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetInstanceDirectory));
        var parent = Path.GetDirectoryName(target) ?? throw new InvalidDataException("Target has no parent.");
        var name = Path.GetFileName(target);
        if (!InstanceMigrationPlanner.ValidFileName(name) || name.StartsWith(".pcl-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid target instance name.");
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Migration requires an independent target instance.");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        RejectLinks(source); RejectLinks(target);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("The target already exists.");
        var stageName = ".pcl-migration-" + Guid.NewGuid().ToString("N");
        var stageDirectory = Path.Combine(parent, stageName);
        var stage = new MigrationStage(stageDirectory, stageName, target, name, source);
        var ownership = Guid.NewGuid().ToString("N");
        var marker = Path.Combine(stageDirectory, ".pcl-migration-owner");
        var problems = plan.Mods.Where(item => item.Action == MigrationModAction.NeedsReview)
            .Select(item => Path.GetFileName(item.Source.Path) + ": " + item.Reason).ToList();
        var committed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(stageDirectory);
            File.WriteAllText(marker, ownership);
            File.WriteAllText(Path.Combine(stageDirectory, ".pclignore"), "migration staging instance");
            await install(stage, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(stageDirectory, ".pclignore"), "migration staging instance");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in plan.Mods.Where(item => item.Action != MigrationModAction.NeedsReview))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.TargetFileName is null || !names.Add(item.TargetFileName))
                    throw new InvalidDataException("More than one selected mod would occupy the same target name.");
                RejectLinks(item.Source.Path);
                using (var sourceFile = new FileStream(item.Source.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    if (!string.Equals(Convert.ToHexString(SHA256.HashData(sourceFile)), item.Source.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Source mod changed after preview: " + item.Source.Path);
                await materialize(stage, item, cancellationToken).ConfigureAwait(false);
            }
            if (copyOptionalContent is not null) await copyOptionalContent(stage, cancellationToken).ConfigureAwait(false);
            var validation = await validate(stage, cancellationToken).ConfigureAwait(false);
            if (validation.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, validation));
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in plan.Mods.Where(item => item.Action != MigrationModAction.NeedsReview))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectLinks(item.Source.Path);
                using var check = new FileStream(item.Source.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(check)), item.Source.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Source mod changed before commit: " + item.Source.Path);
            }
            EnsureOwnership(stageDirectory, marker, ownership);
            var oldJson = Path.Combine(stageDirectory, stageName + ".json");
            var json = JsonNode.Parse(File.ReadAllText(oldJson)) as JsonObject ?? throw new InvalidDataException("Installer did not produce a version object.");
            json["id"] = name;
            if (json["jar"]?.ToString() == stageName) json["jar"] = name;
            File.WriteAllText(Path.Combine(stageDirectory, name + ".json"), json.ToJsonString());
            File.Delete(oldJson);
            var oldJar = Path.Combine(stageDirectory, stageName + ".jar");
            if (File.Exists(oldJar)) File.Move(oldJar, Path.Combine(stageDirectory, name + ".jar"), overwrite: false);
            // Run the final collision check after all potentially long operations.
            RejectLinks(target);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(target) || File.Exists(target)) throw new IOException("The target was created during migration; it was not replaced.");
            File.Delete(Path.Combine(stageDirectory, ".pclignore"));
            Directory.Move(stageDirectory, target);
            committed = true;
            try { File.Delete(Path.Combine(target, ".pcl-migration-owner")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { problems.Add("The committed target retains a harmless migration ownership marker: " + ex.Message); }
            return new(true, false, target, problems);
        }
        catch (OperationCanceledException) { return new(false, true, null, problems); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            problems.Add(ex.Message);
            return new(false, false, null, problems, Directory.Exists(stageDirectory) ? stageDirectory : null);
        }
        finally
        {
            // Cancellation/failure never removes the original or the eventual destination.
            // Staging is retained for diagnostics unless its ownership and all ancestors remain intact.
            if (!committed && Directory.Exists(stageDirectory))
            {
                try
                {
                    EnsureOwnership(stageDirectory, marker, ownership);
                    RejectTreeLinks(stageDirectory);
                    Directory.Delete(stageDirectory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException)
                { /* An ambiguous/replaced stage is preserved instead of deleting user content. */ }
            }
        }
    }

    private static void EnsureOwnership(string stage, string marker, string identity)
    {
        RejectLinks(stage); RejectLinks(marker);
        if (!File.Exists(marker) || File.ReadAllText(marker) != identity)
            throw new IOException("Staging ownership changed; its content was preserved.");
    }

    private static void RejectTreeLinks(string directory)
    {
        foreach (var child in Directory.EnumerateFileSystemEntries(directory))
        { RejectLinks(child); if (Directory.Exists(child)) RejectTreeLinks(child); }
    }

    internal static void RejectLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Migration cannot follow links: " + current); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            var parent = Path.GetDirectoryName(current);
            if (parent is null) return;
            current = parent;
        }
    }
}
