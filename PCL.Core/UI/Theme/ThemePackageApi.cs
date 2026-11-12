using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.UI.Theme.Packages;

namespace PCL.Core.UI.Theme;

public interface IThemePackageHost
{
    string Directory { get; }
    ThemeManifest? ActiveTheme { get; }
    bool IsPreviewing { get; }
    event EventHandler? Changed;
    Task<IReadOnlyList<ThemeCatalogItem>> GetInstalledAsync(CancellationToken token = default);
    Task<bool> InstallAsync(string source, CancellationToken token = default);
    Task<bool> PreviewAsync(string id, CancellationToken token = default);
    void RestoreBuiltIn();
}

public sealed partial class ThemeService
{
    /// <summary>Versioned visual-package host. Null before the main window is ready.</summary>
    public static IThemePackageHost? Packages { get; private set; }

    /// <summary>The application registers its UI host once; package code cannot replace it.</summary>
    public static void RegisterPackageHost(IThemePackageHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (Packages is not null && !ReferenceEquals(Packages, host))
            throw new InvalidOperationException("A theme host is already registered.");
        Packages = host;
    }
}
