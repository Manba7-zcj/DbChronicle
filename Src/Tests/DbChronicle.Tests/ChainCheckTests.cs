using System.Collections.Generic;
using System.Linq;

using DbChronicle.Compilation.Checks;
using DbChronicle.Models;
using DbChronicle.Parsing;
using Xunit;

namespace DbChronicle.Tests
{
    public class ChainCheckTests
    {
        private const string Baseline =
            "  <table name=\"t\" action=\"CREATE\">\n"
            + "    <column name=\"id\" action=\"ADD\" type=\"Int64\" primaryKey=\"true\" autoIncrement=\"true\" nullable=\"false\"/>\n"
            + "    <column name=\"name\" action=\"ADD\" type=\"String\" length=\"50\" nullable=\"true\"/>\n"
            + "    <column name=\"code\" action=\"ADD\" type=\"String\" length=\"20\" charset=\"utf8mb4\" nullable=\"true\"/>\n"
            + "    <column name=\"amount\" action=\"ADD\" type=\"Decimal\" precision=\"18\" scale=\"2\" nullable=\"true\"/>\n"
            + "    <column name=\"note\" action=\"ADD\" type=\"Text\" nullable=\"true\"/>\n"
            + "  </table>";

        [Fact]
        public void Check_ShouldReportMissingTable()
        {
            var diagnostics = Check(true, Alter("ghost", Column("c", "ADD", " type=\"Int32\" nullable=\"true\"")));

            Assert.Contains(diagnostics, x => x.Code == "E111");
        }

        [Fact]
        public void Check_ShouldSkipWildcardTable()
        {
            var diagnostics = Check(true, Alter("*", Column("c", "ADD", " type=\"Int32\" nullable=\"true\"")));

            Assert.DoesNotContain(diagnostics, x => x.Severity == DbChronicle.Common.Enums.DiagnosticSeverity.Error);
        }

        [Fact]
        public void Check_ShouldSkipMissingTable_WhenStrictReferenceCheckDisabled()
        {
            var diagnostics = Check(false, Alter("ghost", Column("c", "ADD", " type=\"Int32\" nullable=\"true\"")));

            Assert.DoesNotContain(diagnostics, x => x.Code == "E111");
        }

        [Fact]
        public void Check_ShouldNotLoseDiagnostics_AfterImplicitTable()
        {
            var diagnostics = Check(true,
                Alter("ghost", Column("c1", "ADD", " type=\"Int32\" nullable=\"true\"")),
                Alter("ghost", Column("c2", "ADD", " type=\"Int32\" nullable=\"true\"")));

            Assert.Equal(2, diagnostics.Count(x => x.Code == "E111"));
        }

        [Fact]
        public void Check_ShouldReportMissingColumn_OnModify()
        {
            var diagnostics = Check(true,
                Alter("t", Column("nope", "MODIFY", " type=\"String\" length=\"10\" nullable=\"true\"")));

            Assert.Contains(diagnostics, x => x.Code == "E112");
        }

        [Fact]
        public void Check_ShouldReportDuplicateAdd_AgainstBaseline()
        {
            var diagnostics = Check(true, Alter("t", Column("name", "ADD", " type=\"String\" length=\"50\" nullable=\"true\"")));

            Assert.Contains(diagnostics, x => x.Code == "E113");
        }

        [Fact]
        public void Check_ShouldReportDuplicateAdd_InsideCreateUnit()
        {
            var body = "  <table name=\"t2\" action=\"CREATE\">\n"
                + "    <column name=\"c\" action=\"ADD\" type=\"Int32\" nullable=\"true\"/>\n"
                + "  </table>\n"
                + "  <table name=\"t2\" action=\"ALTER\">\n"
                + "    <column name=\"c\" action=\"ADD\" type=\"Int32\" nullable=\"true\"/>\n"
                + "  </table>";

            var diagnostics = Check(true, body);

            Assert.Contains(diagnostics, x => x.Code == "E113");
        }

        [Fact]
        public void Check_ShouldReportPrimaryKeyAdd_WhenTableAlreadyHasPrimaryKey()
        {
            var diagnostics = Check(true,
                Alter("t", "    <primaryKey action=\"ADD\" columns=\"id\"/>\n"));

            Assert.Contains(diagnostics, x => x.Code == "E114");
        }

        [Fact]
        public void Check_ShouldReportMissingIndex_OnDrop()
        {
            var diagnostics = Check(true, Alter("t", "    <index name=\"ix_missing\" action=\"DROP\"/>\n"));

            Assert.Contains(diagnostics, x => x.Code == "E115");
        }

        [Fact]
        public void Check_ShouldReportForeignKeyTargetMissing()
        {
            var diagnostics = Check(true,
                Alter("t", "    <foreignKey name=\"fk\" action=\"ADD\" columns=\"id\" refTable=\"missing\" refColumns=\"id\"/>\n"));

            Assert.Contains(diagnostics, x => x.Code == "E116");
        }

        [Fact]
        public void Check_ShouldReportRenameSourceMissing()
        {
            var diagnostics = Check(true,
                Alter("t", Column("name2", "RENAME", " renamedFrom=\"nope\"")));

            Assert.Contains(diagnostics, x => x.Code == "E117");
        }

        [Fact]
        public void Check_ShouldReportSeedRowMismatch()
        {
            var diagnostics = Check(true,
                Alter("t", "    <seed columns=\"id,name\">\n      <row values=\"1\"/>\n    </seed>\n"));

            Assert.Contains(diagnostics, x => x.Code == "E118");
        }

        [Fact]
        public void Check_ShouldReportStageBeforeReference()
        {
            var body = "  <table name=\"t2\" action=\"CREATE\">\n"
                + "    <column name=\"c\" action=\"ADD\" type=\"Int32\" nullable=\"true\"/>\n"
                + "    <update stage=\"before\" set=\"c=1\" where=\"1=1\"/>\n"
                + "  </table>";

            var diagnostics = Check(true, body);

            Assert.Contains(diagnostics, x => x.Code == "E119");
        }

        [Fact]
        public void Check_ShouldReportStringLengthShrink()
        {
            var diagnostics = Check(true,
                Alter("t", Column("name", "MODIFY", " type=\"String\" length=\"20\" nullable=\"true\"")));

            Assert.Contains(diagnostics, x => x.Code == "E054");
        }

        [Fact]
        public void Check_ShouldReportDecimalIntegerDigitShrink()
        {
            var diagnostics = Check(true,
                Alter("t", Column("amount", "MODIFY", " type=\"Decimal\" precision=\"20\" scale=\"8\" nullable=\"true\"")));

            Assert.Contains(diagnostics, x => x.Code == "E057");
        }

        [Fact]
        public void Check_ShouldReportCharsetNarrowing()
        {
            var diagnostics = Check(true,
                Alter("t", Column("code", "MODIFY", " type=\"String\" length=\"20\" charset=\"latin1\" nullable=\"true\"")));

            Assert.Contains(diagnostics, x => x.Code == "E058");
        }

        [Fact]
        public void Check_ShouldReportAutoIncrementChange()
        {
            var diagnostics = Check(true,
                Alter("t", Column("id", "MODIFY", " type=\"Int64\" primaryKey=\"true\" autoIncrement=\"false\" nullable=\"false\"")));

            Assert.Contains(diagnostics, x => x.Code == "E055");
        }

        [Fact]
        public void Check_ShouldReportDataChangeWithoutWhere()
        {
            var diagnostics = Check(true, Alter("t", "    <update set=\"name='x'\"/>\n"));

            Assert.Contains(diagnostics, x => x.Code == "E056");
        }

        [Fact]
        public void Check_ShouldReportDataTargetColumnMissing()
        {
            var diagnostics = Check(true, Alter("t", "    <update set=\"nope=1\" where=\"1=1\"/>\n"));

            Assert.Contains(diagnostics, x => x.Code == "E112");
        }

        [Fact]
        public void Check_ShouldWarnNotNullColumnWithoutDefault()
        {
            var diagnostics = Check(true, Alter("t", Column("flag", "ADD", " type=\"Int32\" nullable=\"false\"")));

            Assert.Contains(diagnostics, x => x.Code == "W034");
        }

        [Fact]
        public void Check_ShouldWarnTextToBoundedString()
        {
            var diagnostics = Check(true,
                Alter("t", Column("note", "MODIFY", " type=\"String\" length=\"100\" nullable=\"true\"")));

            Assert.Contains(diagnostics, x => x.Code == "W035");
        }

        [Fact]
        public void Check_ShouldWarnRename()
        {
            var diagnostics = Check(true,
                Alter("t", Column("name2", "RENAME", " renamedFrom=\"name\"")));

            Assert.Contains(diagnostics, x => x.Code == "W036");
        }

        [Fact]
        public void Check_ShouldWarnTypeFamilyChange()
        {
            var diagnostics = Check(true,
                Alter("t", Column("name", "MODIFY", " type=\"Int32\" nullable=\"true\"")));

            Assert.Contains(diagnostics, x => x.Code == "W037");
        }

        [Fact]
        public void Check_ShouldWarnCollationChange()
        {
            var diagnostics = Check(true,
                Alter("t", Column("name", "MODIFY", " type=\"String\" length=\"50\" collate=\"utf8mb4_bin\" nullable=\"true\"")));

            Assert.Contains(diagnostics, x => x.Code == "W038");
        }

        [Fact]
        public void Check_ShouldWarnDestructiveRawSql()
        {
            var diagnostics = Check(true, "  <rawSql name=\"t\">TRUNCATE TABLE t;</rawSql>");

            Assert.Contains(diagnostics, x => x.Code == "W039");
        }

        [Fact]
        public void Check_ShouldWarnImplicitDecimalPrecision()
        {
            var diagnostics = Check(true,
                Alter("t", Column("amount", "MODIFY", " type=\"Decimal\" nullable=\"true\"")));

            Assert.Contains(diagnostics, x => x.Code == "W040");
        }

        [Fact]
        public void Check_ShouldNotReportSafeWidening()
        {
            var diagnostics = Check(true,
                Alter("t", Column("name", "MODIFY", " type=\"String\" length=\"100\" nullable=\"true\"")));

            Assert.DoesNotContain(diagnostics, x => x.Severity == DbChronicle.Common.Enums.DiagnosticSeverity.Error);
            Assert.DoesNotContain(diagnostics, x => x.Code == "E054");
        }

        private static IReadOnlyList<CompileDiagnostic> Check(bool strictReferenceCheck, params string[] bodies)
        {
            var directory = TestSupport.CreateMigrationsDirectory();
            TestSupport.WriteUnit(directory, "00000000_000000_baseline", Baseline);
            for (var i = 0; i < bodies.Length; i++)
            {
                TestSupport.WriteUnit(directory, "20261127_1000" + i.ToString("00") + "_x" + i, bodies[i]);
            }

            var config = TestSupport.CreateConfig(true);
            config.Paths.Migrations = directory;
            var units = new ChangeUnitLoader(config).LoadAll();
            return new ChangeChecker(strictReferenceCheck).Check(units);
        }

        private static string Alter(string table, string children)
        {
            return "  <table name=\"" + table + "\" action=\"ALTER\">\n" + children + "  </table>";
        }

        private static string Column(string name, string action, string attributes)
        {
            return "    <column name=\"" + name + "\" action=\"" + action + "\"" + attributes + "/>\n";
        }
    }
}
