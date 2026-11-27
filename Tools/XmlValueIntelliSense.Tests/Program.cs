using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XmlValueIntelliSense.Tests
{
    internal static class Program
    {
        private static int _failures;

        private static int Main(string[] args)
        {
            var xmlPath = args.Length > 0
                ? args[0]
                : @"E:\4.MyProject\SnibeMiddleware\SnibeMiddleware-New-Dev2\resource\update\DataBase\20190516_000000.xml";

            if (!File.Exists(xmlPath))
            {
                Console.WriteLine("XML not found: " + xmlPath);
                return 2;
            }

            var text = File.ReadAllText(xmlPath);
            var schema = SchemaDocument.TryLoad(xmlPath);
            Check(schema != null, "schema loaded");

            CheckContext(text, "action=\"MODIFY\"", "column", "action", "MODIFY");
            CheckContext(text, "type=\"Int32\"", "column", "type", "Int32");
            CheckContext(text, "nullable=\"false\"", "column", "nullable", "false");
            CheckContext(text, "action=\"ALTER\"", "table", "action", "ALTER");

            var columnAction = GetValues(schema, text, "action=\"MODIFY\"");
            Check(columnAction.Count == 6, "column action has 6 values, actual=" + columnAction.Count);
            Check(columnAction.Any(x => x.Value == "MODIFY"), "column action contains MODIFY");
            var modify = columnAction.First(x => x.Value == "MODIFY");
            Check(modify.Description.Contains("修改列"), "MODIFY has per-value doc: " + modify.Description);
            var drop = columnAction.First(x => x.Value == "DROP");
            Check(drop.Description.Contains("删除列"), "DROP has per-value doc: " + drop.Description);
            Check(columnAction.All(x => !string.IsNullOrWhiteSpace(x.Description)
                && !x.Description.Contains("列级动作，缺省")), "all column actions have specific docs");

            var logicalType = GetValues(schema, text, "type=\"Int32\"");
            Check(logicalType.Count == 20, "logical type has 20 values, actual=" + logicalType.Count);
            Check(logicalType.Any(x => x.Value == "DateTimeOffset"), "logical type contains DateTimeOffset");
            var int32 = logicalType.First(x => x.Value == "Int32");
            Check(int32.Description.Contains("32 位"), "Int32 has per-value doc: " + int32.Description);
            Check(logicalType.All(x => !string.IsNullOrWhiteSpace(x.Description)
                && !x.Description.Contains("逻辑类型（区分大小写）")), "all logical types have specific docs");

            var nullable = GetValues(schema, text, "nullable=\"false\"");
            Check(nullable.Count == 2, "nullable has 2 values, actual=" + nullable.Count);

            var tableAction = GetValues(schema, text, "action=\"ALTER\"");
            Check(tableAction.Count == 9, "table action has 9 values, actual=" + tableAction.Count);

            var rootRelease = GetValues(schema, text, "release=\"20190516\"");
            Check(rootRelease.Count == 0, "release has no enum values");

            var cached = SchemaDocumentCache.Get(xmlPath, text);
            Check(cached != null, "schema resolved from buffer text");

            var missingSchemaText = text.Replace("DbChronicle.ChangeUnit.xsd", "Missing.ChangeUnit.xsd");
            var missingSchema = SchemaDocumentCache.Get(xmlPath, missingSchemaText);
            Check(missingSchema == null, "schema declaration change is picked up without saving");

            var textNode = text.IndexOf("<description>", StringComparison.Ordinal);
            if (textNode >= 0)
            {
                var context = XmlValueContext.TryCreate(text, textNode + "<description>".Length + 1);
                Check(context == null, "text node is not an attribute value");
            }

            var comment = text.IndexOf("<!--", StringComparison.Ordinal);
            if (comment >= 0)
            {
                var context = XmlValueContext.TryCreate(text, comment + 2);
                Check(context == null, "comment is not an attribute value");
            }

            var validSample = "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                + "<changeUnit id=\"x\" author=\"a\" release=\"20200101\">"
                + "<table name=\"t\" action=\"ALTER\" comment=\"ok\"/></changeUnit>";
            var validIssues = XmlValueValidator.Validate(validSample, schema);
            Check(validIssues.Count == 0, "valid sample has no errors, actual=" + validIssues.Count);

            var emptyActionSample = validSample.Replace("action=\"ALTER\"", "action=\"\"");
            var emptyActionIssues = XmlValueValidator.Validate(emptyActionSample, schema);
            Check(emptyActionIssues.Count == 1, "empty action is reported, actual=" + emptyActionIssues.Count);
            if (emptyActionIssues.Count == 1)
            {
                Check(emptyActionIssues[0].Length == 2, "empty action span covers quotes, actual="
                    + emptyActionIssues[0].Length);
                Check(emptyActionIssues[0].Message.Contains("不能为空"),
                    "empty action message: " + emptyActionIssues[0].Message);
                Check(emptyActionIssues[0].Message.Contains("ALTER"),
                    "empty action message lists allowed values: " + emptyActionIssues[0].Message);
            }

            var emptyNameSample = validSample.Replace("name=\"t\"", "name=\"\"");
            var emptyNameIssues = XmlValueValidator.Validate(emptyNameSample, schema);
            Check(emptyNameIssues.Count == 1, "empty required name is reported, actual=" + emptyNameIssues.Count);

            var emptyCommentSample = validSample.Replace("comment=\"ok\"", "comment=\"\"");
            var emptyCommentIssues = XmlValueValidator.Validate(emptyCommentSample, schema);
            Check(emptyCommentIssues.Count == 0, "empty optional comment is not reported, actual="
                + emptyCommentIssues.Count);

            var commentOnlySample = "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                + "<changeUnit id=\"x\" author=\"a\" release=\"20200101\"><!-- "
                + "<table name=\"t\" action=\"\"/> --></changeUnit>";
            var commentOnlyIssues = XmlValueValidator.Validate(commentOnlySample, schema);
            Check(commentOnlyIssues.Count == 0, "comment content is ignored, actual=" + commentOnlyIssues.Count);

            var emptyBooleanSample = "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                + "<changeUnit id=\"x\" author=\"a\" release=\"20200101\">"
                + "<table name=\"t\" action=\"ALTER\"><column name=\"c\" action=\"ADD\" type=\"Int32\" nullable=\"\"/>"
                + "</table></changeUnit>";
            var emptyBooleanIssues = XmlValueValidator.Validate(emptyBooleanSample, schema);
            Check(emptyBooleanIssues.Count == 1, "empty boolean is reported, actual=" + emptyBooleanIssues.Count);
            if (emptyBooleanIssues.Count == 1)
            {
                Check(emptyBooleanIssues[0].Message.Contains("true"),
                    "empty boolean message lists allowed values: " + emptyBooleanIssues[0].Message);
            }

            var invalidActionSample = validSample.Replace("action=\"ALTER\"", "action=\"BOGUS\"");
            var invalidActionIssues = XmlValueValidator.Validate(invalidActionSample, schema);
            Check(invalidActionIssues.Count == 1, "non-empty invalid action is reported, actual="
                + invalidActionIssues.Count);
            if (invalidActionIssues.Count == 1)
            {
                Check(invalidActionIssues[0].Message.Contains("不是有效值"),
                    "invalid action message: " + invalidActionIssues[0].Message);
            }

            var columnSample = "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                + "<changeUnit id=\"x\" author=\"a\" release=\"20200101\">"
                + "<table name=\"t\" action=\"ALTER\"><column name=\"c\" action=\"ADD\" type=\"Int32\" nullable=\"true\"/>"
                + "</table></changeUnit>";
            Check(XmlValueValidator.Validate(columnSample, schema).Count == 0,
                "valid column sample has no errors");

            var invalidTypeSample = columnSample.Replace("type=\"Int32\"", "type=\"1\"");
            var invalidTypeIssues = XmlValueValidator.Validate(invalidTypeSample, schema);
            Check(invalidTypeIssues.Count == 1, "invalid type value is reported, actual=" + invalidTypeIssues.Count);
            if (invalidTypeIssues.Count == 1)
            {
                Check(invalidTypeIssues[0].Message.Contains("\"1\"")
                    && invalidTypeIssues[0].Message.Contains("Int32"),
                    "invalid type message: " + invalidTypeIssues[0].Message);
            }

            var wrongCaseTypeSample = columnSample.Replace("type=\"Int32\"", "type=\"int32\"");
            var wrongCaseTypeIssues = XmlValueValidator.Validate(wrongCaseTypeSample, schema);
            Check(wrongCaseTypeIssues.Count == 1, "enum values are case sensitive, actual="
                + wrongCaseTypeIssues.Count);

            var invalidNullableSample = columnSample.Replace("nullable=\"true\"", "nullable=\"yes\"");
            var invalidNullableIssues = XmlValueValidator.Validate(invalidNullableSample, schema);
            Check(invalidNullableIssues.Count == 1, "invalid boolean value is reported, actual="
                + invalidNullableIssues.Count);

            Check(FuzzyMatcher.IsMatch("Int8", "in"), "fuzzy in matches Int8");
            Check(FuzzyMatcher.IsMatch("UInt16", "in"), "fuzzy in matches UInt16");
            Check(!FuzzyMatcher.IsMatch("Boolean", "in"), "fuzzy in rejects Boolean");
            Check(!FuzzyMatcher.IsMatch("Float32", "in"), "fuzzy in rejects Float32");
            Check(!FuzzyMatcher.IsMatch("Decimal", "in"), "fuzzy in rejects Decimal");
            Check(FuzzyMatcher.IsMatch("FixedString", "in"), "fuzzy in matches FixedString by subsequence");
            Check(FuzzyMatcher.IsPrefix("Int8", "in"), "prefix in matches Int8");
            Check(!FuzzyMatcher.IsPrefix("UInt16", "in"), "prefix in rejects UInt16");
            Check(!FuzzyMatcher.IsPrefix("FixedString", "in"), "prefix in rejects FixedString");
            Check(FuzzyMatcher.IsMatch("Int32", "i32"), "fuzzy i32 matches Int32");
            Check(FuzzyMatcher.IsMatch("MODIFY", "mfy"), "fuzzy mfy matches MODIFY");
            Check(FuzzyMatcher.Normalize("in") == "in", "normalize keeps in");
            Check(FuzzyMatcher.Normalize("  in") == "in", "normalize trims leading spaces");
            Check(FuzzyMatcher.Normalize("in ") == "", "normalize clears trailing space");

            var fuzzyValues = new List<string> { "Boolean", "Int8", "Int16", "UInt16", "FixedString", "MODIFY" };
            var filteredPrefix = FuzzyMatcher.Filter(fuzzyValues, item => item, "in");
            Check(filteredPrefix.Count == 2 && filteredPrefix[0] == "Int8" && filteredPrefix[1] == "Int16",
                "filter in keeps prefix matches only, actual=" + string.Join(",", filteredPrefix));
            var filteredSubsequence = FuzzyMatcher.Filter(fuzzyValues, item => item, "mfy");
            Check(filteredSubsequence.Count == 1 && filteredSubsequence[0] == "MODIFY",
                "filter mfy falls back to subsequence, actual=" + string.Join(",", filteredSubsequence));
            Check(FuzzyMatcher.Filter(fuzzyValues, item => item, "").Count == fuzzyValues.Count,
                "empty filter keeps all values");

            Console.WriteLine(_failures == 0 ? "ALL PASSED" : ("FAILED: " + _failures));
            return _failures == 0 ? 0 : 1;
        }

        private static List<SchemaValue> GetValues(SchemaDocument schema, string text, string marker)
        {
            var position = text.IndexOf(marker, StringComparison.Ordinal);
            if (position < 0)
            {
                return new List<SchemaValue>();
            }

            var context = XmlValueContext.TryCreate(text, position + marker.IndexOf('=') + 2);
            if (context == null || schema == null)
            {
                return new List<SchemaValue>();
            }

            return schema.GetValues(context).ToList();
        }

        private static void CheckContext(string text, string marker, string elementName, string attributeName,
            string value)
        {
            var position = text.IndexOf(marker, StringComparison.Ordinal);
            if (position < 0)
            {
                Check(false, "marker not found: " + marker);
                return;
            }

            var valuePosition = position + marker.IndexOf('=') + 2;
            var context = XmlValueContext.TryCreate(text, valuePosition);
            Check(context != null, "context for " + marker);
            if (context == null)
            {
                return;
            }

            Check(context.ElementName == elementName, "element for " + marker + " = " + context.ElementName);
            Check(context.AttributeName == attributeName, "attribute for " + marker + " = " + context.AttributeName);
            Check(text.Substring(context.ValueStart, context.ValueLength) == value,
                "value for " + marker + " = " + text.Substring(context.ValueStart, context.ValueLength));
        }

        private static void Check(bool condition, string message)
        {
            if (condition)
            {
                Console.WriteLine("PASS " + message);
                return;
            }

            _failures++;
            Console.WriteLine("FAIL " + message);
        }
    }
}
