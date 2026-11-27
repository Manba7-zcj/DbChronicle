using System.Collections.Generic;
using System.Text.RegularExpressions;

using DbChronicle.Common;
using DbChronicle.Common.Enums;
using DbChronicle.Diagnostics;
using DbChronicle.Models;
using DbChronicle.Models.Operations;

namespace DbChronicle.Compilation.Checks
{
    internal static class ConsequenceChecker
    {
        private static readonly Regex DestructiveSql = new Regex(
            @"\bTRUNCATE\b|\bDROP\s+(TABLE|COLUMN|INDEX|VIEW|CONSTRAINT|DATABASE|SCHEMA)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Dictionary<LogicalType, int> IntegerDigits = new Dictionary<LogicalType, int>
        {
            { LogicalType.Int8, 3 },
            { LogicalType.Int16, 5 },
            { LogicalType.Int32, 10 },
            { LogicalType.Int64, 19 }
        };

        private static readonly Dictionary<string, int> CharsetWidth =
            new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
            {
                { "ascii", 0 },
                { "latin1", 1 },
                { "gbk", 2 },
                { "big5", 2 },
                { "utf8", 3 },
                { "utf8mb3", 3 },
                { "utf8mb4", 4 }
            };

        public static void Check(ChangeUnit unit, StructureModel model, List<CompileDiagnostic> diagnostics)
        {
            foreach (var operation in unit.Operations)
            {
                if (string.Equals((operation.Table ?? "").Trim(), "*", System.StringComparison.Ordinal))
                {
                    continue;
                }

                TableModel table;
                model.Tables.TryGetValue(operation.Table ?? "", out table);
                CheckOperation(unit, operation, table, diagnostics);
            }
        }

        private static void CheckOperation(ChangeUnit unit, SchemaOperation operation, TableModel table,
            List<CompileDiagnostic> diagnostics)
        {
            var column = operation as ColumnOperation;
            if (column != null)
            {
                CheckColumn(unit, column, table, diagnostics);
                return;
            }

            if (operation is DropTableOperation)
            {
                Warn(diagnostics, DiagnosticCodes.RenameBreaksReferences, unit.Id, operation.Table, null,
                    "删除表：视图 / 触发器 / 存储过程与代码中的旧引用会静默失效，执行前请确认并备份");
                return;
            }

            var renameTable = operation as RenameTableOperation;
            if (renameTable != null)
            {
                Warn(diagnostics, DiagnosticCodes.RenameBreaksReferences, unit.Id, operation.Table, null,
                    "重命名表为 " + renameTable.NewName + "：旧名的外部引用会静默失效，请同步修改");
                return;
            }

            var primaryKey = operation as PrimaryKeyOperation;
            if (primaryKey != null)
            {
                if (primaryKey.Action == ObjectAction.Drop || primaryKey.Action == ObjectAction.Replace)
                {
                    Error(diagnostics, DiagnosticCodes.PrimaryKeyOrAutoIncrementChanged, unit.Id, operation.Table, null,
                        "删除或替换主键：行唯一性约束与未来主键分配都会改变，需人工确认并备份");
                }

                if (primaryKey.Action == ObjectAction.Add || primaryKey.Action == ObjectAction.Replace)
                {
                    Warn(diagnostics, DiagnosticCodes.ConstraintTightened, unit.Id, operation.Table, null,
                        "新增或替换主键要求现有数据唯一且非空，需数据验证（探针见 v1.1）");
                }

                return;
            }

            var unique = operation as UniqueConstraintOperation;
            if (unique != null && unique.Action == ObjectAction.Add)
            {
                Warn(diagnostics, DiagnosticCodes.ConstraintTightened, unit.Id, operation.Table, unique.Name,
                    "新增唯一约束要求现有数据无重复，需数据验证");
            }

            var foreignKey = operation as ForeignKeyOperation;
            if (foreignKey != null && foreignKey.Action == ObjectAction.Add && !foreignKey.PartOfCreateTable)
            {
                Warn(diagnostics, DiagnosticCodes.ConstraintTightened, unit.Id, operation.Table, foreignKey.Name,
                    "新增外键要求现有数据都能匹配目标表，需数据验证");
            }

            var check = operation as CheckConstraintOperation;
            if (check != null && check.Action == ObjectAction.Add)
            {
                Warn(diagnostics, DiagnosticCodes.ConstraintTightened, unit.Id, operation.Table, check.Name,
                    "新增检查约束要求现有数据满足表达式，需数据验证");
            }

            var tableOption = operation as TableOptionOperation;
            if (tableOption != null
                && (!string.IsNullOrEmpty(tableOption.Charset) || !string.IsNullOrEmpty(tableOption.Collate)))
            {
                Warn(diagnostics, DiagnosticCodes.CharsetOrCollationChanged, unit.Id, operation.Table, null,
                    "修改表级字符集 / 排序规则会改变比较、排序与唯一性判定结果");
            }

            var rawSql = operation as RawSqlOperation;
            if (rawSql != null)
            {
                foreach (var sql in rawSql.SqlByDialect.Values)
                {
                    if (!string.IsNullOrEmpty(sql) && DestructiveSql.IsMatch(sql))
                    {
                        Warn(diagnostics, DiagnosticCodes.DestructiveRawSql, unit.Id, operation.Table, null,
                            "rawSql 含 DROP / TRUNCATE 等破坏性语句，无法按结构化规则检查，请人工确认");
                        break;
                    }
                }

                return;
            }

            var update = operation as UpdateOperation;
            if (update != null)
            {
                CheckWhere(unit, operation, update.Where, diagnostics);
                return;
            }

            var clean = operation as CleanOperation;
            if (clean != null)
            {
                CheckWhere(unit, operation, clean.Where, diagnostics);
                return;
            }

            var backfill = operation as BackfillOperation;
            if (backfill != null)
            {
                CheckWhere(unit, operation, backfill.Where, diagnostics);
            }
        }

        private static void CheckColumn(ChangeUnit unit, ColumnOperation operation, TableModel table,
            List<CompileDiagnostic> diagnostics)
        {
            var name = operation.Name;
            var definition = operation.Definition;

            if ((operation.Action == ColumnAction.Add || operation.Action == ColumnAction.Modify)
                && definition.Type == LogicalType.Decimal
                && (!definition.Precision.HasValue || !definition.Scale.HasValue))
            {
                Warn(diagnostics, DiagnosticCodes.DecimalPrecisionImplicit, unit.Id, operation.Table, name,
                    "Decimal 未显式声明 precision / scale，将按默认 (18,2) 生成，可能与历史定义不一致");
            }

            if (operation.Action == ColumnAction.Rename)
            {
                Warn(diagnostics, DiagnosticCodes.RenameBreaksReferences, unit.Id, operation.Table, name,
                    "重命名列为 " + name + "：旧列名的外部引用会静默失效，请同步修改");
                return;
            }

            if (operation.Action == ColumnAction.Add)
            {
                if (definition.Nullable == false && !HasDefault(definition))
                {
                    Warn(diagnostics, DiagnosticCodes.ConstraintTightened, unit.Id, operation.Table, name,
                        "新增 NOT NULL 列且未声明默认值，历史行会违反约束，需数据验证或回填");
                }

                if (definition.Unique == true)
                {
                    Warn(diagnostics, DiagnosticCodes.ConstraintTightened, unit.Id, operation.Table, name,
                        "新增唯一列要求现有数据无重复，需数据验证");
                }

                return;
            }

            if (operation.Action != ColumnAction.Modify || table == null)
            {
                return;
            }

            ColumnDefinition before;
            if (!table.Columns.TryGetValue(name, out before) || before == null)
            {
                return;
            }

            var after = ColumnMerger.Merge(before, definition);
            var narrowed = IsStorageNarrowed(before, after);
            var precisionLost = LosesPrecision(before, after);
            var charsetNarrowed = IsCharsetNarrowed(before, after);

            if (narrowed)
            {
                Error(diagnostics, DiagnosticCodes.NarrowingType, unit.Id, operation.Table, name,
                    "类型收窄：" + Describe(before) + " → " + Describe(after) + "，超出新范围的现有值会溢出或被截断，需备份后执行");
            }

            if (precisionLost)
            {
                Error(diagnostics, DiagnosticCodes.PrecisionLoss, unit.Id, operation.Table, name,
                    "精度损失：" + Describe(before) + " → " + Describe(after) + "，现有值可能超出新精度（需人工确认并备份）");
            }

            if (charsetNarrowed)
            {
                Error(diagnostics, DiagnosticCodes.CharsetNarrowed, unit.Id, operation.Table, name,
                    "字符集收窄：" + before.Charset + " → " + after.Charset + "，不可表示的字符会被替换或写入失败");
            }

            if (!narrowed && !precisionLost && !charsetNarrowed
                && !string.IsNullOrEmpty(Family(before.Type)) && Family(before.Type) != Family(after.Type))
            {
                Warn(diagnostics, DiagnosticCodes.TypeFamilyChanged, unit.Id, operation.Table, name,
                    "类型族改变：" + Describe(before) + " → " + Describe(after) + "，排序 / 比较 / 计算语义会随之改变");
            }

            if (CharsetOrCollateChanged(before, after))
            {
                Warn(diagnostics, DiagnosticCodes.CharsetOrCollationChanged, unit.Id, operation.Table, name,
                    "字符集 / 排序规则变更：比较、排序与唯一性判定结果会改变");
            }

            if (BecameNotNull(before, after))
            {
                Warn(diagnostics, DiagnosticCodes.ConstraintTightened, unit.Id, operation.Table, name,
                    "收紧为 NOT NULL：现有 NULL 行会导致执行失败，需先回填并验证");
            }

            if (BecameUnique(before, after))
            {
                Warn(diagnostics, DiagnosticCodes.ConstraintTightened, unit.Id, operation.Table, name,
                    "新增唯一约束：现有重复值会导致执行失败，需先验证");
            }

            if (BecameTextToBounded(before, after))
            {
                Warn(diagnostics, DiagnosticCodes.TextToBoundedString, unit.Id, operation.Table, name,
                    "Text → " + Describe(after) + "：需确认现有值最大长度不超过 " + after.Length);
            }

            if (before.AutoIncrement.GetValueOrDefault() != after.AutoIncrement.GetValueOrDefault()
                || before.PrimaryKey.GetValueOrDefault() != after.PrimaryKey.GetValueOrDefault())
            {
                Error(diagnostics, DiagnosticCodes.PrimaryKeyOrAutoIncrementChanged, unit.Id, operation.Table, name,
                    "主键 / 自增属性变更：未来主键分配会改变，可能与历史数据冲突，需人工确认");
            }
        }

        private static void CheckWhere(ChangeUnit unit, SchemaOperation operation, string where,
            List<CompileDiagnostic> diagnostics)
        {
            if (!string.IsNullOrWhiteSpace(where))
            {
                return;
            }

            Error(diagnostics, DiagnosticCodes.DataChangeWithoutWhere, unit.Id, operation.Table, null,
                "数据改写缺少 where 属性：会覆盖整表历史数据；确需全表请显式写 where=\"1=1\"");
        }

        private static bool HasDefault(ColumnDefinition definition)
        {
            return definition.DefaultValue != null && definition.DefaultValue.IsDeclared && !definition.DefaultValue.IsNone;
        }

        private static bool IsInteger(LogicalType? type)
        {
            return type.HasValue && IntegerDigits.ContainsKey(type.Value);
        }

        private static bool IsFloat(LogicalType? type)
        {
            return type == LogicalType.Float32 || type == LogicalType.Float64;
        }

        private static bool IsStorageNarrowed(ColumnDefinition before, ColumnDefinition after)
        {
            var b = before.Type;
            var a = after.Type;
            if (!b.HasValue || !a.HasValue)
            {
                return false;
            }

            if (IsInteger(b) && IsInteger(a))
            {
                if (IntegerDigits[a.Value] < IntegerDigits[b.Value])
                {
                    return true;
                }

                return before.Unsigned.GetValueOrDefault() != after.Unsigned.GetValueOrDefault();
            }

            if ((b == LogicalType.String || b == LogicalType.FixedString) && b == a
                && before.Length.HasValue && after.Length.HasValue && after.Length.Value < before.Length.Value)
            {
                return true;
            }

            if (b == LogicalType.Binary && a == LogicalType.Binary
                && before.Length.HasValue && after.Length.HasValue && after.Length.Value < before.Length.Value)
            {
                return true;
            }

            if (b == LogicalType.DateTime && (a == LogicalType.Date || a == LogicalType.Time))
            {
                return true;
            }

            return b == LogicalType.DateTimeOffset && a == LogicalType.DateTime;
        }

        private static bool LosesPrecision(ColumnDefinition before, ColumnDefinition after)
        {
            var b = before.Type;
            var a = after.Type;
            if (!b.HasValue || !a.HasValue)
            {
                return false;
            }

            if (b == LogicalType.Decimal && a == LogicalType.Decimal)
            {
                var beforeInteger = before.Precision.GetValueOrDefault(18) - before.Scale.GetValueOrDefault(2);
                var afterInteger = after.Precision.GetValueOrDefault(18) - after.Scale.GetValueOrDefault(2);
                return afterInteger < beforeInteger || after.Scale.GetValueOrDefault(2) < before.Scale.GetValueOrDefault(2);
            }

            if (b == LogicalType.Decimal && (a == LogicalType.Float32 || a == LogicalType.Float64))
            {
                return true;
            }

            if ((b == LogicalType.Float32 || b == LogicalType.Float64) && a == LogicalType.Decimal)
            {
                return true;
            }

            if (b == LogicalType.Decimal && IsInteger(a))
            {
                return true;
            }

            if (IsInteger(b) && a == LogicalType.Decimal)
            {
                var integerDigits = after.Precision.GetValueOrDefault(18) - after.Scale.GetValueOrDefault(2);
                return integerDigits < IntegerDigits[b.Value];
            }

            if (b == LogicalType.Float64 && a == LogicalType.Float32)
            {
                return true;
            }

            if (IsInteger(b) && (a == LogicalType.Float32 || a == LogicalType.Float64))
            {
                return IntegerDigits[b.Value] > (a == LogicalType.Float32 ? 7 : 15);
            }

            return (b == LogicalType.Float32 || b == LogicalType.Float64) && IsInteger(a);
        }

        private static bool IsCharsetNarrowed(ColumnDefinition before, ColumnDefinition after)
        {
            int from;
            int to;
            if (string.IsNullOrEmpty(before.Charset) || string.IsNullOrEmpty(after.Charset)
                || !CharsetWidth.TryGetValue(before.Charset, out from)
                || !CharsetWidth.TryGetValue(after.Charset, out to))
            {
                return false;
            }

            return to < from;
        }

        private static bool CharsetOrCollateChanged(ColumnDefinition before, ColumnDefinition after)
        {
            return Changed(before.Charset, after.Charset) || Changed(before.Collate, after.Collate);
        }

        private static bool Changed(string before, string after)
        {
            return !string.IsNullOrEmpty(after) && !string.Equals(before, after, System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool BecameNotNull(ColumnDefinition before, ColumnDefinition after)
        {
            return before.Nullable != false && after.Nullable == false;
        }

        private static bool BecameUnique(ColumnDefinition before, ColumnDefinition after)
        {
            return before.Unique != true && after.Unique == true;
        }

        private static bool BecameTextToBounded(ColumnDefinition before, ColumnDefinition after)
        {
            return before.Type == LogicalType.Text && after.Type == LogicalType.String && after.Length.HasValue;
        }

        private static string Family(LogicalType? type)
        {
            if (!type.HasValue)
            {
                return "";
            }

            switch (type.Value)
            {
                case LogicalType.Boolean:
                    return "bool";
                case LogicalType.Int8:
                case LogicalType.Int16:
                case LogicalType.Int32:
                case LogicalType.Int64:
                    return "integer";
                case LogicalType.Float32:
                case LogicalType.Float64:
                    return "float";
                case LogicalType.Decimal:
                    return "decimal";
                case LogicalType.String:
                case LogicalType.FixedString:
                case LogicalType.Text:
                    return "string";
                case LogicalType.Json:
                    return "json";
                case LogicalType.Xml:
                    return "xml";
                case LogicalType.DateTime:
                case LogicalType.DateTimeOffset:
                case LogicalType.Date:
                case LogicalType.Time:
                    return "temporal";
                case LogicalType.Binary:
                case LogicalType.Blob:
                    return "binary";
                case LogicalType.Uuid:
                    return "uuid";
                default:
                    return "";
            }
        }

        private static string Describe(ColumnDefinition definition)
        {
            if (!definition.Type.HasValue)
            {
                return "（未声明类型）";
            }

            var text = definition.Type.Value.ToString();
            if (definition.Type == LogicalType.Decimal)
            {
                text += "(" + definition.Precision.GetValueOrDefault(18) + "," + definition.Scale.GetValueOrDefault(2) + ")";
            }
            else if (definition.Length.HasValue
                && (definition.Type == LogicalType.String || definition.Type == LogicalType.FixedString
                    || definition.Type == LogicalType.Binary))
            {
                text += "(" + definition.Length.Value + ")";
            }

            if (definition.Unsigned.GetValueOrDefault())
            {
                text += " unsigned";
            }

            return text;
        }

        private static void Error(List<CompileDiagnostic> diagnostics, string code, string changeId, string table,
            string column, string message)
        {
            diagnostics.Add(new CompileDiagnostic
            {
                Code = code,
                Severity = DiagnosticSeverity.Error,
                ChangeId = changeId,
                Table = table,
                Column = column,
                Message = message
            });
        }

        private static void Warn(List<CompileDiagnostic> diagnostics, string code, string changeId, string table,
            string column, string message)
        {
            diagnostics.Add(new CompileDiagnostic
            {
                Code = code,
                Severity = DiagnosticSeverity.Warning,
                ChangeId = changeId,
                Table = table,
                Column = column,
                Message = message
            });
        }
    }
}
