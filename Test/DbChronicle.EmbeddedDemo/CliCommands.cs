using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using DbChronicle.Common.Enums;
using DbChronicle.Compilation.Checks;
using DbChronicle.Compilation.Compiler;
using DbChronicle.Compilation.Dialects;
using DbChronicle.Configuration;
using DbChronicle.Execution;
using DbChronicle.Executor.AdoNet;
using DbChronicle.Migrations.Executor;
using DbChronicle.Migrations.State;
using DbChronicle.Migrations.Templates;
using DbChronicle.Models;
using DbChronicle.Parsing;

namespace DbChronicle.EmbeddedDemo
{
    internal static class CliCommands
    {
        public static int Compile(CliArguments args)
        {
            var config = Configurator.Load(args);
            var loader = new ChangeUnitLoader(config);
            var allUnits = loader.LoadAll();
            if (allUnits.Count == 0)
            {
                Console.Error.WriteLine("error: 未找到变更单元：" + loader.MigrationsDirectory);
                return ExitCodes.Usage;
            }

            var units = SelectUnits(args, config, allUnits);
            var options = new CompilerOptions
            {
                IncrementalModify = config.Options.IncrementalModify,
                IncludeComments = config.Options.IncludeComments,
                StatementDelimiter = config.Options.StatementDelimiter
            };

            if (units.Count == 0)
            {
                return ExitCodes.Success;
            }

            IReadOnlyList<string> dialects = args.Has("all-dialects")
                ? DialectFactory.SupportedDialects
                : new[] { config.Database.Dialect };

            var outputDirectory = ResolveOutputPath(args, config.ResolveOutputPath());
            Directory.CreateDirectory(outputDirectory);

            var hasError = false;
            var split = args.Has("split");
            if (split)
            {
                var manifest = new StringBuilder();
                foreach (var unit in units)
                {
                    manifest.AppendLine(unit.Id + "\t" + unit.Release);
                }

                File.WriteAllText(Path.Combine(outputDirectory, "_units.txt"), manifest.ToString(), new UTF8Encoding(false));
            }

            foreach (var dialectName in dialects)
            {
                var version = args.Has("all-dialects") ? null : config.Database.Version;
                var dialect = DialectFactory.Create(dialectName, version,
                    new DialectContext { IncrementalModify = options.IncrementalModify });

                var builder = new StringBuilder();
                var dialectFailed = false;

                foreach (var unit in units)
                {
                    var model = options.IncrementalModify
                        ? new StructureModelBuilder().Build(allUnits, unit.Id)
                        : null;
                    var result = new SchemaCompiler(dialect, options, model).Compile(unit);

                    PrintDiagnostics(result.Diagnostics);
                    if (result.HasErrors)
                    {
                        dialectFailed = true;
                        hasError = true;
                        continue;
                    }

                    if (split)
                    {
                        var unitPath = Path.Combine(outputDirectory, unit.Id + "." + dialectName + ".sql");
                        WriteStatementLines(unitPath, result.Statements);
                        Console.WriteLine("已生成：" + unitPath);
                    }
                    else
                    {
                        builder.AppendLine(result.Sql);
                    }
                }

                if (split)
                {
                    if (dialectFailed)
                    {
                        Console.Error.WriteLine("方言 " + dialectName + " 编译失败，已跳过合并输出");
                    }

                    continue;
                }

                var outputPath = Path.Combine(outputDirectory, dialectName + ".sql");
                File.WriteAllText(outputPath, builder.ToString(), new UTF8Encoding(false));
                Console.WriteLine("已生成：" + outputPath + (dialectFailed ? "（含编译错误）" : ""));
            }

            return hasError ? ExitCodes.Execution : ExitCodes.Success;
        }

        public static int Check(CliArguments args)
        {
            var config = Configurator.Load(args);
            var units = new ChangeUnitLoader(config).LoadAll();
            var diagnostics = new List<CompileDiagnostic>();
            var compilerOptions = new CompilerOptions
            {
                IncrementalModify = config.Options.IncrementalModify,
                IncludeComments = config.Options.IncludeComments,
                StatementDelimiter = config.Options.StatementDelimiter
            };

            var dialect = DialectFactory.Create(config.Database.Dialect, config.Database.Version,
                new DialectContext { IncrementalModify = compilerOptions.IncrementalModify });

            foreach (var unit in units)
            {
                try
                {
                    var model = compilerOptions.IncrementalModify
                        ? new StructureModelBuilder().Build(units, unit.Id)
                        : null;
                    diagnostics.AddRange(new SchemaCompiler(dialect, compilerOptions, model).Compile(unit).Diagnostics);
                }
                catch (Exception ex)
                {
                    diagnostics.Add(new CompileDiagnostic
                    {
                        Code = "E043",
                        Severity = DiagnosticSeverity.Error,
                        ChangeId = unit.Id,
                        Message = "编译异常：" + ex.Message
                    });
                }
            }

            diagnostics.AddRange(new ChangeChecker().Check(units));
            PrintDiagnostics(diagnostics);

            var strict = args.Has("strict-warnings");
            var failed = diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error)
                || (strict && diagnostics.Any(x => x.Severity == DiagnosticSeverity.Warning));

            if (failed)
            {
                Console.Error.WriteLine("校验未通过（" + units.Count + " 个变更单元）");
                return ExitCodes.Usage;
            }

            Console.WriteLine("校验通过（" + units.Count + " 个变更单元）");
            return ExitCodes.Success;
        }

        public static int Apply(CliArguments args)
        {
            var config = Configurator.Load(args);
            var units = new ChangeUnitLoader(config).LoadAll();
            var connectionString = ConnectionStringResolver.Resolve();
            var options = new ApplyOptions
            {
                ConnectionString = connectionString,
                AppliedBy = ChangeUnitFactory.ResolveAuthor(),
                DryRun = args.Has("dry-run"),
                MarkAppliedChangeId = args.Get("mark-applied", null),
                Restart = args.Has("restart"),
                ContinueOnError = args.Has("continue-on-error") ? true : (bool?)null,
                LockTimeoutSeconds = config.Options.LockTimeoutSeconds,
                Log = Console.WriteLine
            };

            var factory = new AdoNetExecutorFactory(config.Database.Dialect);
            var executor = new SchemaExecutor(config, factory, factory);

            if (args.Has("check-drift"))
            {
                DriftReport drift;
                try
                {
                    drift = executor.CheckDrift(units, connectionString);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("error: 结构比对失败：" + ex.Message);
                    return ExitCodes.Environment;
                }

                foreach (var missing in drift.MissingColumns)
                {
                    Console.Error.WriteLine("drift: 模型中有、库中没有的列：" + missing);
                }

                foreach (var extra in drift.ExtraColumns)
                {
                    Console.WriteLine("info: 库中多出的列（允许）：" + extra);
                }

                foreach (var table in drift.UnreadableTables)
                {
                    Console.WriteLine("提示: 无法读取表结构：" + table);
                }

                if (drift.HasDrift)
                {
                    Console.Error.WriteLine("发现模型漂移：请核对后再执行 apply");
                    return ExitCodes.Usage;
                }

                Console.WriteLine("未发现模型漂移。");
                return ExitCodes.Success;
            }

            if (args.Has("backfill-logs"))
            {
                BackfillResult backfill;
                try
                {
                    backfill = executor.BackfillLogs(units, options);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("error: 补生成日志失败：" + ex.Message);
                    return ExitCodes.Environment;
                }

                if (!backfill.Success)
                {
                    Console.Error.WriteLine("error: " + backfill.Message);
                    return backfill.ExitCode ?? ExitCodes.Execution;
                }

                Console.WriteLine("补生成完成：新增 " + backfill.BackfilledCount + " 个，已存在 " + backfill.SkippedCount + " 个");
                return ExitCodes.Success;
            }

            ApplyResult result;
            try
            {
                result = executor.Apply(units, options);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: 连接或执行环境异常：" + ex.Message);
                return ExitCodes.Environment;
            }

            PrintDiagnostics(result.Diagnostics);

            if (!result.Success)
            {
                Console.Error.WriteLine("error: " + result.Message);
                return result.ExitCode ?? ExitCodes.Execution;
            }

            Console.WriteLine("完成：应用 " + result.AppliedCount + " 个，跳过 " + result.SkippedCount + " 个");
            return ExitCodes.Success;
        }

        public static int Status(CliArguments args)
        {
            var config = Configurator.Load(args);
            var units = new ChangeUnitLoader(config).LoadAll();
            var applied = new VersionStore(config).LoadAll();

            var releaseFilter = args.Get("release", null);
            var current = applied
                .Where(x => !x.HasFailure)
                .Select(x => x.Release)
                .Where(x => !string.IsNullOrEmpty(x))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .LastOrDefault();

            var appliedIds = new HashSet<string>(
                applied.Where(x => !x.HasFailure).Select(x => x.ChangeId),
                StringComparer.OrdinalIgnoreCase);
            var pending = units.Where(x => !appliedIds.Contains(x.Id)
                && (releaseFilter == null || x.Release == releaseFilter)).ToList();
            var pendingRelease = pending
                .Select(x => x.Release)
                .Where(x => !string.IsNullOrEmpty(x))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .LastOrDefault();

            Console.WriteLine(releaseFilter == null
                ? "数据库当前版本：" + (current ?? "无（未初始化）")
                : "数据库当前版本（过滤 release=" + releaseFilter + "）：" + (current ?? "无（未初始化）"));

            if (pending.Count > 0)
            {
                Console.WriteLine("待应用版本：" + (pendingRelease ?? "无"));
            }

            Console.WriteLine();
            Console.WriteLine("已应用：");
            foreach (var record in applied.Where(x => releaseFilter == null || x.Release == releaseFilter))
            {
                Console.WriteLine((record.HasFailure ? "[!] " : "[x] ") + record.ChangeId + "  " + record.Release
                    + "  " + (record.HasFailure ? "failed at stmt " + record.FailureStatement : "applied " + record.AppliedAt)
                    + " by " + record.AppliedBy);
            }

            Console.WriteLine();
            Console.WriteLine("待应用：");
            foreach (var unit in pending)
            {
                Console.WriteLine("[ ] " + unit.Id + "  " + unit.Release + "  pending  " + (unit.Description ?? ""));
            }

            if (pending.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("提示：执行 apply 应用 " + pending.Count + " 个待应用的变更单元。");
                return ExitCodes.Pending;
            }

            Console.WriteLine();
            Console.WriteLine("数据库已是最新版本。");
            return ExitCodes.Success;
        }

        public static int Verify(CliArguments args)
        {
            var config = Configurator.Load(args);
            var units = new ChangeUnitLoader(config).LoadAll();
            var applied = new VersionStore(config).LoadAll();

            var map = new Dictionary<string, ChangeUnit>(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in units)
            {
                map[unit.Id] = unit;
            }

            var errors = 0;
            foreach (var record in applied)
            {
                ChangeUnit unit;
                if (!map.TryGetValue(record.ChangeId, out unit))
                {
                    Console.Error.WriteLine("error: 已应用的变更单元文件丢失：" + record.ChangeId);
                    errors++;
                    continue;
                }

                if (string.Equals(record.Checksum, unit.Checksum, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[x] " + record.ChangeId);
                    continue;
                }

                var normalized = ChecksumUtil.ComputeNormalizedFile(unit.SourcePath);
                if (string.Equals(record.Checksum, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[?] " + record.ChangeId + "  疑似仅换行或编码变动（规范化后一致）");
                    continue;
                }

                Console.Error.WriteLine("error: 已应用的变更单元内容被修改：" + record.ChangeId
                    + "（已应用=" + record.Checksum + "，当前=" + unit.Checksum + "）");
                errors++;
            }

            if (errors > 0)
            {
                return ExitCodes.Execution;
            }

            Console.WriteLine("checksum 校验通过（" + applied.Count + " 条记录）。");
            return ExitCodes.Success;
        }

        private static IReadOnlyList<ChangeUnit> SelectUnits(CliArguments args, DbChronicleConfig config,
            IReadOnlyList<ChangeUnit> allUnits)
        {
            var changeId = args.Get("change-id", null);
            if (!string.IsNullOrEmpty(changeId))
            {
                var selected = allUnits
                    .Where(x => string.Equals(x.Id, changeId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (selected.Count == 0)
                {
                    throw new InvalidOperationException("未找到变更单元：" + changeId);
                }

                return selected;
            }

            var since = args.Get("since", null);
            if (!string.IsNullOrEmpty(since))
            {
                var sinceValue = NormalizeVersion(since);
                var newer = allUnits
                    .Where(x => string.CompareOrdinal(x.Id, sinceValue) > 0)
                    .ToList();
                Console.WriteLine("自 " + sinceValue + " 之后共 " + newer.Count + " 个变更单元");
                return newer;
            }

            if (!args.Has("pending"))
            {
                return allUnits;
            }

            var applied = new HashSet<string>(
                new VersionStore(config).LoadAll()
                    .Where(x => !x.HasFailure)
                    .Select(x => x.ChangeId),
                StringComparer.OrdinalIgnoreCase);

            var pending = allUnits.Where(x => !applied.Contains(x.Id)).ToList();
            if (pending.Count == 0)
            {
                Console.WriteLine("没有待应用的变更单元（状态文件已是最新）");
                return pending;
            }

            Console.WriteLine("待应用 " + pending.Count + " 个变更单元：" + string.Join(", ", pending.Select(x => x.Id)));
            return pending;
        }

        private static string NormalizeVersion(string version)
        {
            var value = (version ?? "").Trim();
            return (value.Length == 8) ? (value + "_000000") : value;
        }

        private static string ResolveOutputPath(CliArguments args, string fallback)
        {
            var value = args.Get("output-dir", null);
            if (string.IsNullOrEmpty(value))
            {
                value = args.Get("out", null);
            }

            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        private static void WriteStatementLines(string path, IEnumerable<SqlStatement> statements)
        {
            var builder = new StringBuilder();
            foreach (var statement in statements)
            {
                var line = FlattenStatement(statement.Text);
                if (line.Length > 0)
                {
                    builder.AppendLine(line);
                }
            }

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }

        private static string FlattenStatement(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

            var builder = new StringBuilder();
            foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(line);
            }

            var flattened = builder.ToString().Trim();
            return flattened.EndsWith(";", StringComparison.Ordinal)
                ? flattened.Substring(0, flattened.Length - 1).TrimEnd()
                : flattened;
        }

        private static void PrintDiagnostics(IEnumerable<CompileDiagnostic> diagnostics)
        {
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                {
                    Console.Error.WriteLine(diagnostic.ToString());
                }
                else
                {
                    Console.WriteLine(diagnostic.ToString());
                }
            }
        }
    }
}
