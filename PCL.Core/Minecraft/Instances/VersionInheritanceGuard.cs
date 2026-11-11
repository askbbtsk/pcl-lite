using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PCL.Core.Minecraft.Instances;

public sealed class VersionInheritanceException(string message) : InvalidOperationException(message);

/// <summary>Guards synchronous JSON resolution across different McInstance objects, not just one getter.</summary>
public static class VersionInheritanceGuard
{
    public const int MaximumDepth = 64;
    [ThreadStatic] private static List<string>? _active;

    public static IDisposable Enter(string instanceDirectory)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instanceDirectory));
        var chain = _active ??= [];
        if (chain.Contains(path, StringComparer.OrdinalIgnoreCase))
            throw new VersionInheritanceException("Circular version inheritance: " + string.Join(" -> ", chain.Append(path)));
        if (chain.Count >= MaximumDepth)
            throw new VersionInheritanceException($"Version inheritance exceeds {MaximumDepth} levels: {path}");
        chain.Add(path);
        return new Scope(chain, path);
    }

    private sealed class Scope(List<string> chain, string path) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Scope ownership is local to the resolving thread; never clear another caller's chain.
            if (chain.Count == 0 || !string.Equals(chain[^1], path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Version inheritance scopes must be disposed in reverse order.");
            chain.RemoveAt(chain.Count - 1);
        }
    }
}
