using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;

namespace XmlValueIntelliSense
{
    internal sealed class XmlErrorListSource : ITableDataSource, IDisposable
    {
        private readonly object _sync = new object();

        private readonly List<ITableDataSink> _sinks = new List<ITableDataSink>();

        private readonly string _documentPath;

        private XmlErrorSnapshot _snapshot;

        private int _version;

        private bool _disposed;

        public XmlErrorListSource(string documentPath)
        {
            _documentPath = documentPath ?? "";
            _snapshot = new XmlErrorSnapshot(new List<XmlErrorEntry>(), 0);
        }

        public string SourceTypeIdentifier
        {
            get { return StandardTableDataSources.ErrorTableDataSource; }
        }

        public string Identifier
        {
            get { return "XmlValueIntelliSense|" + _documentPath; }
        }

        public string DisplayName
        {
            get
            {
                return string.IsNullOrEmpty(_documentPath)
                    ? "XML Value IntelliSense"
                    : Path.GetFileName(_documentPath);
            }
        }

        public IDisposable Subscribe(ITableDataSink sink)
        {
            if (sink == null)
            {
                return new Subscription(null, null);
            }

            lock (_sync)
            {
                if (_disposed)
                {
                    return new Subscription(null, null);
                }

                _sinks.Add(sink);
                sink.AddSnapshot(_snapshot, true);
            }

            return new Subscription(this, sink);
        }

        public void Update(IReadOnlyList<XmlValidationIssue> issues, ITextSnapshot snapshot)
        {
            var entries = new List<XmlErrorEntry>();
            if (snapshot != null && issues != null)
            {
                foreach (var issue in issues)
                {
                    if (issue == null || issue.Start < 0 || issue.Start >= snapshot.Length)
                    {
                        continue;
                    }

                    var line = snapshot.GetLineFromPosition(issue.Start);
                    entries.Add(new XmlErrorEntry(_documentPath, line.LineNumber,
                        issue.Start - line.Start.Position, issue.Message));
                }
            }

            XmlErrorSnapshot previous;
            XmlErrorSnapshot next;
            ITableDataSink[] sinks;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                previous = _snapshot;
                next = new XmlErrorSnapshot(entries, _version + 1);
                _version = next.VersionNumber;
                _snapshot = next;
                sinks = _sinks.ToArray();
            }

            foreach (var sink in sinks)
            {
                try
                {
                    sink.ReplaceSnapshot(previous, next);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "更新错误列表失败");
                }
            }
        }

        public void Dispose()
        {
            ITableDataSink[] sinks;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                sinks = _sinks.ToArray();
                _sinks.Clear();
            }

            foreach (var sink in sinks)
            {
                try
                {
                    sink.RemoveAllSnapshots();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "清除错误列表失败");
                }
            }
        }

        private void Unsubscribe(ITableDataSink sink)
        {
            lock (_sync)
            {
                _sinks.Remove(sink);
            }
        }

        private sealed class Subscription : IDisposable
        {
            private XmlErrorListSource _source;

            private ITableDataSink _sink;

            public Subscription(XmlErrorListSource source, ITableDataSink sink)
            {
                _source = source;
                _sink = sink;
            }

            public void Dispose()
            {
                var source = _source;
                _source = null;
                if (source != null)
                {
                    source.Unsubscribe(_sink);
                }
            }
        }
    }

    internal sealed class XmlErrorEntry
    {
        public XmlErrorEntry(string documentPath, int line, int column, string message)
        {
            DocumentPath = documentPath ?? "";
            Line = line;
            Column = column;
            Message = message ?? "";
        }

        public string DocumentPath { get; private set; }

        public int Line { get; private set; }

        public int Column { get; private set; }

        public string Message { get; private set; }

        public bool SameAs(XmlErrorEntry other)
        {
            return other != null
                && Line == other.Line
                && Column == other.Column
                && string.Equals(Message, other.Message, StringComparison.Ordinal)
                && string.Equals(DocumentPath, other.DocumentPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class XmlErrorSnapshot : ITableEntriesSnapshot
    {
        private const string ErrorCode = "XMLV001";

        private const string ErrorCategory = "XML";

        private readonly IReadOnlyList<XmlErrorEntry> _entries;

        public XmlErrorSnapshot(IReadOnlyList<XmlErrorEntry> entries, int versionNumber)
        {
            _entries = entries ?? new List<XmlErrorEntry>();
            VersionNumber = versionNumber;
        }

        public int Count
        {
            get { return _entries.Count; }
        }

        public int VersionNumber { get; private set; }

        public void StartCaching()
        {
        }

        public void StopCaching()
        {
        }

        public bool TryGetValue(int index, string keyName, out object content)
        {
            content = null;
            if (index < 0 || index >= _entries.Count || keyName == null)
            {
                return false;
            }

            var entry = _entries[index];
            switch (keyName)
            {
                case StandardTableKeyNames.DocumentName:
                    content = entry.DocumentPath;
                    return true;
                case StandardTableKeyNames.Line:
                    content = entry.Line;
                    return true;
                case StandardTableKeyNames.Column:
                    content = entry.Column;
                    return true;
                case StandardTableKeyNames.Text:
                    content = entry.Message;
                    return true;
                case StandardTableKeyNames.ErrorSeverity:
                    content = __VSERRORCATEGORY.EC_ERROR;
                    return true;
                case StandardTableKeyNames.ErrorCategory:
                    content = ErrorCategory;
                    return true;
                case StandardTableKeyNames.ErrorCode:
                    content = ErrorCode;
                    return true;
                case StandardTableKeyNames.BuildTool:
                    content = "XML Value IntelliSense";
                    return true;
                default:
                    return false;
            }
        }

        public int IndexOf(int currentIndex, ITableEntriesSnapshot newSnapshot)
        {
            if (currentIndex < 0 || currentIndex >= _entries.Count)
            {
                return -1;
            }

            var other = newSnapshot as XmlErrorSnapshot;
            if (other == null)
            {
                return -1;
            }

            var entry = _entries[currentIndex];
            for (var index = 0; index < other._entries.Count; index++)
            {
                if (other._entries[index].SameAs(entry))
                {
                    return index;
                }
            }

            return -1;
        }

        public void Dispose()
        {
        }
    }
}
