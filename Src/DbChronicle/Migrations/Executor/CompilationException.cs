using System.Collections.Generic;

using DbChronicle.Models;

namespace DbChronicle.Migrations.Executor
{
    public sealed class CompilationException : DbChronicleException
    {
        private readonly IReadOnlyList<CompileDiagnostic> _diagnostics;

        public CompilationException(string message, IReadOnlyList<CompileDiagnostic> diagnostics)
            : base(message, 1)
        {
            _diagnostics = diagnostics ?? new CompileDiagnostic[0];
        }

        public override IReadOnlyList<CompileDiagnostic> Diagnostics
        {
            get { return _diagnostics; }
        }
    }
}
