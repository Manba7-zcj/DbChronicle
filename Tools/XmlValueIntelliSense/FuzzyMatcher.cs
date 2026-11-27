using System;
using System.Collections.Generic;

namespace XmlValueIntelliSense
{
    internal static class FuzzyMatcher
    {
        public static bool IsMatch(string text, string filter)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            if (string.IsNullOrEmpty(filter))
            {
                return true;
            }

            var textIndex = 0;
            for (var filterIndex = 0; filterIndex < filter.Length; filterIndex++)
            {
                var target = char.ToUpperInvariant(filter[filterIndex]);
                var found = false;
                while (textIndex < text.Length)
                {
                    if (char.ToUpperInvariant(text[textIndex]) == target)
                    {
                        found = true;
                        textIndex++;
                        break;
                    }

                    textIndex++;
                }

                if (!found)
                {
                    return false;
                }
            }

            return true;
        }

        public static bool IsPrefix(string text, string filter)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(filter))
            {
                return false;
            }

            return text.StartsWith(filter, StringComparison.OrdinalIgnoreCase);
        }

        public static string Normalize(string filter)
        {
            if (string.IsNullOrEmpty(filter))
            {
                return "";
            }

            if (filter.EndsWith(" ", StringComparison.Ordinal))
            {
                return "";
            }

            return filter.Trim();
        }

        public static List<T> Filter<T>(IEnumerable<T> items, Func<T, string> selectText, string filter)
        {
            var result = new List<T>();
            if (items == null)
            {
                return result;
            }

            var normalized = Normalize(filter);
            if (normalized.Length == 0)
            {
                result.AddRange(items);
                return result;
            }

            var matched = new List<T>();
            foreach (var item in items)
            {
                if (IsMatch(selectText == null ? null : selectText(item), normalized))
                {
                    matched.Add(item);
                }
            }

            var hasPrefix = false;
            foreach (var item in matched)
            {
                if (IsPrefix(selectText == null ? null : selectText(item), normalized))
                {
                    hasPrefix = true;
                    break;
                }
            }

            if (!hasPrefix)
            {
                return matched;
            }

            foreach (var item in matched)
            {
                if (IsPrefix(selectText == null ? null : selectText(item), normalized))
                {
                    result.Add(item);
                }
            }

            return result;
        }
    }
}
