using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace PCL
{
    public static class XamlEventSanitizer
    {
        public class SanitizeResult
        {
            public string SanitizedXaml { get; set; } = "";
            public List<string> UnsupportedTypesFound { get; set; } = new();
            public List<string> UnrecognizedTypes { get; set; } = new();
        }

        public static SanitizeResult Sanitize(string xaml)
        {
            var result = new SanitizeResult();
            using var textReader = new StringReader(xaml);
            using var reader = XmlReader.Create(textReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            CustomPageXamlPolicy.Validate(document);
            var root = document.Root;
            if (root is null) return result;

            // Resolve the namespace rather than rewriting tag text. Removing a node must
            // also remove its closing tag, including when the same tag is nested.
            foreach (var element in root.DescendantsAndSelf().ToArray())
            {
                if (element.Document is null) continue;
                var localNamespace = element.GetNamespaceOfPrefix("local");
                if (localNamespace is null) continue;

                var eventAttribute = element.Attribute(localNamespace + "CustomEventService.EventType");
                if (eventAttribute is not null)
                {
                    if (!Normalize(eventAttribute.Value, result, out var eventType))
                    {
                        element.Remove();
                        continue;
                    }
                    eventAttribute.Value = eventType;
                }

                if (element.Name == localNamespace + "CustomEvent")
                {
                    var typeAttribute = element.Attribute("Type");
                    if (typeAttribute is not null)
                    {
                        if (!Normalize(typeAttribute.Value, result, out var customType))
                        {
                            element.Remove();
                            continue;
                        }
                        typeAttribute.Value = customType;
                    }
                }

                if (element.Name == localNamespace + "CustomEventService.EventType" ||
                    element.Name == localNamespace + "CustomEvent.Type")
                {
                    if (!Normalize(element.Value.Trim(), result, out var propertyType))
                        (element.Parent ?? element).Remove();
                    else
                        element.Value = propertyType;
                    continue;
                }

                if (element.Name.LocalName == "Setter" &&
                    (string?)element.Attribute("Property") == "local:CustomEventService.EventType" &&
                    element.Attribute("Value") is { } valueAttribute)
                {
                    if (!Normalize(valueAttribute.Value, result, out var setterType))
                        element.Remove();
                    else
                        valueAttribute.Value = setterType;
                }
            }

            result.UnsupportedTypesFound = result.UnsupportedTypesFound.Distinct().ToList();
            result.UnrecognizedTypes = result.UnrecognizedTypes.Distinct().ToList();
            result.SanitizedXaml = document.ToString(SaveOptions.DisableFormatting);
            return result;
        }

        private static bool Normalize(string value, SanitizeResult result, out string normalized)
        {
            if (EventTypeMapper.TryToEnglish(value, out normalized)) return true;
            normalized = value;
            if (Enum.TryParse<EventType>(value, true, out _)) return true;
            if (EventTypeMapper.IsUnsupportedType(value))
                result.UnsupportedTypesFound.Add(value);
            else
                result.UnrecognizedTypes.Add(value);
            return false;
        }
    }
}
