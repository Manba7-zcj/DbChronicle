using System.Collections.Generic;

using DbChronicle.Configuration;
using DbChronicle.Migrations.State;
using DbChronicle.Models;

namespace DbChronicle.Compilation.Checks
{
    public static class ChainValidator
    {
        public static IReadOnlyList<CompileDiagnostic> Validate(IReadOnlyList<ChangeUnit> units, DbChronicleConfig config)
        {
            var diagnostics = new List<CompileDiagnostic>();
            diagnostics.AddRange(new ChangeChecker(config.Options.StrictReferenceCheck).Check(units));
            diagnostics.AddRange(ReleaseChecker.Check(units, config));
            return diagnostics;
        }

        public static IReadOnlyList<CompileDiagnostic> ValidateRuntime(IReadOnlyList<ChangeUnit> units,
            DbChronicleConfig config, IReadOnlyList<AppliedChange> applied)
        {
            var diagnostics = new List<CompileDiagnostic>();
            diagnostics.AddRange(CapabilityChecker.Check(applied, config));
            diagnostics.AddRange(ReleaseChecker.CheckHoles(config, applied));
            diagnostics.AddRange(ReleaseChecker.CheckOrder(units, config, applied));
            return diagnostics;
        }
    }
}
