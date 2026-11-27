using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using DbChronicle.Compilation.Checks;
using DbChronicle.Configuration;
using DbChronicle.Migrations.State;
using DbChronicle.Models;
using DbChronicle.Parsing;
using Xunit;

namespace DbChronicle.Tests
{
    public class ReleaseChainTests
    {
        [Fact]
        public void Loader_ShouldSortByReleaseThenId_RegardlessOfWriteOrder()
        {
            var config = CreateConfig(out var directory);
            Write(directory, "20261126_100000_z9", "20261126", Column("c1"));
            Write(directory, "20261124_100000_a1", "20261124", Column("c2"));
            Write(directory, "20261125_100000_m5", "20261125", Column("c3"));

            var releases = Load(config).Select(x => x.Release).ToList();

            Assert.Equal(new[] { "20261124", "20261125", "20261126" }, releases);
        }

        [Fact]
        public void Order_ShouldReportRegression_WhenLowerReleasePending()
        {
            var config = CreateConfig(out var directory);
            Write(directory, "20261124_100000_low1", "20261124", Column("c1"));
            Write(directory, "20261126_100000_high1", "20261126", Column("c2"));

            var applied = new List<AppliedChange>
            {
                new AppliedChange { ChangeId = "20261126_100000_high1", Release = "20261126" }
            };

            var diagnostics = ReleaseChecker.CheckOrder(Load(config), config, applied);

            Assert.Contains(diagnostics, x => x.Code == "E127");
        }

        [Fact]
        public void Order_ShouldReportRegression_WhenLowerIdInSameRelease()
        {
            var config = CreateConfig(out var directory);
            Write(directory, "20261126_100000_early1", "20261126", Column("c1"));
            Write(directory, "20261126_120000_late1", "20261126", Column("c2"));

            var applied = new List<AppliedChange>
            {
                new AppliedChange { ChangeId = "20261126_120000_late1", Release = "20261126" }
            };

            var diagnostics = ReleaseChecker.CheckOrder(Load(config), config, applied);

            Assert.Contains(diagnostics, x => x.Code == "E127");
        }

        [Fact]
        public void Order_ShouldDowngrade_WhenLateSubmissionAccepted()
        {
            var config = CreateConfig(out var directory);
            config.Options.AllowLateSubmission = true;
            Write(directory, "20261124_100000_low2", "20261124", Column("c1"));
            Write(directory, "20261126_100000_high2", "20261126", Column("c2"));

            var applied = new List<AppliedChange>
            {
                new AppliedChange { ChangeId = "20261126_100000_high2", Release = "20261126" }
            };

            var diagnostics = ReleaseChecker.CheckOrder(Load(config), config, applied);

            Assert.DoesNotContain(diagnostics, x => x.Code == "E127");
            Assert.Contains(diagnostics, x => x.Code == "W046");
        }

        [Fact]
        public void Release_ShouldReportLateSubmission()
        {
            var config = CreateConfig(out var directory);
            Write(directory, "20261126_100000_late01", "20261123", Column("c1"));

            var diagnostics = ChainValidator.Validate(Load(config), config);

            Assert.Contains(diagnostics, x => x.Code == "E121");
        }

        [Fact]
        public void Release_ShouldAllowPatchFile()
        {
            var config = CreateConfig(out var directory);
            Write(directory, "20261126_100000_patch01_fix_1", "20261123", Column("c1"));

            var diagnostics = ChainValidator.Validate(Load(config), config);

            Assert.DoesNotContain(diagnostics, x => x.Code == "E121");
            Assert.Contains(diagnostics, x => x.Code == "W046");
        }

        [Fact]
        public void Release_ShouldAllowDeclaredLateSubmission()
        {
            var config = CreateConfig(out var directory);
            Write(directory, "20261126_100000_late02", "20261123", Column("c1"), true);

            var diagnostics = ChainValidator.Validate(Load(config), config);

            Assert.DoesNotContain(diagnostics, x => x.Code == "E121");
            Assert.Contains(diagnostics, x => x.Code == "W046");
        }

        [Fact]
        public void Release_ShouldAllowLateSubmission_WhenConfigured()
        {
            var config = CreateConfig(out var directory);
            config.Options.AllowLateSubmission = true;
            Write(directory, "20261126_100000_late03", "20261123", Column("c1"));

            var diagnostics = ChainValidator.Validate(Load(config), config);

            Assert.DoesNotContain(diagnostics, x => x.Code == "E121");
            Assert.Contains(diagnostics, x => x.Code == "W046");
        }

        [Fact]
        public void Release_ShouldReportManifestUnitMissing()
        {
            var config = CreateConfig(out var directory);
            var first = Write(directory, "20261123_100000_unit01", "20261123", Column("c1"));
            Write(directory, "20261123_110000_unit02", "20261123", Column("c2"));
            Freeze(config, "20261123");

            File.Delete(first);

            var diagnostics = ChainValidator.Validate(Load(config), config);

            Assert.Contains(diagnostics, x => x.Code == "E122" && x.Message.Contains("文件缺失"));
        }

        [Fact]
        public void Release_ShouldReportManifestChecksumChanged()
        {
            var config = CreateConfig(out var directory);
            var first = Write(directory, "20261123_100000_unit03", "20261123", Column("c1"));
            Freeze(config, "20261123");

            File.AppendAllText(first, " ", new UTF8Encoding(false));

            var diagnostics = ChainValidator.Validate(Load(config), config);

            Assert.Contains(diagnostics, x => x.Code == "E122" && x.Message.Contains("checksum"));
        }

        [Fact]
        public void Release_ShouldReportUnitOutsideManifest()
        {
            var config = CreateConfig(out var directory);
            Write(directory, "20261123_100000_unit04", "20261123", Column("c1"));
            Freeze(config, "20261123");

            Write(directory, "20261123_120000_unit05", "20261123", Column("c2"));

            var diagnostics = ChainValidator.Validate(Load(config), config);

            Assert.Contains(diagnostics, x => x.Code == "E122" && x.Message.Contains("封版后新增"));
        }

        [Fact]
        public void Release_ShouldReportHole_WhenPartiallyApplied()
        {
            var config = CreateConfig(out var directory);
            Write(directory, "20261123_100000_unit06", "20261123", Column("c1"));
            Write(directory, "20261123_110000_unit07", "20261123", Column("c2"));
            Freeze(config, "20261123");

            var applied = new List<AppliedChange>
            {
                new AppliedChange { ChangeId = "20261123_100000_unit06", Release = "20261123" }
            };

            var diagnostics = ReleaseChecker.CheckHoles(config, applied);

            Assert.Contains(diagnostics, x => x.Code == "E123");
        }

        [Fact]
        public void Release_ShouldNotReportHole_WhenNothingApplied()
        {
            var config = CreateConfig(out var directory);
            Write(directory, "20261123_100000_unit08", "20261123", Column("c1"));
            Freeze(config, "20261123");

            var diagnostics = ReleaseChecker.CheckHoles(config, new List<AppliedChange>());

            Assert.DoesNotContain(diagnostics, x => x.Code == "E123");
        }

        [Fact]
        public void Release_ShouldRefuseOverwriteManifest()
        {
            var config = CreateConfig(out var directory);
            Write(directory, "20261123_100000_unit09", "20261123", Column("c1"));
            var store = new ReleaseManifestStore(config);
            var manifest = store.Freeze("20261123", Load(config), "tester");
            store.Save(manifest);

            Assert.True(store.Exists("20261123"));
            Assert.Throws<System.InvalidOperationException>(() => store.Save(manifest));
        }

        [Fact]
        public void Capability_ShouldWarnMissingLevel()
        {
            var config = CreateConfig(out var directory);
            config.Database.CapabilityLevel = "mysql80";

            var diagnostics = CapabilityChecker.Check(new List<AppliedChange>
            {
                new AppliedChange { ChangeId = "20261123_100000_unit10", Release = "20261123" }
            }, config);

            Assert.Contains(diagnostics, x => x.Code == "W045");
        }

        [Fact]
        public void Capability_ShouldReportDrift_AccordingToPolicy()
        {
            var config = CreateConfig(out var directory);
            config.Database.CapabilityLevel = "mysql80";
            var applied = new List<AppliedChange>
            {
                new AppliedChange
                {
                    ChangeId = "20261123_100000_unit11",
                    Release = "20261123",
                    CapabilityLevel = "mysql57"
                }
            };

            Assert.Contains(CapabilityChecker.Check(applied, config), x => x.Code == "W043");

            config.Options.VersionDriftPolicy = "strict";
            Assert.Contains(CapabilityChecker.Check(applied, config), x => x.Code == "E120");
        }

        private static DbChronicleConfig CreateConfig(out string directory)
        {
            directory = TestSupport.CreateMigrationsDirectory();
            var config = TestSupport.CreateConfig(true);
            config.Paths.Migrations = directory;
            config.Paths.Releases = Path.Combine(directory, "releases");
            return config;
        }

        private static IReadOnlyList<ChangeUnit> Load(DbChronicleConfig config)
        {
            return new ChangeUnitLoader(config).LoadAll();
        }

        private static void Freeze(DbChronicleConfig config, string release)
        {
            var store = new ReleaseManifestStore(config);
            store.Save(store.Freeze(release, Load(config), "tester"));
        }

        private static string Write(string directory, string id, string release, string body, bool lateSubmission = false)
        {
            var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
                + "<changeUnit id=\"" + id + "\" author=\"tester\" release=\"" + release + "\""
                + (lateSubmission ? " lateSubmission=\"true\"" : "") + ">\n"
                + "  <description>test</description>\n"
                + body
                + "\n</changeUnit>\n";
            var path = Path.Combine(directory, id + ".xml");
            File.WriteAllText(path, xml, new UTF8Encoding(false));
            return path;
        }

        private static string Column(string name)
        {
            return "  <table name=\"t\" action=\"ALTER\">\n"
                + "    <column name=\"" + name + "\" action=\"ADD\" type=\"Int32\" nullable=\"true\"/>\n"
                + "  </table>";
        }
    }
}
