using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace XmlValueIntelliSense
{
    internal sealed class XmlValueContext
    {
        private static readonly Regex ElementPattern =
            new Regex(@"^<\s*([A-Za-z_][A-Za-z0-9_.\-:]*)", RegexOptions.Compiled);

        private static readonly Regex AttributePattern =
            new Regex(@"(?<name>[A-Za-z_][A-Za-z0-9_.\-:]*)\s*=\s*(?<q>[""'])(?<value>[\s\S]*?)\k<q>", RegexOptions.Compiled);

        public string ElementName { get; private set; }

        public string ElementNamespace { get; private set; }

        public string AttributeName { get; private set; }

        public string AttributeNamespace { get; private set; }

        public int ValueStart { get; private set; }

        public int ValueLength { get; private set; }

        public char Quote { get; private set; }

        public static XmlValueContext TryCreate(string text, int position)
        {
            if (text == null || position < 0 || position > text.Length)
            {
                return null;
            }

            if (IsInCommentOrCData(text, position))
            {
                return null;
            }

            var tagStart = FindTagStart(text, position);
            if (tagStart < 0)
            {
                return null;
            }

            var tagEnd = FindTagEnd(text, tagStart);
            if (tagEnd < 0 || position > tagEnd)
            {
                return null;
            }

            var tag = text.Substring(tagStart, tagEnd - tagStart + 1);
            if (tag.StartsWith("</", StringComparison.Ordinal) || tag.StartsWith("<!", StringComparison.Ordinal)
                || tag.StartsWith("<?", StringComparison.Ordinal))
            {
                return null;
            }

            var elementMatch = ElementPattern.Match(tag);
            if (!elementMatch.Success)
            {
                return null;
            }

            var elementQName = elementMatch.Groups[1].Value;
            foreach (Match match in AttributePattern.Matches(tag))
            {
                var attributeQName = match.Groups["name"].Value;
                if (attributeQName == "xmlns" || attributeQName.StartsWith("xmlns:", StringComparison.Ordinal))
                {
                    continue;
                }

                var valueStart = tagStart + match.Groups["value"].Index;
                var valueLength = match.Groups["value"].Length;
                var valueEnd = valueStart + valueLength;
                if (position < valueStart || position > valueEnd)
                {
                    continue;
                }

                var context = new XmlValueContext
                {
                    ElementName = LocalName(elementQName),
                    AttributeName = LocalName(attributeQName),
                    ValueStart = valueStart,
                    ValueLength = valueLength,
                    Quote = match.Groups["q"].Value[0]
                };

                var namespaces = CollectNamespaces(text, tagStart, tag);
                context.ElementNamespace = ResolveNamespace(namespaces, Prefix(elementQName), true);
                context.AttributeNamespace = string.IsNullOrEmpty(Prefix(attributeQName))
                    ? ""
                    : ResolveNamespace(namespaces, Prefix(attributeQName), false);
                return context;
            }

            return null;
        }

        private static bool IsInCommentOrCData(string text, int position)
        {
            var index = Math.Min(position, text.Length - 1);
            if (index < 0)
            {
                return false;
            }

            var commentStart = text.LastIndexOf("<!--", index, StringComparison.Ordinal);
            var commentEnd = text.LastIndexOf("-->", index, StringComparison.Ordinal);
            if (commentStart >= 0 && commentStart > commentEnd)
            {
                return true;
            }

            var cdataStart = text.LastIndexOf("<![CDATA[", index, StringComparison.Ordinal);
            var cdataEnd = text.LastIndexOf("]]>", index, StringComparison.Ordinal);
            if (cdataStart >= 0 && cdataStart > cdataEnd)
            {
                return true;
            }

            return false;
        }

        private static int FindTagStart(string text, int position)
        {
            var index = Math.Min(position, text.Length - 1);
            if (index < 0)
            {
                return -1;
            }

            var lastLt = text.LastIndexOf('<', index);
            if (lastLt < 0)
            {
                return -1;
            }

            var lastGt = text.LastIndexOf('>', index);
            if (lastGt > lastLt)
            {
                return -1;
            }

            return lastLt;
        }

        private static int FindTagEnd(string text, int tagStart)
        {
            var quote = '\0';
            for (var i = tagStart; i < text.Length; i++)
            {
                var ch = text[i];
                if (quote != '\0')
                {
                    if (ch == quote)
                    {
                        quote = '\0';
                    }
                    continue;
                }

                if (ch == '"' || ch == '\'')
                {
                    quote = ch;
                    continue;
                }

                if (ch == '>')
                {
                    return i;
                }
            }

            return -1;
        }

        private static Dictionary<string, string> CollectNamespaces(string text, int tagStart, string currentTag)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            AddNamespaces(currentTag, result);

            var position = tagStart;
            var depth = 0;
            while (position > 0 && depth < 64)
            {
                depth++;
                var previousLt = text.LastIndexOf('<', Math.Max(0, position - 1));
                if (previousLt < 0)
                {
                    break;
                }

                var previousEnd = FindTagEnd(text, previousLt);
                if (previousEnd < 0)
                {
                    break;
                }

                var previousTag = text.Substring(previousLt, previousEnd - previousLt + 1);
                if (!previousTag.StartsWith("</", StringComparison.Ordinal)
                    && !previousTag.StartsWith("<!", StringComparison.Ordinal)
                    && !previousTag.StartsWith("<?", StringComparison.Ordinal))
                {
                    AddNamespaces(previousTag, result);
                }

                position = previousLt;
            }

            return result;
        }

        private static void AddNamespaces(string tag, Dictionary<string, string> target)
        {
            foreach (Match match in AttributePattern.Matches(tag))
            {
                var name = match.Groups["name"].Value;
                if (name == "xmlns")
                {
                    if (!target.ContainsKey(""))
                    {
                        target[""] = match.Groups["value"].Value;
                    }
                    continue;
                }

                if (name.StartsWith("xmlns:", StringComparison.Ordinal))
                {
                    var prefix = name.Substring(6);
                    if (!target.ContainsKey(prefix))
                    {
                        target[prefix] = match.Groups["value"].Value;
                    }
                }
            }
        }

        private static string ResolveNamespace(Dictionary<string, string> namespaces, string prefix, bool isElement)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                if (isElement)
                {
                    string value;
                    return namespaces.TryGetValue("", out value) ? value : "";
                }

                return "";
            }

            if (string.Equals(prefix, "xml", StringComparison.Ordinal))
            {
                return "http://www.w3.org/XML/1998/namespace";
            }

            string mapped;
            return namespaces.TryGetValue(prefix, out mapped) ? mapped : "";
        }

        private static string LocalName(string qualifiedName)
        {
            if (string.IsNullOrEmpty(qualifiedName))
            {
                return "";
            }

            var index = qualifiedName.IndexOf(':');
            return index < 0 ? qualifiedName : qualifiedName.Substring(index + 1);
        }

        private static string Prefix(string qualifiedName)
        {
            if (string.IsNullOrEmpty(qualifiedName))
            {
                return "";
            }

            var index = qualifiedName.IndexOf(':');
            return index < 0 ? "" : qualifiedName.Substring(0, index);
        }
    }
}
