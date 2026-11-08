using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.Core.Minecraft.Instances;

/// <summary>Creates an isolated, runnable copy of a version folder for testing changes without touching the source instance.</summary>
public static class InstanceTestCopyService
{
    public static string Create(string sourceInstanceDirectory, string destinationInstanceDirectory, string sourceName, string destinationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstanceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationInstanceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationName);
        if (!string.Equals(sourceName, Path.GetFileName(sourceName), StringComparison.Ordinal)
            || !string.Equals(destinationName, Path.GetFileName(destinationName), StringComparison.Ordinal)
            || sourceName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || destinationName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Instance names must be a single valid path segment.");

        var source = Path.GetFullPath(sourceInstanceDirectory);
        var destination = Path.GetFullPath(destinationInstanceDirectory);
        if (!string.Equals(Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), sourceName,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), destinationName,
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Instance names must match their directory names.");
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        if (string.Equals(source.TrimEnd(Path.DirectorySeparatorChar), destination.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A test copy must use a different instance directory.");
        var sourceParent = Path.GetDirectoryName(source.TrimEnd(Path.DirectorySeparatorChar))
            ?? throw new InvalidDataException("Source instance has no parent directory.");
        var destinationParent = Path.GetDirectoryName(destination.TrimEnd(Path.DirectorySeparatorChar))
            ?? throw new InvalidDataException("Destination instance has no parent directory.");
        if (!string.Equals(sourceParent, destinationParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A test copy must be created beside the source instance.");
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("The destination instance already exists.");
        _RejectReparsePoint(source);

        var sourceJson = Path.Combine(source, sourceName + ".json");
        if (!File.Exists(sourceJson)) throw new FileNotFoundException("The source instance JSON was not found.", sourceJson);
        var json = JsonNode.Parse(File.ReadAllText(sourceJson)) as JsonObject
            ?? throw new InvalidDataException("The source instance JSON must be an object.");
        json["id"] = destinationName;

        var stageParent = Path.GetDirectoryName(destinationParent) ?? destinationParent;
        var stage = Path.Combine(stageParent, ".pcl-test-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var pending = new Stack<string>();
            pending.Push(source);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    _RejectReparsePoint(directory);
                    var relative = Path.GetRelativePath(source, directory);
                    if (string.Equals(relative, "PCLLiteData", StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(Path.Combine(stage, relative));
                    pending.Push(directory);
                }

                foreach (var file in Directory.EnumerateFiles(current))
                {
                    _RejectReparsePoint(file);
                    var relative = Path.GetRelativePath(source, file);
                    if (string.Equals(relative, sourceName + ".json", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(relative, sourceName + ".jar", StringComparison.OrdinalIgnoreCase)) continue;
                    var output = Path.Combine(stage, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    File.Copy(file, output, overwrite: false);
                }
            }

            File.WriteAllText(Path.Combine(stage, destinationName + ".json"),
                json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var sourceJar = Path.Combine(source, sourceName + ".jar");
            if (File.Exists(sourceJar)) File.Copy(sourceJar, Path.Combine(stage, destinationName + ".jar"), overwrite: false);
            var setupPath = Path.Combine(stage, "PCL", "Setup.ini");
            if (File.Exists(setupPath))
            {
                var oldPath = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var newPath = destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var setup = File.ReadAllText(setupPath).Replace(oldPath, newPath, StringComparison.OrdinalIgnoreCase);
                File.WriteAllText(setupPath, setup);
            }
            Directory.Move(stage, destination);
            return destination;
        }
        catch
        {
            if (Directory.Exists(stage))
            {
                try { Directory.Delete(stage, recursive: true); }
                catch { /* Keep the original exception; caller can remove the clearly named staging directory. */ }
            }
            throw;
        }
    }

    private static void _RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Test copy refuses to follow a reparse point: '{path}'.");
    }
}
