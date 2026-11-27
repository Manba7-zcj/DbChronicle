using System.Collections.Generic;
using System.Linq;
using System;

using DbChronicle.Common.Enums;
using DbChronicle.Configuration;
using DbChronicle.Diagnostics;
using DbChronicle.Migrations.State;
using DbChronicle.Models;

namespace DbChronicle.Compilation.Checks
{
    public static class CapabilityChecker
    {
        public static IReadOnlyList<CompileDiagnostic> Check(IReadOnlyList<AppliedChange> applied,
            DbChronicleConfig config)
        {
            var diagnostics = new List<CompileDiagnostic>();
            var current = config.Database.CapabilityLevel;
            if (string.IsNullOrEmpty(current) || applied == null || applied.Count == 0)
            {
                return diagnostics;
            }

            var missing = applied.Where(x => !x.HasFailure && string.IsNullOrEmpty(x.CapabilityLevel)).ToList();
            if (missing.Count > 0)
            {
                diagnostics.Add(Warning(DiagnosticCodes.CapabilityLevelMissing, null,
                    "状态文件有 " + missing.Count + " 个已应用单元缺少 capabilityLevel（旧版本写入），无法判定能力漂移"));
            }

            var drifted = applied
                .Where(x => !x.HasFailure && !string.IsNullOrEmpty(x.CapabilityLevel)
                    && !string.Equals(x.CapabilityLevel, current, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (drifted.Count == 0)
            {
                return diagnostics;
            }

            var message = "已应用单元的能力级别（" + drifted[0].CapabilityLevel + "）与当前配置（" + current
                + "）不一致：" + drifted.Count + " 个单元，首个 " + drifted[0].ChangeId;
            var strict = string.Equals(config.Options.VersionDriftPolicy, "strict", StringComparison.OrdinalIgnoreCase);
            diagnostics.Add(strict
                ? Error(DiagnosticCodes.CapabilityLevelDrift, drifted[0].ChangeId, message + "；versionDriftPolicy=strict 已拒绝执行")
                : Warning(DiagnosticCodes.CapabilityLevelAudit, drifted[0].ChangeId, message));
            return diagnostics;
        }

        private static CompileDiagnostic Error(string code, string changeId, string message)
        {
            return new CompileDiagnostic
            {
                Code = code,
                Severity = DiagnosticSeverity.Error,
                ChangeId = changeId,
                Message = message
            };
        }

        private static CompileDiagnostic Warning(string code, string changeId, string message)
        {
            return new CompileDiagnostic
            {
                Code = code,
                Severity = DiagnosticSeverity.Warning,
                ChangeId = changeId,
                Message = message
            };
        }
    }
}
