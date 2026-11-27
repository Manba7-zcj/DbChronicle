using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System;

using DbChronicle.Common.Enums;
using DbChronicle.Compilation.Checks;
using DbChronicle.Configuration;
using DbChronicle.Execution;
using DbChronicle.Migrations.Executor;
using DbChronicle.Migrations.State;
using DbChronicle.Models;
using DbChronicle.Parsing;

namespace DbChronicle.Embedded
{
    internal sealed class DefaultDbChronicle : IDbChronicle
    {
        private static readonly ConcurrentDictionary<string, object> UpgradeGates =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        private readonly IDbExecutor _executor;
        private readonly DbChronicleOptions _options;
        private readonly DbChronicleConfig _config;

        public DefaultDbChronicle(IDbExecutor executor, DbChronicleOptions options)
        {
            _executor = executor;
            _options = options;
            _config = options.Config ?? ConfigLoader.Load(ResolveConfigPath(options.ConfigPath));
            if (_config.BaseDirectory == null)
            {
                _config.BaseDirectory = AppContext.BaseDirectory;
            }
        }

        public DbChronicleConfig Config
        {
            get { return _config; }
        }

        public IReadOnlyList<ChangeUnit> LoadUnits()
        {
            return new ChangeUnitLoader(_config).LoadAll();
        }

        public DbUpgradeResult DbUpgrade(DbUpgradeOptions options)
        {
            var upgradeOptions = options ?? new DbUpgradeOptions();
            var gate = UpgradeGates.GetOrAdd(_config.ResolveVersionLogPath(), _ => new object());
            lock (gate)
            {
                return DbUpgradeCore(upgradeOptions);
            }
        }

        private DbUpgradeResult DbUpgradeCore(DbUpgradeOptions upgradeOptions)
        {
            if (!upgradeOptions.DryRun && _options.Log != null
                && (string.IsNullOrEmpty(upgradeOptions.ConnectionString) || upgradeOptions.LockFactory == null))
            {
                _options.Log("警告：未同时提供 ConnectionString 与 LockFactory，跨进程互斥未启用；多实例部署请务必提供两者");
            }

            var units = LoadUnits();
            var executor = new SchemaExecutor(_config, null, upgradeOptions.LockFactory);
            var applied = executor.ApplyWith(_executor, units, new ApplyOptions
            {
                ConnectionString = upgradeOptions.ConnectionString,
                AppliedBy = string.IsNullOrEmpty(upgradeOptions.AppliedBy)
                    ? Environment.UserName
                    : upgradeOptions.AppliedBy,
                DryRun = upgradeOptions.DryRun,
                Restart = upgradeOptions.Restart,
                MarkAppliedChangeId = upgradeOptions.MarkAppliedChangeId,
                ContinueOnError = upgradeOptions.ContinueOnError,
                LockTimeoutSeconds = upgradeOptions.LockTimeoutSeconds > 0
                    ? upgradeOptions.LockTimeoutSeconds
                    : _config.Options.LockTimeoutSeconds,
                Log = _options.Log
            });

            return new DbUpgradeResult
            {
                Success = applied.Success,
                AppliedCount = applied.AppliedCount,
                SkippedCount = applied.SkippedCount,
                Sql = applied.Sql,
                Message = applied.Message,
                ExitCode = applied.ExitCode,
                Failures = applied.Failures.ToList(),
                FailedCount = applied.FailedCount,
                Diagnostics = applied.Diagnostics.ToList()
            };
        }

        public string Compile(string changeId = null)
        {
            var units = LoadUnits();
            if (units.Count == 0)
            {
                throw new InvalidOperationException("未找到变更单元：" + _config.ResolveMigrationsPath());
            }

            IReadOnlyList<ChangeUnit> targets = units;
            if (!string.IsNullOrEmpty(changeId))
            {
                targets = units
                    .Where(x => string.Equals(x.Id, changeId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (targets.Count == 0)
                {
                    throw new InvalidOperationException("未找到变更单元：" + changeId);
                }
            }

            var builder = new StringBuilder();
            var errors = new List<CompileDiagnostic>();
            foreach (var unit in targets)
            {
                var result = DbChronicleMigrator.CompileUnit(_config, units, unit);
                builder.AppendLine(result.Sql);
                foreach (var diagnostic in result.Diagnostics)
                {
                    if (diagnostic.Severity == DiagnosticSeverity.Error)
                    {
                        errors.Add(diagnostic);
                        continue;
                    }

                    if (_options.Log != null)
                    {
                        _options.Log(diagnostic.ToString());
                    }
                }
            }

            if (errors.Count > 0)
            {
                throw new InvalidOperationException(
                    "编译失败：" + string.Join("；", errors.Select(x => x.ToString())));
            }

            return builder.ToString();
        }

        public IReadOnlyList<CompileDiagnostic> Check()
        {
            var units = LoadUnits();
            var diagnostics = new List<CompileDiagnostic>();
            foreach (var unit in units)
            {
                try
                {
                    diagnostics.AddRange(DbChronicleMigrator.CompileUnit(_config, units, unit).Diagnostics);
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
            return diagnostics;
        }

        public IReadOnlyList<AppliedChange> Status()
        {
            return new VersionStore(_config).LoadAll();
        }

        public IReadOnlyList<ChangeUnit> PendingUnits()
        {
            var applied = new HashSet<string>(
                new VersionStore(_config).LoadAll()
                    .Where(x => !x.HasFailure)
                    .Select(x => x.ChangeId),
                StringComparer.OrdinalIgnoreCase);

            return LoadUnits().Where(x => !applied.Contains(x.Id)).ToList();
        }

        public void Dispose()
        {
            // 执行器由宿主创建与释放，这里不接管其生命周期
        }

        private static string ResolveConfigPath(string configPath)
        {
            if (string.IsNullOrEmpty(configPath))
            {
                return configPath;
            }

            return Path.IsPathRooted(configPath)
                ? configPath
                : Path.Combine(AppContext.BaseDirectory, configPath);
        }
    }
}
