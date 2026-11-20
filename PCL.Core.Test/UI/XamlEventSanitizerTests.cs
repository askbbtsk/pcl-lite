using System;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test.UI;

[TestClass]
public sealed class XamlEventSanitizerTests
{
    private const string Start = "<StackPanel xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:local='clr-namespace:PCL'>";

    [TestMethod]
    [DataRow("<local:CustomEvent Type='内存优化'><local:CustomEvent Type='CopyText'/></local:CustomEvent>")]
    [DataRow("<Border local:CustomEventService.EventType='内存优化'><Border><TextBlock Text='removed'/></Border></Border>")]
    [DataRow("<Border><TextBlock Text='removed'/><local:CustomEventService.EventType>内存优化</local:CustomEventService.EventType></Border>")]
    [DataRow("<local:CustomEvent><local:CustomEvent.Type>内存优化</local:CustomEvent.Type></local:CustomEvent>")]
    public void InvalidEventRemovesWholeElementAndPreservesFollowingSibling(string invalid)
    {
        var result = XamlEventSanitizer.Sanitize(Start + invalid + "<TextBlock Text='kept'/></StackPanel>");
        var root = XDocument.Parse(result.SanitizedXaml).Root!;
        Assert.HasCount(1, root.Elements().ToArray());
        Assert.AreEqual("kept", (string?)root.Elements().Single().Attribute("Text"));
        CollectionAssert.AreEqual(new[] { "内存优化" }, result.UnsupportedTypesFound);
    }

    [TestMethod]
    public void UnknownEventsAreRemovedWithoutDuplicatingDocumentTail()
    {
        var result = XamlEventSanitizer.Sanitize(Start +
            "<Border local:CustomEventService.EventType='unknown'><Border/></Border>" +
            "<local:CustomEvent Type='unknown'><TextBlock/></local:CustomEvent>" +
            "<TextBlock Text='tail'/></StackPanel>");
        Assert.HasCount(1, XDocument.Parse(result.SanitizedXaml).Root!.Elements().ToArray());
        CollectionAssert.AreEqual(new[] { "unknown" }, result.UnrecognizedTypes);
    }

    [TestMethod]
    public void ValidEventsAndLiteralTextRemainAvailable()
    {
        var result = XamlEventSanitizer.Sanitize(Start +
            "<Border local:CustomEventService.EventType='打开网页'><TextBlock Text='&lt;local:CustomEvent Type=&quot;内存优化&quot;/&gt;'/></Border>" +
            "<local:CustomEvent><local:CustomEvent.Type>复制文本</local:CustomEvent.Type></local:CustomEvent>" +
            "<Setter Value='复制文本' Property='local:CustomEventService.EventType'/>" +
            "<Setter Value='unknown' Property='local:CustomEventService.EventType'/>" +
            "</StackPanel>");
        var elements = XDocument.Parse(result.SanitizedXaml).Root!.Elements().ToArray();
        Assert.HasCount(3, elements);
        Assert.AreEqual("OpenUrl", elements[0].Attributes().Single().Value);
        Assert.AreEqual("<local:CustomEvent Type=\"内存优化\"/>", (string?)elements[0].Elements().Single().Attribute("Text"));
        Assert.AreEqual("CopyText", elements[1].Value);
        Assert.AreEqual("CopyText", (string?)elements[2].Attribute("Value"));
        Assert.IsEmpty(result.UnsupportedTypesFound);
    }

    [TestMethod]
    public void WrappingKeepsNamespaceTextAndSupportedDeclarations()
    {
        var result = XamlEventSanitizer.Sanitize(CustomPageXamlPolicy.WrapFragment(
            "<TextBlock xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Text='xmlns is literal text'/>"));
        Assert.AreEqual("xmlns is literal text",
            (string?)XDocument.Parse(result.SanitizedXaml).Root!.Elements().Single().Attribute("Text"));
    }

    [TestMethod]
    [DataRow("<ResourceDictionary Source='https://example.invalid/theme.xaml'/>")]
    [DataRow("<ResourceDictionary Source='local.xaml'/>")]
    [DataRow("<ResourceDictionary><ResourceDictionary.Source>local.xaml</ResourceDictionary.Source></ResourceDictionary>")]
    [DataRow("<TextBlock xmlns:external='clr-namespace:System.IO;assembly=System.Runtime'/>")]
    public void ExternalXamlCannotEscapeTheCheckedDocument(string fragment)
    {
        Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            XamlEventSanitizer.Sanitize(CustomPageXamlPolicy.WrapFragment(fragment)));
    }

    [TestMethod]
    public void DocumentTypesAreRejectedBeforeEventProcessing()
    {
        Assert.ThrowsExactly<XmlException>(() =>
            XamlEventSanitizer.Sanitize("<!DOCTYPE root [<!ENTITY test 'value'>]><root>&test;</root>"));
    }
}
