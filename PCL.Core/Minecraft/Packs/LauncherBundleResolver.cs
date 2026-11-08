using System.IO;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PCL.Core.Minecraft.Packs;

public sealed record LauncherBundleCandidate(string Path, string ProductName);

/// <summary>Identifies and copies the running PCL Lite executable for launcher-included exports.</summary>
public static class LauncherBundleResolver
{
    public const string LiteProductName = "PCL Lite";
    public const string LegacyPclProductName = "Plain Craft Launcher";
    public const string BundledFileName = "PCL Lite.exe";

    public static string CopyCurrentExecutable(string executablePath, string destinationDirectory,
        Func<string, string?>? productNameReader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var source = Path.GetFullPath(executablePath);
        if (!File.Exists(source)) throw new FileNotFoundException("The running PCL Lite executable is unavailable.", source);
        var readProductName = productNameReader ?? (path => FileVersionInfo.GetVersionInfo(path).ProductName);
        var productName = readProductName(source);
        if (!string.Equals(productName?.Trim(), LiteProductName, StringComparison.Ordinal))
            throw new InvalidDataException($"Refusing to bundle executable '{source}' because its product identity is '{productName ?? "(empty)"}', not '{LiteProductName}'.");

        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);
        var destination = Path.Combine(destinationRoot, BundledFileName);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return source;
        File.Copy(source, destination, overwrite: true);
        return destination;
    }

    public static string? SelectBundledLauncher(IEnumerable<LauncherBundleCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var materialized = candidates.Where(static item => !string.IsNullOrWhiteSpace(item.Path)).ToArray();
        return materialized.FirstOrDefault(static item => string.Equals(item.ProductName?.Trim(), LiteProductName, StringComparison.OrdinalIgnoreCase))?.Path
            ?? materialized.FirstOrDefault(static item => string.Equals(item.ProductName?.Trim(), LegacyPclProductName, StringComparison.OrdinalIgnoreCase))?.Path
            ?? materialized.FirstOrDefault(static item =>
                (item.ProductName.Contains("Launcher", StringComparison.OrdinalIgnoreCase)
                 || item.ProductName.Contains("启动", StringComparison.OrdinalIgnoreCase))
                && !string.Equals(item.ProductName.Trim(), "Plain Craft Launcher Admin Manager", StringComparison.OrdinalIgnoreCase))?.Path;
    }
}


