using System.Collections.Generic;

using DbChronicle.Models;

namespace DbChronicle.Migrations.Executor
{
    public sealed class ChainValidationException : DbChronicleException
    {
        private readonly IReadOnlyList<CompileDiagnostic> _diagnostics;

        public ChainValidationException(string message, IReadOnlyList<CompileDiagnostic> diagnostics, int exitCode)
            : base(message, exitCode)
        {
            _diagnostics = diagnostics ?? new CompileDiagnostic[0];
        }

        public override IReadOnlyList<CompileDiagnostic> Diagnostics
        {
            get { return _diagnostics; }
        }
    }
}
