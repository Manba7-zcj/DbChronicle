using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;

namespace XmlValueIntelliSense
{
    internal sealed class FuzzyCompletionSet : CompletionSet
    {
        private readonly List<Completion> _allCompletions;

        public FuzzyCompletionSet(string moniker, string displayName, ITrackingSpan applicableTo,
            IEnumerable<Completion> completions)
            : base(moniker, displayName, applicableTo, completions, null)
        {
            _allCompletions = completions.ToList();
        }

        public override void Filter()
        {
            var matched = FuzzyMatcher.Filter(_allCompletions, completion => completion.DisplayText, GetFilterText());

            WritableCompletions.Clear();
            foreach (var completion in matched)
            {
                WritableCompletions.Add(completion);
            }

            if (WritableCompletions.Count > 0)
            {
                SelectionStatus = new CompletionSelectionStatus(WritableCompletions[0], true, true);
            }
        }

        private string GetFilterText()
        {
            if (ApplicableTo == null || ApplicableTo.TextBuffer == null)
            {
                return "";
            }

            return ApplicableTo.GetText(ApplicableTo.TextBuffer.CurrentSnapshot) ?? "";
        }
    }
}
