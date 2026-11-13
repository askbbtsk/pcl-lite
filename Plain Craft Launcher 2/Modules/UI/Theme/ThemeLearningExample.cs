using System.IO;
using PCL.Core.UI.Theme.Packages;

namespace PCL;

/// <summary>Export the exact documented sources; never overwrite a user's edits.</summary>
internal static class ThemeLearningExample
{
    internal static string Export(string dataDirectory)
    {
        string root = Path.Combine(dataDirectory, "ThemeExamples");
        Directory.CreateDirectory(root);
        string directory = ThemeFilePaths.Resolve(root, "Starter");
        Directory.CreateDirectory(directory);
        foreach (string name in new[] { "StarterTheme.xaml", "README.md" })
        {
            string target = ThemeFilePaths.Resolve(directory, name);
            if (File.Exists(target)) continue;
            using var input = typeof(ThemeLearningExample).Assembly.GetManifestResourceStream("PCL.ThemeLearning." + name)
                ?? throw new InvalidDataException("Missing embedded theme learning resource: " + name);
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                    output.Flush(true);
                }
                try { File.Move(temporary, target, overwrite: false); }
                catch (IOException) when (File.Exists(target))
                {
                    // A second click/process may have created the user's copy first.
                    // Do not overwrite it, even when its content differs.
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return directory;
    }
}
