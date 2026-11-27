using System;
using System.IO;

using DbChronicle.Compilation.Dialects;
using DbChronicle.Configuration;

namespace DbChronicle.EmbeddedDemo
{
    internal static class Configurator
    {
        public static DbChronicleConfig Load(CliArguments args)
        {
            var noConfig = args.Has("no-config");
            var migrations = args.Get("migrations", null);
            var config = (noConfig || !string.IsNullOrEmpty(migrations))
                ? CreateInlineConfig()
                : LoadFromFile(args);

            ApplyOverrides(config, args);
            return config;
        }

        private static DbChronicleConfig CreateInlineConfig()
        {
            return new DbChronicleConfig
            {
                BaseDirectory = Directory.GetCurrentDirectory()
            };
        }

        private static DbChronicleConfig LoadFromFile(CliArguments args)
        {
            var explicitPath = args.Has("config");
            var path = args.Get("config", ConfigLoader.DefaultFileName);
            if (File.Exists(path))
            {
                return ConfigLoader.Load(path);
            }

            if (explicitPath)
            {
                throw new InvalidOperationException(
                    "配置文件不存在：" + Path.GetFullPath(path) + "（先执行 db-chronicle init）");
            }

            var created = ConfigLoader.CreateDefault(path);
            ConfigLoader.Save(created, path);
            Console.WriteLine("未找到配置文件，已生成默认配置：" + created.ConfigPath);
            return created;
        }

        private static void ApplyOverrides(DbChronicleConfig config, CliArguments args)
        {
            var dialect = args.Get("dialect", null);
            if (!string.IsNullOrEmpty(dialect))
            {
                var normalized = dialect.Trim();
                if (!DialectFactory.IsSupported(normalized))
                {
                    throw new InvalidOperationException(
                        "不支持的方言：" + dialect + "（可选：" + string.Join(" / ", DialectFactory.SupportedDialects) + "）");
                }

                config.Database.Dialect = DialectFactory.Normalize(normalized);
            }

            var version = args.Get("db-version", null);
            if (!string.IsNullOrEmpty(version))
            {
                config.Database.Version = version.Trim();
            }

            var environment = args.Get("environment", null);
            if (!string.IsNullOrEmpty(environment))
            {
                config.Environment = environment.Trim();
            }

            var migrations = args.Get("migrations", null);
            if (!string.IsNullOrEmpty(migrations))
            {
                config.Paths.Migrations = migrations;
            }

            var versionLog = args.Get("version-log", null);
            if (!string.IsNullOrEmpty(versionLog))
            {
                config.Paths.VersionLog = versionLog;
            }

            var output = args.Get("output-dir", null) ?? args.Get("out", null);
            if (!string.IsNullOrEmpty(output))
            {
                config.Paths.Output = output;
            }

            var baseline = args.Get("baseline", null);
            if (baseline != null)
            {
                config.Baseline.File = baseline;
            }

            ApplyBool(args, "baseline-frozen", "no-baseline-frozen", value => config.Baseline.Frozen = value);

            var frozenAt = args.Get("baseline-frozen-at", null);
            if (frozenAt != null)
            {
                config.Baseline.FrozenAt = frozenAt;
            }

            ApplyBool(args, "quote-identifiers", "no-quote-identifiers", value => config.Options.QuoteIdentifiers = value);
            ApplyBool(args, "sql-comments", "no-sql-comments", value => config.Options.IncludeComments = value);
            ApplyBool(args, "include-rollback", "no-include-rollback", value => config.Options.IncludeRollback = value);
            ApplyBool(args, "incremental-modify", "no-incremental-modify", value => config.Options.IncrementalModify = value);

            var delimiter = args.Get("statement-delimiter", null);
            if (!string.IsNullOrEmpty(delimiter))
            {
                config.Options.StatementDelimiter = delimiter;
            }

            var lockTimeout = args.GetInt("lock-timeout", -1);
            if (lockTimeout >= 0)
            {
                config.Options.LockTimeoutSeconds = lockTimeout;
            }

            var commitScope = args.Get("commit-scope", null);
            if (!string.IsNullOrEmpty(commitScope))
            {
                config.Options.CommitScope = commitScope.Trim().ToLowerInvariant();
            }

            var commitBatchSize = args.GetInt("commit-batch-size", -1);
            if (commitBatchSize >= 0)
            {
                config.Options.CommitBatchSize = commitBatchSize;
            }
        }

        private static void ApplyBool(CliArguments args, string onName, string offName, Action<bool> apply)
        {
            if (args.Has(offName))
            {
                apply(false);
            }
            else if (args.Has(onName))
            {
                apply(true);
            }
        }
    }
}
