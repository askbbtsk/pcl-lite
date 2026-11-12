using System.IO;
using System.IO.Compression;
using PCL.Core.App.Localization;
using PCL.Core.UI.Theme.Packages;

namespace PCL;

public partial class FormMain
{
    private bool TryInstallDroppedTheme(IReadOnlyList<string> files)
    {
        if (files.Count != 1 || CustomThemeRuntime.Current is not { } runtime) return false;
        string source = files[0];
        bool theme;
        try
        {
            theme = Directory.Exists(source) ? File.Exists(Path.Combine(source, "theme.json"))
                : source.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ? ThemePackageService.IsThemeXaml(source)
                : source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && IsThemeZip(source);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ModBase.RunInUi(() => ModMain.MyMsgBox(Lang.Text("Theme.Pack.Failed", error.Message), Lang.Text("Theme.Pack.Title")));
            return true;
        }
        if (!theme) return false;
        ModBase.RunInUi(async () => await runtime.InstallAsync(source));
        return true;
    }

    private static bool IsThemeZip(string source)
    {
        using var archive = ZipFile.OpenRead(source);
        return archive.Entries.Any(entry => entry.FullName.Equals("theme.json", StringComparison.OrdinalIgnoreCase));
    }
}
