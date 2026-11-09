using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.PrismImport;

public sealed record PrismImportStage(string Directory, string Name, string TargetDirectory, string TargetName);

/// <summary>Publishes only a fully installed fresh instance. Source files are always read-only inputs.</summary>
public static class PrismImportTransaction
{
    public static async Task ExecuteAsync(PrismReadResult source, string targetDirectory,
        Func<PrismImportStage, CancellationToken, Task> prepare,
        Func<PrismImportStage, CancellationToken, Task> installAndValidate,
        CancellationToken cancellationToken = default)
    {
        if (!source.CanImport) throw new InvalidDataException(string.Join(Environment.NewLine, source.BlockingIssues));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        var name = Path.GetFileName(target);
        if (!InstanceMigration.InstanceMigrationPlanner.ValidFileName(name) || name.StartsWith(".pcl-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid target instance name.");
        if (target.StartsWith(source.InstanceDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || source.InstanceDirectory.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(target, source.InstanceDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Prism import requires an independent target.");
        PrismInstanceReader.RejectLinks(target);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Target already exists.");
        var parent = Path.GetDirectoryName(target) ?? throw new InvalidDataException("Target parent is missing.");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        var stageName = ".pcl-prism-import-" + Guid.NewGuid().ToString("N");
        var stage = new PrismImportStage(Path.Combine(parent, stageName), stageName, target, name);
        var marker = Path.Combine(stage.Directory, ".pcl-prism-owner"); var owner = Guid.NewGuid().ToString("N");
        var committed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PrismInstanceReader.InputsUnchanged(source, cancellationToken)) throw new IOException("Prism metadata changed after preview.");
            System.IO.Directory.CreateDirectory(stage.Directory); File.WriteAllText(marker, owner);
            File.WriteAllText(Path.Combine(stage.Directory, ".pclignore"), "Prism import stage");
            await prepare(stage, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await installAndValidate(stage, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!PrismInstanceReader.InputsUnchanged(source, cancellationToken)) throw new IOException("Prism metadata changed before commit.");
            PrismInstanceReader.RejectLinks(stage.Directory);
            if (File.ReadAllText(marker) != owner) throw new IOException("Prism staging ownership changed.");
            var oldJson = Path.Combine(stage.Directory, stage.Name + ".json");
            var json = JsonNode.Parse(File.ReadAllText(oldJson)) as JsonObject ?? throw new InvalidDataException("Installer did not produce target JSON.");
            json["id"] = name;
            if (json["jar"]?.ToString() == stage.Name) json["jar"] = name;
            File.WriteAllText(Path.Combine(stage.Directory, name + ".json"), json.ToJsonString()); File.Delete(oldJson);
            var oldJar = Path.Combine(stage.Directory, stage.Name + ".jar");
            if (File.Exists(oldJar)) File.Move(oldJar, Path.Combine(stage.Directory, name + ".jar"), overwrite: false);
            PrismInstanceReader.RejectLinks(target); cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Target was created during import and was preserved.");
            File.Delete(Path.Combine(stage.Directory, ".pclignore"));
            Directory.Move(stage.Directory, target); committed = true;
            try { File.Delete(Path.Combine(target, ".pcl-prism-owner")); } catch (IOException) { }
        }
        finally
        {
            if (!committed && Directory.Exists(stage.Directory))
            {
                try
                {
                    PrismInstanceReader.RejectLinks(stage.Directory); PrismInstanceReader.RejectLinks(marker);
                    if (File.ReadAllText(marker) == owner) { RejectTreeLinks(stage.Directory); Directory.Delete(stage.Directory, recursive: true); }
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                { /* Ambiguous ownership or links: retain the hidden stage instead of deleting user content. */ }
            }
        }
    }

    private static void RejectTreeLinks(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        { PrismInstanceReader.RejectLinks(entry); if (Directory.Exists(entry)) RejectTreeLinks(entry); }
    }
}
