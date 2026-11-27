using System.ComponentModel.Composition;

using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace XmlValueIntelliSense
{
    [Export(typeof(ICompletionSourceProvider))]
    [ContentType("xml")]
    [Name("XmlValueCompletion")]
    [Order(After = "default")]
    internal sealed class XmlCompletionSourceProvider : ICompletionSourceProvider
    {
        [Import]
        internal ITextDocumentFactoryService DocumentFactory { get; set; }

        public ICompletionSource TryCreateCompletionSource(ITextBuffer textBuffer)
        {
            if (!FeatureGate.Enabled)
            {
                return null;
            }

            return new XmlCompletionSource(textBuffer, DocumentFactory);
        }
    }
}
