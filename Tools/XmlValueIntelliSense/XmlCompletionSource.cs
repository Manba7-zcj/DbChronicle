using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;

namespace XmlValueIntelliSense
{
    internal sealed class XmlCompletionSource : ICompletionSource
    {
        private readonly ITextBuffer _buffer;

        private readonly ITextDocumentFactoryService _documentFactory;

        private bool _disposed;

        public XmlCompletionSource(ITextBuffer buffer, ITextDocumentFactoryService documentFactory)
        {
            _buffer = buffer;
            _documentFactory = documentFactory;
        }

        public void AugmentCompletionSession(ICompletionSession session, IList<CompletionSet> completionSets)
        {
            if (_disposed || !FeatureGate.Enabled || session == null || completionSets == null)
            {
                return;
            }

            try
            {
                var view = session.TextView;
                if (view == null || view.TextBuffer != _buffer)
                {
                    return;
                }

                var snapshot = view.TextSnapshot;
                if (!SnapshotUtil.IsWithinLimit(snapshot))
                {
                    return;
                }

                var caret = view.Caret.Position.BufferPosition.Position;
                var context = XmlValueContext.TryCreate(snapshot.GetText(), caret);
                if (context == null)
                {
                    return;
                }

                var path = GetFilePath();
                var schema = SchemaDocumentCache.Get(path, SnapshotUtil.GetHeader(snapshot));
                if (schema == null)
                {
                    return;
                }

                var values = schema.GetValues(context);
                if (values.Count == 0)
                {
                    return;
                }

                var filter = snapshot.GetText(context.ValueStart, context.ValueLength);
                values = FuzzyMatcher.Filter(values, value => value.Value, filter);
                if (values.Count == 0)
                {
                    return;
                }

                var span = snapshot.CreateTrackingSpan(context.ValueStart, context.ValueLength,
                    SpanTrackingMode.EdgeInclusive);
                var completions = values
                    .Select(value => new Completion(value.Value, value.Value, value.Description, null, null))
                    .ToList();

                completionSets.Clear();
                completionSets.Add(new FuzzyCompletionSet("XmlValueIntelliSense", "XML Values", span, completions));
                if (FeatureGate.Verbose)
                {
                    Logger.Write("补全：" + context.ElementName + "/" + context.AttributeName
                        + "，候选=" + completions.Count);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "构建补全列表失败");
            }
        }

        public void Dispose()
        {
            _disposed = true;
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
