using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace PCL.Core.UI.Theme.Packages;

/// <summary>Materializes only already-validated snapshot bytes, without a second read of user XAML.</summary>
public sealed class ThemeResourceLoader
{
    public ResourceDictionary Load(ThemePackage package, bool dark, bool glass, string? fontDirectory = null)
    {
        ThemeXamlPolicy.ValidateDependencyGraph(package.Files);
        var combined = new ResourceDictionary();
        long decodedPixels = 0;
        int expandedNodes = 0;
        foreach (string? entry in new[]
        {
            package.Manifest.Entry, dark ? package.Manifest.Dark : package.Manifest.Light,
            glass && package.Manifest.SupportsGlass ? package.Manifest.Glass : null
        })
        {
            if (entry is null) continue;
            var document = Expand(package, ThemeFilePaths.Normalize(entry),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), ref expandedNodes);
            // External images are decoded into a frozen memory object, not held as
            // a file URI; updating/deleting installed themes does not lock the files.
            var assets = new Dictionary<string, ImageSource>();
            foreach (var element in document.Descendants().ToArray())
            foreach (var attribute in element.Attributes().Where(attribute =>
                         attribute.Name.LocalName is "Source" or "ImageSource").ToArray())
            {
                if (attribute.Value.TrimStart().StartsWith('{')) continue;
                string relative = attribute.Value;
                string assetKey = "Pcl.Theme.Asset." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative)));
                if (!assets.ContainsKey(assetKey))
                {
                    if (!package.Files.TryGetValue(relative, out byte[]? bytes))
                        throw new ThemePackageException("Missing image resource.", relative);
                    using var stream = new MemoryStream(bytes, false);
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth > 8192 || decoder.Frames[0].PixelHeight > 8192
                        || (decodedPixels += (long)decoder.Frames[0].PixelWidth * decoder.Frames[0].PixelHeight) > 32 * 1024 * 1024)
                        throw new ThemePackageException("Theme images exceed the dimension/pixel budget.", relative);
                    int decodeWidth = Math.Min(decoder.Frames[0].PixelWidth, 4096);
                    stream.Position = 0;
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = decodeWidth;
                    image.StreamSource = stream;
                    image.EndInit();
                    if (image.PixelWidth > 8192 || image.PixelHeight > 8192)
                        throw new ThemePackageException("Image dimensions exceed 8192 pixels.", relative);
                    image.Freeze();
                    assets[assetKey] = image;
                }
                attribute.Value = "{DynamicResource " + assetKey + "}";
            }
            foreach (var attribute in document.Descendants().Attributes().Where(attribute =>
                         attribute.Name.LocalName == "FontFamily" && attribute.Value.Contains('#')))
            {
                if (fontDirectory is null) throw new InvalidDataException("Local theme fonts require an isolated font cache.");
                int separator = attribute.Value.IndexOf('#');
                string font = attribute.Value[..separator];
                string destination = ThemeFilePaths.Resolve(fontDirectory, font);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!File.Exists(destination)) File.WriteAllBytes(destination, package.Files[font]);
                else if (!SHA256.HashData(File.ReadAllBytes(destination)).AsSpan().SequenceEqual(SHA256.HashData(package.Files[font])))
                    throw new InvalidDataException("Theme font cache content differs from the validated snapshot.");
                attribute.Value = new Uri(destination).AbsoluteUri + attribute.Value[separator..];
            }
            // Internal paths have been replaced from snapshot bytes. No input XAML
            // is re-opened by the parser, including merged dictionaries.
            string markup = document.ToString(SaveOptions.DisableFormatting);
            var parser = new ParserContext();
            using var safeInput = new MemoryStream(Encoding.UTF8.GetBytes(markup), false);
            var loaded = (ResourceDictionary)XamlReader.Load(safeInput, parser);
            foreach (var asset in assets) loaded[asset.Key] = asset.Value;
            combined.MergedDictionaries.Add(loaded);
        }
        ValidateResources(combined);
        return combined;
    }

    private static XDocument Expand(ThemePackage package, string path, HashSet<string> stack, ref int expandedNodes)
    {
        if (!stack.Add(path)) throw new ThemePackageException("Cyclic resource dictionary.", path);
        var document = ThemeXamlPolicy.ReadDocument(package.Files[path], path);
        expandedNodes += document.Descendants().Count();
        if (expandedNodes > 48000)
            throw new ThemePackageException("Expanded theme dictionaries exceed 48000 nodes.", path);
        foreach (var attribute in document.Descendants().Attributes()
                     .Where(attribute => !attribute.IsNamespaceDeclaration
                         && attribute.Name.LocalName is "Source" or "ImageSource"
                         && attribute.Parent?.Name != ThemeXamlPolicy.Presentation + "ResourceDictionary").ToArray())
        {
            if (!attribute.Value.TrimStart().StartsWith('{'))
                attribute.Value = ThemeFilePaths.Dependency(path, attribute.Value);
        }
        foreach (var attribute in document.Descendants().Attributes().Where(attribute =>
                     attribute.Name.LocalName == "FontFamily" && attribute.Value.Contains('#')))
        {
            int separator = attribute.Value.IndexOf('#');
            attribute.Value = ThemeFilePaths.Dependency(path, attribute.Value[..separator]) + attribute.Value[separator..];
        }
        // Package-level XAML always names the actual launcher assembly, never an
        // arbitrary caller's assembly through a namespace-resolution side effect.
        foreach (var declaration in document.Descendants().Attributes().Where(attribute =>
                     attribute.IsNamespaceDeclaration && attribute.Value == ThemeXamlPolicy.LauncherNamespace))
            declaration.Value = "clr-namespace:PCL;assembly=Plain Craft Launcher 2";
        foreach (var element in document.Descendants().Where(element =>
                     element.Name.NamespaceName == ThemeXamlPolicy.LauncherNamespace))
            element.Name = XName.Get(element.Name.LocalName, "clr-namespace:PCL;assembly=Plain Craft Launcher 2");
        foreach (var source in document.Descendants(ThemeXamlPolicy.Presentation + "ResourceDictionary")
                     .Where(element => element.Attribute("Source") is not null).ToArray())
        {
            string dependency = ThemeFilePaths.Dependency(path, source.Attribute("Source")!.Value);
            source.ReplaceWith(new XElement(Expand(package, dependency, stack, ref expandedNodes).Root!));
        }
        stack.Remove(path);
        return document;
    }

    private static void ValidateResources(ResourceDictionary dictionary)
    {
        foreach (var child in dictionary.MergedDictionaries) ValidateResources(child);
        foreach (object key in dictionary.Keys)
        {
            object value = dictionary[key]; // Force deferred resource creation before installation.
            if (key is string resourceKey && resourceKey.StartsWith("ColorBrush", StringComparison.Ordinal)
                && value is not Brush)
                throw new InvalidDataException($"{resourceKey} must be a Brush.");
            if (key is string colorKey && colorKey.StartsWith("ColorObject", StringComparison.Ordinal)
                && value is not Color)
                throw new InvalidDataException($"{colorKey} must be a Color.");
            if (key?.ToString() == "LaunchFontFamily" && value is not FontFamily)
                throw new InvalidDataException("LaunchFontFamily must be a FontFamily.");
            if (key is string parameter && parameter.StartsWith("Pcl.Theme.", StringComparison.Ordinal))
            {
                if (parameter.EndsWith(".Padding", StringComparison.Ordinal) && value is not Thickness
                    || parameter.EndsWith(".CornerRadius", StringComparison.Ordinal) && value is not CornerRadius
                    || parameter.EndsWith(".FontSize", StringComparison.Ordinal)
                        && (value is not double fontSize || !double.IsFinite(fontSize) || fontSize < 8 || fontSize > 48))
                    throw new InvalidDataException($"Invalid visual resource value: {parameter}");
            }
            if (value is Style style)
            {
                foreach (SetterBase setter in style.Setters)
                    if (setter is EventSetter) throw new InvalidDataException("Theme event handlers are not supported.");
                style.Seal();
            }
            if (value is ControlTemplate template)
            {
                var root = template.LoadContent();
                if (root is not FrameworkElement) throw new InvalidDataException($"Invalid visual template: {key}");
            }
            if (key is string name && name.StartsWith("Pcl.Theme.Template.", StringComparison.Ordinal)
                && value is not ControlTemplate)
                throw new InvalidDataException($"Expected ControlTemplate: {name}");
        }
    }
}
