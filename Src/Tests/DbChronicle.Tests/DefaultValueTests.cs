using System.Linq;

using DbChronicle.Diagnostics;
using Xunit;

namespace DbChronicle.Tests
{
    public class DefaultValueTests
    {
        private static string Column(string attributes)
        {
            return "  <table name=\"t\" action=\"ALTER\">\n"
                + "    <column name=\"c\" action=\"ADD\" " + attributes + "/>\n"
                + "  </table>";
        }

        [Fact]
        public void ShouldRejectTextDefault_OnIntegerColumn()
        {
            var unit = TestSupport.ParseSingle(Column("type=\"Int32\" nullable=\"true\" defaultValue=\"in\""));

            Assert.Contains(unit.Diagnostics, x => x.Code == "E060");
            Assert.Contains(unit.Diagnostics, x => x.Message.Contains("必须是数字"));
        }

        [Fact]
        public void ShouldRejectQuotedDefault_OnIntegerColumn()
        {
            var unit = TestSupport.ParseSingle(Column("type=\"Int32\" nullable=\"true\" defaultValue=\"'5'\""));

            Assert.Contains(unit.Diagnostics, x => x.Code == "E060");
            Assert.Contains(unit.Diagnostics, x => x.Message.Contains("字符串字面量"));
        }

        [Fact]
        public void ShouldRejectDecimalDefault_OnIntegerColumn()
        {
            var unit = TestSupport.ParseSingle(Column("type=\"Int64\" nullable=\"true\" defaultValue=\"1.5\""));

            Assert.Contains(unit.Diagnostics, x => x.Code == "E060");
            Assert.Contains(unit.Diagnostics, x => x.Message.Contains("必须是整数"));
        }

        [Fact]
        public void ShouldAcceptIntegerDefault_OnIntegerColumn()
        {
            var unit = TestSupport.ParseSingle(Column("type=\"Int32\" nullable=\"true\" defaultValue=\"-1\""));

            Assert.DoesNotContain(unit.Diagnostics, x => x.Code == "E060");
        }

        [Fact]
        public void ShouldAcceptDecimalDefault_OnDecimalColumn()
        {
            var unit = TestSupport.ParseSingle(
                Column("type=\"Decimal\" precision=\"12\" scale=\"2\" nullable=\"true\" defaultValue=\"0.50\""));

            Assert.DoesNotContain(unit.Diagnostics, x => x.Code == "E060");
        }

        [Fact]
        public void ShouldRejectWordDefault_OnDecimalColumn()
        {
            var unit = TestSupport.ParseSingle(
                Column("type=\"Decimal\" precision=\"12\" scale=\"2\" nullable=\"true\" defaultValue=\"abc\""));

            Assert.Contains(unit.Diagnostics, x => x.Code == "E060");
        }

        [Fact]
        public void ShouldRejectNonBooleanDefault_OnBooleanColumn()
        {
            var unit = TestSupport.ParseSingle(Column("type=\"Boolean\" nullable=\"true\" defaultValue=\"yes\""));

            Assert.Contains(unit.Diagnostics, x => x.Code == "E060");
            Assert.Contains(unit.Diagnostics, x => x.Message.Contains("true / false"));
        }

        [Fact]
        public void ShouldRejectBitDefault_OnBooleanColumn()
        {
            var unit = TestSupport.ParseSingle(Column("type=\"Boolean\" nullable=\"true\" defaultValue=\"0\""));

            Assert.Contains(unit.Diagnostics, x => x.Code == "E060");
        }

        [Fact]
        public void ShouldAcceptBooleanDefault_OnBooleanColumn()
        {
            var unit = TestSupport.ParseSingle(Column("type=\"Boolean\" nullable=\"false\" defaultValue=\"false\""));

            Assert.DoesNotContain(unit.Diagnostics, x => x.Code == "E060");
        }

        [Fact]
        public void ShouldAcceptBareWordDefault_OnStringColumn()
        {
            var unit = TestSupport.ParseSingle(
                Column("type=\"String\" length=\"20\" nullable=\"true\" defaultValue=\"new\""));

            Assert.DoesNotContain(unit.Diagnostics, x => x.Code == "E060");
        }

        [Fact]
        public void ShouldAcceptFunctionDefault_OnDateTimeColumn()
        {
            var unit = TestSupport.ParseSingle(
                Column("type=\"DateTime\" nullable=\"false\" defaultValue=\"CURRENT_TIMESTAMP\""));

            Assert.DoesNotContain(unit.Diagnostics, x => x.Code == "E060");
        }

        [Fact]
        public void ShouldAcceptFunctionDefault_OnBigintColumn()
        {
            var unit = TestSupport.ParseSingle(
                Column("type=\"Int64\" nullable=\"false\" defaultValue=\"(unix_timestamp())\""));

            Assert.DoesNotContain(unit.Diagnostics, x => x.Code == "E060");
        }

        [Fact]
        public void ShouldSkipDefaultValidation_WhenDefaultRemovedOrNull()
        {
            var removed = TestSupport.ParseSingle(Column("type=\"Int32\" nullable=\"true\" defaultValue=\"none\""));
            var nullLiteral = TestSupport.ParseSingle(Column("type=\"Boolean\" nullable=\"true\" defaultValue=\"null\""));

            Assert.DoesNotContain(removed.Diagnostics, x => x.Code == "E060");
            Assert.DoesNotContain(nullLiteral.Diagnostics, x => x.Code == "E060");
        }

        [Fact]
        public void ShouldSkipDefaultValidation_WhenModifyWithoutType()
        {
            var unit = TestSupport.ParseSingle(
                "  <table name=\"t\" action=\"ALTER\">\n"
                + "    <column name=\"c\" action=\"MODIFY\" defaultValue=\"in\"/>\n"
                + "  </table>");

            Assert.DoesNotContain(unit.Diagnostics, x => x.Code == "E060");
        }

        [Fact]
        public void ShouldValidateDefault_OnCreateTableColumn()
        {
            var unit = TestSupport.ParseSingle(
                "  <table name=\"t\" action=\"CREATE\">\n"
                + "    <column name=\"c\" type=\"Int32\" nullable=\"true\" defaultValue=\"abc\"/>\n"
                + "  </table>");

            Assert.Contains(unit.Diagnostics, x => x.Code == "E060");
        }
    }
}
