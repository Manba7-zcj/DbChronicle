using System.Collections.Generic;
using System.IO;
using System;

using DbChronicle.Common.Enums;
using DbChronicle.Diagnostics;
using DbChronicle.Models.Operations;
using DbChronicle.Models;

namespace DbChronicle.Compilation.Checks
{
    public sealed class ChangeChecker
    {
        public IReadOnlyList<CompileDiagnostic> Check(IReadOnlyList<ChangeUnit> units)
        {
            var diagnostics = new List<CompileDiagnostic>();
            var knownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in units)
            {
                if (!knownIds.Add(unit.Id))
                {
                    diagnostics.Add(Error("E110", unit.Id, null, null, "变更单元 id 重复：" + unit.Id));
                }
            }

            var added = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var modified = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var renamedTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var model = new StructureModel();
            var builder = new StructureModelBuilder();
            var aux = new ChainAuxState();

            CheckCrossUnitOrder(units, diagnostics);
            CheckReleaseCrossDay(units, diagnostics);

            foreach (var unit in units)
            {
                var local = CollectLocalCreations(unit);
                CheckUnitReferences(unit, model, aux, local, diagnostics);
                CheckStageBeforeBoundary(unit, local, diagnostics);

                foreach (var operation in unit.Operations)
                {
                    var column = operation as ColumnOperation;
                    if (column != null && column.Action == ColumnAction.Drop
                        && !model.TryGetColumn(operation.Table, column.Name, out _)
                        && !local.Columns.Contains(ColumnKey(operation.Table, column.Name)))
                    {
                        diagnostics.Add(Error("E106", unit.Id, operation.Table, column.Name, "DROP 不存在的列：变更链中没有该列"));
                    }
                }

                foreach (var depend in unit.Depends)
                {
                    if (!knownIds.Contains(depend))
                    {
                        diagnostics.Add(Error("E107", unit.Id, null, null, "depends 指向不存在的变更单元：" + depend));
                    }
                }

                if (IsPatchFile(unit) && unit.Depends.Count == 0)
                {
                    diagnostics.Add(Error("E108", unit.Id, null, null, "补丁文件必须用 depends 声明依赖的原变更单元"));
                }

                CheckRenameOrder(unit, diagnostics);

                foreach (var operation in unit.Operations)
                {
                    var column = operation as ColumnOperation;
                    if (column == null)
                    {
                        continue;
                    }

                    var key = operation.Table + "." + column.Name;
                    if (column.Action == ColumnAction.Add)
                    {
                        string owner;
                        if (added.TryGetValue(key, out owner))
                        {
                            diagnostics.Add(Error("E101", unit.Id, operation.Table, column.Name, "重复 ADD：该列已由 " + owner + " 添加"));
                        }
                        else
                        {
                            added[key] = unit.Id;
                        }
                    }
                    else if (column.Action == ColumnAction.Modify)
                    {
                        string owner;
                        if (modified.TryGetValue(key, out owner) && !string.Equals(owner, unit.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            diagnostics.Add(Error("E102", unit.Id, operation.Table, column.Name, "MODIFY 目标冲突：该列已被 " + owner + " 修改"));
                        }
                        else
                        {
                            modified[key] = unit.Id;
                        }
                    }
                    else if (column.Action == ColumnAction.Drop)
                    {
                        string owner;
                        if (added.TryGetValue(key, out owner) && !string.Equals(owner, unit.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            diagnostics.Add(Error("E103", unit.Id, operation.Table, column.Name, "ADD 与 DROP 顺序矛盾：该列已由 " + owner + " 添加，又被本单元删除"));
                        }
                    }
                    else if (column.Action == ColumnAction.Rename && !string.IsNullOrEmpty(column.RenamedFrom))
                    {
                        string owner;
                        if (renamedTargets.TryGetValue(key, out owner) && !string.Equals(owner, unit.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            diagnostics.Add(Error("E104", unit.Id, operation.Table, column.Name, "RENAME 目标冲突：该列名已被 " + owner + " 使用"));
                        }
                        else
                        {
                            renamedTargets[key] = unit.Id;
                        }
                    }
                }

                builder.Apply(model, unit);
                ApplyAuxState(unit, aux);
            }

            return diagnostics;
        }

        private static LocalCreations CollectLocalCreations(ChangeUnit unit)
        {
            var local = new LocalCreations();
            foreach (var operation in unit.Operations)
            {
                if (operation is CreateTableOperation)
                {
                    local.Tables.Add(operation.Table ?? "");
                }

                var column = operation as ColumnOperation;
                if (column != null && column.Action == ColumnAction.Add)
                {
                    local.Columns.Add(ColumnKey(operation.Table, column.Name));
                }
            }
            return local;
        }

        private static void CheckUnitReferences(
            ChangeUnit unit,
            StructureModel model,
            ChainAuxState aux,
            LocalCreations local,
            List<CompileDiagnostic> diagnostics)
        {
            foreach (var operation in unit.Operations)
            {
                if (operation is CreateTableOperation)
                {
                    if (model.Tables.ContainsKey(operation.Table))
                    {
                        diagnostics.Add(Error(DiagnosticCodes.ChainTableMissing, unit.Id, operation.Table, null,
                            "重复建表：表 " + operation.Table + " 已存在于变更链"));
                    }
                    continue;
                }

                var dropTable = operation as DropTableOperation;
                if (dropTable != null)
                {
                    if (!model.Tables.ContainsKey(dropTable.Table) && !local.Tables.Contains(dropTable.Table))
                    {
                        diagnostics.Add(Error(DiagnosticCodes.ChainTableMissing, unit.Id, dropTable.Table, null,
                            "删除不存在的表：" + dropTable.Table));
                    }
                    continue;
                }

                var renameTable = operation as RenameTableOperation;
                if (renameTable != null)
                {
                    if (!model.Tables.ContainsKey(renameTable.Table) && !local.Tables.Contains(renameTable.Table))
                    {
                        diagnostics.Add(Error(DiagnosticCodes.ChainRenameSourceMissing, unit.Id, renameTable.Table, null,
                            "重命名的源表不存在：" + renameTable.Table));
                    }
                    else if (model.Tables.ContainsKey(renameTable.NewName))
                    {
                        diagnostics.Add(Error(DiagnosticCodes.ChainTableMissing, unit.Id, renameTable.NewName, null,
                            "重命名目标表已存在：" + renameTable.NewName));
                    }
                    continue;
                }

                var sequence = operation as SequenceOperation;
                if (sequence != null)
                {
                    if (sequence.Action == ObjectAction.Add)
                    {
                        if (aux.Sequences.Contains(sequence.Name))
                        {
                            diagnostics.Add(Error(DiagnosticCodes.ChainObjectMissing, unit.Id, null, null,
                                "重复创建序列：" + sequence.Name));
                        }
                    }
                    else if (!aux.Sequences.Contains(sequence.Name))
                    {
                        diagnostics.Add(Error(DiagnosticCodes.ChainObjectMissing, unit.Id, null, null,
                            "操作目标序列不存在：" + sequence.Name));
                    }
                    continue;
                }

                var view = operation as ViewOperation;
                if (view != null)
                {
                    if (view.Action != ObjectAction.Add && view.Action != ObjectAction.Replace
                        && !aux.Views.Contains(view.Name))
                    {
                        diagnostics.Add(Error(DiagnosticCodes.ChainObjectMissing, unit.Id, null, null,
                            "操作目标视图不存在：" + view.Name));
                    }
                    continue;
                }

                if (operation is RawSqlOperation || operation is DataMigrationOperation)
                {
                    continue;
                }

                var table = operation.Table ?? "";
                if (string.IsNullOrEmpty(table))
                {
                    continue;
                }

                if (!model.Tables.ContainsKey(table) && !local.Tables.Contains(table))
                {
                    diagnostics.Add(Error(DiagnosticCodes.ChainTableMissing, unit.Id, table, null,
                        "操作目标表在变更链中不存在：" + table));
                    continue;
                }

                var column = operation as ColumnOperation;
                if (column != null)
                {
                    CheckColumnReference(unit, column, model, local, diagnostics);
                    continue;
                }

                var primaryKey = operation as PrimaryKeyOperation;
                if (primaryKey != null)
                {
                    CheckPrimaryKeyReference(unit, primaryKey, model, local, aux, diagnostics);
                    continue;
                }

                var index = operation as IndexOperation;
                if (index != null)
                {
                    CheckIndexReference(unit, index, model, aux, local, diagnostics);
                    continue;
                }

                var foreignKey = operation as ForeignKeyOperation;
                if (foreignKey != null)
                {
                    CheckForeignKeyReference(unit, foreignKey, model, aux, local, diagnostics);
                    continue;
                }

                var unique = operation as UniqueConstraintOperation;
                if (unique != null)
                {
                    CheckConstraintColumns(unit, unique.Name, unique.Columns, table, model, local, diagnostics);
                    CheckNamedObject(unit, "唯一约束", unique.Name, aux.UniqueConstraints, unique.Action, diagnostics);
                    continue;
                }

                var check = operation as CheckConstraintOperation;
                if (check != null)
                {
                    TableModel tableModel;
                    var exists = model.Tables.TryGetValue(table, out tableModel)
                        && tableModel.CheckConstraints.Exists(x => string.Equals(x.Name, check.Name, StringComparison.OrdinalIgnoreCase));
                    if (check.Action == ObjectAction.Add && exists)
                    {
                        diagnostics.Add(Error(DiagnosticCodes.ChainObjectMissing, unit.Id, table, check.Name, "检查约束已存在：" + check.Name));
                    }
                    else if (check.Action != ObjectAction.Add && !exists)
                    {
                        diagnostics.Add(Error(DiagnosticCodes.ChainObjectMissing, unit.Id, table, check.Name, "检查约束不存在：" + check.Name));
                    }
                    continue;
                }

                var defaultConstraint = operation as DefaultConstraintOperation;
                if (defaultConstraint != null)
                {
                    if (!ColumnExists(model, table, defaultConstraint.Column, local))
                    {
                        diagnostics.Add(Error(DiagnosticCodes.ChainColumnMissing, unit.Id, table, defaultConstraint.Column,
                            "默认值约束的目标列不存在：" + defaultConstraint.Column));
                    }
                    CheckNamedObject(unit, "默认值约束", defaultConstraint.Name, aux.Defaults, defaultConstraint.Action, diagnostics);
                    continue;
                }

                var backfill = operation as BackfillOperation;
                if (backfill != null && !ColumnExists(model, table, backfill.Column, local))
                {
                    diagnostics.Add(Error(DiagnosticCodes.ChainColumnMissing, unit.Id, table, backfill.Column,
                        "回填的目标列不存在：" + backfill.Column));
                    continue;
                }

                var seed = operation as SeedOperation;
                if (seed != null)
                {
                    foreach (var columnName in seed.Columns)
                    {
                        if (!ColumnExists(model, table, columnName, local))
                        {
                            diagnostics.Add(Error(DiagnosticCodes.ChainSeedColumnsInvalid, unit.Id, table, columnName,
                                "种子数据的列不存在：" + columnName));
                        }
                    }
                }
            }
        }

        private static void CheckColumnReference(
            ChangeUnit unit,
            ColumnOperation column,
            StructureModel model,
            LocalCreations local,
            List<CompileDiagnostic> diagnostics)
        {
            var table = column.Table ?? "";
            if (column.Action == ColumnAction.Drop)
            {
                return;
            }

            if (column.Action == ColumnAction.Add)
            {
                TableModel tableModel;
                if (model.Tables.TryGetValue(table, out tableModel) && tableModel.Columns.ContainsKey(column.Name))
                {
                    diagnostics.Add(Error(DiagnosticCodes.ChainColumnAlreadyExists, unit.Id, table, column.Name,
                        "ADD 的列已存在（可能由建表或基线定义）：" + column.Name));
                }
                return;
            }

            if (column.Action == ColumnAction.Rename)
            {
                if (!ColumnExists(model, table, column.RenamedFrom, local))
                {
                    diagnostics.Add(Error(DiagnosticCodes.ChainRenameSourceMissing, unit.Id, table, column.RenamedFrom,
                        "重命名的源列不存在：" + column.RenamedFrom));
                }
                return;
            }

            if (!ColumnExists(model, table, column.Name, local))
            {
                diagnostics.Add(Error(DiagnosticCodes.ChainColumnMissing, unit.Id, table, column.Name,
                    "操作目标列不存在：" + column.Name));
                return;
            }

            if (column.Action == ColumnAction.Reorder && !string.IsNullOrEmpty(column.After)
                && !string.Equals(column.After, "FIRST", StringComparison.OrdinalIgnoreCase)
                && !ColumnExists(model, table, column.After, local))
            {
                diagnostics.Add(Error(DiagnosticCodes.ChainColumnMissing, unit.Id, table, column.After,
                    "REORDER 的 after 列不存在：" + column.After));
            }
        }

        private static void CheckPrimaryKeyReference(
            ChangeUnit unit,
            PrimaryKeyOperation primaryKey,
            StructureModel model,
            LocalCreations local,
            ChainAuxState aux,
            List<CompileDiagnostic> diagnostics)
        {
            var table = primaryKey.Table ?? "";
            TableModel tableModel;
            var exists = model.Tables.TryGetValue(table, out tableModel) && tableModel.PrimaryKeyColumns.Count > 0;
            if (primaryKey.Action == ObjectAction.Add && exists)
            {
                diagnostics.Add(Error(DiagnosticCodes.ChainPrimaryKeyInvalid, unit.Id, table, null,
                    "ADD 主键失败：表已有主键"));
            }
            else if (primaryKey.Action != ObjectAction.Add && !exists)
            {
                diagnostics.Add(Error(DiagnosticCodes.ChainPrimaryKeyInvalid, unit.Id, table, null,
                    "主键操作失败：表当前没有主键"));
            }

            if (primaryKey.Action == ObjectAction.Add)
            {
                CheckConstraintColumns(unit, null, primaryKey.Columns, table, model, local, diagnostics);
            }
        }

        private static void CheckIndexReference(
            ChangeUnit unit,
            IndexOperation index,
            StructureModel model,
            ChainAuxState aux,
            LocalCreations local,
            List<CompileDiagnostic> diagnostics)
        {
            if (index.Action == ObjectAction.Add)
            {
                CheckConstraintColumns(unit, index.Name, index.Columns, index.Table ?? "", model, local, diagnostics);
            }

            var key = ObjectKey(index.Table, index.Name);
            if (index.Action == ObjectAction.Add)
            {
                if (aux.Indexes.Contains(key))
                {
                    diagnostics.Add(Error(DiagnosticCodes.ChainObjectMissing, unit.Id, index.Table, index.Name,
                        "索引已存在：" + index.Name));
                }
                return;
            }

            if (index.Action == ObjectAction.Rename)
            {
                if (!aux.Indexes.Contains(key))
                {
                    diagnostics.Add(Error(DiagnosticCodes.ChainObjectMissing, unit.Id, index.Table, index.Name,
                        "重命名的源索引不存在：" + index.Name));
                }
                return;
            }

            if (!aux.Indexes.Contains(key))
            {
                diagnostics.Add(Error(DiagnosticCodes.ChainObjectMissing, unit.Id, index.Table, index.Name,
                    "索引不存在：" + index.Name));
            }
        }

        private static void CheckForeignKeyReference(
            ChangeUnit unit,
            ForeignKeyOperation foreignKey,
            StructureModel model,
            ChainAuxState aux,
            LocalCreations local,
            List<CompileDiagnostic> diagnostics)
        {
            if (foreignKey.PartOfCreateTable)
            {
                return;
            }

            if (foreignKey.Action == ObjectAction.Add)
            {
                CheckConstraintColumns(unit, foreignKey.Name, foreignKey.Columns, foreignKey.Table ?? "", model, local, diagnostics);
                if (!model.Tables.ContainsKey(foreignKey.RefTable))
                {
                    diagnostics.Add(Error(DiagnosticCodes.ChainForeignKeyTargetMissing, unit.Id, foreignKey.Table, null,
                        "外键引用的表不存在：" + foreignKey.RefTable));
                }
                else
                {
                    TableModel refTable;
                    model.Tables.TryGetValue(foreignKey.RefTable, out refTable);
                    foreach (var refColumn in foreignKey.RefColumns)
                    {
                        if (!refTable.Columns.ContainsKey(refColumn))
                        {
                            diagnostics.Add(Error(DiagnosticCodes.ChainForeignKeyTargetMissing, unit.Id, foreignKey.Table, refColumn,
                                "外键引用的列不存在：" + foreignKey.RefTable + "." + refColumn));
                        }
                    }
                }
                return;
            }

            var key = ObjectKey(foreignKey.Table, foreignKey.Name);
            if (!aux.ForeignKeys.Contains(key))
            {
                diagnostics.Add(Error(DiagnosticCodes.ChainObjectMissing, unit.Id, foreignKey.Table, foreignKey.Name,
                    "外键不存在：" + foreignKey.Name));
            }
        }

        private static void CheckConstraintColumns(
            ChangeUnit unit,
            string name,
            IEnumerable<string> columns,
            string table,
            StructureModel model,
            LocalCreations local,
            List<CompileDiagnostic> diagnostics)
        {
            if (columns == null)
            {
                return;
            }

            foreach (var column in columns)
            {
                if (!ColumnExists(model, table, column, local))
                {
                    diagnostics.Add(Error(DiagnosticCodes.ChainColumnMissing, unit.Id, table, column,
                        "约束引用的列不存在：" + column + (string.IsNullOrEmpty(name) ? "" : "（" + name + "）")));
                }
            }
        }

        private static void CheckNamedObject(
            ChangeUnit unit,
            string kind,
            string name,
            HashSet<string> known,
            ObjectAction action,
            List<CompileDiagnostic> diagnostics)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            if (action == ObjectAction.Add && known.Contains(name))
            {
                diagnostics.Add(Error(DiagnosticCodes.ChainObjectMissing, unit.Id, null, name, kind + "已存在：" + name));
            }
            else if (action != ObjectAction.Add && !known.Contains(name))
            {
                diagnostics.Add(Error(DiagnosticCodes.ChainObjectMissing, unit.Id, null, name, kind + "不存在：" + name));
            }
        }

        private static void CheckStageBeforeBoundary(
            ChangeUnit unit,
            LocalCreations local,
            List<CompileDiagnostic> diagnostics)
        {
            foreach (var operation in unit.Operations)
            {
                if (operation.Stage != OperationStage.Before)
                {
                    continue;
                }

                if (local.Tables.Contains(operation.Table ?? ""))
                {
                    diagnostics.Add(Error(DiagnosticCodes.ChainStageBeforeReference, unit.Id, operation.Table, null,
                        "stage=\"before\" 不能引用本单元新增的表，请改用 stage=\"after\""));
                }

                var backfill = operation as BackfillOperation;
                if (backfill != null && local.Columns.Contains(ColumnKey(operation.Table, backfill.Column)))
                {
                    diagnostics.Add(Error(DiagnosticCodes.ChainStageBeforeReference, unit.Id, operation.Table, backfill.Column,
                        "stage=\"before\" 不能引用本单元新增的列，请改用 stage=\"after\""));
                }

                var seed = operation as SeedOperation;
                if (seed != null)
                {
                    foreach (var column in seed.Columns)
                    {
                        if (local.Columns.Contains(ColumnKey(operation.Table, column)))
                        {
                            diagnostics.Add(Error(DiagnosticCodes.ChainStageBeforeReference, unit.Id, operation.Table, column,
                                "stage=\"before\" 不能引用本单元新增的列，请改用 stage=\"after\""));
                        }
                    }
                }
            }
        }

        private static void ApplyAuxState(ChangeUnit unit, ChainAuxState aux)
        {
            foreach (var operation in unit.Operations)
            {
                var index = operation as IndexOperation;
                if (index != null)
                {
                    var key = ObjectKey(index.Table, index.Name);
                    if (index.Action == ObjectAction.Add || index.Action == ObjectAction.Replace)
                    {
                        aux.Indexes.Add(key);
                    }
                    else if (index.Action == ObjectAction.Drop)
                    {
                        aux.Indexes.Remove(key);
                    }
                    else if (index.Action == ObjectAction.Rename && !string.IsNullOrEmpty(index.NewName))
                    {
                        aux.Indexes.Remove(key);
                        aux.Indexes.Add(ObjectKey(index.Table, index.NewName));
                    }
                    continue;
                }

                var unique = operation as UniqueConstraintOperation;
                if (unique != null)
                {
                    ApplyNamed(unique.Name, unique.Action, aux.UniqueConstraints);
                    continue;
                }

                var foreignKey = operation as ForeignKeyOperation;
                if (foreignKey != null && !foreignKey.PartOfCreateTable)
                {
                    ApplyNamed(ObjectKey(foreignKey.Table, foreignKey.Name), foreignKey.Action, aux.ForeignKeys);
                    continue;
                }

                var defaultConstraint = operation as DefaultConstraintOperation;
                if (defaultConstraint != null)
                {
                    ApplyNamed(defaultConstraint.Name, defaultConstraint.Action, aux.Defaults);
                    continue;
                }

                var sequence = operation as SequenceOperation;
                if (sequence != null)
                {
                    ApplyNamed(sequence.Name, sequence.Action, aux.Sequences);
                    continue;
                }

                var view = operation as ViewOperation;
                if (view != null)
                {
                    ApplyNamed(view.Name, view.Action, aux.Views);
                }
            }
        }

        private static void ApplyNamed(string name, ObjectAction action, HashSet<string> known)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            if (action == ObjectAction.Add || action == ObjectAction.Replace)
            {
                known.Add(name);
            }
            else if (action == ObjectAction.Drop)
            {
                known.Remove(name);
            }
        }

        private static bool ColumnExists(
            StructureModel model,
            string table,
            string column,
            LocalCreations local)
        {
            if (string.IsNullOrEmpty(column))
            {
                return true;
            }

            if (local.Columns.Contains(ColumnKey(table, column)))
            {
                return true;
            }

            TableModel tableModel;
            return model.Tables.TryGetValue(table, out tableModel) && tableModel.Columns.ContainsKey(column);
        }

        private static string ColumnKey(string table, string column)
        {
            return (table ?? "") + "." + (column ?? "");
        }

        private static string ObjectKey(string table, string name)
        {
            return (table ?? "") + "." + (name ?? "");
        }

        private static void CheckCrossUnitOrder(IReadOnlyList<ChangeUnit> units, List<CompileDiagnostic> diagnostics)
        {
            var createIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < units.Count; i++)
            {
                foreach (var operation in units[i].Operations)
                {
                    var create = operation as CreateTableOperation;
                    if (create != null && !createIndex.ContainsKey(create.Table))
                    {
                        createIndex[create.Table] = i;
                    }
                }
            }

            var dropped = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < units.Count; i++)
            {
                var unit = units[i];

                foreach (var operation in unit.Operations)
                {
                    var create = operation as CreateTableOperation;
                    if (create != null)
                    {
                        dropped.Remove(create.Table);
                    }
                }

                foreach (var operation in unit.Operations)
                {
                    var foreignKey = operation as ForeignKeyOperation;
                    if (foreignKey != null
                        && foreignKey.Action != ObjectAction.Drop
                        && !string.IsNullOrEmpty(foreignKey.RefTable))
                    {
                        int createdIn;
                        if (createIndex.TryGetValue(foreignKey.RefTable, out createdIn) && createdIn > i)
                        {
                            diagnostics.Add(Error("E109", unit.Id, operation.Table, null,
                                "外键引用的表 " + foreignKey.RefTable + " 由更晚的变更单元创建，顺序颠倒"));
                        }

                        int droppedIn;
                        if (dropped.TryGetValue(foreignKey.RefTable, out droppedIn))
                        {
                            diagnostics.Add(Error("E109", unit.Id, operation.Table, null,
                                "外键引用的表 " + foreignKey.RefTable + " 已被更早的变更单元删除"));
                        }
                    }

                    var reference = operation.Table ?? "";
                    if (reference.Length > 0 && !(operation is CreateTableOperation))
                    {
                        int droppedIn;
                        if (dropped.TryGetValue(reference, out droppedIn))
                        {
                            diagnostics.Add(Error("E109", unit.Id, reference, null,
                                "该表已由更早的变更单元删除，后续单元不能再引用（如需重建请先 CREATE）"));
                        }
                    }
                }

                foreach (var operation in unit.Operations)
                {
                    var dropTable = operation as DropTableOperation;
                    if (dropTable != null)
                    {
                        dropped[dropTable.Table] = i;
                    }
                }
            }
        }

        private static void CheckReleaseCrossDay(IReadOnlyList<ChangeUnit> units, List<CompileDiagnostic> diagnostics)
        {
            var releaseDates = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var releaseOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var unit in units)
            {
                if (unit.IsBaseline || string.IsNullOrEmpty(unit.Release) || string.IsNullOrEmpty(unit.Id) || unit.Id.Length < 8)
                {
                    continue;
                }

                var date = unit.Id.Substring(0, 8);
                HashSet<string> dates;
                if (!releaseDates.TryGetValue(unit.Release, out dates))
                {
                    dates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    releaseDates[unit.Release] = dates;
                    releaseOwner[unit.Release] = unit.Id;
                }

                dates.Add(date);
            }

            foreach (var pair in releaseDates)
            {
                if (pair.Value.Count > 1)
                {
                    diagnostics.Add(new CompileDiagnostic
                    {
                        Code = "W013",
                        Severity = DiagnosticSeverity.Warning,
                        ChangeId = releaseOwner[pair.Key],
                        Message = "同一 release(" + pair.Key + ") 的变更单元跨天提交：" + string.Join(", ", pair.Value)
                    });
                }
            }
        }

        private static void CheckRenameOrder(ChangeUnit unit, List<CompileDiagnostic> diagnostics)
        {
            var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var operation in unit.Operations)
            {
                var column = operation as ColumnOperation;
                if (column == null)
                {
                    continue;
                }

                var key = operation.Table + "." + column.Name;
                string renamedTo;
                if (column.Action == ColumnAction.Rename && !string.IsNullOrEmpty(column.RenamedFrom))
                {
                    renamed[operation.Table + "." + column.RenamedFrom] = column.Name;
                }
                else if (renamed.TryGetValue(key, out renamedTo))
                {
                    diagnostics.Add(Error("E105", unit.Id, operation.Table, column.Name, "该列已重命名为 " + renamedTo + "，后续操作请使用新名字"));
                }
            }
        }

        private static bool IsPatchFile(ChangeUnit unit)
        {
            if (string.IsNullOrEmpty(unit.SourcePath))
            {
                return false;
            }

            var fileName = Path.GetFileName(unit.SourcePath) ?? "";
            return fileName.Contains("_fix_") || fileName.Contains("_patch_") || fileName.StartsWith("fix_") || fileName.StartsWith("patch_");
        }

        private static CompileDiagnostic Error(string code, string changeId, string table, string column, string message)
        {
            return new CompileDiagnostic
            {
                Code = code,
                Severity = DiagnosticSeverity.Error,
                ChangeId = changeId,
                Table = table,
                Column = column,
                Message = message
            };
        }

        private sealed class ChainAuxState
        {
            public readonly HashSet<string> Indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> UniqueConstraints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> ForeignKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Defaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Sequences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Views = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class LocalCreations
        {
            public readonly HashSet<string> Tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
