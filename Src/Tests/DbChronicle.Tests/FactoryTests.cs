using System.Collections.Generic;
using System.IO;
using System;

using DbChronicle.Common.Enums;
using DbChronicle.Configuration;
using DbChronicle.Embedded;
using DbChronicle.Execution;
using DbChronicle.Models;
using Xunit;

namespace DbChronicle.Tests
{
    public class FactoryTests
    {
        [Fact]
        public void Factory_ShouldCompileOffline_WithInjectedExecutor()
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            TestSupport.WriteUnit(directory, "00000000_000000_baseline",
                "  <table name=\"t\" action=\"CREATE\">\n"
                + "    <column name=\"id\" action=\"ADD\" type=\"Int64\" primaryKey=\"true\" autoIncrement=\"true\" nullable=\"false\"/>\n"
                + "  </table>");

            var configPath = Path.Combine(directory, ConfigLoader.DefaultFileName);
            var config = ConfigLoader.CreateDefault(configPath);
            config.Database.Dialect = "mysql";
            config.Paths.Migrations = ".";
            ConfigLoader.Save(config, configPath);

            using (var chronicle = DbChronicleFactory.Create(new OfflineExecutor(), o => o.ConfigPath = configPath))
            {
                var sql = chronicle.Compile();
                Assert.Contains("CREATE TABLE IF NOT EXISTS `t`", sql);
                Assert.Single(chronicle.LoadUnits());
                Assert.DoesNotContain(chronicle.Check(), x => x.Severity == DiagnosticSeverity.Error);
            }
        }

        [Fact]
        public void Factory_DbUpgrade_ShouldReturnSql_WhenDryRun()
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            TestSupport.WriteUnit(directory, "00000000_000000_baseline",
                "  <table name=\"t\" action=\"CREATE\">\n"
                + "    <column name=\"id\" action=\"ADD\" type=\"Int64\" primaryKey=\"true\" autoIncrement=\"true\" nullable=\"false\"/>\n"
                + "  </table>");

            var configPath = Path.Combine(directory, ConfigLoader.DefaultFileName);
            var config = ConfigLoader.CreateDefault(configPath);
            config.Database.Dialect = "mysql";
            config.Paths.Migrations = ".";
            ConfigLoader.Save(config, configPath);

            using (var chronicle = DbChronicleFactory.Create(new OfflineExecutor(), o => o.ConfigPath = configPath))
            {
                var result = chronicle.DbUpgrade(new DbUpgradeOptions { DryRun = true });

                Assert.True(result.Success);
                Assert.Equal(0, result.AppliedCount);
                Assert.Contains("CREATE TABLE IF NOT EXISTS `t`", result.Sql);
            }
        }

        [Fact]
        public void Factory_ShouldAcceptProvidedConfig()
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            TestSupport.WriteUnit(directory, "00000000_000000_baseline",
                "  <table name=\"t\" action=\"CREATE\">\n"
                + "    <column name=\"id\" action=\"ADD\" type=\"Int64\" primaryKey=\"true\" autoIncrement=\"true\" nullable=\"false\"/>\n"
                + "  </table>");

            var configPath = Path.Combine(directory, ConfigLoader.DefaultFileName);
            var saved = ConfigLoader.CreateDefault(configPath);
            saved.Database.Dialect = "mysql";
            saved.Paths.Migrations = ".";
            ConfigLoader.Save(saved, configPath);

            var provided = ConfigLoader.Load(configPath);

            using (var chronicle = DbChronicleFactory.Create(new OfflineExecutor(),
                new DbChronicleOptions { Config = provided }))
            {
                Assert.Same(provided, chronicle.Config);
                Assert.Contains("CREATE TABLE IF NOT EXISTS `t`", chronicle.Compile());
            }
        }

        [Fact]
        public void Config_ShouldResolveAbsoluteMigrationsPath()
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            TestSupport.WriteUnit(directory, "00000000_000000_baseline",
                "  <table name=\"t\" action=\"CREATE\">\n"
                + "    <column name=\"id\" action=\"ADD\" type=\"Int64\" primaryKey=\"true\" autoIncrement=\"true\" nullable=\"false\"/>\n"
                + "  </table>");

            var configPath = Path.Combine(directory, ConfigLoader.DefaultFileName);
            var config = ConfigLoader.CreateDefault(configPath);
            config.Database.Dialect = "mysql";
            config.Paths.Migrations = directory;
            ConfigLoader.Save(config, configPath);

            using (var chronicle = DbChronicleFactory.Create(new OfflineExecutor(), o => o.ConfigPath = configPath))
            {
                Assert.Equal(Path.GetFullPath(directory), Path.GetFullPath(chronicle.Config.ResolveMigrationsPath()));
                Assert.Single(chronicle.LoadUnits());
            }
        }

        [Fact]
        public void Factory_ShouldRejectNullExecutor()
        {
            Assert.Throws<ArgumentNullException>(() => DbChronicleFactory.Create(null, o => { }));
        }

        [Fact]
        public void Check_ShouldIncludeParseDiagnostics()
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            TestSupport.WriteUnit(directory, "20261127_100000_parse",
                "  <table action=\"CREATE\">\n"
                + "    <column name=\"id\" action=\"ADD\" type=\"Int64\" primaryKey=\"true\" nullable=\"false\"/>\n"
                + "  </table>");

            var configPath = Path.Combine(directory, ConfigLoader.DefaultFileName);
            var config = ConfigLoader.CreateDefault(configPath);
            config.Database.Dialect = "mysql";
            config.Paths.Migrations = ".";
            ConfigLoader.Save(config, configPath);

            using (var chronicle = DbChronicleFactory.Create(new OfflineExecutor(), o => o.ConfigPath = configPath))
            {
                var diagnostics = chronicle.Check();

                Assert.Contains(diagnostics, x => x.Code == "E010" && x.Severity == DiagnosticSeverity.Error);
            }
        }

        [Fact]
        public void Check_ShouldIncludeDialectDiagnostics()
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            TestSupport.WriteUnit(directory, "20261127_100001_seq",
                "  <sequence name=\"seq_order\" action=\"CREATE\" start=\"1\"/>");

            var configPath = Path.Combine(directory, ConfigLoader.DefaultFileName);
            var config = ConfigLoader.CreateDefault(configPath);
            config.Database.Dialect = "sqlite";
            config.Paths.Migrations = ".";
            ConfigLoader.Save(config, configPath);

            using (var chronicle = DbChronicleFactory.Create(new OfflineExecutor(), o => o.ConfigPath = configPath))
            {
                var diagnostics = chronicle.Check();

                Assert.Contains(diagnostics, x => x.Code == "E046" && x.Severity == DiagnosticSeverity.Error);
            }
        }

        [Fact]
        public void Compile_ShouldThrow_WhenChangeIdNotFound()
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            TestSupport.WriteUnit(directory, "00000000_000000_baseline",
                "  <table name=\"t\" action=\"CREATE\">\n"
                + "    <column name=\"id\" action=\"ADD\" type=\"Int64\" primaryKey=\"true\" autoIncrement=\"true\" nullable=\"false\"/>\n"
                + "  </table>");

            var configPath = Path.Combine(directory, ConfigLoader.DefaultFileName);
            var config = ConfigLoader.CreateDefault(configPath);
            config.Database.Dialect = "mysql";
            config.Paths.Migrations = ".";
            ConfigLoader.Save(config, configPath);

            using (var chronicle = DbChronicleFactory.Create(new OfflineExecutor(), o => o.ConfigPath = configPath))
            {
                var error = Assert.Throws<InvalidOperationException>(
                    () => chronicle.Compile("20990101_000000_missing"));

                Assert.Contains("20990101_000000_missing", error.Message);
            }
        }

        [Fact]
        public void Compile_ShouldThrow_WhenMigrationsDirectoryIsEmpty()
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            var configPath = Path.Combine(directory, ConfigLoader.DefaultFileName);
            var config = ConfigLoader.CreateDefault(configPath);
            config.Database.Dialect = "mysql";
            config.Paths.Migrations = ".";
            ConfigLoader.Save(config, configPath);

            using (var chronicle = DbChronicleFactory.Create(new OfflineExecutor(), o => o.ConfigPath = configPath))
            {
                var error = Assert.Throws<InvalidOperationException>(() => chronicle.Compile());

                Assert.Contains("未找到变更单元", error.Message);
            }
        }

        [Fact]
        public void DbUpgrade_ShouldFail_WhenMigrationsDirectoryIsEmpty()
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            var configPath = Path.Combine(directory, ConfigLoader.DefaultFileName);
            var config = ConfigLoader.CreateDefault(configPath);
            config.Database.Dialect = "mysql";
            config.Paths.Migrations = ".";
            ConfigLoader.Save(config, configPath);

            using (var chronicle = DbChronicleFactory.Create(new OfflineExecutor(), o => o.ConfigPath = configPath))
            {
                var result = chronicle.DbUpgrade(new DbUpgradeOptions { DryRun = true });

                Assert.False(result.Success);
                Assert.Equal(1, result.ExitCode);
                Assert.Contains("未找到变更单元", result.Message);
            }
        }

        [Fact]
        public void DbUpgrade_ShouldRejectDryRunWithMarkApplied()
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            TestSupport.WriteUnit(directory, "00000000_000000_baseline",
                "  <table name=\"t\" action=\"CREATE\">\n"
                + "    <column name=\"id\" action=\"ADD\" type=\"Int64\" primaryKey=\"true\" autoIncrement=\"true\" nullable=\"false\"/>\n"
                + "  </table>");

            var configPath = Path.Combine(directory, ConfigLoader.DefaultFileName);
            var config = ConfigLoader.CreateDefault(configPath);
            config.Database.Dialect = "mysql";
            config.Paths.Migrations = ".";
            ConfigLoader.Save(config, configPath);

            using (var chronicle = DbChronicleFactory.Create(new OfflineExecutor(), o => o.ConfigPath = configPath))
            {
                var result = chronicle.DbUpgrade(new DbUpgradeOptions
                {
                    DryRun = true,
                    MarkAppliedChangeId = "00000000_000000_baseline"
                });

                Assert.False(result.Success);
                Assert.Equal(1, result.ExitCode);
                Assert.Contains("dry-run", result.Message);
            }
        }

        [Fact]
        public void DbUpgrade_ShouldSplitRawSqlWithConfiguredDelimiter()
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            TestSupport.WriteUnit(directory, "20261127_100002_raw",
                "  <rawSql name=\"t\">CREATE TABLE a (id INT);\nGO\nCREATE TABLE b (id INT);\nGO</rawSql>");

            var config = new DbChronicleConfig { BaseDirectory = directory };
            config.Database.Dialect = "mysql";
            config.Paths.Migrations = ".";
            config.Paths.VersionLog = "version_log";
            config.Options.StatementDelimiter = "GO";

            var recorder = new RecordingExecutor();
            using (var chronicle = DbChronicleFactory.Create(recorder, new DbChronicleOptions { Config = config }))
            {
                var result = chronicle.DbUpgrade();

                Assert.True(result.Success, result.Message);
                Assert.Equal(2, recorder.Executed.Count);
            }
        }

        [Fact]
        public void PendingUnits_ShouldReturnAllUnits_WhenStateIsEmpty()
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            TestSupport.WriteUnit(directory, "00000000_000000_baseline",
                "  <table name=\"t\" action=\"CREATE\">\n"
                + "    <column name=\"id\" action=\"ADD\" type=\"Int64\" primaryKey=\"true\" autoIncrement=\"true\" nullable=\"false\"/>\n"
                + "  </table>");

            var config = new DbChronicleConfig { BaseDirectory = directory };
            config.Database.Dialect = "mysql";
            config.Paths.Migrations = ".";
            config.Paths.VersionLog = "version_log";

            using (var chronicle = DbChronicleFactory.Create(new OfflineExecutor(), new DbChronicleOptions { Config = config }))
            {
                Assert.Single(chronicle.PendingUnits());
                Assert.Empty(chronicle.Status());
            }
        }

        private sealed class OfflineExecutor : IDbExecutor
        {
            public string DialectName
            {
                get { return "mysql"; }
            }

            public bool SupportsTransactionalDdl
            {
                get { return false; }
            }

            public void Execute(string sql)
            {
            }

            public object Scalar(string sql)
            {
                return null;
            }

            public List<Dictionary<string, object>> Query(string sql)
            {
                return new List<Dictionary<string, object>>();
            }

            public void BeginTransaction()
            {
            }

            public void Commit()
            {
            }

            public void Rollback()
            {
            }

            public void Dispose()
            {
            }
        }

        private sealed class RecordingExecutor : IDbExecutor
        {
            public List<string> Executed { get; } = new List<string>();

            public string DialectName
            {
                get { return "mysql"; }
            }

            public bool SupportsTransactionalDdl
            {
                get { return false; }
            }

            public void Execute(string sql)
            {
                Executed.Add(sql);
            }

            public object Scalar(string sql)
            {
                return null;
            }

            public List<Dictionary<string, object>> Query(string sql)
            {
                return new List<Dictionary<string, object>>();
            }

            public void BeginTransaction()
            {
            }

            public void Commit()
            {
            }

            public void Rollback()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}