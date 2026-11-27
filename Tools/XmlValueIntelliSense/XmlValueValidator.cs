using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace XmlValueIntelliSense
{
    internal sealed class XmlValidationIssue
    {
        public int Start { get; set; }

        public int Length { get; set; }

        public string Message { get; set; }
    }

    internal static class XmlValueValidator
    {
        private static readonly Regex AttributePattern =
            new Regex(@"(?<name>[A-Za-z_][A-Za-z0-9_.\-:]*)\s*=\s*(?<q>[""'])(?<value>[\s\S]*?)\k<q>",
                RegexOptions.Compiled);

        public static IReadOnlyList<XmlValidationIssue> Validate(string text, SchemaDocument schema)
        {
            var issues = new List<XmlValidationIssue>();
            if (string.IsNullOrEmpty(text) || schema == null)
            {
                return issues;
            }

            var seen = new HashSet<int>();
            var index = 0;
            while (index < text.Length)
            {
                var tagStart = text.IndexOf('<', index);
                if (tagStart < 0)
                {
                    break;
                }

                if (tagStart + 1 < text.Length && IsStartTag(text[tagStart + 1]))
                {
                    var tagEnd = FindTagEnd(text, tagStart);
                    if (tagEnd > tagStart)
                    {
                        CollectTagIssues(text, tagStart, tagEnd, schema, issues, seen);
                        index = tagEnd + 1;
                        continue;
                    }
                }

                index = tagStart + 1;
            }

            return issues;
        }

        private static void CollectTagIssues(string text, int tagStart, int tagEnd, SchemaDocument schema,
            List<XmlValidationIssue> issues, HashSet<int> seen)
        {
            var length = tagEnd - tagStart - 1;
            if (length <= 0)
            {
                return;
            }

            var content = text.Substring(tagStart + 1, length);
            foreach (Match match in AttributePattern.Matches(content))
            {
                var valueGroup = match.Groups["value"];
                var name = match.Groups["name"].Value;
                if (name == "xmlns" || name.StartsWith("xmlns:", StringComparison.Ordinal)
                    || name.StartsWith("xsi:", StringComparison.Ordinal)
                    || name.StartsWith("xml:", StringComparison.Ordinal))
                {
                    continue;
                }

                var valueStart = tagStart + 1 + valueGroup.Index;
                var context = XmlValueContext.TryCreate(text, valueStart);
                if (context == null)
                {
                    continue;
                }

                var rule = schema.GetRule(context);
                if (rule == null)
                {
                    continue;
                }

                var value = valueGroup.Value.Trim();
                string message;
                if (value.Length == 0)
                {
                    if (!rule.RequiresValue)
                    {
                        continue;
                    }

                    message = BuildEmptyMessage(context.AttributeName, rule);
                }
                else
                {
                    if (rule.OpenEnded || rule.Values == null || rule.Values.Count == 0
                        || rule.Values.Any(item => string.Equals(item.Value, value, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    message = BuildInvalidMessage(context.AttributeName, value, rule);
                }

                if (!seen.Add(context.ValueStart))
                {
                    continue;
                }

                var start = Math.Max(0, context.ValueStart - 1);
                var end = Math.Min(text.Length, context.ValueStart + context.ValueLength + 1);
                issues.Add(new XmlValidationIssue
                {
                    Start = start,
                    Length = end - start,
                    Message = message
                });
            }
        }

        private static string BuildEmptyMessage(string attributeName, AttributeRule rule)
        {
            if (rule.Values == null || rule.Values.Count == 0)
            {
                return "属性 \"" + attributeName + "\" 不能为空。";
            }

            return "属性 \"" + attributeName + "\" 的值不能为空，允许值：" + BuildAllowedValues(rule) + "。";
        }

        private static string BuildInvalidMessage(string attributeName, string value, AttributeRule rule)
        {
            return "属性 \"" + attributeName + "\" 的值 \"" + value + "\" 不是有效值，允许值：" + BuildAllowedValues(rule) + "。";
        }

        private static string BuildAllowedValues(AttributeRule rule)
        {
            var values = rule.Values.Take(10).Select(item => item.Value).ToArray();
            var suffix = rule.Values.Count > values.Length ? " 等" : "";
            return string.Join(" / ", values) + suffix;
        }

        private static bool IsStartTag(char next)
        {
            return next != '!' && next != '/' && next != '?';
        }

        private static int FindTagEnd(string text, int tagStart)
        {
            var quote = '\0';
            for (var index = tagStart; index < text.Length; index++)
            {
                var ch = text[index];
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
                    return index;
                }
            }

            return -1;
        }
    }
}
