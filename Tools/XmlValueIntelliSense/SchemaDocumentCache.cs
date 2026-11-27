using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XmlValueIntelliSense
{
    internal static class SchemaDocumentCache
    {
        private static readonly ConcurrentDictionary<string, Entry> Entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);

        public static SchemaDocument Get(string xmlPath)
        {
            if (string.IsNullOrEmpty(xmlPath) || !File.Exists(xmlPath))
            {
                return null;
            }

            try
            {
                return Get(xmlPath, File.ReadAllText(xmlPath));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "读取 XML 失败：" + xmlPath);
                return null;
            }
        }

        public static SchemaDocument Get(string xmlPath, string xmlText)
        {
            if (string.IsNullOrEmpty(xmlPath) || string.IsNullOrEmpty(xmlText))
            {
                return null;
            }

            string noNamespaceLocation;
            var namespacedLocations = new List<KeyValuePair<string, string>>();
            if (!SchemaDocument.TryReadLocationsFromText(xmlText, out noNamespaceLocation, namespacedLocations))
            {
                return null;
            }

            var key = BuildKey(xmlPath, noNamespaceLocation, namespacedLocations);
            Entry entry;
            if (Entries.TryGetValue(key, out entry)
                && (entry.Document == null || entry.Document.IsSchemaCurrent()))
            {
                return entry.Document;
            }

            SchemaDocument document = null;
            try
            {
                document = SchemaDocument.TryLoad(xmlPath, noNamespaceLocation, namespacedLocations);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "加载 schema 文档失败");
            }

            Entries[key] = new Entry { Document = document };
            return document;
        }

        private static string BuildKey(string xmlPath, string noNamespaceLocation,
            IEnumerable<KeyValuePair<string, string>> namespacedLocations)
        {
            return xmlPath + "|" + (noNamespaceLocation ?? "")
                + "|" + string.Join(";", namespacedLocations.Select(pair => pair.Key + "=" + pair.Value));
        }

        private sealed class Entry
        {
            public SchemaDocument Document { get; set; }
        }
    }
}
