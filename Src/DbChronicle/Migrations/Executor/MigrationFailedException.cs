using System.Collections.Generic;

using DbChronicle.Models;

namespace DbChronicle.Migrations.Executor
{
    public sealed class MigrationFailedException : DbChronicleException
    {
        private readonly IReadOnlyList<string> _failures;
        private readonly IReadOnlyList<CompileDiagnostic> _diagnostics;

        public MigrationFailedException(string message, IReadOnlyList<string> failures,
            IReadOnlyList<CompileDiagnostic> diagnostics, int appliedCount, int skippedCount)
            : base(message, 3)
        {
            _failures = failures ?? new string[0];
            _diagnostics = diagnostics ?? new CompileDiagnostic[0];
            AppliedCount = appliedCount;
            SkippedCount = skippedCount;
        }

        public override IReadOnlyList<string> Failures
        {
            get { return _failures; }
        }

        public override IReadOnlyList<CompileDiagnostic> Diagnostics
        {
            get { return _diagnostics; }
        }

        public int AppliedCount { get; private set; }

        public int SkippedCount { get; private set; }
    }
}
