using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;

using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace XmlValueIntelliSense
{
    [Export(typeof(ITaggerProvider))]
    [ContentType("xml")]
    [TagType(typeof(ErrorTag))]
    [Name("XmlValueValidation")]
    [Order(After = "default")]
    internal sealed class XmlValidationTaggerProvider : ITaggerProvider
    {
        [Import]
        internal ITextDocumentFactoryService DocumentFactory { get; set; }

        [Import(AllowDefault = true)]
        internal ITableManagerProvider TableManagerProvider { get; set; }

        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
        {
            if (!FeatureGate.Enabled || buffer == null)
            {
                return null;
            }

            var tagger = buffer.Properties.GetOrCreateSingletonProperty(typeof(XmlValidationTagger),
                () => new XmlValidationTagger(buffer, DocumentFactory, TableManagerProvider));
            return tagger as ITagger<T>;
        }
    }

    internal sealed class XmlValidationTagger : ITagger<ErrorTag>, IDisposable
    {
        private const int DebounceMilliseconds = 400;

        private readonly ITextBuffer _buffer;

        private readonly ITextDocumentFactoryService _documentFactory;

        private readonly ITableManagerProvider _tableManagerProvider;

        private readonly Timer _timer;

        private readonly object _sync = new object();

        private ITextSnapshot _snapshot;

        private IReadOnlyList<XmlValidationIssue> _issues = new List<XmlValidationIssue>();

        private XmlErrorListSource _errorSource;

        private ITableManager _tableManager;

        private bool _disposed;

        public XmlValidationTagger(ITextBuffer buffer, ITextDocumentFactoryService documentFactory,
            ITableManagerProvider tableManagerProvider)
        {
            _buffer = buffer;
            _documentFactory = documentFactory;
            _tableManagerProvider = tableManagerProvider;
            _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
            _buffer.Changed += OnBufferChanged;
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public IEnumerable<ITagSpan<ErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (_disposed || spans == null || spans.Count == 0)
            {
                yield break;
            }

            var snapshot = spans[0].Snapshot;
            var issues = EnsureIssues(snapshot);
            var emitted = new HashSet<int>();
            foreach (var span in spans)
            {
                if (span.Snapshot != snapshot)
                {
                    continue;
                }

                foreach (var issue in issues)
                {
                    if (issue.Length <= 0 || issue.Start < 0 || issue.Start >= snapshot.Length)
                    {
                        continue;
                    }

                    var length = Math.Min(issue.Length, snapshot.Length - issue.Start);
                    var issueSpan = new SnapshotSpan(snapshot, issue.Start, length);
                    if (!issueSpan.IntersectsWith(span))
                    {
                        continue;
                    }

                    if (!emitted.Add(issue.Start))
                    {
                        continue;
                    }

                    yield return new TagSpan<ErrorTag>(issueSpan,
                        new ErrorTag(PredefinedErrorTypeNames.SyntaxError, issue.Message));
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _buffer.Changed -= OnBufferChanged;
            _timer.Dispose();

            var tableManager = _tableManager;
            var source = _errorSource;
            _tableManager = null;
            _errorSource = null;

            if (tableManager != null && source != null)
            {
                try
                {
                    tableManager.RemoveSource(source);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "移除错误列表失败");
                }
            }

            if (source != null)
            {
                source.Dispose();
            }
        }

        private IReadOnlyList<XmlValidationIssue> EnsureIssues(ITextSnapshot snapshot)
        {
            lock (_sync)
            {
                if (_snapshot == snapshot)
                {
                    return _issues;
                }
            }

            Validate(snapshot);
            lock (_sync)
            {
                return _issues;
            }
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _timer.Change(DebounceMilliseconds, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void OnTimer(object state)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var snapshot = _buffer.CurrentSnapshot;
                Validate(snapshot);
                var handler = TagsChanged;
                if (handler != null)
                {
                    handler(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "XML 校验失败");
            }
        }

        private void Validate(ITextSnapshot snapshot)
        {
            var issues = new List<XmlValidationIssue>();
            string path = null;
            try
            {
                if (SnapshotUtil.IsWithinLimit(snapshot))
                {
                    path = GetFilePath();
                    if (!string.IsNullOrEmpty(path))
                    {
                        var schema = SchemaDocumentCache.Get(path, SnapshotUtil.GetHeader(snapshot));
                        if (schema != null)
                        {
                            issues = XmlValueValidator.Validate(snapshot.GetText(), schema).ToList();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "XML 值校验失败");
            }

            lock (_sync)
            {
                _snapshot = snapshot;
                _issues = issues;
            }

            var source = EnsureErrorSource(path);
            if (source != null)
            {
                source.Update(issues, snapshot);
            }

            if (FeatureGate.Verbose)
            {
                Logger.Write("校验：" + (path ?? "") + "，错误=" + issues.Count);
            }
        }

        private XmlErrorListSource EnsureErrorSource(string path)
        {
            if (string.IsNullOrEmpty(path) || _tableManagerProvider == null)
            {
                return null;
            }

            lock (_sync)
            {
                if (_disposed)
                {
                    return null;
                }

                if (_errorSource != null)
                {
                    return _errorSource;
                }

                try
                {
                    var tableManager = _tableManagerProvider.GetTableManager(StandardTables.ErrorsTable);
                    if (tableManager == null)
                    {
                        return null;
                    }

                    var source = new XmlErrorListSource(path);
                    tableManager.AddSource(source,
                        StandardTableColumnDefinitions.DocumentName,
                        StandardTableColumnDefinitions.Line,
                        StandardTableColumnDefinitions.Column,
                        StandardTableColumnDefinitions.ErrorSeverity,
                        StandardTableColumnDefinitions.ErrorCode,
                        StandardTableColumnDefinitions.Text,
                        StandardTableColumnDefinitions.ErrorCategory);
                    _tableManager = tableManager;
                    _errorSource = source;
                    return source;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "注册错误列表失败");
                    return null;
                }
            }
        }

        private string GetFilePath()
        {
            ITextDocument document;
            if (_documentFactory != null && _documentFactory.TryGetTextDocument(_buffer, out document))
            {
                return document.FilePath;
            }

            return null;
        }
    }
}
