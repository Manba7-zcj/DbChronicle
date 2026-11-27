using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System;

using DbChronicle.Common.Enums;
using DbChronicle.Configuration;
using DbChronicle.Diagnostics;
using DbChronicle.Migrations.State;
using DbChronicle.Models;

namespace DbChronicle.Compilation.Checks
{
    public static class ReleaseChecker
    {
        public static IReadOnlyList<CompileDiagnostic> Check(IReadOnlyList<ChangeUnit> units, DbChronicleConfig config)
        {
            var diagnostics = new List<CompileDiagnostic>();
            CheckLateSubmission(units, config, diagnostics);
            CheckManifests(units, config, diagnostics);
            return diagnostics;
        }

        public static IReadOnlyList<CompileDiagnostic> CheckHoles(DbChronicleConfig config,
            IReadOnlyList<AppliedChange> applied)
        {
            var diagnostics = new List<CompileDiagnostic>();
            var store = new ReleaseManifestStore(config);
            var appliedIds = new HashSet<string>(
                applied.Where(x => !x.HasFailure).Select(x => x.ChangeId),
                StringComparer.OrdinalIgnoreCase);

            foreach (var manifest in store.LoadAll())
            {
                if (manifest.Units.Count == 0)
                {
                    continue;
                }

                var missing = manifest.Units.Where(x => !appliedIds.Contains(x.Id)).Select(x => x.Id).ToList();
                if (missing.Count == 0 || missing.Count == manifest.Units.Count)
                {
                    continue;
                }

                diagnostics.Add(Error(DiagnosticCodes.ReleaseHole, null, null,
                    "版本空洞：release " + manifest.Name + " 已应用 " + (manifest.Units.Count - missing.Count)
                    + "/" + manifest.Units.Count + "，缺失：" + string.Join(", ", missing)));
            }

            return diagnostics;
        }

        public static IReadOnlyList<CompileDiagnostic> CheckOrder(IReadOnlyList<ChangeUnit> units,
            DbChronicleConfig config, IReadOnlyList<AppliedChange> applied)
        {
            var diagnostics = new List<CompileDiagnostic>();
            AppliedChange max = null;
            foreach (var record in applied.Where(x => !x.HasFailure && !string.IsNullOrEmpty(x.Release)))
            {
                if (max == null || Compare(record.Release, record.ChangeId, max.Release, max.ChangeId) > 0)
                {
                    max = record;
                }
            }

            if (max == null)
            {
                return diagnostics;
            }

            var appliedIds = new HashSet<string>(
                applied.Where(x => !x.HasFailure).Select(x => x.ChangeId),
                StringComparer.OrdinalIgnoreCase);
            var pending = units
                .Where(x => !appliedIds.Contains(x.Id) && !x.IsBaseline && !string.IsNullOrEmpty(x.Release)
                    && Compare(x.Release, x.Id, max.Release, max.ChangeId) < 0)
                .ToList();

            foreach (var unit in pending)
            {
                var message = "升级顺序倒挂：待应用单元 " + unit.Id + "（release " + unit.Release
                    + "）低于已应用最高版本 " + max.Release + " / " + max.ChangeId
                    + "；请改用新 release，或显式接受迟交";
                diagnostics.Add(config.Options.AllowLateSubmission
                    ? Warning(DiagnosticCodes.LateSubmissionPatch, unit.Id, message)
                    : Error(DiagnosticCodes.UpgradeOrderRegression, unit.Id, null, message));
            }

            return diagnostics;
        }

        private static int Compare(string releaseA, string idA, string releaseB, string idB)
        {
            var byRelease = string.Compare(releaseA ?? "", releaseB ?? "", StringComparison.OrdinalIgnoreCase);
            return byRelease != 0
                ? byRelease
                : string.Compare(idA ?? "", idB ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static void CheckLateSubmission(IReadOnlyList<ChangeUnit> units, DbChronicleConfig config,
            List<CompileDiagnostic> diagnostics)
        {
            foreach (var unit in units)
            {
                if (unit.IsBaseline)
                {
                    continue;
                }

                DateTime releaseDate;
                if (!TryParseRelease(unit.Release, out releaseDate))
                {
                    continue;
                }

                DateTime idDate;
                if (!TryParseIdDate(unit.Id, out idDate) || idDate <= releaseDate)
                {
                    continue;
                }

                var patch = unit.LateSubmission || IsPatchFile(unit);
                if (patch || config.Options.AllowLateSubmission)
                {
                    diagnostics.Add(Warning(DiagnosticCodes.LateSubmissionPatch, unit.Id,
                        "迟交（补丁）：id 日期 " + idDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                        + " 晚于 release " + unit.Release + "，请确认已通过 depends 指向同 release 单元"));
                }
                else
                {
                    diagnostics.Add(Error(DiagnosticCodes.LateSubmission, unit.Id, null,
                        "迟交：id 日期 " + idDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                        + " 晚于 release " + unit.Release + "；请改用新 release，或声明 lateSubmission=\"true\" / 文件名带 _fix_ / _patch_"));
                }
            }
        }

        private static void CheckManifests(IReadOnlyList<ChangeUnit> units, DbChronicleConfig config,
            List<CompileDiagnostic> diagnostics)
        {
            var store = new ReleaseManifestStore(config);
            foreach (var manifest in store.LoadAll())
            {
                var inChain = units
                    .Where(x => string.Equals(x.Release, manifest.Name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var byId = new Dictionary<string, ChangeUnit>(StringComparer.OrdinalIgnoreCase);
                foreach (var unit in inChain)
                {
                    byId[unit.Id] = unit;
                }

                var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in manifest.Units)
                {
                    declared.Add(item.Id);
                    ChangeUnit unit;
                    if (!byId.TryGetValue(item.Id, out unit))
                    {
                        diagnostics.Add(Error(DiagnosticCodes.ReleaseManifestDrift, item.Id, null,
                            "封版清单中的单元文件缺失：release " + manifest.Name + " / " + item.Id));
                        continue;
                    }

                    if (!string.Equals(unit.Checksum, item.Checksum, StringComparison.OrdinalIgnoreCase))
                    {
                        diagnostics.Add(Error(DiagnosticCodes.ReleaseManifestDrift, item.Id, null,
                            "已封版单元被修改（checksum 不一致）：release " + manifest.Name + " / " + item.Id));
                    }
                }

                foreach (var unit in inChain)
                {
                    if (!declared.Contains(unit.Id))
                    {
                        diagnostics.Add(Error(DiagnosticCodes.ReleaseManifestDrift, unit.Id, null,
                            "封版后新增的单元（不在清单中）：release " + manifest.Name + " / " + unit.Id));
                    }
                }
            }
        }

        private static bool TryParseRelease(string release, out DateTime value)
        {
            return DateTime.TryParseExact(release ?? "", "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out value);
        }

        private static bool TryParseIdDate(string id, out DateTime value)
        {
            value = default(DateTime);
            if (string.IsNullOrEmpty(id) || id.Length < 8)
            {
                return false;
            }

            return DateTime.TryParseExact(id.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out value);
        }

        private static bool IsPatchFile(ChangeUnit unit)
        {
            if (string.IsNullOrEmpty(unit.SourcePath))
            {
                return false;
            }

            var fileName = Path.GetFileName(unit.SourcePath) ?? "";
            return fileName.Contains("_fix_") || fileName.Contains("_patch_") || fileName.StartsWith("fix_")
                || fileName.StartsWith("patch_");
        }

        private static CompileDiagnostic Error(string code, string changeId, string table, string message)
        {
            return new CompileDiagnostic
            {
                Code = code,
                Severity = DiagnosticSeverity.Error,
                ChangeId = changeId,
                Table = table,
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
