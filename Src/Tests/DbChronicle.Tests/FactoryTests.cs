using System.Collections.Generic;
using System.IO;
using System.Xml;
using System;

using DbChronicle.Common.Enums;
using DbChronicle.Configuration;
using DbChronicle.Embedded;
using DbChronicle.Execution;
using DbChronicle.Migrations.Executor;
using DbChronicle.Migrations.State;
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
                var error = Assert.Throws<MigrationConfigurationException>(
                    () => chronicle.DbUpgrade(new DbUpgradeOptions { DryRun = true }));

                Assert.Contains("未找到变更单元", error.Message);
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
                Assert.Throws<MigrationConfigurationException>(() => chronicle.DbUpgrade(new DbUpgradeOptions
                {
                    DryRun = true,
                    MarkAppliedChangeId = "00000000_000000_baseline"
                }));
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

        [Fact]
        public void DbUpgrade_ShouldThrowStatementExecutionException_WhenExecutionFails()
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            TestSupport.WriteUnit(directory, "20261127_100003_fail",
                "  <rawSql name=\"t\">SELECT 1;</rawSql>");

            var config = new DbChronicleConfig { BaseDirectory = directory };
            config.Database.Dialect = "mysql";
            config.Paths.Migrations = ".";
            config.Paths.VersionLog = "version_log";

            using (var chronicle = DbChronicleFactory.Create(new FailingExecutor(), new DbChronicleOptions { Config = config }))
            {
                var error = Assert.Throws<StatementExecutionException>(() => chronicle.DbUpgrade(new DbUpgradeOptions
                {
                    ContinueOnError = false
                }));

                Assert.IsType<InvalidOperationException>(error.InnerException);
                Assert.Contains("t", error.Describe());
            }
        }

        [Fact]
        public void DbUpgrade_ShouldPropagateStateFileError()
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            TestSupport.WriteUnit(directory, "20261127_100004_state",
                "  <rawSql name=\"t\">SELECT 1;</rawSql>");

            var logDirectory = Path.Combine(directory, "version_log");
            Directory.CreateDirectory(logDirectory);
            File.WriteAllText(Path.Combine(logDirectory, "DbchronicleState.xml"), "<state><applied");

            var config = new DbChronicleConfig { BaseDirectory = directory };
            config.Database.Dialect = "mysql";
            config.Paths.Migrations = ".";
            config.Paths.VersionLog = "version_log";

            using (var chronicle = DbChronicleFactory.Create(new OfflineExecutor(), new DbChronicleOptions { Config = config }))
            {
                Assert.Throws<XmlException>(() => chronicle.DbUpgrade());
            }
        }

        [Fact]
        public void DbUpgrade_ShouldFail_WhenChainInvalid()
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            TestSupport.WriteUnit(directory, "20261127_100005_chain",
                "  <table name=\"ghost\" action=\"ALTER\">\n"
                + "    <column name=\"c\" action=\"ADD\" type=\"Int32\" nullable=\"true\"/>\n"
                + "  </table>");

            var config = new DbChronicleConfig { BaseDirectory = directory };
            config.Database.Dialect = "mysql";
            config.Paths.Migrations = ".";
            config.Paths.VersionLog = "version_log";

            using (var chronicle = DbChronicleFactory.Create(new OfflineExecutor(), new DbChronicleOptions { Config = config }))
            {
                var error = Assert.Throws<ChainValidationException>(() => chronicle.DbUpgrade());

                Assert.Contains(error.Diagnostics, x => x.Code == "E111");
                Assert.Equal(1, error.ExitCode);
            }
        }

        [Fact]
        public void Compile_ShouldThrow_WhenChainInvalid()
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            TestSupport.WriteUnit(directory, "20261127_100006_chain",
                "  <table name=\"ghost\" action=\"ALTER\">\n"
                + "    <column name=\"c\" action=\"ADD\" type=\"Int32\" nullable=\"true\"/>\n"
                + "  </table>");

            var config = new DbChronicleConfig { BaseDirectory = directory };
            config.Database.Dialect = "mysql";
            config.Paths.Migrations = ".";

            using (var chronicle = DbChronicleFactory.Create(new OfflineExecutor(), new DbChronicleOptions { Config = config }))
            {
                var error = Assert.Throws<ChainValidationException>(() => chronicle.Compile());

                Assert.Contains("编译失败", error.Message);
                Assert.Contains(error.Diagnostics, x => x.Code == "E111");
            }
        }

        [Fact]
        public void DbUpgrade_ShouldFail_WhenVersionOrderRegresses()
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            TestSupport.WriteUnit(directory, "20261124_100000_low",
                "  <table name=\"lo\" action=\"CREATE\">\n"
                + "    <column name=\"id\" action=\"ADD\" type=\"Int64\" primaryKey=\"true\" nullable=\"false\"/>\n"
                + "  </table>");
            TestSupport.WriteUnit(directory, "20261126_100000_high",
                "  <table name=\"hi\" action=\"CREATE\">\n"
                + "    <column name=\"id\" action=\"ADD\" type=\"Int64\" primaryKey=\"true\" nullable=\"false\"/>\n"
                + "  </table>");

            var config = new DbChronicleConfig { BaseDirectory = directory };
            config.Database.Dialect = "mysql";
            config.Paths.Migrations = ".";
            config.Paths.VersionLog = "version_log";
            config.Paths.Releases = "releases";

            new VersionStore(config).MarkAppliedManually("20261126_100000_high", "20261126",
                "old", "mysql", "tester", "already applied");

            using (var chronicle = DbChronicleFactory.Create(new OfflineExecutor(), new DbChronicleOptions { Config = config }))
            {
                var error = Assert.Throws<ChainValidationException>(
                    () => chronicle.DbUpgrade(new DbUpgradeOptions { DryRun = true }));

                Assert.Contains(error.Diagnostics, x => x.Code == "E127");
                Assert.Equal(1, error.ExitCode);
            }
        }

        [Fact]
        public void DbUpgrade_ShouldThrowCompilationException_WhenSequenceUnsupported()
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            TestSupport.WriteUnit(directory, "20261127_100007_seq",
                "  <sequence name=\"seq_probe\" action=\"CREATE\" start=\"1\"/>");

            var config = new DbChronicleConfig { BaseDirectory = directory };
            config.Database.Dialect = "mysql";
            config.Paths.Migrations = ".";
            config.Paths.VersionLog = "version_log";

            using (var chronicle = DbChronicleFactory.Create(new OfflineExecutor(), new DbChronicleOptions { Config = config }))
            {
                var error = Assert.Throws<CompilationException>(() => chronicle.DbUpgrade());

                Assert.Contains(error.Diagnostics, x => x.Code == "E046");
            }
        }

        [Fact]
        public void DbUpgrade_ShouldThrowEnvironmentException_WhenDialectMismatched()
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            TestSupport.WriteUnit(directory, "20261127_100008_dialect",
                "  <table name=\"t\" action=\"CREATE\">\n"
                + "    <column name=\"id\" action=\"ADD\" type=\"Int64\" primaryKey=\"true\" nullable=\"false\"/>\n"
                + "  </table>");

            var config = new DbChronicleConfig { BaseDirectory = directory };
            config.Database.Dialect = "sqlite";
            config.Paths.Migrations = ".";
            config.Paths.VersionLog = "version_log";

            using (var chronicle = DbChronicleFactory.Create(new OfflineExecutor(), new DbChronicleOptions { Config = config }))
            {
                Assert.Throws<MigrationEnvironmentException>(() => chronicle.DbUpgrade());
            }
        }

        [Fact]
        public void DbUpgrade_ShouldThrowLockException_WhenLockNotAcquired()
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            TestSupport.WriteUnit(directory, "20261127_100009_lock",
                "  <table name=\"t\" action=\"CREATE\">\n"
                + "    <column name=\"id\" action=\"ADD\" type=\"Int64\" primaryKey=\"true\" nullable=\"false\"/>\n"
                + "  </table>");

            var config = new DbChronicleConfig { BaseDirectory = directory };
            config.Database.Dialect = "mysql";
            config.Paths.Migrations = ".";
            config.Paths.VersionLog = "version_log";

            using (var chronicle = DbChronicleFactory.Create(new OfflineExecutor(), new DbChronicleOptions { Config = config }))
            {
                Assert.Throws<MigrationLockException>(() => chronicle.DbUpgrade(new DbUpgradeOptions
                {
                    ConnectionString = "Server=nowhere;",
                    LockFactory = new BusyLockFactory()
                }));
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

        private sealed class FailingExecutor : IDbExecutor
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
                throw new InvalidOperationException("boom");
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

        private sealed class BusyLockFactory : IDbLockFactory
        {
            public IDbLock Create(string connectionString)
            {
                return new BusyLock();
            }
        }

        private sealed class BusyLock : IDbLock
        {
            public bool TryAcquire(TimeSpan timeout)
            {
                return false;
            }

            public void Dispose()
            {
            }
        }
    }
}