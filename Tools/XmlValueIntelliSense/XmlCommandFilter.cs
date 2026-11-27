using System;
using System.ComponentModel.Composition;
using System.Linq;

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;

namespace XmlValueIntelliSense
{
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("xml")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    [Name("XmlValueCommandFilter")]
    internal sealed class XmlCommandFilterProvider : IVsTextViewCreationListener
    {
        [Import]
        internal IVsEditorAdaptersFactoryService AdapterFactory { get; set; }

        [Import]
        internal ICompletionBroker CompletionBroker { get; set; }

        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            if (!FeatureGate.Enabled)
            {
                return;
            }

            var view = AdapterFactory.GetWpfTextView(textViewAdapter);
            if (view == null)
            {
                return;
            }

            XmlCommandFilter existing;
            if (view.Properties.TryGetProperty(typeof(XmlCommandFilter), out existing))
            {
                return;
            }

            var filter = new XmlCommandFilter(view, CompletionBroker);
            view.Properties.GetOrCreateSingletonProperty(typeof(XmlCommandFilter), () => filter);
            try
            {
                IOleCommandTarget next;
                textViewAdapter.AddCommandFilter(filter, out next);
                filter.Next = next;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "添加命令过滤器失败");
                return;
            }

            view.Closed += (sender, args) =>
            {
                try
                {
                    textViewAdapter.RemoveCommandFilter(filter);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "移除命令过滤器失败");
                }
            };
        }
    }

    internal sealed class XmlCommandFilter : IOleCommandTarget
    {
        private readonly IWpfTextView _view;

        private readonly ICompletionBroker _broker;

        public IOleCommandTarget Next { get; set; }

        public XmlCommandFilter(IWpfTextView view, ICompletionBroker broker)
        {
            _view = view;
            _broker = broker;
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            return Next == null
                ? VSConstants.S_OK
                : Next.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (pguidCmdGroup == VSConstants.VSStd2K)
            {
                var command = (VSConstants.VSStd2KCmdID)nCmdID;
                if (command == VSConstants.VSStd2KCmdID.RETURN || command == VSConstants.VSStd2KCmdID.TAB)
                {
                    if (TryCommit())
                    {
                        return VSConstants.S_OK;
                    }
                }
            }

            return Next == null
                ? VSConstants.S_OK
                : Next.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        private bool TryCommit()
        {
            try
            {
                var session = _broker.GetSessions(_view)
                    .FirstOrDefault(item => item.CompletionSets != null
                        && item.CompletionSets.Any(set => set.Moniker == "XmlValueIntelliSense"));
                if (session == null)
                {
                    return false;
                }

                var completionSet = session.CompletionSets.FirstOrDefault(set => set.Moniker == "XmlValueIntelliSense");
                if (completionSet == null)
                {
                    return false;
                }

                if (!IsSelected(completionSet))
                {
                    var value = GetCurrentValue();
                    var best = completionSet.Completions
                        .FirstOrDefault(item => FuzzyMatcher.IsMatch(item.DisplayText, value))
                        ?? completionSet.Completions.FirstOrDefault();
                    if (best == null)
                    {
                        session.Dismiss();
                        return false;
                    }

                    completionSet.SelectionStatus = new CompletionSelectionStatus(best, true, true);
                }

                var committed = completionSet.SelectionStatus == null
                    ? ""
                    : completionSet.SelectionStatus.Completion.DisplayText;
                session.Commit();
                Logger.Write("命令过滤器已提交补全：" + committed);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "回车提交补全失败");
                return false;
            }
        }

        private static bool IsSelected(CompletionSet completionSet)
        {
            return completionSet.SelectionStatus != null
                && completionSet.SelectionStatus.Completion != null
                && completionSet.SelectionStatus.IsSelected;
        }

        private string GetCurrentValue()
        {
            var snapshot = _view.TextSnapshot;
            var context = XmlValueContext.TryCreate(snapshot.GetText(),
                _view.Caret.Position.BufferPosition.Position);
            return context == null ? "" : snapshot.GetText(context.ValueStart, context.ValueLength);
        }
    }
}
