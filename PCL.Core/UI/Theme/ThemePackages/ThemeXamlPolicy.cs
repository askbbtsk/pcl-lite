using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PCL.Core.UI.Theme.Packages;

/// <summary>
/// Positive file-language contract checked before WPF instantiates anything.
/// Themes are visual descriptions, not another route to plugins or homepage actions.
/// </summary>
public static class ThemeXamlPolicy
{
    public static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    public static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    public const string ThemeNamespace = "clr-namespace:PCL.Core.UI.Theme;assembly=PCL.Core";
    public const string LauncherNamespace = "clr-namespace:PCL";
    private static readonly HashSet<string> VisualTypes = new(StringComparer.Ordinal)
    {
        "ResourceDictionary", "Style", "Setter", "ControlTemplate", "DataTemplate",
        "Trigger", "MultiTrigger", "DataTrigger", "MultiDataTrigger", "Condition",
        "EventTrigger", "BeginStoryboard", "StopStoryboard", "RemoveStoryboard", "Storyboard",
        "DoubleAnimation", "ColorAnimation", "ThicknessAnimation", "PointAnimation",
        "Grid", "RowDefinition", "ColumnDefinition", "Border", "StackPanel", "DockPanel",
        "WrapPanel", "Canvas", "Viewbox", "TextBlock", "Run", "Span", "Bold", "Italic", "LineBreak",
        "ContentPresenter", "ItemsPresenter", "ScrollContentPresenter", "ScrollViewer", "ScrollBar", "TextBox",
        "Track", "Thumb", "RepeatButton", "ToggleButton", "Popup",
        "Rectangle", "Ellipse", "Path", "Line", "Polygon", "Polyline",
        "Image", "ImageBrush", "DrawingImage", "GeometryDrawing", "DrawingGroup",
        "DrawingBrush", "SolidColorBrush", "LinearGradientBrush", "RadialGradientBrush", "GradientStop",
        "Color", "Thickness", "CornerRadius", "FontFamily", "FontWeight", "FontStyle",
        "GeometryGroup", "PathGeometry", "StreamGeometry", "RectangleGeometry", "EllipseGeometry",
        "PathFigure", "LineSegment", "PolyLineSegment", "BezierSegment", "PolyBezierSegment", "ArcSegment",
        "TransformGroup", "TranslateTransform", "ScaleTransform", "RotateTransform", "SkewTransform", "MatrixTransform",
        "DropShadowEffect", "BlurEffect", "CubicEase", "QuadraticEase", "SineEase", "CircleEase", "BackEase"
    };
    private static readonly HashSet<string> Members = new(StringComparer.Ordinal)
    {
        "Resources", "MergedDictionaries", "Source", "Key", "Name", "TargetType", "BasedOn", "Setters",
        "Template", "Triggers", "Conditions", "EnterActions", "ExitActions", "Property", "Value", "Binding",
        "TargetName", "EventName", "RoutedEvent", "Storyboard", "Actions", "TargetProperty", "BeginTime", "Duration",
        "From", "To", "By", "AutoReverse", "RepeatBehavior", "FillBehavior", "EasingFunction", "EasingMode",
        "Content", "ContentTemplate", "ContentSource", "RecognizesAccessKey", "IsMouseOver", "IsKeyboardFocusWithin", "IsFocused",
        "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight", "Margin", "Padding",
        "HorizontalAlignment", "VerticalAlignment", "HorizontalContentAlignment", "VerticalContentAlignment",
        "Orientation", "Background", "Foreground", "BorderBrush", "BorderThickness", "CornerRadius",
        "Opacity", "Visibility", "IsHitTestVisible", "Clip", "ClipToBounds", "SnapsToDevicePixels", "UseLayoutRounding",
        "RenderTransform", "RenderTransformOrigin", "LayoutTransform", "Effect",
        "FontFamily", "FontSize", "FontWeight", "FontStyle", "FontStretch", "Text", "TextWrapping",
        "TextTrimming", "TextAlignment", "LineHeight", "LineStackingStrategy", "Inlines", "TextDecorations",
        "ColumnDefinitions", "RowDefinitions", "Column", "Row", "ColumnSpan", "RowSpan",
        "Dock", "LastChildFill", "Left", "Top", "Right", "Bottom", "ZIndex",
        "Stretch", "StretchDirection", "Data", "Fill", "Stroke", "StrokeThickness", "StrokeLineJoin",
        "StrokeStartLineCap", "StrokeEndLineCap", "Points", "X1", "X2", "Y1", "Y2",
        "Color", "GradientStops", "StartPoint", "EndPoint", "Offset", "Center", "GradientOrigin",
        "RadiusX", "RadiusY", "MappingMode", "SpreadMethod", "RelativeTransform", "Transform",
        "Geometry", "Children", "Drawings", "Brush", "Pen", "Figures", "Segments", "IsClosed", "IsFilled",
        "Point", "Point1", "Point2", "Point3", "Size", "RotationAngle", "IsLargeArc", "SweepDirection",
        "ScaleX", "ScaleY", "CenterX", "CenterY", "Angle", "AngleX", "AngleY", "X", "Y", "Matrix",
        "BlurRadius", "ShadowDepth", "Direction", "Radius", "RenderingBias", "KernelType",
        "Viewport", "Viewbox", "ViewportUnits", "ViewboxUnits", "TileMode", "AlignmentX", "AlignmentY",
        "ImageSource",
        "HorizontalScrollBarVisibility", "VerticalScrollBarVisibility", "CanContentScroll",
        "IsDirectionReversed", "Minimum", "Maximum", "ViewportSize", "SmallChange", "LargeChange",
        "DecreaseRepeatButton", "IncreaseRepeatButton", "Thumb", "Placement", "PlacementTarget",
        "IsOpen", "StaysOpen", "AllowsTransparency", "PopupAnimation", "IsChecked", "IsEnabled",
        "Focusable", "IsTabStop", "TabIndex", "FocusVisualStyle",
        "CaretBrush", "SelectionBrush", "SelectionOpacity", "IsReadOnly", "IsEditable",
        "Title", "Description", "IconData", "IsSelected", "IsExpanded", "IsInvalid",
        "IsPressed", "IsHighlighted", "IsDanger", "IsGlass", "Role", "Progress",
        "IsMouseOver", "IsKeyboardFocusWithin", "IsFocused", "IsDropDownOpen", "HasItems", "HasFocus", "IsHovered", "BeginStoryboardName"
    };
    private static readonly HashSet<string> AttachedOwners = new(StringComparer.Ordinal)
    { "Grid", "Canvas", "DockPanel", "Panel", "Storyboard", "TextElement", "ScrollViewer", "KeyboardNavigation" };
    private static readonly HashSet<string> HostTypes = new(StringComparer.Ordinal)
    { "MyTextBox", "MyComboBox", "MyComboBoxItem", "MyScrollBar", "MyScrollViewer", "MyMenuItem", "MyTextButton" };
    private static readonly HashSet<string> BindablePaths = new(StringComparer.Ordinal)
    {
        "Content", "ContentTemplate", "Title", "Description", "IconData", "Role", "Progress",
        "IsEnabled", "IsMouseOver", "IsKeyboardFocusWithin", "IsFocused", "IsPressed", "IsSelected",
        "IsChecked", "IsExpanded", "IsInvalid", "IsHighlighted", "IsDanger", "IsGlass", "HasFocus", "IsHovered",
        "Background", "Foreground", "BorderBrush", "BorderThickness", "CornerRadius", "Padding", "Margin",
        "FontFamily", "FontSize", "FontWeight", "FontStyle", "HorizontalContentAlignment", "VerticalContentAlignment",
        "ActualWidth", "ActualHeight", "Width", "Height", "Opacity", "Visibility", "Text", "HasItems",
        "IsDropDownOpen", "SelectionBoxItem", "SelectionBoxItemTemplate", "SelectionBoxItemStringFormat",
        "IsEditable", "IsReadOnly", "Orientation", "Minimum", "Maximum", "Value", "ViewportSize",
        "CanContentScroll", "ComputedHorizontalScrollBarVisibility", "ComputedVerticalScrollBarVisibility",
        "ScrollableWidth", "ScrollableHeight", "HorizontalOffset", "VerticalOffset"
    };

    public static XDocument ReadDocument(byte[] bytes, string file)
    {
        if (bytes.LongLength > ThemeApi.MaximumFileBytes) throw new ThemePackageException("XAML exceeds 8 MiB.", file);
        try
        {
            using var stream = new MemoryStream(bytes, false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = ThemeApi.MaximumFileBytes,
                MaxCharactersFromEntities = 0
            });
            return XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException error)
        { throw new ThemePackageException(error.Message, file, error.LineNumber, error.LinePosition, error); }
    }

    public static IReadOnlyList<string> Validate(byte[] bytes, string file)
    {
        var document = ReadDocument(bytes, file);
        var root = document.Root ?? throw new ThemePackageException("Missing resource dictionary.", file);
        if (root.Name != Presentation + "ResourceDictionary") Fail(root, file, "Expected ResourceDictionary.");
        var dependencies = new List<string>();
        int nodes = 0;
        foreach (var element in root.DescendantsAndSelf())
        {
            if (++nodes > 16000 || element.Ancestors().Take(65).Count() > 64)
                Fail(element, file, "Theme exceeds the visual complexity limit.");
            ValidateElement(element, file);
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    if (!IsNamespace(attribute.Value)) Fail(attribute, file, "External XAML namespace is not allowed.");
                    continue;
                }
                if (attribute.Name.Namespace == Xaml)
                {
                    if (attribute.Name.LocalName is not ("Key" or "Name"))
                        Fail(attribute, file, "Only x:Key and x:Name directives are supported.");
                    ValidateMarkup(attribute.Value.Trim(), element, attribute, file);
                    continue;
                }
                if (attribute.Name.Namespace != XNamespace.None)
                    Fail(attribute, file, "Namespaced/attached custom members are not allowed.");
                string member = attribute.Name.LocalName;
                ValidateMember(member, element, file);
                string value = attribute.Value.Trim();
                ValidateMarkup(value, element, attribute, file);
                if (element.Name.LocalName == "Setter" && member == "Value"
                    && element.Attribute("Property")?.Value is { } setterProperty)
                {
                    if (new[] { "Source", "ImageSource", "FontFamily", "Resources", "Template", "Content", "ContentTemplate" }
                        .Contains(setterProperty) && !value.StartsWith('{'))
                        Fail(attribute, file, "URI/object setters must use a validated local resource.");
                    if (setterProperty is "IsEnabled" or "IsReadOnly" or "IsHitTestVisible" or "Focusable" or "IsTabStop")
                        Fail(attribute, file, "Theme styles cannot change host interaction state.");
                }
                if (element.Name.LocalName == "Setter" && member == "Property"
                    && element.Attribute("TargetName") is null
                    && value is "Visibility" or "Opacity" or "Height" or "Width" or "MaxHeight" or "MaxWidth")
                    Fail(attribute, file, "Apply layout/visibility changes to a named visual, not the host control.");
                if (member is "Source" or "ImageSource")
                {
                    if (value.StartsWith('{'))
                    {
                        if (element.Name.LocalName == "ResourceDictionary")
                            Fail(attribute, file, "Dictionary sources must be literal local files.");
                        if (!value.StartsWith("{StaticResource ", StringComparison.Ordinal)
                            && !value.StartsWith("{DynamicResource ", StringComparison.Ordinal))
                            Fail(attribute, file, "Images may reference only typed local image resources.");
                    }
                    else dependencies.Add(ThemeFilePaths.Dependency(file, value));
                }
                if (member == "FontFamily" && (value.Contains('/') || value.Contains('\\') || value.Contains('#') || value.Contains(':'))
                    && !value.StartsWith('{'))
                {
                    int separator = value.IndexOf('#');
                    if (separator <= 0) Fail(attribute, file, "A local font needs file.ttf#Family Name.");
                    dependencies.Add(ThemeFilePaths.Dependency(file, value[..separator]));
                }
                if (member is "Duration" or "BeginTime" && value is not "Automatic")
                {
                    if (!TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var duration)
                        || duration < TimeSpan.Zero || duration > TimeSpan.FromSeconds(30))
                        Fail(attribute, file, "Animation times must be between 0 and 30 seconds.");
                }
                if (member == "RepeatBehavior" && value != "1" && value != "1x")
                    Fail(attribute, file, "Theme animations must be finite single-pass animations.");
                if (member == "RoutedEvent" && value is not ("FrameworkElement.Loaded" or "Loaded"))
                    Fail(attribute, file, "Only the visual Loaded event can start a storyboard.");
                if (member is "TargetType" or "DataType") ValidateTypeName(value, element, attribute, file);
                if (member == "Property") ValidateMember(value, element, file);
                if (member == "TargetProperty") ValidateAnimationTarget(value, element, attribute, file);
                if (member is "BlurRadius" or "Radius" && double.TryParse(value, out double radius)
                    && (!double.IsFinite(radius) || radius < 0 || radius > 64))
                    Fail(attribute, file, "Blur radius must be between 0 and 64.");
            }
            // Property elements cannot be used to smuggle arbitrary URI/type converters.
            if (element.Name.LocalName is "Image.Source" or "BitmapImage.UriSource"
                or "ResourceDictionary.Source" or "TextBlock.FontFamily" or "Control.FontFamily")
                Fail(element, file, "Use a validated resource attribute rather than a URI property element.");
            if (element.Name.LocalName.Contains('.') && !element.HasElements
                && !string.IsNullOrWhiteSpace(element.Value))
                Fail(element, file, "Scalar properties must use validated attributes.");
            if (element.Name.LocalName == "FontFamily" && element.Value.IndexOfAny(['/', '\\', '#', ':']) >= 0)
                Fail(element, file, "Use a FontFamily attribute for local font files.");
        }
        ValidateTemplateParts(document, file);
        return dependencies;
    }

    private static void ValidateElement(XElement element, string file)
    {
        string name = element.Name.LocalName;
        if (element.Name.NamespaceName == ThemeNamespace && name == "ThemePresenter") return;
        if ((element.Name.NamespaceName == LauncherNamespace
                || element.Name.NamespaceName == "clr-namespace:PCL;assembly=Plain Craft Launcher 2")
            && name == "MyTextBox") return;
        if (element.Name.NamespaceName == "clr-namespace:System;assembly=mscorlib"
            && name is "Double" or "Int32" or "Boolean") return;
        if (element.Name.Namespace != Presentation) Fail(element, file, "Only documented visual types may be constructed.");
        string[] parts = name.Split('.');
        if (!VisualTypes.Contains(parts[0])) Fail(element, file, $"Unsupported visual type: {parts[0]}");
        if (parts.Length > 2) Fail(element, file, "Invalid property element.");
        if (parts.Length == 2) ValidateMember(parts[1], element, file);
    }

    private static bool IsNamespace(string value) =>
        value == Presentation.NamespaceName || value == Xaml.NamespaceName || value == ThemeNamespace
        || value == LauncherNamespace || value == "clr-namespace:PCL;assembly=Plain Craft Launcher 2"
        || value == "clr-namespace:System;assembly=mscorlib";

    private static void ValidateMember(string value, XElement owner, string file)
    {
        string[] parts = value.Split('.');
        if (!Members.Contains(parts[^1]) || parts.Length > 2
            || (parts.Length == 2 && !AttachedOwners.Contains(parts[0])))
            Fail(owner, file, $"Unsupported theme member: {value}");
    }

    private static void ValidateMarkup(string value, XElement owner, XObject location, string file)
    {
        if (!value.StartsWith('{') || value.StartsWith("{}", StringComparison.Ordinal)) return;
        foreach (Match match in Regex.Matches(value, @"\{([A-Za-z_][A-Za-z0-9_:]*)"))
        {
            string name = match.Groups[1].Value;
            if (name.Contains(':'))
            {
                var parts = name.Split(':');
                if (parts.Length != 2 || owner.GetNamespaceOfPrefix(parts[0]) != Xaml
                    || parts[1] is not ("Type" or "Null"))
                    Fail(location, file, $"Unsupported markup extension: {name}");
                if (parts[1] == "Type")
                    ValidateTypeName(match.Value + value[(match.Index + match.Length)..], owner, location, file);
            }
            else if (name is not ("StaticResource" or "DynamicResource" or "TemplateBinding" or "Binding" or "RelativeSource"))
                Fail(location, file, $"Unsupported markup extension: {name}");
        }
        if (value.StartsWith("{Binding", StringComparison.Ordinal))
        {
            if (Regex.IsMatch(value, @"\b(Source|Converter|XPath|StringFormat|FallbackValue|TargetNullValue|UpdateSourceTrigger)\s*=")
                || Regex.IsMatch(value, @"\bMode\s*=\s*(?!OneWay\b|OneTime\b)"))
                Fail(location, file, "Theme bindings are one-way visual bindings without external sources/converters.");
            Match path = Regex.Match(value, @"^\{Binding\s+(?:Path\s*=\s*)?([^,}]+)");
            if (path.Success && !path.Groups[1].Value.Contains('=') && !BindablePaths.Contains(path.Groups[1].Value.Trim()))
                Fail(location, file, $"Unsupported visual binding path: {path.Groups[1].Value}");
            foreach (Match namedPath in Regex.Matches(value, @"\bPath\s*=\s*([^,}]+)"))
                if (!BindablePaths.Contains(namedPath.Groups[1].Value.Trim()))
                    Fail(location, file, "Binding path is not in the visual contract.");
            if (value.Contains("RelativeSource", StringComparison.Ordinal)
                && !value.Contains("TemplatedParent", StringComparison.Ordinal)
                && !value.Contains("Self", StringComparison.Ordinal))
                Fail(location, file, "Bindings may target Self, TemplatedParent or a named element in the template.");
        }
        if (value.StartsWith("{TemplateBinding", StringComparison.Ordinal))
        {
            string path = value["{TemplateBinding".Length..].Trim().TrimEnd('}').Trim();
            if (!BindablePaths.Contains(path)) Fail(location, file, "Unsupported template binding.");
        }
    }

    private static void ValidateTypeName(string value, XElement owner, XObject location, string file)
    {
        string token = value.Trim();
        if (token.StartsWith('{')) token = token[(token.IndexOf(' ') + 1)..].TrimEnd('}').Trim();
        string[] parts = token.Split(':');
        string name = parts[^1];
        XNamespace? ns = parts.Length == 1 ? owner.GetDefaultNamespace() : owner.GetNamespaceOfPrefix(parts[0]);
        bool valid = parts.Length <= 2 && (ns == Presentation && (VisualTypes.Contains(name)
                || name is "TextBox" or "ComboBox" or "ComboBoxItem" or "ContentControl" or "TextBlock")
            || ns?.NamespaceName == ThemeNamespace && name == "ThemePresenter"
            || (ns?.NamespaceName == LauncherNamespace || ns?.NamespaceName == "clr-namespace:PCL;assembly=Plain Craft Launcher 2")
                && HostTypes.Contains(name));
        if (!valid) Fail(location, file, $"Unsupported theme target type: {token}");
    }

    private static void ValidateAnimationTarget(string value, XElement owner, XObject location, string file)
    {
        // Storyboards cannot drive commands, resources, templates, data or global state.
        string normalized = value.Replace("(", "").Replace(")", "");
        foreach (string part in normalized.Split('.'))
            if (!new[] { "Opacity", "Color", "Background", "SolidColorBrush", "Border", "RenderTransform",
                    "TranslateTransform", "ScaleTransform", "RotateTransform", "X", "Y", "ScaleX", "ScaleY", "Angle" }.Contains(part))
                Fail(location, file, $"Unsupported animation target: {value}");
    }

    private static void ValidateTemplateParts(XDocument document, string file)
    {
        foreach (var template in document.Descendants(Presentation + "ControlTemplate"))
        {
            string target = template.Attribute("TargetType")?.Value
                ?? template.Ancestors(Presentation + "Style").FirstOrDefault()?.Attribute("TargetType")?.Value ?? "";
            string[] required = target.Contains("TextBox", StringComparison.Ordinal) ? ["PART_ContentHost"]
                : target.Contains("ComboBox", StringComparison.Ordinal) && !target.Contains("ComboBoxItem", StringComparison.Ordinal)
                    ? ["PART_Popup", "PART_EditableTextBox"]
                : target.Contains("ScrollBar", StringComparison.Ordinal) ? ["PART_Track"] : [];
            foreach (string name in required)
                if (!template.Descendants().Any(node => (string?)node.Attribute(Xaml + "Name") == name
                        || (string?)node.Attribute("Name") == name))
                    Fail(template, file, $"Required template part is missing: {name}");
            if (target.Contains("ThemePresenter", StringComparison.Ordinal)
                && !template.Descendants(Presentation + "ContentPresenter").Any(node =>
                    node.Attribute("Content")?.Value == "{TemplateBinding Content}" || node.Attribute("ContentSource")?.Value == "Content"))
                Fail(template, file, "ThemePresenter templates must display the host Content slot.");
        }
    }

    public static void ValidateDependencyGraph(IReadOnlyDictionary<string, byte[]> files)
    {
        var complete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files.Keys.Where(key => key.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))) Visit(file, 0);
        void Visit(string file, int depth)
        {
            if (complete.Contains(file)) return;
            if (depth > 32 || !visiting.Add(file)) throw new ThemePackageException("Cyclic or excessively deep theme dictionary.", file);
            foreach (string dependency in Validate(files[file], file))
            {
                if (!files.ContainsKey(dependency)) throw new ThemePackageException($"Missing resource: {dependency}", file);
                if (dependency.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) Visit(dependency, depth + 1);
            }
            visiting.Remove(file);
            complete.Add(file);
        }
    }

    private static void Fail(XObject location, string file, string message)
    {
        var line = (IXmlLineInfo)location;
        throw new ThemePackageException(message, file, line.LineNumber, line.LinePosition);
    }
}
