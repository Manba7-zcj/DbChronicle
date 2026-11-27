using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Schema;

namespace XmlValueIntelliSense
{
    internal sealed class SchemaValue
    {
        public string Value { get; set; }

        public string Description { get; set; }
    }

    internal sealed class AttributeRule
    {
        public string Name { get; set; }

        public bool Required { get; set; }

        public bool OpenEnded { get; set; }

        public IReadOnlyList<SchemaValue> Values { get; set; }

        public string Description { get; set; }

        public bool RequiresValue
        {
            get { return Required || (Values != null && Values.Count > 0); }
        }
    }

    internal sealed class SchemaDocument
    {
        private const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

        private readonly XmlSchemaSet _schemas;

        private readonly List<XmlSchemaElement> _elements = new List<XmlSchemaElement>();

        private readonly Dictionary<string, FileStamp> _schemaStamps =
            new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);

        private SchemaDocument(XmlSchemaSet schemas)
        {
            _schemas = schemas;
        }

        public bool IsSchemaCurrent()
        {
            foreach (var pair in _schemaStamps)
            {
                try
                {
                    var info = new FileInfo(pair.Key);
                    if (!info.Exists || info.LastWriteTimeUtc != pair.Value.LastWriteUtc
                        || info.Length != pair.Value.Length)
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }

        public static SchemaDocument TryLoad(string xmlPath)
        {
            if (string.IsNullOrEmpty(xmlPath) || !File.Exists(xmlPath))
            {
                return null;
            }

            string noNamespaceLocation;
            var namespacedLocations = new List<KeyValuePair<string, string>>();
            string xmlText;
            try
            {
                xmlText = File.ReadAllText(xmlPath);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "读取 XML 失败：" + xmlPath);
                return null;
            }

            if (!TryReadLocationsFromText(xmlText, out noNamespaceLocation, namespacedLocations))
            {
                return null;
            }

            return TryLoad(xmlPath, noNamespaceLocation, namespacedLocations);
        }

        public static SchemaDocument TryLoad(string xmlPath, string noNamespaceLocation,
            IReadOnlyList<KeyValuePair<string, string>> namespacedLocations)
        {
            if (string.IsNullOrEmpty(xmlPath))
            {
                return null;
            }

            if (string.IsNullOrEmpty(noNamespaceLocation)
                && (namespacedLocations == null || namespacedLocations.Count == 0))
            {
                return null;
            }

            var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(xmlPath)) ?? "";
            var schemas = new XmlSchemaSet();
            schemas.XmlResolver = new LocalOnlyXmlResolver();

            var added = 0;
            var schemaPaths = new List<string>();
            if (!string.IsNullOrEmpty(noNamespaceLocation))
            {
                var path = ResolvePath(baseDirectory, noNamespaceLocation);
                if (path != null && File.Exists(path))
                {
                    try
                    {
                        schemas.Add(null, path);
                        added++;
                        schemaPaths.Add(path);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "添加 noNamespaceSchemaLocation 失败：" + path);
                    }
                }
            }

            foreach (var pair in namespacedLocations)
            {
                var path = ResolvePath(baseDirectory, pair.Value);
                if (path != null && File.Exists(path))
                {
                    try
                    {
                        schemas.Add(pair.Key, path);
                        added++;
                        schemaPaths.Add(path);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "添加 schemaLocation 失败：" + pair.Key + " " + path);
                    }
                }
            }

            if (added == 0)
            {
                return null;
            }

            try
            {
                schemas.Compile();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "编译 XSD 失败");
                return null;
            }

            var document = new SchemaDocument(schemas);
            foreach (var schemaPath in schemaPaths)
            {
                try
                {
                    var info = new FileInfo(schemaPath);
                    document._schemaStamps[schemaPath] = new FileStamp
                    {
                        LastWriteUtc = info.LastWriteTimeUtc,
                        Length = info.Length
                    };
                }
                catch
                {
                }
            }

            document.BuildElementIndex();
            Logger.Write("schema 已加载：" + xmlPath + "，元素数=" + document._elements.Count);
            return document;
        }

        public IReadOnlyList<SchemaValue> GetValues(XmlValueContext context)
        {
            if (context == null)
            {
                return new List<SchemaValue>();
            }

            return GetValues(context.ElementName, context.ElementNamespace,
                context.AttributeName, context.AttributeNamespace);
        }

        public IReadOnlyList<SchemaValue> GetValues(string elementName, string elementNamespace,
            string attributeName, string attributeNamespace)
        {
            var rule = GetRule(elementName, elementNamespace, attributeName, attributeNamespace);
            return rule == null ? new List<SchemaValue>() : rule.Values;
        }

        public AttributeRule GetRule(XmlValueContext context)
        {
            if (context == null)
            {
                return null;
            }

            return GetRule(context.ElementName, context.ElementNamespace,
                context.AttributeName, context.AttributeNamespace);
        }

        public AttributeRule GetRule(string elementName, string elementNamespace,
            string attributeName, string attributeNamespace)
        {
            var element = FindElement(elementName, elementNamespace);
            if (element == null)
            {
                return null;
            }

            var complexType = element.ElementSchemaType as XmlSchemaComplexType;
            if (complexType == null)
            {
                return null;
            }

            var attribute = FindAttribute(complexType, attributeName, attributeNamespace);
            if (attribute == null)
            {
                return null;
            }

            var description = GetDocumentation(attribute.Annotation);
            var simpleType = attribute.AttributeSchemaType;
            if (simpleType == null)
            {
                simpleType = attribute.SchemaType as XmlSchemaSimpleType;
            }

            var values = new List<SchemaValue>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            CollectEnums(simpleType, values, seen, description, 0);

            if (values.Count == 0 && simpleType != null && simpleType.Datatype != null
                && simpleType.Datatype.TypeCode == XmlTypeCode.Boolean)
            {
                AddValue(values, seen, "true", description);
                AddValue(values, seen, "false", description);
            }

            return new AttributeRule
            {
                Name = attributeName,
                Required = attribute.Use == XmlSchemaUse.Required,
                OpenEnded = IsOpenEnded(simpleType, 0),
                Values = values,
                Description = description
            };
        }

        private static bool IsOpenEnded(XmlSchemaSimpleType type, int depth)
        {
            if (type == null || depth > 16)
            {
                return false;
            }

            if (type.Content is XmlSchemaSimpleTypeUnion)
            {
                return true;
            }

            var restriction = type.Content as XmlSchemaSimpleTypeRestriction;
            if (restriction != null && IsOpenEnded(restriction.BaseType, depth + 1))
            {
                return true;
            }

            var baseType = type.BaseXmlSchemaType as XmlSchemaSimpleType;
            return baseType != null && !ReferenceEquals(baseType, type) && IsOpenEnded(baseType, depth + 1);
        }

        public bool HasValues(XmlValueContext context)
        {
            return GetValues(context).Count > 0;
        }

        private void BuildElementIndex()
        {
            var visited = new HashSet<XmlSchemaElement>();
            foreach (XmlSchemaObject item in _schemas.GlobalElements.Values)
            {
                var element = item as XmlSchemaElement;
                if (element != null)
                {
                    IndexElement(element, visited, 0);
                }
            }
        }

        private void IndexElement(XmlSchemaElement element, HashSet<XmlSchemaElement> visited, int depth)
        {
            if (element == null || depth > 32 || visited.Contains(element))
            {
                return;
            }

            visited.Add(element);
            if (!string.IsNullOrEmpty(ElementName(element)))
            {
                _elements.Add(element);
            }

            var complexType = element.ElementSchemaType as XmlSchemaComplexType;
            if (complexType != null)
            {
                IndexParticle(complexType.ContentTypeParticle, visited, depth + 1);
            }
        }

        private void IndexParticle(XmlSchemaParticle particle, HashSet<XmlSchemaElement> visited, int depth)
        {
            if (particle == null || depth > 32)
            {
                return;
            }

            var group = particle as XmlSchemaGroupBase;
            if (group != null)
            {
                foreach (XmlSchemaObject item in group.Items)
                {
                    IndexParticle(item as XmlSchemaParticle, visited, depth + 1);
                }
                return;
            }

            var element = particle as XmlSchemaElement;
            if (element != null)
            {
                IndexElement(element, visited, depth + 1);
                return;
            }

            var groupRef = particle as XmlSchemaGroupRef;
            if (groupRef != null)
            {
                IndexParticle(groupRef.Particle, visited, depth + 1);
            }
        }

        private XmlSchemaElement FindElement(string localName, string namespaceUri)
        {
            if (string.IsNullOrEmpty(localName))
            {
                return null;
            }

            XmlSchemaElement fallback = null;
            foreach (var element in _elements)
            {
                if (!string.Equals(ElementName(element), localName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(ElementNamespace(element), namespaceUri ?? "", StringComparison.Ordinal))
                {
                    return element;
                }

                if (fallback == null)
                {
                    fallback = element;
                }
            }

            return fallback;
        }

        private static XmlSchemaAttribute FindAttribute(XmlSchemaComplexType complexType, string localName, string namespaceUri)
        {
            if (complexType == null || string.IsNullOrEmpty(localName))
            {
                return null;
            }

            var qualifiedName = new XmlQualifiedName(localName, namespaceUri ?? "");
            var attribute = complexType.AttributeUses[qualifiedName] as XmlSchemaAttribute;
            if (attribute != null)
            {
                return attribute;
            }

            foreach (XmlSchemaObject item in complexType.AttributeUses.Values)
            {
                var candidate = item as XmlSchemaAttribute;
                if (candidate != null && string.Equals(candidate.Name, localName, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            return null;
        }

        private void CollectEnums(XmlSchemaSimpleType type, List<SchemaValue> target, HashSet<string> seen,
            string fallbackDescription, int depth)
        {
            if (type == null || depth > 16)
            {
                return;
            }

            var restriction = type.Content as XmlSchemaSimpleTypeRestriction;
            if (restriction != null)
            {
                var added = false;
                foreach (XmlSchemaObject item in restriction.Facets)
                {
                    var enumeration = item as XmlSchemaEnumerationFacet;
                    if (enumeration == null)
                    {
                        continue;
                    }

                    added = true;
                    var description = GetDocumentation(enumeration.Annotation);
                    AddValue(target, seen, enumeration.Value,
                        string.IsNullOrEmpty(description) ? fallbackDescription : description);
                }

                if (added)
                {
                    return;
                }

                if (restriction.BaseType != null)
                {
                    CollectEnums(restriction.BaseType, target, seen, fallbackDescription, depth + 1);
                    return;
                }

                if (restriction.BaseTypeName != null && !restriction.BaseTypeName.IsEmpty)
                {
                    var baseType = _schemas.GlobalTypes[restriction.BaseTypeName] as XmlSchemaSimpleType;
                    if (baseType != null)
                    {
                        CollectEnums(baseType, target, seen, fallbackDescription, depth + 1);
                    }
                }
            }

            var union = type.Content as XmlSchemaSimpleTypeUnion;
            if (union != null)
            {
                if (union.BaseMemberTypes != null && union.BaseMemberTypes.Length > 0)
                {
                    foreach (var member in union.BaseMemberTypes)
                    {
                        CollectEnums(member, target, seen, fallbackDescription, depth + 1);
                    }
                }
                else if (union.BaseTypes != null)
                {
                    foreach (XmlSchemaSimpleType member in union.BaseTypes)
                    {
                        CollectEnums(member, target, seen, fallbackDescription, depth + 1);
                    }
                }

                return;
            }

            var baseSimpleType = type.BaseXmlSchemaType as XmlSchemaSimpleType;
            if (baseSimpleType != null && !ReferenceEquals(baseSimpleType, type))
            {
                CollectEnums(baseSimpleType, target, seen, fallbackDescription, depth + 1);
            }
        }

        private static void AddValue(List<SchemaValue> target, HashSet<string> seen, string value, string description)
        {
            if (value == null)
            {
                return;
            }

            var trimmed = value.Trim();
            if (trimmed.Length == 0 || !seen.Add(trimmed))
            {
                return;
            }

            target.Add(new SchemaValue { Value = trimmed, Description = description ?? "" });
        }

        private static string GetDocumentation(XmlSchemaAnnotation annotation)
        {
            if (annotation == null)
            {
                return "";
            }

            var builder = new StringBuilder();
            foreach (XmlSchemaObject item in annotation.Items)
            {
                var documentation = item as XmlSchemaDocumentation;
                if (documentation == null || documentation.Markup == null)
                {
                    continue;
                }

                foreach (XmlNode node in documentation.Markup)
                {
                    if (!string.IsNullOrEmpty(node.InnerText))
                    {
                        builder.AppendLine(node.InnerText.Trim());
                    }
                }
            }

            return builder.ToString().Trim();
        }

        internal static bool TryReadLocationsFromText(string xmlText, out string noNamespaceLocation,
            List<KeyValuePair<string, string>> namespacedLocations)
        {
            noNamespaceLocation = null;
            namespacedLocations.Clear();

            if (string.IsNullOrEmpty(xmlText))
            {
                return false;
            }

            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    IgnoreWhitespace = true
                };

                using (var reader = XmlReader.Create(new StringReader(xmlText), settings))
                {
                    if (reader.MoveToContent() != XmlNodeType.Element)
                    {
                        return false;
                    }

                    noNamespaceLocation = reader.GetAttribute("noNamespaceSchemaLocation", XsiNamespace);
                    var schemaLocation = reader.GetAttribute("schemaLocation", XsiNamespace);
                    if (!string.IsNullOrEmpty(schemaLocation))
                    {
                        var parts = schemaLocation.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        for (var i = 0; i + 1 < parts.Length; i += 2)
                        {
                            namespacedLocations.Add(new KeyValuePair<string, string>(parts[i], parts[i + 1]));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "读取 schemaLocation 失败");
                return false;
            }

            return !string.IsNullOrEmpty(noNamespaceLocation) || namespacedLocations.Count > 0;
        }

        private static string ResolvePath(string baseDirectory, string location)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                return null;
            }

            var value = location.Trim();
            Uri absolute;
            if (Uri.TryCreate(value, UriKind.Absolute, out absolute))
            {
                return absolute.IsFile ? absolute.LocalPath : null;
            }

            try
            {
                return Path.GetFullPath(Path.Combine(baseDirectory, value));
            }
            catch
            {
                return null;
            }
        }

        private static string ElementName(XmlSchemaElement element)
        {
            if (!string.IsNullOrEmpty(element.Name))
            {
                return element.Name;
            }

            return element.RefName == null ? "" : element.RefName.Name;
        }

        private static string ElementNamespace(XmlSchemaElement element)
        {
            if (!string.IsNullOrEmpty(element.Name))
            {
                return element.QualifiedName == null ? "" : element.QualifiedName.Namespace;
            }

            return element.RefName == null ? "" : element.RefName.Namespace;
        }

        private sealed class FileStamp
        {
            public DateTime LastWriteUtc { get; set; }

            public long Length { get; set; }
        }

        private sealed class LocalOnlyXmlResolver : XmlUrlResolver
        {
            public override object GetEntity(Uri absoluteUri, string role, Type ofObjectToReturn)
            {
                if (absoluteUri == null || !absoluteUri.IsFile)
                {
                    throw new XmlException("只允许加载本地 XSD：" + absoluteUri);
                }

                return base.GetEntity(absoluteUri, role, ofObjectToReturn);
            }
        }
    }
}
