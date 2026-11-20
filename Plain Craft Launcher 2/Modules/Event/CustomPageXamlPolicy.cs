using System;
using System.Linq;
using System.Xml.Linq;

namespace PCL;

/// <summary>Shared boundary for user-supplied homepage and help-page XAML.</summary>
public static class CustomPageXamlPolicy
{
    private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string Local = "clr-namespace:PCL;assembly=Plain Craft Launcher 2";

    public static string WrapFragment(string fragment) =>
        $"<StackPanel xmlns=\"{Presentation}\" xmlns:sys=\"clr-namespace:System;assembly=System.Runtime\" xmlns:x=\"{Xaml}\" xmlns:local=\"{Local}\">{fragment}</StackPanel>";

    public static void Validate(XDocument document)
    {
        if (document.Root is not { } root) return;
        foreach (var element in root.DescendantsAndSelf())
        {
            if (!IsAllowedNamespace(element.Name.NamespaceName))
                throw new UnauthorizedAccessException($"不允许使用 XAML 命名空间：{element.Name.NamespaceName}");
            foreach (var attribute in element.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
                if (!IsAllowedNamespace(attribute.Value))
                    throw new UnauthorizedAccessException($"不允许使用 XAML 命名空间：{attribute.Value}");

            // A referenced dictionary is parsed by WPF outside the checked document.
            if (element.Name == XName.Get("ResourceDictionary.Source", Presentation) ||
                element.Name == XName.Get("ResourceDictionary", Presentation) &&
                element.Attributes().Any(attribute => attribute.Name.LocalName is "Source" or "ResourceDictionary.Source"))
                throw new UnauthorizedAccessException("自定义页面不允许加载外部资源字典。");
        }
    }

    private static bool IsAllowedNamespace(string value) =>
        value is "" or Presentation or Xaml or Local or "clr-namespace:PCL" or
            "clr-namespace:System;assembly=System.Runtime" or "clr-namespace:System;assembly=mscorlib" or
            "clr-namespace:System;assembly=System.Private.CoreLib" or "http://www.w3.org/XML/1998/namespace";
}
