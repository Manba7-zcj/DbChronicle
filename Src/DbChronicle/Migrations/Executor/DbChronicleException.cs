using System.Collections.Generic;

using System;

using DbChronicle.Models;

namespace DbChronicle.Migrations.Executor
{
    public abstract class DbChronicleException : Exception
    {
        private static readonly IReadOnlyList<CompileDiagnostic> NoDiagnostics = new CompileDiagnostic[0];
        private static readonly IReadOnlyList<string> NoFailures = new string[0];

        protected DbChronicleException(string message, int exitCode, Exception inner = null)
            : base(message, inner)
        {
            ExitCode = exitCode;
        }

        public int ExitCode { get; private set; }

        public virtual IReadOnlyList<CompileDiagnostic> Diagnostics
        {
            get { return NoDiagnostics; }
        }

        public virtual IReadOnlyList<string> Failures
        {
            get { return NoFailures; }
        }
    }
}
