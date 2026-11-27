using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows.Threading;

using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace XmlValueIntelliSense
{
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("xml")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    [Name("XmlValueCompletionTrigger")]
    internal sealed class XmlValueCompletionTriggerProvider : IWpfTextViewCreationListener
    {
        [Import]
        internal ICompletionBroker CompletionBroker { get; set; }

        [Import]
        internal ITextDocumentFactoryService DocumentFactory { get; set; }

        public void TextViewCreated(IWpfTextView textView)
        {
            if (!FeatureGate.Enabled)
            {
                return;
            }

            new XmlValueCompletionTrigger(textView, CompletionBroker, DocumentFactory);
        }
    }

    internal sealed class XmlValueCompletionTrigger
    {
        private readonly IWpfTextView _view;

        private readonly ICompletionBroker _broker;

        private readonly ITextDocumentFactoryService _documentFactory;

        private readonly DispatcherTimer _timer;

        private int _suppressedVersion = -1;

        private bool _closed;

        private bool _ignoreDismiss;

        private bool _restartPending;

        private bool _committed;

        private bool _dismissing;

        public XmlValueCompletionTrigger(IWpfTextView view, ICompletionBroker broker,
            ITextDocumentFactoryService documentFactory)
        {
            _view = view;
            _broker = broker;
            _documentFactory = documentFactory;
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            _timer.Tick += OnTimerTick;
            _view.TextBuffer.Changed += OnBufferChanged;
            _view.Closed += OnViewClosed;
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (_closed || e == null || e.Changes == null || e.Changes.Count == 0)
            {
                return;
            }

            if (_committed)
            {
                _committed = false;
                _timer.Stop();
                _suppressedVersion = _view.TextSnapshot.Version.VersionNumber;
                return;
            }

            var deletion = e.Changes.All(change => change.NewLength == 0 && change.OldLength > 0);
            if (deletion)
            {
                _timer.Stop();
                _restartPending = false;
                _ignoreDismiss = false;
                DismissOurSessions();
                _suppressedVersion = _view.TextSnapshot.Version.VersionNumber;
                return;
            }

            _restartPending = true;
            _ignoreDismiss = true;
            DismissOurSessions();
            _timer.Stop();
            _timer.Start();
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            _timer.Stop();
            _ignoreDismiss = false;
            if (_restartPending)
            {
                _restartPending = false;
                _suppressedVersion = -1;
            }

            if (!FeatureGate.Enabled || _closed || !_view.HasAggregateFocus || _view.IsClosed)
            {
                return;
            }

            try
            {
                if (_view.Selection.SelectedSpans.Any(span => span.Length > 0))
                {
                    return;
                }

                if (_broker == null || _broker.GetSessions(_view).Count > 0)
                {
                    return;
                }

                var snapshot = _view.TextSnapshot;
                if (!SnapshotUtil.IsWithinLimit(snapshot))
                {
                    return;
                }

                if (_suppressedVersion == snapshot.Version.VersionNumber)
                {
                    return;
                }

                var caret = _view.Caret.Position.BufferPosition.Position;
                var context = XmlValueContext.TryCreate(snapshot.GetText(), caret);
                if (context == null)
                {
                    return;
                }

                var path = GetFilePath();
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }

                var schema = SchemaDocumentCache.Get(path, SnapshotUtil.GetHeader(snapshot));
                if (schema == null || !schema.HasValues(context))
                {
                    return;
                }

                var session = _broker.TriggerCompletion(_view);
                if (session != null)
                {
                    session.Dismissed += OnSessionDismissed;
                    session.Committed += OnSessionCommitted;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "自动触发补全失败");
            }
        }

        private void DismissOurSessions()
        {
            if (_broker == null || _dismissing)
            {
                return;
            }

            _dismissing = true;
            try
            {
                foreach (var session in _broker.GetSessions(_view))
                {
                    if (session == null || session.CompletionSets == null)
                    {
                        continue;
                    }

                    if (session.CompletionSets.Any(set => set.Moniker == "XmlValueIntelliSense"))
                    {
                        session.Dismiss();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "关闭补全会话失败");
            }
            finally
            {
                _dismissing = false;
            }
        }

        private void OnSessionDismissed(object sender, EventArgs e)
        {
            var session = sender as ICompletionSession;
            if (session != null)
            {
                session.Dismissed -= OnSessionDismissed;
                session.Committed -= OnSessionCommitted;
            }

            if (_ignoreDismiss)
            {
                return;
            }

            _suppressedVersion = _view.TextSnapshot.Version.VersionNumber;
        }

        private void OnSessionCommitted(object sender, EventArgs e)
        {
            var session = sender as ICompletionSession;
            if (session != null)
            {
                session.Committed -= OnSessionCommitted;
                session.Dismissed -= OnSessionDismissed;
            }

            _committed = true;
            _timer.Stop();
            _suppressedVersion = _view.TextSnapshot.Version.VersionNumber;
        }

        private string GetFilePath()
        {
            ITextDocument document;
            if (_documentFactory != null && _documentFactory.TryGetTextDocument(_view.TextBuffer, out document))
            {
                return document.FilePath;
            }

            return null;
        }

        private void OnViewClosed(object sender, EventArgs e)
        {
            _closed = true;
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
            _view.TextBuffer.Changed -= OnBufferChanged;
            _view.Closed -= OnViewClosed;
        }
    }
}
