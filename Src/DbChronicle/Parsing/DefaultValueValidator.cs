using System;
using System.Text.RegularExpressions;

using DbChronicle.Common.Enums;
using DbChronicle.Models;

namespace DbChronicle.Parsing
{
    internal static class DefaultValueValidator
    {
        private static readonly Regex FunctionPattern =
            new Regex(@"^[A-Za-z_][A-Za-z0-9_]*\s*\([\s\S]*\)$", RegexOptions.Compiled);

        private static readonly Regex IntegerPattern =
            new Regex(@"^[+-]?[0-9]+$", RegexOptions.Compiled);

        private static readonly Regex NumberPattern =
            new Regex(@"^[+-]?([0-9]+(\.[0-9]*)?|\.[0-9]+)([eE][+-]?[0-9]+)?$", RegexOptions.Compiled);

        public static bool TryValidate(LogicalType type, DefaultValueSpec spec, out string message)
        {
            message = null;
            if (spec == null || !spec.IsDeclared || spec.IsNone || spec.IsNullLiteral)
            {
                return true;
            }

            var raw = spec.RawValue == null ? "" : spec.RawValue.Trim();

            if (type == LogicalType.Boolean)
            {
                if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                message = "列类型为 Boolean，defaultValue 只能是 true / false，实际：" + Describe(raw);
                return false;
            }

            if (!IsNumericType(type))
            {
                return true;
            }

            if (raw.Length == 0)
            {
                message = "列类型 " + type + " 是数值类型，defaultValue 不能为空（会被当成空字符串）";
                return false;
            }

            if (IsExpression(raw))
            {
                return true;
            }

            if (IntegerPattern.IsMatch(raw))
            {
                return true;
            }

            if (NumberPattern.IsMatch(raw))
            {
                if (IsIntegerType(type))
                {
                    message = "列类型 " + type + " 是整数类型，defaultValue 必须是整数，实际：" + Describe(raw);
                    return false;
                }

                return true;
            }

            if (IsQuoted(raw))
            {
                message = "列类型 " + type + " 是数值类型，defaultValue 不能是字符串字面量：" + Describe(raw);
                return false;
            }

            message = "列类型 " + type + " 是数值类型，defaultValue 必须是数字，实际：" + Describe(raw);
            return false;
        }

        private static bool IsNumericType(LogicalType type)
        {
            return IsIntegerType(type)
                || type == LogicalType.Float32
                || type == LogicalType.Float64
                || type == LogicalType.Decimal;
        }

        private static bool IsIntegerType(LogicalType type)
        {
            return type == LogicalType.Int8 || type == LogicalType.Int16
                || type == LogicalType.Int32 || type == LogicalType.Int64;
        }

        private static bool IsQuoted(string text)
        {
            return text.Length >= 2 && text[0] == '\'' && text[text.Length - 1] == '\'';
        }

        private static bool IsExpression(string text)
        {
            if (FunctionPattern.IsMatch(text))
            {
                return true;
            }

            if (text.StartsWith("CURRENT_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(text, "SYSUTCDATETIME", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return text.Length >= 2 && text[0] == '(' && text[text.Length - 1] == ')';
        }

        private static string Describe(string text)
        {
            return text.Length == 0 ? "空字符串" : "\"" + text + "\"";
        }
    }
}
